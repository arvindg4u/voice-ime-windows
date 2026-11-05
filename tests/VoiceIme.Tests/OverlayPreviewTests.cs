using Xunit;

namespace VoiceIme.Tests;

public sealed class OverlayPreviewTests
{
    [Theory]
    [InlineData("hello world", " this is", "hello world this is")]
    [InlineData("", "hello", "hello")]
    [InlineData("done", "", "done")]
    [InlineData("", "", "")]
    [InlineData("  spaced  ", "  out  ", "spaced out")]
    public void FormatPreview_JoinsFinalAndInterim(string finals, string interim, string expected) =>
        Assert.Equal(expected, OverlayState.FormatPreview(finals, interim));

    // The pill renders settled text and the hypothesis as two separate Runs so
    // they can be inked differently. The split must therefore preserve the
    // joined text exactly, or the preview would silently drop or duplicate a
    // space at the seam.

    [Theory]
    [InlineData("hello world", " this is")]
    [InlineData("", "hello")]
    [InlineData("done", "")]
    [InlineData("", "")]
    [InlineData("  spaced  ", "  out  ")]
    [InlineData("a", "b")]
    public void PreviewRuns_ConcatenateBackToTheJoinedPreview(string finals, string interim)
    {
        var (finalsRun, interimRun) = OverlayState.PreviewRuns(finals, interim);

        Assert.Equal(
            OverlayState.FormatPreview(finals, interim),
            finalsRun + interimRun);
    }

    [Fact]
    public void PreviewRuns_TrimsBothParts()
    {
        var (finalsRun, interimRun) = OverlayState.PreviewRuns("  keep  ", "  me  ");

        Assert.Equal("keep", finalsRun);
        Assert.Equal(" me", interimRun);
    }

    [Fact]
    public void PreviewRuns_InterimOnly_HasNoLeadingSpace()
    {
        // Nothing precedes it, so a leading space would show as a gap at the
        // start of the pill.
        var (finalsRun, interimRun) = OverlayState.PreviewRuns("", " hello");

        Assert.Equal(string.Empty, finalsRun);
        Assert.Equal("hello", interimRun);
    }

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("0123456789abc", 10, "3456789abc")]
    [InlineData("abc", 0, "")]
    public void TailWindow_KeepsTheLastCharacters(string input, int max, string expected) =>
        Assert.Equal(expected, OverlayState.TailWindow(input, max));

    [Fact]
    public void TailWindow_NegativeMax_YieldsEmptyNotAThrow()
    {
        // Slicing with a negative index would throw; the guard must catch it.
        Assert.Equal(string.Empty, OverlayState.TailWindow("something", -5));
    }

    [Fact]
    public void PreviewRuns_FinalsOnly_HasNoTrailingSpace()
    {
        var (finalsRun, interimRun) = OverlayState.PreviewRuns("done", "   ");

        Assert.Equal("done", finalsRun);
        Assert.Equal(string.Empty, interimRun);
    }
}
