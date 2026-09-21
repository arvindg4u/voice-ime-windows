using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// REST transport: POST {base}/models/{model}:generateContent (ports Android
/// generateContent path). Owns key rotation and audio scrub. On HTTP 400 it
/// throws <see cref="TransportFallbackException"/> for the two narrow,
/// documented mismatch patterns — every other status fails fast.
/// </summary>
public sealed class RestTranscriptionTransport : ITranscriptionTransport
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public RestTranscriptionTransport(HttpClient http, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUrl);
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.AudioWav);
        try
        {
            ArgumentNullException.ThrowIfNull(request.ApiKeys);
            return await TranscribeCoreAsync(request, ct);
        }
        finally
        {
            Array.Clear(request.AudioWav, 0, request.AudioWav.Length);
        }
    }

    private async Task<TranscriptionResult> TranscribeCoreAsync(
        TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var keys = request.ApiKeys
            .Select(k => k?.Trim() ?? "")
            .Where(k => k.Length > 0)
            .ToList();
        if (keys.Count == 0)
            throw new TranscribeException("No API key — open Settings");

        var audioB64 = Convert.ToBase64String(request.AudioWav);
        var body = BuildGeminiRequestJson(audioB64, request.CustomPrompt);
        var url = $"{_baseUrl.TrimEnd('/')}/models/{request.Model}:generateContent";

        var start = ((request.StartKeyIndex % keys.Count) + keys.Count) % keys.Count;
        TranscribeException? last429 = null;
        for (var i = 0; i < keys.Count; i++)
        {
            var idx = (start + i) % keys.Count;
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.TryAddWithoutValidation("x-goog-api-key", keys[idx]);
            httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(httpRequest, ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TranscribeException("Network error — check connection", ex);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                throw new TranscribeException("Network error — check connection", ex);
            }

            using (response)
            {
                string payload;
                try
                {
                    payload = await response.Content.ReadAsStringAsync(ct);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new TranscribeException("Network error — check connection", ex);
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                    {
                        throw;
                    }

                    throw new TranscribeException("Network error — check connection", ex);
                }
                if ((int)response.StatusCode == 429)
                {
                    last429 = new TranscribeException("Rate limited on all keys — retry later");
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    // CHANGE 1 (new): narrow 400 fallback signal. Detail is fixed
                    // and safe — the raw body (which echoes server text) is never embedded.
                    if (ModelRouter.IsLiveOnlyError((int)response.StatusCode, payload))
                        throw new TransportFallbackException(TransportKind.Live, idx);
                    if (ModelRouter.IsDevInstructionError((int)response.StatusCode, payload))
                        throw new TransportFallbackException(TransportKind.Interactions, idx);
                    throw new TranscribeException(
                        response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                            ? "Invalid API key — check Settings"
                            : $"Request failed ({(int)response.StatusCode}) — try again");
                }
                // CHANGE 2 (new): malformed JSON becomes a typed error instead of
                // leaking a raw JsonException to the UI.
                string transcript;
                try
                {
                    transcript = ParseTranscript(payload);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new TranscribeException("Couldn't understand the response — try again", ex);
                }
                if (string.IsNullOrWhiteSpace(transcript))
                    throw new TranscribeException("Got empty transcript — try again");
                Array.Clear(request.AudioWav, 0, request.AudioWav.Length);
                return new TranscriptionResult(transcript, idx, TransportKind.Rest);
            }
        }
        throw last429 ?? new TranscribeException("Rate limited on all keys — retry later");
    }

    internal static string BuildGeminiRequestJson(string audioB64, string customPrompt)
    {
        // Byte-identical to the previous LlmClient implementation.
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
}
