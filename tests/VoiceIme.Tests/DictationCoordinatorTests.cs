using System;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Synthetic-clock tests for <see cref="DictationCoordinator"/>: debounce
/// coalescing, hold vs toggle, 50 ms release-grace, generation staleness, and
/// cancel-mid-upload draining to Idle. No real hotkeys, audio, or HTTP.
/// </summary>
public sealed class DictationCoordinatorTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime Get() => Now;

        public void Advance(TimeSpan delta) => Now += delta;

        public void AdvanceMs(double ms) => Advance(TimeSpan.FromMilliseconds(ms));
    }

    private static (DictationCoordinator Coordinator, Clock Clock) Create(
        string mode = ActivationModes.Toggle)
    {
        var clock = new Clock();
        var coordinator = new DictationCoordinator(clock.Get)
        {
            ActivationMode = mode,
        };
        return (coordinator, clock);
    }

    private static byte[] RealWav() => new byte[100];

    [Fact]
    public void Constants_MatchHandyPortContract()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(30), DictationCoordinator.DebounceInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(50), DictationCoordinator.ReleaseGrace);
        Assert.Equal(25, DictationCoordinator.CancelPollIntervalMs);
    }

    [Fact]
    public void Toggle_FirstPress_StartsRecording()
    {
        var (coordinator, _) = Create();

        var command = coordinator.OnHotkeyDown();

        Assert.Equal(HotkeyCommand.StartRecording, command);
        Assert.Equal(DictationState.Recording, coordinator.State);
        Assert.Equal(1, coordinator.Generation);
    }

    [Fact]
    public void Debounce_SecondPressWithin30ms_IsCoalesced()
    {
        var (coordinator, clock) = Create();
        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnHotkeyDown());

        clock.AdvanceMs(10);
        var command = coordinator.OnHotkeyDown();

        Assert.Equal(HotkeyCommand.None, command);
        Assert.Equal(DictationState.Recording, coordinator.State);
    }

    [Fact]
    public void Toggle_SecondPressAfterDebounce_StopsRecording()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();

        clock.AdvanceMs(100);
        var command = coordinator.OnHotkeyDown();

        Assert.Equal(HotkeyCommand.StopRecording, command);
        Assert.Equal(DictationState.Uploading, coordinator.State);
    }

    [Fact]
    public void Toggle_FullCycle_ReturnsToIdle()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        var generation = coordinator.Generation;

        Assert.Equal(AudioCaptureOutcome.Uploading, coordinator.ReportAudioCaptured(RealWav()));
        Assert.True(coordinator.ReportUploadSucceeded(generation));
        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void HoldOrToggle_ReleaseAfterHold_StopsRecording()
    {
        var (coordinator, clock) = Create(ActivationModes.HoldOrToggle);
        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnHotkeyDown());

        clock.AdvanceMs(400);
        Assert.Equal(HotkeyCommand.None, coordinator.OnHotkeyUp());
        Assert.True(coordinator.HasPendingRelease);

        clock.AdvanceMs(60);
        var command = coordinator.Tick();

        Assert.Equal(HotkeyCommand.StopRecording, command);
        Assert.Equal(DictationState.Uploading, coordinator.State);
    }

    [Fact]
    public void HoldOrToggle_FastTap_LocksOnInsteadOfStopping()
    {
        var (coordinator, clock) = Create(ActivationModes.HoldOrToggle);
        coordinator.OnHotkeyDown();

        clock.AdvanceMs(100);
        coordinator.OnHotkeyUp();
        clock.AdvanceMs(60);
        var command = coordinator.Tick();

        Assert.Equal(HotkeyCommand.None, command);
        Assert.Equal(DictationState.Recording, coordinator.State);
        Assert.False(coordinator.HasPendingRelease);

        // The locked session now ends on the next press, like toggle.
        clock.AdvanceMs(100);
        Assert.Equal(HotkeyCommand.StopRecording, coordinator.OnHotkeyDown());
    }

    [Fact]
    public void HoldOrToggle_RepeatPressInsideGrace_CancelsDeferredRelease()
    {
        var (coordinator, clock) = Create(ActivationModes.HoldOrToggle);
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(400);
        coordinator.OnHotkeyUp();

        clock.AdvanceMs(10);
        var command = coordinator.OnHotkeyDown();

        Assert.Equal(HotkeyCommand.None, command);
        Assert.False(coordinator.HasPendingRelease);

        clock.AdvanceMs(100);
        Assert.Equal(HotkeyCommand.None, coordinator.Tick());
        Assert.Equal(DictationState.Recording, coordinator.State);
    }

    [Fact]
    public void PushToTalk_ShortRelease_AlwaysStops()
    {
        var (coordinator, clock) = Create(ActivationModes.PushToTalk);
        coordinator.OnHotkeyDown();

        clock.AdvanceMs(50);
        coordinator.OnHotkeyUp();
        clock.AdvanceMs(60);

        Assert.Equal(HotkeyCommand.StopRecording, coordinator.Tick());
        Assert.Equal(DictationState.Uploading, coordinator.State);
    }

    [Fact]
    public void Toggle_KeyUp_IsIgnored()
    {
        var (coordinator, _) = Create();
        coordinator.OnHotkeyDown();

        Assert.Equal(HotkeyCommand.None, coordinator.OnHotkeyUp());
        Assert.Equal(DictationState.Recording, coordinator.State);
    }

    [Fact]
    public void OptimisticStart_Failure_RollsBackWithTypedError()
    {
        var (coordinator, _) = Create();
        IDictationError? raised = null;
        coordinator.ErrorRaised += (error, _) => raised = error;
        coordinator.OnHotkeyDown();

        coordinator.ReportStartResult(
            coordinator.Generation,
            false,
            new RecordingError(RecordingErrorReason.MicDenied));

        Assert.Equal(DictationState.Error, coordinator.State);
        Assert.IsType<RecordingError>(raised);

        coordinator.AcknowledgeError();
        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void OptimisticStart_Success_KeepsRecording()
    {
        var (coordinator, _) = Create();
        var raised = 0;
        coordinator.ErrorRaised += (_, _) => raised++;
        coordinator.OnHotkeyDown();

        coordinator.ReportStartResult(coordinator.Generation, true);

        Assert.Equal(DictationState.Recording, coordinator.State);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Error_PressIsIgnored_UntilAcknowledged()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        coordinator.ReportStartResult(
            coordinator.Generation, false, new RecordingError(RecordingErrorReason.Unknown));

        clock.AdvanceMs(100);
        Assert.Equal(HotkeyCommand.None, coordinator.OnHotkeyDown());

        coordinator.AcknowledgeError();
        clock.AdvanceMs(100);
        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnHotkeyDown());
    }

    [Fact]
    public void Generation_StaleStartFailure_DoesNotTouchNewSession()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        var staleGeneration = coordinator.Generation;

        // A cancel drains the first session, then a new session starts live.
        coordinator.CancelCurrentOperation();
        clock.AdvanceMs(100);
        var raised = 0;
        coordinator.ErrorRaised += (_, _) => raised++;
        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnHotkeyDown());
        Assert.Equal(staleGeneration + 1, coordinator.Generation);

        // The late start-failure from session N arrives during session N+1:
        // ignored, and the new session keeps recording.
        coordinator.ReportStartResult(
            staleGeneration, false, new RecordingError(RecordingErrorReason.MicDenied));

        Assert.Equal(0, raised);
        Assert.Equal(DictationState.Recording, coordinator.State);
        Assert.Equal(staleGeneration + 1, coordinator.Generation);
    }

    [Fact]
    public void Generation_StaleUploadCompletion_DoesNotClobberNewSession()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        var staleGeneration = coordinator.Generation;
        Assert.Equal(AudioCaptureOutcome.Uploading, coordinator.ReportAudioCaptured(RealWav()));

        coordinator.CancelCurrentOperation();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        Assert.Equal(staleGeneration + 1, coordinator.Generation);

        Assert.False(coordinator.ReportUploadSucceeded(staleGeneration));
        Assert.Equal(DictationState.Recording, coordinator.State);
    }

    [Fact]
    public void Generation_StaleUploadFailure_DoesNotResurfaceError()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        var staleGeneration = coordinator.Generation;

        coordinator.CancelCurrentOperation();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        var raised = 0;
        coordinator.ErrorRaised += (_, _) => raised++;

        var accepted = coordinator.ReportUploadFailed(
            staleGeneration,
            new TranscriptionError("late failure"));

        Assert.False(accepted);
        Assert.Equal(0, raised);
        Assert.Equal(DictationState.Recording, coordinator.State);
    }

    [Fact]
    public void Cancel_MidUpload_DrainsToIdleAndCancelsToken()
    {
        var (coordinator, clock) = Create();
        var cancelled = 0;
        coordinator.OperationCancelled += () => cancelled++;
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        var generation = coordinator.Generation;
        Assert.Equal(AudioCaptureOutcome.Uploading, coordinator.ReportAudioCaptured(RealWav()));
        var token = coordinator.SessionToken;
        Assert.False(token.IsCancellationRequested);

        coordinator.CancelCurrentOperation();

        Assert.Equal(DictationState.Idle, coordinator.State);
        Assert.Equal(1, cancelled);
        Assert.True(token.IsCancellationRequested);
        Assert.False(coordinator.ReportUploadSucceeded(generation));
    }

    [Fact]
    public void Cancel_FromRecording_DrainsToIdle()
    {
        var (coordinator, _) = Create();
        coordinator.OnHotkeyDown();

        coordinator.CancelCurrentOperation();

        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void Cancel_FromIdle_StillRaisesOperationCancelled()
    {
        var (coordinator, _) = Create();
        var cancelled = 0;
        coordinator.OperationCancelled += () => cancelled++;

        coordinator.CancelCurrentOperation();

        Assert.Equal(DictationState.Idle, coordinator.State);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void UploadFailed_LiveGeneration_RaisesTypedError()
    {
        var (coordinator, clock) = Create();
        IDictationError? raised = null;
        coordinator.ErrorRaised += (error, _) => raised = error;
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        coordinator.ReportAudioCaptured(RealWav());
        var error = new TranscriptionError("Invalid API key — check Settings");

        Assert.True(coordinator.ReportUploadFailed(coordinator.Generation, error));
        Assert.Equal(DictationState.Error, coordinator.State);
        Assert.Same(error, raised);
    }

    [Fact]
    public void EmptyAudio_DiscardsToIdle()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();

        // Header-only WAV: no samples captured.
        Assert.Equal(
            AudioCaptureOutcome.Discarded,
            coordinator.ReportAudioCaptured(new byte[44]));
        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void LateStop_AfterCancel_IsDiscarded()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        coordinator.CancelCurrentOperation();
        clock.AdvanceMs(100);

        Assert.Equal(
            AudioCaptureOutcome.Discarded,
            coordinator.ReportAudioCaptured(RealWav()));
        Assert.Equal(DictationState.Idle, coordinator.State);
    }

    [Fact]
    public void Press_WhileUploading_IsIgnored()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();

        clock.AdvanceMs(100);
        Assert.Equal(HotkeyCommand.None, coordinator.OnHotkeyDown());
        Assert.Equal(DictationState.Uploading, coordinator.State);
    }

    [Fact]
    public void ToggleInput_StartsLockedAndIgnoresDebounce()
    {
        var (coordinator, _) = Create(ActivationModes.HoldOrToggle);

        Assert.Equal(HotkeyCommand.StartRecording, coordinator.OnToggleInput());

        // No release edge exists for tray input, yet an immediate second
        // toggle still stops — tray stop must always work.
        Assert.Equal(HotkeyCommand.StopRecording, coordinator.OnToggleInput());
        Assert.Equal(DictationState.Uploading, coordinator.State);
    }

    [Fact]
    public void AutoStop_StopsRecording()
    {
        var (coordinator, _) = Create();
        coordinator.OnHotkeyDown();

        Assert.Equal(HotkeyCommand.StopRecording, coordinator.OnAutoStop());
        Assert.Equal(DictationState.Uploading, coordinator.State);
    }

    [Fact]
    public void AutoStop_WhenIdle_DoesNothing()
    {
        var (coordinator, _) = Create();

        Assert.Equal(HotkeyCommand.None, coordinator.OnAutoStop());
    }

    [Fact]
    public void InvalidActivationMode_CoercesToToggle()
    {
        var (coordinator, _) = Create();
        coordinator.ActivationMode = "bogus";

        Assert.Equal(ActivationModes.Toggle, coordinator.ActivationMode);
    }

    [Fact]
    public void NotifyUploadCancelled_ReturnsLiveSessionToIdle()
    {
        var (coordinator, clock) = Create();
        coordinator.OnHotkeyDown();
        clock.AdvanceMs(100);
        coordinator.OnHotkeyDown();
        var generation = coordinator.Generation;

        coordinator.NotifyUploadCancelled(generation);

        Assert.Equal(DictationState.Idle, coordinator.State);
    }
}
