using System;
using System.IO;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// F1/F2 shared-instance wiring tests. App passes its live stores into the
/// section views at construction — the assertions bind the views to explicit
/// temp-file stores through the same static <c>CreateMainWindow</c> overload
/// the instance path uses, so nothing here constructs the singleton
/// <see cref="System.Windows.Application"/> (only one may exist per process)
/// or touches %AppData%, DPAPI, or the network. WPF construction needs an STA
/// thread plus a window station — on headless runners construction throws and
/// the test passes vacuously (same pattern as <c>MainWindowTests</c>). Full
/// coverage runs on windows-latest CI.
/// </summary>
[Collection("WpfSta")]
public sealed class AppWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void CreateMainWindow_BindsLiveSettingsToGeneralAndGemini()
    {
        TryRunOnSta(() =>
        {
            var settings = new SettingsStore();
            var clips = new ClipboardStore(PathFor("clips.json"));
            var window = App.CreateMainWindow(
                settings, clips, new NullHotkeyRegistrar(), () => Array.Empty<string>());
            try
            {
                Assert.Same(
                    settings,
                    Assert.IsType<GeneralSettingsView>(
                        window.SectionView(MainSection.General)).BoundSettings);
                Assert.Same(
                    settings,
                    Assert.IsType<GeminiSettingsView>(
                        window.SectionView(MainSection.Gemini)).BoundSettings);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CreateMainWindow_BindsLiveSettingsToAdvanced()
    {
        TryRunOnSta(() =>
        {
            var settings = new SettingsStore();
            var clips = new ClipboardStore(PathFor("adv.json"));
            var window = App.CreateMainWindow(
                settings, clips, new NullHotkeyRegistrar(), () => Array.Empty<string>());
            try
            {
                Assert.Same(
                    settings,
                    Assert.IsType<AdvancedSettingsView>(
                        window.SectionView(MainSection.Advanced)).BoundSettings);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CreateMainWindow_BindsLiveSettingsToAbout()
    {
        TryRunOnSta(() =>
        {
            var settings = new SettingsStore();
            var clips = new ClipboardStore(PathFor("about.json"));
            var window = App.CreateMainWindow(
                settings, clips, new NullHotkeyRegistrar(), () => Array.Empty<string>());
            try
            {
                Assert.Same(
                    settings,
                    Assert.IsType<AboutSettingsView>(
                        window.SectionView(MainSection.About)).BoundSettings);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CreateMainWindow_BindsLiveClipsToHistory()
    {
        TryRunOnSta(() =>
        {
            var settings = new SettingsStore();
            var clips = new ClipboardStore(PathFor("clips.json"));
            clips.Add("spoken earlier");
            var window = App.CreateMainWindow(
                settings, clips, new NullHotkeyRegistrar(), () => Array.Empty<string>());
            try
            {
                var history = Assert.IsType<HistorySettingsView>(
                    window.SectionView(MainSection.History));
                Assert.Same(clips, history.BoundStore);
                Assert.Equal("spoken earlier", Assert.Single(history.Rows).Body);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void GeneralViewSave_MutatesLiveSettings_AndRaisesSavedForTrayRefresh()
    {
        TryRunOnSta(() =>
        {
            var settings = new SettingsStore();
            var clips = new ClipboardStore(PathFor("clips.json"));
            var saverCalls = 0;
            SettingsStore? raised = null;
            var window = new MainWindow(
                () =>
                {
                    var view = new GeneralSettingsView(
                        settings,
                        new NullHotkeyRegistrar(),
                        () => Array.Empty<string>(),
                        _ => saverCalls++);
                    view.Saved += s => raised = s;
                    return view;
                },
                () => null,
                () => null);
            try
            {
                var general = Assert.IsType<GeneralSettingsView>(
                    window.SectionView(MainSection.General));
                general.ApplyHotkey("Alt+F4", HotkeyChord.DefaultChord);

                Assert.Equal("Alt+F4", settings.Hotkey);
                Assert.Equal(1, saverCalls);
                Assert.Same(settings, raised);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RefreshSectionViews_PullsLiveStoreChangesIntoShownShell()
    {
        TryRunOnSta(() =>
        {
            var settings = new SettingsStore();
            var clips = new ClipboardStore(PathFor("clips.json"));
            var window = App.CreateMainWindow(
                settings, clips, new NullHotkeyRegistrar(), () => Array.Empty<string>());
            try
            {
                settings.Hotkey = "Alt+F4";
                settings.Model = "test-model";
                settings.StartHidden = true;
                settings.ShowOverlay = OverlayModes.Minimal;
                settings.AutoSubmit = true;
                settings.HistoryLimit = 42;
                settings.Theme = AppThemes.Dark;
                clips.Add("dictated while hidden");

                App.RefreshSectionViews(window, settings, clips);

                var general = Assert.IsType<GeneralSettingsView>(
                    window.SectionView(MainSection.General));
                var gemini = Assert.IsType<GeminiSettingsView>(
                    window.SectionView(MainSection.Gemini));
                var history = Assert.IsType<HistorySettingsView>(
                    window.SectionView(MainSection.History));
                var advanced = Assert.IsType<AdvancedSettingsView>(
                    window.SectionView(MainSection.Advanced));
                var about = Assert.IsType<AboutSettingsView>(
                    window.SectionView(MainSection.About));
                Assert.Equal("Alt+F4", general.HotkeyLabel);
                Assert.Equal("test-model", gemini.ModelText);
                Assert.Equal("dictated while hidden", Assert.Single(history.Rows).Body);
                Assert.True(advanced.StartHiddenChecked == true);
                Assert.Equal(
                    OverlayModes.LabelFor(OverlayModes.Minimal),
                    advanced.SelectedShowOverlayLabel);
                Assert.True(advanced.AutoSubmitChecked == true);
                Assert.Equal("42", advanced.HistoryLimitText);
                Assert.Equal(AppThemes.LabelFor(AppThemes.Dark), about.SelectedThemeLabel);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RefreshSectionViews_NullWindow_IsNoOp()
    {
        // UI-free: no shell built yet (MainWindow optional factories also
        // tolerate a null registration), so this must not throw.
        App.RefreshSectionViews(null, new SettingsStore(), new ClipboardStore(PathFor("null.json")));
    }

    [Fact]
    public void TrayText_ReflectsHotkey()
    {
        Assert.Equal(
            "Voice IME — Ctrl+Shift+Space to dictate",
            App.TrayText(HotkeyChord.DefaultChord));
    }

    [Fact]
    public void TruncateTrayText_LongHotkey_StaysWithin63CharLimit()
    {
        var text = App.TrayText(new string('x', 100));

        Assert.Equal(App.TrayTextLimit, text.Length);
        Assert.Equal(63, App.TrayTextLimit);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    private static void TryRunOnSta(Action body)
    {
        StaTestHelper.TryRunOnSta(body);
    }
}
