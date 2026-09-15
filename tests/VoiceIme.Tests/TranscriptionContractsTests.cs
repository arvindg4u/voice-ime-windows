using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

public sealed class TranscriptionContractsTests
{
    [Fact]
    public void TranscriptionRequest_With_CopiesWithNewStartIndex()
    {
        var request = new TranscriptionRequest(
            AudioWav: new byte[10], Model: "m", ApiKeys: ["k1", "k2"],
            StartKeyIndex: 0, CustomPrompt: "p", SmartMode: true);

        var moved = request with { StartKeyIndex = 1 };

        Assert.Equal(1, moved.StartKeyIndex);
        Assert.Equal(0, request.StartKeyIndex);
        Assert.Same(request.AudioWav, moved.AudioWav);
        Assert.True(moved.SmartMode);
    }

    [Fact]
    public async Task LiveStub_ThrowsPhase2ErrorWithoutTouchingNetwork()
    {
        ITranscriptionTransport transport = new LiveTranscriptionTransport();
        var request = new TranscriptionRequest(
            AudioWav: new byte[44], Model: "gemini-live-2.5", ApiKeys: ["k1"]);

        var ex = await Assert.ThrowsAsync<TranscribeException>(() =>
            transport.TranscribeAsync(request, CancellationToken.None));

        Assert.Contains("Phase 2", ex.Message);
    }

    [Fact]
    public void TranscriptionResult_CarriesTransportUsed()
    {
        var result = new TranscriptionResult("hi", 1, TransportKind.Rest);

        Assert.Equal("hi", result.Transcript);
        Assert.Equal(1, result.UsedKeyIndex);
        Assert.Equal(TransportKind.Rest, result.TransportUsed);
    }
}
