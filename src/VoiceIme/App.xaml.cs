using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using VoiceIme.Theme;

namespace VoiceIme;

/// <summary>
/// Tray-first application: a NotifyIcon owns the lifecycle, the settings window
/// opens on demand. Global hotkey (Ctrl+Shift+Space) toggles recording from any app.
/// Thin glue over <see cref="DictationCoordinator"/>: App executes effects
/// (capture, upload, overlay, tray, paste) while the coordinator owns
/// IDLE → RECORDING → UPLOADING → ERROR. No dictation logic lives here —
/// hotkey edges go in, commands come out, async stages report back.
/// </summary>
public partial class App : System.Windows.Application
{
    private const int HotkeyId = 0xB001;
    private const int CancelHotkeyId = 0xB002;
    internal const int TrayTextLimit = 63;
    private NotifyIcon? _tray;
    private Notifier _notifier = new((_, _, _) => { });
    private HotkeyWindow? _hotkeyWindow;
    private HotkeyWindow? _cancelHotkeyWindow;
    private readonly AudioRecorder _recorder = new();
    private readonly LlmClient _llm = new();
    private SettingsStore _settings = SettingsStore.Load();
    private readonly ClipboardStore _clips = new();
    private readonly DictationCoordinator _coordinator = new();
    private IDisposable? _recordingMute;
    private OverlayWindow? _overlay;
    private DispatcherTimer? _releaseTimer;
    private SingleInstance? _singleInstance;
    private bool _hotkeyPhysicalDown;
    private uint _hotkeyModifiers;
    private uint _hotkeyVirtualKey;

    // Task 5 Live streaming state. The pump buffers recorder chunks pre-ACK
    // (brief §6 ruling); the drain starts only after ConnectAndSetupAsync
    // succeeds. All fields are published in one synchronous block on the UI
    // thread before the first post-publish await, so the stop path always
    // sees a consistent snapshot. The NAudio callback only calls TryEnqueue
    // (thread-safe) and marshals session failure to the Dispatcher before
    // touching the coordinator or UI. Interim/final strings are display-only.
    private readonly LiveSessionGuard _liveGuard = new();
    private LivePcmPump? _livePump;
    private LiveSession? _liveSession;
    private Task<bool>? _liveSetup;
    private Task? _liveDrain;
    private bool _liveDrainStarted;
    private Action<byte[]>? _livePcmHandler;
    private long _liveGeneration;
    private int _liveKeyIndex;
    private string _liveFinals = string.Empty;
    private string _liveInterim = string.Empty;

