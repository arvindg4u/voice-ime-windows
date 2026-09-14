using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VoiceIme.Views;

/// <summary>
/// History section screen (Handy history port): newest-first transcript cards
/// with a medium timestamp, selectable italic transcript, and a
/// copy/pin/retry/delete icon row, plus an Open-folder shortcut and a
/// clear-unpinned footer. Failed transcriptions appear as retryable rows
/// (empty text + error) — audio is never persisted, so Retry surfaces a
/// re-dictate hint instead of re-sending. Viewmodel-less code-behind over
/// <see cref="ClipboardStore"/>; the store keeps its cap-100 / pin-survives /
/// newest-first semantics untouched (this view only calls Add/TogglePin/
/// Delete/ClearUnpinned). Hosted by <see cref="MainWindow"/> via
/// RegisterSectionView(MainSection.History, view).
/// </summary>
public partial class HistorySettingsView : System.Windows.Controls.UserControl
{
    internal const int PreviewLength = 80;
    internal const string RedictateHint = "Audio isn't kept — press your dictation hotkey and speak again.";
    private const string CopiedHint = "Copied to clipboard ✓";
    private const string UnknownError = "Transcription failed (details weren't kept).";

    private ClipboardStore _store;
    private readonly Dictionary<Guid, string> _errors = new();
    private readonly Action<string> _copyText;
    private readonly Action _openDataFolder;
    private readonly List<HistoryRow> _rows = new();
    private Guid? _copiedId;

    public HistorySettingsView()
        : this(new ClipboardStore())
    {
    }

    internal HistorySettingsView(
        ClipboardStore store,
        Action<string>? copyText = null,
        Action? openDataFolder = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _copyText = copyText ?? new Action<string>(static text => System.Windows.Clipboard.SetText(text));
        _openDataFolder = openDataFolder ?? OpenDataFolderInExplorer;
        InitializeComponent();
        Refresh();
    }

    /// <summary>Test seam: current row snapshots, newest-first.</summary>
    internal IReadOnlyList<HistoryRow> Rows => _rows.AsReadOnly();

    /// <summary>Test seam: footer hint text.</summary>
    internal string HintMessage => HintText.Text;

    /// <summary>
    /// The shared <see cref="ClipboardStore"/> this view renders — the F2
    /// shared-instance wiring asserts the app's live store lands here.
    /// </summary>
    internal ClipboardStore BoundStore => _store;

    /// <summary>Test seam: whether the empty-history message is shown.</summary>
    internal bool IsEmptyVisible => EmptyText.Visibility == Visibility.Visible;

    /// <summary>
    /// Points the view at the app's live <see cref="ClipboardStore"/> (which
    /// keeps accumulating dictations while the shell is open). The view's
    /// default store still lets it render standalone in tests.
    /// </summary>
    internal void BindStore(ClipboardStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        Refresh();
    }

    /// <summary>
    /// Remembers a transcription error for an already-stored entry (App calls
    /// this right after adding the empty-text entry). Errors stay in memory
    /// only — like audio, they are never persisted.
    /// </summary>
    internal void AttachError(Guid id, string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _errors[id] = error;
        Refresh();
    }

    /// <summary>
    /// Pure helper: compact display label — pin prefix, HH:mm stamp, and text
    /// truncated to <see cref="PreviewLength"/> chars. Unit-testable on any
    /// OS — no WPF types involved.
    /// </summary>
    internal static string FormatEntryLabel(bool pinned, DateTime created, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var preview = text.Length <= PreviewLength ? text : text[..PreviewLength] + "…";
        return $"{(pinned ? "📌 " : string.Empty)}{created:HH:mm} — {preview}";
    }

