using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using RichardSzalay.MockHttp;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LlmClientRoutingTests
{
    private const string BaseUrl = "https://example.com/v1beta";
    private const string RestModel = "gemini-2.5-flash";
    private const string IxModel = "gemini-3.5-transcribe";
    private const string LiveModel = "gemini-2.5-flash-live";
    private static string RestEndpoint => $"{BaseUrl}/models/{RestModel}:generateContent";
    private static string IxEndpoint => $"{BaseUrl}/interactions";

    private static string RestOk(string text) =>
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text + "\"}]}}]}";

    [Fact]
    public async Task TranscribeAsync_TranscribeModel_PostsToInteractions()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, IxEndpoint)
            .Respond(HttpStatusCode.OK, "application/json", """{"output_text":"ix-hi"}""");
        var rest = mock.When(HttpMethod.Post, RestEndpoint);
        using var llm = new LlmClient(mock.ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1"], BaseUrl, IxModel, customPrompt: "p", smartMode: true);

        Assert.Equal("ix-hi", transcript);
        Assert.Equal(0, used);
        Assert.Equal(0, mock.GetMatchCount(rest));
    }

    [Fact]
    public async Task TranscribeAsync_LiveCandidate_StreamsOverWebSocketWithoutHttp()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Throw(new System.InvalidOperationException("must not call HTTP"));
        var rest = mock.When(HttpMethod.Post, RestEndpoint)
            .Respond(HttpStatusCode.OK, "application/json", RestOk("must-not-use"));
        using var llm = new LlmClient(mock.ToHttpClient());
        var factories = new FakeLiveSocketFactory();
        factories.Build = () =>
        {
            var s = new FakeLiveSocket();
            s.EnqueueText("""{"setupComplete":{}}""");
            s.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"live-hi"}}}""");
            s.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
            return s;
        };
        llm.LiveSocketsForTests = factories;

        var (transcript, used) = await llm.TranscribeAsync(
            AudioRecorder.TestToneWav(), ["k1"], BaseUrl, LiveModel);

        Assert.Equal("live-hi", transcript);
        Assert.Equal(0, used);
        Assert.Equal(0, mock.GetMatchCount(rest));
    }

    [Fact]
    public async Task TranscribeAsync_Rest400LiveOnly_SwitchesToLiveOnce()
    {
        var mock = new MockHttpMessageHandler();
        var rest = mock.When(HttpMethod.Post, RestEndpoint)
            .Respond(HttpStatusCode.BadRequest, "application/json",
                "This model only supports bidiGenerateContent");
        using var llm = new LlmClient(mock.ToHttpClient());
        var factories = new FakeLiveSocketFactory();
        factories.Build = () =>
        {
            var s = new FakeLiveSocket();
            s.EnqueueText("""{"setupComplete":{}}""");
            s.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"switched"}}}""");
            s.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
            return s;
        };
        llm.LiveSocketsForTests = factories;

        var (transcript, used) = await llm.TranscribeAsync(
            AudioRecorder.TestToneWav(), ["k1"], BaseUrl, RestModel);

        Assert.Equal("switched", transcript);
        Assert.Equal(0, used);
        Assert.Equal(1, mock.GetMatchCount(rest));
    }

    [Fact]
    public async Task TranscribeAsync_Rest400DevInstruction_SwitchesToInteractionsSameKey()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, RestEndpoint)
            .WithHeaders("x-goog-api-key", "k2")
            .Respond(HttpStatusCode.BadRequest, "application/json",
                "systemInstruction is not supported by this model");
        mock.When(HttpMethod.Post, IxEndpoint)
            .WithHeaders("x-goog-api-key", "k1")
            .Respond(HttpStatusCode.OK, "application/json", """{"output_text":"wrong-key"}""");
        mock.When(HttpMethod.Post, IxEndpoint)
            .WithHeaders("x-goog-api-key", "k2")
            .Respond(HttpStatusCode.OK, "application/json", """{"output_text":"same-key"}""");
        using var llm = new LlmClient(mock.ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2"], BaseUrl, RestModel, startIndex: 1);

        Assert.Equal("same-key", transcript);
        Assert.Equal(1, used);
    }

    [Fact]
    public async Task TranscribeAsync_Rest400Generic_FailsWithoutSecondTransport()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, RestEndpoint)
            .Respond(HttpStatusCode.BadRequest, "application/json", "quota exploded");
        var ix = mock.When(HttpMethod.Post, IxEndpoint)
            .Respond(HttpStatusCode.OK, "application/json", """{"output_text":"never"}""");
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1"], BaseUrl, RestModel));

        Assert.Contains("Request failed (400)", ex.Message);
        Assert.Equal(0, mock.GetMatchCount(ix));
    }

    [Fact]
    public async Task TranscribeAsync_Rest401AfterTranscribeModelCheck_DoesNotFallback()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, IxEndpoint)
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{}");
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1"], BaseUrl, IxModel));

        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_OrdinaryModelWithSmartMode_StaysRest()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, RestEndpoint)
            .Respond(HttpStatusCode.OK, "application/json", RestOk("still-rest"));
        using var llm = new LlmClient(mock.ToHttpClient());

        var (transcript, _) = await llm.TranscribeAsync(
            new byte[100], ["k1"], BaseUrl, RestModel, smartMode: true);

        Assert.Equal("still-rest", transcript);
    }
}
