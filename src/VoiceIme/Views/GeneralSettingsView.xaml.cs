using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceIme.Views;

/// <summary>
/// General section screen (Handy GeneralSettings port): dictation-hotkey
/// capture chip, activation-mode dropdown, the Sound group (microphone
/// picker, mute toggle, speaker test, channel picker, output picker, volume
/// slider, feedback toggle), and the cancel-key capture row. Viewmodel-less
/// code-behind bound to <see cref="SettingsStore"/> — every write
/// validates-then-commits and surfaces failures inline, never throws out of
/// an event handler. Hosted by <see cref="MainWindow"/> via
/// RegisterSectionView(MainSection.General, view).
/// </summary>
public partial class GeneralSettingsView : System.Windows.Controls.UserControl
{
    private const string UnavailableSuffix = " (unavailable)";

    private readonly SettingsStore _settings;
    private readonly Func<IReadOnlyList<string>> _listMicrophones;
    private readonly Func<IReadOnlyList<string>> _listOutputDevices;
    private readonly Action<SettingsStore> _saver;

    /// <summary>
    /// The shared <see cref="SettingsStore"/> this view edits — the F1/F2
    /// shared-instance wiring asserts the app's live store lands here.
    /// </summary>
    internal SettingsStore BoundSettings => _settings;

    private bool _initializing = true;
    private bool _capturing;
    private Window? _captureWindow;
    private string _previousChord = HotkeyChord.DefaultChord;
    private readonly HashSet<Key> _heldKeys = new();
    private uint _captureMods;
    private uint? _captureVk;

    public GeneralSettingsView()
        : this(SettingsStore.Load(), new NullHotkeyRegistrar(), MicrophoneDevices.ListNames)
    {
    }

    /// <summary>
    /// Live-hotkey suspend/resume channel. Defaults to the validation-only
    /// <see cref="NullHotkeyRegistrar"/>; the app replaces it with a
    /// <see cref="LiveHotkeyRegistrar"/> when the section is shown so capture
    /// suspends the real global hotkey and commit re-registers it.
    /// </summary>
    internal IHotkeyRegistrar HotkeyRegistrar { get; set; }

    internal GeneralSettingsView(
        SettingsStore settings,
        IHotkeyRegistrar registrar,
        Func<IReadOnlyList<string>> listMicrophones,
        Action<SettingsStore>? saver = null,
        Func<IReadOnlyList<string>>? listOutputDevices = null)
    {
        _settings = settings;
        HotkeyRegistrar = registrar;
        _listMicrophones = listMicrophones;
        _listOutputDevices = listOutputDevices ?? OutputDevices.ListNames;
        _saver = saver ?? (static s => s.Save());
        InitializeComponent();
        Unloaded += (_, _) => EndCaptureMode();
        LoadFromSettings();
        _initializing = false;
    }

    /// <summary>Test seam: the hotkey chip's current label.</summary>
    internal string HotkeyLabel => HotkeyText.Text;

    /// <summary>Test seam: activation dropdown item count.</summary>
    internal int ActivationItemCount => ActivationBox.Items.Count;

    /// <summary>Test seam: microphone dropdown item count.</summary>
    internal int MicrophoneItemCount => MicBox.Items.Count;

    /// <summary>Test seam: inline hotkey error text.</summary>
    internal string HotkeyErrorText => HotkeyError.Text;

    /// <summary>Test seam: whether the inline hotkey error is shown.</summary>
    internal bool IsHotkeyErrorVisible => HotkeyError.Visibility == Visibility.Visible;

    /// <summary>Test seam: selected activation label.</summary>
    internal string? SelectedActivationLabel => ActivationBox.SelectedItem as string;

    /// <summary>Test seam: selected microphone item.</summary>
    internal string? SelectedMicrophone => MicBox.SelectedItem as string;

    /// <summary>Test seam: mute toggle state.</summary>
    internal bool? MuteChecked => MuteCheck.IsChecked;

    /// <summary>
    /// Re-reads the bound store into the controls (see
    /// <see cref="GeminiSettingsView.ReloadFromSettings"/> — same shared-store
    /// rationale). Skips hotkey capture state: never call mid-capture.
    /// </summary>
    internal void ReloadFromSettings() => LoadFromSettings();

    private void LoadFromSettings()
    {
        RefreshHotkeyChip();
        ClearHotkeyError();

        ActivationBox.Items.Clear();
        ActivationBox.Items.Add("Hold or toggle");
        ActivationBox.Items.Add("Push to talk");
        ActivationBox.Items.Add("Toggle");
        ActivationBox.SelectedItem = ActivationModes.LabelFor(_settings.ActivationMode);

        RefreshMicrophoneList();

        MuteCheck.IsChecked = _settings.MuteWhileRecording;
        RefreshSoundGroup();
        RefreshCancelRow();
    }

    // Hotkey capture (Handy GlobalShortcutInput port).

