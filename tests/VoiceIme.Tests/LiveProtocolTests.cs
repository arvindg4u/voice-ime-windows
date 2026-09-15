using System;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LiveProtocolTests
{
    private static JsonElement Root(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData(true, "SMART")]
    [InlineData(false, "VERBATIM")]
    public void BuildLiveSetupJson_Shape_ModalitiesVadMode(bool smart, string mode)
    {
        var root = Root(LiveProtocol.BuildLiveSetupJson("gemini-2.5-flash-live", smart));
        var setup = root.GetProperty("setup");
        Assert.Equal("models/gemini-2.5-flash-live", setup.GetProperty("model").GetString());
        Assert.Equal("TEXT", setup.GetProperty("generationConfig")
            .GetProperty("responseModalities")[0].GetString());
        Assert.True(setup.GetProperty("realtimeInputConfig")
            .GetProperty("automaticActivityDetection").GetProperty("disabled").GetBoolean());
        var iat = setup.GetProperty("inputAudioTranscription");
        Assert.Equal(mode, iat.GetProperty("mode").GetString());
        Assert.Equal(0, iat.GetProperty("languageCodes").GetArrayLength());
        Assert.False(setup.GetProperty("generationConfig").TryGetProperty("inputAudioTranscription", out _));
    }

    [Fact]
    public void BuildLiveSetupJson_UsesModelExactly()
    {
        var root = Root(LiveProtocol.BuildLiveSetupJson("  My-LIVE-Custom ", false));
        Assert.Equal("models/  My-LIVE-Custom ", root.GetProperty("setup").GetProperty("model").GetString());
    }

    [Fact]
    public void ActivityMarkers_HaveExactShapes()
    {
        Assert.Equal("{}", Root(LiveProtocol.BuildLiveActivityStartJson())
            .GetProperty("realtimeInput").GetProperty("activityStart").GetRawText());
        Assert.Equal("{}", Root(LiveProtocol.BuildLiveActivityEndJson())
            .GetProperty("realtimeInput").GetProperty("activityEnd").GetRawText());
        Assert.True(Root(LiveProtocol.BuildLiveAudioEndJson())
            .GetProperty("realtimeInput").GetProperty("audioStreamEnd").GetBoolean());
    }

    [Fact]
    public void BuildLiveAudioMessage_CarriesRawPcmBase64WithMime()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        var root = Root(LiveProtocol.BuildLiveAudioMessage(pcm));
        var audio = root.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(pcm), audio.GetProperty("data").GetString());
    }

    [Fact]
    public void ParseLiveInputTranscripts_FinalWinsOverInterim()
    {
        var msg = """{"serverContent":{"inputTranscription":{"text":"final words"},"interimInputTranscription":{"text":"interim words"}}}""";
        Assert.Equal(["final words"], LiveProtocol.ParseLiveInputTranscripts(msg));
        Assert.True(LiveProtocol.HasLiveFinalTranscript(msg));
    }

    [Fact]
    public void ParseLiveInputTranscripts_InterimOnly_IsNotFinal()
    {
        var msg = """{"serverContent":{"interimInputTranscription":{"text":"hello wor"}}}""";
        Assert.Equal(["hello wor"], LiveProtocol.ParseLiveInputTranscripts(msg));
        Assert.False(LiveProtocol.HasLiveFinalTranscript(msg));
    }

    [Fact]
    public void ParseLiveInputTranscripts_SnakeCase_Tolerated()
    {
        var msg = """{"server_content":{"input_transcription":{"text":"hi"}}}""";
        Assert.Equal(["hi"], LiveProtocol.ParseLiveInputTranscripts(msg));
        Assert.True(LiveProtocol.HasLiveFinalTranscript(msg));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json{{{")]
    [InlineData("{}")]
    [InlineData("""{"serverContent":{}}""")]
    [InlineData("""{"serverContent":{"inputTranscription":{"text":""}}}""")]
    public void ParseLiveInputTranscripts_NonTranscript_YieldsEmpty(string msg)
    {
        Assert.Empty(LiveProtocol.ParseLiveInputTranscripts(msg));
        Assert.False(LiveProtocol.HasLiveFinalTranscript(msg));
        Assert.False(LiveProtocol.IsLiveSetupComplete(msg));
        Assert.False(LiveProtocol.IsLiveTurnComplete(msg));
    }

    [Theory]
    [InlineData("""{"setupComplete":{}}""")]
    [InlineData("""{"setup_complete":{}}""")]
    public void IsLiveSetupComplete_AckShapes_True(string msg) =>
        Assert.True(LiveProtocol.IsLiveSetupComplete(msg));

    [Theory]
    [InlineData("""{"serverContent":{"turnComplete":true}}""", true)]
    [InlineData("""{"serverContent":{"generationComplete":true}}""", true)]
    [InlineData("""{"server_content":{"turnComplete":true}}""", true)]
    [InlineData("""{"serverContent":{"turnComplete":false}}""", false)]
    public void IsLiveTurnComplete_Matrix(string msg, bool expected) =>
        Assert.Equal(expected, LiveProtocol.IsLiveTurnComplete(msg));

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com/v1beta", "generativelanguage.googleapis.com")]
    [InlineData("https://proxy.example.com/gemini", "proxy.example.com")]
    [InlineData("not a url", "generativelanguage.googleapis.com")]
    [InlineData("", "generativelanguage.googleapis.com")]
    public void LiveHost_Matrix(string baseUrl, string expected) =>
        Assert.Equal(expected, LiveProtocol.LiveHost(baseUrl));

    [Fact]
    public void BuildLiveUri_IsWssWithRpcPathAndEscapedKey()
    {
        var uri = LiveProtocol.BuildLiveUri("generativelanguage.googleapis.com", "a b&c");
        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("generativelanguage.googleapis.com", uri.Host);
        Assert.StartsWith("/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent", uri.AbsolutePath);
        Assert.Contains("key=a%20b%26c", uri.Query);
    }

    [Fact]
    public void WavToMono16kPcm_StripsHeaderFromRecorderWav()
    {
        var wav = AudioRecorder.TestToneWav();
        var pcm = LiveProtocol.WavToMono16kPcm(wav);
        Assert.Equal(wav.Length - 44, pcm.Length);
    }

    [Fact]
    public void WavToMono16kPcm_BadHeader_ThrowsTypedError()
    {
        var ex = Assert.Throws<TranscribeException>(() =>
            LiveProtocol.WavToMono16kPcm(Encoding.ASCII.GetBytes("garbage-bytes-here!!")));
        Assert.Contains("Invalid audio", ex.Message);
    }

    [Fact]
    public void WavToMono16kPcm_WrongFormat_ThrowsTypedError()
    {
        var stereo = AudioRecorder.PcmToWav(new byte[3200], sampleRate: 8000);
        var ex = Assert.Throws<TranscribeException>(() => LiveProtocol.WavToMono16kPcm(stereo));
        Assert.Contains("not supported", ex.Message);
    }

    [Theory]
    [InlineData("HTTP/1.1 401 Unauthorized", "Invalid API key", false)]
    [InlineData("HTTP/1.1 403 Forbidden", "Invalid API key", false)]
    [InlineData("HTTP/1.1 429 Too Many Requests", "Rate limited", true)]
    [InlineData("HTTP/1.1 500 Internal Server Error", "Server error (HTTP 500)", false)]
    [InlineData("garbage", "Network error", false)]
    public void MapHandshakeError_Matrix(string status, string contains, bool rotate)
    {
        var (message, shouldRotate) = LiveProtocol.MapHandshakeError(status);
        Assert.Contains(contains, message);
        Assert.Equal(rotate, shouldRotate);
    }

    [Fact]
    public void MapHandshakeError_NeverEchoesInput()
    {
        var secret = "sk-live-SECRETKEY123";
        var (message, _) = LiveProtocol.MapHandshakeError("HTTP/1.1 500 x " + secret + "?key=" + secret);
        Assert.DoesNotContain("SECRETKEY", message);
    }

    [Fact]
    public void TimingConstants_MatchAndroidContract()
    {
        Assert.Equal(TimeSpan.FromSeconds(75), LiveProtocol.LiveTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), LiveProtocol.FinalizeGrace);
        Assert.Equal(TimeSpan.FromSeconds(6), LiveProtocol.CloseGrace);
        Assert.Equal(TimeSpan.FromSeconds(20), LiveProtocol.SetupAckTimeout);
        Assert.Equal(3200, LiveProtocol.ChunkBytes);
    }
}
