using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VoiceIme;

public sealed record ClipboardEntry(Guid Id, string Text, DateTime CreatedUtc, bool Pinned);

/// <summary>
/// Transcript history persisted to %AppData%/VoiceIme/clips.json. The legacy
/// default remains 100 entries, while the Advanced setting can raise or lower
/// the live cap up to <see cref="SettingsStore.MaxHistoryLimit"/>. Pinned
/// entries survive normal eviction where possible.
/// </summary>
public sealed class ClipboardStore
{
    /// <summary>Legacy/default cap retained for callers that do not configure a limit.</summary>
    public const int MaxEntries = SettingsStore.DefaultHistoryLimit;

    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _path;
    private readonly List<ClipboardEntry> _entries = [];
    private int _limit;

    public ClipboardStore(string? path = null, int maxEntries = MaxEntries)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceIme", "clips.json");
        _limit = ClampLimit(maxEntries);
        Load();
    }

    public IReadOnlyList<ClipboardEntry> Entries => _entries.AsReadOnly();

    /// <summary>The currently applied history cap.</summary>
    public int Limit => _limit;

    public ClipboardEntry Add(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entry = new ClipboardEntry(Guid.NewGuid(), text, DateTime.UtcNow, Pinned: false);
        _entries.Insert(0, entry);
        Evict();
        Save();
        return entry;
    }

    /// <summary>
    /// Applies a new cap immediately, evicts excess rows, and persists the
    /// result. This is intentionally separate from SettingsStore.Save because
    /// the two stores have different files and failure semantics.
    /// </summary>
    public void SetLimit(int maxEntries)
    {
        var next = ClampLimit(maxEntries);
        if (next == _limit)
        {
            return;
        }

        _limit = next;
        // A store is constructed before App loads SettingsStore. Reload from
        // disk when the cap changes so rows above the legacy default (100) are
        // recoverable when the user has configured a larger limit.
        Load();
        Evict();
        Save();
    }

    public void TogglePin(Guid id)
    {
        var i = _entries.FindIndex(e => e.Id == id);
        if (i < 0) return;
        _entries[i] = _entries[i] with { Pinned = !_entries[i].Pinned };
        Save();
    }

    public void Delete(Guid id)
    {
        _entries.RemoveAll(e => e.Id == id);
        Save();
    }

    public void ClearUnpinned()
    {
        _entries.RemoveAll(e => !e.Pinned);
        Save();
    }

    private static int ClampLimit(int limit) =>
        Math.Clamp(limit, 1, SettingsStore.MaxHistoryLimit);

    private void Evict()
    {
        // Newest-first; drop oldest unpinned beyond the cap.
        for (var i = _entries.Count - 1; i >= 0 && _entries.Count > _limit; i--)
        {
            if (!_entries[i].Pinned) _entries.RemoveAt(i);
        }
        // Pathological all-pinned overflow: still cap by dropping oldest pinned.
        while (_entries.Count > _limit) _entries.RemoveAt(_entries.Count - 1);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(_path));
            if (list is null) return;
            _entries.Clear();
            _entries.AddRange(list
                .Where(static e => e is not null)
                .Select(static e => e! with { Text = e!.Text ?? string.Empty })
                .OrderByDescending(e => e.CreatedUtc));
            Evict();
        }
        catch
        {
            /* corrupt file → start fresh; history is best-effort */
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var directoryForTemp = string.IsNullOrEmpty(directory)
                ? Environment.CurrentDirectory
                : directory;
            var tempPath = Path.Combine(
                directoryForTemp,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(_entries, JsonOptions), Encoding.UTF8);
                if (File.Exists(_path))
                {
                    try
                    {
                        File.Replace(tempPath, _path, destinationBackupFileName: null);
                    }
                    catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
                    {
                        File.Move(tempPath, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, _path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch { /* preserve the original persistence result */ }
            }
        }
        catch
        {
            /* history is best-effort */
        }
    }
}
