using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// Everything all transports genuinely share. Transport-specific fields
/// (Live session, activity markers, PCM streaming) belong on Live contracts,
/// never here.
/// </summary>
public sealed record TranscriptionRequest(
    byte[] AudioWav,
    string Model,
    IReadOnlyList<string> ApiKeys,
    int StartKeyIndex = 0,
    string CustomPrompt = "",
    bool SmartMode = false);

/// <summary>Common result: transcript, working key index, and which transport served it.</summary>
public sealed record TranscriptionResult(
    string Transcript,
    int UsedKeyIndex,
    TransportKind TransportUsed);

/// <summary>
/// One transport family. Accepts <see cref="CancellationToken"/>; cancellation
/// surfaces as <see cref="OperationCanceledException"/>, never as a typed
/// network error. Failures throw user-safe <see cref="TranscribeException"/>.
/// </summary>
public interface ITranscriptionTransport
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

/// <summary>
/// Internal signal from a transport to the orchestrator: retry the same
/// logical request on <see cref="Target"/> starting at <see cref="KeyIndex"/>
/// (same key, no rotation for the switch itself). Detail is a fixed safe
/// string — never the HTTP body.
/// </summary>
internal sealed class TransportFallbackException(TransportKind target, int keyIndex)
    : Exception("Transport fallback to " + target)
{
    public TransportKind Target { get; } = target;
    public int KeyIndex { get; } = keyIndex;
}
