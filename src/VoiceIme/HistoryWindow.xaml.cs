using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace VoiceIme;

/// <summary>Transcript history: copy back, pin, delete. Newest first.</summary>
public partial class HistoryWindow : Window
{
    private sealed record DisplayEntry(Guid Id, string Label);

    private readonly ClipboardStore _store;

    public HistoryWindow(ClipboardStore store)
    {
        InitializeComponent();
        _store = store;
        Refresh();
    }

    private void Refresh()
    {
        ClipsList.ItemsSource = _store.Entries
            .Select(e => new DisplayEntry(
                e.Id,
                $"{(e.Pinned ? "📌 " : "")}{e.CreatedUtc:HH:mm} — {Truncate(e.Text, 80)}"))
            .ToList();
    }

    private Guid? SelectedId() => (ClipsList.SelectedItem as DisplayEntry)?.Id;

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void ClipsList_DoubleClick(object sender, MouseButtonEventArgs e) => CopySelected();

    private void CopySelected()
    {
        var id = SelectedId();
        if (id is null) return;
        var entry = _store.Entries.FirstOrDefault(x => x.Id == id);
        if (entry is not null) System.Windows.Clipboard.SetText(entry.Text);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id is null) return;
        _store.TogglePin(id.Value);
        Refresh();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedId();
        if (id is null) return;
        _store.Delete(id.Value);
        Refresh();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _store.ClearUnpinned();
        Refresh();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