    /// <summary>
    /// Test seam (internal): copies the entry and marks its Copy button with
    /// a check; kept off the private event handler so tests can drive the
    /// clipboard path directly.
    /// </summary>
    internal void CopyEntry(Guid id)
    {
        var entry = _store.Entries.FirstOrDefault(e => e.Id == id);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Text))
        {
            return;
        }

        try
        {
            _copyText(entry.Text);
            _copiedId = id;
            HintText.Text = CopiedHint;
        }
        catch (Exception ex)
        {
            HintText.Text = $"Copy failed: {ex.Message}";
        }

        Refresh();
    }

    /// <summary>Test seam (internal): toggles the entry's pin. See <see cref="CopyEntry"/>.</summary>
    internal void TogglePinEntry(Guid id)
    {
        _store.TogglePin(id);
        Refresh();
    }

    /// <summary>Test seam (internal): deletes the entry. See <see cref="CopyEntry"/>.</summary>
    internal void DeleteEntry(Guid id)
    {
        _store.Delete(id);
        _errors.Remove(id);
        Refresh();
    }

    /// <summary>
    /// Test seam (internal): shows the re-dictate hint (audio is never
    /// persisted, so there is nothing to re-send). See <see cref="CopyEntry"/>.
    /// </summary>
    internal void RetryEntry(Guid id)
    {
        if (_store.Entries.Any(e => e.Id == id))
        {
            HintText.Text = RedictateHint;
        }
    }

    /// <summary>Test seam (internal): clears unpinned entries. See <see cref="CopyEntry"/>.</summary>
    internal void ClearUnpinnedEntries()
    {
        _store.ClearUnpinned();
        Refresh();
        HintText.Text = "Unpinned entries cleared.";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Guid id })
        {
            CopyEntry(id);
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Guid id })
        {
            TogglePinEntry(id);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Guid id })
        {
            DeleteEntry(id);
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: Guid id })
        {
            RetryEntry(id);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearUnpinnedEntries();

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _openDataFolder();
        }
        catch (Exception ex)
        {
            HintText.Text = $"Couldn't open the data folder: {ex.Message}";
        }
    }

    private void Refresh()
    {
        PruneErrors();
        if (_copiedId is Guid copied && _store.Entries.All(e => e.Id != copied))
        {
            _copiedId = null;
        }

        _rows.Clear();
        _rows.AddRange(_store.Entries.Select(ToRow));
        EntriesList.ItemsSource = _rows.ToArray();
        var empty = _rows.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EntriesList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private HistoryRow ToRow(ClipboardEntry entry)
    {
        var failed = string.IsNullOrWhiteSpace(entry.Text);
        var time = entry.CreatedUtc.ToLocalTime();
        var body = failed
            ? (_errors.TryGetValue(entry.Id, out var known) ? known : UnknownError)
            : entry.Text;
        return new HistoryRow(
            entry.Id,
            time.ToString("HH:mm"),
            failed ? string.Empty : entry.Text,
            failed ? Visibility.Collapsed : Visibility.Visible,
            failed ? body : string.Empty,
            failed ? Visibility.Visible : Visibility.Collapsed,
            _copiedId == entry.Id ? "✓" : "⧉",
            failed ? Visibility.Collapsed : Visibility.Visible,
            entry.Pinned ? "★" : "☆",
            entry.Pinned ? "Unpin" : "Pin",
            failed ? Visibility.Visible : Visibility.Collapsed,
            FormatEntryLabel(entry.Pinned, time, body));
    }

    private void PruneErrors()
    {
        if (_errors.Count == 0)
        {
            return;
        }

        var live = new HashSet<Guid>(_store.Entries.Select(e => e.Id));
        foreach (var id in _errors.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _errors.Remove(id);
        }
    }

    private static void OpenDataFolderInExplorer()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceIme");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    /// <summary>
    /// Bindable snapshot of one <see cref="ClipboardEntry"/> for the card
    /// template: pre-formatted timestamp/body/error strings plus per-button
    /// visibility and labels (copy check, pin star, conditional retry).
    /// Rebuilt on every <see cref="Refresh"/> — never mutated in place.
    /// </summary>
    internal sealed record HistoryRow(
        Guid Id,
        string TimeLabel,
        string Body,
        Visibility BodyVisibility,
        string Error,
        Visibility ErrorVisibility,
        string CopyGlyph,
        Visibility CopyVisibility,
        string PinGlyph,
        string PinTip,
        Visibility RetryVisibility,
        string CompactLabel);
}
