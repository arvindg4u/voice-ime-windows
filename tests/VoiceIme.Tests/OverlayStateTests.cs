using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// OverlayState is pure (no WPF types) and runs on any OS; the
/// OverlayWindow STA tests follow the MainWindowTests pattern (vacuous on
/// headless runners, full assertions on windows-latest CI).
/// </summary>
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
    public void ComputePosition_BottomCenterWith40pxOffset()
    {
        var workArea = new Rect(0, 0, 1920, 1040); // 1080p minus 40px taskbar

        var (left, top) = OverlayWindow.ComputePosition(
            workArea, OverlayWindow.RestingWidth, OverlayWindow.PillHeight);

        Assert.Equal((1920 - 256) / 2, left);
        Assert.Equal(1040 - 50 - 40, top);
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
    /// failures. Mirrors MainWindowTests.TryRunOnSta.
    /// </summary>
    private static bool TryRunOnSta(Action<OverlayWindow> body)
    {
        OverlayWindow? window = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                window = new OverlayWindow
                {
                    WorkAreaProvider = () => new Rect(0, 0, 1920, 1040),
                };
                body(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null && IsHeadlessFailure(failure))
        {
            return false;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return true;
    }

    private static bool IsHeadlessFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var name = current.GetType().Name;
            if (name.Contains("InvalidOperationException", StringComparison.Ordinal)
                || name.Contains("COMException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
