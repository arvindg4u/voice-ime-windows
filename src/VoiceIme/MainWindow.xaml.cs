using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VoiceIme.Theme;

namespace VoiceIme;

/// <summary>
/// Severity of a footer/statu message. Drives colour plus an accompanying
/// glyph so state is never conveyed by colour alone.
/// </summary>
public enum StatusSeverity
{
    /// <summary>Nothing to report — neutral muted text, no icon.</summary>
    Neutral,
    /// <summary>Something worked (saved, copied, connected).</summary>
    Success,
    /// <summary>Worked with a caveat, or a setting that is stored but inert.</summary>
    Warning,
    /// <summary>The operation failed and the user may need to act.</summary>
    Error,
}

/// <summary>
/// Handy shell: 160px sidebar + scrollable section host + status footer.
/// Per-section Views/* plug in via <see cref="RegisterSectionView"/> (later
/// tasks) and activate via <see cref="NavigateTo"/>. Close hides to tray;
/// quit happens only through the tray menu (<see cref="PermitClose"/>).
/// All brushes come from Theme/HandyTheme.xaml via DynamicResource.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Attached flag marking the active nav button. Pure XAML trigger target:
    /// the HandyNavButton style shows the pink overlay when true, so theme
    /// flips update live without code-behind brush math.
    /// </summary>
    public static readonly DependencyProperty IsNavActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsNavActive",
            typeof(bool),
            typeof(MainWindow),
            new FrameworkPropertyMetadata(false));

    public static bool GetIsNavActive(DependencyObject target) =>
        (bool)target.GetValue(IsNavActiveProperty);

    public static void SetIsNavActive(DependencyObject target, bool value) =>
        target.SetValue(IsNavActiveProperty, value);

    private readonly Dictionary<MainSection, System.Windows.Controls.Button> _navButtons = new();
    private readonly Dictionary<MainSection, object> _sectionViews = new();
    private bool _permitClose;
    private SettingsStore? _firstRunHintSettings;
    private Action<SettingsStore>? _firstRunHintSaver;

    public MainSection CurrentSection { get; private set; } = MainSection.General;

    public MainWindow()
        : this(
            () => new Views.GeneralSettingsView(),
            () => new Views.GeminiSettingsView(),
            () => new Views.HistorySettingsView(),
            () => new Views.AdvancedSettingsView(),
            () => new Views.AboutSettingsView())
    {
    }

    /// <summary>
    /// Test seam: callers inject the General/Gemini/History/Advanced/About
    /// section views (headless tests pass no factory or a non-UI
    /// placeholder); null disables that section's registration so
    /// construction never requires a window station.
    /// </summary>
    internal MainWindow(
        Func<object?>? createGeneralView,
        Func<object?>? createGeminiView = null,
        Func<object?>? createHistoryView = null,
        Func<object?>? createAdvancedView = null,
        Func<object?>? createAboutView = null)
    {
        InitializeComponent();

        // Native chrome follows the Handy theme: WPF repaints the client area
        // via DynamicResource, but the Win32 title bar needs the DWM immersive
        // dark-mode attribute — set once the HWND exists and again on every
        // theme swap. Unhooked on Closed so test-constructed windows never
        // leak static-event subscriptions.
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        ThemeManager.ThemeApplied += OnThemeApplied;
        Closed += (_, _) => ThemeManager.ThemeApplied -= OnThemeApplied;

        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        BuildNav();
        var general = createGeneralView?.Invoke();
        if (general is not null)
        {
            RegisterSectionView(MainSection.General, general);
        }

        var gemini = createGeminiView?.Invoke();
        if (gemini is not null)
        {
            RegisterSectionView(MainSection.Gemini, gemini);
        }

        var history = createHistoryView?.Invoke();
        if (history is not null)
        {
            RegisterSectionView(MainSection.History, history);
        }

        var advanced = createAdvancedView?.Invoke();
        if (advanced is not null)
        {
            RegisterSectionView(MainSection.Advanced, advanced);
        }

        var about = createAboutView?.Invoke();
        if (about is not null)
        {
            RegisterSectionView(MainSection.About, about);
        }

        NavigateTo(MainSection.General);
    }

    /// <summary>
    /// Shows the section's registered view, or a placeholder until its
    /// Views/* screen lands. Safe to call before Show(). Must be called on
    /// the UI thread.
    /// </summary>
    public void NavigateTo(MainSection section)
    {
        CurrentSection = section;
        SectionHost.Content = _sectionViews.TryGetValue(section, out var view)
            ? view
            : BuildPlaceholder(section);
        RefreshNav();
    }

    /// <summary>
    /// Plugs a section's UserControl into the shell. Passing null removes the
    /// registration (placeholder returns). Refreshes live if that section is
    /// currently shown. Must be called on the UI thread.
    /// </summary>
    public void RegisterSectionView(MainSection section, object? view)
    {
        if (view is null)
        {
            _sectionViews.Remove(section);
        }
        else
        {
            _sectionViews[section] = view;
        }

        if (section == CurrentSection)
        {
            NavigateTo(section);
        }
    }

    /// <summary>
    /// The view registered for a section, if any. App wiring uses it to hand
    /// the General screen its live-hotkey channel (Task 3).
    /// </summary>
    internal object? SectionView(MainSection section) =>
        _sectionViews.TryGetValue(section, out var view) ? view : null;

    /// <summary>
    /// Mirrors the tray state into the footer. Thread-safe.
    ///
    /// Resets to <see cref="StatusSeverity.Neutral"/> — otherwise a stale error
    /// glyph and colour would survive into the next "ready" message, which is
    /// exactly the kind of quiet lie this footer exists to avoid.
    /// </summary>
    public void SetStatus(string text) => SetStatus(text, StatusSeverity.Neutral);

    /// <summary>
    /// Sets the footer message together with its severity.
    ///
    /// The footer previously rendered every message — "ready", "saved", and
    /// "couldn't save your settings" — in the same muted grey, so a failure was
    /// indistinguishable from a heartbeat. Severity now drives both the colour
    /// and an accompanying glyph, because colour alone must never be the only
    /// cue (WCAG 1.4.1). Thread-safe.
    /// </summary>
    public void SetStatus(string text, StatusSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetStatus(text, severity));
            return;
        }

        StatusText.Text = text;

        var (glyph, iconBrush, textBrush) = severity switch
        {
            StatusSeverity.Success => (MainNav.Glyphs.Success, "SuccessText", "SuccessText"),
            StatusSeverity.Warning => (MainNav.Glyphs.Warning, "WarningText", "WarningText"),
            StatusSeverity.Error => (MainNav.Glyphs.Error, "ErrorText", "ErrorText"),
            _ => (string.Empty, "TextMuted", "TextMuted"),
        };

        StatusIcon.Text = glyph;
        StatusIcon.SetResourceReference(TextBlock.ForegroundProperty, iconBrush);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, textBrush);
    }

    /// <summary>Convenience: report a failure through the footer.</summary>
    public void SetStatusError(string text) => SetStatus(text, StatusSeverity.Error);

    /// <summary>
    /// Permits the next close to proceed. Called only by the tray Quit path;
    /// every other close hides to tray instead.
    /// </summary>
    private void OnThemeApplied(HandyTheme _) => ThemeManager.ApplyWindowChrome(this);

    internal void PermitClose() => _permitClose = true;

    /// <summary>
    /// Guard consulted before a close-to-tray hide: when it returns false the
    /// close is cancelled and the window stays visible. App wires it to the
    /// live <c>ShowTrayIcon</c> setting — hiding with the icon off would
    /// strand the app invisible (no icon, no window). Null (tests, legacy
    /// construction) means hide freely, preserving prior behavior.
    /// </summary>
    internal Func<bool>? CanHideWindow { get; set; }

    /// <summary>Test seam: the sidebar button backing a section.</summary>
    internal System.Windows.Controls.Button NavButtonFor(MainSection section) => _navButtons[section];

    /// <summary>
    /// Binds the first-run hint banner (Task 9): visible while
    /// <c>SeenHint</c> is false, hidden once set. Called by App's static
    /// builder with the live store right after construction; tests inject a
    /// no-op saver so dismiss never touches disk. Binding never touches
    /// disk; the click handlers never throw (fail-fast null check here is a
    /// programmer-error guard, same as <see cref="SetStatus"/>).
    /// </summary>
    internal void BindFirstRunHint(SettingsStore settings, Action<SettingsStore>? saver = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _firstRunHintSettings = settings;
        _firstRunHintSaver = saver ?? (static s => s.Save());
        RefreshFirstRunHint();
    }

    /// <summary>Test seam: whether the first-run hint card is shown.</summary>
    internal bool IsFirstRunHintVisible =>
        FirstRunHintCard.Visibility == Visibility.Visible;

    private void RefreshFirstRunHint()
    {
        try
        {
            if (_firstRunHintSettings is null)
            {
                FirstRunHintCard.Visibility = Visibility.Collapsed;
                return;
            }

            // The banner is a setup prompt: its message depends on whether the
            // app is actually usable yet. With no API key the hotkey is dead,
            // so telling the user to "press it" would be a lie.
            FirstRunHintText.Text = string.IsNullOrWhiteSpace(_firstRunHintSettings.BaseUrl) ||
                                    _firstRunHintSettings.ApiKeys.Count == 0
                ? "Add your Gemini API key to start dictating. It stays on this PC, encrypted."
                : $"You're all set. Press {_firstRunHintSettings.Hotkey} to dictate from any app.";
            FirstRunHintCard.Visibility = _firstRunHintSettings.SeenHint
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch
        {
            // Banner refresh is best-effort — never break the shell.
            FirstRunHintCard.Visibility = Visibility.Collapsed;
        }
    }

    private void FirstRunHintSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Send the user where the blocking task actually lives — the API
            // key is in Speech, not General. The old handler navigated to
            // General, so "Open Settings" landed them on a screen with nothing
            // to fill in. Dismissal stays explicit.
            NavigateTo(MainSection.Gemini);
        }
        catch
        {
            // Handlers never throw.
        }
    }

    private void FirstRunHintDismissButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_firstRunHintSettings is null)
            {
                FirstRunHintCard.Visibility = Visibility.Collapsed;
                return;
            }

            _firstRunHintSettings.SeenHint = true;
            _firstRunHintSaver?.Invoke(_firstRunHintSettings);
            RefreshFirstRunHint();
        }
        catch
        {
            // Handlers never throw.
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_permitClose)
        {
            // Tray-guard: with the icon off, hiding strands the app invisible
            // (no icon, no window) — cancel the close and stay visible. Guard
            // exceptions fail closed (stay visible) rather than strand.
            bool canHide = true;
            try
            {
                canHide = CanHideWindow?.Invoke() != false;
            }
            catch
            {
                canHide = false;
            }

            e.Cancel = true;
            if (canHide)
            {
                Hide();
            }
        }

        base.OnClosing(e);
    }

    private void BuildNav()
    {
        foreach (var section in MainNav.Ordered)
        {
            var captured = section;
            var button = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("NavButton"),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            };

            // Nav rows previously had no accessible name, so a screen reader
            // announced an unnamed button containing a private-use glyph. Name
            // it, and expose the label as help text for good measure.
            AutomationProperties.SetName(button, MainNav.AutomationNameFor(captured));
            AutomationProperties.SetAutomationId(button, "Nav" + captured);

            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            // Decorative glyph: a plain TextBlock exposes no automation peer,
            // so it is invisible to a screen reader while the button's own
            // accessible name carries the meaning. No extra plumbing needed.
            var glyph = new TextBlock
            {
                Text = MainNav.IconFor(captured),
                Style = (Style)FindResource("NavGlyph"),
            };
            row.Children.Add(glyph);

            row.Children.Add(new TextBlock
            {
                Text = MainNav.LabelFor(captured),
                Style = (Style)FindResource("NavLabel"),
            });

            button.Content = row;
            button.Click += (_, _) => NavigateTo(captured);
            NavPanel.Children.Add(button);
            _navButtons[captured] = button;
        }
    }

    private void RefreshNav()
    {
        foreach (var (section, button) in _navButtons)
        {
            SetIsNavActive(button, section == CurrentSection);
        }
    }

    private static TextBlock BuildPlaceholder(MainSection section)
    {
        var placeholder = new TextBlock
        {
            Text = $"{MainNav.LabelFor(section)} settings are coming soon.",
            FontSize = 13,
            Margin = new Thickness(4),
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return placeholder;
    }
}
