using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
// UseWindowsForms puts System.Drawing and System.Windows.Forms on the
// implicit-using list, so these three names are ambiguous. Pin the WPF ones.
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace VoiceIme.Theme;

/// <summary>
/// Concrete app theme. There is no "system" member — see
/// <see cref="ThemeManager.ResolveTheme"/> which maps a stored preference
/// ("system"|"light"|"dark") onto one of these, following the OS for "system".
/// </summary>
public enum HandyTheme
{
    Light,
    Dark,
}

/// <summary>
/// Applies a <see cref="Palette"/> to the running application.
///
/// <para><b>Mechanism.</b> The theme is applied by swapping a
/// <see cref="ResourceDictionary"/> in and out of
/// <c>Application.Resources.MergedDictionaries</c> rather than by mutating
/// individual entries. Merged-dictionary order decides precedence (the last
/// dictionary wins), so the active theme is appended last. Every consumer uses
/// <c>DynamicResource</c>, so they all re-resolve on the swap.</para>
///
/// <para><b>High contrast.</b> When a Windows contrast theme is active the
/// palette is replaced entirely by <see cref="SystemColors"/> — an app must
/// yield to the OS palette rather than paint its own colours, otherwise the
/// contrast theme is silently ignored. High contrast is a single variant: it
/// does not have light/dark flavours.</para>
///
/// <para><b>Testability.</b> The mapping from preference to theme, and from
/// environment to palette, are pure functions of injectable providers, so the
/// logic is unit-testable without a registry, a window station, or a running
/// WPF application.</para>
/// </summary>
public static class ThemeManager
{
    public const string SystemPreference = "system";
    public const string LightPreference = "light";
    public const string DarkPreference = "dark";

    // Swappable seams for tests; default to reading the real OS state.
    internal static Func<bool> SystemDarkModeProvider = DetectSystemDarkMode;
    internal static Func<bool> SystemHighContrastProvider = DetectSystemHighContrast;

