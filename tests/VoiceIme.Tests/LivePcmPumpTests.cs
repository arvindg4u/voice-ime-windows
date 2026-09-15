using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

public sealed class LivePcmPumpTests
{
    [Fact]
    public async Task Drain_PreservesOrder()
    {
        using var pump = new LivePcmPump(capacity: 8);
        Assert.True(pump.TryEnqueue([1]));
        Assert.True(pump.TryEnqueue([2]));
        Assert.True(pump.TryEnqueue([3]));
        pump.Complete();

        var seen = new List<byte>();
        await pump.DrainAsync((chunk, _) => { seen.Add(chunk[0]); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public void TryEnqueue_FullQueue_ReturnsFalse()
    {
        using var pump = new LivePcmPump(capacity: 2);
        Assert.True(pump.TryEnqueue([1]));
        Assert.True(pump.TryEnqueue([2]));
        Assert.False(pump.TryEnqueue([3])); // bounded: deterministic failure, no growth
    }

    [Fact]
    public void TryEnqueue_AfterComplete_ReturnsFalse()
    {
        using var pump = new LivePcmPump();
        pump.Complete();
        Assert.False(pump.TryEnqueue([1]));
    }

    [Fact]
    public async Task Drain_CancelledToken_StopsPromptly()
    {
        using var pump = new LivePcmPump();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pump.DrainAsync((_, _) => Task.CompletedTask, cts.Token));
    }

    [Fact]
    public async Task Drain_EmptyCompleted_CompletesImmediately()
    {
        using var pump = new LivePcmPump();
        pump.Complete();
        var calls = 0;
        await pump.DrainAsync((_, _) => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, calls);
    }
}
