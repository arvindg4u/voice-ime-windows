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

    private static MockHttpMessageHandler HandlerForKey(string key, HttpStatusCode code, string body)
    {
        var mock = new MockHttpMessageHandler();
        mock.When(req =>
                req.RequestUri!.ToString() == $"{BaseUrl}/models/{Model}:generateContent" &&
                req.Headers.GetValues("x-goog-api-key").First() == key)
            .Respond(code, "application/json", body);
        return mock;
    }

    [Fact]
    public async Task TranscribeAsync_Success_ReturnsTranscriptAndUsedIndex()
    {
        // Both keys route to OK; startIndex 1 must be used as-is.
        var mock = new MockHttpMessageHandler();
        mock.When(req => req.Headers.GetValues("x-goog-api-key").First() == "k1")
            .Respond(HttpStatusCode.OK, "application/json", OkPayload("from-k1"));
        mock.When(req => req.Headers.GetValues("x-goog-api-key").First() == "k2")
            .Respond(HttpStatusCode.OK, "application/json", OkPayload("from-k2"));
        using var llm = new LlmClient(mock.ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2"], BaseUrl, Model, startIndex: 1);

        Assert.Equal("from-k2", transcript);
        Assert.Equal(1, used);
    }

    [Fact]
    public async Task TranscribeAsync_429_RotatesToNextKey()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(req => req.Headers.GetValues("x-goog-api-key").First() == "k1")
            .Respond(HttpStatusCode.TooManyRequests, "application/json", "{}");
        mock.When(req => req.Headers.GetValues("x-goog-api-key").First() == "k2")
            .Respond(HttpStatusCode.OK, "application/json", OkPayload("rotated"));
        using var llm = new LlmClient(mock.ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2"], BaseUrl, Model);

        Assert.Equal("rotated", transcript);
        Assert.Equal(1, used);
    }

    [Fact]
    public async Task TranscribeAsync_All429_ThrowsRateLimited()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(_ => true)
            .Respond(HttpStatusCode.TooManyRequests, "application/json", "{}");
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1", "k2"], BaseUrl, Model));

        Assert.Contains("Rate limited", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_401_FailsImmediately()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(_ => true)
            .Respond(HttpStatusCode.Unauthorized, "application/json", "{}");
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