    /// <summary>
    /// Latched Live failure (pump-full NAudio callback, background drain
    /// error). Set synchronously on any thread before marshaling, consumed
    /// exactly once on the UI thread — by <see cref="FailLiveSession"/>, the
    /// record path, or the stop path, whichever runs first. Fail-closed: a
    /// failed session never finalizes or pastes, even if stop wins the race.
    /// </summary>
    private volatile bool _liveFailed;
    private long _liveFailedGeneration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Task 9 single-instance: the second instance asks the running one
        // to show its settings window (via the HotkeyWindow broadcast) and
        // exits — never a second tray icon.
        _singleInstance = new SingleInstance();
        if (!_singleInstance.Acquire())
        {
            _singleInstance.NotifyRunningInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        _settings = SettingsStore.Load();
        ThemeManager.ApplyTheme(ThemeManager.ResolveTheme(_settings.Theme));
        _clips.SetLimit(_settings.HistoryLimit);
        _recorder.AutoStopped += HandleAutoStop;
        _recorder.LevelChanged += HandleRecorderLevelChanged;
        _coordinator.ErrorRaised += HandleError;
        _coordinator.OperationCancelled += () =>
        {
            _hotkeyPhysicalDown = false;
            DisarmCancelHotkey();
            RestoreRecordingMute();
            _overlay?.Hide();
            RefreshMenu();
            SetTray("Voice IME — ready");
        };

        // Resolves deferred hold-mode releases once the 50 ms grace elapses
        // (the coordinator returns StopRecording for a real hold). Idle cost
        // is one cheap HasPendingRelease check per tick.
        _releaseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DictationCoordinator.CancelPollIntervalMs),
        };
        _releaseTimer.Tick += (_, _) =>
        {
            PollHotkeyRelease();
            if (_coordinator.Tick() == HotkeyCommand.StopRecording)
            {
                _hotkeyPhysicalDown = false;
                _ = StopAndTranscribeAsync();
            }
        };
        _releaseTimer.Start();

        _notifier = new Notifier((title, body, severity) =>
            _tray?.ShowBalloonTip(3000, title, body, ToToolTipIcon(severity)));
        _tray = new NotifyIcon
        {
            Text = TrayText(_settings.Hotkey),
            Visible = _settings.ShowTrayIcon,
            Icon = System.Drawing.SystemIcons.Information,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => HandleToggleInput();

        _hotkeyWindow = new HotkeyWindow(HotkeyId, HandleHotkeyPress, OpenSettings);
        _cancelHotkeyWindow = new HotkeyWindow(CancelHotkeyId, CancelRecording);
        RegisterStoredHotkey();

        // Task 8 behaviors (Handy startup/tray parity): reconcile the logon
        // shortcut against Autostart, then enforce the tray guard. The guard
        // runs after shortcut reconcile — an icon-off + hidden-window start
        // must surface the window so the app is never stranded invisible.
        // The normal product launch is tray-first. Users who turn off Start
        // hidden get the settings shell on the next launch; the guard below
        // still surfaces a window when the tray icon is disabled so the app
        // can never become unreachable.
        ReconcileAutostart();
        EnforceTrayGuard();
        if (!_settings.StartHidden)
        {
            _mainWindow ??= CreateMainWindow();
            AttachLiveHotkey(_mainWindow);
            RefreshSectionViews(_mainWindow, _settings, _clips);
            ShowMainWindow();
        }
    }

    /// <summary>
    /// Handy menu order (Task 7): idle = version (disabled) | Copy Last
    /// Transcript | History… | Settings… (Ctrl+,) | Quit; busy
    /// (recording/uploading) = version | Cancel | Copy Last Transcript |
    /// History… | Settings… | Quit.
    /// Rows come from the pure <see cref="TrayMenu"/> model (unit-tested headless);
    /// this method only translates rows into WinForms items. Existing system
    /// icons stay — no binary assets in v1.
    /// </summary>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var busy = _coordinator.State is DictationState.Recording or DictationState.Uploading;
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var hasTranscript = _clips.Entries.Any(e => !string.IsNullOrWhiteSpace(e.Text));
        foreach (var item in TrayMenu.Items(busy, "v" + version, hasTranscript))
        {
            menu.Items.Add(TranslateMenuItem(item));
        }

        return menu;
    }

    /// <summary>
    /// Rebuilds the tray menu after busy/idle or history transitions so the
    /// Cancel row and Copy enablement track state (Handy behavior).
    /// </summary>
    private void RefreshMenu()
    {
        if (_tray is null) return;
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    private ToolStripItem TranslateMenuItem(TrayMenuItem item)
    {
        if (item.IsSeparator)
        {
            return new ToolStripSeparator();
        }

        ToolStripMenuItem row = item.Action switch
        {
            TrayMenuAction.Cancel => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Exclamation.ToBitmap(),
                (_, _) => CancelRecording()),
            TrayMenuAction.CopyLastTranscript => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Application.ToBitmap(),
                (_, _) => CopyLastTranscript()),
            TrayMenuAction.History => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Application.ToBitmap(),
                (_, _) => OpenHistory()),
            TrayMenuAction.Settings => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Shield.ToBitmap(),
                (_, _) => OpenSettings()),
            TrayMenuAction.Quit => new ToolStripMenuItem(
                item.Label, null, (_, _) => Quit()),
            _ => new ToolStripMenuItem(item.Label),
        };
        row.Enabled = item.Enabled;
        return row;
    }

    private void CopyLastTranscript()
    {
        var latest = _clips.Entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Text));
        if (latest is null)
        {
            return;
        }

        try
        {
            System.Windows.Forms.Clipboard.SetText(latest.Text);
        }
        catch (Exception ex)
        {
            HandleError(new PasteError(), ex);
        }
    }

    private static ToolTipIcon ToToolTipIcon(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => ToolTipIcon.Error,
        NotificationSeverity.Warning => ToolTipIcon.Warning,
        _ => ToolTipIcon.Info,
    };

    /// <summary>
    /// Global hotkey delivers the press edge through WM_HOTKEY. The release is
    /// reconciled by <see cref="PollHotkeyRelease"/> for hold-style modes;
    /// toggle mode simply flips on each press. Tray/overlay cancellation still
    /// uses the coordinator's single cancel path.
    /// </summary>
    private void HandleHotkeyPress()
    {
        if (!HotkeyChord.TryParse(_settings.Hotkey, out _hotkeyModifiers, out _hotkeyVirtualKey))
        {
            _hotkeyPhysicalDown = false;
            _hotkeyModifiers = 0;
            _hotkeyVirtualKey = 0;
        }
        else
        {
            _hotkeyPhysicalDown = true;
        }

        _coordinator.ActivationMode = _settings.ActivationMode;
        switch (_coordinator.OnHotkeyDown())
        {
            case HotkeyCommand.StartRecording:
                _ = StartRecordingAsync();
                break;
            case HotkeyCommand.StopRecording:
                _hotkeyPhysicalDown = false;
                _ = StopAndTranscribeAsync();
                break;
            default:
                if (_coordinator.State != DictationState.Recording)
                {
                    _hotkeyPhysicalDown = false;
                }
                break;
        }
    }

    /// <summary>
    /// RegisterHotKey has no key-up notification. While recording in a
    /// hold-style activation, poll the physical chord and forward its first
    /// release to the coordinator; <see cref="DictationCoordinator.Tick"/>
    /// then applies the hold threshold/release grace. This keeps toggle mode
    /// free of keyboard polling and gives tray-started sessions no phantom
    /// release edge.
    /// </summary>
    private void PollHotkeyRelease()
    {
        if (!_hotkeyPhysicalDown)
        {
            return;
        }

        if (_coordinator.State != DictationState.Recording)
        {
            _hotkeyPhysicalDown = false;
            return;
        }

        _coordinator.ActivationMode = _settings.ActivationMode;
        if (_coordinator.ActivationMode == ActivationModes.Toggle
            || NativeInput.IsHotkeyDown(_hotkeyModifiers, _hotkeyVirtualKey))
        {
            return;
        }

        _hotkeyPhysicalDown = false;
        _coordinator.OnHotkeyUp();
    }

    /// <summary>
    /// Tray double-click has no release edge either, so it uses the
    /// never-debounced toggle entry: a quick stop right after a start works.
    /// </summary>
    private void HandleToggleInput()
    {
        _hotkeyPhysicalDown = false;
        _coordinator.ActivationMode = _settings.ActivationMode;
        switch (_coordinator.OnToggleInput())
        {
            case HotkeyCommand.StartRecording:
                _ = StartRecordingAsync();
                break;
            case HotkeyCommand.StopRecording:
                _ = StopAndTranscribeAsync();
                break;
        }
    }

    private void HandleRecorderLevelChanged(float level)
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => HandleRecorderLevelChanged(level)));
            }
            catch
            {
                // The dispatcher may already be shutting down.
            }

            return;
        }

        _overlay?.SetLevel(SoundFeedback.ApplyDisplayGain(level, _settings.Volume));
    }

    private void HandleAutoStop()
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(HandleAutoStop));
            }
            catch
            {
                // The dispatcher may already be shutting down.
            }

            return;
        }

        if (_coordinator.OnAutoStop() == HotkeyCommand.StopRecording)
        {
            _ = StopAndTranscribeAsync();
        }
    }

    /// <summary>
    /// Optimistic start: UI flips to recording immediately; a start failure
    /// rolls back through the coordinator, which surfaces the typed error.
    /// </summary>
    private async Task StartRecordingAsync()
    {
        if (_coordinator.State != DictationState.Recording) return;
        var generation = _coordinator.Generation;
        var token = _coordinator.SessionToken;
        var hotkeyAt = DateTimeOffset.UtcNow;
        ArmCancelHotkey();
        RefreshMenu();
        SetTray(TrayStateText.TooltipFor(DictationState.Recording, _settings.Hotkey));
        ShowOverlay(OverlayPhase.Recording);
        if (MicrophoneDevices.ListNames().Count == 0)
        {
            _coordinator.ReportStartResult(
                generation, false, new RecordingError(RecordingErrorReason.NoDevice));
            return;
        }

        if (_settings.MuteWhileRecording)
        {
            _recordingMute?.Dispose();
            _recordingMute = AudioOutputMute.TryMute(_settings.OutputDevice);
        }

        // Live models buffer recorder chunks through the pump from the first
        // callback (brief §6: never lose the beginning of speech). The pump
        // and handler are published before capture starts; teardown on any
        // start failure below keeps no session alive.
        var live = ModelRouter.RouteModel(_settings.Model) == TransportKind.Live;
        LivePcmPump? pump = null;
        Action<byte[]>? pcmHandler = null;
        if (live)
        {
            pump = new LivePcmPump();
            pcmHandler = chunk =>
            {
                if (!pump.TryEnqueue(chunk))
                {
                    Array.Clear(chunk, 0, chunk.Length);
                    FailLiveSession(generation, new TranscriptionError("Transcription failed — try again"));
                }
            };
            _recorder.PcmChunkAvailable += pcmHandler;
        }

        try
        {
            var microphoneIndex = MicrophoneDevices.FindIndex(_settings.Microphone);
            // Gemini Live requires mono 16 kHz PCM. The recorder therefore
            // uses mono for Live; the REST/Interactions WAV path honors the
            // selected channel below.
            var channel = live ? AudioChannels.Mono : _settings.Channel;
            await Task.Run(() => _recorder.Start(microphoneIndex, channel), token);
            Logger.LogLatency("hotkey_to_capture", DateTimeOffset.UtcNow - hotkeyAt);
        }
        catch (OperationCanceledException)
        {
            // User cancelled before capture started — CancelCurrentOperation
            // already drained to idle; reconciling here would stick UI.
            TeardownLivePump(pump, pcmHandler);
            RestoreRecordingMute();
            _coordinator.NotifyUploadCancelled(generation);
        }
        catch (Exception ex)
        {
            TeardownLivePump(pump, pcmHandler);
            RestoreRecordingMute();
            _coordinator.ReportStartResult(
                generation, false, RecordingError.FromException(ex), ex);
        }

        if (live && pump is not null && pcmHandler is not null
            && generation == _coordinator.Generation
            && _coordinator.State == DictationState.Recording
            && !token.IsCancellationRequested)
        {
            await StartLiveSessionAsync(pump, pcmHandler, generation, token);
            var stopOwnsUpload = generation == _coordinator.Generation
                && _coordinator.State == DictationState.Uploading
                && !token.IsCancellationRequested;
            if ((_coordinator.State != DictationState.Recording || _liveFailed) && !stopOwnsUpload)
            {
                // Setup failure/cancellation happened after the microphone
                // opened; close it and restore any temporary output mute.
                _recorder.Cancel();
                RestoreRecordingMute();
            }
        }
        else
        {
            TeardownLivePump(pump, pcmHandler);
            if (_coordinator.State != DictationState.Recording)
            {
                RestoreRecordingMute();
            }
        }
    }

    private async Task StopAndTranscribeAsync()
    {
        _hotkeyPhysicalDown = false;
        if (_coordinator.State != DictationState.Uploading) return;
        var generation = _coordinator.Generation;
        var token = _coordinator.SessionToken;
        ArmCancelHotkey();
        RefreshMenu();
        SetTray(TrayStateText.TooltipFor(DictationState.Uploading, _settings.Hotkey));
        ShowOverlay(OverlayPhase.Uploading);

        // Live models finalize the streamed session (pump → drain → final)
        // instead of uploading a WAV. The Live branch owns its session only
        // when the recording actually started one; a model switch mid-record
        // falls through to the unchanged WAV path below.
        if (ModelRouter.RouteModel(_settings.Model) == TransportKind.Live
            && _livePump is not null
            && _liveSession is not null
            && _liveSetup is not null
            && generation == _liveGeneration)
        {
            await StopLiveAndCommitAsync(generation, token);
            return;
        }

        // If the model changed while a Live session was recording, the WAV
        // fallback below is intentional, but the old pump/socket must first be
        // stopped so it cannot continue sending audio or retain a queue.
        if (_livePump is not null || _liveSession is not null || _liveSetup is not null)
        {
            await AbandonLiveSessionAsync();
        }

        var stopAt = DateTimeOffset.UtcNow;
        byte[] wav;
        try
        {
            wav = await _recorder.StopAsync(token);
            RestoreRecordingMute();
        }
        catch (OperationCanceledException)
        {
            RestoreRecordingMute();
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (Exception ex)
        {
            RestoreRecordingMute();
            _coordinator.ReportUploadFailed(
                generation, new RecordingError(RecordingErrorReason.Unknown), ex);
            return;
        }

        try
        {
            // Stage gate: a cancel that landed after StopAsync must not upload.
            if (token.IsCancellationRequested)
            {
                _coordinator.NotifyUploadCancelled(generation);
                return;
            }

            Logger.LogLatency("capture_to_stop", DateTimeOffset.UtcNow - stopAt);

            // Empty audio (or a late stop from an ended session) discards — but a
            // cancelled-while-stopping session must not touch status text.
            if (_coordinator.ReportAudioCaptured(wav) == AudioCaptureOutcome.Discarded)
            {
                if (token.IsCancellationRequested)
                {
                    _coordinator.NotifyUploadCancelled(generation);
                    return;
                }

                DisarmCancelHotkey();
                RefreshMenu();
                _overlay?.Hide();
                SetTray("Voice IME — no audio captured");
                return;
            }

            await TranscribeAndPasteAsync(wav, generation, token);
        }
        finally
        {
            // The WAV is needed only until the transport returns. Do not leave
            // microphone data in a managed array on success, failure, or
            // cancellation, including discarded/late-stop paths.
            Array.Clear(wav, 0, wav.Length);
        }
    }

    /// <summary>
    /// Upload/transcribe/paste phase of <see cref="StopAndTranscribeAsync"/>.
    /// The session token threads into <see cref="LlmClient.TranscribeAsync"/>
    /// (Task 7 review follow-up), so cancel aborts the in-flight upload;
    /// per-stage gates plus the coordinator generation check mean a late
    /// completion can neither paste nor clobber Idle. All failures funnel
    /// through the coordinator, which raises the typed error once.
    /// </summary>
    private async Task TranscribeAndPasteAsync(
        byte[] wav, long generation, CancellationToken token)
    {
        string transcript;
        int usedIndex;
        var transcribeAt = DateTimeOffset.UtcNow;
        try
        {
            _settings.ApiKeys = SettingsStore.NormalizeApiKeys(_settings.ApiKeys);
            (transcript, usedIndex) = await _llm.TranscribeAsync(
                wav, _settings.ApiKeys, _settings.BaseUrl, _settings.Model,
                _settings.KeyCursor, _settings.ActivePromptText, _settings.SmartMode, ct: token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (TranscribeException ex)
        {
            // Stale first: a late failure after cancel must stay silent —
            // no history row, no view touch, no error re-surface.
            if (token.IsCancellationRequested
                || generation != _coordinator.Generation
                || _coordinator.State != DictationState.Uploading)
            {
                _coordinator.NotifyUploadCancelled(generation);
                return;
            }

            // Failed rows persist as retryable empty-text entries (privacy:
            // audio is never persisted). The live History view picks up the
            // error text when it exists; otherwise the row still clears with
            // the store and shows a generic message when the view opens.
            var safeError = TranscriptionError.From(ex);
            var failed = _clips.Add(string.Empty);
            if (_mainWindow?.SectionView(MainSection.History)
                is Views.HistorySettingsView history)
            {
                history.AttachError(failed.Id, safeError.UserMessage);
            }

            _coordinator.ReportUploadFailed(generation, safeError, ex);
            return;
        }
        catch (Exception ex)
        {
            // Same staleness first: a late unexpected throw must not surface.
            if (token.IsCancellationRequested
                || generation != _coordinator.Generation
                || _coordinator.State != DictationState.Uploading)
            {
                _coordinator.NotifyUploadCancelled(generation);
                return;
            }

            _coordinator.ReportUploadFailed(
                generation, new TranscriptionError("Something went wrong"), ex);
            return;
        }

        // Stage gate: a cancel that landed after transcribe must not paste.
        if (token.IsCancellationRequested)
        {
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }

        CommitTranscript(transcript, usedIndex, generation, token, transcribeAt);
    }

    /// <summary>
    /// Shared commit tail for both dictation paths (extracted from
    /// <see cref="TranscribeAndPasteAsync"/> for the Task 5 Live branch):
    /// cursor advance, safe logging, history, paste, and the exactly-once
    /// <see cref="DictationCoordinator.ReportUploadSucceeded"/> gate. Callers
    /// enforce staleness before invoking — a stale completion must never
    /// reach this helper. Synchronous: every effect inside is synchronous.
    /// </summary>
    private void CommitTranscript(
        string transcript, int usedIndex, long generation, CancellationToken token, DateTimeOffset transcribeAt)
    {
        _settings.ApiKeys = SettingsStore.NormalizeApiKeys(_settings.ApiKeys);
        _settings.KeyCursor = (_settings.ApiKeys.Count == 0)
            ? 0
            : (usedIndex + 1) % _settings.ApiKeys.Count;
        try
        {
            _settings.Save();
        }
        catch (Exception saveEx)
        {
            // A successful transcription must still paste and settle the
            // coordinator if settings storage is temporarily unavailable.
            // Log only the exception type; never expose paths or key material.
            Logger.Error($"settings save after transcript failed: {saveEx.GetType().Name}");
        }
        Logger.LogLatency("capture_to_response", DateTimeOffset.UtcNow - transcribeAt);
        Logger.LogKeyUsed(usedIndex, _settings.ApiKeys);
        Logger.LogTranscriptReceived(transcript.Length);
        _clips.Add(transcript);
        RefreshMenu();
        var pasteAt = DateTimeOffset.UtcNow;
        try
        {
            NativeInput.PasteIntoFocusedWindow(transcript, _settings.PasteMethod);
            // AutoSubmit: Enter right after the chord lands, for single-line
            // targets. Inside the same try — an Enter failure is a paste
            // failure, not a silent half-delivery.
            if (_settings.AutoSubmit)
            {
                NativeInput.PressEnter();
            }
        }
        catch (Exception pasteEx)
        {
            _coordinator.ReportUploadFailed(generation, new PasteError(), pasteEx);
            return;
        }

        Logger.LogLatency("response_to_paste", DateTimeOffset.UtcNow - pasteAt);

        // Commit last: false means a newer session (or cancel) owns the
        // machine — never touch status on a stale completion.
        if (_coordinator.ReportUploadSucceeded(generation))
        {
            DisarmCancelHotkey();
            RefreshMenu();
            _overlay?.Hide();
            SetTray("Voice IME — pasted ✓");
            SoundFeedback.PlayPostPasteTone(_settings);
        }
    }

    /// <summary>
    /// Task 5 Live session start: runs after the recorder starts (brief §6 —
    /// the pump already buffers chunks, so no speech is lost waiting for the
    /// setup ACK). Single attempt on the cursor key: streaming cannot replay
    /// a partial session onto the next key, so Rotate fails this attempt
    /// instead of rotating. Setup-false rolls back through
    /// <see cref="DictationCoordinator.ReportStartResult"/> exactly like a
    /// capture start failure. Runs on the UI thread; every continuation below
    /// stays on it (no ConfigureAwait(false)), so field publish/claim order
    /// is total without locks.
    /// </summary>
    private async Task StartLiveSessionAsync(
        LivePcmPump pump, Action<byte[]> handler, long generation, CancellationToken token)
    {
        _livePump = pump;
        _livePcmHandler = handler;
        _liveGeneration = generation;
        _liveKeyIndex = 0;
        _liveFinals = string.Empty;
        _liveInterim = string.Empty;
        _liveFailed = false;
        _liveFailedGeneration = 0;
        _liveDrainStarted = false;
        _liveSetup = null;
        _liveDrain = null;

        var keys = _settings.ApiKeys
            .Select(k => k?.Trim() ?? "")
            .Where(k => k.Length > 0)
            .ToList();
        if (keys.Count == 0)
        {
            TeardownLivePump(pump, handler);
            ClearLiveFields();
            RetireLiveGeneration(generation);
            _coordinator.ReportStartResult(
                generation, false, new TranscriptionError("No API key — open Settings"));
            return;
        }

        var keyIndex = ((_settings.KeyCursor % keys.Count) + keys.Count) % keys.Count;
        _liveKeyIndex = keyIndex;
        LiveSession? session = null;
        bool connected;
        try
        {
            session = new LiveSession(
                _llm.LiveSocketsForTests?.Create() ?? new ClientWebSocketLiveSocket(),
                _settings.Model, _settings.SmartMode, _liveGuard, _liveGuard.Next());
            session.InterimReceived += text => PublishLivePreview(generation, text, false);
            session.FinalReceived += delta => PublishLivePreview(generation, delta, true);
            _liveSession = session;
            var uri = LiveProtocol.BuildLiveUri(LiveProtocol.LiveHost(_settings.BaseUrl), keys[keyIndex]);
            var setup = session.ConnectAndSetupAsync(uri, token);
            _liveSetup = setup;
            connected = await setup;
        }
        catch (OperationCanceledException)
        {
            var stopOwnsUpload = generation == _coordinator.Generation
                && _coordinator.State == DictationState.Uploading
                && !token.IsCancellationRequested;
            TeardownLivePump(pump, handler);
            await DisposeLiveSessionQuietly(session);
            ClearLiveFields();
            if (!stopOwnsUpload)
            {
                RetireLiveGeneration(generation);
                _coordinator.NotifyUploadCancelled(generation);
            }

            return;
        }
        catch (Exception ex)
        {
            var stopOwnsUpload = generation == _coordinator.Generation
                && _coordinator.State == DictationState.Uploading
                && !token.IsCancellationRequested;
            TeardownLivePump(pump, handler);
            await DisposeLiveSessionQuietly(session);
            ClearLiveFields();
            if (!stopOwnsUpload)
            {
                RetireLiveGeneration(generation);
                _coordinator.ReportStartResult(
                    generation, false, new TranscriptionError("Transcription failed — try again"), ex);
            }

            return;
        }

        // If the stop edge won while setup was in flight, its WAV fallback
        // owns teardown and should see the original pump/session snapshot.
        if (generation == _coordinator.Generation
            && _coordinator.State == DictationState.Uploading
            && !token.IsCancellationRequested)
        {
            return;
        }

        if (session is null
            || !connected
            || _liveFailed
            || generation != _coordinator.Generation
            || _coordinator.State != DictationState.Recording
            || token.IsCancellationRequested)
        {
            TeardownLivePump(pump, handler);
            await DisposeLiveSessionQuietly(session);
            ClearLiveFields();
            if (!_liveFailed)
            {
                RetireLiveGeneration(generation);
            }

            if (!connected
                && !_liveFailed
                && generation == _coordinator.Generation
                && _coordinator.State == DictationState.Recording
                && !token.IsCancellationRequested)
            {
                _coordinator.ReportStartResult(
                    generation, false, new TranscriptionError("Timed out — try again"));
            }
            else if (!_liveFailed)
            {
                // A latched failure surfaces through FailLiveSession — stay
                // silent here so the typed error appears exactly once.
                _coordinator.NotifyUploadCancelled(generation);
            }

            return;
        }

        StartLiveDrain(pump, session, generation, token);
    }

    /// <summary>
    /// Claims the exactly-once drain starter (UI thread both callers — the
    /// record path after setup, the stop path before Complete — so a plain
    /// flag suffices). The drain runs concurrently with recording once the
    /// ACK lands; at stop it finishes the buffered tail. Never faults: the
    /// wrapper converts every failure, so the stop path's await is safe.
    /// </summary>
    private void StartLiveDrain(
        LivePcmPump pump, LiveSession session, long generation, CancellationToken token)
    {
        if (_liveDrainStarted)
            return;
        _liveDrainStarted = true;
        _liveDrain = RunLiveDrainAsync(pump, session, generation, token);
    }

    /// <summary>
    /// Background drain wrapper: pump chunks → session in order until
    /// <see cref="LivePcmPump.Complete"/>. Cancellation is silent (the cancel
    /// path owns the UI); any other failure reports a typed error through
    /// <see cref="FailLiveSession"/> — never a partial paste. No logging:
    /// chunks are raw audio.
    /// </summary>
    private async Task RunLiveDrainAsync(
        LivePcmPump pump, LiveSession session, long generation, CancellationToken token)
    {
        try
        {
            await pump.DrainAsync(session.SendPcmAsync, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // CancelCurrentOperation owns the UI from here.
        }
        catch (Exception ex)
        {
            FailLiveSession(generation, MapLiveError(ex));
        }
    }

    /// <summary>
    /// Maps a drain failure to a user-safe typed error. A rotate signal means
    /// the attempt is over (streaming cannot replay onto the next key);
    /// anything else is a network or transcription failure. Fixed strings
    /// only — socket messages never surface.
    /// </summary>
    private static TranscriptionError MapLiveError(Exception ex) =>
        ex is LiveSocketException live
        && live.Message.Contains("Rate limited", StringComparison.Ordinal)
            ? new TranscriptionError("Rate limited on all keys — retry later")
            : ex is LiveSocketException
                ? new TranscriptionError("Network error — check connection")
                : new TranscriptionError("Transcription failed — try again");

    /// <summary>
    /// Live preview fan-in: finals accumulate, interim replaces. Marshals to
    /// the Dispatcher (session events fire on the drain/reader path), which
    /// also serializes concurrent interim/final arrivals in order.
    /// </summary>
    private void PublishLivePreview(long generation, string text, bool isFinal)
    {
        if (generation != _coordinator.Generation
            || _coordinator.State is DictationState.Idle or DictationState.Error)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(() => PublishLivePreview(generation, text, isFinal));
            }
            catch
            {
                // The dispatcher may already be shutting down.
            }

            return;
        }

        // Re-check after marshaling: the session may have been cancelled or
        // committed while this preview waited in the dispatcher queue.
        if (generation != _coordinator.Generation
            || _coordinator.State is DictationState.Idle or DictationState.Error)
        {
            return;
        }

        if (isFinal)
            _liveFinals += text;
        else
            _liveInterim = text;
        _overlay?.SetPreview(OverlayState.FormatPreview(_liveFinals, _liveInterim));
    }

    /// <summary>
    /// Fails the in-flight Live session from any thread (pump-full NAudio
    /// callback, background drain failure): unsubscribes, cancels to Idle,
    /// and surfaces the typed error. Stale generations (already stopped or
    /// superseded) are ignored — exactly-once surfacing by construction.
    /// </summary>
    private void FailLiveSession(long generation, TranscriptionError error)
    {
        // A detached callback from an older pump must not poison a newer
        // session's failure latch.
        if (generation != _liveGeneration)
        {
            return;
        }

        // Publish the generation before the volatile latch so a reader that
        // observes the latch also observes which session failed.
        _liveFailedGeneration = generation;
        _liveFailed = true;
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(() => FailLiveSession(generation, error));
            }
            catch
            {
                // The dispatcher may already be shutting down.
            }

            return;
        }

        if (generation != _liveGeneration
            || _liveFailedGeneration != generation
            || !_liveFailed)
            return;
        _liveFailed = false;
        _liveFailedGeneration = 0;
        DetachLivePump();
        var pump = _livePump;
        var session = _liveSession;
        var setup = _liveSetup;
        var drain = _liveDrain;
        ClearLiveFields();
        _liveGeneration = 0;

        // Stop accepting producer data and cancel the linked Live token before
        // cleanup. The background drain is awaited by the helper below, so a
        // pump-full/error callback cannot dispose resources under an active
        // consumer or let a stale send race a later session.
        pump?.Complete();
        _coordinator.CancelCurrentOperation();
        _recorder.Cancel();
        RestoreRecordingMute();
        HandleError(error);
        _ = FinishFailedLiveCleanupAsync(pump, session, setup, drain);
    }

    private static async Task FinishFailedLiveCleanupAsync(
        LivePcmPump? pump, LiveSession? session, Task<bool>? setup, Task? drain)
    {
        // Always yield before awaiting the captured drain. A drain can report
        // its own failure from inside RunLiveDrainAsync; yielding prevents this
        // cleanup task from ever awaiting that same task before it returns.
        await Task.Yield();
        try
        {
            pump?.Complete();
            if (setup is not null)
            {
                try { await setup; } catch { /* failure cleanup is best effort */ }
            }

            if (drain is not null)
            {
                try { await drain; } catch { /* wrapper already mapped it */ }
            }
        }
        finally
        {
            pump?.Dispose();
            await DisposeLiveSessionQuietly(session);
        }
    }

    /// <summary>
    /// The exact commit predicate the Live stop path enforces before paste:
    /// live token alive, generation current, machine still Uploading. Static
    /// so the integration tests drive it without the App singleton.
    /// </summary>
    internal static bool ShouldCommitLiveTranscript(
        long generation, CancellationToken token, DictationCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return !token.IsCancellationRequested
            && generation == coordinator.Generation
            && coordinator.State == DictationState.Uploading;
    }

    /// <summary>
    /// Live stop path: detaches the pump handler, stops capture (the WAV is
    /// discarded — memory-only), resolves the setup gate, drains the buffered
    /// tail, finalizes, and commits through the shared
    /// <see cref="CommitTranscript"/> exactly once. Every failure funnels to
    /// the coordinator with the typed error; cancellation is always silent.
    /// </summary>
    private async Task StopLiveAndCommitAsync(long generation, CancellationToken token)
    {
        var pump = _livePump;
        var session = _liveSession;
        var setup = _liveSetup;
        var drain = _liveDrain;
        var keyIndex = _liveKeyIndex;
        DetachLivePump();
        ClearLiveFields();
        var transcribeAt = DateTimeOffset.UtcNow;

        // A failure latched before/during stop (pump-full, drain error) wins
        // over finalizing: a failed session never pastes, even truncated.
        if (await AbortIfLiveFailedAsync(generation, token, pump, session, drain))
            return;

        byte[] wav;
        try
        {
            wav = await _recorder.StopAsync(token);
            RestoreRecordingMute();
        }
        catch (OperationCanceledException)
        {
            RestoreRecordingMute();
            if (pump is not null) TeardownLivePump(pump, null);
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (Exception ex)
        {
            RestoreRecordingMute();
            if (pump is not null) TeardownLivePump(pump, null);
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.ReportUploadFailed(
                generation, new RecordingError(RecordingErrorReason.Unknown), ex);
            return;
        }

        // The recording is captured; its bytes never leave memory. Clear it
        // before any cancellation/staleness gate can return.
        Array.Clear(wav, 0, wav.Length);

        if (session is null || pump is null || setup is null)
        {
            TeardownLivePump(pump, null);
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.ReportUploadFailed(
                generation, new RecordingError(RecordingErrorReason.Unknown));
            return;
        }

        // A cancel that landed after StopAsync must not finalize.
        if (token.IsCancellationRequested)
        {
            TeardownLivePump(pump, null);
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }

        // The setup gate may still be in flight (stop pressed mid-handshake):
        // it is bounded by LiveTimeout inside the session, so awaiting is safe.
        bool connected;
        try
        {
            connected = await setup;
        }
        catch (OperationCanceledException)
        {
            TeardownLivePump(pump, null);
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (Exception ex)
        {
            TeardownLivePump(pump, null);
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            if (ShouldCommitLiveTranscript(generation, token, _coordinator))
            {
                _coordinator.ReportUploadFailed(
                    generation, new TranscriptionError("Transcription failed — try again"), ex);
            }
            else
            {
                _coordinator.NotifyUploadCancelled(generation);
            }

            return;
        }

        if (!connected)
        {
            TeardownLivePump(pump, null);
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            if (ShouldCommitLiveTranscript(generation, token, _coordinator))
                _coordinator.ReportUploadFailed(generation, new TranscriptionError("Timed out — try again"));
            else
                _coordinator.NotifyUploadCancelled(generation);
            return;
        }

        // Exactly-once drain (the record path normally already started it),
        // then Complete-then-finalize so the last buffered chunk (§15) is
        // delivered before the end markers.
        if (drain is null)
        {
            StartLiveDrain(pump, session, generation, token);
            drain = _liveDrain;
        }

        pump.Complete();
        if (drain is not null)
            await drain;
        // The drain owns each chunk's lifetime. Dispose the pump now so a
        // cancelled/failed tail cannot retain queued audio.
        TeardownLivePump(pump, null);

        // A failure latched during the drain (drain error racing stop) also
        // aborts before finalize — same fail-closed rule as above.
        if (await AbortIfLiveFailedAsync(generation, token, pump, session, drain))
            return;

        // A cancel that landed during the drain must not finalize.
        if (!ShouldCommitLiveTranscript(generation, token, _coordinator))
        {
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }

        LiveAttemptOutcome outcome;
        try
        {
            outcome = await session.CompleteAndReadFinalAsync(token);
        }
        catch (OperationCanceledException)
        {
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (Exception) when (token.IsCancellationRequested || generation != _coordinator.Generation)
        {
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (Exception ex)
        {
            await DisposeLiveSessionQuietly(session);
            RetireLiveGeneration(generation);
            _coordinator.ReportUploadFailed(
                generation, new TranscriptionError("Transcription failed — try again"), ex);
            return;
        }

        await DisposeLiveSessionQuietly(session);
        // A drain failure can be latched while the final response is in
        // flight. Consume it before any commit, not only before finalize.
        if (await AbortIfLiveFailedAsync(generation, token, null, null, null))
            return;

        if (!ShouldCommitLiveTranscript(generation, token, _coordinator))
        {
            RetireLiveGeneration(generation);
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }

        // Capture is stopped, the handler is detached, and the session has
        // closed. Retire this generation before touching History/clipboard so
        // a callback already in NAudio teardown cannot surface a stale error.
        RetireLiveGeneration(generation);
        switch (outcome)
        {
            case LiveAttemptOutcome.Done done:
                if (string.IsNullOrWhiteSpace(done.Text))
                {
                    PersistFailedLiveRow("Got empty transcript — try again");
                    _coordinator.ReportUploadFailed(
                        generation, new TranscriptionError("Got empty transcript — try again"));
                    return;
                }

                CommitTranscript(done.Text, keyIndex, generation, token, transcribeAt);
                break;
            case LiveAttemptOutcome.Rotate:
                if (!ShouldCommitLiveTranscript(generation, token, _coordinator))
                {
                    _coordinator.NotifyUploadCancelled(generation);
                    return;
                }

                PersistFailedLiveRow("Rate limited on all keys — retry later");
                _coordinator.ReportUploadFailed(
                    generation, new TranscriptionError("Rate limited on all keys — retry later"));
                break;
            case LiveAttemptOutcome.Fail fail:
                if (!ShouldCommitLiveTranscript(generation, token, _coordinator))
                {
                    _coordinator.NotifyUploadCancelled(generation);
                    return;
                }

                PersistFailedLiveRow(fail.Message);
                _coordinator.ReportUploadFailed(generation, new TranscriptionError(fail.Message), fail.Cause);
                break;
        }
    }

    /// <summary>
    /// Consumes a latched Live failure: releases the session and surfaces the
    /// typed error (or stays silent when already stale), then reports true so
    /// the stop path aborts instead of finalizing. Exactly-once with
    /// <see cref="FailLiveSession"/> via the shared latch.
    /// </summary>
    private async Task<bool> AbortIfLiveFailedAsync(
        long generation,
        CancellationToken token,
        LivePcmPump? pump,
        LiveSession? session,
        Task? drain)
    {
        if (!_liveFailed || _liveFailedGeneration != generation)
            return false;

        _liveFailed = false;
        _liveFailedGeneration = 0;
        _liveGeneration = 0;
        _recorder.Cancel();
        RestoreRecordingMute();

        // Complete the producer first, then await the already-started drain
        // before disposing either resource. Otherwise a pump-full/drain error
        // can leave a background consumer using a disposed pump or socket.
        pump?.Complete();
        if (drain is not null)
        {
            try
            {
                await drain;
            }
            catch
            {
                // RunLiveDrainAsync normally converts failures to the latch;
                // keep cleanup fail-safe if a future implementation changes
                // that wrapper.
            }
        }

        pump?.Dispose();
        await DisposeLiveSessionQuietly(session);
        if (ShouldCommitLiveTranscript(generation, token, _coordinator))
            _coordinator.ReportUploadFailed(generation, new TranscriptionError("Transcription failed — try again"));
        else
            _coordinator.NotifyUploadCancelled(generation);
        return true;
    }

    /// <summary>
    /// Persists a failed Live attempt as a retryable empty-text row, mirroring
    /// the REST path (audio is never persisted). Kept separate so the REST
    /// path diff stays moved-code-only.
    /// </summary>
    private void PersistFailedLiveRow(string message)
    {
        var safeMessage = TranscriptionError.Sanitize(message);
        var failed = _clips.Add(string.Empty);
        if (_mainWindow?.SectionView(MainSection.History)
            is Views.HistorySettingsView history)
        {
            history.AttachError(failed.Id, safeMessage);
        }
    }

    /// <summary>
    /// Unsubscribes the NAudio chunk handler. Pump completion/disposal is the
    /// owner's job (<see cref="TeardownLivePump"/>); this only detaches.
    /// </summary>
    private void DetachLivePump()
    {
        var handler = _livePcmHandler;
        _livePcmHandler = null;
        if (handler is not null)
            _recorder.PcmChunkAvailable -= handler;
    }

    /// <summary>Releases a pump that will never drain: complete then dispose (both idempotent).</summary>
    private void TeardownLivePump(LivePcmPump? pump, Action<byte[]>? handler)
    {
        if (handler is not null)
            _recorder.PcmChunkAvailable -= handler;
        pump?.Complete();
        pump?.Dispose();
    }

    /// <summary>
    /// Stops a Live session when the user switches models while recording.
    /// The current stop path deliberately falls back to the recorder's WAV
    /// capture in that case, so the old pump, setup task, drain, and socket
    /// must all be completed before the fallback can proceed.
    /// </summary>
    private async Task AbandonLiveSessionAsync()
    {
        var pump = _livePump;
        var session = _liveSession;
        var setup = _liveSetup;
        var drain = _liveDrain;

        DetachLivePump();
        ClearLiveFields();
        // Detached callbacks from this session must not affect the fallback
        // recording or a later Live generation.
        _liveGeneration = 0;
        _liveFailed = false;
        _liveFailedGeneration = 0;

        pump?.Complete();
        await DisposeLiveSessionQuietly(session);

        if (setup is not null)
        {
            try
            {
                await setup;
            }
            catch
            {
                // Cancellation/transport failure is expected during abandon.
            }
        }

        if (drain is not null)
        {
            try
            {
                await drain;
            }
            catch
            {
                // RunLiveDrainAsync normally observes and maps its own errors.
            }
        }

        pump?.Dispose();
    }

    /// <summary>Clears the published Live snapshot. Callers snapshot locals first.</summary>
    private void ClearLiveFields()
    {
        _livePump = null;
        _liveSession = null;
        _liveSetup = null;
        _liveDrain = null;
        _livePcmHandler = null;
    }

    /// <summary>
    /// Retires a completed Live generation so a late recorder callback cannot
    /// turn a successful/settled stop into a second error notification.
    /// Conditional retirement preserves a newer generation if one has already
    /// started.
    /// </summary>
    private void RetireLiveGeneration(long generation)
    {
        if (_liveGeneration == generation)
        {
            _liveGeneration = 0;
        }
    }

    /// <summary>Best-effort session release: tears down the session CTS and socket, never throws.</summary>
    private static async Task DisposeLiveSessionQuietly(LiveSession? session)
    {
        if (session is null)
            return;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort — the finalize path already closed the socket.
        }
    }

    /// <summary>
    /// Single handler for all typed pipeline errors (Task 7 → Task 8
    /// contract): the coordinator already reverted atomically before raising,
    /// so this only shows — one balloon via <see cref="Notifier"/> plus the
    /// inline overlay error — then acknowledges back to Idle. Never leaves
    /// the overlay stuck and never raises UI from a MessageBox — all
    /// user-visible output routes through the Notifier. Inner exceptions
    /// are discarded: <see cref="IDictationError.UserMessage"/> strings are
    /// pre-approved safe, and raw exception text (which may embed key material
    /// or paths) must never reach the surface.
    /// </summary>
    private void HandleError(IDictationError error, Exception? cause = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => HandleError(error, cause)));
            }
            catch
            {
                // The dispatcher may already be shutting down.
            }

            return;
        }

        try
        {
            DisarmCancelHotkey();
            RefreshMenu();
            if (OverlayModes.ShouldShowPill(_settings.ShowOverlay))
            {
                EnsureOverlay();
                _overlay?.ShowError(error.UserMessage);
            }
            _notifier.Notify(
                "Voice IME",
                error.UserMessage,
                NotificationSeverity.Error,
                secrets: _settings.ApiKeys);
            SetTray(TrayStateText.TooltipFor(DictationState.Error, _settings.Hotkey, error.UserMessage));
            if (cause is not null)
            {
                // Type names only — raw exception text may embed key material.
                Logger.Error($"dictation failed error={error.GetType().Name} cause={cause.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            // Presentation is best effort; a disposed tray/window must not
            // strand the coordinator in Error forever.
            Logger.Error($"error presentation failed: {ex.GetType().Name}");
        }
        finally
        {
            _coordinator.AcknowledgeError();
        }
    }

    /// <summary>
    /// Lazily creates the recording overlay (a Window needs a window station,
    /// so construction is deferred until first dictation). Cancel discards
    /// the in-flight capture and hides the overlay via the coordinator's
    /// <c>CancelCurrentOperation</c> entry point.
    /// </summary>
    private void EnsureOverlay()
    {
        if (_overlay is not null)
        {
            return;
        }

        _overlay = new OverlayWindow();
        _overlay.CancelRequested += CancelRecording;
    }

    /// <summary>
    /// Task 8 overlay gating (Handy show_overlay parity, "none" mode): the
    /// pill only raises when the mode allows it. Errors still surface via
    /// balloon + tray — hiding the pill never hides the failure.
    /// </summary>
    private void ShowOverlay(OverlayPhase phase)
    {
        if (!OverlayModes.ShouldShowPill(_settings.ShowOverlay))
        {
            return;
        }

        EnsureOverlay();
        _overlay?.Show(phase, _settings.ShowOverlay);
    }

    /// <summary>
    /// Task 8 autostart reconcile (Handy LaunchAtStartup parity, Startup-
    /// folder subset): create the logon shortcut when opted in but missing,
    /// remove it when opted out but present, otherwise leave the folder
    /// alone. Pure decision via <see cref="StartupShell.DecideReconcile"/> —
    /// this method only executes it. Never throws out of startup: shell
    /// failures warn via trace, a missing shortcut must not kill launch.
    /// </summary>
    private void ReconcileAutostart()
    {
        try
        {
            var path = StartupShell.ShortcutPath(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            switch (StartupShell.DecideReconcile(_settings.Autostart, File.Exists(path)))
            {
                case StartupAction.Create:
                    CreateStartupShortcut(path);
                    break;
                case StartupAction.Remove:
                    File.Delete(path);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"autostart reconcile failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Applies an Autostart toggle immediately (Advanced view save path):
    /// create wins the shortcut, clear removes it. Same never-throw
    /// contract as <see cref="ReconcileAutostart"/>.
    /// </summary>
    private void ApplyAutostartSetting()
    {
        ReconcileAutostart();
    }

    /// <summary>
    /// Writes the logon shortcut via the Windows Script Host shell object —
    /// the small dependency-free route to a .lnk (no COM reference needed,
    /// late-bound so a missing wshom has one catch site in
    /// <see cref="ReconcileAutostart"/>). Target is this process's EXE path.
    /// </summary>
    private static void CreateStartupShortcut(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            throw new InvalidOperationException("Windows Script Host shell is unavailable.");
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = StartupShell.ExePathForShortcut(
                    Environment.ProcessPath, AppContext.BaseDirectory);
                shortcut.WorkingDirectory = AppContext.BaseDirectory;
                shortcut.Description = "Voice IME — launch to the tray at sign-in";
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Task 8 tray guard (Handy parity): icon off + window hidden strands the
    /// app invisible, so that combination surfaces the window instead. Runs at
    /// startup and after Advanced saves. Pure decision via
    /// <see cref="TrayStateText.ResolveTrayGuard"/> — this method only
    /// applies it (and syncs icon visibility to the setting).
    /// </summary>
    private void EnforceTrayGuard()
    {
        if (_tray is not null)
        {
            _tray.Visible = _settings.ShowTrayIcon;
        }

        var windowVisible = _mainWindow?.IsVisible == true;
        if (TrayStateText.ResolveTrayGuard(_settings.ShowTrayIcon, windowVisible)
            == TrayGuardAction.ShowWindow)
        {
            _mainWindow ??= CreateMainWindow();
            AttachLiveHotkey(_mainWindow);
            ShowMainWindow();
        }
    }

    private void RestoreRecordingMute()
    {
        var mute = _recordingMute;
        _recordingMute = null;
        try
        {
            mute?.Dispose();
        }
        catch
        {
            // The endpoint may disappear during shutdown/unplug.
        }
    }

    /// <summary>
    /// Single cancel entry point in App: the coordinator aborts the session
    /// token (in-flight stop/transcribe drains through it) and drains to
    /// Idle, the recorder drops capture, and the OperationCancelled handler
    /// hides the overlay and returns the tray to ready.
    /// </summary>
    private void CancelRecording()
    {
        _hotkeyPhysicalDown = false;
        DisarmCancelHotkey();
        DetachLivePump();
        var livePump = _livePump;
        var liveSession = _liveSession;
        var liveDrain = _liveDrain;
        DetachLivePump();
        ClearLiveFields();
        _liveGeneration = 0;
        _liveFailed = false;
        _liveFailedGeneration = 0;
        ClearLivePreview();
        // Cancel the coordinator token before asking the drain to finish. The
        // drain then releases any in-flight socket send without disposing the
        // pump underneath its reader.
        _coordinator.CancelCurrentOperation();
        _recorder.Cancel();
        RestoreRecordingMute();
        _ = FinishCancelledLiveCleanupAsync(livePump, liveSession, liveDrain);
    }

    private static async Task FinishCancelledLiveCleanupAsync(
        LivePcmPump? pump, LiveSession? session, Task? drain)
    {
        // Abort socket I/O after the coordinator token is cancelled, but keep
        // the pump alive until its reader has observed cancellation.
        await DisposeLiveSessionQuietly(session);
        pump?.Complete();
        if (drain is not null)
        {
            try
            {
                await drain;
            }
            catch
            {
                // Cancellation cleanup is best effort; the wrapper normally
                // converts transport failures into a silent cancellation.
            }
        }

        pump?.Dispose();
        await DisposeLiveSessionQuietly(session);
    }

    /// <summary>
    /// Clears the streaming hypothesis display (cancel/complete paths). The
    /// strings reset here; the overlay hides through its existing path.
    /// </summary>
    private void ClearLivePreview()
    {
        _liveFinals = string.Empty;
        _liveInterim = string.Empty;
        try
        {
            _overlay?.SetPreview(string.Empty);
        }
        catch
        {
            // Best effort — the overlay may already be gone at shutdown.
        }
    }

    /// <summary>
    /// Task 6 cancel arming (see <see cref="SoundFeedback"/>): the cancel key
    /// registers only while busy (recording/uploading) and unregisters on
    /// settle — a bare Esc must never be a global always-on registration.
    /// Routes to the existing <c>CancelCurrentOperation</c> path via
    /// <see cref="CancelRecording"/>, alongside the overlay Cancel button.
    /// A zero-modifier chord registers as a bare key: Win32 accepts
    /// modifiers=0, so Esc fires system-wide only for the seconds we are
    /// busy. Idempotent: safe to call on every state entry.
    /// </summary>
    private void ArmCancelHotkey()
    {
        if (_cancelHotkeyWindow is null || !ShouldArmCancel(_coordinator.State))
        {
            return;
        }

        if (!HotkeyChord.TryParseWithBareKey(_settings.CancelHotkey, out var modifiers, out var vk))
        {
            modifiers = 0;
            vk = 0x1B; // Esc — store coercion already guarantees this parses.
        }

        _cancelHotkeyWindow.Register(modifiers, vk);
    }

    /// <summary>
    /// Pure arm-decision behind <see cref="ArmCancelHotkey"/>: the cancel key
    /// may only register while busy. Any-OS unit-testable (see
    /// <c>SoundFeedbackTests</c>); App itself cannot be constructed in tests.
    /// </summary>
    internal static bool ShouldArmCancel(DictationState state) =>
        state is DictationState.Recording or DictationState.Uploading;

    private void DisarmCancelHotkey() => _cancelHotkeyWindow?.Unregister();

    // MainWindow singleton: Task 4 deleted SettingsWindow (its Base
    // URL/keys/model/prompt fields live in Views.GeminiSettingsView now);
    // Task 5 deleted HistoryWindow (history lives in
    // Views.HistorySettingsView, transit errors re-dictated — audio is
    // never persisted).
    //
    // Task 3: the stored hotkey owns the live global registration, not a
    // hardcoded chord — App registers whatever SettingsStore holds (invalid
    // values already coerce to the default on load), and hands the General
    // screen a LiveHotkeyRegistrar so capture suspends the real hotkey.
    private void RegisterStoredHotkey()
    {
        if (_hotkeyWindow is null)
        {
            return;
        }

        if (!HotkeyChord.TryParse(_settings.Hotkey, out var modifiers, out var vk))
        {
            _settings.Hotkey = HotkeyChord.DefaultChord;
            HotkeyChord.TryParse(_settings.Hotkey, out modifiers, out vk);
        }

        _hotkeyWindow.Register(modifiers, vk);
    }

    private void AttachLiveHotkey(MainWindow window)
    {
        if (window.SectionView(MainSection.General) is Views.GeneralSettingsView general)
        {
            if (_hotkeyWindow is not null)
            {
                general.HotkeyRegistrar = new LiveHotkeyRegistrar(_hotkeyWindow);
            }

            general.Saved -= RefreshTrayTextFromSave;
            general.Saved += RefreshTrayTextFromSave;
        }

        // Task 8: Advanced saves apply live — autostart shortcut follows the
        // toggle and the tray guard re-runs (an icon-off save with the window
        // hidden surfaces the window instead of stranding the app). Idempotent
        // subscribe, same pattern as the General handler above.
        if (window.SectionView(MainSection.Advanced) is Views.AdvancedSettingsView advanced)
        {
            advanced.Saved -= ApplyAdvancedSave;
            advanced.Saved += ApplyAdvancedSave;
        }
    }

    /// <summary>
    /// <see cref="Views.AdvancedSettingsView.Saved"/> adapter: reapplies the
    /// Task 8 shell behaviors the view persists (same shared-store rationale
    /// as <see cref="RefreshTrayTextFromSave"/> — the event's store IS the
    /// live one, so this only executes the reconciles).
    /// </summary>
    private void ApplyAdvancedSave(SettingsStore _)
    {
        _clips.SetLimit(_settings.HistoryLimit);
        if (_mainWindow?.SectionView(MainSection.History)
            is Views.HistorySettingsView history)
        {
            history.BindStore(_clips);
        }

        ApplyAutostartSetting();
        EnforceTrayGuard();
    }

    private MainWindow? _mainWindow;

    /// <summary>
    /// The app's live settings store every section view must bind to. Shared-
    /// instance rule (F1): dictation reads SettingsStore state from here, so a
    /// view holding any other instance would edit settings dictation never
    /// sees — and this instance's key-cursor save would clobber the view's
    /// freshly saved keys. Internal so tests can assert the wiring.
    /// </summary>
    internal SettingsStore LiveSettings => _settings;

    /// <summary>
    /// The app's live transcript store every History view must bind to.
    /// Shared-instance rule (F2): same file, same divergence — a view on a
    /// private store shows stale transcripts and its pin/delete saves clobber
    /// transcripts dictated since the window opened.
    /// </summary>
    internal ClipboardStore LiveClips => _clips;

    /// <summary>
    /// Builds the settings shell with section views bound to the app's live
    /// stores (F1/F2 shared-instance rule — never parameterless Load()s).
    /// </summary>
    internal MainWindow CreateMainWindow() => CreateMainWindow(
        _settings,
        _clips,
        _hotkeyWindow is not null
            ? new LiveHotkeyRegistrar(_hotkeyWindow)
            : new NullHotkeyRegistrar(),
        MicrophoneDevices.ListNames);

    /// <summary>
    /// Static shell builder behind <see cref="CreateMainWindow"/> — the F1/F2
    /// wiring tests drive this overload directly so they assert the live-store
    /// bindings without constructing the singleton
    /// <see cref="System.Windows.Application"/> (only one may exist per
    /// process) or touching disk/DPAPI.
    /// </summary>
    internal static MainWindow CreateMainWindow(
        SettingsStore settings,
        ClipboardStore clips,
        IHotkeyRegistrar registrar,
        Func<IReadOnlyList<string>> listMicrophones)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(listMicrophones);
        var window = new MainWindow(
            () => new Views.GeneralSettingsView(settings, registrar, listMicrophones),
            () => new Views.GeminiSettingsView(settings),
            () => new Views.HistorySettingsView(clips),
            () => new Views.AdvancedSettingsView(settings),
            () => new Views.AboutSettingsView(settings));
        // Close-to-tray guard: hiding with the icon off strands the app
        // invisible (Task 8 review HIGH). Reads the live store so toggling
        // the setting later takes effect without rebuilding the shell.
        window.CanHideWindow = () => settings.ShowTrayIcon;
        // Task 9: first-run hint reads/persists SeenHint on the live store —
        // dismiss saves through the normal path (s => s.Save()).
        window.BindFirstRunHint(settings);
        return window;
    }

    /// <summary>
    /// Opens the settings shell on the Gemini section (the rehomed legacy
    /// SettingsWindow content).
    /// </summary>
    internal void OpenSettings()
    {
        _mainWindow ??= CreateMainWindow();
        AttachLiveHotkey(_mainWindow);
        RefreshSectionViews(_mainWindow, _settings, _clips);
        _mainWindow.NavigateTo(MainSection.Gemini);
        ShowMainWindow();
    }

    /// <summary>
    /// Opens the main window on the History section (the rehomed legacy
    /// HistoryWindow content) — the Task 5 entry point, reachable from the
    /// tray History… row. The view is already bound to <see cref="LiveClips"/>
    /// at construction; <see cref="RefreshSectionViews"/> re-syncs it here so
    /// transcripts dictated while the window was hidden appear, and the
    /// settings fields reload from <see cref="LiveSettings"/>.
    /// </summary>
    internal void OpenHistory()
    {
        _mainWindow ??= CreateMainWindow();
        AttachLiveHotkey(_mainWindow);
        RefreshSectionViews(_mainWindow, _settings, _clips);
        _mainWindow.NavigateTo(MainSection.History);
        ShowMainWindow();
    }

    /// <summary>
    /// Re-syncs a shell's views with the shared stores before it is shown:
    /// the History view rebinds to the live clips (transcripts dictated while
    /// the window was hidden would otherwise never appear) and the
    /// General/Gemini/Advanced/About fields reload from the live settings. Static with
    /// explicit stores so the wiring tests can drive it without constructing
    /// the singleton <see cref="System.Windows.Application"/>. Views built
    /// over App's instances observe the same objects dictation reads, so this
    /// is a display refresh, not a divergence repair.
    /// </summary>
    internal static void RefreshSectionViews(
        MainWindow? window, SettingsStore settings, ClipboardStore clips)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clips);
        if (window is null) return;
        if (window.SectionView(MainSection.History)
            is Views.HistorySettingsView history)
        {
            history.BindStore(clips);
        }

        if (window.SectionView(MainSection.General)
            is Views.GeneralSettingsView general)
        {
            general.ReloadFromSettings();
        }

        if (window.SectionView(MainSection.Gemini)
            is Views.GeminiSettingsView gemini)
        {
            gemini.ReloadFromSettings();
        }

        if (window.SectionView(MainSection.Advanced)
            is Views.AdvancedSettingsView advanced)
        {
            advanced.ReloadFromSettings();
        }

        if (window.SectionView(MainSection.About)
            is Views.AboutSettingsView about)
        {
            about.ReloadFromSettings();
        }

        window.RefreshFirstRunHintBanner();
    }

    internal void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    /// <summary>
    /// The tray tooltip for a hotkey — WinForms caps tooltips at 63 chars, so
    /// this truncates (same rule as <see cref="SetTray"/>).
    /// </summary>
    internal static string TrayText(string hotkey) =>
        TruncateTrayText($"Voice IME — {hotkey} to dictate");

    /// <summary>WinForms caps tooltips at 63 chars — truncate, never throw.</summary>
    internal static string TruncateTrayText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length > TrayTextLimit ? text[..TrayTextLimit] : text;
    }

    /// <summary>
    /// Refreshes the tray tooltip from the live settings store — call after
    /// the hotkey changes (the tooltip is otherwise built once at startup).
    /// </summary>
    internal void RefreshTrayText()
    {
        if (_tray is null) return;
        _tray.Text = TrayText(_settings.Hotkey);
    }

    /// <summary>
    /// <see cref="Views.GeneralSettingsView.Saved"/> adapter: the event
    /// carries the saved store, the tooltip re-reads the live one.
    /// </summary>
    private void RefreshTrayTextFromSave(SettingsStore _)
    {
        RefreshTrayText();
        _mainWindow?.RefreshFirstRunHintBanner();
    }

    private void SetTray(string text)
    {
        if (_tray is null) return;
        _tray.Text = TruncateTrayText(text);
        _mainWindow?.SetStatus(text);
    }

    /// <summary>
    /// Quit happens only here: a hidden MainWindow cancels Close, so permit
    /// it explicitly before shutting down.
    /// </summary>
    private void Quit()
    {
        _mainWindow?.PermitClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        _releaseTimer?.Stop();
        // Task 5: release the Live session without awaiting (socket close is
        // best-effort here; the session CTS is cancelled synchronously first).
        DetachLivePump();
        var livePump = _livePump;
        var liveSession = _liveSession;
        var liveDrain = _liveDrain;
        ClearLiveFields();
        _liveGeneration = 0;
        _liveFailed = false;
        _liveFailedGeneration = 0;
        _coordinator.CancelCurrentOperation();
        _ = FinishCancelledLiveCleanupAsync(livePump, liveSession, liveDrain);
        _coordinator.Dispose();
        _recorder.Dispose();
        RestoreRecordingMute();
        _overlay?.Close();
        _hotkeyWindow?.Dispose();
        _cancelHotkeyWindow?.Dispose();
        _cancelHotkeyWindow = null;
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _llm.Dispose();
        base.OnExit(e);
    }
}
