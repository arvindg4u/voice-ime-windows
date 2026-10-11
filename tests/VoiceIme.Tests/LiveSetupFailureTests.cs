using System;
using System.Threading;
using System.Threading.Tasks;
using VoiceIme.Theme;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Regression suite for the "instant timeout" bug: a Live handshake rejected
/// in under a second (invalid key 403, rate limit 429, server error) was
/// collapsed to a bare false and surfaced as "Timed out — try again".
/// Covers the real status mapping (CollectHttpResponseDetails path and the
/// .NET exception-message fallback) and the typed
/// <see cref="LiveSession.SetupFailure"/> record.
/// </summary>
public sealed class LiveSetupFailureTests
{
    private static readonly Uri Uri = new("wss://example.com/ws?key=k");

    private static LiveSessionOptions Fast => new(
        SetupAckTimeout: TimeSpan.FromMilliseconds(50),
        FinalizeGrace: TimeSpan.FromMilliseconds(50),
        LiveTimeout: TimeSpan.FromSeconds(5),
        CloseGrace: TimeSpan.FromMilliseconds(50));

    private static (FakeLiveSocket Socket, LiveSession Session) Setup()
    {
        var guard = new LiveSessionGuard();
        var socket = new FakeLiveSocket();
        return (socket, new LiveSession(socket, "m-live", smartMode: true, guard, guard.Next(), Fast));
    }

    // --- LiveProtocol handshake mapping ---

    [Theory]
    [InlineData(401, "Invalid API key — check Settings", false)]
    [InlineData(403, "Invalid API key — check Settings", false)]
    [InlineData(429, "Rate limited on all keys — retry later", true)]
    [InlineData(404, "Model not available for Live — check Settings", false)]
    [InlineData(500, "Server error (HTTP 500) — try again", false)]
    [InlineData(503, "Server error (HTTP 503) — try again", false)]
    public void MapHandshakeStatus_KnownCodes_MapToTypedMessages(
        int code, string expectedMessage, bool expectedRotate)
    {
        var (message, rotate) = LiveProtocol.MapHandshakeStatus(code);
        Assert.Equal(expectedMessage, message);
        Assert.Equal(expectedRotate, rotate);
    }

    [Theory]
    [InlineData(
        "The server returned status code '403' when status code '101' was expected.",
        "Invalid API key — check Settings", false)]
    [InlineData(
        "The server returned status code '429' when status code '101' was expected.",
        "Rate limited on all keys — retry later", true)]
    [InlineData(
        "The server returned status code '500' when status code '101' was expected.",
        "Server error (HTTP 500) — try again", false)]
    public void MapHandshakeError_DotNetExceptionShape_MapsRealStatus(
        string exceptionMessage, string expectedMessage, bool expectedRotate)
    {
        var (message, rotate) = LiveProtocol.MapHandshakeError(exceptionMessage);
        Assert.Equal(expectedMessage, message);
        Assert.Equal(expectedRotate, rotate);
    }

    [Fact]
    public void MapHandshakeError_RawHttpHead_StillMaps()
    {
        var (message, _) = LiveProtocol.MapHandshakeError("HTTP/1.1 403 Forbidden");
        Assert.Equal("Invalid API key — check Settings", message);
    }

    [Fact]
    public void MapHandshakeError_NoStatusAnywhere_IsNetworkError()
    {
        var (message, rotate) = LiveProtocol.MapHandshakeError("connection reset");
        Assert.Equal("Network error — check connection", message);
        Assert.False(rotate);
    }

    // --- Setup-close reason mapping (server reports errors via close frame) ---

    [Theory]
    [InlineData(1008,
        "models/gemini-live-2.5-flash-preview is not found for API version v1beta, or is not supported for bidiGenerateContent. Call ListModels",
        "Model not available for Live — check Settings", false)]
    [InlineData(1008, "API key not valid. Please pass a valid API key.",
        "Invalid API key — check Settings", false)]
    [InlineData(1008, "Operation is not implemented, or supported, or enabled.",
        "Server error (close 1008) — try again", false)]
    [InlineData(1011, "Quota exceeded for quota metric",
        "Rate limited on all keys — retry later", true)]
    [InlineData(1011, "RESOURCE_EXHAUSTED",
        "Rate limited on all keys — retry later", true)]
    [InlineData(1007, "Request contains an invalid argument.",
        "Live setup rejected by server — check model in Settings", false)]
    [InlineData(1011, "Internal error encountered.",
        "Server error (close 1011) — try again", false)]
    public void MapSetupCloseReason_KnownServerCloses_MapToTypedMessages(
        int code, string reason, string expectedMessage, bool expectedRotate)
    {
        var (message, rotate) = LiveProtocol.MapSetupCloseReason(code, reason);
        Assert.Equal(expectedMessage, message);
        Assert.Equal(expectedRotate, rotate);
    }

    [Fact]
    public void MapSetupCloseReason_NoCloseInfo_StaysNetworkError()
    {
        var (message, rotate) = LiveProtocol.MapSetupCloseReason(null, null);
        Assert.Equal("Network error — check connection", message);
        Assert.False(rotate);
    }

