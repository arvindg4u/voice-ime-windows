using System.Threading;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// Phase-1 Live placeholder: routing recognizes Live-candidate models, but the
/// WebSocket engine (framing, setup ACK, PCM, resumption) is Phase 2. Fails
/// loudly with a user-safe message instead of silently using REST.
/// </summary>
public sealed class LiveTranscriptionTransport : ITranscriptionTransport
{
    public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct) =>
        throw new TranscribeException("Live transcription isn't supported yet — coming in Phase 2");
}
