using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VoiceIme.Tests;

public sealed class ClipboardStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Add_InsertsNewestFirst()
    {
        var store = new ClipboardStore(PathFor("a.json"));

        store.Add("first");
        store.Add("second");

        Assert.Equal("second", store.Entries[0].Text);
        Assert.Equal("first", store.Entries[1].Text);
    }

    [Fact]
    public void Evict_DropsOldestUnpinnedBeyondCap()
    {
        var store = new ClipboardStore(PathFor("b.json"));

        for (var i = 0; i < ClipboardStore.MaxEntries + 5; i++)
            store.Add($"clip {i}");

        Assert.Equal(ClipboardStore.MaxEntries, store.Entries.Count);
        Assert.DoesNotContain(store.Entries, e => e.Text == "clip 0");
    }

    [Fact]
    public void Pinned_SurviveEviction()
    {
        var store = new ClipboardStore(PathFor("c.json"));
        var keep = store.Add("keep me");
        store.TogglePin(keep.Id);

        for (var i = 0; i < ClipboardStore.MaxEntries + 5; i++)
            store.Add($"clip {i}");

        Assert.Contains(store.Entries, e => e.Text == "keep me" && e.Pinned);
    }

    [Fact]
    public void Delete_RemovesEntry_And_ClearUnpinned_KeepsPinned()
    {
        var store = new ClipboardStore(PathFor("d.json"));
        var gone = store.Add("gone");
        var keep = store.Add("keep");
        store.TogglePin(keep.Id);

        store.Delete(gone.Id);
        Assert.DoesNotContain(store.Entries, e => e.Id == gone.Id);

        store.ClearUnpinned();
        Assert.Single(store.Entries);
        Assert.Equal(keep.Id, store.Entries[0].Id);
    }

    [Fact]
    public void SetLimit_ReEvictsExistingEntries_And_Persists()
    {
        var path = PathFor("e.json");
        var store = new ClipboardStore(path);
        for (var i = 0; i < 20; i++)
        {
            store.Add($"clip {i}");
        }

        store.SetLimit(5);

        Assert.Equal(5, store.Limit);
        Assert.Equal(5, store.Entries.Count);
        // Oldest entries went first (newest-first ordering).
        Assert.Equal("clip 19", store.Entries[0].Text);

        // A lowered cap is on disk, so a fresh store loads the same count.
        var reloaded = new ClipboardStore(path);
        Assert.Equal(5, reloaded.Entries.Count);
    }

    [Fact]
    public void SetLimit_BelowOne_ClampsToOne()
    {
        var store = new ClipboardStore(PathFor("f.json"));
        store.Add("a");
        store.Add("b");

        store.SetLimit(0);

        Assert.Equal(ClipboardStore.MinLimit, store.Limit);
        Assert.Single(store.Entries);
    }

    [Fact]
    public void Constructor_AppliesLimit_OnLoad()
    {
        var path = PathFor("g.json");
        var seed = new ClipboardStore(path);
        for (var i = 0; i < 20; i++)
        {
            seed.Add($"clip {i}");
        }

        var limited = new ClipboardStore(path, limit: 3);

        Assert.Equal(3, limited.Limit);
        Assert.Equal(3, limited.Entries.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
