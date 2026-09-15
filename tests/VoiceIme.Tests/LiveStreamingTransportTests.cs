using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LiveStreamingTransportTests
{
    private const string BaseUrl = "https://example.com/v1beta";

    private static (FakeLiveSocketFactory Factories, LiveSessionGuard Guard) Setup(
        string finalText, LiveSessionOptions? options = null)
    {
        var factories = new FakeLiveSocketFactory();
        factories.Build = () =>
        {
            var s = new FakeLiveSocket();
            s.EnqueueText("""{"setupComplete":{}}""");
            s.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"he"}}}""");
            s.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"hello"}}}""");
            s.EnqueueText("{\"serverContent\":{\"inputTranscription\":{\"text\":\"" + finalText + "\"}}}");
            s.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
            return s;
        };
        return (factories, new LiveSessionGuard());
    }

    [Fact]
    public async Task TranscribeStreaming_PumpChunks_ArriveBeforeFinalize()
    {
        var (factories, guard) = Setup("streamed final");
        var transport = new LiveTranscriptionTransport(BaseUrl, guard, factories);
        using var pump = new LivePcmPump();
        Assert.True(pump.TryEnqueue(new byte[3200]));
        Assert.True(pump.TryEnqueue(new byte[1600]));
        pump.Complete();

        var outcome = await transport.TranscribeStreamingAsync(
            "m-live", true, BaseUrl, "k1", pump, guard, CancellationToken.None);

        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);
        Assert.Equal("streamed final", done.Text);
        var socket = factories.Created[0];
        // setup, activityStart, 2 chunks, activityEnd, audioStreamEnd
        Assert.Equal(6, socket.Sent.Count);
    }

    [Fact]
    public async Task TranscribeStreaming_InterimNeverLeaksIntoDone()
    {
        var (factories, guard) = Setup("done");
        var transport = new LiveTranscriptionTransport(BaseUrl, guard, factories);
        using var pump = new LivePcmPump();
        pump.Complete();

        var outcome = await transport.TranscribeStreamingAsync(
            "m-live", true, BaseUrl, "k1", pump, guard, CancellationToken.None);

        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);
        Assert.Equal("done", done.Text); // no "he"/"hello" prefix
    }

    [Fact]
    public async Task TranscribeStreaming_SetupNeverAcked_FailsTyped()
    {
        var factories = new FakeLiveSocketFactory(); // no scripted messages
        var guard = new LiveSessionGuard();
        var transport = new LiveTranscriptionTransport(BaseUrl, guard, factories);
        using var pump = new LivePcmPump();
        pump.Complete();

        var outcome = await transport.TranscribeStreamingAsync(
            "m-live", true, BaseUrl, "k1", pump, guard, CancellationToken.None,
            new LiveSessionOptions(
                SetupAckTimeout: TimeSpan.FromMilliseconds(50),
                FinalizeGrace: TimeSpan.FromMilliseconds(50),
                LiveTimeout: TimeSpan.FromSeconds(5),
                CloseGrace: TimeSpan.FromMilliseconds(50)));

        Assert.IsType<LiveAttemptOutcome.Fail>(outcome);
    }
}
