using System;
using System.IO;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Autostart shell-helper tests (Task 8). Pure — the folder is injected, so
/// tests point at a throwaway path and never near the real Startup folder.
/// </summary>
public sealed class StartupShellTests
{
    [Fact]
    public void ShortcutPath_CombinesFolderAndFileName()
    {
        var folder = Path.Combine("C:", "fake-startup");

        var path = StartupShell.ShortcutPath(folder);

        Assert.Equal(
            Path.Combine(folder, StartupShell.ShortcutFileName),
            path);
        Assert.Equal("Voice IME.lnk", StartupShell.ShortcutFileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShortcutPath_BlankFolder_Throws(string? folder)
    {
        Assert.Throws<ArgumentException>(() => StartupShell.ShortcutPath(folder!));
    }

    [Theory]
    [InlineData(true, false, StartupAction.Create)]
    [InlineData(true, true, StartupAction.None)]
    [InlineData(false, true, StartupAction.Remove)]
    [InlineData(false, false, StartupAction.None)]
    public void DecideReconcile_CoversAllCombinations(
        bool autostart, bool shortcutExists, StartupAction expected)
    {
        Assert.Equal(expected, StartupShell.DecideReconcile(autostart, shortcutExists));
    }

    [Fact]
    public void ExePathForShortcut_ProcessPath_Wins()
    {
        Assert.Equal(
            @"C:\app\VoiceIme.exe",
            StartupShell.ExePathForShortcut(@"C:\app\VoiceIme.exe", @"C:\other"));
    }

    [Fact]
    public void ExePathForShortcut_BlankProcessPath_FallsBackToBaseDir()
    {
        Assert.Equal(
            Path.Combine("base", StartupShell.ExeFileName),
            StartupShell.ExePathForShortcut("   ", "base"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExePathForShortcut_BlankBaseDir_Throws(string? baseDir)
    {
        Assert.Throws<ArgumentException>(
            () => StartupShell.ExePathForShortcut(null, baseDir!));
    }

    // --- Launch visibility (double-click must always show something) ---

    [Fact]
    public void ShouldShowWindowAtLaunch_DefaultLaunch_ShowsWindow()
    {
        Assert.True(StartupShell.ShouldShowWindowAtLaunch(
            startHidden: false, args: Array.Empty<string>()));
        Assert.True(StartupShell.ShouldShowWindowAtLaunch(
            startHidden: false, args: null));
    }

    [Fact]
    public void ShouldShowWindowAtLaunch_StartHiddenSetting_StaysInTray()
    {
        Assert.False(StartupShell.ShouldShowWindowAtLaunch(
            startHidden: true, args: Array.Empty<string>()));
    }

    [Theory]
    [InlineData("--hidden")]
    [InlineData("--HIDDEN")]
    [InlineData("/hidden")]
    [InlineData("-hidden")]
    [InlineData("--tray")]
    [InlineData("/tray")]
    [InlineData("--minimized")]
    [InlineData("/minimized")]
    [InlineData("--background")]
    [InlineData("  --hidden  ")]
    public void ShouldShowWindowAtLaunch_HiddenArg_StaysInTray(string arg)
    {
        Assert.False(StartupShell.ShouldShowWindowAtLaunch(
            startHidden: false, args: new[] { arg }));
        Assert.True(StartupShell.IsHiddenLaunchArg(arg));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("hidden")]
    [InlineData("")]
    [InlineData(null)]
    public void IsHiddenLaunchArg_UnrelatedArgs_AreVisible(string? arg)
    {
        Assert.False(StartupShell.IsHiddenLaunchArg(arg));
        Assert.True(StartupShell.ShouldShowWindowAtLaunch(
            startHidden: false,
            args: arg is null ? null : new[] { arg }));
    }

    [Fact]
    public void HiddenLaunchArg_IsTheCanonicalShortcutArgument()
    {
        Assert.Equal("--hidden", StartupShell.HiddenLaunchArg);
        Assert.True(StartupShell.IsHiddenLaunchArg(StartupShell.HiddenLaunchArg));
    }
}
