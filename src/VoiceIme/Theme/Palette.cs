using System.Collections.Generic;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace VoiceIme.Theme;

/// <summary>
/// The single source of truth for every colour in the app.
///
/// Layering (standard design-token architecture — see docs/DESIGN-TOKENS.md):
///
///   primitive (brand hue)  ->  semantic token (this file)  ->  XAML brush key  ->  control style
///
/// Control styles and views must only ever reference the <em>semantic</em>
/// layer. Nothing downstream of this file may contain a literal colour.
///
/// Every token is emitted in BOTH palettes so a theme swap writes the complete
/// set — a key present in one palette but missing from the other would leave
/// stale colours on screen after a live switch.
///
/// <para>Contrast contract: every foreground/background pair the UI actually
/// renders is verified against WCAG 2.2 by ThemeContrastTests. Values here are
/// not free-form — changing one can break that test, which is the point.</para>
/// </summary>
public static class Palette
{
    // ---------------------------------------------------------------------
    // Token keys. Naming follows the semantic convention used by Fluent 2 /
    // Material 3: {role}{emphasis}. "On*" means "content drawn on top of".
    // ---------------------------------------------------------------------

    // Surfaces — the background ladder. Elevation is expressed by lightness
    // (light theme: card is brightest; dark theme: card is lighter than page).
    public const string SurfaceCanvas = "SurfaceCanvas";     // window/page background
    public const string SurfaceCard = "SurfaceCard";         // card / raised panel
    public const string SurfaceSunken = "SurfaceSunken";     // wells, list background, inputs
    public const string SurfaceHover = "SurfaceHover";       // row/nav hover wash
    public const string SurfaceSelected = "SurfaceSelected"; // selected row / active nav
    public const string SurfaceOverlay = "SurfaceOverlay";   // floating pill (overlay)

    // Text — a four-step hierarchy, all AA-or-better on every surface above.
    public const string TextPrimary = "TextPrimary";     // body, labels (AAA)
    public const string TextSecondary = "TextSecondary"; // supporting text (AA)
    public const string TextMuted = "TextMuted";         // hints, captions (AA)
    public const string TextDisabled = "TextDisabled";   // disabled only (AA-exempt, held at 3:1)

    // Accent — the brand pink. Fill tokens carry dark ink, never white.
    public const string AccentFill = "AccentFill";           // primary button / active track
    public const string AccentFillHover = "AccentFillHover"; // hover
    public const string AccentFillPressed = "AccentFillPressed"; // pressed
    public const string OnAccent = "OnAccent";               // ink on AccentFill
    public const string AccentInk = "AccentInk";             // accent used AS text/focus
    public const string AccentSubtle = "AccentSubtle";       // tinted container

    // Lines and focus.
    public const string BorderSubtle = "BorderSubtle"; // decorative separators, card edge
    public const string BorderStrong = "BorderStrong"; // input/control boundary (1.4.11: 3:1)
    public const string FocusRing = "FocusRing";       // keyboard focus indicator (1.4.11)

    // Status — each has text (on neutral surfaces), surface (tinted container)
    // and on-colour (ink on that container).
    public const string ErrorText = "ErrorText";
    public const string ErrorSurface = "ErrorSurface";
    public const string OnError = "OnError";
    public const string WarningText = "WarningText";
    public const string WarningSurface = "WarningSurface";
    public const string OnWarning = "OnWarning";
    public const string SuccessText = "SuccessText";
    public const string SuccessSurface = "SuccessSurface";
    public const string OnSuccess = "OnSuccess";

    /// <summary>
    /// Light theme. Brand hue is <c>#DA5893</c> (Handy pink) used verbatim as
    /// the accent fill; it fails AA with white ink (3.62:1) but passes with
    /// near-black ink (5.11:1), so filled accents carry dark ink.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Color> Light =
        new Dictionary<string, Color>
        {
            [SurfaceCanvas] = Color.FromRgb(0xFA, 0xFA, 0xFA),
            [SurfaceCard] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            [SurfaceSunken] = Color.FromRgb(0xF4, 0xF4, 0xF5),
            [SurfaceHover] = Color.FromRgb(0xF1, 0xF1, 0xF3),
            [SurfaceSelected] = Color.FromRgb(0xFC, 0xE7, 0xF1),
            [SurfaceOverlay] = Color.FromRgb(0xFF, 0xFF, 0xFF),

            [TextPrimary] = Color.FromRgb(0x1A, 0x1A, 0x1A),
            [TextSecondary] = Color.FromRgb(0x5A, 0x5A, 0x5A),
            [TextMuted] = Color.FromRgb(0x68, 0x68, 0x6A),
            [TextDisabled] = Color.FromRgb(0x86, 0x86, 0x88),

            [AccentFill] = Color.FromRgb(0xDA, 0x58, 0x93),
            [AccentFillHover] = Color.FromRgb(0xCE, 0x4F, 0x89),
            [AccentFillPressed] = Color.FromRgb(0xCD, 0x53, 0x8A),
            [OnAccent] = Color.FromRgb(0x1A, 0x11, 0x14),
            [AccentInk] = Color.FromRgb(0xA6, 0x3A, 0x6A),
            [AccentSubtle] = Color.FromRgb(0xFC, 0xE7, 0xF1),

            [BorderSubtle] = Color.FromRgb(0xE6, 0xE6, 0xE8),
            [BorderStrong] = Color.FromRgb(0x86, 0x86, 0x8B),
            [FocusRing] = Color.FromRgb(0xA6, 0x3A, 0x6A),

            [ErrorText] = Color.FromRgb(0xB3, 0x26, 0x1E),
            [ErrorSurface] = Color.FromRgb(0xFC, 0xEB, 0xEA),
            [OnError] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            [WarningText] = Color.FromRgb(0x8A, 0x53, 0x00),
            [WarningSurface] = Color.FromRgb(0xFF, 0xF4, 0xE0),
            [OnWarning] = Color.FromRgb(0xFF, 0xFF, 0xFF),
            [SuccessText] = Color.FromRgb(0x1E, 0x6E, 0x3C),
            [SuccessSurface] = Color.FromRgb(0xE6, 0xF4, 0xEA),
            [OnSuccess] = Color.FromRgb(0xFF, 0xFF, 0xFF),
        };

