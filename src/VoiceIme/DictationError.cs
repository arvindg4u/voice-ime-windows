using System;
using System.Linq;

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
/// Transcription failed. The transport messages are intentionally kept as a
/// small allowlist: a future transport or a test double must not turn an
/// arbitrary exception message into user-visible text. Unknown messages fall
/// back to a generic retry prompt.
/// </summary>
public sealed record TranscriptionError(string Message) : IDictationError
{
    public string UserMessage => Sanitize(Message);

    public static TranscriptionError From(TranscribeException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new TranscriptionError(Sanitize(ex.Message));
    }

    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Transcription failed — try again";
        }

        var value = message.Trim();
        if (value is
            "No API key — open Settings"
            or "Invalid API key — check Settings"
            or "Rate limited on all keys — retry later"
            or "Network error — check connection"
            or "Timed out — try again"
            or "Got empty transcript — try again"
            or "Couldn't understand the response — try again"
            or "Invalid audio — try again"
            or "Audio format not supported — try again"
            or "Transcription failed — try again"
            or "Server error (HTTP 400) — try again"
            or "Server error (HTTP 401) — try again"
            or "Server error (HTTP 403) — try again"
            or "Server error (HTTP 404) — try again"
            or "Server error (HTTP 408) — try again"
            or "Server error (HTTP 429) — try again"
            or "Server error (HTTP 500) — try again"
            or "Server error (HTTP 502) — try again"
            or "Server error (HTTP 503) — try again"
            or "Server error (HTTP 504) — try again")
        {
            return value;
        }

        if (IsRequestFailure(value) || IsServerFailure(value))
        {
            return value;
        }

        return "Transcription failed — try again";
    }

    private static bool IsRequestFailure(string value) =>
        HasThreeDigitCode(value, "Request failed (", ") — try again");

    private static bool IsServerFailure(string value) =>
        HasThreeDigitCode(value, "Server error (HTTP ", ") — try again");

    private static bool HasThreeDigitCode(string value, string prefix, string suffix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var code = value[prefix.Length..^suffix.Length];
        return code.Length == 3
            && code[0] is >= '1' and <= '5'
            && code.All(static c => c is >= '0' and <= '9');
    }
}

/// <summary>
/// Paste/clipboard delivery failed. The transcript is always in History
/// (ClipboardStore.Add runs before the paste keystroke), so the message
/// points there instead of asking for a re-dictation.
/// </summary>
public sealed record PasteError : IDictationError
{
    public string UserMessage =>
        "Couldn't paste into the focused window — transcript kept in History";
}
