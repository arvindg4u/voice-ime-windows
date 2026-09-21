using Xunit;

namespace VoiceIme.Tests;

public sealed class LiveSessionGuardTests
{
    [Fact]
    public void Next_ReturnsMonotonicIdsFromOne()
    {
        var guard = new LiveSessionGuard();
        Assert.Equal(1, guard.Next());
        Assert.Equal(2, guard.Next());
    }

    [Fact]
    public void IsCurrent_OnlyLatestSession()
    {
        var guard = new LiveSessionGuard();
        var first = guard.Next();
        var second = guard.Next();
        Assert.False(guard.IsCurrent(first));
        Assert.True(guard.IsCurrent(second));
        Assert.False(guard.IsCurrent(0));
    }

    [Fact]
    public void IsCurrent_CapturedId_BeatsMutableRead()
    {
        // The regression this guard exists for: callbacks must capture the id
        // at start, never read a counter at fire time.
        var guard = new LiveSessionGuard();
        var captured = guard.Next();
        guard.Next();
        Assert.False(guard.IsCurrent(captured));
    }

    [Fact]
    public void Invalidate_CurrentSession_RejectsLateCallbacks()
    {
        var guard = new LiveSessionGuard();
        var ended = guard.Next();

        guard.Invalidate(ended);

        Assert.False(guard.IsCurrent(ended));
        Assert.Equal(3, guard.Next());
    }

    [Fact]
    public void Invalidate_OldSession_DoesNotRetireNewSession()
    {
        var guard = new LiveSessionGuard();
        var old = guard.Next();
        var current = guard.Next();

        guard.Invalidate(old);

        Assert.True(guard.IsCurrent(current));
    }
}
