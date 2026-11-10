using System;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// OverlayState is pure (no WPF types) and runs on any OS; the
/// OverlayWindow STA tests follow the MainWindowTests pattern (vacuous on
/// headless runners, full assertions on windows-latest CI).
/// </summary>
[Collection("WpfSta")]
public sealed class OverlayStateTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(65, "1:05")]
    [InlineData(600, "10:00")]
    public void FormatElapsed_FormatsAsMinutesSeconds(int seconds, string expected)
    {
        Assert.Equal(expected, OverlayState.FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatElapsed_ClampsNegativeToZero()
    {
        Assert.Equal("0:00", OverlayState.FormatElapsed(TimeSpan.FromSeconds(-30)));
    }

    [Theory]
    [InlineData(0, "Sending")]
    [InlineData(1, "Sending.")]
    [InlineData(2, "Sending..")]
    [InlineData(3, "Sending...")]
    [InlineData(4, "Sending")]
    [InlineData(7, "Sending...")]
    public void FormatUploadingLabel_CyclesEllipsis(int tick, string expected)
    {
        Assert.Equal(expected, OverlayState.FormatUploadingLabel(tick));
    }

    [Fact]
    public void FormatUploadingLabel_WrapsNegativeTicks()
    {
        Assert.Equal("Sending...", OverlayState.FormatUploadingLabel(-1));
    }

    [Fact]
    public void WithPhase_ResetsLevelAndReplacesMessage()
    {
        var state = new OverlayState(OverlayPhase.Recording, 0.7f, "old");

        var next = state.WithPhase(OverlayPhase.Error, "mic denied");

        Assert.Equal(OverlayPhase.Error, next.Phase);
        Assert.Equal(0f, next.Level);
        Assert.Equal("mic denied", next.Message);
        // Original untouched (immutability).
        Assert.Equal(0.7f, state.Level);
    }

    [Fact]
    public void WithLevel_ClampsToZeroOne()
    {
        Assert.Equal(0f, new OverlayState(OverlayPhase.Recording, 0f).WithLevel(-1f).Level);
        Assert.Equal(1f, new OverlayState(OverlayPhase.Recording, 0f).WithLevel(2f).Level);
        Assert.Equal(0.5f, new OverlayState(OverlayPhase.Recording, 0f).WithLevel(0.5f).Level);
    }

    [Fact]
    public void WithNotice_SetsAndClears()
    {
        var state = new OverlayState(OverlayPhase.Recording, 0f);

        var with = state.WithNotice("Admin app", isWarning: true);
        Assert.Equal("Admin app", with.Notice);
        Assert.True(with.NoticeIsWarning);

        var cleared = with.WithNotice("   ");
        Assert.Null(cleared.Notice);
        Assert.False(cleared.NoticeIsWarning);
    }

    [Fact]
    public void WithPhase_ClearsNotice()
    {
        var state = new OverlayState(OverlayPhase.Recording, 0f).WithNotice("hint");

        var next = state.WithPhase(OverlayPhase.Uploading);

        Assert.Null(next.Notice);
        Assert.False(next.NoticeIsWarning);
    }

    [Fact]
    public void ComputePosition_BottomCenterWith40pxOffset()
    {
        var workArea = new Rect(0, 0, 1920, 1040); // 1080p minus 40px taskbar

        var (left, top) = OverlayWindow.ComputePosition(
            workArea, OverlayWindow.RestingWidth, OverlayWindow.PillHeight);

        Assert.Equal((1920 - 256) / 2, left);
        Assert.Equal(1040 - 50 - 40, top);
    }

    [Fact]
    public void ApplyNonActivatingStyles_AddsNoActivateAndToolWindow()
    {
        var styled = OverlayWindow.ApplyNonActivatingStyles(0);

        Assert.Equal(OverlayWindow.WsExNoActivate, styled & OverlayWindow.WsExNoActivate);
        Assert.Equal(OverlayWindow.WsExToolWindow, styled & OverlayWindow.WsExToolWindow);
    }

    [Fact]
    public void ApplyNonActivatingStyles_PreservesExistingBits()
    {
        const int existing = 0x00000010; // e.g. WS_EX_ACCEPTFILES

        var styled = OverlayWindow.ApplyNonActivatingStyles(existing);

        Assert.Equal(existing, styled & existing);
    }

    [Fact]
    public void Show_SetsSnapshotPhase_AndSetLevelUpdatesSnapshot()
    {
        if (!TryRunOnSta(window =>
        {
            window.Show(OverlayPhase.Recording);
            Assert.Equal(OverlayPhase.Recording, window.Snapshot.Phase);

            window.SetLevel(0.5f);
            // The dispatcher post lands before Close via priority drain below.
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(0.5f, window.Snapshot.Level);
        }))
        {
            return;
        }
    }

    [Fact]
    public void ShowError_SurfacesMessage_AndHideClearsVisibility()
    {
        if (!TryRunOnSta(window =>
        {
            window.ShowError("mic denied");

            Assert.Equal(OverlayPhase.Error, window.Snapshot.Phase);
            Assert.Equal("mic denied", window.Snapshot.Message);

            window.Hide();
            Assert.False(window.IsVisible);
        }))
        {
            return;
        }
    }

    /// <summary>
    /// Runs the body on an STA thread with a fresh OverlayWindow. Returns
    /// false when no window station is available (headless); rethrows body
    /// failures. Mirrors MainWindowTests.TryRunOnSta. Animations are disabled
    /// for the duration so Hide() is synchronous (the fade-out is time-based).
    /// </summary>
    private static bool TryRunOnSta(Action<OverlayWindow> body)
    {
        var previous = OverlayWindow.AnimationsEnabledProvider;
        OverlayWindow.AnimationsEnabledProvider = static () => false;
        try
        {
            OverlayWindow? window = null;
            return StaTestHelper.TryRunOnSta(() =>
            {
                try
                {
                    window = new OverlayWindow
                    {
                        WorkAreaProvider = () => new Rect(0, 0, 1920, 1040),
                    };
                    body(window);
                }
                finally
                {
                    try
                    {
                        window?.Close();
                    }
                    catch
                    {
                        // Close failures surface via the body exception path.
                    }
                }
            });
        }
        finally
        {
            OverlayWindow.AnimationsEnabledProvider = previous;
        }
    }
}
