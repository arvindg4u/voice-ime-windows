namespace VoiceIme.Theme;

/// <summary>
/// Non-colour design tokens: the spacing scale, corner radii, control metrics
/// and type ramp that every view must use instead of magic numbers.
///
/// Values follow the Windows 11 / Fluent 2 baseline so the app sits naturally
/// next to Explorer and Settings rather than looking hand-rolled:
///
/// <list type="bullet">
/// <item>4px base unit; the scale is 4 → 8 → 12 → 16 → 24 → 32.</item>
/// <item>Controls are 32px tall (Windows' preferred desktop control height),
/// with 40px for primary actions and a 44px minimum pointer target for the
/// overlay's floating button.</item>
/// <item>Radii: 4 for inputs, 8 for cards and buttons, 12 for the floating
/// pill. Windows 11 uses 4/8/12 in exactly this way.</item>
/// <item>Type ramp: 12 caption / 13 body / 14 body-strong / 20 title / 28
/// display, on Segoe UI Variable where available (falls back to Segoe UI).</item>
/// </list>
///
/// These are plain constants rather than XAML resources so they can be used
/// from code-behind, and so the XAML dictionary can be generated from the same
/// values — one source, no drift.
/// </summary>
public static class DesignTokens
{
    // --- Spacing scale ----------------------------------------------------
    /// <summary>4 px — icon-to-text, tight pairs.</summary>
    public const double Space1 = 4;
    /// <summary>8 px — related controls.</summary>
    public const double Space2 = 8;
    /// <summary>12 px — row inner gap, card inner padding (compact).</summary>
    public const double Space3 = 12;
    /// <summary>16 px — card padding, content gutter.</summary>
    public const double Space4 = 16;
    /// <summary>24 px — between groups.</summary>
    public const double Space5 = 24;
    /// <summary>32 px — page top/bottom breathing room.</summary>
    public const double Space6 = 32;

    // --- Corner radii ----------------------------------------------------
    /// <summary>4 px — text inputs, small chips.</summary>
    public const double RadiusSmall = 4;
    /// <summary>8 px — buttons, cards, nav items.</summary>
    public const double RadiusMedium = 8;
    /// <summary>12 px — floating pill, dialogs.</summary>
    public const double RadiusLarge = 12;
    /// <summary>Fully round (pills, toggles, dots).</summary>
    public const double RadiusPill = 999;

    // --- Control metrics -------------------------------------------------
    /// <summary>Standard control height (Windows preferred desktop metric).</summary>
    public const double ControlHeight = 32;
    /// <summary>Primary / prominent action height.</summary>
    public const double ControlHeightLarge = 40;
    /// <summary>Minimum interactive target for touch and motor accessibility.</summary>
    public const double MinTouchTarget = 44;
    /// <summary>Height of one settings row (label + control).</summary>
    public const double SettingsRowMinHeight = 56;
    /// <summary>Sidebar width.</summary>
    public const double SidebarWidth = 208;
    /// <summary>Maximum comfortable text column — keeps helper text readable.</summary>
    public const double ContentMaxWidth = 720;

    // --- Type ramp (device-independent pixels) ---------------------------
    /// <summary>Caption / helper / footer.</summary>
    public const double FontCaption = 12;
    /// <summary>Body copy and control labels.</summary>
    public const double FontBody = 13;
    /// <summary>Emphasised body (row titles).</summary>
    public const double FontBodyStrong = 14;
    /// <summary>Section/page title.</summary>
    public const double FontTitle = 20;
    /// <summary>Display (first-run hero).</summary>
    public const double FontDisplay = 28;

    /// <summary>
    /// Preferred UI font. Segoe UI Variable is the Windows 11 system font and
    /// renders more crisply at small sizes; the comma fallback chain degrades
    /// to Segoe UI on Windows 10 and earlier.
    /// </summary>
    public const string FontFamily =
        "Segoe UI Variable Text, Segoe UI, Segoe UI Emoji, sans-serif";

    /// <summary>Monospace stack for API keys, logs and versions.</summary>
    public const string MonoFontFamily =
        "Cascadia Mono, Consolas, Courier New, monospace";
}