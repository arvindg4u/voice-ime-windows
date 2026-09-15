using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// Live transport: one Gemini Live WebSocket attempt per key, rotating to the
/// next key on rate-limit signals. WAV is stripped to 16 kHz mono PCM before
/// any socket is created, so typed audio errors never open a connection. Each
/// attempt runs on a private PCM copy because <see cref="LiveSession"/>
/// zeroes its input on completion. All failure messages are fixed user-safe
/// strings; keys and URIs never appear in them. No logging.
/// </summary>
public sealed class LiveTranscriptionTransport : ITranscriptionTransport
{
    private const string NetworkError = "Network error — check connection";
    private const string TimedOut = "Timed out — try again";
    private const string Failed = "Transcription failed — try again";
    private const string RotateSignal = "Rate limited";

    private readonly string _baseUrl;
    private readonly LiveSessionGuard _guard;
    private readonly ILiveSocketFactory _sockets;

    internal LiveTranscriptionTransport(string baseUrl, LiveSessionGuard? guard = null, ILiveSocketFactory? sockets = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _baseUrl = baseUrl;
        _guard = guard ?? new LiveSessionGuard();
        _sockets = sockets ?? new ClientWebSocketLiveSocketFactory();
    }

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var keys = request.ApiKeys
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .ToList();
        if (keys.Count == 0)
            throw new TranscribeException("No API key — open Settings");

        // Typed audio errors surface before any socket is created.
        var pcm = LiveProtocol.WavToMono16kPcm(request.AudioWav);

        var start = ((request.StartKeyIndex % keys.Count) + keys.Count) % keys.Count;
        try
        {
            for (var i = 0; i < keys.Count; i++)
            {
                var idx = (start + i) % keys.Count;
                var uri = LiveProtocol.BuildLiveUri(LiveProtocol.LiveHost(_baseUrl), keys[idx]);
                var session = new LiveSession(
                    _sockets.Create(), request.Model, request.SmartMode, _guard, _guard.Next());
                var outcome = await session.RunAsync((byte[])pcm.Clone(), uri, ct);
                switch (outcome)
                {
                    case LiveAttemptOutcome.Done done:
                        Array.Clear(request.AudioWav, 0, request.AudioWav.Length);
                        return new TranscriptionResult(done.Text, idx, TransportKind.Live);
                    case LiveAttemptOutcome.Rotate:
                        continue;
                    case LiveAttemptOutcome.Fail fail:
                        throw new TranscribeException(fail.Message, fail.Cause);
                }
            }
        }
        catch (TransportFallbackException ex)
        {
            // Safety net: no Live code throws this, so collapse it to a safe failure.
            throw new TranscribeException("Transcription failed — try again", ex);
        }
        throw new TranscribeException("Rate limited on all keys — retry later");
    }

    /// <summary>
    /// Streaming attempt on ONE key (the caller owns rotation/cursor):
    /// connects, streams pump chunks as they arrive, finalizes on pump
    /// completion. Setup-phase failure reports <c>TimedOut</c>: setup timeouts
    /// are dominated by ACK timeouts; a close-before-ACK is a rare transport
    /// drop where either safe message is acceptable. Chunk send failure maps
    /// the rotate signal to <c>Rotate</c> and anything else to a network
    /// <c>Fail</c>; drain cancellation surfaces as
    /// <see cref="OperationCanceledException"/> unwrapped. The finalize
    /// outcome passes through verbatim. No logging; all failure messages are
    /// fixed user-safe strings.
    /// </summary>
    internal async Task<LiveAttemptOutcome> TranscribeStreamingAsync(
        string model,
        bool smartMode,
        string baseUrl,
        string apiKey,
        LivePcmPump pump,
        LiveSessionGuard guard,
        CancellationToken ct,
        LiveSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(guard);

        try
        {
            var uri = LiveProtocol.BuildLiveUri(LiveProtocol.LiveHost(baseUrl), apiKey);
            await using var session = new LiveSession(_sockets.Create(), model, smartMode, guard, guard.Next(), options);
            if (!await session.ConnectAndSetupAsync(uri, ct).ConfigureAwait(false))
                return new LiveAttemptOutcome.Fail(TimedOut, null);

            try
            {
                await pump.DrainAsync(session.SendPcmAsync, ct).ConfigureAwait(false);
            }
            catch (LiveSocketException ex)
            {
                return IsRotateSignal(ex)
                    ? new LiveAttemptOutcome.Rotate()
                    : new LiveAttemptOutcome.Fail(NetworkError, ex);
            }

            return await session.CompleteAndReadFinalAsync(ct).ConfigureAwait(false);
        }
        catch (TransportFallbackException ex)
        {
            // Safety net: no Live code throws this, so collapse it to a safe failure.
            return new LiveAttemptOutcome.Fail(Failed, ex);
        }
    }

    private static bool IsRotateSignal(LiveSocketException ex) =>
        ex.Message.Contains(RotateSignal, StringComparison.Ordinal);
}
