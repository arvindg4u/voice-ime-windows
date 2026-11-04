using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;

namespace VoiceIme.Theme;

/// <summary>
/// Maps a stored theme preference to the concrete theme and swaps the
/// HandyTheme.xaml brushes at runtime (all consumers use DynamicResource,
/// so they update live). The mapping is a pure function of (preference, OS
/// setting) via the injectable <c>SystemDarkModeProvider</c>, so it is
/// unit-testable without touching the registry or a running WPF app.
///
/// Palette design (dark mode best-practices, see docs/RESEARCH.md):
/// - Never pure black (#000000) as a dark surface — causes eye strain and
///   OLED smearing. Dark backgrounds use dark gray (#2C2B29, Windows 11
///   app-dark).
/// - Never pure white (#FFFFFF) as dark text — causes halation/glare. Dark
///   text uses off-white (#E8E8E8).
/// - Brand accent is desaturated and lightened for dark mode so it reads as
///   a calm tint rather than neon against dark surfaces.
/// - Surfaces layer with clear value gaps so elevation reads without
///   shadows: Background < Surface < SurfaceElevated.
/// - Contrast targets: primary text ≥ 7:1, secondary text ≥ 4.5:1,
///   disabled ≥ 3:1, UI components ≥ 3:1 against their background.
/// </summary>
public static class ThemeManager
{
    public const string SystemPreference = "system";
    public const string LightPreference = "light";
    public const string DarkPreference = "dark";

    /// <summary>
    /// Semantic color roles, shared by both palettes. Each role gets a
    /// separate value per theme — this is a single token system mapped to
    /// two palettes, not two independent stylesheets.
    /// </summary>
    public static readonly IReadOnlyList<string> TokenRoles = new[]
    {
        // Base surfaces
        "HandyBackground",   // window/page base canvas
        "HandySurface",      // cards, elevated containers
        "HandySurfaceElevated", // modals, dropdowns, flyouts (lightest surface)

        // Text
        "HandyText",         // primary body text
        "HandyTextSecondary", // secondary/caption/helper text
        "HandyTextDisabled", // disabled text

        // Brand accent (brand pink; adjusted per theme for comfort)
        "HandyAccent",       // primary brand color
        "HandyAccentHover",  // accent under pointer
        "HandyAccentActive", // accent when pressed/active
        "HandyAccentContainer", // light tinted fill behind accent elements

        // Borders & dividers
        "HandyBorder",       // input borders, card borders
        "HandyDivider",      // hairline section dividers

        // Status
        "HandyError",        // error text/borders
        "HandyWarning",      // warning text/borders

        // Focus & interaction
        "HandyFocusBorder",  // keyboard focus ring
        "HandyInkDisabled",  // disabled icon/button glyphs
        "HandyHoverBackground", // subtle hover fill
        "HandyActiveBackground", // active/selected fill (nav rail, tiles)

        // Brand
        "HandyLogoPrimary",  // logo pink
    };

