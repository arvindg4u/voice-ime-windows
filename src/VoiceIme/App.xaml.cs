using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using VoiceIme.Theme;

namespace VoiceIme;

/// <summary>
/// Tray-first application: a NotifyIcon owns the lifecycle, the settings window
/// opens on demand. Global hotkey (Ctrl+Shift+Space) toggles recording from any app.
/// All dictation state mirrors Android's IDLE → RECORDING → UPLOADING → ERROR machine.
/// </summary>
public partial class App : System.Windows.Application
{
    private const int HotkeyId = 0xB001;
    private NotifyIcon? _tray;
    private HotkeyWindow? _hotkeyWindow;
    private readonly AudioRecorder _recorder = new();
    private readonly LlmClient _llm = new();
    private SettingsStore _settings = SettingsStore.Load();
    private readonly ClipboardStore _clips = new();
    private CancellationTokenSource? _recordCts;
    private bool _recording;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.ApplyTheme(ThemeManager.ResolveTheme(ThemeManager.SystemPreference));
        _settings = SettingsStore.Load();
        _recorder.AutoStopped += () => _ = StopAndTranscribeAsync();

        _tray = new NotifyIcon
        {
            Text = $"Voice IME — {_settings.Hotkey} to dictate",
            Visible = true,
            Icon = System.Drawing.SystemIcons.Information,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ToggleAsync();

        _hotkeyWindow = new HotkeyWindow(HotkeyId, () => ToggleAsync());
        RegisterStoredHotkey();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add($"Dictate ({_settings.Hotkey})", null, (_, _) => ToggleAsync());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("History…", null, (_, _) => OpenHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        return menu;
    }

    private void ToggleAsync() => _ = _recording ? StopAndTranscribeAsync() : StartRecordingAsync();

    private async Task StartRecordingAsync()
    {
        if (_recording) return;
        _recording = true;
        _recordCts = new CancellationTokenSource();
        SetTray("Voice IME — recording… tap hotkey to stop", balloon: false);
        try
        {
            await Task.Run(() => _recorder.Start(), _recordCts.Token);
        }
        catch (Exception ex)
        {
            _recording = false;
            SetTray("Voice IME — microphone unavailable", balloon: true);
            System.Windows.MessageBox.Show($"Microphone unavailable: {ex.Message}", "Voice IME");
        }
    }

    private async Task StopAndTranscribeAsync()
    {
        if (!_recording) return;
        _recording = false;
        SetTray("Voice IME — transcribing…", balloon: false);
        byte[] wav;
        try
        {
            wav = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            SetTray("Voice IME — recording failed", balloon: true);
            System.Windows.MessageBox.Show($"No audio captured: {ex.Message}", "Voice IME");
            return;
        }
        if (wav.Length <= 44)
        {
            SetTray("Voice IME — no audio captured", balloon: true);
            return;
        }
        try
        {
            var (transcript, usedIndex) = await _llm.TranscribeAsync(
                wav, _settings.ApiKeys, _settings.BaseUrl, _settings.Model,
                _settings.KeyCursor, _settings.CustomPrompt);
            _settings.KeyCursor = (_settings.ApiKeys.Count == 0)
                ? 0
                : (usedIndex + 1) % _settings.ApiKeys.Count;
            _settings.Save();
            _clips.Add(transcript);
            NativeInput.PasteIntoFocusedWindow(transcript);
            SetTray("Voice IME — pasted ✓", balloon: false);
        }
        catch (TranscribeException ex)
        {
            // Failed rows persist as retryable empty-text entries (privacy:
            // audio is never persisted). The live History view picks up the
            // error text when it exists; otherwise the row still clears with
            // the store and shows a generic message when the view opens.
            var failed = _clips.Add(string.Empty);
            if (_mainWindow?.SectionView(MainSection.History)
                is Views.HistorySettingsView history)
            {
                history.AttachError(failed.Id, ex.Message);
            }

            SetTray($"Voice IME — {ex.Message}", balloon: true);
        }
        catch (Exception ex)
        {
            SetTray("Voice IME — error", balloon: true);
            System.Windows.MessageBox.Show(ex.Message, "Voice IME");
        }
    }

    // MainWindow singleton: Task 4 deleted SettingsWindow (its Base
    // URL/keys/model/prompt fields live in Views.GeminiSettingsView now);
    // Task 5 deleted HistoryWindow (history lives in
    // Views.HistorySettingsView, transit errors re-dictated — audio is
    // never persisted).
    //
    // Task 3: the stored hotkey owns the live global registration, not a
    // hardcoded chord — App registers whatever SettingsStore holds (invalid
    // values already coerce to the default on load), and hands the General
    // screen a LiveHotkeyRegistrar so capture suspends the real hotkey.
    private void RegisterStoredHotkey()
    {
        if (_hotkeyWindow is null)
        {
            return;
        }

        if (!HotkeyChord.TryParse(_settings.Hotkey, out var modifiers, out var vk))
        {
            _settings.Hotkey = HotkeyChord.DefaultChord;
            HotkeyChord.TryParse(_settings.Hotkey, out modifiers, out vk);
        }

        _hotkeyWindow.Register(modifiers, vk);
    }

    private void AttachLiveHotkey(MainWindow window)
    {
        if (window.SectionView(MainSection.General) is Views.GeneralSettingsView general
            && _hotkeyWindow is not null)
        {
            general.HotkeyRegistrar = new LiveHotkeyRegistrar(_hotkeyWindow);
        }
    }

    private MainWindow? _mainWindow;

    /// <summary>
    /// Opens the settings shell on the Gemini section (the rehomed legacy
    /// SettingsWindow content).
    /// </summary>
    internal void OpenSettings()
    {
        _mainWindow ??= new MainWindow();
        AttachLiveHotkey(_mainWindow);
        _mainWindow.NavigateTo(MainSection.Gemini);
        ShowMainWindow();
    }

    internal void OpenHistory()
    {
        _mainWindow ??= new MainWindow();
        if (_mainWindow.SectionView(MainSection.History)
            is Views.HistorySettingsView history)
        {
            history.BindStore(_clips);
        }

        _mainWindow.NavigateTo(MainSection.History);
        ShowMainWindow();
    }

    internal void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void SetTray(string text, bool balloon)
    {
        if (_tray is null) return;
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (balloon) _tray.ShowBalloonTip(3000, "Voice IME", text, ToolTipIcon.Info);
        _mainWindow?.SetStatus(text);
    }

    /// <summary>
    /// Quit happens only here: a hidden MainWindow cancels Close, so permit
    /// it explicitly before shutting down.
    /// </summary>
    private void Quit()
    {
        _mainWindow?.PermitClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _recorder.Cancel();
        _hotkeyWindow?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _llm.Dispose();
        base.OnExit(e);
    }
}
