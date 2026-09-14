using System;
using VoiceIme.Theme;

namespace VoiceIme;

/// <summary>
/// Theme preferences for the About screen (Handy theme port): the stored
/// preference string. <see cref="ThemeManager.ResolveTheme"/> maps it to the
/// concrete theme; unknown values coerce to <see cref="System"/> — never throw
/// on a bad file.
/// </summary>
public static class AppThemes
{
    public const string System = ThemeManager.SystemPreference;
    public const string Light = ThemeManager.LightPreference;
    public const string Dark = ThemeManager.DarkPreference;

    public static bool IsValid(string? value) =>
        string.Equals(value, System, StringComparison.Ordinal)
        || string.Equals(value, Light, StringComparison.Ordinal)
        || string.Equals(value, Dark, StringComparison.Ordinal);

    public static string LabelFor(string? theme) => theme switch
    {
        Light => "Light",
        Dark => "Dark",
        _ => "System",
    };

    public static string ThemeForLabel(string? label) => label switch
    {
        "Light" => Light,
        "Dark" => Dark,
        _ => System,
    };
}
