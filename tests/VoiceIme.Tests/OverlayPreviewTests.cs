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
}
