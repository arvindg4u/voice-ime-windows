using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace VoiceIme.Theme;

/// <summary>
/// WCAG 2.2 relative-luminance and contrast-ratio maths.
///
/// Kept free of WPF application state so it runs head-less (Linux CI included):
/// only <see cref="Color"/>, which is a plain struct.
/// </summary>
public static class Contrast
{
    /// <summary>WCAG 2.2 AA for normal-weight body text.</summary>
    public const double AaText = 4.5;

    /// <summary>WCAG 2.2 AA for large text (≥18pt / ≥14pt bold).</summary>
    public const double AaLargeText = 3.0;

    /// <summary>WCAG 2.2 AA for non-text content — UI components, focus indicators (1.4.11).</summary>
    public const double AaNonText = 3.0;

    /// <summary>WCAG 2.2 AAA for normal-weight body text.</summary>
    public const double AaaText = 7.0;

    /// <summary>
    /// Relative luminance per WCAG 2.2 §relative-luminance: undo the sRGB
    /// transfer function, then weight the primaries (0.2126 R, 0.7152 G,
    /// 0.0722 B). Alpha is ignored — callers composite first.
    /// </summary>
    public static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R))
             + (0.7152 * Channel(color.G))
             + (0.0722 * Channel(color.B));
    }

    /// <summary>
    /// Contrast ratio between two opaque colours, in the range 1.0–21.0.
    /// Order does not matter.
    /// </summary>
    public static double Ratio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var lighter = Math.Max(la, lb);
        var darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Composites a translucent foreground over an opaque background, returning
    /// the colour a user actually perceives. Required because several tokens
    /// are painted with alpha (e.g. a 20% border) and the contrast of the
    /// <em>rendered</em> result is what has to pass.
    /// </summary>
    public static Color Composite(Color foreground, Color background)
    {
        var a = foreground.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(foreground.R, background.R),
                             Mix(foreground.G, background.G),
                             Mix(foreground.B, background.B));
    }

    /// <summary>Formats a ratio for human-readable test failure output.</summary>
    public static string Format(double ratio) =>
        ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1";
}

/// <summary>
/// One contrast obligation the UI must satisfy: <paramref name="Foreground"/>
/// drawn on <paramref name="Background"/> must reach <paramref name="Minimum"/>.
/// </summary>
/// <param name="Id">Stable identifier used in test failure messages.</param>
/// <param name="Foreground">Palette token key of the ink.</param>
/// <param name="Background">Palette token key of the surface behind it.</param>
/// <param name="Minimum">Required ratio (use the <see cref="Contrast"/> constants).</param>
/// <param name="Rationale">Why this pair exists in the UI.</param>
public sealed record ContrastRule(
    string Id,
    string Foreground,
    string Background,
    double Minimum,
    string Rationale)
{
    public ContrastRule WithMinimum(double minimum) => this with { Minimum = minimum };
}

/// <summary>
/// The contrast contract: every foreground/surface combination the redesigned
/// UI actually paints, per theme.
///
/// This is deliberately exhaustive rather than a sample — a token that is
/// never checked is a token that drifts. When a new visual state is added
/// (say, a disabled danger button), add its pair here; the test then proves it
/// before it can ship.
/// </summary>
public static class ContrastContract
{
    /// <summary>Surfaces that text is ever drawn directly on.</summary>
    private static readonly string[] TextSurfaces =
    {
        Palette.SurfaceCanvas,
        Palette.SurfaceCard,
        Palette.SurfaceSunken,
        Palette.SurfaceHover,
        Palette.SurfaceSelected,
        Palette.SurfaceOverlay,
    };

    public static IReadOnlyList<ContrastRule> Rules { get; } = Build();

