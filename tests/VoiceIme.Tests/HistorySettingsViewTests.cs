using System;
using System.IO;
using System.Linq;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// History settings screen tests. The pure label formatter
/// (<c>FormatEntryLabel</c>) runs on any OS. View tests need an STA thread
/// plus a window station — on headless runners construction throws and the
/// test passes vacuously (same pattern as <c>MainWindowTests</c>). Full
/// coverage runs on windows-latest CI. Views are built over temp-file stores
/// with a no-op clipboard and folder opener so tests never touch
/// %AppData%, the real clipboard, or Explorer.
/// </summary>
[Collection("WpfSta")]
public sealed class HistorySettingsViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void FormatEntryLabel_UnpinnedShortText_HasTimeAndNoPrefix()
    {
        var label = HistorySettingsView.FormatEntryLabel(
            false, new DateTime(2026, 9, 14, 9, 5, 0), "hello");

        Assert.Equal("09:05 — hello", label);
    }

    [Fact]
    public void FormatEntryLabel_Pinned_AddsPinPrefix()
    {
        var label = HistorySettingsView.FormatEntryLabel(
            true, new DateTime(2026, 9, 14, 21, 30, 0), "hello");

        Assert.Equal("📌 21:30 — hello", label);
    }

    [Fact]
    public void FormatEntryLabel_LongText_TruncatesAt80Chars()
    {
        var text = new string('x', 90);

        var label = HistorySettingsView.FormatEntryLabel(
            false, new DateTime(2026, 9, 14, 9, 5, 0), text);

        Assert.Equal($"09:05 — {new string('x', 80)}…", label);
    }

    [Fact]
    public void FormatEntryLabel_Exactly80Chars_KeepsWholeText()
    {
        var text = new string('x', 80);

        var label = HistorySettingsView.FormatEntryLabel(
            false, new DateTime(2026, 9, 14, 9, 5, 0), text);

        Assert.Equal($"09:05 — {text}", label);
    }

    [Fact]
    public void CardOpacityForRow_Failed_IsMuted()
    {
        Assert.Equal(0.6, HistorySettingsView.CardOpacityForRow(true));
    }

    [Fact]
    public void CardOpacityForRow_Succeeded_IsFull()
    {
        Assert.Equal(1.0, HistorySettingsView.CardOpacityForRow(false));
    }

    [Fact]
    public void Rows_EmptyStore_ShowsEmptyMessage()
    {
        TryRunOnSta(PathFor("empty.json"), _ => { }, view =>
        {
            Assert.Empty(view.Rows);
            Assert.True(view.IsEmptyVisible);
        });
    }

    [Fact]
    public void Rows_PopulatedStore_NewestFirstWithPinStar()
    {
        TryRunOnSta(PathFor("rows.json"),
            store =>
            {
                store.Add("older");
                var pinned = store.Add("newer");
                store.TogglePin(pinned.Id);
            },
            view =>
            {
                Assert.Equal(2, view.Rows.Count);
                Assert.Equal("newer", view.Rows[0].Body);
                Assert.Equal("★", view.Rows[0].PinGlyph);
                Assert.Equal("Unpin", view.Rows[0].PinTip);
                Assert.Equal("older", view.Rows[1].Body);
                Assert.Equal("☆", view.Rows[1].PinGlyph);
                Assert.False(view.IsEmptyVisible);
            });
    }

    [Fact]
    public void CopyEntry_CopiesTextAndMarksCheck()
    {
        string? copied = null;
        TryRunOnSta(PathFor("copy.json"), store => store.Add("say this"),
            view =>
            {
                view.CopyEntry(view.Rows[0].Id);

                Assert.Equal("say this", copied);
                Assert.Equal("✓", view.Rows[0].CopyGlyph);
                Assert.Equal("Copied to clipboard ✓", view.HintMessage);
            },
            onCopy: text => copied = text);
    }

    [Fact]
    public void TogglePinEntry_FlipsStar()
    {
        TryRunOnSta(PathFor("pin.json"), store => store.Add("keep me"), view =>
        {
            var id = view.Rows[0].Id;

            view.TogglePinEntry(id);

            Assert.Equal("★", view.Rows[0].PinGlyph);
            Assert.Equal("Unpin", view.Rows[0].PinTip);
        });
    }

    [Fact]
    public void DeleteEntry_RemovesRow()
    {
        TryRunOnSta(PathFor("delete.json"),
            store =>
            {
                store.Add("gone");
                store.Add("stays");
            },
            view =>
            {
                var id = view.Rows.Single(r => r.Body == "gone").Id;

                view.DeleteEntry(id);

                Assert.DoesNotContain(view.Rows, r => r.Id == id);
                Assert.Single(view.Rows);
            });
    }

    [Fact]
    public void ClearUnpinnedEntries_KeepsOnlyPinned()
    {
        TryRunOnSta(PathFor("clear.json"),
            store =>
            {
                store.Add("gone");
                var keep = store.Add("keep");
                store.TogglePin(keep.Id);
            },
            view =>
            {
                view.ClearUnpinnedEntries();

                var single = Assert.Single(view.Rows);
                Assert.Equal("keep", single.Body);
                Assert.Contains("Unpinned", view.HintMessage, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void AttachError_FailedRow_ShowsErrorHidesCopyShowsRetry()
    {
        TryRunOnSta(PathFor("failed.json"), store => store.Add(string.Empty), view =>
        {
            view.AttachError(view.Rows[0].Id, "quota exhausted");

            var row = Assert.Single(view.Rows);
            Assert.Equal(string.Empty, row.Body);
            Assert.Equal("quota exhausted", row.Error);
            Assert.Equal("⧉", row.CopyGlyph);
            Assert.Equal("☆", row.PinGlyph);
        });
    }

    [Fact]
    public void Rows_SucceededRow_HasFullOpacity()
    {
        TryRunOnSta(PathFor("opacity-ok.json"), store => store.Add("heard clearly"), view =>
        {
            var row = Assert.Single(view.Rows);

            Assert.Equal(1.0, row.CardOpacity);
            Assert.Equal(string.Empty, row.Error);
        });
    }

    [Fact]
    public void Rows_FailedRow_HasMutedOpacityAndInlineError()
    {
        TryRunOnSta(PathFor("opacity-failed.json"), store => store.Add(string.Empty), view =>
        {
            view.AttachError(view.Rows[0].Id, "quota exhausted");

            var row = Assert.Single(view.Rows);
            Assert.Equal(0.6, row.CardOpacity);
            Assert.Equal("quota exhausted", row.Error);
            Assert.Equal(string.Empty, row.Body);
        });
    }

    [Fact]
    public void CopyEntry_ClipboardThrows_ShowsCopyFailureHint()
    {
        TryRunOnSta(PathFor("copy-fail.json"), store => store.Add("say this"),
            view =>
            {
                view.CopyEntry(view.Rows[0].Id);

                Assert.StartsWith("Copy failed:", view.HintMessage, StringComparison.Ordinal);
                Assert.Equal("⧉", view.Rows[0].CopyGlyph);
            },
            onCopy: _ => throw new InvalidOperationException("clipboard busy"));
    }

    [Fact]
    public void RetryEntry_FailedRow_ShowsRedictateHint()
    {
        TryRunOnSta(PathFor("retry.json"), store => store.Add(string.Empty), view =>
        {
            view.AttachError(view.Rows[0].Id, "quota exhausted");
            view.RetryEntry(view.Rows[0].Id);

            Assert.Equal(HistorySettingsView.RedictateHint, view.HintMessage);
        });
    }

    [Fact]
    public void BindStore_SwitchesToLiveStore()
    {
        StaTestHelper.TryRunOnSta(() =>
        {
            var live = new ClipboardStore(PathFor("live.json"));
            live.Add("spoken later");
            var view = new HistorySettingsView(
                new ClipboardStore(PathFor("stale.json")),
                copyText: _ => { },
                openDataFolder: () => { });

            view.BindStore(live);

            Assert.Equal("spoken later", Assert.Single(view.Rows).Body);
        });
    }

    [Fact]
    public void BoundStore_IsInjectedInstance_RendersLiveAdds()
    {
        ClipboardStore? built = null;
        TryRunOnSta(PathFor("bound.json"), store =>
        {
            built = store;
            store.Add("spoken earlier");
        }, view =>
        {
            Assert.Same(built, view.BoundStore);
            Assert.Equal("spoken earlier", Assert.Single(view.Rows).Body);
        });
    }

    [Fact]
    public void MainWindow_DefaultCtor_HostsHistoryView()
    {
        RunOnStaWindow(window =>
        {
            Assert.IsType<HistorySettingsView>(window.SectionView(MainSection.History));
        }, () => new MainWindow());
    }

    [Fact]
    public void MainWindow_NavigateToHistory_ShowsRegisteredContent()
    {
        RunOnStaWindow(window =>
        {
            window.NavigateTo(MainSection.History);

            Assert.Equal(MainSection.History, window.CurrentSection);
            Assert.IsType<HistorySettingsView>(window.SectionView(MainSection.History));
        }, () => new MainWindow());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    private static void TryRunOnSta(
        string storePath,
        Action<ClipboardStore> seed,
        Action<HistorySettingsView> body,
        Action<string>? onCopy = null)
    {
        StaTestHelper.TryRunOnSta(() =>
        {
            var store = new ClipboardStore(storePath);
            seed(store);
            var view = new HistorySettingsView(
                store,
                copyText: onCopy ?? (_ => { }),
                openDataFolder: () => { });
            body(view);
        });
    }

    private static void RunOnStaWindow(Action<MainWindow> body, Func<MainWindow> create)
    {
        MainWindow? window = null;
        StaTestHelper.TryRunOnSta(() =>
        {
            try
            {
                window = create();
                body(window);
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch
                {
                    // Close failures surface via the body exception path.
                }
            }
        });
    }
}
