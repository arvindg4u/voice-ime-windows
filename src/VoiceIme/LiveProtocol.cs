using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VoiceIme;

/// <summary>
/// Gemini Live (BidiGenerateContent WebSocket) wire protocol: message builders,
/// server-message parsers, host/URI helpers, WAV-to-PCM stripping, and handshake
/// error mapping. Ports the Android GeminiTransports wire contract verbatim.
/// System.Text.Json only; no logging; never stores keys.
/// </summary>
internal static class LiveProtocol
{
    internal static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(75);
    internal static readonly TimeSpan FinalizeGrace = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan SetupAckTimeout = TimeSpan.FromSeconds(20);
    internal const int ChunkBytes = 3200;
    internal const string PcmMime = "audio/pcm;rate=16000";
    internal const string RpcPath = "/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    internal const string DefaultHost = "generativelanguage.googleapis.com";

    private static readonly Regex HandshakeStatus = new(@"HTTP/\S+\s+(\d{3})", RegexOptions.Compiled);

    /// <summary>
    /// Live session setup message. inputAudioTranscription is TOP-LEVEL inside
    /// setup, never inside generationConfig. Model used exactly as passed.
    /// </summary>
    internal static string BuildLiveSetupJson(string model, bool smartMode)
    {
        var setup = new JsonObject
        {
            ["model"] = "models/" + model,
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray { "TEXT" },
            },
            ["realtimeInputConfig"] = new JsonObject
            {
                ["automaticActivityDetection"] = new JsonObject { ["disabled"] = true },
            },
            ["inputAudioTranscription"] = new JsonObject
            {
                ["languageCodes"] = new JsonArray(),
                ["mode"] = smartMode ? "SMART" : "VERBATIM",
            },
        };
        return new JsonObject { ["setup"] = setup }.ToJsonString();
    }

    /// <summary>Live turn-start marker (manual VAD): sent before the first PCM chunk.</summary>
    internal static string BuildLiveActivityStartJson() =>
        new JsonObject { ["realtimeInput"] = new JsonObject { ["activityStart"] = new JsonObject() } }.ToJsonString();

    /// <summary>Live turn-end marker (manual VAD): sent after the last PCM chunk.</summary>
    internal static string BuildLiveActivityEndJson() =>
        new JsonObject { ["realtimeInput"] = new JsonObject { ["activityEnd"] = new JsonObject() } }.ToJsonString();

    /// <summary>Live audio chunk message: base64 raw PCM (never WAV bytes) with its mime type.</summary>
    internal static string BuildLiveAudioMessage(byte[] pcmChunk)
    {
        ArgumentNullException.ThrowIfNull(pcmChunk);
        return new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject
                {
                    ["mimeType"] = PcmMime,
                    ["data"] = Convert.ToBase64String(pcmChunk),
                },
            },
        }.ToJsonString();
    }

    /// <summary>Live end-of-stream marker sent after the last PCM chunk.</summary>
    internal static string BuildLiveAudioEndJson() =>
        new JsonObject { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }.ToJsonString();

    /// <summary>
    /// Reads transcription text fragments from one Live server message: FINAL
    /// input-transcription first, else the interim hypothesis. Tolerates
    /// snake_case variants; empty or malformed JSON yields an empty list.
    /// </summary>
    internal static IReadOnlyList<string> ParseLiveInputTranscripts(string serverJson)
    {
        if (!TryParseRoot(serverJson, out var root))
            return Array.Empty<string>();
        using (root)
        {
            if (!TryChild(root.RootElement, "serverContent", "server_content", out var serverContent))
                return Array.Empty<string>();
            var text = FinalText(serverContent);
            if (text.Length == 0)
                text = InterimText(serverContent);
            if (text.Length == 0)
                return Array.Empty<string>();
            return new[] { text };
        }
    }

    /// <summary>True when a Live server message carries a FINAL input transcription.</summary>
    internal static bool HasLiveFinalTranscript(string serverJson)
    {
        if (!TryParseRoot(serverJson, out var root))
            return false;
        using (root)
        {
            if (!TryChild(root.RootElement, "serverContent", "server_content", out var serverContent))
                return false;
            return FinalText(serverContent).Length > 0;
        }
    }

    /// <summary>True when a Live server message acks our setup ({setupComplete:{}}).</summary>
    internal static bool IsLiveSetupComplete(string serverJson)
    {
        if (!TryParseRoot(serverJson, out var root))
            return false;
        using (root)
        {
            var el = root.RootElement;
            return el.ValueKind == JsonValueKind.Object
                && (el.TryGetProperty("setupComplete", out _) || el.TryGetProperty("setup_complete", out _));
        }
    }

    /// <summary>True when a Live server message ends the turn.</summary>
    internal static bool IsLiveTurnComplete(string serverJson)
    {
        if (!TryParseRoot(serverJson, out var root))
            return false;
        using (root)
        {
            if (!TryChild(root.RootElement, "serverContent", "server_content", out var serverContent))
                return false;
            if (IsTrue(serverContent, "turnComplete")
                || IsTrue(serverContent, "turn_complete"))
                return true;
            return IsTrue(serverContent, "generationComplete")
                || IsTrue(serverContent, "generation_complete");
        }
    }

    /// <summary>Live WebSocket host: the baseUrl's host, or the default Google host.</summary>
    internal static string LiveHost(string baseUrl)
    {
        if (!string.IsNullOrEmpty(baseUrl)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;
        return DefaultHost;
    }

    /// <summary>Live wss URI: RPC path with the key attached as an escaped query parameter.</summary>
    internal static Uri BuildLiveUri(string host, string key) =>
        new UriBuilder("wss", host, 443, RpcPath, "?key=" + Uri.EscapeDataString(key)).Uri;

    /// <summary>
    /// Strips the WAV header, returning raw 16 kHz 16-bit mono PCM for the Live
    /// socket. Anything else fails fast with a user-safe message.
    /// </summary>
    internal static byte[] WavToMono16kPcm(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (wav.Length < 12 || Tag(wav, 0) != "RIFF" || Tag(wav, 8) != "WAVE")
            throw new TranscribeException("Invalid audio — try again");

        var audioFormat = -1;
        var channels = -1;
        var sampleRate = -1;
        var blockAlign = -1;
        var bitsPerSample = -1;
        var dataOffset = -1;
        var dataLength = -1;

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Tag(wav, pos);
            var size = (long)U32(wav, pos + 4);
            if (pos + 8 + size > wav.Length)
                throw new TranscribeException("Invalid audio — try again");
            if (id == "fmt ")
            {
                if (size < 16)
                    throw new TranscribeException("Invalid audio — try again");
                audioFormat = U16(wav, pos + 8);
                channels = U16(wav, pos + 10);
                sampleRate = (int)U32(wav, pos + 12);
                blockAlign = U16(wav, pos + 20);
                bitsPerSample = U16(wav, pos + 22);
            }
            else if (id == "data")
            {
                dataOffset = pos + 8;
                dataLength = (int)size;
            }
            pos += 8 + (int)size + ((int)size & 1);
        }

        if (dataOffset < 0 || dataLength < 0)
            throw new TranscribeException("Invalid audio — try again");
        if (audioFormat != 1 || channels != 1 || sampleRate != 16000
            || blockAlign != 2 || bitsPerSample != 16)
            throw new TranscribeException("Audio format not supported — try again");
        if (dataLength % blockAlign != 0)
            throw new TranscribeException("Invalid audio — try again");

        var pcm = new byte[dataLength];
        Buffer.BlockCopy(wav, dataOffset, pcm, 0, dataLength);
        return pcm;
    }

    /// <summary>
    /// Maps a WebSocket handshake failure to a fixed user-safe message plus
    /// whether the caller should rotate keys. Never echoes its input.
    /// </summary>
    internal static (string Message, bool Rotate) MapHandshakeError(string statusAndHead)
    {
        var input = statusAndHead ?? "";
        var match = HandshakeStatus.Match(input);
        if (!match.Success)
            return ("Network error — check connection", false);
        var code = match.Groups[1].Value;
        if (code == "401" || code == "403")
            return ("Invalid API key — check Settings", false);
        if (code == "429" || input.Contains("Rate limited", StringComparison.Ordinal))
            return ("Rate limited on all keys — retry later", true);
        return ($"Server error (HTTP {code}) — try again", false);
    }

    private static bool TryParseRoot(string serverJson, out JsonDocument root)
    {
        root = null!;
        if (string.IsNullOrEmpty(serverJson))
            return false;
        try
        {
            root = JsonDocument.Parse(serverJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryChild(JsonElement el, string camel, string snake, out JsonElement child)
    {
        child = default;
        if (el.ValueKind != JsonValueKind.Object)
            return false;
        return el.TryGetProperty(camel, out child) || el.TryGetProperty(snake, out child);
    }

    private static string FinalText(JsonElement serverContent) =>
        TryChild(serverContent, "inputTranscription", "input_transcription", out var t) ? TextOf(t) : "";

    private static string InterimText(JsonElement serverContent) =>
        TryChild(serverContent, "interimInputTranscription", "interim_input_transcription", out var t) ? TextOf(t) : "";

    private static string TextOf(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("text", out var text)
        && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? ""
            : "";

    private static bool IsTrue(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string Tag(byte[] buf, int offset) =>
        new([(char)buf[offset], (char)buf[offset + 1], (char)buf[offset + 2], (char)buf[offset + 3]]);

    private static int U16(byte[] buf, int offset) =>
        buf[offset] | (buf[offset + 1] << 8);

    private static uint U32(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
}
