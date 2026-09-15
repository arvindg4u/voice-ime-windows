using System.Collections.Generic;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Overlay-mode vocabulary tests (Task 8). Pure — runs on any OS.
/// </summary>
public sealed class OverlayModesTests
{
    [Theory]
    [InlineData(OverlayModes.None)]
    [InlineData(OverlayModes.Minimal)]
    [InlineData(OverlayModes.Full)]
    public void IsValid_KnownModes_ReturnsTrue(string mode)
    {
        Assert.True(OverlayModes.IsValid(mode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("hologram")]
    [InlineData("FULL")]
    public void IsValid_OtherValues_ReturnsFalse(string? mode)
    {
        Assert.False(OverlayModes.IsValid(mode));
    }

    [Fact]
    public void LabelFor_MapsAllModes()
    {
        Assert.Equal("None", OverlayModes.LabelFor(OverlayModes.None));
        Assert.Equal("Minimal", OverlayModes.LabelFor(OverlayModes.Minimal));
        Assert.Equal("Full", OverlayModes.LabelFor(OverlayModes.Full));
    }

    [Fact]
    public void LabelFor_Unknown_CoercesToFull()
    {
        Assert.Equal("Full", OverlayModes.LabelFor("hologram"));
        Assert.Equal("Full", OverlayModes.LabelFor(null));
    }

    [Fact]
    public void ModeForLabel_MapsAllLabels()
    {
        Assert.Equal(OverlayModes.None, OverlayModes.ModeForLabel("None"));
        Assert.Equal(OverlayModes.Minimal, OverlayModes.ModeForLabel("Minimal"));
        Assert.Equal(OverlayModes.Full, OverlayModes.ModeForLabel("Full"));
    }

    [Fact]
    public void ModeForLabel_Unknown_CoercesToFull()
    {
        Assert.Equal(OverlayModes.Full, OverlayModes.ModeForLabel("Hologram"));
        Assert.Equal(OverlayModes.Full, OverlayModes.ModeForLabel(null));
    }

    [Fact]
    public void Coerce_ValidModes_PassesThrough()
    {
        Assert.Equal(OverlayModes.None, OverlayModes.Coerce(OverlayModes.None));
        Assert.Equal(OverlayModes.Minimal, OverlayModes.Coerce(OverlayModes.Minimal));
        Assert.Equal(OverlayModes.Full, OverlayModes.Coerce(OverlayModes.Full));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData(" TRUE ")]
    public void Coerce_LegacyTrue_MapsToFullSilently(string legacy)
    {
        var warnings = new List<string>();

        var mode = OverlayModes.Coerce(legacy, warnings.Add);

        Assert.Equal(OverlayModes.Full, mode);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData(" FALSE ")]
    public void Coerce_LegacyFalse_MapsToNoneSilently(string legacy)
    {
        var warnings = new List<string>();

        var mode = OverlayModes.Coerce(legacy, warnings.Add);

        Assert.Equal(OverlayModes.None, mode);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hologram")]
    public void Coerce_Unknown_CoercesToFullWithWarning(string? bad)
    {
        var warnings = new List<string>();

        var mode = OverlayModes.Coerce(bad, warnings.Add);

        Assert.Equal(OverlayModes.Full, mode);
        Assert.Single(warnings);
        Assert.Contains("showOverlay", warnings[0]);
    }

    [Fact]
    public void ShouldShowPill_NoneOnly_ReturnsFalse()
    {
        Assert.False(OverlayModes.ShouldShowPill(OverlayModes.None));
        Assert.True(OverlayModes.ShouldShowPill(OverlayModes.Minimal));
        Assert.True(OverlayModes.ShouldShowPill(OverlayModes.Full));
    }

    [Fact]
    public void ShouldShowWaveform_FullOnly_ReturnsTrue()
    {
        Assert.False(OverlayModes.ShouldShowWaveform(OverlayModes.None));
        Assert.False(OverlayModes.ShouldShowWaveform(OverlayModes.Minimal));
        Assert.True(OverlayModes.ShouldShowWaveform(OverlayModes.Full));
    }
}
