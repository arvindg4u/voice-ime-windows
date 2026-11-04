using System.Collections.Generic;

namespace VoiceIme;

/// <summary>
/// Sidebar metadata for <see cref="MainWindow"/>. Single source of truth for
/// section order, labels and icons; kept free of WPF types so it is
/// unit-testable on any thread. Pure data — no behavior.
/// </summary>
public static class MainNav
{
    public static IReadOnlyList<MainSection> Ordered { get; } = new[]
    {
        MainSection.General,
        MainSection.History,
        MainSection.Gemini,
        MainSection.Advanced,
        MainSection.About,
    };

    /// <summary>
    /// Display label. "Gemini" reads as an implementation detail; users think in
    /// tasks, so the section is "Speech" — the same reasoning that keeps the
    /// menu item "Speech model" rather than a vendor name.
    /// </summary>
    public static string LabelFor(MainSection section) => section switch
    {
        MainSection.General => "General",
        MainSection.History => "History",
        MainSection.Gemini => "Speech",
        MainSection.Advanced => "Advanced",
        MainSection.About => "About",
        // Unnamed enum values (e.g. a stale cast) fall back to the raw name
        // instead of throwing — same coerce-don't-throw discipline as the
        // string LabelFor helpers. Silences CS8524.
        _ => section.ToString(),
    };

    /// <summary>
    /// Segoe Fluent Icons glyph per section.
    ///
    /// These replace the emoji the first port used. Emoji were a real problem:
    /// they are colour glyphs, so they ignore the theme, they render
    /// differently per font fallback, and the About entry was a bare Latin "i"
    /// plus a variation selector rather than an icon at all. Segoe Fluent Icons
    /// is the Windows 11 system icon font — monochrome, vector, theme-aware, and
    /// what Explorer and Settings themselves use.
    /// </summary>
    public static string IconFor(MainSection section) => section switch
    {
        MainSection.General => Glyphs.Settings,      // gear
        MainSection.History => Glyphs.History,        // clock
        MainSection.Gemini => Glyphs.Microphone,      // mic
        MainSection.Advanced => Glyphs.Tune,          // sliders
        MainSection.About => Glyphs.Info,             // info circle
        _ => Glyphs.Question,                         // question circle
    };

    /// <summary>
    /// Accessible name for a nav item. Screen readers announce this instead of
    /// guessing at a private-use glyph, which they render as "unknown" or
    /// nothing at all.
    /// </summary>
    public static string AutomationNameFor(MainSection section) =>
        LabelFor(section) + " section";

    /// <summary>
    /// Segoe Fluent Icons codepoints (U+E700–U+F8FF private-use area).
    /// Kept as a nested type so the font's contract is in one place — if a
    /// glyph is swapped, this is the only file that changes.
    /// </summary>
    public static class Glyphs
    {
        public const string Settings = "\uE713";
        public const string History = "\uE81C";
        public const string Microphone = "\uE720";
        public const string Tune = "\uE9E9";
        public const string Info = "\uE946";
        public const string Question = "\uE897";

        // Additional glyphs used across the views.
        public const string Play = "\uE768";
        public const string Stop = "\uE71A";
        public const string Copy = "\uE8C8";
        public const string Pin = "\uE718";
        public const string Unpin = "\uE77A";
        public const string Delete = "\uE74D";
        public const string Refresh = "\uE72C";
        public const string OpenFolder = "\uE8B7";
        public const string Warning = "\uE7BA";
        public const string Error = "\uEA39";
        public const string Success = "\uE930";
        public const string InfoSmall = "\uE946";
        public const string Chevron = "\uE76C";
        public const string Add = "\uE710";
        public const string Search = "\uE721";
    }
}