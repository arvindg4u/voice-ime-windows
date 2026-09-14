using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using RichardSzalay.MockHttp;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LlmClientTests
{
    private const string BaseUrl = "https://example.com/v1beta";
    private const string Model = "gemini-2.5-flash";

    private static string OkPayload(string text) =>
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text + "\"}]}}]}";

    private const string Endpoint = $"{BaseUrl}/models/{Model}:generateContent";

    private static MockHttpMessageHandler KeyedHandler(params (string Key, HttpStatusCode Code, string Body)[] routes)
    {
        var mock = new MockHttpMessageHandler();
        foreach (var (key, code, body) in routes)
        {
            mock.When(HttpMethod.Post, Endpoint)
                .WithHeaders("x-goog-api-key", key)
                .Respond(code, "application/json", body);
        }
        return mock;
    }

    [Fact]
    public async Task TranscribeAsync_Success_ReturnsTranscriptAndUsedIndex()
    {
        // Both keys route to OK; startIndex 1 must be used as-is.
        using var llm = new LlmClient(KeyedHandler(
            ("k1", HttpStatusCode.OK, OkPayload("from-k1")),
            ("k2", HttpStatusCode.OK, OkPayload("from-k2"))).ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2"], BaseUrl, Model, startIndex: 1);

        Assert.Equal("from-k2", transcript);
        Assert.Equal(1, used);
    }

    [Fact]
    public async Task TranscribeAsync_429_RotatesToNextKey()
    {
        using var llm = new LlmClient(KeyedHandler(
            ("k1", HttpStatusCode.TooManyRequests, "{}"),
            ("k2", HttpStatusCode.OK, OkPayload("rotated"))).ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2"], BaseUrl, Model);

        Assert.Equal("rotated", transcript);
        Assert.Equal(1, used);
    }

    [Fact]
    public async Task TranscribeAsync_All429_ThrowsRateLimited()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.TooManyRequests, "application/json", "{}");
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1", "k2"], BaseUrl, Model));

        Assert.Contains("Rate limited", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_401_FailsImmediately()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.Unauthorized, "application/json", "{}");
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["bad"], BaseUrl, Model));

        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_NoKeys_Throws()
    {
        using var llm = new LlmClient(new MockHttpMessageHandler().ToHttpClient());

        await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], [], BaseUrl, Model));
    }

    [Fact]
    public void ParseTranscript_JoinsAllCandidatesParts()
    {
        var payload = """{"candidates":[{"content":{"parts":[{"text":"he"},{"text":"llo"}]}},{"content":{"parts":[{"text":" world"}]}}]}""";

        Assert.Equal("hello world", LlmClient.ParseTranscript(payload));
    }

    [Fact]
    public void BuildGeminiRequestJson_IncludesAudioAndCustomPrompt()
    {
        var json = LlmClient.BuildGeminiRequestJson("QUJD", "Keep Hinglish as-is");

        Assert.Contains("audio/wav", json);
        Assert.Contains("QUJD", json);
        Assert.Contains("User preferences: Keep Hinglish as-is", json);
    }
}
