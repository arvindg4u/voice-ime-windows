using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// MainWindow shell tests. WPF windows require an STA thread; xUnit runs MTA,
/// so each test body runs on a dedicated STA thread. Window creation needs a
/// window station — on headless runners it throws, and the test passes
/// vacuously (returns false) instead of failing. Full coverage runs on
/// windows-latest CI.
/// </summary>
[Collection("WpfSta")]
public sealed class MainWindowTests
{
    [Fact]
    public void Constructor_DefaultsToGeneralSection()
    {
        if (!TryRunOnSta(window =>
        {
            Assert.Equal(MainSection.General, window.CurrentSection);
            Assert.True(MainWindow.GetIsNavActive(window.NavButtonFor(MainSection.General)));
            Assert.All(
                MainNav.Ordered.Where(s => s != MainSection.General),
                s => Assert.False(MainWindow.GetIsNavActive(window.NavButtonFor(s))));
        }))
        {
            return;
        }
    }

    [Fact]
    public void NavigateTo_MarksOnlyActiveNavItem()
    {
        if (!TryRunOnSta(window =>
        {
            window.NavigateTo(MainSection.About);

            Assert.Equal(MainSection.About, window.CurrentSection);
            Assert.True(MainWindow.GetIsNavActive(window.NavButtonFor(MainSection.About)));
            Assert.False(MainWindow.GetIsNavActive(window.NavButtonFor(MainSection.General)));
        }))
        {
            return;
        }
    }

    [Fact]
    public void RegisterSectionView_ShowsRegisteredContent()
    {
        if (!TryRunOnSta(window =>
        {
            var view = new TextBlock { Text = "general screen" };
            window.RegisterSectionView(MainSection.General, view);
            window.NavigateTo(MainSection.General);

            var host = Assert.IsType<ContentControl>(window.FindName("SectionHost"));
            Assert.Same(view, host.Content);
        }))
        {
            return;
        }
    }

    [Fact]
    public void SetStatus_AcceptsTextWithoutThrowing()
    {
        if (!TryRunOnSta(window =>
        {
            var exception = Record.Exception(() => window.SetStatus("Voice IME — recording…"));

            Assert.Null(exception);
        }))
        {
            return;
        }
    }

    [Fact]
    public void Close_DefaultGuard_HidesToTray()
    {
        if (!TryRunOnSta(window =>
        {
            window.Show();
            window.Close();

            Assert.False(window.IsVisible);
        }))
        {
            return;
        }
    }

    [Fact]
    public void Close_GuardRefusesHide_StaysVisible()
    {
        if (!TryRunOnSta(window =>
        {
            window.CanHideWindow = () => false;
            window.Show();
            window.Close();

            // Close-to-tray cancelled AND hide skipped: with the tray icon
            // off this is the only visible surface, so it must stay up.
            Assert.True(window.IsVisible);
        }))
        {
            return;
        }
    }

    [Fact]
    public void Close_GuardThrows_StaysVisible()
    {
        if (!TryRunOnSta(window =>
        {
            window.CanHideWindow = () => throw new InvalidOperationException("guard blew up");
            window.Show();
            window.Close();

            Assert.True(window.IsVisible);
        }))
        {
            return;
        }
    }

    // Task 9 (Handy parity gaps): first-run hint banner. Binds the live
    // store via the BindFirstRunHint seam with a no-op saver (never touches
    // disk); button clicks drive through RaiseEvent like real user input.

    [Fact]
    public void FirstRunHint_Unbound_StaysHidden()
    {
        if (!TryRunOnSta(window =>
        {
            Assert.False(window.IsFirstRunHintVisible);
        }))
        {
            return;
        }
    }

    [Fact]
    public void FirstRunHint_UnseenStore_ShowsWithHotkeyReminder()
    {
        if (!TryRunOnSta(window =>
        {
            var settings = new SettingsStore { Hotkey = "Alt+F4" };
            window.BindFirstRunHint(settings, _ => { });

            Assert.True(window.IsFirstRunHintVisible);
            var text = Assert.IsType<TextBlock>(window.FindName("FirstRunHintText"));
            Assert.Contains("Alt+F4", text.Text);
        }))
        {
            return;
        }
    }

    [Fact]
    public void FirstRunHint_SeenStore_StaysHidden()
    {
        if (!TryRunOnSta(window =>
        {
            window.BindFirstRunHint(
                new SettingsStore { SeenHint = true }, _ => { });

            Assert.False(window.IsFirstRunHintVisible);
        }))
        {
            return;
        }
    }

    [Fact]
    public void FirstRunHint_Dismiss_PersistsSeenHintAndHides()
    {
        if (!TryRunOnSta(window =>
        {
            var settings = new SettingsStore();
            var saverCalls = 0;
            window.BindFirstRunHint(settings, _ => saverCalls++);

            var dismiss = Assert.IsType<Button>(
                window.FindName("FirstRunHintDismissButton"));
            dismiss.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(settings.SeenHint);
            Assert.Equal(1, saverCalls);
            Assert.False(window.IsFirstRunHintVisible);
        }))
        {
            return;
        }
    }

    [Fact]
    public void FirstRunHint_OpenSettings_NavigatesWithoutDismissing()
    {
        if (!TryRunOnSta(window =>
        {
            var settings = new SettingsStore();
            var saverCalls = 0;
            window.BindFirstRunHint(settings, _ => saverCalls++);
            window.NavigateTo(MainSection.About);

            var open = Assert.IsType<Button>(
                window.FindName("FirstRunHintSettingsButton"));
            open.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(MainSection.General, window.CurrentSection);
            Assert.False(settings.SeenHint);
            Assert.Equal(0, saverCalls);
            Assert.True(window.IsFirstRunHintVisible);
        }))
        {
            return;
        }
    }

    [Fact]
    public void FirstRunHint_ThrowingSaver_NeverThrows()
    {
        if (!TryRunOnSta(window =>
        {
            var settings = new SettingsStore();
            window.BindFirstRunHint(
                settings,
                _ => throw new InvalidOperationException("disk blew up"));

            var dismiss = Assert.IsType<Button>(
                window.FindName("FirstRunHintDismissButton"));
            var exception = Record.Exception(
                () => dismiss.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));

            Assert.Null(exception);
            Assert.True(settings.SeenHint);
        }))
        {
            return;
        }
    }

    /// <summary>
    /// Runs the body on an STA thread with a fresh MainWindow. Returns false
    /// when no window station is available (headless); rethrows body failures.
    /// </summary>
    private static bool TryRunOnSta(Action<MainWindow> body)
    {
        MainWindow? window = null;
        return StaTestHelper.TryRunOnSta(() =>
        {
            try
            {
                window = new MainWindow();
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
}
