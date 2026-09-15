using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

internal sealed class FakeLiveSocket : ILiveSocket
{
    internal sealed class CloseSignal { public static readonly CloseSignal Instance = new(); }
    private readonly Queue<object> _inbound = new();
    private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly List<string> Sent = new();
    public readonly List<Uri> ConnectedTo = new();
    public bool Disposed { get; private set; }
    public bool CloseRequested { get; private set; }

    public void EnqueueText(string text) { lock (_inbound) _inbound.Enqueue(text); _gate.TrySetResult(); }
    public void EnqueueFailure(Exception ex) { lock (_inbound) _inbound.Enqueue(ex); _gate.TrySetResult(); }
    public void EnqueueClose() { lock (_inbound) _inbound.Enqueue(CloseSignal.Instance); _gate.TrySetResult(); }

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        ConnectedTo.Add(uri);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (Sent) Sent.Add(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (_inbound)
            {
                if (_inbound.Count > 0)
                {
                    var next = _inbound.Dequeue();
                    if (next is CloseSignal) return null;
                    if (next is Exception ex) throw ex;
                    return (string)next;
                }
                wait = _gate.Task;
            }
            await wait.WaitAsync(ct);
        }
    }

    public Task CloseAsync(CancellationToken ct) { CloseRequested = true; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class FakeLiveSocketFactory : ILiveSocketFactory
{
    public readonly List<FakeLiveSocket> Created = new();
    public Func<FakeLiveSocket> Build = () => new FakeLiveSocket();
    public ILiveSocket Create() { var s = Build(); Created.Add(s); return s; }
}

public sealed class LiveSocketTests
{
    [Fact]
    public async Task FakeLiveSocket_ScriptedDialogue_ReplaysInOrder()
    {
        var fake = new FakeLiveSocket();
        fake.EnqueueText("""{"setupComplete":{}}""");
        fake.EnqueueClose();

        await fake.ConnectAsync(new Uri("wss://example.com/x?key=k"), CancellationToken.None);
        await fake.SendTextAsync("hello", CancellationToken.None);
        Assert.Equal("""{"setupComplete":{}}""", await fake.ReceiveTextAsync(CancellationToken.None));
        Assert.Null(await fake.ReceiveTextAsync(CancellationToken.None));
        Assert.Equal(["hello"], fake.Sent);
    }

    [Fact]
    public async Task FakeLiveSocket_CancelledReceive_ThrowsOperationCanceled()
    {
        var fake = new FakeLiveSocket();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.ReceiveTextAsync(cts.Token));
    }

    [Fact]
    public void LiveSocketException_IsIOException_WithSafeMessage()
    {
        var ex = new LiveSocketException("Network error — check connection");
        Assert.IsAssignableFrom<System.IO.IOException>(ex);
    }

    [Fact]
    public void ClientWebSocketLiveSocket_ToString_DoesNotLeak()
    {
        // Default Object.ToString carries only the type name — assert no override leaks URIs.
        var socket = new ClientWebSocketLiveSocket();
        Assert.DoesNotContain("wss://", socket.ToString());
    }
}
