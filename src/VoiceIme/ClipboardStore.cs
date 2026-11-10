using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceIme;

public sealed record ClipboardEntry(Guid Id, string Text, DateTime CreatedUtc, bool Pinned);

/// <summary>
/// Transcript history persisted to %AppData%/VoiceIme/clips.json (newest-first,
/// pinned survive eviction). The retention cap follows
/// <see cref="SettingsStore.HistoryLimit"/> through <see cref="SetLimit"/>,
/// defaulting to <see cref="DefaultMaxEntries"/>.
/// Mirrors Android ClipboardStore/ClipboardPolicy.
/// </summary>
public sealed class ClipboardStore
{
    /// <summary>Default retention cap when no setting overrides it.</summary>
    public const int DefaultMaxEntries = 100;

    /// <summary>Legacy alias for <see cref="DefaultMaxEntries"/> (test/back-compat).</summary>
    public const int MaxEntries = DefaultMaxEntries;

    /// <summary>Smallest allowed limit; guards against a zero/negative slice.</summary>
    public const int MinLimit = 1;

    private readonly string _path;
    private readonly List<ClipboardEntry> _entries = [];
    private int _limit = DefaultMaxEntries;

    public ClipboardStore(string? path = null, int limit = DefaultMaxEntries)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceIme", "clips.json");
        _limit = Math.Max(MinLimit, limit);
        Load();
    }

    public IReadOnlyList<ClipboardEntry> Entries => _entries.AsReadOnly();

    /// <summary>
    /// Current retention cap. Assigning is equivalent to <see cref="SetLimit"/>:
    /// clamps, re-evicts, and persists so the file matches the cap immediately.
    /// </summary>
    public int Limit
    {
        get => _limit;
        set => SetLimit(value);
    }

    /// <summary>
    /// Applies the persisted <see cref="SettingsStore.HistoryLimit"/> (already
    /// clamped 10–500 there). Re-evicts existing entries and saves, so a lowered
    /// cap takes effect on transcripts already stored, not only future ones.
    /// </summary>
    public void SetLimit(int limit)
    {
        var clamped = Math.Max(MinLimit, limit);
        if (clamped == _limit)
        {
            return;
        }

        _limit = clamped;
        Evict();
        Save();
    }

    public ClipboardEntry Add(string text)
    {
        var entry = new ClipboardEntry(Guid.NewGuid(), text, DateTime.UtcNow, Pinned: false);
        _entries.Insert(0, entry);
        Evict();
        Save();
        return entry;
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
            _entries.AddRange(list.OrderByDescending(e => e.CreatedUtc).Take(_limit));
        }
        catch { /* corrupt file → start fresh */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch { /* history is best-effort */ }
    }
}