    /// <summary>The dictionary currently installed in MergedDictionaries.</summary>
    private static ResourceDictionary? _active;

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
    /// Reads the Windows *Apps* theme (Settings → Personalization → Colors →
    /// "Choose your default app mode"). Returns false (light) when the value
    /// cannot be read. Deliberately reads <c>AppsUseLightTheme</c> and not
    /// <c>SystemUsesLightTheme</c>: the latter describes the shell chrome.
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
    /// True when a Windows contrast theme is active, in which case the app
    /// must stop painting its own palette and defer to <see cref="SystemColors"/>.
    /// </summary>
    public static bool DetectSystemHighContrast()
    {
        try
        {
            return SystemParameters.HighContrast;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The concrete theme most recently applied. Window-chrome consumers read
    /// this; defaults to Light before the first <see cref="ApplyTheme"/> call.
    /// </summary>
    public static HandyTheme CurrentTheme { get; private set; } = HandyTheme.Light;

    /// <summary>
    /// True when the palette currently installed is the high-contrast one.
    /// Callers that need to skip purely decorative colour (brand tint, custom
    /// shadows) should check this and render no decoration at all.
    /// </summary>
    public static bool IsHighContrast { get; private set; }

    /// <summary>
    /// Raised after the palette is swapped so windows can refresh surfaces WPF
    /// does not own (the Win32 title bar). Raised synchronously on the calling
    /// thread; subscribers must not throw.
    /// </summary>
    public static event Action<HandyTheme>? ThemeApplied;

    /// <summary>
    /// Builds the palette dictionary for the current environment.
    ///
    /// High contrast takes precedence over the light/dark choice: the user has
    /// told the OS they need maximum contrast, so honouring that beats
    /// honouring a stored Dark preference.
    /// </summary>
    internal static ResourceDictionary BuildDictionary()
    {
        var highContrast = SystemHighContrastProvider();
        return highContrast ? BuildHighContrastDictionary() : BuildFromPalette();
    }

    private static ResourceDictionary BuildFromPalette()
    {
        var source = CurrentTheme == HandyTheme.Dark ? Palette.Dark : Palette.Light;
        var dictionary = new ResourceDictionary();

        foreach (var entry in source)
        {
            dictionary[entry.Key] = Freeze(entry.Value);
        }

        // Legacy Handy-era keys resolve to their semantic replacement so any
        // not-yet-migrated view keeps working — and keeps theming correctly.
        foreach (var (legacyKey, semanticKey) in Palette.LegacyAliases)
        {
            if (source.TryGetValue(semanticKey, out var color))
            {
                dictionary[legacyKey] = Freeze(color);
            }
        }

        return dictionary;
    }

    /// <summary>
    /// High-contrast palette: every semantic token maps onto a system colour
    /// so the OS contrast theme owns the appearance.
    ///
    /// Status colours intentionally collapse to <see cref="SystemColors.WindowTextColor"/>
    /// — inventing a red in high contrast is exactly the failure mode the
    /// contrast themes exist to prevent. Meaning that was conveyed by hue must
    /// therefore also be conveyed by text or an icon (WCAG 1.4.1), which the
    /// status views do.
    /// </summary>
    private static ResourceDictionary BuildHighContrastDictionary()
    {
        var dictionary = new ResourceDictionary();

        void Set(string key, Color color) => dictionary[key] = Freeze(color);

        var window = SystemColors.WindowColor;
        var windowText = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        var highlightText = SystemColors.HighlightTextColor;

        // Surfaces — flatten to the single window surface (no elevation games
        // in high contrast; the OS deliberately removes depth cues).
        Set(Palette.SurfaceCanvas, window);
        Set(Palette.SurfaceCard, window);
        Set(Palette.SurfaceSunken, window);
        Set(Palette.SurfaceHover, highlight);
        Set(Palette.SurfaceSelected, highlight);
        Set(Palette.SurfaceOverlay, window);

        // Text — window text everywhere; secondary/tertiary collapse because
        // a contrast theme has no notion of "muted" (and GreyText is reserved
        // for genuinely disabled content).
        Set(Palette.TextPrimary, windowText);
        Set(Palette.TextSecondary, windowText);
        Set(Palette.TextMuted, windowText);
        Set(Palette.TextDisabled, SystemColors.GrayTextColor);

        // Accent.
        Set(Palette.AccentFill, highlight);
        Set(Palette.AccentFillHover, highlight);
        Set(Palette.AccentFillPressed, highlight);
        Set(Palette.OnAccent, highlightText);
        Set(Palette.AccentInk, windowText);
        Set(Palette.AccentSubtle, highlight);

        // Lines and focus. WPF's SystemColors has no Hotlight/HotTrack pair
        // guaranteed across themes, so the focus ring uses window text — it is
        // by definition the maximum-contrast line colour in a contrast theme.
        Set(Palette.BorderSubtle, windowText);
        Set(Palette.BorderStrong, windowText);
        Set(Palette.FocusRing, windowText);

        // Status — all collapse to window text; see the method remarks.
        foreach (var key in new[]
                 {
                     Palette.ErrorText, Palette.ErrorSurface,
                     Palette.WarningText, Palette.WarningSurface,
                     Palette.SuccessText, Palette.SuccessSurface,
                 })
        {
            Set(key, windowText);
        }

        Set(Palette.OnError, window);
        Set(Palette.OnWarning, window);
        Set(Palette.OnSuccess, window);

        // Legacy aliases resolve against the high-contrast values above.
        foreach (var (legacyKey, semanticKey) in Palette.LegacyAliases)
        {
            if (dictionary[semanticKey] is SolidColorBrush brush)
            {
                dictionary[legacyKey] = brush;
            }
        }

        return dictionary;
    }

    /// <summary>
    /// Brushes are frozen: they are shared across the whole visual tree, so
    /// freezing removes per-use change-notification cost and makes them safe
    /// to hand to other threads.
    /// </summary>
    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Installs the palette for <paramref name="theme"/>.
    ///
    /// No-ops the dictionary swap when there is no running WPF application
    /// (unit tests), but still updates <see cref="CurrentTheme"/> and raises
    /// <see cref="ThemeApplied"/> so chrome logic stays testable.
    /// </summary>
    public static void ApplyTheme(HandyTheme theme)
    {
        CurrentTheme = theme;
        IsHighContrast = SystemHighContrastProvider();

        var app = Application.Current;
        if (app is not null)
        {
            var dictionaries = app.Resources.MergedDictionaries;
            var replacement = BuildDictionary();

            // Append-last so the active theme wins merged-dictionary lookup,
            // then drop the previous one.
            dictionaries.Add(replacement);
            if (_active is not null)
            {
                dictionaries.Remove(_active);
            }

            _active = replacement;
        }

        try
        {
            ThemeApplied?.Invoke(theme);
        }
        catch
        {
            // Subscriber contract violation — never let it break a theme swap.
        }
    }

    /// <summary>
    /// Flips the Win32 title bar to match <see cref="CurrentTheme"/> via
    /// DWMWA_USE_IMMERSIVE_DARK_MODE (attribute 20 on Windows 10 20H1+, 19 on
    /// 1809–1909) — WPF never restyles the native chrome by itself, so a dark
    /// app with a white title bar is the classic symptom. Safe no-op when the
    /// window has no handle yet (call again from SourceInitialized) or on
    /// systems without DWM support. Never throws.
    ///
    /// Note: the documented behaviour is that setting TRUE makes the bar
    /// "follow the system setting", but in practice it always paints dark —
    /// so the value is driven from our own theme state, not left to DWM.
    /// </summary>
    public static void ApplyWindowChrome(System.Windows.Window window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            ApplyWindowChrome(handle);
        }
        catch
        {
            // No handle / no window station — chrome stays default.
        }
    }

    /// <summary>HWND overload for callers that already own the handle.</summary>
    public static void ApplyWindowChrome(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // A contrast theme owns the chrome; do not fight it.
        if (IsHighContrast)
        {
            return;
        }

        var useDark = CurrentTheme == HandyTheme.Dark ? 1 : 0;
        try
        {
            // Documented attribute first (20, Windows 10 20H1+); fall back to
            // the pre-20H1 value (19) when DWM rejects it.
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // dwmapi missing (very old OS) — chrome stays default.
        }
    }

    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}