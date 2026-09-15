using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LiveTransportTests
{
    private const string BaseUrl = "https://example.com/v1beta";
    private const string LiveModel = "gemini-2.5-flash-live";
    private const string SecretKey = "sk-live-SECRETKEY123";

    private static (FakeLiveSocketFactory Factories, LiveTranscriptionTransport Transport) Setup()
    {
        var factories = new FakeLiveSocketFactory();
        return (factories, new LiveTranscriptionTransport(BaseUrl, new LiveSessionGuard(), factories));
    }

    private static TranscriptionRequest Request(params string[] keys) =>
        new(AudioRecorder.TestToneWav(), LiveModel, keys, SmartMode: true);

    private static void ScriptFinal(FakeLiveSocketFactory factories, string text)
    {
        factories.Build = () =>
        {
            var s = new FakeLiveSocket();
            s.EnqueueText("""{"setupComplete":{}}""");
            s.EnqueueText("{\"serverContent\":{\"inputTranscription\":{\"text\":\"" + text + "\"}}}");
            s.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
            return s;
        };
    }

    [Fact]
    public async Task TranscribeAsync_ScriptedDialogue_ReturnsLiveResult()
    {
        var (factories, transport) = Setup();
        ScriptFinal(factories, "live hello");
        var result = await transport.TranscribeAsync(Request("k1"), CancellationToken.None);
        Assert.Equal("live hello", result.Transcript);
        Assert.Equal(0, result.UsedKeyIndex);
        Assert.Equal(TransportKind.Live, result.TransportUsed);
        var uri = factories.Created[0].ConnectedTo[0];
        Assert.Equal("wss", uri.Scheme);
    }

    [Fact]
    public async Task TranscribeAsync_429OnFirstKey_RotatesToSecond()
    {
        var (factories, transport) = Setup();
        var first = true;
        factories.Build = () =>
        {
            var s = new FakeLiveSocket();
            if (first)
            {
                first = false;
                s.EnqueueFailure(new LiveSocketException("Rate limited — retry"));
            }
            else
            {
                s.EnqueueText("""{"setupComplete":{}}""");
                s.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"rotated"}}}""");
                s.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
            }
            return s;
        };
        var result = await transport.TranscribeAsync(Request("k1", "k2"), CancellationToken.None);
        Assert.Equal("rotated", result.Transcript);
        Assert.Equal(1, result.UsedKeyIndex);
    }

    [Fact]
    public async Task TranscribeAsync_NoKeys_ThrowsTypedError()
    {
        var (_, transport) = Setup();
        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            transport.TranscribeAsync(Request("  "), CancellationToken.None));
        Assert.Contains("No API key", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_InvalidWav_ThrowsBeforeConnecting()
    {
        var (factories, transport) = Setup();
        var bad = new TranscriptionRequest(new byte[] { 1, 2, 3 }, LiveModel, ["k1"]);
        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            transport.TranscribeAsync(bad, CancellationToken.None));
        Assert.Contains("Invalid audio", ex.Message);
        Assert.Empty(factories.Created);
    }

    [Fact]
    public async Task TranscribeAsync_FailureWithSecretKey_NeverEchoesKey()
    {
        var (factories, transport) = Setup();
        factories.Build = () =>
        {
            var s = new FakeLiveSocket();
            s.EnqueueFailure(new LiveSocketException("Network error — check connection"));
            return s;
        };
        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            transport.TranscribeAsync(Request(SecretKey), CancellationToken.None));
        Assert.DoesNotContain("SECRETKEY", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_CancelledToken_PropagatesOperationCanceled()
    {
        var (_, transport) = Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.TranscribeAsync(Request("k1"), cts.Token));
    }

    [Fact]
    public void Transport_ToString_DoesNotLeak()
    {
        var (_, transport) = Setup();
        Assert.DoesNotContain("wss://", transport.ToString());
    }
}
