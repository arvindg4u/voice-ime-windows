using System;

namespace VoiceIme;

/// <summary>
/// Overlay phases. Task 7/8 contract: <see cref="OverlayWindow.Show"/>
/// takes one of these; do not rename the members.
/// </summary>
public enum OverlayPhase
{
    Recording,
    Uploading,
    Error,
}

/// <summary>
/// Pure overlay viewmodel: phase + perceptual level 0..1 (+ optional message
/// for <see cref="OverlayPhase.Error"/>). All transitions return new
/// instances and never mutate — the window renders from snapshots. No WPF
/// types here, so this is fully unit-testable on any OS.
/// </summary>
public sealed record OverlayState(OverlayPhase Phase, float Level, string? Message = null)
{
    public static OverlayState Initial { get; } = new(OverlayPhase.Recording, 0f);

    /// <summary>
    /// Ellipsis steps in the uploading label cycle ("" through "...").
    /// </summary>
    public const int EllipsisPhaseCount = 4;

    /// <summary>
    /// Formats an elapsed duration as m:ss ("0:00", "0:05", "1:05", "10:00").
    /// Negative inputs clamp to zero.
    /// </summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
    }

    /// <summary>
    /// Formats the indeterminate uploading label for a 1 s timer tick (the
    /// whole-second elapsed count): cycles "Sending" → "Sending." →
    /// "Sending.." → "Sending..." then wraps. Negative ticks wrap
    /// (tick -1 shows three dots), so any tick is safe.
    /// </summary>
    public static string FormatUploadingLabel(int tick)
    {
        var phase = ((tick % EllipsisPhaseCount) + EllipsisPhaseCount) % EllipsisPhaseCount;
        return "Sending" + new string('.', phase);
    }

    /// <summary>
    /// Joins finalized text and the interim hypothesis into a single preview
    /// line: both parts trimmed, joined with one space, empty parts dropped.
    /// Empty input yields "".
    /// </summary>
    internal static string FormatPreview(string finals, string interim)
    {
        var head = finals.Trim();
        var tail = interim.Trim();
        if (head.Length == 0)
        {
            return tail;
        }

        if (tail.Length == 0)
        {
            return head;
        }

        return head + " " + tail;
    }

    /// <summary>
    /// Keeps only the last <paramref name="maxChars"/> characters of a string.
    ///
    /// <para>Measured cost of a single-line TextBlock grows with its character
    /// count, and the preview is re-measured on every interim revision. The
    /// window is deliberately far wider than the visible pill, so the cut is
    /// always off-screen — this bounds the layout work without changing what
    /// the user sees.</para>
    ///
    /// <para>Returns the input unchanged when it already fits, and never throws
    /// on a non-positive window (it degrades to empty rather than slicing with
    /// a negative index).</para>
    /// </summary>
    internal static string TailWindow(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    /// <summary>
    /// Splits the preview into the two runs the pill renders: settled text and
    /// the provisional hypothesis.
    ///
    /// <para>A separating space is prepended to the interim run only when both
    /// parts are present, so the two runs concatenate into exactly
    /// <see cref="FormatPreview"/>'s output — one space between them, none
    /// dangling at the end.</para>
    /// </summary>
    internal static (string Finals, string Interim) PreviewRuns(string finals, string interim)
    {
        var head = finals.Trim();
        var tail = interim.Trim();
        if (tail.Length == 0)
        {
            return (head, string.Empty);
        }

        return head.Length == 0 ? (string.Empty, tail) : (head, " " + tail);
    }

    /// <summary>
    /// Moves to a new phase, resetting the level (a fresh phase starts flat)
    /// and replacing the message.
    /// </summary>
    public OverlayState WithPhase(OverlayPhase phase, string? message = null) =>
        this with { Phase = phase, Level = 0f, Message = message };

    /// <summary>Sets the level, clamped to 0..1.</summary>
    public OverlayState WithLevel(float level) =>
        this with { Level = Math.Clamp(level, 0f, 1f) };
}
