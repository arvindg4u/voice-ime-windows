using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LiveSessionStreamingTests
{
    private static readonly Uri Uri = new("wss://example.com/ws?key=k");
    private static LiveSessionOptions Fast => new(
        SetupAckTimeout: TimeSpan.FromMilliseconds(50),
        FinalizeGrace: TimeSpan.FromMilliseconds(50),
        LiveTimeout: TimeSpan.FromSeconds(5),
        CloseGrace: TimeSpan.FromMilliseconds(50));

    private static (FakeLiveSocket Socket, LiveSession Session) Setup()
    {
        var guard = new LiveSessionGuard();
        var socket = new FakeLiveSocket();
        return (socket, new LiveSession(socket, "m-live", smartMode: true, guard, guard.Next(), Fast));
    }

    [Fact]
    public async Task ConnectAndSetup_AckBeforeTimeout_ReturnsTrueWithoutStreaming()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        Assert.True(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));
        Assert.Single(socket.Sent); // setup only
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_NoAck_ReturnsFalseAndNeverStreams()
    {
        var (socket, session) = Setup();
        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));
        Assert.Single(socket.Sent);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SendPcm_StreamsActivityStartOnceThenChunksInOrder()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        Assert.True(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));
        await session.SendPcmAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await session.SendPcmAsync(new byte[] { 3, 4 }, CancellationToken.None);

        Assert.Equal(4, socket.Sent.Count); // setup, activityStart, chunk, chunk
        var start = JsonDocument.Parse(socket.Sent[1]).RootElement;
        Assert.Equal("{}", start.GetProperty("realtimeInput").GetProperty("activityStart").GetRawText());
        var audio = JsonDocument.Parse(socket.Sent[2]).RootElement
            .GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2]), audio.GetProperty("data").GetString());
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Complete_FinalBeforeTurnComplete_ReturnsDone()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        Assert.True(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));
        await session.SendPcmAsync(new byte[3200], CancellationToken.None);
        socket.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"streamed hi"}}}""");
        socket.EnqueueText("""{"serverContent":{"turnComplete":true}}""");

        var outcome = await session.CompleteAndReadFinalAsync(CancellationToken.None);

        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);
        Assert.Equal("streamed hi", done.Text);
        // Tail markers exactly once each, in order.
        var tail = socket.Sent.Skip(socket.Sent.Count - 2).ToList();
        Assert.True(JsonDocument.Parse(tail[0]).RootElement.GetProperty("realtimeInput")
            .TryGetProperty("activityEnd", out _));
        Assert.True(JsonDocument.Parse(tail[1]).RootElement.GetProperty("realtimeInput")
            .GetProperty("audioStreamEnd").GetBoolean());
        Assert.True(socket.CloseRequested && socket.Disposed);
    }

    [Fact]
    public async Task Complete_TurnCompleteWithoutFinal_FailsTyped()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        Assert.True(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));
        socket.EnqueueText("""{"serverContent":{"turnComplete":true}}""");

        var outcome = await session.CompleteAndReadFinalAsync(CancellationToken.None);

        Assert.IsType<LiveAttemptOutcome.Fail>(outcome);
    }

    [Fact]
    public async Task SendPcm_AfterComplete_IsNoOp()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        Assert.True(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));
        socket.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"x"}}}""");
        socket.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
        await session.CompleteAndReadFinalAsync(CancellationToken.None);
        var count = socket.Sent.Count;

        await session.SendPcmAsync(new byte[] { 9 }, CancellationToken.None);

        Assert.Equal(count, socket.Sent.Count);
    }

    [Fact]
    public async Task Streaming_CancelledToken_PropagatesOperationCanceled()
    {
        var (_, session) = Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ConnectAndSetupAsync(Uri, cts.Token));
    }

    [Fact]
    public async Task Streaming_StaleGuard_IgnoresAck()
    {
        var guard = new LiveSessionGuard();
        var socket = new FakeLiveSocket();
        var stale = new LiveSession(socket, "m", false, guard, guard.Next(), Fast);
        guard.Next();
        socket.EnqueueText("""{"setupComplete":{}}""");

        Assert.False(await stale.ConnectAndSetupAsync(Uri, CancellationToken.None));
        Assert.Single(socket.Sent);
    }
}
