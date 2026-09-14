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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
