using System;
using System.IO;
using Xunit;

namespace VoiceIme.Tests;

public sealed class DictationErrorTests
{
    [Fact]
    public void RecordingError_MicDenied_PointsAtPrivacySettings()
    {
        var error = new RecordingError(RecordingErrorReason.MicDenied);

        Assert.Contains("privacy", error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordingError_NoDevice_AsksToConnectOne()
    {
        var error = new RecordingError(RecordingErrorReason.NoDevice);

        Assert.Contains("No microphone", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingError_Unknown_IsGenericButSafe()
    {
        var error = new RecordingError(RecordingErrorReason.Unknown);

        Assert.False(string.IsNullOrWhiteSpace(error.UserMessage));
    }

    [Fact]
    public void RecordingError_FromException_MapsUnauthorizedAccessToMicDenied()
    {
        var error = RecordingError.FromException(new UnauthorizedAccessException("denied"));

        Assert.Equal(RecordingErrorReason.MicDenied, error.Reason);
    }

    [Fact]
    public void RecordingError_FromException_MapsDeviceTextToNoDevice()
    {
        var error = RecordingError.FromException(new InvalidOperationException("BadDeviceId 42"));

        Assert.Equal(RecordingErrorReason.NoDevice, error.Reason);
    }

    [Fact]
    public void RecordingError_FromException_MapsOtherToUnknown()
    {
        var error = RecordingError.FromException(new IOException("boom"));

        Assert.Equal(RecordingErrorReason.Unknown, error.Reason);
    }

    [Fact]
    public void TranscriptionError_CarriesSafeMessageVerbatim()
    {
        var error = TranscriptionError.From(
            new TranscribeException("Invalid API key — check Settings"));

        Assert.Equal("Invalid API key — check Settings", error.UserMessage);
    }

    [Fact]
    public void TranscriptionError_BlankMessage_FallsBackToGeneric()
    {
        var error = new TranscriptionError("  ");

        Assert.Equal("Transcription failed — try again", error.UserMessage);
    }

    [Fact]
    public void PasteError_PointsAtHistory()
    {
        IDictationError error = new PasteError();

        Assert.Contains("History", error.UserMessage, StringComparison.Ordinal);
    }
}
