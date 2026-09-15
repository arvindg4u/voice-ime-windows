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
}
