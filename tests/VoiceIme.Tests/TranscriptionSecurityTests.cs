using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using RichardSzalay.MockHttp;
using Xunit;

namespace VoiceIme.Tests;

public sealed class TranscriptionSecurityTests
{
    private const string BaseUrl = "https://example.com/v1beta";
    private const string Model = "gemini-2.5-flash";
    private const string SecretKey = "sk-live-SECRETKEY123";
    private const string SecretPrompt = "my secret hobby is unicycling";

    private static LlmClient ClientFor(MockHttpMessageHandler mock) =>
        new(mock.ToHttpClient());

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task TranscribeAsync_Failures_NeverEchoKeyOrPrompt(HttpStatusCode code)
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(code, "application/json", """{"error":{"message":"nope"}}""");
        using var llm = ClientFor(mock);

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], [SecretKey], BaseUrl, Model, customPrompt: SecretPrompt));

        Assert.DoesNotContain("SECRETKEY", ex.Message);
        Assert.DoesNotContain("unicycling", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_All429_NeverEchoesKeys()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.TooManyRequests, "application/json", "{}");
        using var llm = ClientFor(mock);

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], [SecretKey, "k2"], BaseUrl, Model, customPrompt: SecretPrompt));

        Assert.DoesNotContain("SECRETKEY", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_LiveOnlyBodyWithSecrets_FallbackErrorStaysClean()
    {
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.BadRequest, "application/json",
            "only supports bidiGenerateContent key=" + SecretKey + " " + SecretPrompt);
        using var llm = ClientFor(mock);

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            llm.TranscribeAsync(new byte[100], [SecretKey], BaseUrl, Model, customPrompt: SecretPrompt));

        Assert.DoesNotContain("SECRETKEY", ex.Message);
        Assert.DoesNotContain("unicycling", ex.Message);
    }

    [Fact]
    public void RequestBuilders_ContainPromptByDesign_ButNeverLogIt()
    {
        // The prompt must travel in the body (feature), but no code path may log it:
        // assert the builders carry it while the client performs zero logging.
        Assert.Contains(SecretPrompt,
            LlmClient.BuildGeminiRequestJson("QUJD", SecretPrompt));
        Assert.Contains(SecretPrompt,
            InteractionsTranscriptionTransport.BuildInteractionsRequestJson(
                "QUJD", "gemini-3.5-transcribe", SecretPrompt, smartMode: true));
        var logged = typeof(LlmClient).GetMethods(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name.Contains("Log"));
        Assert.Empty(logged);
    }
}
