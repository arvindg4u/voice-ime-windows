using Xunit;

namespace VoiceIme.Tests;

public sealed class FinalAccumulatorTests
{
    [Fact]
    public void Append_FirstFragment_ReturnsWhole()
    {
        var acc = new FinalAccumulator();
        Assert.Equal("hello world", acc.Append("hello world"));
        Assert.Equal("hello world", acc.Snapshot());
    }

    [Fact]
    public void Append_OverlappingResend_ReturnsOnlyDelta()
    {
        var acc = new FinalAccumulator();
        acc.Append("hello world");
        Assert.Equal(" this is", acc.Append("world this is"));
        Assert.Equal("hello world this is", acc.Snapshot());
    }

    [Fact]
    public void Append_ExactEcho_ReturnsEmpty()
    {
        var acc = new FinalAccumulator();
        acc.Append("hello world");
        Assert.Equal("", acc.Append("hello world"));
        Assert.Equal("hello world", acc.Snapshot());
    }

    [Fact]
    public void Append_DisjointFragment_Appends()
    {
        var acc = new FinalAccumulator();
        acc.Append("hello");
        Assert.Equal(" world", acc.Append(" world"));
    }

    [Theory]
    [InlineData("")]
    public void Append_Empty_ReturnsEmpty(string incoming)
    {
        var acc = new FinalAccumulator();
        Assert.Equal("", acc.Append(incoming));
        Assert.Equal("", acc.Snapshot());
    }

    [Fact]
    public void Append_CumulativeResend_Collapses()
    {
        var acc = new FinalAccumulator();
        Assert.Equal("hello world", acc.Append("hello world"));
        Assert.Equal("", acc.Append("hello world"));
        Assert.Equal("hello world", acc.Snapshot());
    }
}
