using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceIme;

public sealed record ClipboardEntry(Guid Id, string Text, DateTime CreatedUtc, bool Pinned);

/// <summary>
/// Transcript history persisted to %AppData%/VoiceIme/clips.json (cap 100,
/// pinned survive eviction). Mirrors Android ClipboardStore/ClipboardPolicy.
/// </summary>
public sealed class ClipboardStore
{
    public const int MaxEntries = 100;

    private readonly string _path;
    private readonly List<ClipboardEntry> _entries = [];

    public ClipboardStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceIme", "clips.json");
        Load();
    }

    public IReadOnlyList<ClipboardEntry> Entries => _entries.AsReadOnly();

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
        for (var i = _entries.Count - 1; i >= 0 && _entries.Count > MaxEntries; i--)
        {
            if (!_entries[i].Pinned) _entries.RemoveAt(i);
        }
        // Pathological all-pinned overflow: still cap by dropping oldest pinned.
        while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(_path));
            if (list is null) return;
            _entries.Clear();
            _entries.AddRange(list.OrderByDescending(e => e.CreatedUtc).Take(MaxEntries));
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
