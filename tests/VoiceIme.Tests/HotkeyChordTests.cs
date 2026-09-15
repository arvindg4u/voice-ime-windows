using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Hotkey parse/format helpers. Pure logic, headless-safe on any OS.
/// </summary>
public sealed class HotkeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Space", 0x0004 | 0x0002, 0x20)]
    [InlineData("ctrl+shift+space", 0x0004 | 0x0002, 0x20)]
    [InlineData("Shift+Ctrl+Space", 0x0004 | 0x0002, 0x20)]
    [InlineData("Alt+F4", 0x0001, 0x73)]
    [InlineData("Ctrl+Alt+Delete", 0x0002 | 0x0001, 0x2E)]
    [InlineData("Win+K", 0x0008, 'K')]
    [InlineData("Ctrl+A", 0x0002, 'A')]
    [InlineData("ctrl+a", 0x0002, 'A')]
    [InlineData("Shift+Enter", 0x0004, 0x0D)]
    [InlineData("Control+Shift+F12", 0x0002 | 0x0004, 0x7B)]
    public void TryParse_ValidChords_ReturnsModifiersAndKey(
        string chord, uint modifiers, uint vk)
    {
        Assert.True(HotkeyChord.TryParse(chord, out var actualMods, out var actualVk));
        Assert.Equal(modifiers, actualMods);
        Assert.Equal(vk, actualVk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Space")]
    [InlineData("A")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Space+Enter")]
    [InlineData("Ctrl+Bogus")]
    [InlineData("Ctrl++Space")]
    [InlineData("Ctrl+Space+")]
    [InlineData("Ctrl+*")]
    public void TryParse_InvalidChords_ReturnsFalse(string? chord)
    {
        Assert.False(HotkeyChord.TryParse(chord, out _, out _));
    }

    [Theory]
    [InlineData(0x0002 | 0x0004, 0x20, "Ctrl+Shift+Space")]
    [InlineData(0x0001, 0x73, "Alt+F4")]
    [InlineData(0x0008, (uint)'K', "Win+K")]
    [InlineData(0x0002 | 0x0001 | 0x0004 | 0x0008, (uint)'A', "Ctrl+Alt+Shift+Win+A")]
    public void Format_RoundTripsParsedChords(uint modifiers, uint vk, string expected)
    {
        Assert.Equal(expected, HotkeyChord.Format(modifiers, vk));
    }

    [Fact]
    public void Parse_Format_RoundTripsDefaultChord()
    {
        Assert.True(HotkeyChord.TryParse(HotkeyChord.DefaultChord, out var mods, out var vk));
        Assert.Equal(HotkeyChord.DefaultChord, HotkeyChord.Format(mods, vk));
    }

    [Theory]
    [InlineData("Esc", 0x1B)]
    [InlineData("esc", 0x1B)]
    [InlineData("Escape", 0x1B)]
    [InlineData("Space", 0x20)]
    [InlineData("A", 'A')]
    [InlineData("F5", 0x74)]
    [InlineData("Ctrl+Esc", 0x1B)]
    [InlineData("Alt+F4", 0x73)]
    public void TryParseWithBareKey_AcceptsBareKeyAndFullChord(string chord, uint vk)
    {
        Assert.True(HotkeyChord.TryParseWithBareKey(chord, out _, out var actualVk));
        Assert.Equal(vk, actualVk);
    }

    [Fact]
    public void TryParseWithBareKey_FullChord_ReturnsModifiers()
    {
        Assert.True(HotkeyChord.TryParseWithBareKey("Ctrl+Esc", out var mods, out _));
        Assert.Equal(HotkeyChord.ModControl, mods);
    }

    [Fact]
    public void TryParseWithBareKey_BareKey_ReturnsZeroModifiers()
    {
        Assert.True(HotkeyChord.TryParseWithBareKey("Esc", out var mods, out _));
        Assert.Equal(0u, mods);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("bogus")]
    [InlineData("Ctrl+Bogus")]
    public void TryParseWithBareKey_Invalid_ReturnsFalse(string? chord)
    {
        Assert.False(HotkeyChord.TryParseWithBareKey(chord, out _, out _));
    }

    [Fact]
    public void TryParse_StillRejectsBareKey_ForAlwaysOnHotkey()
    {
        // The dictation hotkey keeps TryParse: a bare Esc must never become a
        // global always-on registration — only the busy-armed cancel path may
        // use TryParseWithBareKey.
        Assert.False(HotkeyChord.TryParse("Esc", out _, out _));
    }

    [Theory]
    [InlineData("hold_or_toggle", true)]
    [InlineData("push_to_talk", true)]
    [InlineData("toggle", true)]
    [InlineData("hold", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("TOGGLE", false)]
    public void ActivationModes_IsValid_AcceptsOnlyKnownModes(string? mode, bool expected)
    {
        Assert.Equal(expected, ActivationModes.IsValid(mode));
    }

    [Theory]
    [InlineData(ActivationModes.HoldOrToggle, "Hold or toggle")]
    [InlineData(ActivationModes.PushToTalk, "Push to talk")]
    [InlineData(ActivationModes.Toggle, "Toggle")]
    [InlineData("bogus", "Toggle")]
    [InlineData(null, "Toggle")]
    public void ActivationModes_LabelRoundTrip(string? mode, string label)
    {
        Assert.Equal(label, ActivationModes.LabelFor(mode));
        Assert.Equal(mode switch
        {
            ActivationModes.HoldOrToggle => ActivationModes.HoldOrToggle,
            ActivationModes.PushToTalk => ActivationModes.PushToTalk,
            _ => ActivationModes.Toggle,
        }, ActivationModes.ModeForLabel(label));
    }
}
