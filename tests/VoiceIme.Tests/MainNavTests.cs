using System.Linq;
using Xunit;

namespace VoiceIme.Tests;

public sealed class MainNavTests
{
    [Fact]
    public void Ordered_CoversEverySectionExactlyOnce()
    {
        var values = System.Enum.GetValues<MainSection>();

        Assert.Equal(values.Length, MainNav.Ordered.Count);
        Assert.Equal(values.Length, MainNav.Ordered.Distinct().Count());
        Assert.All(values, v => Assert.Contains(v, MainNav.Ordered));
    }

    [Fact]
    public void Ordered_MatchesPlanSidebarOrder()
    {
        var expected = new[]
        {
            MainSection.General,
            MainSection.History,
            MainSection.Gemini,
            MainSection.Advanced,
            MainSection.About,
        };

        Assert.True(MainNav.Ordered.SequenceEqual(expected));
    }

    [Theory]
    [InlineData(MainSection.General, "General")]
    [InlineData(MainSection.History, "History")]
    [InlineData(MainSection.Gemini, "Gemini")]
    [InlineData(MainSection.Advanced, "Advanced")]
    [InlineData(MainSection.About, "About")]
    public void LabelFor_ReturnsPlanName(MainSection section, string expected)
    {
        Assert.Equal(expected, MainNav.LabelFor(section));
    }

    [Fact]
    public void IconFor_ReturnsDistinctNonEmptyIcons()
    {
        var icons = MainNav.Ordered.Select(MainNav.IconFor).ToArray();

        Assert.All(icons, i => Assert.False(string.IsNullOrWhiteSpace(i)));
        Assert.Equal(icons.Length, icons.Distinct().Count());
    }

    [Fact]
    public void UnknownSection_FallsBackWithoutThrowing()
    {
        var unknown = (MainSection)99;

        Assert.Equal("99", MainNav.LabelFor(unknown));
        Assert.Equal("❓", MainNav.IconFor(unknown));
    }
}
