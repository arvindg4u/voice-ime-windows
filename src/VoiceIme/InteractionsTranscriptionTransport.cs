using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// Interactions transport for the exact allowlist model
/// (<c>gemini-3.5-transcribe</c>): POST {base}/interactions with the model in
/// the body, preferences inline in the text part (transcribe models reject
/// systemInstruction), smart/verbatim via
/// generation_config.transcription_config.mode. Same rotation contract as REST.
/// </summary>
public sealed class InteractionsTranscriptionTransport : ITranscriptionTransport
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public InteractionsTranscriptionTransport(HttpClient http, string baseUrl)
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
        var body = BuildInteractionsRequestJson(audioB64, request.Model, request.CustomPrompt, request.SmartMode);
        var url = $"{_baseUrl.TrimEnd('/')}/interactions";

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
                    throw new TranscribeException(
                        response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                            ? "Invalid API key — check Settings"
                            : $"Request failed ({(int)response.StatusCode}) — try again");
                }
                var transcript = ParseInteractionsTranscript(payload);
                Array.Clear(request.AudioWav, 0, request.AudioWav.Length);
                return new TranscriptionResult(transcript, idx, TransportKind.Interactions);
            }
        }
        throw last429 ?? new TranscribeException("Rate limited on all keys — retry later");
    }

    internal static string BuildInteractionsRequestJson(
        string audioB64, string model, string customPrompt, bool smartMode)
    {
        var trimmed = (customPrompt ?? "").Trim();
        var instruction = trimmed.Length == 0
            ? "Transcribe the speech exactly. Output only the transcript, no commentary."
            : "Transcribe the speech exactly. Output only the transcript, no commentary. User preferences:\n" + trimmed;
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("input");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", instruction);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("type", "audio");
            writer.WriteString("data", audioB64);
            writer.WriteString("mime_type", "audio/wav");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("generation_config");
            writer.WriteStartObject("transcription_config");
            writer.WriteString("mode", smartMode ? "smart" : "verbatim");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string ParseInteractionsTranscript(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("output_text", out var direct)
                && direct.GetString() is { } text
                && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
            var sb = new StringBuilder();
            if (root.TryGetProperty("steps", out var steps))
            {
                foreach (var step in steps.EnumerateArray())
                {
                    if (!step.TryGetProperty("type", out var type)
                        || type.GetString() != "model_output") continue;
                    if (!step.TryGetProperty("content", out var contents)) continue;
                    foreach (var content in contents.EnumerateArray())
                    {
                        if (!content.TryGetProperty("text", out var part)) continue;
                        if (content.TryGetProperty("type", out var partType)
                            && partType.GetString() is { } t
                            && t != "text" && t.Length != 0) continue;
                        sb.Append(part.GetString());
                    }
                }
            }
            var joined = sb.ToString().Trim();
            if (joined.Length != 0) return joined;
        }
        catch
        {
            // Malformed JSON falls through to the typed empty error below.
        }
        throw new TranscribeException("Got empty transcript — try again");
    }
}
