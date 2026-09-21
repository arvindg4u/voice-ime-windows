using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace VoiceIme;

/// <summary>
/// Bounded producer/consumer pump feeding <c>LiveSession.SendPcmAsync</c> via a
/// single send drain (serialization is preserved; disposal may concurrently
/// clear queued buffers during cancellation).
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
    private int _disposed;

    internal LivePcmPump(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            // Disposal can race cancellation with the drain's reader while
            // queued buffers are being cleared; allow that cleanup reader.
            SingleReader = false,
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
        try
        {
            await foreach (var chunk in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await send(chunk, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Audio is sensitive and the channel owns this buffer after
                    // enqueue, including when a socket send/cancellation fails.
                    Array.Clear(chunk, 0, chunk.Length);
                }
            }
        }
        finally
        {
            while (_channel.Reader.TryRead(out var queued))
            {
                Array.Clear(queued, 0, queued.Length);
            }
        }
    }

    /// <summary>
    /// Stops accepting chunks; the drain finishes already-queued chunks.
    /// Idempotent: subsequent <see cref="TryEnqueue"/> calls return false.
    /// </summary>
    internal void Complete()
    {
        // TryComplete is idempotent and does not throw when teardown paths
        // race (normal stop, failure abort, and cancellation can all call it).
        _channel.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Complete() releases a reader blocked in DrainAsync once queued
        // chunks are consumed; best-effort, never throws (idempotent).
        try
        {
            Complete();
        }
        catch (ChannelClosedException)
        {
            // Already completed by an explicit Complete(); still drain below.
        }

        while (_channel.Reader.TryRead(out var chunk))
        {
            Array.Clear(chunk, 0, chunk.Length);
        }
    }
}
