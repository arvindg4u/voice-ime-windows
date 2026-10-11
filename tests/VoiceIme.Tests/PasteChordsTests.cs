using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// SendInput chord-vocabulary tests (Task 8). Pure — runs on any OS; the
/// actual SendInput call stays in <see cref="NativeInput"/>.
/// </summary>
public sealed class PasteChordsTests
{
    [Fact]
    public void ForMethod_CtrlV_HoldsControlTapsV()
    {
        var chord = PasteChords.ForMethod(PasteMethods.CtrlV);

        Assert.Equal(PasteMethods.CtrlV, chord.Method);
        Assert.Equal(new ushort[] { PasteChords.VkControl }, chord.Modifiers);
        Assert.Equal(PasteChords.VkV, chord.Key);
    }

    [Fact]
    public void ForMethod_ShiftInsert_HoldsShiftTapsInsert()
    {
        var chord = PasteChords.ForMethod(PasteMethods.ShiftInsert);

        Assert.Equal(PasteMethods.ShiftInsert, chord.Method);
        Assert.Equal(new ushort[] { PasteChords.VkShift }, chord.Modifiers);
        Assert.Equal(PasteChords.VkInsert, chord.Key);
    }

    [Fact]
    public void ForMethod_CtrlShiftV_HoldsControlShiftTapsV()
    {
        var chord = PasteChords.ForMethod(PasteMethods.CtrlShiftV);

        Assert.Equal(PasteMethods.CtrlShiftV, chord.Method);
        Assert.Equal(
            new ushort[] { PasteChords.VkControl, PasteChords.VkShift },
            chord.Modifiers);
        Assert.Equal(PasteChords.VkV, chord.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("super-paste")]
    public void ForMethod_Unknown_CoercesToCtrlV(string? method)
    {
        var chord = PasteChords.ForMethod(method);

        Assert.Equal(PasteMethods.CtrlV, chord.Method);
        Assert.Equal(new ushort[] { PasteChords.VkControl }, chord.Modifiers);
        Assert.Equal(PasteChords.VkV, chord.Key);
    }

    [Theory]
    [InlineData(PasteChords.VkInsert, true)]   // Insert is an extended key
    [InlineData(0xA3, true)]                   // right Ctrl
    [InlineData(0xA5, true)]                   // right Alt
    [InlineData(0x5B, true)]                   // left Win
    [InlineData(PasteChords.VkV, false)]
    [InlineData(PasteChords.VkControl, false)] // generic/left Ctrl
    [InlineData(PasteChords.VkShift, false)]
    [InlineData(PasteChords.VkReturn, false)]  // main Enter (numpad Enter would be extended)
    public void IsExtendedKey_MatchesWin32ScanCodeRules(ushort vk, bool extended)
    {
        // Shift+Insert delivery depends on this: without KEYEVENTF_EXTENDEDKEY
        // the Insert scan code aliases the numpad 0 key.
        Assert.Equal(extended, NativeInput.IsExtendedKey(vk));
    }
}
