using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// Timeout and grace configuration for one <see cref="LiveSession"/> attempt.
/// Defaults track the <see cref="LiveProtocol"/> constants; tests pass
/// millisecond-scale values for determinism.
/// </summary>
internal sealed record LiveSessionOptions(
    TimeSpan SetupAckTimeout,
    TimeSpan FinalizeGrace,
    TimeSpan LiveTimeout,
    TimeSpan CloseGrace)
{
    internal static LiveSessionOptions Default => new(
        LiveProtocol.SetupAckTimeout,
        LiveProtocol.FinalizeGrace,
        LiveProtocol.LiveTimeout,
        LiveProtocol.CloseGrace);
}

/// <summary>
/// Outcome of one Live attempt. Done carries only non-empty authoritative final
/// text — interim hypotheses are never fabricated into a final.
/// </summary>
internal abstract record LiveAttemptOutcome
{
    internal sealed record Done(string Text) : LiveAttemptOutcome;
    internal sealed record Rotate : LiveAttemptOutcome;
    internal sealed record Fail(string Message, Exception? Cause) : LiveAttemptOutcome;
}

/// <summary>
/// One Gemini Live attempt: setup gating, chunked PCM streaming, bounded finalize.
/// Linear flow, no background threads: the receive loops below are driven inline
/// and each phase reaches exactly one return path per outcome by construction, so
/// no TaskCompletionSource or Interlocked coordination is needed for exactly-once
/// completion. Every received message first checks the session guard — a stale
/// session ignores everything and never streams. Every socket call observes the
/// LiveTimeout-linked token (derived from the caller's token), so the outer bound
/// covers hung sends and connects as well as receives; caller cancellation still
/// surfaces as OperationCanceledException unwrapped. Interim hypotheses never reach
/// the accumulator. No logging; all failure messages are fixed user-safe strings.
/// </summary>
internal sealed class LiveSession : IAsyncDisposable
{
    private const string NetworkError = "Network error — check connection";
    private const string TimedOut = "Timed out — try again";
    private const string EmptyTranscript = "Got empty transcript — try again";
    private const string Failed = "Transcription failed — try again";
    private const string RotateSignal = "Rate limited";

    private readonly ILiveSocket _socket;
    private readonly string _model;
    private readonly bool _smartMode;
    private readonly LiveSessionGuard _guard;
    private readonly long _sessionId;
    private readonly LiveSessionOptions _options;
    private int _disposed;
    private CancellationTokenSource? _sessionCts;
    private bool _activityStarted;
    private bool _completed;

    internal LiveSession(
        ILiveSocket socket,
        string model,
        bool smartMode,
        LiveSessionGuard guard,
        long sessionId,
        LiveSessionOptions? options = null)
    {
        _socket = socket;
        _model = model;
        _smartMode = smartMode;
        _guard = guard;
        _sessionId = sessionId;
        _options = options ?? LiveSessionOptions.Default;
    }

    /// <summary>
    /// Raised synchronously on the socket-reader path for each interim
    /// hypothesis preview (replace semantics — latest only, never
    /// accumulated). Callers marshal to the UI thread; listener exceptions
    /// are swallowed so the reader never throws. No-op when unsubscribed.
    /// </summary>
    internal event Action<string>? InterimReceived;

    /// <summary>
    /// Raised synchronously on the socket-reader path for each non-empty
    /// final delta after it reaches the accumulator. Listener exceptions are
    /// swallowed so the reader never throws. No-op when unsubscribed.
    /// </summary>
    internal event Action<string>? FinalReceived;

