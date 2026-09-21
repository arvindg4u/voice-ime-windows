using System;
using System.Windows;
using System.Windows.Controls;

namespace VoiceIme.Views;

/// <summary>
/// Advanced section screen (Handy AdvancedSettings port, read-only-safe
/// subset): App toggles (start-hidden, autostart, tray icon, overlay),
/// Output paste-method dropdown, and the History limit numeric (clamped
/// 10–500). Viewmodel-less code-behind bound to <see cref="SettingsStore"/>
/// — every write validates-then-commits and surfaces failures inline, never
/// throws out of an event handler. Saves are applied live by App: autostart,
/// tray visibility, overlay mode, paste delivery, and history eviction all
/// follow the stored choices.
/// Hosted by <see cref="MainWindow"/> via
/// RegisterSectionView(MainSection.Advanced, view).
/// </summary>
public partial class AdvancedSettingsView : System.Windows.Controls.UserControl
{
    private readonly SettingsStore _settings;
    private readonly Action<SettingsStore> _saver;

    /// <summary>
    /// The shared <see cref="SettingsStore"/> this view edits — the F1
    /// shared-instance wiring asserts the app's live store lands here.
    /// </summary>
    internal SettingsStore BoundSettings => _settings;

    private bool _initializing = true;

    public AdvancedSettingsView()
        : this(SettingsStore.Load())
    {
    }

    internal AdvancedSettingsView(
        SettingsStore settings,
        Action<SettingsStore>? saver = null)
    {
        _settings = settings;
        _saver = saver ?? (static s => s.Save());
        InitializeComponent();
        LoadFromSettings();
        _initializing = false;
    }

    /// <summary>Test seam: start-hidden toggle state.</summary>
    internal bool? StartHiddenChecked => StartHiddenCheck.IsChecked;

    /// <summary>Test seam: autostart toggle state.</summary>
    internal bool? AutostartChecked => AutostartCheck.IsChecked;

    /// <summary>Test seam: tray-icon toggle state.</summary>
    internal bool? ShowTrayIconChecked => ShowTrayIconCheck.IsChecked;

    /// <summary>Test seam: overlay dropdown item count.</summary>
    internal int ShowOverlayItemCount => ShowOverlayBox.Items.Count;

    /// <summary>Test seam: selected overlay mode label.</summary>
    internal string? SelectedShowOverlayLabel => ShowOverlayBox.SelectedItem as string;

    /// <summary>Test seam: auto-submit toggle state.</summary>
    internal bool? AutoSubmitChecked => AutoSubmitCheck.IsChecked;

    /// <summary>Test seam: paste-method dropdown item count.</summary>
    internal int PasteMethodItemCount => PasteMethodBox.Items.Count;

    /// <summary>Test seam: selected paste-method label.</summary>
    internal string? SelectedPasteMethodLabel => PasteMethodBox.SelectedItem as string;

    /// <summary>Test seam: history-limit box text.</summary>
    internal string HistoryLimitText
    {
        get => HistoryLimitBox.Text;
        set => HistoryLimitBox.Text = value;
    }

    /// <summary>Test seam: inline history-limit error text.</summary>
    internal string HistoryLimitErrorText => HistoryLimitError.Text;

    /// <summary>Test seam: whether the inline history-limit error is shown.</summary>
    internal bool IsHistoryLimitErrorVisible => HistoryLimitError.Visibility == Visibility.Visible;

    /// <summary>
    /// Re-reads the bound store into the controls (same shared-store
    /// rationale as <see cref="GeminiSettingsView.ReloadFromSettings"/>).
    /// </summary>
    internal void ReloadFromSettings() => LoadFromSettings();

    /// <summary>
    /// Test seam (internal): commits the history-limit box; kept off the
    /// private event handlers so tests can drive the clamp path directly.
    /// Returns false when the text is not a number (shows inline error,
    /// keeps the stored value).
    /// </summary>
    internal bool CommitHistoryLimit()
    {
        var trimmed = HistoryLimitBox.Text.Trim();
        if (!int.TryParse(trimmed, out var parsed))
        {
            // Reset the box BEFORE showing the error: assigning Text fires
            // TextChanged synchronously, and the handler clears a stale error
            // once the text parses — doing it after would erase this error.
            HistoryLimitBox.Text = _settings.HistoryLimit.ToString();
            ShowHistoryLimitError($"\"{trimmed}\" isn't a number — history limit stays {_settings.HistoryLimit}.");
            return false;
        }

        var clamped = SettingsStore.ClampHistoryLimit(parsed);
        _settings.HistoryLimit = clamped;
        HistoryLimitBox.Text = clamped.ToString();
        ClearHistoryLimitError();
        TrySaveSettings(out var saveError);
        if (saveError is not null)
        {
            ShowHistoryLimitError($"History limit is {clamped} for now, but saving failed: {saveError}");
            return false;
        }

        return true;
    }

