using System;
using System.Threading;

namespace VoiceIme;

/// <summary>Pipeline lifecycle owned by <see cref="DictationCoordinator"/>.</summary>
public enum DictationState
{
    Idle,
    Recording,
    Uploading,
    Error,
}

/// <summary>What the host should do after a hotkey edge or a <see cref="DictationCoordinator.Tick"/>.</summary>
public enum HotkeyCommand
{
    None,
    StartRecording,
    StopRecording,
}

/// <summary>Outcome of reporting captured audio to the coordinator.</summary>
public enum AudioCaptureOutcome
{
    /// <summary>Empty capture, or a late stop from a session that already ended — stay idle.</summary>
    Discarded,

    /// <summary>Real audio for the current session — the host may upload it.</summary>
    Uploading,
}

/// <summary>
/// Pure dictation state machine (IDLE → RECORDING → UPLOADING → ERROR),
/// porting Handy's transcription-coordinator essentials:
/// <list type="bullet">
/// <item>30 ms press debounce (key repeat / double-fire);</item>
/// <item>hold-vs-toggle classification per activationMode — hold starts on
/// key-down and stops on key-up, toggle flips on each press, and a 50 ms
/// release-grace lets a fast tap in hold mode lock on instead of being
/// lost (a repeat press inside the grace cancels the deferred release);</item>
/// <item>optimistic start + rollback: recording begins immediately, a start
/// failure reverts to idle surfacing the typed error;</item>
/// <item>generation counters: every session bumps <see cref="Generation"/>,
/// so a late stop/transcribe callback from a previous session is ignored;</item>
/// <item>one <see cref="CancelCurrentOperation"/> entry point that cancels
/// the session token, drops deferred input, and drains to Idle.</item>
/// </list>
/// No UI handles: time comes from an injectable clock (tests drive it
/// synthetically; production passes the default wall clock), and side
/// effects leave as return values plus the <see cref="ErrorRaised"/> /
/// <see cref="OperationCancelled"/> events. The host (App) executes capture,
/// upload, overlay, tray, and paste — this class only decides.
/// Divergences from Handy, disclosed: a press that arrives while uploading
/// is ignored (no busy-pipeline remember/forget — v1 has a single pipeline),
/// and there is one global binding (no per-binding bookkeeping).
/// </summary>
public sealed class DictationCoordinator : IDisposable
{
    /// <summary>Press edges closer than this are key repeat — dropped.</summary>
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// A key-up in a hold mode waits this long before resolving, so a
    /// synthesized auto-repeat press can cancel it (Handy #1539 pattern).
    /// </summary>
    public static readonly TimeSpan ReleaseGrace = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Host poll cadence for <see cref="Tick"/> and the hotkey release poll:
    /// App runs both on this interval so cancel/debounce resolve promptly
    /// without a busy loop. Stage gates between stop → transcribe → paste
    /// check <see cref="SessionToken"/> on the same granularity.
    /// </summary>
    public const int CancelPollIntervalMs = 25;

    /// <summary>Handy default: holds at least this long stop; shorter ones lock on.</summary>
    public static readonly TimeSpan DefaultHoldThreshold = TimeSpan.FromMilliseconds(300);

    private readonly Func<DateTime> _clock;
    private DateTime? _lastPressUtc;
    private DateTime? _pressStartUtc;
    private bool _locked;
    private PendingRelease? _pendingRelease;
    private CancellationTokenSource? _sessionCts;
    private bool _disposed;
    private string _activationMode = ActivationModes.Toggle;

