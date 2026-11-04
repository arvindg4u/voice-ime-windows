using System;

namespace VoiceIme;

/// <summary>
/// Marker for pipeline failures that reach the user. Messages surfaced through
/// <see cref="Notifier"/> must come from <see cref="UserMessage"/> — never from
/// raw exception text — so no key material or filesystem paths leak.
/// Task 7 defines these types; Task 8 only raises them from the coordinator.
/// </summary>
public interface IDictationError
{
    string UserMessage { get; }
}

/// <summary>Why recording never started (or died immediately).</summary>
public enum RecordingErrorReason
{
    MicDenied,
    NoDevice,
    Unknown,
}

/// <summary>Recording failed: mic denied, no device, or unknown.</summary>
public sealed record RecordingError(RecordingErrorReason Reason) : IDictationError
{
    public string UserMessage => Reason switch
    {
        RecordingErrorReason.MicDenied =>
            "Microphone access denied — allow it in Windows privacy settings",
        RecordingErrorReason.NoDevice =>
            "No microphone found — connect one and try again",
        _ => "Microphone unavailable — try again",
    };

    /// <summary>
    /// Best-effort classification of a recorder exception. NAudio surfaces
    /// driver failures as MmException with free-form text, so "no device" is
    /// detected heuristically; callers that already enumerated devices should
    /// construct <see cref="RecordingErrorReason.NoDevice"/> directly.
    /// </summary>
    public static RecordingError FromException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is UnauthorizedAccessException
            || ex.InnerException is UnauthorizedAccessException)
        {
            return new RecordingError(RecordingErrorReason.MicDenied);
        }

        var text = ex.Message;
        if (text.Contains("device", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NoDriver", StringComparison.Ordinal)
            || text.Contains("BadDeviceId", StringComparison.Ordinal))
        {
            return new RecordingError(RecordingErrorReason.NoDevice);
        }

        return new RecordingError(RecordingErrorReason.Unknown);
    }
}

/// <summary>
/// Transcription failed. Wraps the already user-safe
/// <see cref="TranscribeException"/> message (LlmClient never puts key
/// material in it); blank messages fall back to a generic string.
/// </summary>
public sealed record TranscriptionError(string Message) : IDictationError
{
    public string UserMessage =>
        string.IsNullOrWhiteSpace(Message) ? "Transcription failed — try again" : Message;

    public static TranscriptionError From(TranscribeException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new TranscriptionError(ex.Message);
    }
}

// PasteError lives in PasteOutcome.cs — its message now depends on the
// classified failure reason, so the type is defined beside the classifier.
