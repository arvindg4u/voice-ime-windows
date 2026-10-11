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

    /// <summary>
    /// Close code observed when <see cref="ReceiveTextAsync"/> returned null
    /// (server-initiated close), else null. Default interface members so
    /// scripted fakes keep compiling unchanged.
    /// </summary>
    int? CloseCode => null;

    /// <summary>Close reason paired with <see cref="CloseCode"/>; never contains the key.</summary>
    string? CloseReason => null;
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

    /// <inheritdoc />
    public int? CloseCode { get; private set; }

    /// <inheritdoc />
    public string? CloseReason { get; private set; }

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        // Best practice (.NET 7+): collect the upgrade response so a rejected
        // handshake reports its real HTTP status (401/403 invalid key, 429
        // rate limit, …) instead of being guessed from exception text. An
        // instant 403 must surface as "Invalid API key", never as a timeout.
        _socket.Options.CollectHttpResponseDetails = true;
        // Keep the long-lived dictation socket alive through NAT/proxy idle
        // windows (Google terminates silent connections).
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await _socket.ConnectAsync(uri, ct);
            Logger.Debug("live-wire: handshake ok");
        }
        catch (WebSocketException ex)
        {
            var status = (int)_socket.HttpStatusCode;
            var (message, _) = status > 0
                ? LiveProtocol.MapHandshakeStatus(status)
                : LiveProtocol.MapHandshakeError(ex.Message);
            // Key-free diagnosis trail (Android live-wire log parity): the
            // status and exception type only — never the URI or key.
            Logger.Warning($"live-wire: handshake failed http={status} ({ex.WebSocketErrorCode})");
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
            Logger.Warning($"live-wire: send failed ({ex.WebSocketErrorCode})");
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
                // Record WHY the server closed: the Gemini Live endpoint
                // reports setup errors (bad model, quota, invalid argument)
                // through the close frame, not the handshake. The reason is
                // server text and never contains the key; truncated for logs.
                CloseCode = result.CloseStatus is WebSocketCloseStatus status
                    ? (int)status
                    : null;
                CloseReason = result.CloseStatusDescription;
                var reasonForLog = (CloseReason ?? string.Empty);
                if (reasonForLog.Length > 200) reasonForLog = reasonForLog[..200];
                Logger.Info($"live-wire: server closed code={CloseCode?.ToString() ?? "?"} reason=\"{reasonForLog}\"");
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
