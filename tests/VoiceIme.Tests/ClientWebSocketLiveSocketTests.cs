using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Wire-level tests for <see cref="ClientWebSocketLiveSocket"/> against a real
/// in-process WebSocket server. The Gemini Live endpoint sends every server
/// message — setupComplete included — as a BINARY frame carrying UTF-8 JSON
/// (the official Python SDK reads raw bytes then JSON-decodes; Android handles
/// onMessage(ByteString)). The scripted FakeLiveSocket cannot cover frame
/// types, which is exactly how the binary-discard regression shipped: these
/// tests pin the decode behaviour at the frame level.
/// </summary>
public sealed class ClientWebSocketLiveSocketTests
{
    private const string SetupCompleteJson = """{"setupComplete":{}}""";

    [Fact]
    public async Task BinaryFrame_IsDecodedAsUtf8Json()
    {
        await RunAsync(
            server: async ws =>
            {
                await SendAsync(ws, SetupCompleteJson, WebSocketMessageType.Binary, endOfMessage: true);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            },
            client: async (socket, ct) =>
            {
                Assert.Equal(SetupCompleteJson, await socket.ReceiveTextAsync(ct));
                Assert.Null(await socket.ReceiveTextAsync(ct));
                Assert.Equal(1000, socket.CloseCode);
            });
    }

    [Fact]
    public async Task FragmentedBinaryFrame_IsReassembledBeforeDecoding()
    {
        const string json = """{"serverContent":{"inputTranscription":{"text":"hello from fragments"}}}""";
        await RunAsync(
            server: async ws =>
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                var half = bytes.Length / 2;
                await ws.SendAsync(new ArraySegment<byte>(bytes, 0, half),
                    WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None);
                await ws.SendAsync(new ArraySegment<byte>(bytes, half, bytes.Length - half),
                    WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            },
            client: async (socket, ct) =>
            {
                Assert.Equal(json, await socket.ReceiveTextAsync(ct));
                Assert.Null(await socket.ReceiveTextAsync(ct));
            });
    }

    [Fact]
    public async Task TextFrame_IsStillDecoded()
    {
        await RunAsync(
            server: async ws =>
            {
                await SendAsync(ws, SetupCompleteJson, WebSocketMessageType.Text, endOfMessage: true);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            },
            client: async (socket, ct) =>
            {
                Assert.Equal(SetupCompleteJson, await socket.ReceiveTextAsync(ct));
                Assert.Null(await socket.ReceiveTextAsync(ct));
            });
    }

    [Fact]
    public async Task ServerCloseWithReason_RecordsCodeAndReason()
    {
        await RunAsync(
            server: ws => ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation, "models/nope is not found", CancellationToken.None),
            client: async (socket, ct) =>
            {
                Assert.Null(await socket.ReceiveTextAsync(ct));
                Assert.Equal(1008, socket.CloseCode);
                Assert.Equal("models/nope is not found", socket.CloseReason);
            });
    }

    private static Task SendAsync(WebSocket ws, string json, WebSocketMessageType type, bool endOfMessage) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), type, endOfMessage, CancellationToken.None);

    /// <summary>
    /// Hosts one WebSocket accept on a loopback HttpListener, runs the server
    /// script against it, and the client script against a connected
    /// <see cref="ClientWebSocketLiveSocket"/>. Everything is bounded by a
    /// 15-second watchdog so a framing bug fails fast instead of hanging CI.
    /// </summary>
    private static async Task RunAsync(
        Func<WebSocket, Task> server,
        Func<ClientWebSocketLiveSocket, CancellationToken, Task> client)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = watchdog.Token;
        var (listener, port) = StartListener();
        try
        {
            var serverTask = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                await server(wsContext.WebSocket);
            }, ct);

            await using var socket = new ClientWebSocketLiveSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), ct);
            await client(socket, ct);
            await serverTask.WaitAsync(ct);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private static (HttpListener Listener, int Port) StartListener()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException)
            {
                // Port taken or reservation conflict: try another.
            }
        }
        throw new InvalidOperationException("Could not bind a loopback HttpListener port.");
    }
}
