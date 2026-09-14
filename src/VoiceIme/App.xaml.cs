using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

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
        _settings = SettingsStore.Load();
        _recorder.AutoStopped += () => _ = StopAndTranscribeAsync();

        _tray = new NotifyIcon
        {
            Text = "Voice IME — Ctrl+Shift+Space to dictate",
            Visible = true,
            Icon = System.Drawing.SystemIcons.Information,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ToggleAsync();

        _hotkeyWindow = new HotkeyWindow(HotkeyId, () => ToggleAsync());
        _hotkeyWindow.Register(0x0004 /* MOD_SHIFT */ | NativeInput.ModControl, 0x20 /* SPACE */);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Dictate (Ctrl+Shift+Space)", null, (_, _) => ToggleAsync());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("History…", null, (_, _) => OpenHistory());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());
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
            SetTray($"Voice IME — {ex.Message}", balloon: true);
        }
        catch (Exception ex)
        {
            SetTray("Voice IME — error", balloon: true);
            System.Windows.MessageBox.Show(ex.Message, "Voice IME");
        }
    }

    private void OpenSettings()
    {
        foreach (System.Windows.Window w in Windows)
            if (w is SettingsWindow) { w.Activate(); return; }
        new SettingsWindow().Show();
    }

    private void OpenHistory()
    {
        foreach (System.Windows.Window w in Windows)
            if (w is HistoryWindow) { w.Activate(); return; }
        new HistoryWindow(_clips).Show();
    }

    private void SetTray(string text, bool balloon)
    {
        if (_tray is null) return;
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (balloon) _tray.ShowBalloonTip(3000, "Voice IME", text, ToolTipIcon.Info);
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
