using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace VoiceIme;

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

    /// <summary>Mirrors the tray state into the footer. Thread-safe.</summary>
    public void SetStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetStatus(text));
            return;
        }

        StatusText.Text = text;
    }

    /// <summary>
    /// Permits the next close to proceed. Called only by the tray Quit path;
    /// every other close hides to tray instead.
    /// </summary>
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

            FirstRunHintText.Text =
                $"Press {_firstRunHintSettings.Hotkey} to dictate.";
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
            // Navigate without dismissing — dismiss is explicit only.
            NavigateTo(MainSection.General);
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
                Style = (Style)FindResource("HandyNavButton"),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            };
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = MainNav.IconFor(captured),
                FontSize = 14,
                Width = 24,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = MainNav.LabelFor(captured),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
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
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "HandyMidGray");
        return placeholder;
    }
}
