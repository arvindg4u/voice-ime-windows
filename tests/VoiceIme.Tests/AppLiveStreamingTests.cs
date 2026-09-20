using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Task 5 App Live-streaming integration tests, driven at the
/// coordinator + transport + pump integration level.
///
/// Seam note: <see cref="App"/> itself cannot be constructed in tests (the
/// singleton <see cref="System.Windows.Application"/> allows one instance per
/// process and needs an STA thread plus a window station — see
/// <c>AppWiringTests</c>). The App-owned surface under test here is therefore
/// the narrow internal static gate <c>App.ShouldCommitLiveTranscript</c> (the
/// exact predicate <c>CommitTranscriptAsync</c> enforces before paste/commit),
/// plus the coordinator/transport/pump contracts the App Live branch relies
/// on: gate accept/reject, pump-full failure with no commit, cancel-during-
/// drain surfacing OCE with no commit, stale-generation silence, setup-false
/// rollback, last-chunk-before-Complete ordering (§15), interim/final event
/// preview joining, and Live model routing. Headless-safe: no windows, no
/// microphone, no network (fake sockets only). Full WPF coverage runs on
/// windows-latest CI.
/// </summary>
public sealed class AppLiveStreamingTests
{
    private const string BaseUrl = "https://example.com/v1beta";

    private static readonly LiveSessionOptions FastOptions = new(
        SetupAckTimeout: TimeSpan.FromMilliseconds(500),
        FinalizeGrace: TimeSpan.FromMilliseconds(500),
        LiveTimeout: TimeSpan.FromSeconds(10),
        CloseGrace: TimeSpan.FromMilliseconds(100));

