using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VoiceIme;

/// <summary>
/// Handy compact-pill port: topmost transparent 256x50 overlay showing a
/// recording dot + waveform + timer, an uploading spinner ("Sending…"), or
/// an error message (auto-hides after 3 s). Bottom-center above the taskbar
/// (Win32 work-area based), 40 px bottom offset.
///
/// Contract for Tasks 7/8 — do not rename: <see cref="Show"/>,
/// <see cref="Hide"/>, <see cref="SetLevel"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    public const double RestingWidth = 256;
    public const double WorkingWidth = 216;
    public const double PillHeight = 50;
    public const double BottomOffset = 40;
    public const int WaveBarCount = 9;
    public const double WaveBarWidth = 4;
    public const double WaveBarGap = 3;
    public const double ErrorAutoHideSeconds = 3;

    // Visibility cached in a Volatile flag so the ~30 Hz level callback does
    // one read, not a dispatcher call (Handy OVERLAY_ENABLED pattern).
    private int _visibleFlag;
    private volatile float _level;
    private string _mode = OverlayModes.Full;
    private OverlayState _state = OverlayState.Initial;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _errorTimer;
    private DateTime _phaseStartUtc = DateTime.UtcNow;
    private int _uploadTicks;
    private readonly System.Windows.Shapes.Rectangle[] _bars = new System.Windows.Shapes.Rectangle[WaveBarCount];

    /// <summary>Invoked (UI thread) when the cancel button is pressed.</summary>
    public event Action? CancelRequested;

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
            bar.SetResourceReference(Shape.FillProperty, "HandyAccent");
            WavePanel.Children.Add(bar);
            _bars[i] = bar;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshTimer();

        _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ErrorAutoHideSeconds) };
        _errorTimer.Tick += (_, _) => Hide();
    }

    /// <summary>Shows the overlay in the given phase and positions it. Thread-safe.</summary>
    public void Show(OverlayPhase phase) => Show(phase, OverlayModes.Full);

    /// <summary>
    /// Task 8 mode-aware show (Handy show_overlay parity): "minimal" raises
    /// the pill without the waveform bars, "full" is unchanged. "none" never
    /// reaches here — App gates on <see cref="OverlayModes.ShouldShowPill"/>
    /// before calling — and coerces to full defensively if it does.
    /// Thread-safe.
    /// </summary>
    public void Show(OverlayPhase phase, string? mode)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(phase, mode));
            return;
        }

        _mode = OverlayModes.Coerce(mode);

        _state = OverlayState.Initial.WithPhase(
            phase, phase == OverlayPhase.Error ? "Something went wrong" : null);
        _level = 0f;
        _phaseStartUtc = DateTime.UtcNow;
        _uploadTicks = 0;
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
    }

    /// <summary>
    /// Shows the overlay in the <see cref="OverlayPhase.Error"/> phase with a
    /// message; auto-hides after <see cref="ErrorAutoHideSeconds"/>. Thread-safe.
    /// </summary>
    public void ShowError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowError(message));
            return;
        }

        Show(OverlayPhase.Error);
        _state = _state.WithPhase(OverlayPhase.Error, message);
        Render();
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
        Volatile.Write(ref _visibleFlag, 0);
        if (IsVisible)
        {
            base.Hide();
        }
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

    private void PositionBottomCenter()
    {
        var area = WorkAreaProvider is not null
            ? WorkAreaProvider()
            : SystemParameters.WorkArea;
        var (left, top) = ComputePosition(area, Width, Height);
        Left = left;
        Top = top;
    }

    private void Render()
    {
        var recording = _state.Phase == OverlayPhase.Recording;
        var uploading = _state.Phase == OverlayPhase.Uploading;
        var error = _state.Phase == OverlayPhase.Error;

        Width = uploading ? WorkingWidth : RestingWidth;

        StatusDot.Visibility = recording || error ? Visibility.Visible : Visibility.Collapsed;
        if (recording || error)
        {
            StatusDot.SetResourceReference(
                Shape.FillProperty, recording ? "HandyAccent" : "HandyError");
            StatusDot.BeginStoryboard(
                (System.Windows.Media.Animation.Storyboard)FindResource("DotPulse"));
        }
        else
        {
            // Drop the pulse clock so it doesn't keep animating while hidden.
            StatusDot.BeginAnimation(UIElement.OpacityProperty, null);
        }

        Spinner.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        if (uploading)
        {
            Spinner.BeginStoryboard(
                (System.Windows.Media.Animation.Storyboard)FindResource("SpinnerSpin"));
        }
        else
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        // Task 8 "minimal" mode: the pill raises, but the waveform bars stay
        // hidden — recording still shows dot + timer, uploading is untouched.
        var showWaveform = recording
            && string.Equals(_mode, OverlayModes.Full, StringComparison.Ordinal);
        WavePanel.Visibility = showWaveform ? Visibility.Visible : Visibility.Collapsed;
        SendingLabel.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        // The timer shows the capture duration while recording and keeps
        // climbing while uploading (indeterminate — no backend streaming).
        TimerText.Visibility = recording || uploading ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        if (error)
        {
            ErrorText.Text = _state.Message ?? string.Empty;
        }

        RefreshTimer();
        RenderWaveform();
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
}
