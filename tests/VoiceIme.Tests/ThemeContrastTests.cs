using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using VoiceIme.Theme;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Locks the accessibility floor of the design system.
///
/// These tests are the reason the palette cannot silently regress: any change
/// to a token value, or to the set of pairs the UI renders, must keep every
/// rule in <see cref="ContrastContract"/> at or above its WCAG 2.2 threshold.
///
/// Everything here is pure maths over <see cref="Color"/> structs, so it runs
/// on every OS — including headless Linux CI, unlike the WPF-constructing
/// tests.
/// </summary>
public sealed class ThemeContrastTests
{
    [Fact]
    public void LightPalette_SatisfiesEveryContrastRule()
    {
        var failures = ContrastContract.Failures(Palette.Light, "light");

        Assert.True(failures.Count == 0,
            "Light palette breaks the contrast contract:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DarkPalette_SatisfiesEveryContrastRule()
    {
        var failures = ContrastContract.Failures(Palette.Dark, "dark");

        Assert.True(failures.Count == 0,
            "Dark palette breaks the contrast contract:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// A key that exists in one palette but not the other leaves the *previous*
    /// theme's brush in the dictionary after a live swap — the stale-colour bug.
    /// Both palettes must define exactly the same keys.
    /// </summary>
    [Fact]
    public void BothPalettes_DefineExactlyTheSameKeys()
    {
        var light = Palette.Light.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var dark = Palette.Dark.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(light, dark);
    }

    /// <summary>
    /// Every legacy alias must point at a token that actually exists, in both
    /// palettes — otherwise the alias silently renders as an unresolved
    /// resource (invisible/black text) in one theme only.
    /// </summary>
    [Fact]
    public void LegacyAliases_ResolveInBothPalettes()
    {
        foreach (var (legacyKey, semanticKey) in Palette.LegacyAliases)
        {
            Assert.True(Palette.Light.ContainsKey(semanticKey),
                $"Light palette is missing '{semanticKey}' (alias target of '{legacyKey}').");
            Assert.True(Palette.Dark.ContainsKey(semanticKey),
                $"Dark palette is missing '{semanticKey}' (alias target of '{legacyKey}').");
        }
    }

    /// <summary>
    /// The alias list only ever shrinks. Bump this number DOWN when a view is
    /// migrated off a Handy* key; never up.
    /// </summary>
    [Fact]
    public void LegacyAliasCountOnlyShrinks()
    {
        // Was 10 flat Handy* tokens before the semantic rewrite.
        Assert.True(Palette.LegacyAliases.Count <= 10,
            $"Legacy alias list grew to {Palette.LegacyAliases.Count}. " +
            "New code must use semantic tokens; aliases are for migration only.");
    }

    // -----------------------------------------------------------------
    // The maths itself — proven against the published WCAG reference values.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 21.0)]  // black on white
    [InlineData(0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 21.0)]  // and the reverse
    [InlineData(0x80, 0x80, 0x80, 0xFF, 0xFF, 0xFF, 3.949)] // ~3.95:1 mid grey
    [InlineData(0xDA, 0x58, 0x93, 0xFF, 0xFF, 0xFF, 3.623)] // brand pink on white
    public void Ratio_MatchesKnownWcagValues(
        byte fr, byte fg, byte fb, byte br, byte bg, byte bb, double expected)
    {
        var foreground = Color.FromRgb(fr, fg, fb);
        var background = Color.FromRgb(br, bg, bb);

        Assert.Equal(expected, Contrast.Ratio(foreground, background), 2);
    }

    [Fact]
    public void Ratio_IsSymmetric()
    {
        var a = Color.FromRgb(0x1A, 0x1A, 0x1A);
        var b = Color.FromRgb(0xFA, 0xFA, 0xFA);

        Assert.Equal(Contrast.Ratio(a, b), Contrast.Ratio(b, a), 10);
    }

    [Fact]
    public void Composite_OverOpaqueBackground_ReturnsForegroundWhenFullyOpaque()
    {
        var fg = Color.FromArgb(0xFF, 0xDA, 0x58, 0x93);
        var bg = Color.FromRgb(0xFF, 0xFF, 0xFF);

        var result = Contrast.Composite(fg, bg);

        Assert.Equal(fg.R, result.R);
        Assert.Equal(fg.G, result.G);
        Assert.Equal(fg.B, result.B);
    }

    [Fact]
    public void Composite_TransparentForeground_ReturnsBackground()
    {
        var fg = Color.FromArgb(0x00, 0xDA, 0x58, 0x93);
        var bg = Color.FromRgb(0x12, 0x34, 0x56);

        var result = Contrast.Composite(fg, bg);

        Assert.Equal(bg.R, result.R);
        Assert.Equal(bg.G, result.G);
        Assert.Equal(bg.B, result.B);
    }

    /// <summary>
    /// A translucent token must be measured *as rendered*. This is the bug
    /// class that made the old 20%-alpha border 1.25:1 against its own card.
    /// </summary>
    [Fact]
    public void Composite_HalfAlpha_BlendsTowardBackground()
    {
        var fg = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF); // 50% white
        var bg = Color.FromRgb(0x00, 0x00, 0x00);       // black

        var result = Contrast.Composite(fg, bg);

        Assert.InRange(result.R, 0x7F, 0x81);
    }

    /// <summary>
    /// Guards the historical failure directly: white ink on the brand pink is
    /// below AA, which is why AccentFill pairs with OnAccent instead.
    /// </summary>
    [Fact]
    public void WhiteOnBrandPink_WouldFailAa_WhichIsWhyOnAccentExists()
    {
        var pink = Palette.Light[Palette.AccentFill];
        var white = Color.FromRgb(0xFF, 0xFF, 0xFF);

        Assert.True(Contrast.Ratio(white, pink) < Contrast.AaText,
            "If brand pink ever carries white text at AA, OnAccent can be revisited.");

        var onAccent = Palette.Light[Palette.OnAccent];
        Assert.True(Contrast.Ratio(onAccent, pink) >= Contrast.AaText);
    }

    /// <summary>
    /// Every token in both palettes must be present and opaque-or-intentional;
    /// a fully transparent token would silently delete a UI element.
    /// </summary>
    [Fact]
    public void NoTokenIsFullyTransparent()
    {
        foreach (var (theme, palette) in new[]
                 {
                     ("light", (IReadOnlyDictionary<string, Color>)Palette.Light),
                     ("dark", Palette.Dark),
                 })
        {
            foreach (var (key, color) in palette)
            {
                Assert.True(color.A > 0,
                    $"{theme} token '{key}' is fully transparent (A=0) — it would render as nothing.");
            }
        }
    }
}