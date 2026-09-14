using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using VoiceIme.Theme;

namespace VoiceIme;

/// <summary>
/// Tray-first application: a NotifyIcon owns the lifecycle, the settings window
/// opens on demand. Global hotkey (Ctrl+Shift+Space) toggles recording from any app.
/// Thin glue over <see cref="DictationCoordinator"/>: App executes effects
/// (capture, upload, overlay, tray, paste) while the coordinator owns
/// IDLE → RECORDING → UPLOADING → ERROR. No dictation logic lives here —
/// hotkey edges go in, commands come out, async stages report back.
/// </summary>
public partial class App : System.Windows.Application
{
    private const int HotkeyId = 0xB001;
    internal const int TrayTextLimit = 63;
    private NotifyIcon? _tray;
    private Notifier _notifier = new((_, _, _) => { });
    private HotkeyWindow? _hotkeyWindow;
    private readonly AudioRecorder _recorder = new();
    private readonly LlmClient _llm = new();
    private SettingsStore _settings = SettingsStore.Load();
    private readonly ClipboardStore _clips = new();
    private readonly DictationCoordinator _coordinator = new();
    private OverlayWindow? _overlay;
    private DispatcherTimer? _releaseTimer;
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Task 9 single-instance: the second instance asks the running one
        // to show its settings window (via the HotkeyWindow broadcast) and
        // exits — never a second tray icon.
        _singleInstance = new SingleInstance();
        if (!_singleInstance.Acquire())
        {
            _singleInstance.NotifyRunningInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        ThemeManager.ApplyTheme(ThemeManager.ResolveTheme(ThemeManager.SystemPreference));
        _settings = SettingsStore.Load();
        _recorder.AutoStopped += HandleAutoStop;
        _recorder.LevelChanged += level => _overlay?.SetLevel(level);
        _coordinator.ErrorRaised += HandleError;
        _coordinator.OperationCancelled += () =>
        {
            _overlay?.Hide();
            RefreshMenu();
            SetTray("Voice IME — ready");
        };

        // Resolves deferred hold-mode releases once the 50 ms grace elapses
        // (the coordinator returns StopRecording for a real hold). Idle cost
        // is one cheap HasPendingRelease check per tick.
        _releaseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DictationCoordinator.CancelPollIntervalMs),
        };
        _releaseTimer.Tick += (_, _) =>
        {
            if (_coordinator.Tick() == HotkeyCommand.StopRecording)
            {
                _ = StopAndTranscribeAsync();
            }
        };
        _releaseTimer.Start();

        _notifier = new Notifier((title, body, severity) =>
            _tray?.ShowBalloonTip(3000, title, body, ToToolTipIcon(severity)));
        _tray = new NotifyIcon
        {
            Text = TrayText(_settings.Hotkey),
            Visible = true,
            Icon = System.Drawing.SystemIcons.Information,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => HandleToggleInput();

        _hotkeyWindow = new HotkeyWindow(HotkeyId, HandleHotkeyPress, OpenSettings);
        RegisterStoredHotkey();
    }

    /// <summary>
    /// Handy menu order (Task 7): idle = version (disabled) | Copy Last
    /// Transcript | History… | Settings… (Ctrl+,) | Quit; busy
    /// (recording/uploading) = version | Cancel | Copy Last Transcript |
    /// History… | Settings… | Quit.
    /// Rows come from the pure <see cref="TrayMenu"/> model (unit-tested headless);
    /// this method only translates rows into WinForms items. Existing system
    /// icons stay — no binary assets in v1.
    /// </summary>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var busy = _coordinator.State is DictationState.Recording or DictationState.Uploading;
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var hasTranscript = _clips.Entries.Any(e => !string.IsNullOrEmpty(e.Text));
        foreach (var item in TrayMenu.Items(busy, "v" + version, hasTranscript))
        {
            menu.Items.Add(TranslateMenuItem(item));
        }

        return menu;
    }

    /// <summary>
    /// Rebuilds the tray menu after busy/idle or history transitions so the
    /// Cancel row and Copy enablement track state (Handy behavior).
    /// </summary>
    private void RefreshMenu()
    {
        if (_tray is null) return;
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    private ToolStripItem TranslateMenuItem(TrayMenuItem item)
    {
        if (item.IsSeparator)
        {
            return new ToolStripSeparator();
        }

        ToolStripMenuItem row = item.Action switch
        {
            TrayMenuAction.Cancel => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Exclamation.ToBitmap(),
                (_, _) => CancelRecording()),
            TrayMenuAction.CopyLastTranscript => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Application.ToBitmap(),
                (_, _) => CopyLastTranscript()),
            TrayMenuAction.History => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Application.ToBitmap(),
                (_, _) => OpenHistory()),
            TrayMenuAction.Settings => new ToolStripMenuItem(
                item.Label, System.Drawing.SystemIcons.Shield.ToBitmap(),
                (_, _) => OpenSettings()),
            TrayMenuAction.Quit => new ToolStripMenuItem(
                item.Label, null, (_, _) => Quit()),
            _ => new ToolStripMenuItem(item.Label),
        };
        row.Enabled = item.Enabled;
        return row;
    }

    private void CopyLastTranscript()
    {
        var latest = _clips.Entries.Count > 0 ? _clips.Entries[0] : null;
        if (latest is null || string.IsNullOrEmpty(latest.Text))
        {
            return;
        }

        try
        {
            System.Windows.Forms.Clipboard.SetText(latest.Text);
        }
        catch (Exception ex)
        {
            HandleError(new PasteError(), ex);
        }
    }

    private static ToolTipIcon ToToolTipIcon(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => ToolTipIcon.Error,
        NotificationSeverity.Warning => ToolTipIcon.Warning,
        _ => ToolTipIcon.Info,
    };

    /// <summary>
    /// Global hotkey delivers press edges only (WM_HOTKEY has no key-up), so
    /// it feeds <see cref="DictationCoordinator.OnHotkeyDown"/>. In toggle
    /// mode (the default) press starts / press stops; in hold modes the first
    /// press starts and stopping uses the overlay/tray Cancel affordances (or
    /// a future key hook feeding OnHotkeyUp) — cancel always drains to Idle.
    /// </summary>
    private void HandleHotkeyPress()
    {
        _coordinator.ActivationMode = _settings.ActivationMode;
        switch (_coordinator.OnHotkeyDown())
        {
            case HotkeyCommand.StartRecording:
                _ = StartRecordingAsync();
                break;
            case HotkeyCommand.StopRecording:
                _ = StopAndTranscribeAsync();
                break;
        }
    }

    /// <summary>
    /// Tray double-click has no release edge either, so it uses the
    /// never-debounced toggle entry: a quick stop right after a start works.
    /// </summary>
    private void HandleToggleInput()
    {
        _coordinator.ActivationMode = _settings.ActivationMode;
        switch (_coordinator.OnToggleInput())
        {
            case HotkeyCommand.StartRecording:
                _ = StartRecordingAsync();
                break;
            case HotkeyCommand.StopRecording:
                _ = StopAndTranscribeAsync();
                break;
        }
    }

    private void HandleAutoStop()
    {
        if (_coordinator.OnAutoStop() == HotkeyCommand.StopRecording)
        {
            _ = StopAndTranscribeAsync();
        }
    }

    /// <summary>
    /// Optimistic start: UI flips to recording immediately; a start failure
    /// rolls back through the coordinator, which surfaces the typed error.
    /// </summary>
    private async Task StartRecordingAsync()
    {
        if (_coordinator.State != DictationState.Recording) return;
        var generation = _coordinator.Generation;
        var token = _coordinator.SessionToken;
        var hotkeyAt = DateTimeOffset.UtcNow;
        RefreshMenu();
        SetTray("Voice IME — recording… tap hotkey to stop");
        EnsureOverlay();
        _overlay?.Show(OverlayPhase.Recording);
        if (MicrophoneDevices.ListNames().Count == 0)
        {
            _coordinator.ReportStartResult(
                generation, false, new RecordingError(RecordingErrorReason.NoDevice));
            return;
        }

        try
        {
            await Task.Run(() => _recorder.Start(), token);
            Logger.LogLatency("hotkey_to_capture", DateTimeOffset.UtcNow - hotkeyAt);
        }
        catch (OperationCanceledException)
        {
            // User cancelled before capture started — CancelCurrentOperation
            // already drained to idle; reconciling here would stick UI.
            _coordinator.NotifyUploadCancelled(generation);
        }
        catch (Exception ex)
        {
            _coordinator.ReportStartResult(
                generation, false, RecordingError.FromException(ex), ex);
        }
    }

    private async Task StopAndTranscribeAsync()
    {
        if (_coordinator.State != DictationState.Uploading) return;
        var generation = _coordinator.Generation;
        var token = _coordinator.SessionToken;
        RefreshMenu();
        SetTray("Voice IME — transcribing…");
        _overlay?.Show(OverlayPhase.Uploading);
        var stopAt = DateTimeOffset.UtcNow;
        byte[] wav;
        try
        {
            wav = await _recorder.StopAsync(token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (Exception ex)
        {
            _coordinator.ReportUploadFailed(
                generation, new RecordingError(RecordingErrorReason.Unknown), ex);
            return;
        }

        // Stage gate: a cancel that landed after StopAsync must not upload.
        if (token.IsCancellationRequested)
        {
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }

        Logger.LogLatency("capture_to_stop", DateTimeOffset.UtcNow - stopAt);

        // Empty audio (or a late stop from an ended session) discards — but a
        // cancelled-while-stopping session must not touch status text.
        if (_coordinator.ReportAudioCaptured(wav) == AudioCaptureOutcome.Discarded)
        {
            if (token.IsCancellationRequested)
            {
                _coordinator.NotifyUploadCancelled(generation);
                return;
            }

            RefreshMenu();
            _overlay?.Hide();
            SetTray("Voice IME — no audio captured");
            return;
        }

        await TranscribeAndPasteAsync(wav, generation, token);
    }

    /// <summary>
    /// Upload/transcribe/paste phase of <see cref="StopAndTranscribeAsync"/>.
    /// The session token threads into <see cref="LlmClient.TranscribeAsync"/>
    /// (Task 7 review follow-up), so cancel aborts the in-flight upload;
    /// per-stage gates plus the coordinator generation check mean a late
    /// completion can neither paste nor clobber Idle. All failures funnel
    /// through the coordinator, which raises the typed error once.
    /// </summary>
    private async Task TranscribeAndPasteAsync(
        byte[] wav, long generation, CancellationToken token)
    {
        string transcript;
        int usedIndex;
        var transcribeAt = DateTimeOffset.UtcNow;
        try
        {
            (transcript, usedIndex) = await _llm.TranscribeAsync(
                wav, _settings.ApiKeys, _settings.BaseUrl, _settings.Model,
                _settings.KeyCursor, _settings.CustomPrompt, ct: token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }
        catch (TranscribeException ex)
        {
            // Stale first: a late failure after cancel must stay silent —
            // no history row, no view touch, no error re-surface.
            if (token.IsCancellationRequested
                || generation != _coordinator.Generation
                || _coordinator.State != DictationState.Uploading)
            {
                _coordinator.NotifyUploadCancelled(generation);
                return;
            }

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

            _coordinator.ReportUploadFailed(generation, TranscriptionError.From(ex), ex);
            return;
        }
        catch (Exception ex)
        {
            // Same staleness first: a late unexpected throw must not surface.
            if (token.IsCancellationRequested
                || generation != _coordinator.Generation
                || _coordinator.State != DictationState.Uploading)
            {
                _coordinator.NotifyUploadCancelled(generation);
                return;
            }

            _coordinator.ReportUploadFailed(
                generation, new TranscriptionError("Something went wrong"), ex);
            return;
        }

        // Stage gate: a cancel that landed after transcribe must not paste.
        if (token.IsCancellationRequested)
        {
            _coordinator.NotifyUploadCancelled(generation);
            return;
        }

        _settings.KeyCursor = (_settings.ApiKeys.Count == 0)
            ? 0
            : (usedIndex + 1) % _settings.ApiKeys.Count;
        _settings.Save();
        Logger.LogLatency("capture_to_response", DateTimeOffset.UtcNow - transcribeAt);
        Logger.LogKeyUsed(usedIndex, _settings.ApiKeys);
        Logger.LogTranscriptReceived(transcript.Length);
        _clips.Add(transcript);
        RefreshMenu();
        var pasteAt = DateTimeOffset.UtcNow;
        try
        {
            NativeInput.PasteIntoFocusedWindow(transcript);
        }
        catch (Exception pasteEx)
        {
            _coordinator.ReportUploadFailed(generation, new PasteError(), pasteEx);
            return;
        }

        Logger.LogLatency("response_to_paste", DateTimeOffset.UtcNow - pasteAt);

        // Commit last: false means a newer session (or cancel) owns the
        // machine — never touch status on a stale completion.
        if (_coordinator.ReportUploadSucceeded(generation))
        {
            RefreshMenu();
            _overlay?.Hide();
            SetTray("Voice IME — pasted ✓");
        }
    }

    /// <summary>
    /// Single handler for all typed pipeline errors (Task 7 → Task 8
    /// contract): the coordinator already reverted atomically before raising,
    /// so this only shows — one balloon via <see cref="Notifier"/> plus the
    /// inline overlay error — then acknowledges back to Idle. Never leaves
    /// the overlay stuck and never raises UI from a MessageBox — all
    /// user-visible output routes through the Notifier. Inner exceptions
    /// are discarded: <see cref="IDictationError.UserMessage"/> strings are
    /// pre-approved safe, and raw exception text (which may embed key material
    /// or paths) must never reach the surface.
    /// </summary>
    private void HandleError(IDictationError error, Exception? cause = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        RefreshMenu();
        _overlay?.ShowError(error.UserMessage);
        _notifier.Notify(
            "Voice IME",
            error.UserMessage,
            NotificationSeverity.Error,
            secrets: _settings.ApiKeys);
        SetTray($"Voice IME — {error.UserMessage}");
        if (cause is not null)
        {
            // Type names only — raw exception text may embed key material.
            Logger.Error($"dictation failed error={error.GetType().Name} cause={cause.GetType().Name}");
        }

        _coordinator.AcknowledgeError();
    }

    /// <summary>
    /// Lazily creates the recording overlay (a Window needs a window station,
    /// so construction is deferred until first dictation). Cancel discards
    /// the in-flight capture and hides the overlay via the coordinator's
    /// <c>CancelCurrentOperation</c> entry point.
    /// </summary>
    private void EnsureOverlay()
    {
        if (_overlay is not null)
        {
            return;
        }

        _overlay = new OverlayWindow();
        _overlay.CancelRequested += CancelRecording;
    }

    /// <summary>
    /// Single cancel entry point in App: the coordinator aborts the session
    /// token (in-flight stop/transcribe drains through it) and drains to
    /// Idle, the recorder drops capture, and the OperationCancelled handler
    /// hides the overlay and returns the tray to ready.
    /// </summary>
    private void CancelRecording()
    {
        _coordinator.CancelCurrentOperation();
        _recorder.Cancel();
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
        if (window.SectionView(MainSection.General) is Views.GeneralSettingsView general)
        {
            if (_hotkeyWindow is not null)
            {
                general.HotkeyRegistrar = new LiveHotkeyRegistrar(_hotkeyWindow);
            }

            general.Saved -= RefreshTrayTextFromSave;
            general.Saved += RefreshTrayTextFromSave;
        }
    }

    private MainWindow? _mainWindow;

    /// <summary>
    /// The app's live settings store every section view must bind to. Shared-
    /// instance rule (F1): dictation reads SettingsStore state from here, so a
    /// view holding any other instance would edit settings dictation never
    /// sees — and this instance's key-cursor save would clobber the view's
    /// freshly saved keys. Internal so tests can assert the wiring.
    /// </summary>
    internal SettingsStore LiveSettings => _settings;

    /// <summary>
    /// The app's live transcript store every History view must bind to.
    /// Shared-instance rule (F2): same file, same divergence — a view on a
    /// private store shows stale transcripts and its pin/delete saves clobber
    /// transcripts dictated since the window opened.
    /// </summary>
    internal ClipboardStore LiveClips => _clips;

    /// <summary>
    /// Builds the settings shell with section views bound to the app's live
    /// stores (F1/F2 shared-instance rule — never parameterless Load()s).
    /// </summary>
    internal MainWindow CreateMainWindow() => CreateMainWindow(
        _settings,
        _clips,
        _hotkeyWindow is not null
            ? new LiveHotkeyRegistrar(_hotkeyWindow)
            : new NullHotkeyRegistrar(),
        MicrophoneDevices.ListNames);

    /// <summary>
    /// Static shell builder behind <see cref="CreateMainWindow"/> — the F1/F2
    /// wiring tests drive this overload directly so they assert the live-store
    /// bindings without constructing the singleton
    /// <see cref="System.Windows.Application"/> (only one may exist per
    /// process) or touching disk/DPAPI.
    /// </summary>
    internal static MainWindow CreateMainWindow(
        SettingsStore settings,
        ClipboardStore clips,
        IHotkeyRegistrar registrar,
        Func<IReadOnlyList<string>> listMicrophones)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(listMicrophones);
        return new(
            () => new Views.GeneralSettingsView(settings, registrar, listMicrophones),
            () => new Views.GeminiSettingsView(settings),
            () => new Views.HistorySettingsView(clips),
            () => new Views.AdvancedSettingsView(settings));
    }

    /// <summary>
    /// Opens the settings shell on the Gemini section (the rehomed legacy
    /// SettingsWindow content).
    /// </summary>
    internal void OpenSettings()
    {
        _mainWindow ??= CreateMainWindow();
        AttachLiveHotkey(_mainWindow);
        RefreshSectionViews(_mainWindow, _settings, _clips);
        _mainWindow.NavigateTo(MainSection.Gemini);
        ShowMainWindow();
    }

    /// <summary>
    /// Opens the main window on the History section (the rehomed legacy
    /// HistoryWindow content) — the Task 5 entry point, reachable from the
    /// tray History… row. The view is already bound to <see cref="LiveClips"/>
    /// at construction; <see cref="RefreshSectionViews"/> re-syncs it here so
    /// transcripts dictated while the window was hidden appear, and the
    /// settings fields reload from <see cref="LiveSettings"/>.
    /// </summary>
    internal void OpenHistory()
    {
        _mainWindow ??= CreateMainWindow();
        AttachLiveHotkey(_mainWindow);
        RefreshSectionViews(_mainWindow, _settings, _clips);
        _mainWindow.NavigateTo(MainSection.History);
        ShowMainWindow();
    }

    /// <summary>
    /// Re-syncs a shell's views with the shared stores before it is shown:
    /// the History view rebinds to the live clips (transcripts dictated while
    /// the window was hidden would otherwise never appear) and the
    /// General/Gemini fields reload from the live settings. Static with
    /// explicit stores so the wiring tests can drive it without constructing
    /// the singleton <see cref="System.Windows.Application"/>. Views built
    /// over App's instances observe the same objects dictation reads, so this
    /// is a display refresh, not a divergence repair.
    /// </summary>
    internal static void RefreshSectionViews(
        MainWindow? window, SettingsStore settings, ClipboardStore clips)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clips);
        if (window is null) return;
        if (window.SectionView(MainSection.History)
            is Views.HistorySettingsView history)
        {
            history.BindStore(clips);
        }

        if (window.SectionView(MainSection.General)
            is Views.GeneralSettingsView general)
        {
            general.ReloadFromSettings();
        }

        if (window.SectionView(MainSection.Gemini)
            is Views.GeminiSettingsView gemini)
        {
            gemini.ReloadFromSettings();
        }

        if (window.SectionView(MainSection.Advanced)
            is Views.AdvancedSettingsView advanced)
        {
            advanced.ReloadFromSettings();
        }
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

    /// <summary>
    /// The tray tooltip for a hotkey — WinForms caps tooltips at 63 chars, so
    /// this truncates (same rule as <see cref="SetTray"/>).
    /// </summary>
    internal static string TrayText(string hotkey) =>
        TruncateTrayText($"Voice IME — {hotkey} to dictate");

    /// <summary>WinForms caps tooltips at 63 chars — truncate, never throw.</summary>
    internal static string TruncateTrayText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length > TrayTextLimit ? text[..TrayTextLimit] : text;
    }

    /// <summary>
    /// Refreshes the tray tooltip from the live settings store — call after
    /// the hotkey changes (the tooltip is otherwise built once at startup).
    /// </summary>
    internal void RefreshTrayText()
    {
        if (_tray is null) return;
        _tray.Text = TrayText(_settings.Hotkey);
    }

    /// <summary>
    /// <see cref="Views.GeneralSettingsView.Saved"/> adapter: the event
    /// carries the saved store, the tooltip re-reads the live one.
    /// </summary>
    private void RefreshTrayTextFromSave(SettingsStore _) => RefreshTrayText();

    private void SetTray(string text)
    {
        if (_tray is null) return;
        _tray.Text = TruncateTrayText(text);
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
        _singleInstance?.Dispose();
        _singleInstance = null;
        _releaseTimer?.Stop();
        _coordinator.Dispose();
        _recorder.Cancel();
        _overlay?.Close();
        _hotkeyWindow?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _llm.Dispose();
        base.OnExit(e);
    }
}
