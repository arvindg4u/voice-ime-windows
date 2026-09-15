using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LiveSessionTests
{
    private static readonly Uri Uri = new("wss://example.com/ws?key=k");
    private static LiveSessionOptions Fast => new(
        SetupAckTimeout: TimeSpan.FromMilliseconds(50),
        FinalizeGrace: TimeSpan.FromMilliseconds(50),
        LiveTimeout: TimeSpan.FromSeconds(5),
        CloseGrace: TimeSpan.FromMilliseconds(50));

    private static (FakeLiveSocket Socket, LiveSession Session) Setup(
        LiveSessionGuard? guard = null, long? sessionId = null, LiveSessionOptions? options = null)
    {
        guard ??= new LiveSessionGuard();
        var socket = new FakeLiveSocket();
        var session = new LiveSession(socket, "gemini-2.5-flash-live", smartMode: true,
            guard, sessionId ?? guard.Next(), options ?? Fast);
        return (socket, session);
    }

    [Fact]
    public async Task RunAsync_NoAckBeforeTimeout_SendsOnlySetup()
    {
        var (socket, session) = Setup();
        // Never enqueue ACK: session must time out having sent ONLY setup.
        var outcome = await session.RunAsync(new byte[6400], Uri, CancellationToken.None);
        Assert.IsType<LiveAttemptOutcome.Fail>(outcome);
        Assert.Single(socket.Sent);
        Assert.Contains("setup", socket.Sent[0]);
    }

    [Fact]
    public async Task RunAsync_FullDialogue_ReturnsAuthoritativeFinal()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        socket.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"hello wor"}}}""");
        socket.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"hello world"}}}""");
        socket.EnqueueText("""{"serverContent":{"turnComplete":true}}""");

        var outcome = await session.RunAsync(new byte[6400], Uri, CancellationToken.None);

        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);
        Assert.Equal("hello world", done.Text);
        // Order: setup, activityStart, 2 chunks, activityEnd, audioStreamEnd.
        Assert.Equal(6, socket.Sent.Count);
        using var setup = JsonDocument.Parse(socket.Sent[0]);
        Assert.Equal("models/gemini-2.5-flash-live", setup.RootElement.GetProperty("setup").GetProperty("model").GetString());
        Assert.True(socket.CloseRequested && socket.Disposed);
    }

    [Fact]
    public async Task RunAsync_InterimOnlyThenClose_DoesNotFabricateFinal()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        socket.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"heard words"}}}""");
        socket.EnqueueClose(); // server closes with only interim seen

        var outcome = await session.RunAsync(new byte[3200], Uri, CancellationToken.None);

        Assert.IsType<LiveAttemptOutcome.Fail>(outcome); // no fabrication from interim
    }

    [Fact]
    public async Task RunAsync_OverlappingFinals_AccumulateWithoutDuplication()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        socket.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"hello world"}}}""");
        socket.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"world this is"}}}""");
        socket.EnqueueText("""{"serverContent":{"turnComplete":true}}""");

        var outcome = await session.RunAsync(new byte[3200], Uri, CancellationToken.None);

        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);
        Assert.Equal("hello world this is", done.Text);
    }

    [Fact]
    public async Task RunAsync_StaleSessionId_IgnoresAckAndNeverStreams()
    {
        var guard = new LiveSessionGuard();
        var socket = new FakeLiveSocket();
        var stale = new LiveSession(socket, "m", false, guard, guard.Next(), Fast);
        guard.Next(); // newer dictation exists
        socket.EnqueueText("""{"setupComplete":{}}""");

        var outcome = await stale.RunAsync(new byte[3200], Uri, CancellationToken.None);

        Assert.IsType<LiveAttemptOutcome.Fail>(outcome); // ACK ignored → setup timeout path
        Assert.Single(socket.Sent); // never streamed
    }

    [Fact]
    public async Task RunAsync_CancelledToken_PropagatesOperationCanceled()
    {
        var (_, session) = Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.RunAsync(new byte[3200], Uri, cts.Token));
    }

    [Fact]
    public async Task RunAsync_SocketFailure_MapsToSafeFailAndDisposes()
    {
        var guard = new LiveSessionGuard();
        var failing = new FakeLiveSocket();
        failing.EnqueueFailure(new LiveSocketException("Network error — check connection"));
        var session = new LiveSession(failing, "m", false, guard, guard.Next(), Fast);

        var outcome = await session.RunAsync(new byte[3200], Uri, CancellationToken.None);

        var fail = Assert.IsType<LiveAttemptOutcome.Fail>(outcome);
        Assert.Contains("Network error", fail.Message);
        Assert.True(failing.CloseRequested && failing.Disposed);
    }
}
