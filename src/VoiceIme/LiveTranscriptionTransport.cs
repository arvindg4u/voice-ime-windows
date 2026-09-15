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
}