    // Accent, borders and secondary fills are conceptually theme-invariant
    // in the Handy reference, but dark mode best-practices say the accent
    // should be desaturated/lightened on dark. Each palette lists every key
    // so ApplyTheme always sets every role.
    private static readonly IReadOnlyDictionary<string, Color> LightPalette =
        new Dictionary<string, Color>
        {
            // Surfaces
            ["HandyBackground"] = Color.FromRgb(0xFB, 0xFB, 0xFB),
            ["HandySurface"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["HandySurfaceElevated"] = Color.FromRgb(0xF8, 0xF8, 0xF8),

            // Text
            ["HandyText"] = Color.FromRgb(0x0F, 0x0F, 0x0F),
            ["HandyTextSecondary"] = Color.FromRgb(0x5F, 0x5F, 0x5F),
            ["HandyTextDisabled"] = Color.FromRgb(0xC7, 0xC7, 0xC7),

            // Brand accent (pink, comfortable on light)
            ["HandyAccent"] = Color.FromRgb(0xDA, 0x58, 0x93),
            ["HandyAccentHover"] = Color.FromRgb(0xC4, 0x4E, 0x7F),
            ["HandyAccentActive"] = Color.FromRgb(0xAE, 0x43, 0x72),
            ["HandyAccentContainer"] = Color.FromRgb(0xFD, 0xE9, 0xF0),

            // Borders
            ["HandyBorder"] = Color.FromRgb(0xE5, 0xE5, 0xE5),
            ["HandyDivider"] = Color.FromRgb(0xE5, 0xE5, 0xE5),

            // Status
            ["HandyError"] = Color.FromRgb(0xDC, 0x26, 0x26),
            ["HandyWarning"] = Color.FromRgb(0xD9, 0x77, 0x06),

            // Focus / interaction
            ["HandyFocusBorder"] = Color.FromRgb(0xDA, 0x58, 0x93),
            ["HandyInkDisabled"] = Color.FromRgb(0xC7, 0xC7, 0xC7),
            ["HandyHoverBackground"] = Color.FromRgb(0xEF, 0xEF, 0xEF),
            ["HandyActiveBackground"] = Color.FromRgb(0xFD, 0xE9, 0xF0),

            // Brand
            ["HandyLogoPrimary"] = Color.FromRgb(0xFA, 0xA2, 0xCA),

            // Legacy aliases kept for consumers that still reference them.
            ["HandyCardBackground"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            ["HandyCardBorder"] = Color.FromArgb(0x33, 0x80, 0x80, 0x80),
            ["HandySecondaryBackground"] = Color.FromArgb(0x33, 0x80, 0x80, 0x80),
            ["HandyMidGray"] = Color.FromRgb(0x80, 0x80, 0x80),
        };

    private static readonly IReadOnlyDictionary<string, Color> DarkPalette =
        new Dictionary<string, Color>
        {
            // Surfaces — dark gray base, not pure black; clear elevation gaps.
            ["HandyBackground"] = Color.FromRgb(0x2C, 0x2B, 0x29),
            ["HandySurface"] = Color.FromRgb(0x3A, 0x39, 0x37),
            ["HandySurfaceElevated"] = Color.FromRgb(0x45, 0x44, 0x42),

            // Text — off-white, not pure white (reduces glare/halation).
            ["HandyText"] = Color.FromRgb(0xE8, 0xE8, 0xE8),
            ["HandyTextSecondary"] = Color.FromRgb(0xA8, 0xA7, 0xA4),
            ["HandyTextDisabled"] = Color.FromRgb(0x6E, 0x6D, 0x6B),

            // Brand accent — desaturated + lightened for dark so it doesn't
            // vibrate against dark surfaces.
            ["HandyAccent"] = Color.FromRgb(0xE0, 0x8F, 0xB8),
            ["HandyAccentHover"] = Color.FromRgb(0xD6, 0x7A, 0xA8),
            ["HandyAccentActive"] = Color.FromRgb(0xC8, 0x6A, 0x94),
            ["HandyAccentContainer"] = Color.FromRgb(0x3B, 0x2D, 0x32),

            // Borders — visible against dark surfaces.
            ["HandyBorder"] = Color.FromRgb(0x3A, 0x39, 0x37),
            ["HandyDivider"] = Color.FromRgb(0x3A, 0x39, 0x37),

            // Status — softened for dark backgrounds.
            ["HandyError"] = Color.FromRgb(0xF8, 0x71, 0x71),
            ["HandyWarning"] = Color.FromRgb(0xFB, 0xBF, 0x24),

            // Focus / interaction
            ["HandyFocusBorder"] = Color.FromRgb(0xE0, 0x8F, 0xB8),
            ["HandyInkDisabled"] = Color.FromRgb(0x6E, 0x6D, 0x6B),
            ["HandyHoverBackground"] = Color.FromRgb(0x38, 0x37, 0x35),
            ["HandyActiveBackground"] = Color.FromRgb(0x3B, 0x2D, 0x32),

            // Brand
            ["HandyLogoPrimary"] = Color.FromRgb(0xF2, 0x8C, 0xBB),

            // Legacy aliases kept for consumers that still reference them.
            ["HandyCardBackground"] = Color.FromRgb(0x3A, 0x39, 0x37),
            ["HandyCardBorder"] = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            ["HandySecondaryBackground"] = Color.FromArgb(0x33, 0x80, 0x80, 0x80),
            ["HandyMidGray"] = Color.FromRgb(0x80, 0x80, 0x80),
        };

    // Swappable seam for tests; defaults to reading the OS setting.
    internal static Func<bool> SystemDarkModeProvider = DetectSystemDarkMode;

    /// <summary>
    /// Maps "system"|"light"|"dark" to the concrete theme. Unknown, null,
    /// empty or whitespace-only input falls back to the OS ("system")
    /// behavior.
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
        var app = System.Windows.Application.Current;
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
