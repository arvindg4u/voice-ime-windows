using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Tray tooltip + never-strand-invisible guard tests (Task 8). Pure — the
/// tooltip strings come from <see cref="App.TrayText"/>/
/// <see cref="App.TruncateTrayText"/>, which already run any-OS.
/// </summary>
public sealed class TrayStateTextTests
{
    [Fact]
    public void TooltipFor_Idle_MatchesHotkeyTooltip()
    {
        const string hotkey = "Ctrl+Alt+V";

        Assert.Equal(App.TrayText(hotkey), TrayStateText.TooltipFor(DictationState.Idle, hotkey));
    }

    [Fact]
    public void TooltipFor_Recording_ReportsBusy()
    {
        var tooltip = TrayStateText.TooltipFor(DictationState.Recording, "Ctrl+Alt+V");

        Assert.Equal("Voice IME — recording… tap hotkey to stop", tooltip);
    }

    [Fact]
    public void TooltipFor_Uploading_ReportsBusy()
    {
        var tooltip = TrayStateText.TooltipFor(DictationState.Uploading, "Ctrl+Alt+V");

        Assert.Equal("Voice IME — transcribing…", tooltip);
    }

    [Fact]
    public void TooltipFor_ErrorWithDetail_CarriesSafeMessage()
    {
        var tooltip = TrayStateText.TooltipFor(DictationState.Error, "Ctrl+Alt+V", "mic unavailable");

        Assert.Equal("Voice IME — mic unavailable", tooltip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TooltipFor_ErrorWithoutDetail_FallsBackToGeneric(string? detail)
    {
        var tooltip = TrayStateText.TooltipFor(DictationState.Error, "Ctrl+Alt+V", detail);

        Assert.Equal("Voice IME — error", tooltip);
    }

    [Fact]
    public void TooltipFor_LongDetail_StaysWithinTooltipCap()
    {
        var tooltip = TrayStateText.TooltipFor(
            DictationState.Error, "Ctrl+Alt+V", new string('x', 200));

        Assert.True(tooltip.Length <= App.TrayTextLimit);
    }

    [Theory]
    [InlineData(true, true, TrayGuardAction.None)]
    [InlineData(true, false, TrayGuardAction.None)]
    [InlineData(false, true, TrayGuardAction.None)]
    [InlineData(false, false, TrayGuardAction.ShowWindow)]
    public void ResolveTrayGuard_OnlyHiddenIconPlusHiddenWindow_ForcesWindow(
        bool showTrayIcon, bool windowVisible, TrayGuardAction expected)
    {
        Assert.Equal(expected, TrayStateText.ResolveTrayGuard(showTrayIcon, windowVisible));
    }
}
