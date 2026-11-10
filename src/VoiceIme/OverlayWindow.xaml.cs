using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VoiceIme;

/// <summary>
/// Handy compact-pill port: a topmost transparent pill (256x50 visible, plus a
/// transparent shadow margin around it) showing a
/// recording dot + waveform + timer, an uploading spinner ("Sending…"), or
/// an error message (auto-hides after <see cref="ErrorAutoHideSeconds"/>).
/// Bottom-center on the foreground window's monitor, 40 px above the taskbar.
///
/// The window is non-activating (<c>WS_EX_NOACTIVATE</c>) and stays out of
/// Alt+Tab (<c>WS_EX_TOOLWINDOW</c>) so it never steals focus from the app
/// being dictated into.
///
/// Contract for Tasks 7/8 — do not rename: <see cref="Show"/>,
/// <see cref="Hide"/>, <see cref="SetLevel"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    public const double RestingWidth = 256;
    public const double PillHeight = 50;
    public const double BottomOffset = 40;

    /// <summary>Transparent margin kept around the pill for its drop shadow.</summary>
    public const double ShadowMargin = 10;
    public const int WaveBarCount = 9;
    public const double WaveBarWidth = 4;
    public const double WaveBarGap = 3;

    /// <summary>
    /// How long an error stays on screen before auto-hiding. Long enough to
    /// read the recovery line for a paste failure; the tray balloon and History
    /// still carry it after it goes.
    /// </summary>
    public const double ErrorAutoHideSeconds = 8;

    // Win32 extended styles that keep this HUD from ever taking focus or
    // appearing in Alt+Tab. ShowActivated=False only covers the initial show;
    // WS_EX_NOACTIVATE covers a click on the buttons (an upstream Handy bug
    // was exactly "overlay steals focus and typing fails").
    private const int GwlExStyle = -20;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExToolWindow = 0x00000080;

    /// <summary>
    /// Pure: OR the non-activating/no-Alt+Tab extended styles onto an existing
    /// GWL_EXSTYLE value. Unit-tested without a window station.
    /// </summary>
    internal static int ApplyNonActivatingStyles(int currentExStyle) =>
        currentExStyle | WsExNoActivate | WsExToolWindow;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // Foreground-monitor placement.
    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>
    /// Most characters of transcript the preview keeps.
    ///
    /// <para>A single-line TextBlock is re-measured on every interim update, so
    /// without a bound the cost of each update grows with the length of the
    /// recording — a two-minute dictation reaches ~1,800 characters and every
    /// revision re-measures all of them. The window is far wider than the pill,
    /// so the cut is always scrolled off-screen and invisible; it exists purely
    /// to keep the measure O(1) per update.</para>
    /// </summary>
    public const int PreviewTailChars = 400;

    // Visibility cached in a Volatile flag so the ~30 Hz level callback does
    // one read, not a dispatcher call (Handy OVERLAY_ENABLED pattern).
    private int _visibleFlag;
    private volatile float _level;
    private volatile string _previewFinals = string.Empty;
    private volatile string _previewInterim = string.Empty;
    private string _mode = OverlayModes.Full;
    private OverlayState _state = OverlayState.Initial;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _errorTimer;
    private readonly DispatcherTimer _noticeTimer;

    // Tracks the phase last announced so a re-render for a notice does not
    // re-announce (a phase change always resets this in ShowCore).
    private OverlayPhase? _announcedPhase;
    private DateTime _phaseStartUtc = DateTime.UtcNow;
    private int _uploadTicks;
    private double _lastExtentWidth;
    private double _visibleWidth = RestingWidth;
    private readonly System.Windows.Shapes.Rectangle[] _bars = new System.Windows.Shapes.Rectangle[WaveBarCount];

    // Coalesces bursty interim updates into one render per dispatcher frame.
    private bool _previewRenderQueued;

    // Announcement/hint wording: the keys are configurable, so never hardcode.
    private string _cancelHotkey = SettingsStore.DefaultCancelHotkey;
    private string _dictationHotkey = HotkeyChord.DefaultChord;

    /// <summary>
    /// Reduced-motion seam: true when Windows animations are enabled. Swappable
    /// so tests can drive both branches without touching the OS.
    /// </summary>
    internal static Func<bool> AnimationsEnabledProvider = DetectAnimationsEnabled;

    private static bool DetectAnimationsEnabled()
    {
        try
        {
            return SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True when pulse/spin/slide animations may run.</summary>
    private static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return AnimationsEnabledProvider();
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>Invoked (UI thread) when the cancel button is pressed.</summary>
    public event Action? CancelRequested;

    /// <summary>Invoked (UI thread) when the finish (stop + paste) button is pressed.</summary>
    public event Action? FinishRequested;

    /// <summary>
    /// Test seam: replaces the real <see cref="SystemParameters.WorkArea"/>
    /// (headless tests have no window station).
    /// </summary>
    internal Func<Rect>? WorkAreaProvider { get; set; }

    public OverlayWindow()
    {
        InitializeComponent();
        for (var i = 0; i < WaveBarCount; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = WaveBarWidth,
                Height = 3,
                RadiusX = 2,
                RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(i == 0 ? 0 : WaveBarGap, 0, 0, 0),
            };
            bar.SetResourceReference(Shape.FillProperty, "AccentFill");
            WavePanel.Children.Add(bar);
            _bars[i] = bar;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshTimer();

        _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ErrorAutoHideSeconds) };
        _errorTimer.Tick += (_, _) => Hide();

        _noticeTimer = new DispatcherTimer();
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _state = _state with { Notice = null };
            Render();
        };

        // WS_EX_NOACTIVATE/TOOLWINDOW: ShowActivated=False is not enough once
        // the user can click the buttons.
        SourceInitialized += (_, _) => ApplyNonActivatingWindowStyles();
    }

    private void ApplyNonActivatingWindowStyles()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            SetWindowLong(hwnd, GwlExStyle, ApplyNonActivatingStyles(GetWindowLong(hwnd, GwlExStyle)));
        }
        catch
        {
            // Best effort — a failure only means the default activation policy.
        }
    }

    /// <summary>
    /// Sets the keys used in the screen-reader announcement and (future) hints.
    /// Blank values fall back to the app defaults. Thread-safe.
    /// </summary>
    public void Configure(string? cancelHotkey, string? dictationHotkey)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Configure(cancelHotkey, dictationHotkey));
            return;
        }

        _cancelHotkey = string.IsNullOrWhiteSpace(cancelHotkey)
            ? SettingsStore.DefaultCancelHotkey
            : cancelHotkey.Trim();
        _dictationHotkey = string.IsNullOrWhiteSpace(dictationHotkey)
            ? HotkeyChord.DefaultChord
            : dictationHotkey.Trim();
    }

    /// <summary>
    /// Shows a transient notice in the pill's center slot (the first-run key
    /// hint or the elevated-target warning). Passing null/blank clears it.
    /// When <paramref name="duration"/> is set the notice auto-clears, yielding
    /// the slot back to the waveform/preview. Thread-safe; no-op when hidden.
    /// </summary>
    public void SetNotice(string? notice, bool isWarning = false, TimeSpan? duration = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetNotice(notice, isWarning, duration));
            return;
        }

        if (Volatile.Read(ref _visibleFlag) == 0 && !string.IsNullOrWhiteSpace(notice))
        {
            return;
        }

        _state = _state.WithNotice(notice, isWarning);
        _noticeTimer.Stop();
        if (_state.Notice is not null && duration is TimeSpan d && d > TimeSpan.Zero)
        {
            _noticeTimer.Interval = d;
            _noticeTimer.Start();
        }

        Render();
    }

    /// <summary>Shows the overlay in the given phase and positions it. Thread-safe.</summary>
    public void Show(OverlayPhase phase) => Show(phase, OverlayModes.Full);

    /// <summary>
    /// Task 8 mode-aware show (Handy show_overlay parity): "minimal" raises
    /// the pill with a compact level cue, "full" shows the whole waveform.
    /// "none" never reaches here — App gates on
    /// <see cref="OverlayModes.ShouldShowPill"/> before calling — and coerces
    /// to full defensively if it does. Thread-safe.
    /// </summary>
    public void Show(OverlayPhase phase, string? mode) => ShowCore(phase, mode, message: null);

    /// <summary>
    /// Shows the overlay in the <see cref="OverlayPhase.Error"/> phase with a
    /// message; auto-hides after <see cref="ErrorAutoHideSeconds"/>. Thread-safe.
    /// </summary>
    public void ShowError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ShowCore(OverlayPhase.Error, OverlayModes.Full, message);
    }

    /// <summary>
    /// Single render/announce entry point for every phase. Previously
    /// <see cref="ShowError"/> rendered twice, so the screen reader heard a
    /// throwaway "Something went wrong" before the real message. Thread-safe.
    /// </summary>
    private void ShowCore(OverlayPhase phase, string? mode, string? message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowCore(phase, mode, message));
            return;
        }

        _mode = OverlayModes.Coerce(mode);
        _state = OverlayState.Initial.WithPhase(phase, message);
        _level = 0f;
        _previewFinals = string.Empty;
        _previewInterim = string.Empty;
        _lastExtentWidth = 0;
        _phaseStartUtc = DateTime.UtcNow;
        _uploadTicks = 0;
        _previewRenderQueued = false;
        _noticeTimer.Stop();
        _announcedPhase = null;
        Render();
        PositionBottomCenter();
        _timer.Start();
        if (phase == OverlayPhase.Error)
        {
            _errorTimer.Start();
        }
        else
        {
            _errorTimer.Stop();
        }

        if (!IsVisible)
        {
            base.Show();
        }

        Volatile.Write(ref _visibleFlag, 1);
        StartEntrance();
    }

    /// <summary>Hides the overlay. Thread-safe.</summary>
    public new void Hide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Hide);
            return;
        }

        _timer.Stop();
        _errorTimer.Stop();
        _noticeTimer.Stop();
        Volatile.Write(ref _visibleFlag, 0);
        if (!IsVisible)
        {
            return;
        }

        if (!AnimationsEnabled)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            base.Hide();
            return;
        }

        StartExit();
    }

    /// <summary>Fades the pill in on show (no-op under reduced motion).</summary>
    private void StartEntrance()
    {
        BeginAnimation(OpacityProperty, null);
        if (!AnimationsEnabled)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Fades the pill out, then hides. The completion checks the visibility
    /// flag so a show that raced in during the fade is not hidden afterwards.
    /// </summary>
    private void StartExit()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (Volatile.Read(ref _visibleFlag) != 0)
            {
                return;
            }

            try
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;
                if (IsVisible)
                {
                    base.Hide();
                }
            }
            catch
            {
                // Closed mid-fade — nothing to hide.
            }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Feeds a perceptual 0..1 mic level into the waveform. Called at ~30 Hz
    /// from AudioRecorder.LevelChanged: a single Volatile read when hidden,
    /// else a dispatcher post — never blocks the capture thread.
    /// </summary>
    public void SetLevel(float level)
    {
        if (Volatile.Read(ref _visibleFlag) == 0)
        {
            return;
        }

        _level = Math.Clamp(level, 0f, 1f);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RenderWaveform);
            return;
        }

        RenderWaveform();
    }

    /// <summary>
    /// Shows the streaming hypothesis during Recording, as a rolling tail: the
    /// text is left-aligned in a horizontal scroller whose offset is pinned to
    /// the end, so new words enter from the right and old ones leave the left.
    /// Thread-safe via the <see cref="SetLevel"/> dispatcher pattern; empty
    /// strings clear. Never shows outside <see cref="OverlayPhase.Recording"/>.
    /// </summary>
    /// <param name="finals">Settled transcript, rendered in the primary ink.</param>
    /// <param name="interim">Provisional hypothesis, rendered dimmed.</param>
    public void SetPreview(string finals, string interim)
    {
        ArgumentNullException.ThrowIfNull(finals);
        ArgumentNullException.ThrowIfNull(interim);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetPreview(finals, interim));
            return;
        }

        if (Volatile.Read(ref _visibleFlag) == 0)
        {
            return;
        }

        _previewFinals = finals;
        _previewInterim = interim;

        // Coalesce bursty interim tokens into one render per dispatcher frame:
        // the render forces UpdateLayout, which is the expensive part.
        if (_previewRenderQueued)
        {
            return;
        }

        _previewRenderQueued = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _previewRenderQueued = false;
                RenderPreview();
            }),
            DispatcherPriority.Render);
    }

    /// <summary>
    /// Overload that takes the pre-joined single-line form. Kept so callers and
    /// tests can push one string; it is split back apart for two-tone rendering
    /// by treating everything as settled text.
    /// </summary>
    public void SetPreview(string previewText)
    {
        ArgumentNullException.ThrowIfNull(previewText);
        SetPreview(previewText, string.Empty);
    }

    /// <summary>Current phase snapshot (pure <see cref="OverlayState"/>).</summary>
    internal OverlayState Snapshot => _state with { Level = _level };

    /// <summary>
    /// Computes the pill's top-left for bottom-center placement above the
    /// taskbar. Pure in (work area, window size) — unit-testable on any OS.
    /// </summary>
    internal static (double Left, double Top) ComputePosition(
        Rect workArea, double windowWidth, double windowHeight) =>
        (workArea.Left + (workArea.Width - windowWidth) / 2,
         workArea.Bottom - windowHeight - BottomOffset);

    /// <summary>
    /// Converts a physical-pixel work area to DIPs at the given scale. Pure and
    /// unit-tested; a non-positive scale is treated as 1:1.
    /// </summary>
    internal static Rect PhysicalWorkAreaToDips(
        int left, int top, int right, int bottom, double scale)
    {
        if (scale <= 0)
        {
            scale = 1;
        }

        return new Rect(
            left / scale,
            top / scale,
            (right - left) / scale,
            (bottom - top) / scale);
    }

    /// <summary>
    /// Work area of the monitor holding the foreground window, in DIPs, so the
    /// pill appears where the user is working instead of always on the primary
    /// display. Falls back to <see cref="SystemParameters.WorkArea"/> when the
    /// monitor cannot be determined or its DPI differs from the primary's
    /// (mixed-DPI WPF virtual-screen coordinate math is not worth a mis-placed
    /// pill). Never throws.
    /// </summary>
    private static Rect PreferredWorkArea()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return SystemParameters.WorkArea;
            }

            var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return SystemParameters.WorkArea;
            }

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return SystemParameters.WorkArea;
            }

            var dpi = GetDpiForWindow(foreground);
            var systemDpi = GetDpiForSystem();
            if (dpi == 0 || systemDpi == 0 || dpi != systemDpi)
            {
                return SystemParameters.WorkArea;
            }

            var work = info.Work;
            return PhysicalWorkAreaToDips(
                work.Left, work.Top, work.Right, work.Bottom, dpi / 96.0);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private void PositionBottomCenter()
    {
        var area = WorkAreaProvider is not null
            ? WorkAreaProvider()
            : PreferredWorkArea();
        var (left, top) = ComputePosition(area, _visibleWidth, PillHeight);
        // ComputePosition places the visible pill; shift so the transparent
        // shadow margin sits outside it.
        Left = left - ShadowMargin;
        Top = top - ShadowMargin;
    }

    private void Render()
    {
        var recording = _state.Phase == OverlayPhase.Recording;
        var uploading = _state.Phase == OverlayPhase.Uploading;
        var error = _state.Phase == OverlayPhase.Error;

        // Constant visible width across phases: a pill that narrows when it
        // starts uploading reads as a glitch. The window adds the shadow margin
        // on all four sides.
        _visibleWidth = RestingWidth;
        Width = _visibleWidth + (2 * ShadowMargin);

        StatusDot.Visibility = recording || error ? Visibility.Visible : Visibility.Collapsed;
        if (recording || error)
        {
            StatusDot.SetResourceReference(
                Shape.FillProperty, recording ? "AccentFill" : "ErrorText");
            if (AnimationsEnabled)
            {
                StatusDot.BeginStoryboard(
                    (System.Windows.Media.Animation.Storyboard)FindResource("DotPulse"));
            }
            else
            {
                // Reduced motion: hold the dot at full opacity, no pulse.
                StatusDot.BeginAnimation(UIElement.OpacityProperty, null);
                StatusDot.Opacity = 1;
            }
        }
        else
        {
            // Drop the pulse clock so it doesn't keep animating while hidden.
            StatusDot.BeginAnimation(UIElement.OpacityProperty, null);
        }

        Spinner.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        if (uploading && AnimationsEnabled)
        {
            Spinner.BeginStoryboard(
                (System.Windows.Media.Animation.Storyboard)FindResource("SpinnerSpin"));
        }
        else
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        // Center slot precedence while Recording: live preview, else the level
        // cue. "minimal" keeps a compact cue; "full" shows the whole waveform.
        RenderPreview();
        SendingLabel.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        // The timer shows the capture duration while recording and keeps
        // climbing while uploading (indeterminate — no backend streaming).
        TimerText.Visibility = recording || uploading ? Visibility.Visible : Visibility.Collapsed;
        // Finish (stop + paste) is only meaningful while recording; during
        // upload there is nothing to finish (cancel still aborts).
        FinishButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        if (error)
        {
            ErrorText.Text = _state.Message ?? string.Empty;
        }

        RefreshTimer();
        RenderWaveform();

        // Announce the phase transition to a screen reader. The dictation pill
        // is the only feedback a blind user gets that recording is live, and it
        // is non-focusable so it can be discovered no other way. Only the
        // phase is announced — never the interim transcript, which would be
        // constant chatter.
        AnnouncePhase();
    }

    /// <summary>
    /// Announces the phase transition to assistive technology.
    ///
    /// <para>The pill is the only signal that recording is live, and it is
    /// deliberately non-focusable, so a screen-reader user cannot reach it by
    /// navigation — a forced notification is the only channel. Only the phase
    /// is announced; the streaming transcript never is, because announcing
    /// every interim token is the "chatty" failure WCAG 4.1.3 warns about.</para>
    /// </summary>
    private void AnnouncePhase()
    {
        // Render runs again for notice updates (set/expire) without a phase
        // change; announcing only on transition keeps the screen reader from
        // repeating itself.
        if (_announcedPhase == _state.Phase)
        {
            return;
        }

        _announcedPhase = _state.Phase;
        var message = _state.Phase switch
        {
            OverlayPhase.Recording =>
                $"Recording. Press {_cancelHotkey} to cancel, {_dictationHotkey} to finish.",
            OverlayPhase.Uploading => "Transcribing.",
            OverlayPhase.Error => _state.Message ?? "Dictation failed.",
            _ => null,
        };

        // Errors are worth interrupting for; recording/sending are not.
        LiveRegion.Notify(this, message!, important: _state.Phase == OverlayPhase.Error);
    }

    private void RefreshTimer()
    {
        if (_state.Phase == OverlayPhase.Recording)
        {
            TimerText.Text = OverlayState.FormatElapsed(DateTime.UtcNow - _phaseStartUtc);
            return;
        }

        if (_state.Phase != OverlayPhase.Uploading)
        {
            return;
        }

        // Indeterminate upload: elapsed readout on the right, and the label
        // bumps one dot per 1 s tick on the shared timer.
        TimerText.Text = OverlayState.FormatElapsed(DateTime.UtcNow - _phaseStartUtc);
        SendingLabel.Text = OverlayState.FormatUploadingLabel(_uploadTicks);
        _uploadTicks++;
    }

    /// <summary>
    /// Renders the interim preview in the center slot while Recording with
    /// non-empty text (taking the slot from the waveform); collapsed
    /// otherwise, so Uploading/Error never show it. UI thread only.
    /// </summary>
    private void RenderPreview()
    {
        var finals = _previewFinals;
        var interim = _previewInterim;
        var showPreview = _state.Phase == OverlayPhase.Recording
            && (finals.Length > 0 || interim.Length > 0);
        PreviewScroll.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        if (showPreview)
        {
            // Bound the working text before it reaches layout.
            var (finalsRun, interimRun) = OverlayState.PreviewRuns(
                OverlayState.TailWindow(finals, PreviewTailChars),
                OverlayState.TailWindow(interim, PreviewTailChars));
            PreviewFinals.Text = finalsRun;
            PreviewInterim.Text = interimRun;
            ScrollPreviewToTail();
            SlideInNewText();
        }

        // A transient notice (first-run key hint / elevated warning) takes the
        // center slot ahead of the level cue.
        var notice = _state.Phase == OverlayPhase.Recording && !showPreview ? _state.Notice : null;
        if (notice is null)
        {
            NoticeText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoticeText.Text = notice;
            NoticeText.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                _state.NoticeIsWarning ? "WarningText" : "TextMuted");
            NoticeText.Visibility = Visibility.Visible;
        }

        var showWaveform = _state.Phase == OverlayPhase.Recording
            && !showPreview
            && notice is null;
        WavePanel.Visibility = showWaveform ? Visibility.Visible : Visibility.Collapsed;

        // "minimal" keeps a compact 3-bar level cue (the recording is still
        // visible); "full" shows all nine bars.
        var full = string.Equals(_mode, OverlayModes.Full, StringComparison.Ordinal);
        for (var i = 0; i < _bars.Length; i++)
        {
            var keep = full || i is >= 3 and <= 5;
            _bars[i].Visibility = showWaveform && keep ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Pins the preview scroller to the end so the newest words are the visible
    /// ones.
    ///
    /// <para>Layout is forced first because the extent is only recalculated
    /// during the measure pass — scrolling before that clamps to the *previous*
    /// extent and leaves the pill one update behind. The deferred call is
    /// belt-and-braces for the case where the scroller just became visible in
    /// this same pass.</para>
    ///
    /// <para><c>double.MaxValue</c> is used rather than the computed width: it
    /// clamps to <c>ScrollableWidth</c>, so it is correct regardless of any
    /// drift in our own arithmetic — and Display-mode text metrics mean a
    /// hand-computed width would not match anyway.</para>
    /// </summary>
    private void ScrollPreviewToTail()
    {
        PreviewScroll.UpdateLayout();
        Dispatcher.BeginInvoke(
            new Action(() => PreviewScroll.ScrollToHorizontalOffset(double.MaxValue)),
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Slides the text left by exactly the amount it grew, so new words appear
    /// to push in from the right instead of teleporting into place.
    ///
    /// <para>Deliberately a <see cref="TranslateTransform"/> and not an
    /// animation of the scroll offset: the offset is a read-only DP, and
    /// animating a helper value at the overlay's update rate would have
    /// successive animations fighting each other. A render transform leaves
    /// scroll state untouched, so an update arriving mid-flight cannot desync
    /// the pinned position.</para>
    /// </summary>
    private void SlideInNewText()
    {
        var extent = PreviewText.ActualWidth;
        var growth = extent - _lastExtentWidth;
        _lastExtentWidth = extent;

        // Nothing was added (or the text shrank as the hypothesis was
        // corrected), or the user asked for reduced motion — no slide either way.
        if (!AnimationsEnabled || growth <= 0.5)
        {
            PreviewText.RenderTransform = null;
            return;
        }

        var slide = new TranslateTransform(growth, 0);
        PreviewText.RenderTransform = slide;
        var animation = new DoubleAnimation(growth, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        slide.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void RenderWaveform()
    {
        if (_state.Phase != OverlayPhase.Recording)
        {
            return;
        }

        var level = Math.Clamp(_level, 0f, 1f);
        for (var i = 0; i < _bars.Length; i++)
        {
            var edge = 1f - Math.Abs(i - (WaveBarCount - 1) / 2f) / ((WaveBarCount + 1) / 2f);
            var height = 3 + Math.Pow(level * edge, 0.7) * 15;
            _bars[i].Height = Math.Max(3, Math.Min(18, height));
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke();

    private void OnFinishClick(object sender, RoutedEventArgs e) =>
        FinishRequested?.Invoke();
}