    private void HotkeyChip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        StartCapture(CaptureTarget.Hotkey);
    }

    private void HotkeyChip_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing && (e.Key == Key.Enter || e.Key == Key.Space))
        {
            e.Handled = true;
            StartCapture(CaptureTarget.Hotkey);
        }
    }

    private void StartCapture(CaptureTarget target)
    {
        if (_capturing)
        {
            return;
        }

        _captureTarget = target;
        if (target == CaptureTarget.Cancel)
        {
            _previousChord = _settings.CancelHotkey;
        }
        else
        {
            _previousChord = _settings.Hotkey;
        }

        HotkeyRegistrar.Unregister();
        _capturing = true;
        _heldKeys.Clear();
        _captureMods = 0;
        _captureVk = null;

        _captureWindow = Window.GetWindow(this);
        if (_captureWindow is not null)
        {
            _captureWindow.PreviewKeyDown += OnCaptureKeyDown;
            _captureWindow.PreviewKeyUp += OnCaptureKeyUp;
            _captureWindow.PreviewMouseLeftButtonDown += OnCaptureMouseDown;
        }

        if (target == CaptureTarget.Cancel)
        {
            CancelHotkeyChip.SetResourceReference(Border.BorderBrushProperty, "HandyAccent");
            BeginCancelCapture();
        }
        else
        {
            HotkeyChip.SetResourceReference(Border.BorderBrushProperty, "HandyAccent");
            HotkeyText.Text = "press keys…";
            ClearHotkeyError();
        }
    }

    private void OnCaptureKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None)
        {
            return;
        }

        _heldKeys.Add(key);
        if (IsModifier(key, out var mask))
        {
            _captureMods |= mask;
        }
        else
        {
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk > 0)
            {
                _captureVk = (uint)vk;
            }
        }

        if (_captureTarget == CaptureTarget.Cancel)
        {
            CancelHotkeyText.Text = FormatCancelPreview();
        }
        else
        {
            HotkeyText.Text = FormatCapturePreview();
        }
    }

    private void OnCaptureKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _heldKeys.Remove(key);
        if (_heldKeys.Count == 0)
        {
            CommitCapture();
        }
    }

    private void OnCaptureMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        // Click-outside cancels: clicks on either chip start/continue
        // capture and must not cancel it.
        for (var current = e.OriginalSource as DependencyObject;
            current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, HotkeyChip)
                || ReferenceEquals(current, CancelHotkeyChip))
            {
                return;
            }
        }

        CancelCapture();
    }

    private void CommitCapture()
    {
        var mods = _captureMods;
        var vk = _captureVk;
        var previous = _previousChord;
        var target = _captureTarget;
        if (target == CaptureTarget.Cancel)
        {
            CommitCancelCapture(mods, vk);
            return;
        }

        EndCaptureMode();
        if (vk is null || mods == 0)
        {
            _ = HotkeyRegistrar.TryRegister(previous, out _);
            ShowHotkeyError($"That isn't a complete hotkey — hold at least one modifier (Ctrl, Alt, Shift, Win) plus a key. Kept {previous}.");
            return;
        }

        ApplyHotkey(HotkeyChord.Format(mods, vk.Value), previous);
    }

    private void CancelCapture()
    {
        var previous = _previousChord;
        var target = _captureTarget;
        // Capture suspends the LIVE DICTATION registration, so a cancelled
        // cancel-capture must re-arm the dictation chord — never the cancel
        // chord (a bare Esc would fail parse, kill dictation, and misreport).
        var restore = target == CaptureTarget.Cancel ? _settings.Hotkey : previous;
        EndCaptureMode();
        if (!HotkeyRegistrar.TryRegister(restore, out var error))
        {
            var message = string.IsNullOrEmpty(error)
                ? $"Couldn't restore {restore} — restart the app to re-arm the hotkey."
                : error;
            if (target == CaptureTarget.Cancel)
            {
                ShowCancelHotkeyError(message);
            }
            else
            {
                ShowHotkeyError(message);
            }
        }
    }

    private void EndCaptureMode()
    {
        if (!_capturing)
        {
            return;
        }

        _capturing = false;
        if (_captureWindow is not null)
        {
            _captureWindow.PreviewKeyDown -= OnCaptureKeyDown;
            _captureWindow.PreviewKeyUp -= OnCaptureKeyUp;
            _captureWindow.PreviewMouseLeftButtonDown -= OnCaptureMouseDown;
            _captureWindow = null;
        }

        _heldKeys.Clear();
        HotkeyChip.SetResourceReference(Border.BorderBrushProperty, "HandyCardBorder");
        CancelHotkeyChip.SetResourceReference(Border.BorderBrushProperty, "HandyCardBorder");
        RefreshHotkeyChip();
        RefreshCancelRow();
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e) =>
        ApplyHotkey(HotkeyChord.DefaultChord, _settings.Hotkey);

    /// <summary>
    /// Test seam (internal): registers then persists a chord; kept off the
    /// private event handlers so tests can drive the rollback path directly.
    /// </summary>
    internal void ApplyHotkey(string candidate, string rollbackTo)
    {
        if (!HotkeyRegistrar.TryRegister(candidate, out var error))
        {
            _ = HotkeyRegistrar.TryRegister(rollbackTo, out _);
            ShowHotkeyError(string.IsNullOrEmpty(error)
                ? $"Couldn't register {candidate} — kept {rollbackTo}."
                : $"{error} Kept {rollbackTo}.");
            return;
        }

        _settings.Hotkey = candidate;
        if (!TrySaveSettings(out var saveError))
        {
            ShowHotkeyError($"Hotkey is {candidate} for now, but saving failed: {saveError}");
        }
        else
        {
            ClearHotkeyError();
        }

        RefreshHotkeyChip();
    }

    private string FormatCancelPreview()
    {
        // Cancel accepts a bare key ("Esc") or a full chord: show the key as
        // soon as it lands so single-key capture has live feedback.
        if (_captureVk is uint cancelVk)
        {
            return _captureMods == 0
                ? HotkeyChord.KeyName(cancelVk)
                : HotkeyChord.Format(_captureMods, cancelVk);
        }

        return FormatCapturePreview();
    }

    private string FormatCapturePreview()
    {
        var parts = new List<string>();
        if ((_captureMods & HotkeyChord.ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((_captureMods & HotkeyChord.ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((_captureMods & HotkeyChord.ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((_captureMods & HotkeyChord.ModWin) != 0)
        {
            parts.Add("Win");
        }

        if (_captureVk is uint vk)
        {
            parts.Add(HotkeyChord.KeyName(vk));
        }

        return parts.Count == 0 ? "press keys…" : string.Join("+", parts);
    }

    private void RefreshHotkeyChip() => HotkeyText.Text = _settings.Hotkey;

    private void ShowHotkeyError(string message)
    {
        HotkeyError.Text = message;
        HotkeyError.Visibility = Visibility.Visible;
    }

    private void ClearHotkeyError() => HotkeyError.Visibility = Visibility.Collapsed;

    private static bool IsModifier(Key key, out uint mask)
    {
        switch (key)
        {
            case Key.LeftCtrl:
            case Key.RightCtrl:
                mask = HotkeyChord.ModControl;
                return true;
            case Key.LeftAlt:
            case Key.RightAlt:
                mask = HotkeyChord.ModAlt;
                return true;
            case Key.LeftShift:
            case Key.RightShift:
                mask = HotkeyChord.ModShift;
                return true;
            case Key.LWin:
            case Key.RWin:
                mask = HotkeyChord.ModWin;
                return true;
            default:
                mask = 0;
                return false;
        }
    }

    // Activation mode.

    private void ActivationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.ActivationMode = ActivationModes.ModeForLabel(ActivationBox.SelectedItem as string);
        TrySaveSettings(out _);
    }

    // Sound group.

    private void RefreshMicButton_Click(object sender, RoutedEventArgs e) => RefreshMicrophoneList();

    private void RefreshMicrophoneList()
    {
        MicBox.SelectionChanged -= MicBox_SelectionChanged;
        try
        {
            MicBox.Items.Clear();
            MicBox.Items.Add(MicrophoneDevices.SystemDefaultLabel);
            foreach (var name in _listMicrophones())
            {
                MicBox.Items.Add(name);
            }

            var stored = _settings.Microphone;
            if (string.IsNullOrEmpty(stored))
            {
                MicBox.SelectedItem = MicrophoneDevices.SystemDefaultLabel;
            }
            else if (MicBox.Items.Contains(stored))
            {
                MicBox.SelectedItem = stored;
            }
            else
            {
                // Device unplugged: keep the stored value visible so the
                // display matches what is persisted.
                var missing = stored + UnavailableSuffix;
                MicBox.Items.Add(missing);
                MicBox.SelectedItem = missing;
            }
        }
        finally
        {
            MicBox.SelectionChanged += MicBox_SelectionChanged;
        }
    }

    private void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var selected = MicBox.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)
            || selected.EndsWith(UnavailableSuffix, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Microphone = selected == MicrophoneDevices.SystemDefaultLabel ? "" : selected;
        TrySaveSettings(out _);
    }

    private void MuteCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.MuteWhileRecording = MuteCheck.IsChecked == true;
        TrySaveSettings(out _);
    }

    private void TestSoundButton_Click(object sender, RoutedEventArgs e)
    {
        // Keep playback off the UI thread. SoundFeedback selects the
        // persisted output device (or the system default when empty).
        SoundStatus.Text = "Playing test tone…";
        _ = Task.Run(() =>
        {
            try
            {
                SoundFeedback.PlayTestTone(_settings);
                SetSoundStatus("Test tone played ✓");
            }
            catch (Exception ex)
            {
                Logger.Warning($"speaker test failed: {ex.GetType().Name}");
                SetSoundStatus("Speaker test failed — check the output device.");
            }
        });
    }

    private void SetSoundStatus(string message)
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetSoundStatus(message)));
                return;
            }

            SoundStatus.Text = message;
        }
        catch
        {
            // The settings window may be closing while playback finishes.
        }
    }

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
    /// Raised after the view persists the shared <see cref="SettingsStore"/>
    /// (hotkey commit, activation pick, microphone, mute, channel, output,
    /// volume, feedback, cancel-key). App observes it to refresh tray chrome
    /// that is otherwise built once at startup. Not raised on save failure.
    /// </summary>
    internal event Action<SettingsStore>? Saved;
}