    public DictationCoordinator(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>Session counter: bumped on every recording start.</summary>
    public long Generation { get; private set; }

    /// <summary>
    /// Token for the current session, cancelled by
    /// <see cref="CancelCurrentOperation"/>. The host threads it through
    /// stop → transcribe so a cancel aborts the in-flight upload, and checks
    /// it before paste/status so a late completion cannot deliver.
    /// </summary>
    public CancellationToken SessionToken => _sessionCts?.Token ?? CancellationToken.None;

    /// <summary>Activation mode; invalid values coerce to toggle (never throws).</summary>
    public string ActivationMode
    {
        get => _activationMode;
        set => _activationMode = ActivationModes.IsValid(value) ? value : ActivationModes.Toggle;
    }

    /// <summary>Hold-or-toggle tap/hold boundary. Push-to-talk always uses zero.</summary>
    public TimeSpan HoldThreshold { get; set; } = DefaultHoldThreshold;

    /// <summary>True while a key-up waits out <see cref="ReleaseGrace"/> — the host should keep ticking.</summary>
    public bool HasPendingRelease => _pendingRelease is not null;

    /// <summary>Raised with the typed error (plus cause for trace logging) when a stage fails.</summary>
    public event Action<IDictationError, Exception?>? ErrorRaised;

    /// <summary>Raised when <see cref="CancelCurrentOperation"/> drains to idle — the host hides UI.</summary>
    public event Action? OperationCancelled;

    /// <summary>Key-down edge: starts, stops (toggle/locked), or debounces.</summary>
    public HotkeyCommand OnHotkeyDown()
    {
        var now = _clock();

        // A repeat press inside the grace cancels the deferred release
        // (the key is still held) — before debounce, like Handy.
        if (_pendingRelease is not null)
        {
            _pendingRelease = null;
            return HotkeyCommand.None;
        }

        if (_lastPressUtc is { } last && now - last < DebounceInterval)
        {
            return HotkeyCommand.None;
        }

        _lastPressUtc = now;
        switch (State)
        {
            case DictationState.Idle:
                BeginSession(now, locked: ActivationMode == ActivationModes.Toggle);
                return HotkeyCommand.StartRecording;
            case DictationState.Recording:
                // A locked session ends on the next press. Toggle ends every
                // session even if it began unlocked under a hold mode (the
                // setting changed mid-recording) — otherwise nothing could
                // stop it.
                if (_locked || ActivationMode == ActivationModes.Toggle)
                {
                    _pendingRelease = null;
                    State = DictationState.Uploading;
                    return HotkeyCommand.StopRecording;
                }

                // The key is still held (its release will end this
                // recording), so a repeated press means nothing.
                return HotkeyCommand.None;
            default:
                // Uploading or Error: the single pipeline is busy.
                return HotkeyCommand.None;
        }
    }

    /// <summary>Key-up edge: defers the release in hold modes, ignored in toggle.</summary>
    public HotkeyCommand OnHotkeyUp()
    {
        if (ActivationMode == ActivationModes.Toggle)
        {
            return HotkeyCommand.None;
        }

        if (State != DictationState.Recording || _locked)
        {
            return HotkeyCommand.None;
        }

        var now = _clock();
        var threshold = ActivationMode == ActivationModes.HoldOrToggle
            ? HoldThreshold
            : TimeSpan.Zero;
        _pendingRelease = new PendingRelease(now + ReleaseGrace, now, threshold);
        return HotkeyCommand.None;
    }

    /// <summary>
    /// Toggle entry for inputs with no release edge (tray double-click):
    /// starts locked, or stops when recording. Never debounced — a quick
    /// tray stop after a tray start must work.
    /// </summary>
    public HotkeyCommand OnToggleInput()
    {
        switch (State)
        {
            case DictationState.Idle:
                BeginSession(_clock(), locked: true);
                return HotkeyCommand.StartRecording;
            case DictationState.Recording:
                _pendingRelease = null;
                State = DictationState.Uploading;
                return HotkeyCommand.StopRecording;
            default:
                return HotkeyCommand.None;
        }
    }

    /// <summary>5-minute auto-stop: always stops when recording.</summary>
    public HotkeyCommand OnAutoStop()
    {
        if (State != DictationState.Recording)
        {
            return HotkeyCommand.None;
        }

        _pendingRelease = null;
        State = DictationState.Uploading;
        return HotkeyCommand.StopRecording;
    }

    /// <summary>
    /// Resolves a deferred release once <see cref="ReleaseGrace"/> elapses:
    /// a hold stops, a tap locks on. The host calls this on a
    /// <see cref="CancelPollIntervalMs"/>-ms cadence while
    /// <see cref="HasPendingRelease"/> is true.
    /// </summary>
    public HotkeyCommand Tick()
    {
        var pending = _pendingRelease;
        if (pending is null)
        {
            return HotkeyCommand.None;
        }

        if (_clock() < pending.Deadline)
        {
            return HotkeyCommand.None;
        }

        _pendingRelease = null;
        if (State != DictationState.Recording || _locked)
        {
            return HotkeyCommand.None;
        }

        var held = pending.ReleasedAt - (_pressStartUtc ?? pending.ReleasedAt);
        if (held >= pending.Threshold)
        {
            State = DictationState.Uploading;
            return HotkeyCommand.StopRecording;
        }

        _locked = true;
        return HotkeyCommand.None;
    }

    /// <summary>
    /// Reconciles the optimistic start for one session: success keeps
    /// recording; failure rolls back to Error and surfaces the typed error.
    /// Results carrying a stale generation (a late start-failure from
    /// session N arriving during live session N+1) are ignored, like every
    /// other stage gate on this machine.
    /// </summary>
    public void ReportStartResult(long generation, bool started, IDictationError? error = null, Exception? cause = null)
    {
        if (generation != Generation || State != DictationState.Recording)
        {
            return;
        }

        if (started)
        {
            return;
        }

        State = DictationState.Error;
        RaiseError(error ?? new RecordingError(RecordingErrorReason.Unknown), cause);
    }

    /// <summary>
    /// Hands captured audio over: empty audio (or a late stop from an ended
    /// session) discards to Idle; real audio for the live session arms the
    /// upload under the current <see cref="Generation"/>.
    /// </summary>
    public AudioCaptureOutcome ReportAudioCaptured(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (State != DictationState.Uploading)
        {
            return AudioCaptureOutcome.Discarded;
        }

        if (wav.Length <= 44)
        {
            State = DictationState.Idle;
            return AudioCaptureOutcome.Discarded;
        }

        return AudioCaptureOutcome.Uploading;
    }

    /// <summary>
    /// Commits a finished upload: true (→ Idle) only when the generation is
    /// still live — a late completion after cancel/retry returns false and
    /// the host must NOT paste or touch status.
    /// </summary>
    public bool ReportUploadSucceeded(long generation)
    {
        if (generation != Generation || State != DictationState.Uploading)
        {
            return false;
        }

        State = DictationState.Idle;
        return true;
    }

    /// <summary>
    /// Fails the live upload with the typed error; stale generations are
    /// ignored so a late failure cannot re-surface UI after cancel.
    /// </summary>
    public bool ReportUploadFailed(long generation, IDictationError error, Exception? cause = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (generation != Generation || State != DictationState.Uploading)
        {
            return false;
        }

        State = DictationState.Error;
        RaiseError(error, cause);
        return true;
    }

    /// <summary>
    /// A stage observed cancellation for this generation: make sure the
    /// machine is idle (CancelCurrentOperation usually got there first).
    /// </summary>
    public void NotifyUploadCancelled(long generation)
    {
        if (generation == Generation
            && (State is DictationState.Recording or DictationState.Uploading))
        {
            State = DictationState.Idle;
        }
    }

    /// <summary>
    /// Single cancel entry point: aborts the session token (in-flight stop /
    /// transcribe drains through it), drops deferred input, and drains to
    /// Idle from any non-idle state. Always raises
    /// <see cref="OperationCancelled"/> — hiding UI is idempotent.
    /// </summary>
    public void CancelCurrentOperation()
    {
        try
        {
            _sessionCts?.Cancel();
        }
        catch
        {
            // Best effort: state still drains to idle below.
        }

        _pendingRelease = null;
        if (State is not DictationState.Idle)
        {
            State = DictationState.Idle;
        }

        try
        {
            OperationCancelled?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"cancel notification failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Dismisses the error display back to Idle after the host showed it.</summary>
    public void AcknowledgeError()
    {
        if (State == DictationState.Error)
        {
            State = DictationState.Idle;
        }
    }

    private void RaiseError(IDictationError error, Exception? cause)
    {
        try
        {
            ErrorRaised?.Invoke(error, cause);
        }
        catch (Exception ex)
        {
            // A presentation subscriber must not turn a settled pipeline into
            // an unobserved exception. The typed state remains Error for the
            // host to acknowledge.
            Logger.Error($"error notification failed: {ex.GetType().Name}");
        }
    }

    private void BeginSession(DateTime now, bool locked)
    {
        _pressStartUtc = now;
        _locked = locked;
        Generation++;
        // A prior session may still have a late transport continuation even
        // after the state returned to Idle. Cancel it before replacing the
        // source; generation checks still keep any continuation silent.
        try
        {
            _sessionCts?.Cancel();
        }
        catch
        {
            // The old source may already be disposing during shutdown.
        }
        finally
        {
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();
        }
        State = DictationState.Recording;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _sessionCts?.Cancel();
        }
        catch
        {
            // Disposal still proceeds if a token source is already torn down.
        }
        finally
        {
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
    }

    private sealed record PendingRelease(DateTime Deadline, DateTime ReleasedAt, TimeSpan Threshold);
}
