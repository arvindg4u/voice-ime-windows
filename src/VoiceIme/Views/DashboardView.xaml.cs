using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VoiceIme.Views;

/// <summary>
/// Dashboard (home) screen: app state, hotkey hint, quick-action tiles to
/// every section, and the most recent transcript. Viewmodel-less code-behind
/// bound to the shared <see cref="SettingsStore"/> and <see cref="ClipboardStore"/>
/// (F1/F2 shared-instance rule). Hosted by <see cref="MainWindow"/> via
/// RegisterSectionView(MainSection.Dashboard, view).
/// </summary>
public partial class DashboardView : System.Windows.Controls.UserControl
{
    private readonly SettingsStore _settings;
    private readonly ClipboardStore _clips;

    /// <summary>The shared <see cref="SettingsStore"/> this view reads.</summary>
    internal SettingsStore BoundSettings => _settings;

    /// <summary>The shared <see cref="ClipboardStore"/> this view reads.</summary>
    internal ClipboardStore BoundStore => _clips;

    public DashboardView()
        : this(SettingsStore.Load(), new ClipboardStore())
    {
    }

    internal DashboardView(SettingsStore settings, ClipboardStore clips)
    {
        _settings = ArgumentNullException.ThrowIfNull(settings);
        _clips = ArgumentNullException.ThrowIfNull(clips);
        InitializeComponent();
        RefreshFromStores();
    }

    /// <summary>Re-reads both shared stores into the UI (hotkey hint, recent entry).</summary>
    internal void RefreshFromStores()
    {
        TaglineText.Text = $"Press {_settings.Hotkey} to dictate — in any app.";
        HotkeyHintText.Text = TaglineText.Text;
        RefreshRecent();
    }

    /// <summary>Plain-language hotkey reminder shown on the status card.</summary>
    internal string HotkeyHint => $"Press {_settings.Hotkey} to dictate.";

    /// <summary>Most recent non-empty transcript, or null.</summary>
    internal ClipboardEntry? LatestTranscript =>
        _clips.Entries.FirstOrDefault(static e => !string.IsNullOrEmpty(e.Text));

    /// <summary>Test seam: whether the empty-state panel is showing.</summary>
    internal bool IsRecentEmptyVisible => RecentEmpty.Visibility == Visibility.Visible;

    private void RefreshRecent()
    {
        var latest = LatestTranscript;
        if (latest is null)
        {
            RecentFilled.Visibility = Visibility.Collapsed;
            RecentEmpty.Visibility = Visibility.Visible;
            return;
        }

        RecentBody.Text = latest.Text;
        RecentTime.Text = latest.CreatedUtc.ToString("yyyy-MM-dd HH:mm");
        RecentFilled.Visibility = Visibility.Visible;
        RecentEmpty.Visibility = Visibility.Collapsed;
    }

    private static MainSection? SectionFromTag(object? tag) =>
        tag is string s && Enum.TryParse<MainSection>(s, out var section) ? section : null;

    private void SectionTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && SectionFromTag(button.Tag) is { } section)
        {
            NavigateTo(section);
        }
    }

    private void RecentCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var latest = LatestTranscript;
        if (latest is null || string.IsNullOrEmpty(latest.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(latest.Text);
        }
        catch
        {
            // Clipboard copy is best-effort — never breaks the dashboard.
        }
    }

    private void RecentOpenHistoryButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(MainSection.History);

    private void OpenHistoryButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(MainSection.History);

    private void NavigateTo(MainSection section)
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            window.NavigateTo(section);
        }
    }
}
