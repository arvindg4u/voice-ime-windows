using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// Bounded producer/consumer pump feeding <c>LiveSession.SendPcmAsync</c> via a
/// single-reader drain (serializing its callers, per the Task 1 review note).
/// The NAudio callback is the writer via <see cref="TryEnqueue"/>; the drain
/// loop is the sole reader. Queue-full fails the session safely (brief §10);
/// last-chunk-before-stop is guaranteed by Complete()-then-drain ordering
/// (brief §15). No logging.
/// </summary>
internal sealed class LivePcmPump : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Default bound: ~30 s of audio at 100 ms/chunk (matches Android pending cap).
    /// </summary>
    private const int DefaultCapacity = 300;

    private readonly Channel<byte[]> _channel;
    private bool _disposed;

    internal LivePcmPump(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// Non-blocking enqueue. Returns false when the queue is full or completed
    /// (FullMode.Wait + TryWrite: deterministic failure, no drop, no growth);
    /// the caller fails the session on false.
    /// </summary>
    internal bool TryEnqueue(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return _channel.Writer.TryWrite(chunk);
    }

    /// <summary>
    /// Single-reader drain: invokes <paramref name="send"/> for each queued chunk
    /// in order until <see cref="Complete"/> is called and the queue empties.
    /// </summary>
    internal async Task DrainAsync(Func<byte[], CancellationToken, Task> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);
        await foreach (var chunk in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await send(chunk, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops accepting chunks; the drain finishes already-queued chunks.
    /// Idempotent: subsequent <see cref="TryEnqueue"/> calls return false.
    /// </summary>
    internal void Complete()
    {
        _channel.Writer.Complete();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Complete() releases a reader blocked in DrainAsync once queued
        // chunks are consumed; best-effort, never throws (idempotent).
        try
        {
            Complete();
        }
        catch (ChannelClosedException)
        {
            // Already completed by an explicit Complete(); nothing to release.
        }
    }
}