    /// <summary>
    /// Dark theme. Not an inversion of the light theme: the canvas is a warm
    /// near-black (<c>#1B1A19</c>, never pure black), elevation steps <em>up</em>
    /// in lightness, white text is pulled back to <c>#F2F1EF</c> to avoid
    /// halation, and the accent is lightened to <c>#E08BB5</c> so the pink does
    /// not vibrate against the dark surfaces.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Color> Dark =
        new Dictionary<string, Color>
        {
            [SurfaceCanvas] = Color.FromRgb(0x1B, 0x1A, 0x19),
            [SurfaceCard] = Color.FromRgb(0x23, 0x22, 0x21),
            [SurfaceSunken] = Color.FromRgb(0x15, 0x14, 0x13),
            [SurfaceHover] = Color.FromRgb(0x2E, 0x2D, 0x2B),
            [SurfaceSelected] = Color.FromRgb(0x3A, 0x28, 0x31),
            [SurfaceOverlay] = Color.FromRgb(0x2A, 0x29, 0x27),

            [TextPrimary] = Color.FromRgb(0xF2, 0xF1, 0xEF),
            [TextSecondary] = Color.FromRgb(0xBA, 0xB8, 0xB4),
            [TextMuted] = Color.FromRgb(0x9A, 0x98, 0x94),
            [TextDisabled] = Color.FromRgb(0x80, 0x7E, 0x7A),

            [AccentFill] = Color.FromRgb(0xE0, 0x8B, 0xB5),
            [AccentFillHover] = Color.FromRgb(0xE8, 0x9C, 0xC0),
            [AccentFillPressed] = Color.FromRgb(0xD4, 0x79, 0x9F),
            [OnAccent] = Color.FromRgb(0x1A, 0x11, 0x14),
            [AccentInk] = Color.FromRgb(0xF0, 0xA6, 0xC8),
            [AccentSubtle] = Color.FromRgb(0x3A, 0x28, 0x31),

            [BorderSubtle] = Color.FromRgb(0x35, 0x33, 0x2F),
            [BorderStrong] = Color.FromRgb(0x78, 0x76, 0x6F),
            [FocusRing] = Color.FromRgb(0xF0, 0xA6, 0xC8),

            [ErrorText] = Color.FromRgb(0xFF, 0x8A, 0x80),
            [ErrorSurface] = Color.FromRgb(0x3A, 0x24, 0x22),
            [OnError] = Color.FromRgb(0x2A, 0x0F, 0x0D),
            [WarningText] = Color.FromRgb(0xFF, 0xC2, 0x4D),
            [WarningSurface] = Color.FromRgb(0x3A, 0x2E, 0x14),
            [OnWarning] = Color.FromRgb(0x2A, 0x20, 0x08),
            [SuccessText] = Color.FromRgb(0x6F, 0xD0, 0x8C),
            [SuccessSurface] = Color.FromRgb(0x1E, 0x33, 0x24),
            [OnSuccess] = Color.FromRgb(0x0C, 0x24, 0x13),
        };

    /// <summary>
    /// Deprecated Handy-era keys mapped onto their semantic replacement.
    ///
    /// The old palette had ten flat tokens; several were overloaded (one gray
    /// served hints, captions, footer text <em>and</em> disabled state). These
    /// aliases keep any not-yet-migrated consumer compiling and themed while
    /// views are converted to the semantic keys one by one. New code must not
    /// use them — <see cref="Theme.TokenAudit"/> asserts the alias list only
    /// ever shrinks.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyAliases =
        new Dictionary<string, string>
        {
            ["HandyText"] = TextPrimary,
            ["HandyMidGray"] = TextMuted,
            ["HandyBackground"] = SurfaceCanvas,
            ["HandyCardBackground"] = SurfaceCard,
            ["HandyCardBorder"] = BorderSubtle,
            ["HandySecondaryBackground"] = SurfaceSunken,
            ["HandyAccent"] = AccentFill,
            ["HandyLogoPrimary"] = AccentFill,
            ["HandyError"] = ErrorText,
            ["HandyWarning"] = WarningText,
        };
}