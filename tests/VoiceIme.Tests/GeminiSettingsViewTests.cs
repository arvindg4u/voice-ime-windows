using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Gemini settings screen tests. Pure helpers (<c>ValidatePrompt</c>,
/// <c>SplitKeys</c>) run on any OS. View tests need an STA thread plus a
/// window station — on headless runners construction throws and the test
/// passes vacuously (same pattern as <c>MainWindowTests</c>). Full coverage
/// runs on windows-latest CI. Views are built with a no-op saver and fake
/// pipeline so tests never touch disk, DPAPI, or the network.
/// </summary>
[Collection("WpfSta")]
public sealed class GeminiSettingsViewTests
{
    [Fact]
    public void ValidatePrompt_RejectsOverLimit_WithMessage()
    {
        var error = GeminiSettingsView.ValidatePrompt(new string('x', 2001));

        Assert.NotNull(error);
        Assert.Contains("2000", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePrompt_AcceptsAtAndUnderLimit()
    {
        Assert.Null(GeminiSettingsView.ValidatePrompt(new string('x', 2000)));
        Assert.Null(GeminiSettingsView.ValidatePrompt("trim me"));
        Assert.Null(GeminiSettingsView.ValidatePrompt(""));
    }

    [Fact]
    public void SplitKeys_TrimsAndDropsBlanks()
    {
        var keys = GeminiSettingsView.SplitKeys("  key-1  \n\n   \nkey-2\n\tkey-3\t\n");

        Assert.True(keys.SequenceEqual(["key-1", "key-2", "key-3"]));
    }

    [Fact]
    public void SplitKeys_EmptyText_YieldsNoKeys()
    {
        Assert.Empty(GeminiSettingsView.SplitKeys("  \n \n"));
    }

    [Fact]
    public void Defaults_LoadIntoControls()
    {
        TryRunOnSta(new SettingsStore(), view =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta", view.BaseUrlText);
            Assert.Equal("gemini-2.5-flash", view.ModelText);
            Assert.Equal("", view.PromptText);
            Assert.Equal("0/2000 characters", view.PromptHintText);
            Assert.False(view.IsPromptOverLimit);
        });
    }

    [Fact]
    public void SaveNow_OverlongPrompt_RejectsWithMessageAndKeepsOld()
    {
        var store = new SettingsStore { CustomPrompt = "kept" };
        var saved = new List<string>();
        TryRunOnSta(store, view =>
        {
            view.PromptText = new string('x', 2001);

            var ok = view.SaveNow();

            Assert.False(ok);
            Assert.Contains("2000", view.StatusMessage, StringComparison.Ordinal);
            Assert.Equal("kept", store.CustomPrompt);
            Assert.Empty(saved);
        }, saver: s => saved.Add(s.CustomPrompt));
    }

    [Fact]
    public void SaveNow_ValidInput_TrimsPersistsAndConfirms()
    {
        var store = new SettingsStore();
        var saved = new List<SettingsStore>();
        TryRunOnSta(store, view =>
        {
            view.BaseUrlText = "  https://example.test/v1beta  ";
            view.KeysText = "  key-1  \n\nkey-2\n";
            view.ModelText = "  test-model  ";
            view.PromptText = "  polish it  ";

            var ok = view.SaveNow();

            Assert.True(ok);
            Assert.Equal("https://example.test/v1beta", store.BaseUrl);
            Assert.True(store.ApiKeys.SequenceEqual(["key-1", "key-2"]));
            Assert.Equal("test-model", store.Model);
            Assert.Equal("polish it", store.CustomPrompt);
            Assert.Equal("Saved ✓", view.StatusMessage);
            Assert.Single(saved);
        }, saver: saved.Add);
    }

    [Fact]
    public void TestNowAsync_Success_ShowsTranscript()
    {
        TryRunOnSta(new SettingsStore(), view =>
        {
            view.TestNowAsync().GetAwaiter().GetResult();

            Assert.StartsWith("Test OK — transcript:", view.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("hello", view.StatusMessage, StringComparison.Ordinal);
        }, transcribeAsync: (_, _, _, _, _) => Task.FromResult(("hello", 0)));
    }

    [Fact]
    public void TestNowAsync_Failure_ShowsError()
    {
        TryRunOnSta(new SettingsStore(), view =>
        {
            view.TestNowAsync().GetAwaiter().GetResult();

            Assert.StartsWith("Test failed:", view.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("boom", view.StatusMessage, StringComparison.Ordinal);
        }, transcribeAsync: (_, _, _, _, _) => Task.FromException<(string, int)>(new TranscribeException("boom")));
    }

    [Fact]
    public void BindsInjectedStore_ExposesSameInstance()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            Assert.Same(store, view.BoundSettings);
        });
    }

    [Fact]
    public void Defaults_LoadIntoLibraryControls()
    {
        // Empty store: no dropdown items, blank name field and editor.
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            Assert.Equal(0, view.PromptCount);
            Assert.Empty(view.PromptNames);
            Assert.Null(view.SelectedPromptName);
            Assert.Equal("", view.PromptName);
        });
    }

    [Fact]
    public void LoadFromSettings_ShowsActiveEntry()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "polish it"), new PromptEntry("Mail", "short")],
            ActivePrompt = "Mail",
            CustomPrompt = "short",
        };
        TryRunOnSta(store, view =>
        {
            Assert.True(view.PromptNames.SequenceEqual(["Work", "Mail"]));
            Assert.Equal("Mail", view.SelectedPromptName);
            Assert.Equal("Mail", view.PromptName);
            Assert.Equal("short", view.PromptText);
        });
    }

    [Fact]
    public void SavePrompt_NewName_AppendsSelectsAndPersists()
    {
        var store = new SettingsStore();
        var saved = new List<SettingsStore>();
        TryRunOnSta(store, view =>
        {
            view.PromptName = "  Work  ";
            view.PromptText = "  polish it  ";

            var ok = view.SavePrompt();

            Assert.True(ok);
            Assert.True(store.Prompts.SequenceEqual([new PromptEntry("Work", "polish it")]));
            Assert.Equal("Work", store.ActivePrompt);
            Assert.Equal("polish it", store.ActivePromptText);
            Assert.Equal("polish it", store.CustomPrompt);
            Assert.Equal("Saved ✓", view.StatusMessage);
            Assert.Equal("Work", view.SelectedPromptName);
            Assert.Single(saved);
        }, saver: saved.Add);
    }

    [Fact]
    public void SavePrompt_ExistingName_UpsertsCaseInsensitively()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "old"), new PromptEntry("Mail", "short")],
            ActivePrompt = "Mail",
            CustomPrompt = "short",
        };
        var saved = new List<SettingsStore>();
        TryRunOnSta(store, view =>
        {
            view.PromptName = "WORK";
            view.PromptText = "new text";

            var ok = view.SavePrompt();

            Assert.True(ok);
            // Canonical casing kept, order kept, other entry untouched.
            Assert.True(store.Prompts.SequenceEqual(
                [new PromptEntry("Work", "new text"), new PromptEntry("Mail", "short")]));
            Assert.Equal("Work", store.ActivePrompt);
            Assert.Equal("new text", store.CustomPrompt);
            Assert.Equal("Saved ✓", view.StatusMessage);
            Assert.Single(saved);
        }, saver: saved.Add);
    }

    [Fact]
    public void SavePrompt_BlankName_RejectsWithMessageAndKeepsStore()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "kept")],
            ActivePrompt = "Work",
            CustomPrompt = "kept",
        };
        var saved = new List<string>();
        TryRunOnSta(store, view =>
        {
            view.PromptName = "   ";
            view.PromptText = "changed";

            var ok = view.SavePrompt();

            Assert.False(ok);
            Assert.Contains("required", view.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.True(store.Prompts.SequenceEqual([new PromptEntry("Work", "kept")]));
            Assert.Empty(saved);
        }, saver: s => saved.Add(s.ActivePromptText));
    }

    [Fact]
    public void SavePrompt_NameOver60_RejectsWithMessageAndKeepsStore()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            view.PromptName = new string('n', 61);
            view.PromptText = "text";

            var ok = view.SavePrompt();

            Assert.False(ok);
            Assert.Contains("60", view.StatusMessage, StringComparison.Ordinal);
            Assert.Empty(store.Prompts);
        });
    }

    [Fact]
    public void SavePrompt_TextOver2000_RejectsWithMessageAndKeepsStore()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "kept")],
            ActivePrompt = "Work",
            CustomPrompt = "kept",
        };
        TryRunOnSta(store, view =>
        {
            view.PromptName = "Work";
            view.PromptText = new string('x', 2001);

            var ok = view.SavePrompt();

            Assert.False(ok);
            Assert.Contains("2000", view.StatusMessage, StringComparison.Ordinal);
            Assert.True(store.Prompts.SequenceEqual([new PromptEntry("Work", "kept")]));
        });
    }

    [Fact]
    public void SavePrompt_WhenFull_Rejects21stWithMessage()
    {
        var store = new SettingsStore
        {
            Prompts = Enumerable.Range(1, 20)
                .Select(i => new PromptEntry($"P{i:00}", $"t{i}"))
                .ToList(),
            ActivePrompt = "P01",
            CustomPrompt = "t1",
        };
        TryRunOnSta(store, view =>
        {
            view.PromptName = "Twenty-first";
            view.PromptText = "extra";

            var ok = view.SavePrompt();

            Assert.False(ok);
            Assert.Contains("20", view.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(20, store.Prompts.Count);
            Assert.Equal("P01", store.ActivePrompt);
        });
    }

    [Fact]
    public void SavePrompt_SaverFailure_ReportsInline()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            view.PromptName = "Work";
            view.PromptText = "text";

            var ok = view.SavePrompt();

            Assert.False(ok);
            Assert.StartsWith("Save failed:", view.StatusMessage, StringComparison.Ordinal);
        }, saver: _ => throw new InvalidOperationException("disk gone"));
    }

    [Fact]
    public void SelectPrompt_Switch_CommitsOutgoingTextAndLoadsTarget()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "old work"), new PromptEntry("Mail", "short")],
            ActivePrompt = "Work",
            CustomPrompt = "old work",
        };
        var saved = new List<SettingsStore>();
        TryRunOnSta(store, view =>
        {
            // Edit Work's text, then switch to Mail: Work keeps the edit.
            view.PromptText = "edited work";

            var ok = view.SelectPrompt("Mail");

            Assert.True(ok);
            Assert.True(store.Prompts.SequenceEqual(
                [new PromptEntry("Work", "edited work"), new PromptEntry("Mail", "short")]));
            Assert.Equal("Mail", store.ActivePrompt);
            Assert.Equal("short", view.PromptText);
            Assert.Equal("Mail", view.PromptName);
            Assert.Equal("Mail", view.SelectedPromptName);
            Assert.Equal("Saved ✓", view.StatusMessage);
            Assert.Single(saved);
        }, saver: saved.Add);
    }

    [Fact]
    public void SelectPrompt_UnknownName_RejectsAndRestoresPicker()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "text")],
            ActivePrompt = "Work",
            CustomPrompt = "text",
        };
        TryRunOnSta(store, view =>
        {
            var ok = view.SelectPrompt("Ghost");

            Assert.False(ok);
            Assert.Contains("Ghost", view.StatusMessage, StringComparison.Ordinal);
            Assert.Equal("Work", store.ActivePrompt);
            Assert.Equal("Work", view.SelectedPromptName);
        });
    }

    [Fact]
    public void SelectPrompt_OverlongOutgoingText_BlocksSwitch()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "old"), new PromptEntry("Mail", "short")],
            ActivePrompt = "Work",
            CustomPrompt = "old",
        };
        TryRunOnSta(store, view =>
        {
            view.PromptText = new string('x', 2001);

            var ok = view.SelectPrompt("Mail");

            Assert.False(ok);
            Assert.Contains("2000", view.StatusMessage, StringComparison.Ordinal);
            Assert.Equal("Work", store.ActivePrompt);
            Assert.Equal("Work", view.SelectedPromptName);
        });
    }

    [Fact]
    public void DeletePrompt_ActiveEntry_FallsBackToFirst()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "w"), new PromptEntry("Mail", "m")],
            ActivePrompt = "Mail",
            CustomPrompt = "m",
        };
        var saved = new List<SettingsStore>();
        TryRunOnSta(store, view =>
        {
            var ok = view.DeletePrompt();

            Assert.True(ok);
            Assert.True(store.Prompts.SequenceEqual([new PromptEntry("Work", "w")]));
            Assert.Equal("Work", store.ActivePrompt);
            Assert.Equal("w", store.CustomPrompt);
            Assert.Equal("w", view.PromptText);
            Assert.Equal("Work", view.SelectedPromptName);
            Assert.Equal("Saved ✓", view.StatusMessage);
            Assert.Single(saved);
        }, saver: saved.Add);
    }

    [Fact]
    public void DeletePrompt_SaverFailure_ReportsInline()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "w")],
            ActivePrompt = "Work",
            CustomPrompt = "w",
        };
        TryRunOnSta(store, view =>
        {
            var ok = view.DeletePrompt();

            Assert.False(ok);
            Assert.StartsWith("Save failed:", view.StatusMessage, StringComparison.Ordinal);
        }, saver: _ => throw new InvalidOperationException("disk gone"));
    }

    [Fact]
    public void DeletePrompt_LastEntry_EmptiesLibraryAndEditor()
    {
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "w")],
            ActivePrompt = "Work",
            CustomPrompt = "w",
        };
        TryRunOnSta(store, view =>
        {
            var ok = view.DeletePrompt();

            Assert.True(ok);
            Assert.Empty(store.Prompts);
            Assert.Equal("", store.ActivePrompt);
            Assert.Equal("", store.CustomPrompt);
            Assert.Equal("", view.PromptText);
            Assert.Equal("", view.PromptName);
            Assert.Equal(0, view.PromptCount);
        });
    }

    [Fact]
    public void DeletePrompt_NothingSelected_RejectsWithMessage()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            var ok = view.DeletePrompt();

            Assert.False(ok);
            Assert.Contains("Select", view.StatusMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SaveNow_WritesEditorIntoActiveEntry()
    {
        // The plain Save footer commits the editor into the active prompt —
        // editing no longer bypasses the library.
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "old")],
            ActivePrompt = "Work",
            CustomPrompt = "old",
        };
        var saved = new List<SettingsStore>();
        TryRunOnSta(store, view =>
        {
            view.PromptText = "  edited  ";

            var ok = view.SaveNow();

            Assert.True(ok);
            Assert.True(store.Prompts.SequenceEqual([new PromptEntry("Work", "edited")]));
            Assert.Equal("edited", store.CustomPrompt);
            Assert.Equal("Saved ✓", view.StatusMessage);
            Assert.Single(saved);
        }, saver: saved.Add);
    }

    [Fact]
    public void TestNowAsync_SendsActivePromptText()
    {
        // The backend pipeline receives the active text, not the stale store
        // copy — the byte-identical contract at the LlmClient boundary.
        var store = new SettingsStore
        {
            Prompts = [new PromptEntry("Work", "active text")],
            ActivePrompt = "Work",
            CustomPrompt = "active text",
        };
        // Captured via closure and asserted INSIDE the STA body: on
        // headless runners the body never runs and the test passes vacuously
        // (an assertion after TryRunOnSta would fail on the unset null).
        string? sentPrompt = null;
        TryRunOnSta(store, view =>
        {
            view.TestNowAsync().GetAwaiter().GetResult();

            Assert.StartsWith("Test OK — transcript:", view.StatusMessage, StringComparison.Ordinal);
            Assert.Equal("active text", sentPrompt);
        }, transcribeAsync: (_, _, _, _, prompt) =>
        {
            sentPrompt = prompt;
            return Task.FromResult(("hello", 0));
        });
    }

    [Fact]
    public void ReloadFromSettings_ExternalLibraryChange_RefreshesPickerAndEditor()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            store.Prompts = [new PromptEntry("Work", "w")];
            store.ActivePrompt = "Work";
            store.CustomPrompt = "w";
            view.ReloadFromSettings();

            Assert.True(view.PromptNames.SequenceEqual(["Work"]));
            Assert.Equal("Work", view.SelectedPromptName);
            Assert.Equal("Work", view.PromptName);
            Assert.Equal("w", view.PromptText);
        });
    }

    [Fact]
    public void ReloadFromSettings_ExternalChange_RefreshesFields()
    {
        var store = new SettingsStore();
        TryRunOnSta(store, view =>
        {
            store.BaseUrl = "https://example.test/v1beta";
            store.Model = "test-model";
            view.ReloadFromSettings();

            Assert.Equal("https://example.test/v1beta", view.BaseUrlText);
            Assert.Equal("test-model", view.ModelText);
        });
    }

    [Fact]
    public void MainWindow_DefaultCtor_HostsGeminiView()
    {
        RunOnStaWindow(window =>
        {
            Assert.IsType<GeminiSettingsView>(window.SectionView(MainSection.Gemini));
        }, () => new MainWindow());
    }

    [Fact]
    public void MainWindow_NavigateToGemini_ShowsRegisteredContent()
    {
        RunOnStaWindow(window =>
        {
            window.NavigateTo(MainSection.Gemini);

            Assert.Equal(MainSection.Gemini, window.CurrentSection);
            Assert.IsType<GeminiSettingsView>(window.SectionView(MainSection.Gemini));
        }, () => new MainWindow());
    }

    private static void TryRunOnSta(
        SettingsStore store,
        Action<GeminiSettingsView> body,
        Action<SettingsStore>? saver = null,
        Func<byte[], IReadOnlyList<string>, string, string, string, Task<(string Transcript, int UsedIndex)>>? transcribeAsync = null)
    {
        StaTestHelper.TryRunOnSta(() =>
        {
            var view = new GeminiSettingsView(
                store,
                saver ?? (_ => { }),
                recordTone: () => Array.Empty<byte>(),
                transcribeAsync: transcribeAsync ?? ((_, _, _, _, _) => Task.FromResult(("", 0))));
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
