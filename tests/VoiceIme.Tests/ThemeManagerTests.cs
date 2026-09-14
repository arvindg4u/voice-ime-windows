using System;
using VoiceIme.Theme;
using Xunit;

namespace VoiceIme.Tests;

public sealed class ThemeManagerTests
{
    [Fact]
    public void ResolveTheme_Light_ReturnsLight_RegardlessOfSystem()
    {
        WithSystemDarkMode(true, () =>
            Assert.Equal(HandyTheme.Light, ThemeManager.ResolveTheme("light")));
        WithSystemDarkMode(false, () =>
            Assert.Equal(HandyTheme.Light, ThemeManager.ResolveTheme("light")));
    }

    [Fact]
    public void ResolveTheme_Dark_ReturnsDark_RegardlessOfSystem()
    {
        WithSystemDarkMode(false, () =>
            Assert.Equal(HandyTheme.Dark, ThemeManager.ResolveTheme("dark")));
        WithSystemDarkMode(true, () =>
            Assert.Equal(HandyTheme.Dark, ThemeManager.ResolveTheme("dark")));
    }

    [Fact]
    public void ResolveTheme_System_FollowsOsSetting()
    {
        WithSystemDarkMode(false, () =>
            Assert.Equal(HandyTheme.Light, ThemeManager.ResolveTheme("system")));
        WithSystemDarkMode(true, () =>
            Assert.Equal(HandyTheme.Dark, ThemeManager.ResolveTheme("system")));
    }

    [Theory]
    [InlineData("sepia")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveTheme_InvalidOrMissing_FallsBackToSystem(string? preference)
    {
        WithSystemDarkMode(false, () =>
            Assert.Equal(HandyTheme.Light, ThemeManager.ResolveTheme(preference)));
        WithSystemDarkMode(true, () =>
            Assert.Equal(HandyTheme.Dark, ThemeManager.ResolveTheme(preference)));
    }

    [Theory]
    [InlineData("Light", HandyTheme.Light)]
    [InlineData("DARK", HandyTheme.Dark)]
    [InlineData(" System ", HandyTheme.Light)]
    public void ResolveTheme_IsCaseAndWhitespaceInsensitive(string preference, HandyTheme expected)
    {
        WithSystemDarkMode(false, () =>
            Assert.Equal(expected, ThemeManager.ResolveTheme(preference)));
    }

    private static void WithSystemDarkMode(bool isDark, Action test)
    {
        var previous = ThemeManager.SystemDarkModeProvider;
        ThemeManager.SystemDarkModeProvider = () => isDark;
        try
        {
            test();
        }
        finally
        {
            ThemeManager.SystemDarkModeProvider = previous;
        }
    }
}