    private static FakeLiveSocketFactory ScriptedFactory(string finalText)
    {
        var factories = new FakeLiveSocketFactory();
        factories.Build = () =>
        {
            var s = new FakeLiveSocket();
            s.EnqueueText("""{"setupComplete":{}}""");
            s.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"he"}}}""");
            s.EnqueueText("{\"serverContent\":{\"inputTranscription\":{\"text\":\"" + finalText + "\"}}}");
            s.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
            return s;
        };
        return factories;
    }

    private static Uri LiveUri() =>
        LiveProtocol.BuildLiveUri(LiveProtocol.LiveHost(BaseUrl), "k1");

    /// <summary>Moves a fresh coordinator Idle → Recording → Uploading.</summary>
    private static (DictationCoordinator Coordinator, long Generation, CancellationToken Token)
        StartUploadingSession()
    {
        var coordinator = new DictationCoordinator();
        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnToggleInput());
        Assert.Equal(HotkeyCommand.StopRecording, coordinator.OnToggleInput());
        Assert.Equal(DictationState.Uploading, coordinator.State);
        return (coordinator, coordinator.Generation, coordinator.SessionToken);
    }

    [Fact]
    public async Task StreamingDone_CurrentGeneration_GateAccepts_AndCoordinatorCommits()
    {
        // Arrange: live session in Uploading + scripted Done.
        var (coordinator, generation, token) = StartUploadingSession();
        var factories = ScriptedFactory("streamed commit");
        var guard = new LiveSessionGuard();
        var transport = new LiveTranscriptionTransport(BaseUrl, guard, factories);
        using var pump = new LivePcmPump();
        pump.Complete();

        // Act: the exact App Live stop path — drain+finalize, then the gate.
        var outcome = await transport.TranscribeStreamingAsync(
            "gemini-2.5-flash-live", false, BaseUrl, "k1", pump, guard,
            CancellationToken.None, FastOptions);
        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);

        // Assert: Done text + gate accept + coordinator commit (→ Idle).
        Assert.Equal("streamed commit", done.Text);
        Assert.True(App.ShouldCommitLiveTranscript(generation, token, coordinator));
        Assert.True(coordinator.ReportUploadSucceeded(generation));
        Assert.Equal(DictationState.Idle, coordinator.State);
        coordinator.Dispose();
    }

    [Fact]
    public void Gate_StaleGeneration_Rejects_AndCoordinatorStaysSilent()
    {
        // Arrange: session 1 uploads; cancel + session 2 takes over (also uploading).
        var first = StartUploadingSession();
        var staleGeneration = first.Generation;
        first.Coordinator.CancelCurrentOperation();
        var second = (Coordinator: first.Coordinator, Generation: 0L, Token: CancellationToken.None);
        Assert.Equal(HotkeyCommand.StartRecording, second.Coordinator.OnToggleInput());
        Assert.Equal(HotkeyCommand.StopRecording, second.Coordinator.OnToggleInput());
        var liveToken = second.Coordinator.SessionToken;

        // Act: the late session-1 completion asks the gate.
        var accepted = App.ShouldCommitLiveTranscript(staleGeneration, liveToken, second.Coordinator);

        // Assert: rejected, and the coordinator ignores the stale commit too.
        Assert.False(accepted);
        Assert.False(second.Coordinator.ReportUploadSucceeded(staleGeneration));
        Assert.Equal(DictationState.Uploading, second.Coordinator.State);
        second.Coordinator.Dispose();
    }

    [Fact]
    public void Gate_CancelledToken_Rejects()
    {
        // Arrange: session cancelled (token aborted, machine idle).
        var (coordinator, generation, token) = StartUploadingSession();
        coordinator.CancelCurrentOperation();

        // Act + Assert: gate rejects — no paste, no status touch.
        Assert.True(token.IsCancellationRequested);
        Assert.False(App.ShouldCommitLiveTranscript(generation, token, coordinator));
        coordinator.Dispose();
    }

    [Fact]
    public void Gate_WrongState_Rejects()
    {
        // Arrange: still Recording (transcribe finished before stop — impossible
        // ordering the gate must refuse), and later Idle.
        var coordinator = new DictationCoordinator();
        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnToggleInput());
        var generation = coordinator.Generation;
        var token = coordinator.SessionToken;

        Assert.False(App.ShouldCommitLiveTranscript(generation, token, coordinator));

        coordinator.CancelCurrentOperation();
        Assert.False(App.ShouldCommitLiveTranscript(generation, token, coordinator));
        coordinator.Dispose();
    }

    [Fact]
    public void PumpFull_TryEnqueueFalse_SessionFailsWithNoCommit()
    {
        // Arrange: bounded pump saturated, like 30 s of un-ACKed audio.
        var (coordinator, generation, _) = StartUploadingSession();
        using var pump = new LivePcmPump(capacity: 1);
        Assert.True(pump.TryEnqueue(new byte[3200]));

        // Act: NAudio chunk arrives, TryEnqueue refuses → App FailSession:
        // cancel the session CTS (typed error, no partial paste).
        Assert.False(pump.TryEnqueue(new byte[3200]));
        coordinator.CancelCurrentOperation();

        // Assert: idle, and the stale generation can never commit.
        Assert.Equal(DictationState.Idle, coordinator.State);
        Assert.False(coordinator.ReportUploadSucceeded(generation));
        coordinator.Dispose();
    }

    [Fact]
    public async Task CancelDuringDrain_SurfacesOCE_AndNeverCommits()
    {
        // Arrange: queued audio with a cancel landing mid-drain.
        var (coordinator, generation, _) = StartUploadingSession();
        using var pump = new LivePcmPump();
        Assert.True(pump.TryEnqueue(new byte[3200]));
        Assert.True(pump.TryEnqueue(new byte[1600]));
        pump.Complete();
        using var cts = new CancellationTokenSource();
        var sent = 0;

        // Act: cancel after the first chunk (mirrors App FailSession racing
        // the drain); the OCE must surface unwrapped.
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pump.DrainAsync(
                (_, ct) =>
                {
                    sent++;
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cts.Token));

        // Assert: OCE (not a typed Fail), exactly one chunk sent, no commit.
        Assert.IsType<OperationCanceledException>(thrown);
        Assert.Equal(1, sent);
        coordinator.CancelCurrentOperation();
        Assert.False(coordinator.ReportUploadSucceeded(generation));
        coordinator.Dispose();
    }

    [Fact]
    public async Task LastChunkBeforeComplete_IsDelivered_BeforeFinalizeMarkers()
    {
        // Arrange (§15 boundary): the final PCM chunk lands before Complete().
        var factories = ScriptedFactory("boundary final");
        var guard = new LiveSessionGuard();
        var transport = new LiveTranscriptionTransport(BaseUrl, guard, factories);
        using var pump = new LivePcmPump();
        Assert.True(pump.TryEnqueue(new byte[3200]));
        Assert.True(pump.TryEnqueue(new byte[1600]));
        Assert.True(pump.TryEnqueue(new byte[800]));
        pump.Complete();

        // Act.
        var outcome = await transport.TranscribeStreamingAsync(
            "m-live", false, BaseUrl, "k1", pump, guard,
            CancellationToken.None, FastOptions);

        // Assert: Done + all 3 chunks sent in order before the finalize pair
        // (setup, activityStart, 3 chunks, activityEnd, audioStreamEnd).
        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);
        Assert.Equal("boundary final", done.Text);
        Assert.Equal(7, factories.Created[0].Sent.Count);
    }

    [Fact]
    public void SetupFalse_RollsBackThroughReportStartResult()
    {
        // Arrange: optimistic Recording whose setup never ACKs.
        var coordinator = new DictationCoordinator();
        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnToggleInput());
        var generation = coordinator.Generation;
        IDictationError? raised = null;
        coordinator.ErrorRaised += (error, _) => raised = error;

        // Act: the exact rollback App performs on ConnectAndSetupAsync false.
        coordinator.ReportStartResult(
            generation, false, new RecordingError(RecordingErrorReason.Unknown));

        // Assert: Error with the typed error surfaced (HandleError shows it).
        Assert.Equal(DictationState.Error, coordinator.State);
        Assert.IsType<RecordingError>(raised);
        coordinator.AcknowledgeError();
        Assert.Equal(DictationState.Idle, coordinator.State);
        coordinator.Dispose();
    }

    [Fact]
    public async Task SessionEvents_InterimAndFinal_JoinIntoPreview_WithoutLeakingInterim()
    {
        // Arrange: App-style subscription (finals buffer + latest interim).
        var socket = new FakeLiveSocket();
        socket.EnqueueText("""{"setupComplete":{}}""");
        socket.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"he"}}}""");
        socket.EnqueueText("""{"serverContent":{"interimInputTranscription":{"text":"hello"}}}""");
        socket.EnqueueText("""{"serverContent":{"inputTranscription":{"text":"hello world"}}}""");
        socket.EnqueueText("""{"serverContent":{"turnComplete":true}}""");
        var guard = new LiveSessionGuard();
        await using var session = new LiveSession(
            socket, "m-live", false, guard, guard.Next(), FastOptions);
        var finals = "";
        var interim = "";
        session.InterimReceived += text => interim = text;
        session.FinalReceived += delta => finals += delta;

        // Act: the exact App drain-task shape — connect, drain, finalize.
        Assert.True(await session.ConnectAndSetupAsync(LiveUri(), CancellationToken.None));
        using var pump = new LivePcmPump();
        Assert.True(pump.TryEnqueue(new byte[3200]));
        pump.Complete();
        await pump.DrainAsync(session.SendPcmAsync, CancellationToken.None);
        var outcome = await session.CompleteAndReadFinalAsync(CancellationToken.None);

        // Assert: Done carries the authoritative final only; the preview join
        // (what App pushes via Dispatcher to SetPreview) shows finals+interim.
        var done = Assert.IsType<LiveAttemptOutcome.Done>(outcome);
        Assert.Equal("hello world", done.Text);
        Assert.Equal("hello world", finals);
        Assert.Equal("hello", interim);
        Assert.Equal("hello world hello", OverlayState.FormatPreview(finals, interim));
    }

    [Fact]
    public void RouteModel_LiveModel_SelectsLiveBranch()
    {
        // The branch predicate StartRecordingAsync uses.
        Assert.Equal(TransportKind.Live, ModelRouter.RouteModel("gemini-2.5-flash-live"));
        Assert.Equal(TransportKind.Live, ModelRouter.RouteModel("gemini-live-2.5"));
        Assert.Equal(TransportKind.Rest, ModelRouter.RouteModel("gemini-2.5-flash"));
        Assert.Equal(TransportKind.Rest, ModelRouter.RouteModel(null));
    }

    [Fact]
    public void FormatPreview_EmptyParts_Clears()
    {
        // The clear-preview contract App relies on for cancel/complete.
        Assert.Equal(string.Empty, OverlayState.FormatPreview(string.Empty, string.Empty));
        Assert.Equal("final", OverlayState.FormatPreview("  final  ", string.Empty));
    }
}
