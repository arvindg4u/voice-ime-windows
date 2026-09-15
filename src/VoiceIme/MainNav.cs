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

    public static string LabelFor(MainSection section) => section switch
    {
        MainSection.General => "General",
        MainSection.History => "History",
        MainSection.Gemini => "Gemini",
        MainSection.Advanced => "Advanced",
        MainSection.About => "About",
        // Unnamed enum values (e.g. a stale cast) fall back to the raw name
        // instead of throwing — same coerce-don't-throw discipline as the
        // string LabelFor helpers. Silences CS8524.
        _ => section.ToString(),
    };

    public static string IconFor(MainSection section) => section switch
    {
        MainSection.General => "⚙️",
        MainSection.History => "🕘",
        MainSection.Gemini => "✨",
        MainSection.Advanced => "🔧",
        MainSection.About => "ℹ️",
        _ => "❓",
    };
}
