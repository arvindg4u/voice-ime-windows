using System;
using System.Linq;
using System.Reflection;
using VoiceIme.Theme;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Guards the non-colour token scales.
///
/// These are cheap invariants, but they catch the two ways a design system rots:
/// a metric creeping outside the documented scale, and a value that is
/// technically valid but unusable (a control shorter than the legal pointer
/// target, a font size below the Windows legibility floor).
/// </summary>
public sealed class DesignTokenTests
{
    /// <summary>The only spacing values the UI may use.</summary>
    private static readonly double[] Scale = { 4, 8, 12, 16, 24, 32 };

    [Fact]
    public void SpacingScale_UsesOnlyDocumentedSteps()
    {
        var spacings = new[]
        {
            DesignTokens.Space1, DesignTokens.Space2, DesignTokens.Space3,
            DesignTokens.Space4, DesignTokens.Space5, DesignTokens.Space6,
        };

        Assert.All(spacings, s => Assert.Contains(s, Scale));
        Assert.Equal(spacings.Length, spacings.Distinct().Count());
    }

    [Theory]
    [InlineData(nameof(DesignTokens.ControlHeight))]
    [InlineData(nameof(DesignTokens.ControlHeightLarge))]
    [InlineData(nameof(DesignTokens.MinTouchTarget))]
    [InlineData(nameof(DesignTokens.SettingsRowMinHeight))]
    [InlineData(nameof(DesignTokens.SidebarWidth))]
    [InlineData(nameof(DesignTokens.ContentMaxWidth))]
    public void MetricTokens_AreNamedPublicConstants(string name)
    {
        var field = typeof(DesignTokens).GetField(
            name, BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.IsLiteral, $"{name} must be a const so XAML can consume it.");
    }

    /// <summary>
    /// WCAG 2.5.8 sets 24x24 CSS px as the minimum pointer target. The touch
    /// target token must be at least that, and the overlay's cancel button was
    /// historically 22px — smaller than the legal minimum.
    /// </summary>
    [Fact]
    public void MinimumTouchTarget_MeetsWcagMinimum()
    {
        Assert.True(DesignTokens.MinTouchTarget >= 24,
            $"Minimum pointer target {DesignTokens.MinTouchTarget} is below WCAG 2.5.8's 24.");
    }

    /// <summary>
    /// Windows guidance: nothing smaller than 12px regular is legible in every
    /// language. The ramp must not go below it.
    /// </summary>
    [Fact]
    public void TypeRamp_NeverGoesBelowLegibilityFloor()
    {
        var sizes = new[]
        {
            DesignTokens.FontCaption, DesignTokens.FontBody, DesignTokens.FontBodyStrong,
            DesignTokens.FontTitle, DesignTokens.FontDisplay,
        };

        Assert.All(sizes, s => Assert.True(s >= 12, $"Font size {s} is below the 12px floor."));
    }

    [Fact]
    public void TypeRamp_IsMonotonicallyIncreasing()
    {
        var ascending = new[]
        {
            DesignTokens.FontCaption,
            DesignTokens.FontBody,
            DesignTokens.FontBodyStrong,
            DesignTokens.FontTitle,
            DesignTokens.FontDisplay,
        };

        for (var i = 1; i < ascending.Length; i++)
        {
            Assert.True(ascending[i] > ascending[i - 1],
                $"Type ramp is out of order at index {i}: {ascending[i - 1]} then {ascending[i]}.");
        }
    }

    /// <summary>
    /// Radii follow the Windows 11 three-step model (4 / 8 / 12). A fourth
    /// arbitrary radius is how a UI starts to look hand-drawn.
    /// </summary>
    [Fact]
    public void Radii_FollowWindowsThreeStepModel()
    {
        Assert.Equal(4, DesignTokens.RadiusSmall);
        Assert.Equal(8, DesignTokens.RadiusMedium);
        Assert.Equal(12, DesignTokens.RadiusLarge);
    }

    /// <summary>
    /// Segoe UI Variable is the Windows 11 system font; the stack must degrade
    /// to Segoe UI so Windows 10 still resolves something sane.
    /// </summary>
    [Fact]
    public void FontStack_PrefersUiVariableAndFallsBackToSegoeUi()
    {
        Assert.Contains("Segoe UI Variable", DesignTokens.FontFamily, StringComparison.Ordinal);
        Assert.Contains("Segoe UI", DesignTokens.FontFamily, StringComparison.Ordinal);
    }

    /// <summary>
    /// Settings rows must be tall enough for a 32px control plus padding,
    /// otherwise the control clips on a 1.25x DPI setting.
    /// </summary>
    [Fact]
    public void SettingsRow_AccommodatesTheStandardControl()
    {
        Assert.True(DesignTokens.SettingsRowMinHeight >= DesignTokens.ControlHeight + 16,
            "A settings row must fit a standard control plus vertical padding.");
    }
}