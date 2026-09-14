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
        var keys = apiKeys
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .ToList();
        if (keys.Count == 0)
            throw new TranscribeException("No API key — open Settings");

        var audioB64 = Convert.ToBase64String(wav);
        var body = BuildGeminiRequestJson(audioB64, customPrompt);
        var url = $"{baseUrl.TrimEnd('/')}/models/{model}:generateContent";

        var start = ((startIndex % keys.Count) + keys.Count) % keys.Count;
        TranscribeException? last429 = null;
        for (var i = 0; i < keys.Count; i++)
        {
            var idx = (start + i) % keys.Count;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("x-goog-api-key", keys[idx]);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TranscribeException("Network error — check connection", ex);
            }

            using (response)
            {
                var payload = await response.Content.ReadAsStringAsync(ct);
                if ((int)response.StatusCode == 429)
                {
                    last429 = new TranscribeException("Rate limited on all keys — retry later");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new TranscribeException(
                        response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                            ? "Invalid API key — check Settings"
                            : $"Request failed ({(int)response.StatusCode}) — try again");
                }
                var transcript = ParseTranscript(payload);
                if (string.IsNullOrWhiteSpace(transcript))
                    throw new TranscribeException("Got empty transcript — try again");
                // Scrub audio from memory ASAP.
                Array.Clear(wav, 0, wav.Length);
                return (transcript, idx);
            }
        }
        throw last429 ?? new TranscribeException("Rate limited on all keys — retry later");
    }

    internal static string BuildGeminiRequestJson(string audioB64, string customPrompt)
    {
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = "audio/wav",
                    ["data"] = audioB64,
                },
            },
            new JsonObject { ["text"] = "Transcribe this audio. Respond with the transcript only." },
        };
        var root = new JsonObject { ["contents"] = new JsonArray { new JsonObject { ["parts"] = parts } } };
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            root["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray
                {
                    new JsonObject { ["text"] = "User preferences: " + customPrompt.Trim() },
                },
            };
        }
        return root.ToJsonString();
    }

    internal static string ParseTranscript(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var sb = new StringBuilder();
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates))
            return "";
        foreach (var c in candidates.EnumerateArray())
        {
            if (!c.TryGetProperty("content", out var content)) continue;
            if (!content.TryGetProperty("parts", out var parts)) continue;
            foreach (var p in parts.EnumerateArray())
            {
                if (p.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
            }
        }
        return sb.ToString().Trim();
    }

    public void Dispose() => _http.Dispose();
}
