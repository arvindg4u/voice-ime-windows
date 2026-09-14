using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VoiceIme.Theme;

namespace VoiceIme.Views;

/// <summary>
/// About section screen (Handy AboutSettings port, read-only-safe subset):
/// theme dropdown (system/light/dark, applies live via
/// <see cref="ThemeManager"/>), language dropdown (English-only catalog in
/// v1 — selection persists, UI stays English, labeled honestly), version row
/// (mono), app-data + log-folder buttons, source link, and acknowledgments
/// text. Donate is intentionally absent. Viewmodel-less code-behind bound to
/// <see cref="SettingsStore"/> — every write validates-then-commits and never
/// throws out of an event handler. Display + persist only: startup still
/// applies the system preference until T8 wires theme-at-launch.
/// Hosted by <see cref="MainWindow"/> via
/// RegisterSectionView(MainSection.About, view).
/// </summary>
public partial class AboutSettingsView : System.Windows.Controls.UserControl
{
    /// <summary>Source repository opened by the source-link button.</summary>
    internal const string SourceRepositoryUrl = "https://github.com/arvindg4u/voice-ime-windows";

    private readonly SettingsStore _settings;
    private readonly Action<SettingsStore> _saver;

    /// <summary>
    /// The shared <see cref="SettingsStore"/> this view edits — the F1
    /// shared-instance wiring asserts the app's live store lands here.
    /// </summary>
    internal SettingsStore BoundSettings => _settings;

    private bool _initializing = true;

    public AboutSettingsView()
        : this(SettingsStore.Load())
    {
    }

    internal AboutSettingsView(
        SettingsStore settings,
        Action<SettingsStore>? saver = null)
    {
        _settings = settings;
        _saver = saver ?? (static s => s.Save());
        InitializeComponent();
        LoadFromSettings();
        _initializing = false;
    }

    /// <summary>Test seam: theme dropdown item count.</summary>
    internal int ThemeItemCount => ThemeBox.Items.Count;

    /// <summary>Test seam: selected theme label.</summary>
    internal string? SelectedThemeLabel => ThemeBox.SelectedItem as string;

    /// <summary>Test seam: language dropdown item count.</summary>
    internal int LanguageItemCount => LanguageBox.Items.Count;

    /// <summary>Test seam: selected language label.</summary>
    internal string? SelectedLanguageLabel => LanguageBox.SelectedItem as string;

    /// <summary>Test seam: version row text.</summary>
    internal string VersionLabel => VersionText.Text;

    /// <summary>
    /// Re-reads the bound store into the controls (same shared-store
    /// rationale as <see cref="AdvancedSettingsView.ReloadFromSettings"/>).
    /// </summary>
    internal void ReloadFromSettings() => LoadFromSettings();

    /// <summary>
    /// Test seam: drives a theme selection through the persist path
    /// (persists, applies, saves) the way a user pick does.
    /// </summary>
    internal void SelectThemeLabel(string label) => ThemeBox.SelectedItem = label;

    /// <summary>
    /// Test seam: drives a language selection through the persist path
    /// the way a user pick does.
    /// </summary>
    internal void SelectLanguageLabel(string label) => LanguageBox.SelectedItem = label;

    private void LoadFromSettings()
    {
        ThemeBox.SelectionChanged -= ThemeBox_SelectionChanged;
        try
        {
            ThemeBox.Items.Clear();
            ThemeBox.Items.Add(AppThemes.LabelFor(AppThemes.System));
            ThemeBox.Items.Add(AppThemes.LabelFor(AppThemes.Light));
            ThemeBox.Items.Add(AppThemes.LabelFor(AppThemes.Dark));
            ThemeBox.SelectedItem = AppThemes.LabelFor(_settings.Theme);
        }
        finally
        {
            ThemeBox.SelectionChanged += ThemeBox_SelectionChanged;
        }

        LanguageBox.SelectionChanged -= LanguageBox_SelectionChanged;
        try
        {
            LanguageBox.Items.Clear();
            LanguageBox.Items.Add(AppLanguages.LabelFor(AppLanguages.English));
            LanguageBox.SelectedItem = AppLanguages.LabelFor(_settings.Language);
        }
        finally
        {
            LanguageBox.SelectionChanged += LanguageBox_SelectionChanged;
        }

        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.Theme = AppThemes.ThemeForLabel(ThemeBox.SelectedItem as string);
        ThemeManager.ApplyTheme(ThemeManager.ResolveTheme(_settings.Theme));
        TrySaveSettings();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.Language = AppLanguages.LanguageForLabel(LanguageBox.SelectedItem as string);
        TrySaveSettings();
    }

    private void AppDataButton_Click(object sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceIme"));

    private void LogFolderButton_Click(object sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(Path.GetDirectoryName(Logger.LogPath)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceIme", "logs"));

    private void SourceLinkButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(SourceRepositoryUrl);

    private static void OpenFolderInExplorer(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch
        {
            // Folder open is best-effort — never crash the settings screen.
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Link open is best-effort — never crash the settings screen.
        }
    }

    private void TrySaveSettings()
    {
        try
        {
            _saver(_settings);
            Saved?.Invoke(_settings);
        }
        catch
        {
            // Persist is best-effort from a handler — never throws out of one.
        }
    }

    /// <summary>
    /// Raised after the view persists the shared <see cref="SettingsStore"/>.
    /// App observes it to refresh chrome that is otherwise built once at
    /// startup. Not raised on save failure.
    /// </summary>
    internal event Action<SettingsStore>? Saved;
}