    internal async Task<LiveAttemptOutcome> RunAsync(byte[] pcm, Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(uri);

        using var liveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        liveCts.CancelAfter(_options.LiveTimeout);
        var liveToken = liveCts.Token;
        // Prompt abort: every socket call below observes the linked token, so a
        // stuck call unblocks the moment ct cancels (surfacing as OCE unwrapped);
        // this registration additionally aborts transports that need an explicit
        // abort to release promptly.
        using var abort = ct.Register(static state =>
        {
            try
            {
                _ = ((ILiveSocket)state!).DisposeAsync();
            }
            catch
            {
                // Best-effort abort only.
            }
        }, _socket);

        try
        {
            try
            {
                await _socket.ConnectAsync(uri, liveToken);
            }
            catch (LiveSocketException ex)
            {
                return MapSocketFailure(ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new LiveAttemptOutcome.Fail(TimedOut, null);
            }

            try
            {
                await _socket.SendTextAsync(LiveProtocol.BuildLiveSetupJson(_model, _smartMode), liveToken);
            }
            catch (LiveSocketException ex)
            {
                return IsRotateSignal(ex)
                    ? new LiveAttemptOutcome.Rotate()
                    : new LiveAttemptOutcome.Fail(NetworkError, ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new LiveAttemptOutcome.Fail(TimedOut, null);
            }

            var ack = await WaitForSetupAckAsync(liveToken, ct);
            if (ack is not null)
                return ack;

            try
            {
                await StreamAudioAsync(pcm, liveToken);
            }
            catch (LiveSocketException ex)
            {
                return IsRotateSignal(ex)
                    ? new LiveAttemptOutcome.Rotate()
                    : new LiveAttemptOutcome.Fail(NetworkError, ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new LiveAttemptOutcome.Fail(TimedOut, null);
            }

            return await ReadFinalAsync(liveToken, ct);
        }
        catch (TransportFallbackException ex)
        {
            // No Live code throws this; mapped defensively to a safe failure.
            return new LiveAttemptOutcome.Fail(Failed, ex);
        }
        catch (LiveSocketException ex)
        {
            return MapSocketFailure(ex);
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            _guard.Invalidate(_sessionId);
            using var closeCts = new CancellationTokenSource(_options.CloseGrace);
            try
            {
                await _socket.CloseAsync(closeCts.Token);
            }
            catch
            {
                // Best-effort close: never mask the attempt outcome.
            }
            try
            {
                await _socket.DisposeAsync();
            }
            catch
            {
                // Disposal is best effort and must not replace the attempt
                // outcome or cancellation exception.
            }
            Array.Clear(pcm, 0, pcm.Length);
        }
    }

    /// <summary>
    /// Streaming phase 1: connects, sends setup, and gates on the setup ACK.
    /// Returns false (never throws, except on caller cancellation) when the
    /// session cannot proceed; the caller treats that as a typed session
    /// failure. Bounds the whole streaming session by LiveTimeout from this
    /// call: the linked CTS is stored as a field and torn down in
    /// <see cref="CompleteAndReadFinalAsync"/> or <see cref="DisposeAsync"/>.
    /// Reuses the Phase-2 <see cref="WaitForSetupAckAsync"/> verbatim — its
    /// null-means-proceed contract maps directly onto this bool API, so no
    /// second ACK loop exists to drift.
    /// </summary>
    internal async Task<bool> ConnectAndSetupAsync(Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ct.ThrowIfCancellationRequested();
        if (_sessionCts is not null)
            throw new InvalidOperationException("Session is already connected.");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sessionCts.CancelAfter(_options.LiveTimeout);
        var liveToken = _sessionCts.Token;

        try
        {
            try
            {
                await _socket.ConnectAsync(uri, liveToken);
            }
            catch (LiveSocketException)
            {
                // Collapses Phase-2's Rotate/Fail mapping to false by design:
                // this API reports only proceed/no-proceed.
                TearDownSession();
                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TearDownSession();
                return false;
            }

            try
            {
                await _socket.SendTextAsync(LiveProtocol.BuildLiveSetupJson(_model, _smartMode), liveToken);
            }
            catch (LiveSocketException)
            {
                TearDownSession();
                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TearDownSession();
                return false;
            }

            LiveAttemptOutcome? ack;
            try
            {
                ack = await WaitForSetupAckAsync(liveToken, ct);
            }
            catch (LiveSocketException)
            {
                // Receive failure during the ACK gate: same no-proceed outcome.
                TearDownSession();
                return false;
            }
            if (ack is not null)
            {
                TearDownSession();
                return false;
            }
            return true;
        }
        catch (TransportFallbackException)
        {
            // No Live code throws this; mapped defensively, mirroring RunAsync.
            TearDownSession();
            return false;
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation (OCE unwrapped by contract): release the
            // session CTS so no LiveTimeout timer leaks, then rethrow.
            TearDownSession();
            throw;
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            TearDownSession();
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>
    /// Streaming phase 2: sends <paramref name="pcmChunk"/> as one audio
    /// message in call order. The first call sends the activityStart turn
    /// marker exactly once before its chunk. Chunks must be 16 kHz mono
    /// 16-bit PCM — caller contract, not validated here. Throws
    /// LiveSocketException on send failure with OperationCanceledException
    /// unwrapped. After <see cref="CompleteAndReadFinalAsync"/> starts this
    /// is a no-op: it returns without sending and never throws.
    /// </summary>
    internal async Task SendPcmAsync(byte[] pcmChunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcmChunk);
        try
        {
            if (_completed)
                return;
            var sessionCts = _sessionCts;
            if (sessionCts is null)
                throw new InvalidOperationException("Session is not connected.");
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token, ct);
            var token = linkedCts.Token;
            if (!_activityStarted)
            {
                await _socket.SendTextAsync(LiveProtocol.BuildLiveActivityStartJson(), token);
                _activityStarted = true;
            }
            await _socket.SendTextAsync(LiveProtocol.BuildLiveAudioMessage(pcmChunk), token);
        }
        finally
        {
            Array.Clear(pcmChunk, 0, pcmChunk.Length);
        }
    }

    /// <summary>
    /// Streaming phase 3: sends activityEnd + audioStreamEnd in order, then a
    /// bounded finalize reusing the exact Phase-2 <see cref="ReadFinalAsync"/>
    /// logic (turnComplete-not-final ruling, empty-to-typed-error, guard
    /// checks, Rotate-signal mapping) → Done/Rotate/Fail. Closes and disposes
    /// the socket before returning, mirroring RunAsync's finally.
    /// </summary>
    internal async Task<LiveAttemptOutcome> CompleteAndReadFinalAsync(CancellationToken ct)
    {
        _completed = true;
        var sessionCts = _sessionCts;
        if (sessionCts is null)
            throw new InvalidOperationException("Session is not connected.");
        var liveToken = sessionCts.Token;

        try
        {
            try
            {
                using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(liveToken, ct);
                var tailToken = tailCts.Token;
                await _socket.SendTextAsync(LiveProtocol.BuildLiveActivityEndJson(), tailToken);
                await _socket.SendTextAsync(LiveProtocol.BuildLiveAudioEndJson(), tailToken);
            }
            catch (LiveSocketException ex)
            {
                return MapSocketFailure(ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new LiveAttemptOutcome.Fail(TimedOut, null);
            }

            return await ReadFinalAsync(liveToken, ct);
        }
        catch (TransportFallbackException ex)
        {
            // No Live code throws this; mapped defensively, mirroring RunAsync.
            return new LiveAttemptOutcome.Fail(Failed, ex);
        }
        catch (LiveSocketException ex)
        {
            return MapSocketFailure(ex);
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            TearDownSession();
            await CloseSocketAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        TearDownSession();
        try
        {
            await _socket.DisposeAsync();
        }
        catch
        {
            // Best effort during cancellation/shutdown.
        }
    }

    /// <summary>
    /// Cancels and disposes the session CTS exactly once; safe to call from
    /// setup failure, complete, and dispose paths. The socket stays open for
    /// <see cref="DisposeAsync"/> (or Complete's close) to release, mirroring
    /// RunAsync's ownership.
    /// </summary>
    private void TearDownSession()
    {
        var sessionCts = Interlocked.Exchange(ref _sessionCts, null);
        if (sessionCts is not null)
        {
            try
            {
                sessionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Cancel-after-dispose race from a concurrent teardown; already gone.
            }
            sessionCts.Dispose();
        }

        // Also invalidate setup-only/disposed sessions that have no CTS left.
        // Invalidate is conditional, so an old teardown cannot retire a newer
        // session that has already claimed the guard.
        _guard.Invalidate(_sessionId);
    }

    /// <summary>
    /// Best-effort close under <see cref="LiveSessionOptions.CloseGrace"/>,
    /// then dispose — the Complete path's mirror of RunAsync's finally. Never
    /// masks the attempt outcome.
    /// </summary>
    private async Task CloseSocketAsync()
    {
        using var closeCts = new CancellationTokenSource(_options.CloseGrace);
        try
        {
            await _socket.CloseAsync(closeCts.Token);
        }
        catch
        {
            // Best-effort close: never mask the attempt outcome.
        }
        try
        {
            await _socket.DisposeAsync();
        }
        catch
        {
            // Close is best effort; never mask the transcript outcome.
        }
    }

    /// <summary>
    /// Reads until the setup ACK. Returns null to proceed; any other message is
    /// ignored and no PCM is sent until the ACK arrives.
    /// </summary>
    private async Task<LiveAttemptOutcome?> WaitForSetupAckAsync(CancellationToken liveToken, CancellationToken ct)
    {
        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(liveToken);
        setupCts.CancelAfter(_options.SetupAckTimeout);
        try
        {
            while (true)
            {
                var message = await _socket.ReceiveTextAsync(setupCts.Token);
                if (message is null)
                    return new LiveAttemptOutcome.Fail(NetworkError, null);
                if (!_guard.IsCurrent(_sessionId))
                    continue;
                if (LiveProtocol.IsLiveSetupComplete(message))
                    return null;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // SetupAckTimeout (or the outer LiveTimeout — both report the same timeout).
            return new LiveAttemptOutcome.Fail(TimedOut, null);
        }
    }

    private async Task StreamAudioAsync(byte[] pcm, CancellationToken ct)
    {
        await _socket.SendTextAsync(LiveProtocol.BuildLiveActivityStartJson(), ct);
        for (var offset = 0; offset < pcm.Length; offset += LiveProtocol.ChunkBytes)
        {
            var length = Math.Min(LiveProtocol.ChunkBytes, pcm.Length - offset);
            var chunk = new byte[length];
            try
            {
                Buffer.BlockCopy(pcm, offset, chunk, 0, length);
                await _socket.SendTextAsync(LiveProtocol.BuildLiveAudioMessage(chunk), ct);
            }
            finally
            {
                Array.Clear(chunk, 0, chunk.Length);
            }
        }
        await _socket.SendTextAsync(LiveProtocol.BuildLiveActivityEndJson(), ct);
        await _socket.SendTextAsync(LiveProtocol.BuildLiveAudioEndJson(), ct);
    }

    private async Task<LiveAttemptOutcome> ReadFinalAsync(CancellationToken liveToken, CancellationToken ct)
    {
        var accumulator = new FinalAccumulator();
        using var finalizeCts = CancellationTokenSource.CreateLinkedTokenSource(liveToken);
        finalizeCts.CancelAfter(_options.FinalizeGrace);
        try
        {
            while (true)
            {
                var message = await _socket.ReceiveTextAsync(finalizeCts.Token);
                if (message is null)
                    return SnapshotOrEmpty(accumulator);
                if (!_guard.IsCurrent(_sessionId))
                    continue;
                if (LiveProtocol.HasLiveFinalTranscript(message))
                {
                    var advanced = AppendFinals(accumulator, message);
                    var snapshot = accumulator.Snapshot();
                    // A final that adds no new text (server echo of cumulative
                    // input) still counts as the final signal: complete with the
                    // authoritative text gathered so far.
                    if (!advanced && snapshot.Length > 0)
                        return new LiveAttemptOutcome.Done(snapshot);
                    continue;
                }
                // Interim hypothesis preview: latest only, never accumulated.
                // HasLiveFinalTranscript was false above, so ParseLive... yields
                // at most the interim text; unknown frames yield empty (no raise).
                // Raised before the turnComplete check so a preview sharing the
                // turn-end frame still reaches listeners. Interim never reaches
                // the accumulator, so no final is fabricated from it.
                foreach (var preview in LiveProtocol.ParseLiveInputTranscripts(message))
                    RaiseQuietly(InterimReceived, preview);
                if (LiveProtocol.IsLiveTurnComplete(message))
                    return SnapshotOrEmpty(accumulator);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (liveToken.IsCancellationRequested)
                return new LiveAttemptOutcome.Fail(TimedOut, null);
            return SnapshotOrEmpty(accumulator);
        }
    }

    private bool AppendFinals(FinalAccumulator accumulator, string message)
    {
        var advanced = false;
        foreach (var fragment in LiveProtocol.ParseLiveInputTranscripts(message))
        {
            var delta = accumulator.Append(fragment);
            if (delta.Length == 0)
                continue;
            advanced = true;
            RaiseQuietly(FinalReceived, delta);
        }
        return advanced;
    }

    /// <summary>
    /// Invokes a transcript listener without ever throwing: a null event is a
    /// no-op and listener exceptions are swallowed, matching Android's
    /// try { onFinal } catch around its UI callbacks.
    /// </summary>
    private static void RaiseQuietly(Action<string>? listeners, string text)
    {
        if (listeners is null)
            return;
        try
        {
            listeners(text);
        }
        catch
        {
            // Listener failure must never break the socket-reader path.
        }
    }

    private static LiveAttemptOutcome SnapshotOrEmpty(FinalAccumulator accumulator)
    {
        var snapshot = accumulator.Snapshot();
        return snapshot.Length > 0
            ? new LiveAttemptOutcome.Done(snapshot)
            : new LiveAttemptOutcome.Fail(EmptyTranscript, null);
    }

    private static LiveAttemptOutcome MapSocketFailure(LiveSocketException ex)
    {
        if (IsRotateSignal(ex))
            return new LiveAttemptOutcome.Rotate();
        // Never expose socket/URI details: handshake errors can include the
        // API-key query string or server-provided text.
        return new LiveAttemptOutcome.Fail(NetworkError, ex);
    }

    private static bool IsRotateSignal(LiveSocketException ex) =>
        ex.Message.Contains(RotateSignal, StringComparison.Ordinal);
}