    [Fact]
    public async Task ConnectAndSetup_ServerCloseWithModelReason_RecordsModelFailure()
    {
        var (socket, session) = Setup();
        socket.CloseCode = 1008;
        socket.CloseReason =
            "models/m-live is not found for API version v1beta, or is not supported for bidiGenerateContent. Call ListModels";
        socket.EnqueueClose();

        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));

        var fail = Assert.IsType<LiveAttemptOutcome.Fail>(session.SetupFailure);
        Assert.Equal("Model not available for Live — check Settings", fail.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_ServerCloseWithQuotaReason_RecordsRotate()
    {
        var (socket, session) = Setup();
        socket.CloseCode = 1011;
        socket.CloseReason = "Quota exceeded for quota metric 'Live API requests'";
        socket.EnqueueClose();

        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));

        Assert.IsType<LiveAttemptOutcome.Rotate>(session.SetupFailure);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_ServerCloseWithoutReason_StaysNetworkError()
    {
        var (socket, session) = Setup();
        socket.EnqueueClose();

        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));

        var fail = Assert.IsType<LiveAttemptOutcome.Fail>(session.SetupFailure);
        Assert.Equal("Network error — check connection", fail.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public void BuildLiveUri_OmitsExplicitPort()
    {
        var uri = LiveProtocol.BuildLiveUri("generativelanguage.googleapis.com", "k");
        Assert.True(uri.IsDefaultPort);
        Assert.DoesNotContain(":443", uri.ToString());
    }

    // --- LiveSession.SetupFailure ---

    [Fact]
    public async Task ConnectAndSetup_Success_LeavesSetupFailureNull()
    {
        var (socket, session) = Setup();
        socket.EnqueueText("""{"setupComplete":{}}""");
        Assert.True(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));
        Assert.Null(session.SetupFailure);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_ReceiveFailure_RecordsTypedFail()
    {
        var (socket, session) = Setup();
        socket.EnqueueFailure(new LiveSocketException("Invalid API key — check Settings"));

        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));

        var fail = Assert.IsType<LiveAttemptOutcome.Fail>(session.SetupFailure);
        Assert.Equal("Invalid API key — check Settings", fail.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_RateLimitedFailure_RecordsRotate()
    {
        var (socket, session) = Setup();
        socket.EnqueueFailure(new LiveSocketException("Rate limited on all keys — retry later"));

        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));

        Assert.IsType<LiveAttemptOutcome.Rotate>(session.SetupFailure);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_NoAck_RecordsTimedOut()
    {
        var (_, session) = Setup();
        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));

        var fail = Assert.IsType<LiveAttemptOutcome.Fail>(session.SetupFailure);
        Assert.Equal("Timed out — try again", fail.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_ConnectRejected_RecordsHandshakeMessage()
    {
        var guard = new LiveSessionGuard();
        var socket = new ConnectRejectingSocket("Invalid API key — check Settings");
        var session = new LiveSession(socket, "m-live", smartMode: false, guard, guard.Next(), Fast);

        Assert.False(await session.ConnectAndSetupAsync(Uri, CancellationToken.None));

        var fail = Assert.IsType<LiveAttemptOutcome.Fail>(session.SetupFailure);
        Assert.Equal("Invalid API key — check Settings", fail.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAndSetup_CallerCancelled_ThrowsWithoutRecordingFailure()
    {
        var guard = new LiveSessionGuard();
        var socket = new FakeLiveSocket();
        var session = new LiveSession(socket, "m-live", smartMode: false, guard, guard.Next(), Fast);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ConnectAndSetupAsync(Uri, cts.Token));
        Assert.Null(session.SetupFailure);
        await session.DisposeAsync();
    }

    /// <summary>Connect-phase rejection fake: the handshake itself fails.</summary>
    private sealed class ConnectRejectingSocket(string message) : ILiveSocket
    {
        public Task ConnectAsync(Uri uri, CancellationToken ct) =>
            throw new LiveSocketException(message);

        public Task SendTextAsync(string text, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> ReceiveTextAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Theme plumbing added with the dark/light overhaul: CurrentTheme tracking,
/// the ThemeApplied notification, and the never-throw chrome helpers.
/// </summary>
[Collection("ThemeStatics")]
public sealed class ThemeChromeTests
{
    [Fact]
    public void ApplyTheme_TracksCurrentThemeAndRaisesThemeApplied()
    {
        var before = ThemeManager.CurrentTheme;
        HandyTheme? observed = null;
        Action<HandyTheme> handler = t => observed = t;
        ThemeManager.ThemeApplied += handler;
        try
        {
            ThemeManager.ApplyTheme(HandyTheme.Dark);
            Assert.Equal(HandyTheme.Dark, ThemeManager.CurrentTheme);
            Assert.Equal(HandyTheme.Dark, observed);

            ThemeManager.ApplyTheme(HandyTheme.Light);
            Assert.Equal(HandyTheme.Light, ThemeManager.CurrentTheme);
            Assert.Equal(HandyTheme.Light, observed);
        }
        finally
        {
            ThemeManager.ThemeApplied -= handler;
            ThemeManager.ApplyTheme(before);
        }
    }

    [Fact]
    public void ApplyWindowChrome_ZeroHandle_DoesNotThrow()
    {
        var exception = Record.Exception(() => ThemeManager.ApplyWindowChrome(IntPtr.Zero));
        Assert.Null(exception);
    }

    [Fact]
    public void ApplyWindowChrome_NullWindow_DoesNotThrow()
    {
        var exception = Record.Exception(() => ThemeManager.ApplyWindowChrome((System.Windows.Window)null!));
        Assert.Null(exception);
    }
}