    private static List<ContrastRule> Build()
    {
        var rules = new List<ContrastRule>();

        // --- Text hierarchy on every surface it can land on -----------------
        var textLevels = new (string Token, double Min, string Why)[]
        {
            (Palette.TextPrimary, Contrast.AaaText, "body/label text"),
            (Palette.TextSecondary, Contrast.AaText, "supporting text"),
            (Palette.TextMuted, Contrast.AaText, "hints and captions"),
            // Disabled content is exempt from 1.4.3, but holding 3:1 keeps it
            // legible rather than invisible — the old palette rendered it at 1.25:1.
            (Palette.TextDisabled, Contrast.AaNonText, "disabled label"),
        };

        foreach (var surface in TextSurfaces)
        {
            foreach (var (token, min, why) in textLevels)
            {
                rules.Add(new ContrastRule(
                    $"{token}/{surface}", token, surface, min, why));
            }
        }

        // --- Accent fill carries dark ink ----------------------------------
        // The brand pink cannot carry white text (3.62:1); this is why the
        // filled-accent tokens pair with a near-black OnAccent.
        foreach (var fill in new[]
                 {
                     Palette.AccentFill,
                     Palette.AccentFillHover,
                     Palette.AccentFillPressed,
                 })
        {
            rules.Add(new ContrastRule(
                $"{Palette.OnAccent}/{fill}", Palette.OnAccent, fill,
                Contrast.AaText, "button label on accent fill"));
        }

        // --- Accent used as ink --------------------------------------------
        foreach (var surface in new[]
                 {
                     Palette.SurfaceCanvas,
                     Palette.SurfaceCard,
                     Palette.SurfaceSunken,
                     Palette.SurfaceSelected,
                     Palette.AccentSubtle,
                 })
        {
            rules.Add(new ContrastRule(
                $"{Palette.AccentInk}/{surface}", Palette.AccentInk, surface,
                Contrast.AaText, "accent text / selection"));
        }

        // --- Boundaries and focus (1.4.11 non-text contrast) ---------------
        foreach (var surface in new[]
                 {
                     Palette.SurfaceCanvas,
                     Palette.SurfaceCard,
                     Palette.SurfaceSunken,
                 })
        {
            rules.Add(new ContrastRule(
                $"{Palette.BorderStrong}/{surface}", Palette.BorderStrong, surface,
                Contrast.AaNonText, "input/control boundary"));
        }

        foreach (var surface in new[]
                 {
                     Palette.SurfaceCanvas,
                     Palette.SurfaceCard,
                     Palette.SurfaceSunken,
                     Palette.SurfaceHover,
                 })
        {
            rules.Add(new ContrastRule(
                $"{Palette.FocusRing}/{surface}", Palette.FocusRing, surface,
                Contrast.AaNonText, "keyboard focus ring"));
        }

        // --- Status: text on neutral, and ink on its own container ---------
        var statuses = new (string Text, string Surface, string On)[]
        {
            (Palette.ErrorText, Palette.ErrorSurface, Palette.OnError),
            (Palette.WarningText, Palette.WarningSurface, Palette.OnWarning),
            (Palette.SuccessText, Palette.SuccessSurface, Palette.OnSuccess),
        };

        foreach (var (text, surface, on) in statuses)
        {
            // SurfaceOverlay included: the dictation pill paints status text
            // directly on it.
            foreach (var backdrop in new[] { Palette.SurfaceCanvas, Palette.SurfaceCard, Palette.SurfaceOverlay })
            {
                rules.Add(new ContrastRule(
                    $"{text}/{backdrop}", text, backdrop, Contrast.AaText,
                    "status message on neutral surface"));
            }

            rules.Add(new ContrastRule(
                $"{text}/{surface}", text, surface, Contrast.AaText,
                "status text on its tinted container"));
            rules.Add(new ContrastRule(
                $"{on}/{text}", on, text, Contrast.AaText,
                "badge ink on status fill"));
        }

        return rules;
    }

    /// <summary>
    /// Every rule evaluated against one palette, returning the failures only.
    /// </summary>
    public static IReadOnlyList<string> Failures(
        IReadOnlyDictionary<string, Color> palette,
        string themeName)
    {
        var failures = new List<string>();

        foreach (var rule in Rules)
        {
            if (!palette.TryGetValue(rule.Foreground, out var fg))
            {
                failures.Add($"{themeName}: missing token '{rule.Foreground}' (rule {rule.Id})");
                continue;
            }

            if (!palette.TryGetValue(rule.Background, out var bg))
            {
                failures.Add($"{themeName}: missing token '{rule.Background}' (rule {rule.Id})");
                continue;
            }

            var rendered = Contrast.Composite(fg, bg);
            var ratio = Contrast.Ratio(rendered, bg);

            if (ratio + 1e-9 < rule.Minimum)
            {
                failures.Add(
                    $"{themeName}: {rule.Id} = {Contrast.Format(ratio)} " +
                    $"< required {Contrast.Format(rule.Minimum)} — {rule.Rationale}");
            }
        }

        return failures;
    }

    /// <summary>All failures across both palettes.</summary>
    public static IReadOnlyList<string> AllFailures() =>
        Failures(Palette.Light, "light")
            .Concat(Failures(Palette.Dark, "dark"))
            .ToList();
}