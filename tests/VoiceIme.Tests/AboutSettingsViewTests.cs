using System;
using VoiceIme.Theme;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// About screen tests. WPF construction needs an STA thread plus a window
/// station — on headless runners construction throws and the test passes
/// vacuously (same pattern as <c>MainWindowTests</c>). Full coverage runs on
/// windows-latest CI. The view is built with a no-op saver so tests never
/// touch disk or DPAPI. Display/persist behavior and startup theme preference
/// are covered by the shell wiring and ThemeManager tests.
/// </summary>
[Collection("WpfSta")]
public sealed class AboutSettingsViewTests
{
    [Fact]
    public void Defaults_LoadIntoControls()
    {
        TryRunOnSta(new SettingsStore(), view =>
        {
            Assert.Equal(3, view.ThemeItemCount);
            Assert.Equal(AppThemes.LabelFor(AppThemes.System), view.SelectedThemeLabel);
            Assert.Equal(1, view.LanguageItemCount);
            Assert.Equal(AppLanguages.LabelFor(AppLanguages.English), view.SelectedLanguageLabel);
            Assert.StartsWith("v", view.VersionLabel, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void StoredValues_LoadIntoControls()
    {
        var store = new SettingsStore
        {
            Theme = AppThemes.Dark,
            Language = AppLanguages.English,
        };
        TryRunOnSta(store, view =>
        {
            Assert.Equal(AppThemes.LabelFor(AppThemes.Dark), view.SelectedThemeLabel);
            Assert.Equal(AppLanguages.LabelFor(AppLanguages.English), view.SelectedLanguageLabel);
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
    public void ThemeSelection_PersistsAndSaves()
    {
        var store = new SettingsStore();
        var saverCalls = 0;
        StaTestHelper.TryRunOnSta(() =>
        {
            var view = new AboutSettingsView(store, _ => saverCalls++);

            view.SelectThemeLabel(AppThemes.LabelFor(AppThemes.Dark));

            Assert.Equal(AppThemes.Dark, store.Theme);
            Assert.Equal(1, saverCalls);
            Assert.Equal(AppThemes.LabelFor(AppThemes.Dark), view.SelectedThemeLabel);
        });
    }

    [Fact]
    public void ThemeMapping_SystemLightDark_Resolves()
    {
        // ResolveTheme is the ThemeManager seam the view drives. Light/dark
        // map directly (provider-independent); system resolves to a concrete
        // theme. Provider-independent on purpose: the static
        // SystemDarkModeProvider is also mutated by the collection-less
        // ThemeManagerTests, so touching it here would race across xUnit
        // collections — the system-follows-OS path is covered there.
        Assert.Equal(HandyTheme.Light, ThemeManager.ResolveTheme(AppThemes.Light));
        Assert.Equal(HandyTheme.Dark, ThemeManager.ResolveTheme(AppThemes.Dark));
        Assert.True(ThemeManager.ResolveTheme(AppThemes.System) is HandyTheme.Light or HandyTheme.Dark);
    }

    [Fact]
    public void LanguageSelection_PersistsAndSaves()
    {
        var store = new SettingsStore();
        var saverCalls = 0;
        StaTestHelper.TryRunOnSta(() =>
        {
            var view = new AboutSettingsView(store, _ => saverCalls++);

            Assert.Equal(1, view.LanguageItemCount);
            view.SelectLanguageLabel(AppLanguages.LabelFor(AppLanguages.English));

            Assert.Equal(AppLanguages.English, store.Language);
            Assert.Equal(1, saverCalls);
        });
    }

    [Fact]
    public void ReloadFromSettings_ExternalChange_RefreshesControls()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            store.Theme = AppThemes.Light;
            store.Language = AppLanguages.English;

            view.ReloadFromSettings();

            Assert.Equal(AppThemes.LabelFor(AppThemes.Light), view.SelectedThemeLabel);
            Assert.Equal(
                AppLanguages.LabelFor(AppLanguages.English),
                view.SelectedLanguageLabel);
        });
    }

    [Fact]
    public void MainWindow_DefaultCtor_HostsAboutView()
    {
        RunOnStaWindow(window =>
        {
            Assert.IsType<AboutSettingsView>(window.SectionView(MainSection.About));
        }, () => new MainWindow());
    }

    [Fact]
    public void MainWindow_NavigateToAbout_ShowsRegisteredContent()
    {
        RunOnStaWindow(window =>
        {
            window.NavigateTo(MainSection.About);

            Assert.Equal(MainSection.About, window.CurrentSection);
            Assert.IsType<AboutSettingsView>(window.SectionView(MainSection.About));
        }, () => new MainWindow());
    }

    private static void TryRunOnSta(SettingsStore store, Action<AboutSettingsView> body)
    {
        StaTestHelper.TryRunOnSta(() =>
        {
            var view = new AboutSettingsView(store, _ => { });
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