    private void LoadFromSettings()
    {
        StartHiddenCheck.IsChecked = _settings.StartHidden;
        AutostartCheck.IsChecked = _settings.Autostart;
        ShowTrayIconCheck.IsChecked = _settings.ShowTrayIcon;

        ShowOverlayBox.SelectionChanged -= ShowOverlayBox_SelectionChanged;
        try
        {
            ShowOverlayBox.Items.Clear();
            ShowOverlayBox.Items.Add(OverlayModes.LabelFor(OverlayModes.Full));
            ShowOverlayBox.Items.Add(OverlayModes.LabelFor(OverlayModes.Minimal));
            ShowOverlayBox.Items.Add(OverlayModes.LabelFor(OverlayModes.None));
            ShowOverlayBox.SelectedItem = OverlayModes.LabelFor(_settings.ShowOverlay);
        }
        finally
        {
            ShowOverlayBox.SelectionChanged += ShowOverlayBox_SelectionChanged;
        }

        AutoSubmitCheck.IsChecked = _settings.AutoSubmit;

        PasteMethodBox.SelectionChanged -= PasteMethodBox_SelectionChanged;
        try
        {
            PasteMethodBox.Items.Clear();
            PasteMethodBox.Items.Add(PasteMethods.LabelFor(PasteMethods.CtrlV));
            PasteMethodBox.Items.Add(PasteMethods.LabelFor(PasteMethods.ShiftInsert));
            PasteMethodBox.Items.Add(PasteMethods.LabelFor(PasteMethods.CtrlShiftV));
            PasteMethodBox.SelectedItem = PasteMethods.LabelFor(_settings.PasteMethod);
        }
        finally
        {
            PasteMethodBox.SelectionChanged += PasteMethodBox_SelectionChanged;
        }

        HistoryLimitBox.Text = _settings.HistoryLimit.ToString();
        ClearHistoryLimitError();
    }

    private void StartHiddenCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartHidden = StartHiddenCheck.IsChecked == true;
        TrySaveSettings(out _);
    }

    private void AutostartCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.Autostart = AutostartCheck.IsChecked == true;
        TrySaveSettings(out _);
    }

    private void ShowTrayIconCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowTrayIcon = ShowTrayIconCheck.IsChecked == true;
        TrySaveSettings(out _);
    }

    private void ShowOverlayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.ShowOverlay = OverlayModes.ModeForLabel(ShowOverlayBox.SelectedItem as string);
        TrySaveSettings(out _);
    }

    private void AutoSubmitCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoSubmit = AutoSubmitCheck.IsChecked == true;
        TrySaveSettings(out _);
    }

    private void PasteMethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.PasteMethod = PasteMethods.MethodForLabel(PasteMethodBox.SelectedItem as string);
        TrySaveSettings(out _);
    }

    private void HistoryLimitBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        // Live commit would fight the user mid-typing (e.g. clearing the box
        // to type "200" parses "" first) — commit on focus loss instead; this
        // handler only clears a stale error once the text parses again.
        if (int.TryParse(HistoryLimitBox.Text.Trim(), out _))
        {
            ClearHistoryLimitError();
        }
    }

    private void HistoryLimitBox_LostFocus(object sender, RoutedEventArgs e) => CommitHistoryLimit();

    private void ShowHistoryLimitError(string message)
    {
        HistoryLimitError.Text = message;
        HistoryLimitError.Visibility = Visibility.Visible;
    }

    private void ClearHistoryLimitError() => HistoryLimitError.Visibility = Visibility.Collapsed;

    private bool TrySaveSettings(out string? error)
    {
        try
        {
            _saver(_settings);
            error = null;
            Saved?.Invoke(_settings);
            return true;
        }
        catch
        {
            error = "Could not save settings — try again.";
            return false;
        }
    }

    /// <summary>
    /// Raised after the view persists the shared <see cref="SettingsStore"/>.
    /// App observes it to refresh tray chrome that is otherwise built once at
    /// startup. Not raised on save failure.
    /// </summary>
    internal event Action<SettingsStore>? Saved;
}
