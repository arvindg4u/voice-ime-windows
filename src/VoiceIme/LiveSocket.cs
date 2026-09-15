using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace VoiceIme;

/// <summary>
/// Gemini Live WebSocket seam: <see cref="ILiveSocket"/> is the engine-facing
/// abstraction (scripted by fakes in tests), <see cref="ClientWebSocketLiveSocket"/>
/// is the thin <see cref="ClientWebSocket"/> wrapper used in production.
/// No logging anywhere in this file; exception messages never carry the URI.
/// </summary>
/// <remarks>
/// Decision record: <see cref="ClientWebSocket"/> chosen per plan §5 — its
/// managed framing is correct for this endpoint. Android's raw socket exists
/// only because OkHttp's writer demonstrably dropped the setup frame on that
/// stack (tcpdump-proven), which is not evidence against .NET's
/// <see cref="ClientWebSocket"/>. No new dependency.
/// </remarks>
internal sealed class LiveSocketException(string message, Exception? inner = null)
    : IOException(message, inner);

internal interface ILiveSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendTextAsync(string text, CancellationToken ct);
    // Next server text message; null on server close; throws LiveSocketException on failure.
    // OCE on cancellation passes through unwrapped.
    Task<string?> ReceiveTextAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}

internal interface ILiveSocketFactory
{
    ILiveSocket Create();
}

internal sealed class ClientWebSocketLiveSocket : ILiveSocket
{
    private const int ReceiveBufferSize = 8192;
    private readonly ClientWebSocket _socket = new();
    private bool _disposed;

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            await _socket.ConnectAsync(uri, ct);
        }
        catch (WebSocketException ex)
        {
            var (message, _) = LiveProtocol.MapHandshakeError(ex.Message);
            throw new LiveSocketException(message, ex);
        }
    }

    public async Task SendTextAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        catch (WebSocketException ex)
        {
            throw new LiveSocketException("Network error — check connection", ex);
        }
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ReceiveSegmentAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ReplyCloseAsync(ct);
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                while (!result.EndOfMessage)
                    result = await ReceiveSegmentAsync(buffer, ct);
                continue;
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_disposed)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(LiveProtocol.CloseGrace);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, linked.Token);
        }
        catch (Exception)
        {
            // Best-effort close handshake: fall through to Abort below.
        }
        _socket.Abort();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _socket.Abort();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<WebSocketReceiveResult> ReceiveSegmentAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        }
        catch (WebSocketException ex)
        {
            throw new LiveSocketException("Network error — check connection", ex);
        }
    }

    private async Task ReplyCloseAsync(CancellationToken ct)
    {
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
        }
        catch (Exception)
        {
            // Best-effort close reply: the server is already closing.
        }
    }
}

internal sealed class ClientWebSocketLiveSocketFactory : ILiveSocketFactory
{
    public ILiveSocket Create() => new ClientWebSocketLiveSocket();
}
