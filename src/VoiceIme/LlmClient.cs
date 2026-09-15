using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        CancellationToken ct = default)
    {
        var transport = new RestTranscriptionTransport(_http, baseUrl);
        var request = new TranscriptionRequest(wav, model, apiKeys, startIndex, customPrompt);
        var result = await transport.TranscribeAsync(request, ct);
        return (result.Transcript, result.UsedKeyIndex);
    }

    internal static string BuildGeminiRequestJson(string audioB64, string customPrompt) =>
        RestTranscriptionTransport.BuildGeminiRequestJson(audioB64, customPrompt);

    internal static string ParseTranscript(string payload) =>
        RestTranscriptionTransport.ParseTranscript(payload);

    public void Dispose() => _http.Dispose();
}
