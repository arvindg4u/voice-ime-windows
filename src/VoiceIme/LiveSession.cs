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
    private bool _disposed;

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
                return new LiveAttemptOutcome.Fail(NetworkError, ex);
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
                return new LiveAttemptOutcome.Fail(NetworkError, ex);
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
        finally
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
            await _socket.DisposeAsync();
            Array.Clear(pcm, 0, pcm.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _socket.DisposeAsync();
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
            Buffer.BlockCopy(pcm, offset, chunk, 0, length);
            await _socket.SendTextAsync(LiveProtocol.BuildLiveAudioMessage(chunk), ct);
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
                if (LiveProtocol.IsLiveTurnComplete(message))
                    return SnapshotOrEmpty(accumulator);
                // Interim hypotheses and anything else are ignored: interim only
                // ever describes the in-progress turn and never reaches the
                // accumulator, so no final is fabricated from it.
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (liveToken.IsCancellationRequested)
                return new LiveAttemptOutcome.Fail(TimedOut, null);
            return SnapshotOrEmpty(accumulator);
        }
    }

    private static bool AppendFinals(FinalAccumulator accumulator, string message)
    {
        var advanced = false;
        foreach (var fragment in LiveProtocol.ParseLiveInputTranscripts(message))
            advanced |= accumulator.Append(fragment).Length > 0;
        return advanced;
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
        if (ex.Message.Contains(RotateSignal, StringComparison.Ordinal))
            return new LiveAttemptOutcome.Rotate();
        return new LiveAttemptOutcome.Fail(ex.Message, ex);
    }
}
