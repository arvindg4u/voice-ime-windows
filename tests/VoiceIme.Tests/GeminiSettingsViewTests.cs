using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new GeminiSettingsView(
                    store,
                    saver ?? (_ => { }),
                    recordTone: () => Array.Empty<byte>(),
                    transcribeAsync: transcribeAsync ?? ((_, _, _, _, _) => Task.FromResult(("", 0))));
                body(view);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null && IsHeadlessFailure(failure))
        {
            return;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunOnStaWindow(Action<MainWindow> body, Func<MainWindow> create)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = create();
                body(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }

                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null && IsHeadlessFailure(failure))
        {
            return;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static bool IsHeadlessFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var name = current.GetType().Name;
            if (name.Contains("InvalidOperationException", StringComparison.Ordinal)
                || name.Contains("COMException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
