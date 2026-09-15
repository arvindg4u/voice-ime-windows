using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RichardSzalay.MockHttp;
using Xunit;

namespace VoiceIme.Tests;

public sealed class RestRegressionTests
{
    private const string BaseUrl = "https://example.com/v1beta";
    private const string Model = "gemini-2.5-flash";
    private static string Endpoint => $"{BaseUrl}/models/{Model}:generateContent";

    private static string OkPayload(string text) =>
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text + "\"}]}}]}";

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
    public async Task TranscribeAsync_StartIndexBeyondRange_WrapsAround()
    {
        using var llm = new LlmClient(KeyedHandler(
            ("k1", HttpStatusCode.OK, OkPayload("a")),
            ("k2", HttpStatusCode.OK, OkPayload("b")),
            ("k3", HttpStatusCode.OK, OkPayload("c"))).ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2", "k3"], BaseUrl, Model, startIndex: 5);

        Assert.Equal("c", transcript);
        Assert.Equal(2, used);
    }

    [Fact]
    public async Task TranscribeAsync_Rotation_WrapsFromLastKeyToFirst()
    {
        using var llm = new LlmClient(KeyedHandler(
            ("k3", HttpStatusCode.TooManyRequests, "{}"),
            ("k1", HttpStatusCode.OK, OkPayload("wrapped"))).ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2", "k3"], BaseUrl, Model, startIndex: 2);

        Assert.Equal("wrapped", transcript);
        Assert.Equal(0, used);
    }

    [Fact]
    public async Task TranscribeAsync_FirstKeySuccess_StopsImmediately()
    {
        using var llm = new LlmClient(KeyedHandler(
            ("k1", HttpStatusCode.OK, OkPayload("first"))).ToHttpClient());

        var (transcript, used) = await llm.TranscribeAsync(
            new byte[100], ["k1", "k2"], BaseUrl, Model);

        Assert.Equal("first", transcript);
        Assert.Equal(0, used);
    }

    [Fact]
    public async Task TranscribeAsync_CancelledToken_PropagatesOperationCanceled()
    {
        using var llm = new LlmClient(KeyedHandler(
            ("k1", HttpStatusCode.OK, OkPayload("never"))).ToHttpClient());

        // Note: HttpClient surfaces cancellation as TaskCanceledException (an
        // OperationCanceledException subtype), so ThrowsAnyAsync pins the
        // contract that matters — cancel propagates unwrapped, never as
        // TranscribeException — without coupling to the concrete subtype.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1"], BaseUrl, Model,
                ct: new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task TranscribeAsync_NetworkFailure_ThrowsNetworkError()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, Endpoint)
            .Throw(new HttpRequestException("boom"));
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1"], BaseUrl, Model));

        Assert.Contains("Network error", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_MalformedResponse_ThrowsTypedError()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.OK, "application/json", "not json{{{");
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1"], BaseUrl, Model));

        Assert.Contains("understand the response", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyCandidates_ThrowsEmptyTranscript()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.OK, "application/json", "{\"candidates\":[]}");
        using var llm = new LlmClient(mock.ToHttpClient());

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], ["k1"], BaseUrl, Model));

        Assert.Contains("empty transcript", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_Success_ScrubsAudioBytes()
    {
        using var llm = new LlmClient(KeyedHandler(
            ("k1", HttpStatusCode.OK, OkPayload("hi"))).ToHttpClient());
        var wav = Enumerable.Repeat((byte)7, 100).ToArray();

        await llm.TranscribeAsync(wav, ["k1"], BaseUrl, Model);

        Assert.All(wav, b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildGeminiRequestJson_BlankPrompt_OmitsSystemInstruction()
    {
        Assert.DoesNotContain("systemInstruction", LlmClient.BuildGeminiRequestJson("QUJD", ""));
        Assert.DoesNotContain("systemInstruction", LlmClient.BuildGeminiRequestJson("QUJD", "   "));
    }

    [Fact]
    public void BuildGeminiRequestJson_StructureMatchesFrozenContract()
    {
        using var doc = JsonDocument.Parse(LlmClient.BuildGeminiRequestJson("QUJD", "Keep Hinglish"));
        var root = doc.RootElement;
        var parts = root.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("audio/wav", parts[0].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal("QUJD", parts[0].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("Transcribe this audio. Respond with the transcript only.", parts[1].GetProperty("text").GetString());
        Assert.Contains("User preferences: Keep Hinglish",
            root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }
}
