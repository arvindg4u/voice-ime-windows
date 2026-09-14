using System;
using System.Linq;
using System.Windows.Controls;
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
