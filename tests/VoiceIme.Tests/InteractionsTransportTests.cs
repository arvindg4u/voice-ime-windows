using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RichardSzalay.MockHttp;
using Xunit;

namespace VoiceIme.Tests;

public sealed class InteractionsTransportTests
{
    private const string BaseUrl = "https://example.com/v1beta";
    private const string Model = "gemini-3.5-transcribe";
    private static string Endpoint => $"{BaseUrl}/interactions";

    [Fact]
    public void BuildInteractionsRequestJson_Smart_SetsSmartModeWithoutSystemInstruction()
    {
        var json = InteractionsTranscriptionTransport.BuildInteractionsRequestJson(
            "QUJD", Model, "", smartMode: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("systemInstruction", out _));
        Assert.Equal("smart", root.GetProperty("generation_config")
            .GetProperty("transcription_config").GetProperty("mode").GetString());
        var input = root.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        Assert.Contains("Transcribe the speech exactly", input[0].GetProperty("text").GetString());
        Assert.Equal("audio", input[1].GetProperty("type").GetString());
        Assert.Equal("audio/wav", input[1].GetProperty("mime_type").GetString());
        Assert.Equal("QUJD", input[1].GetProperty("data").GetString());
    }

    [Fact]
    public void BuildInteractionsRequestJson_VerbatimWithPrompt_InlinesPreferences()
    {
        var json = InteractionsTranscriptionTransport.BuildInteractionsRequestJson(
            "QUJD", Model, "Keep Hinglish  ", smartMode: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("verbatim", root.GetProperty("generation_config")
            .GetProperty("transcription_config").GetProperty("mode").GetString());
        Assert.Contains("User preferences:\nKeep Hinglish",
            root.GetProperty("input")[0].GetProperty("text").GetString());
        Assert.False(root.TryGetProperty("systemInstruction", out _));
    }

    [Fact]
    public void ParseInteractionsTranscript_OutputTextWins()
    {
        var payload = """{"output_text":"  hello world  ","steps":[]}""";

        Assert.Equal("hello world",
            InteractionsTranscriptionTransport.ParseInteractionsTranscript(payload));
    }

    [Fact]
    public void ParseInteractionsTranscript_WalksModelOutputStepsOnly()
    {
        var payload = """{"steps":[{"type":"user_input","content":[{"type":"text","text":"echo me"}]},{"type":"model_output","content":[{"type":"text","text":"he"},{"type":"text","text":"llo"}]},{"type":"model_output","content":[{"type":"image","text":"skip me"}]}]}""";

        Assert.Equal("hello",
            InteractionsTranscriptionTransport.ParseInteractionsTranscript(payload));
    }

    [Fact]
    public void ParseInteractionsTranscript_BlankPayload_ThrowsTypedError()
    {
        var ex = Assert.Throws<TranscribeException>(() =>
            InteractionsTranscriptionTransport.ParseInteractionsTranscript("""{"steps":[]}"""));

        Assert.Contains("empty transcript", ex.Message);
    }

    [Fact]
    public void ParseInteractionsTranscript_MalformedJson_ThrowsTypedError()
    {
        var ex = Assert.Throws<TranscribeException>(() =>
            InteractionsTranscriptionTransport.ParseInteractionsTranscript("nope{{{"));

        Assert.Contains("empty transcript", ex.Message);
    }

    private static InteractionsTranscriptionTransport TransportFor(
        MockHttpMessageHandler mock) =>
        new(mock.ToHttpClient(), BaseUrl);

    private static TranscriptionRequest RequestFor(params string[] keys) =>
        new(new byte[100], Model, keys, SmartMode: true);

    [Fact]
    public async Task TranscribeAsync_Success_ReturnsTranscriptIndexAndTransport()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, Endpoint)
            .WithHeaders("x-goog-api-key", "k1")
            .Respond(HttpStatusCode.OK, "application/json", """{"output_text":"hi"}""");

        var result = await TransportFor(mock).TranscribeAsync(RequestFor("k1"), CancellationToken.None);

        Assert.Equal("hi", result.Transcript);
        Assert.Equal(0, result.UsedKeyIndex);
        Assert.Equal(TransportKind.Interactions, result.TransportUsed);
    }

    [Fact]
    public async Task TranscribeAsync_429_RotatesToNextKey()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, Endpoint)
            .WithHeaders("x-goog-api-key", "k1")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", "{}");
        mock.When(HttpMethod.Post, Endpoint)
            .WithHeaders("x-goog-api-key", "k2")
            .Respond(HttpStatusCode.OK, "application/json", """{"output_text":"rotated"}""");

        var result = await TransportFor(mock).TranscribeAsync(RequestFor("k1", "k2"), CancellationToken.None);

        Assert.Equal("rotated", result.Transcript);
        Assert.Equal(1, result.UsedKeyIndex);
    }

    [Fact]
    public async Task TranscribeAsync_401_FailsImmediately()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.Unauthorized, "application/json", "{}");

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            TransportFor(mock).TranscribeAsync(RequestFor("k1"), CancellationToken.None));

        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_CancelledToken_PropagatesOperationCanceled()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.OK, "application/json", """{"output_text":"never"}""");

        // ThrowsAnyAsync: HttpClient surfaces cancellation as
        // TaskCanceledException (an OperationCanceledException subtype) and
        // xUnit ThrowsAsync is exact-match — same pin as RestRegressionTests.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransportFor(mock).TranscribeAsync(
                RequestFor("k1"), new CancellationToken(canceled: true)));
    }
}
