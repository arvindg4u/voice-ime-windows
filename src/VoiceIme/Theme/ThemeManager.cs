using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VoiceIme.Theme;

/// <summary>
/// Concrete Handy UI theme. There is no "system" value here — see
/// <see cref="ThemeManager.ResolveTheme"/> which maps a stored preference
/// ("system"|"light"|"dark") to one of these, following the OS for "system".
/// </summary>
public enum HandyTheme
{
    Light,
    Dark,
}

/// <summary>
/// Maps a stored theme preference to the concrete theme and swaps the
/// HandyTheme.xaml brushes at runtime (all consumers use DynamicResource,
/// so they update live). The mapping is a pure function of (preference, OS
/// setting) via the injectable <c>SystemDarkModeProvider</c>, so it is
/// unit-testable without touching the registry or a running WPF app.
/// </summary>
public static class ThemeManager
{
    public const string SystemPreference = "system";
    public const string LightPreference = "light";
    public const string DarkPreference = "dark";

    // Accent, mid-gray, borders and secondary fills are theme-invariant;
    // they are listed in both palettes so ApplyTheme always sets every key.
    private static readonly IReadOnlyDictionary<string, Color> LightPalette =
        new Dictionary<string, Color>
        {
            ["HandyText"] = Color.FromRgb(0x0F, 0x0F, 0x0F),
            ["HandyBackground"] = Color.FromRgb(0xFB, 0xFB, 0xFB),
            ["HandyAccent"] = Color.FromRgb(0xDA, 0x58, 0x93),
            ["HandyLogoPrimary"] = Color.FromRgb(0xFA, 0xA2, 0xCA),
            ["HandyError"] = Color.FromRgb(0xDC, 0x26, 0x26),
            ["HandyWarning"] = Color.FromRgb(0xD9, 0x77, 0x06),
            ["HandyMidGray"] = Color.FromRgb(0x80, 0x80, 0x80),
            ["HandyCardBorder"] = Color.FromArgb(0x33, 0x80, 0x80, 0x80),
            ["HandyCardBackground"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["HandySecondaryBackground"] = Color.FromArgb(0x33, 0x80, 0x80, 0x80),
        };

    private static readonly IReadOnlyDictionary<string, Color> DarkPalette =
        new Dictionary<string, Color>
        {
            ["HandyText"] = Color.FromRgb(0xFB, 0xFB, 0xFB),
            ["HandyBackground"] = Color.FromRgb(0x2C, 0x2B, 0x29),
            ["HandyAccent"] = Color.FromRgb(0xDA, 0x58, 0x93),
            ["HandyLogoPrimary"] = Color.FromRgb(0xF2, 0x8C, 0xBB),
            ["HandyError"] = Color.FromRgb(0xF8, 0x71, 0x71),
            ["HandyWarning"] = Color.FromRgb(0xFB, 0xBF, 0x24),
            ["HandyMidGray"] = Color.FromRgb(0x80, 0x80, 0x80),
            ["HandyCardBorder"] = Color.FromArgb(0x33, 0x80, 0x80, 0x80),
            // White @5% over #2C2B29 (reference cards use bg-white/5).
            ["HandyCardBackground"] = Color.FromRgb(0x37, 0x36, 0x34),
            ["HandySecondaryBackground"] = Color.FromArgb(0x33, 0x80, 0x80, 0x80),
        };

    // Swappable seam for tests; defaults to reading the OS setting.
    internal static Func<bool> SystemDarkModeProvider = DetectSystemDarkMode;

    /// <summary>
    /// Maps "system"|"light"|"dark" to the concrete theme. Unknown, null,
    /// empty or whitespace-only input falls back to the OS ("system") behavior.
    /// </summary>
    public static HandyTheme ResolveTheme(string? preference)
    {
        var normalized = (preference ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            LightPreference => HandyTheme.Light,
            DarkPreference => HandyTheme.Dark,
            _ => SystemDarkModeProvider() ? HandyTheme.Dark : HandyTheme.Light,
        };
    }

    /// <summary>
    /// Reads the Windows Apps theme (Settings → Personalization → Colors).
    /// Returns false (light) when the value cannot be read.
    /// </summary>
    public static bool DetectSystemDarkMode()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return Convert.ToInt32(value) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the Handy brush entries in the application resources so every
    /// DynamicResource consumer updates live. No-op when there is no running
    /// WPF application (e.g. unit tests).
    /// </summary>
    public static void ApplyTheme(HandyTheme theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var palette = theme == HandyTheme.Dark ? DarkPalette : LightPalette;
        foreach (var entry in palette)
        {
            app.Resources[entry.Key] = new SolidColorBrush(entry.Value);
        }
    }
}
