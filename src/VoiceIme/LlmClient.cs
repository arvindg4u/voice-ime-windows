using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>Thrown when transcription fails; message is safe to show the user.</summary>
public sealed class TranscribeException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>
/// Sends WAV audio to Gemini generateContent and returns the transcript plus the
/// index of the key that worked, so the caller can advance its cursor to
/// (usedIndex + 1) % n for even spread. Key rotation: keys are tried in
/// round-robin order from startIndex; on HTTP 429 the next key is tried.
/// Non-429 errors fail immediately. API keys are never logged.
/// Mirrors Android LlmClient (REST path).
/// </summary>
public sealed class LlmClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly LiveSessionGuard _liveGuard = new();

    internal ILiveSocketFactory? LiveSocketsForTests { get; set; }

    public LlmClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(105),
        };
    }

    public async Task<(string Transcript, int UsedIndex)> TranscribeAsync(
        byte[] wav,
        IReadOnlyList<string> apiKeys,
        string baseUrl,
        string model,
        int startIndex = 0,
        string customPrompt = "",
        bool smartMode = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wav);
        try
        {
            ArgumentNullException.ThrowIfNull(apiKeys);
            var request = new TranscriptionRequest(wav, model, apiKeys, startIndex, customPrompt, smartMode);
            var result = await TranscribeRoutedAsync(request, baseUrl, ct);
            return (result.Transcript, result.UsedKeyIndex);
        }
        finally
        {
            // Every route, including cancellation, malformed responses, and
            // all-key rotation, releases the caller's microphone buffer.
            Array.Clear(wav, 0, wav.Length);
        }
    }

    /// <summary>
    /// Static routing first, then at most one transport switch: only the two
    /// narrow 400 patterns (same key, no rotation for the switch) may move a
    /// request. Fallback targets never fall back again, so loops are
    /// impossible by construction.
    /// </summary>
    private async Task<TranscriptionResult> TranscribeRoutedAsync(
        TranscriptionRequest request, string baseUrl, CancellationToken ct)
    {
        switch (ModelRouter.RouteModel(request.Model))
        {
            case TransportKind.Live:
                try
                {
                    return await NewLiveTransport(baseUrl).TranscribeAsync(CloneForTransport(request), ct);
                }
                catch (TransportFallbackException ex)
                {
                    // Safety net: no Live code throws this, so collapse it to a safe failure.
                    throw new TranscribeException("Transcription failed — try again", ex);
                }
            case TransportKind.Interactions:
                return await new InteractionsTranscriptionTransport(_http, baseUrl)
                    .TranscribeAsync(CloneForTransport(request), ct);
            default:
                try
                {
                    return await new RestTranscriptionTransport(_http, baseUrl)
                        .TranscribeAsync(CloneForTransport(request), ct);
                }
                catch (TransportFallbackException fb) when (fb.Target == TransportKind.Live)
                {
                    return await NewLiveTransport(baseUrl).TranscribeAsync(
                        CloneForTransport(request) with { StartKeyIndex = fb.KeyIndex }, ct);
                }
                catch (TransportFallbackException fb) when (fb.Target == TransportKind.Interactions)
                {
                    return await new InteractionsTranscriptionTransport(_http, baseUrl)
                        .TranscribeAsync(CloneForTransport(request) with { StartKeyIndex = fb.KeyIndex }, ct);
                }
        }
    }

    private static TranscriptionRequest CloneForTransport(TranscriptionRequest request) =>
        request with { AudioWav = (byte[])request.AudioWav.Clone() };

    private LiveTranscriptionTransport NewLiveTransport(string baseUrl) =>
        new(baseUrl, _liveGuard, LiveSocketsForTests ?? new ClientWebSocketLiveSocketFactory());

    internal static string BuildGeminiRequestJson(string audioB64, string customPrompt) =>
        RestTranscriptionTransport.BuildGeminiRequestJson(audioB64, customPrompt);

    internal static string ParseTranscript(string payload) =>
        RestTranscriptionTransport.ParseTranscript(payload);

    public void Dispose() => _http.Dispose();
}
