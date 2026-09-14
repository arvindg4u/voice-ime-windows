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

    private readonly Dictionary<MainSection, Button> _navButtons = new();
    private readonly Dictionary<MainSection, object> _sectionViews = new();
    private bool _permitClose;

    public MainSection CurrentSection { get; private set; } = MainSection.General;

    public MainWindow()
        : this(() => new Views.GeneralSettingsView())
    {
    }

    /// <summary>
    /// Test seam: callers inject the General section view (headless tests
    /// pass no factory or a non-UI placeholder); null disables the default
    /// General registration so construction never requires a window station.
    /// </summary>
    internal MainWindow(Func<object?>? createGeneralView)
    {
        InitializeComponent();
        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        BuildNav();
        var view = createGeneralView?.Invoke();
        if (view is not null)
        {
            RegisterSectionView(MainSection.General, view);
        }

        NavigateTo(MainSection.General);
    }

    /// <summary>
    /// Shows the section's registered view, or a placeholder until its
    /// Views/* screen lands (Tasks 3-5). Safe to call before Show().
    /// Must be called on the UI thread.
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

    /// <summary>Test seam: the sidebar button backing a section.</summary>
    internal Button NavButtonFor(MainSection section) => _navButtons[section];

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_permitClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void BuildNav()
    {
        foreach (var section in MainNav.Ordered)
        {
            var captured = section;
            var button = new Button
            {
                Style = (Style)FindResource("HandyNavButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
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
