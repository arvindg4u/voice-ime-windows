using System;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Advanced settings screen tests. WPF construction needs an STA thread plus a
/// window station — on headless runners construction throws and the test
/// passes vacuously (same pattern as <c>MainWindowTests</c>). Full coverage
/// runs on windows-latest CI. The view is built with a no-op saver so tests
/// never touch disk or DPAPI. Display + persist only: Task 8 wires the shell
/// behaviors (autostart reconcile, tray guard, overlay modes, paste chords)
/// in App — this file asserts what the controls show and persist.
/// </summary>
[Collection("WpfSta")]
public sealed class AdvancedSettingsViewTests
{
    [Fact]
    public void Defaults_LoadIntoControls()
    {
        TryRunOnSta(new SettingsStore(), view =>
        {
            Assert.False(view.StartHiddenChecked == true);
            Assert.False(view.AutostartChecked == true);
            Assert.True(view.ShowTrayIconChecked == true);
            Assert.Equal(3, view.ShowOverlayItemCount);
            Assert.Equal(
                OverlayModes.LabelFor(OverlayModes.Full),
                view.SelectedShowOverlayLabel);
            Assert.False(view.AutoSubmitChecked == true);
            Assert.Equal(3, view.PasteMethodItemCount);
            Assert.Equal(
                PasteMethods.LabelFor(PasteMethods.CtrlV),
                view.SelectedPasteMethodLabel);
            Assert.Equal("100", view.HistoryLimitText);
            Assert.False(view.IsHistoryLimitErrorVisible);
        });
    }

    [Fact]
    public void StoredValues_LoadIntoControls()
    {
        var store = new SettingsStore
        {
            StartHidden = true,
            Autostart = true,
            ShowTrayIcon = false,
            ShowOverlay = OverlayModes.Minimal,
            AutoSubmit = true,
            PasteMethod = PasteMethods.CtrlShiftV,
            HistoryLimit = 250,
        };
        TryRunOnSta(store, view =>
        {
            Assert.True(view.StartHiddenChecked == true);
            Assert.True(view.AutostartChecked == true);
            Assert.False(view.ShowTrayIconChecked == true);
            Assert.Equal(
                OverlayModes.LabelFor(OverlayModes.Minimal),
                view.SelectedShowOverlayLabel);
            Assert.True(view.AutoSubmitChecked == true);
            Assert.Equal(
                PasteMethods.LabelFor(PasteMethods.CtrlShiftV),
                view.SelectedPasteMethodLabel);
            Assert.Equal("250", view.HistoryLimitText);
        });
    }

    [Fact]
    public void BindsInjectedStore_ExposesSameInstance()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            Assert.Same(store, view.BoundSettings);
        });
    }

    [Fact]
    public void CommitHistoryLimit_BelowMin_ClampsToMin()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            view.HistoryLimitText = "3";

            var ok = view.CommitHistoryLimit();

            Assert.True(ok);
            Assert.Equal(10, store.HistoryLimit);
            Assert.Equal("10", view.HistoryLimitText);
        });
    }

    [Fact]
    public void CommitHistoryLimit_AboveMax_ClampsToMax()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            view.HistoryLimitText = "9999";

            var ok = view.CommitHistoryLimit();

            Assert.True(ok);
            Assert.Equal(500, store.HistoryLimit);
            Assert.Equal("500", view.HistoryLimitText);
        });
    }

    [Fact]
    public void CommitHistoryLimit_NonNumeric_ShowsErrorAndKeepsStored()
    {
        var store = new SettingsStore { HistoryLimit = 100 };
        TryRunOnSta(store, view =>
        {
            view.HistoryLimitText = "lots";

            var ok = view.CommitHistoryLimit();

            Assert.False(ok);
            Assert.Equal(100, store.HistoryLimit);
            Assert.Equal("100", view.HistoryLimitText);
            Assert.True(view.IsHistoryLimitErrorVisible);
            Assert.Contains("isn't a number", view.HistoryLimitErrorText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CommitHistoryLimit_Valid_SavesStore()
    {
        var store = new SettingsStore();
        var saverCalls = 0;
        StaTestHelper.TryRunOnSta(() =>
        {
            var view = new AdvancedSettingsView(store, _ => saverCalls++);
            view.HistoryLimitText = "250";

            var ok = view.CommitHistoryLimit();

            Assert.True(ok);
            Assert.Equal(250, store.HistoryLimit);
            Assert.Equal(1, saverCalls);
            Assert.False(view.IsHistoryLimitErrorVisible);
        });
    }

    [Fact]
    public void ReloadFromSettings_ExternalChange_RefreshesControls()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            store.StartHidden = true;
            store.ShowOverlay = OverlayModes.None;
            store.AutoSubmit = true;
            store.PasteMethod = PasteMethods.ShiftInsert;
            store.HistoryLimit = 42;

            view.ReloadFromSettings();

            Assert.True(view.StartHiddenChecked == true);
            Assert.Equal(
                OverlayModes.LabelFor(OverlayModes.None),
                view.SelectedShowOverlayLabel);
            Assert.True(view.AutoSubmitChecked == true);
            Assert.Equal(
                PasteMethods.LabelFor(PasteMethods.ShiftInsert),
                view.SelectedPasteMethodLabel);
            Assert.Equal("42", view.HistoryLimitText);
        });
    }

    [Fact]
    public void MainWindow_DefaultCtor_HostsAdvancedView()
    {
        RunOnStaWindow(window =>
        {
            Assert.IsType<AdvancedSettingsView>(window.SectionView(MainSection.Advanced));
        }, () => new MainWindow());
    }

    [Fact]
    public void MainWindow_NavigateToAdvanced_ShowsRegisteredContent()
    {
        RunOnStaWindow(window =>
        {
            window.NavigateTo(MainSection.Advanced);

            Assert.Equal(MainSection.Advanced, window.CurrentSection);
            Assert.IsType<AdvancedSettingsView>(window.SectionView(MainSection.Advanced));
        }, () => new MainWindow());
    }

    private static void TryRunOnSta(SettingsStore store, Action<AdvancedSettingsView> body)
    {
        StaTestHelper.TryRunOnSta(() =>
        {
            var view = new AdvancedSettingsView(store, _ => { });
            body(view);
        });
    }

    private static void RunOnStaWindow(Action<MainWindow> body, Func<MainWindow> create)
    {
        MainWindow? window = null;
        StaTestHelper.TryRunOnSta(() =>
        {
            try
            {
                window = create();
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
