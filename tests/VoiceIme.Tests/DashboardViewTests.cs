using System;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Dashboard (home) screen tests. WPF construction needs an STA thread plus
/// a window station — on headless runners construction throws and the test
/// passes vacuously (same pattern as <see cref="MainWindowTests"/>). Full
/// GUI-level coverage runs on windows-latest CI.
/// </summary>
[Collection("WpfSta")]
public sealed class DashboardViewTests
{
    [Fact]
    public void EmptyStore_ShowsEmptyState()
    {
        TryRunOnSta(view =>
        {
            Assert.True(view.IsRecentEmptyVisible);
            Assert.Null(view.LatestTranscript);
        });
    }

    [Fact]
    public void RecentEntry_ShowsTranscriptAndTime()
    {
        TryRunOnSta((store, clips, view) =>
        {
            clips.Add("hello world");

            view.RefreshFromStores();

            Assert.False(view.IsRecentEmptyVisible);
            Assert.NotNull(view.LatestTranscript);
            Assert.Equal("hello world", view.LatestTranscript.Text);
            Assert.Equal("hello world", view.RecentBody.Text);
            Assert.False(string.IsNullOrEmpty(view.RecentTime.Text));
        });
    }

    [Fact]
    public void HotkeyHint_MentionsStoredHotkey()
    {
        TryRunOnSta((store, _, view) =>
        {
            store.Hotkey = "Alt+F4";
            view.RefreshFromStores();

            Assert.Equal("Press Alt+F4 to dictate.", view.HotkeyHint);
            Assert.Contains("Alt+F4", view.TaglineText.Text);
        });
    }

    [Fact]
    public void BindsInjectedStores_ExposesSameInstances()
    {
        var store = new SettingsStore();
        var clips = new ClipboardStore();
        TryRunOnSta(() =>
        {
            var view = new DashboardView(store, clips);
            Assert.Same(store, view.BoundSettings);
            Assert.Same(clips, view.BoundStore);
        });
    }

    /// <summary>Builds a dashboard with fresh in-memory stores on the STA thread.</summary>
    private static bool TryRunOnSta(Action<DashboardView> body) =>
        StaTestHelper.TryRunOnSta(() =>
        {
            var store = new SettingsStore();
            var clips = new ClipboardStore();
            var view = new DashboardView(store, clips);
            body(view);
        });

    /// <summary>Builds a dashboard, exposing the stores so tests can mutate them first.</summary>
    private static bool TryRunOnSta(
        Action<SettingsStore, ClipboardStore, DashboardView> body) =>
        StaTestHelper.TryRunOnSta(() =>
        {
            var store = new SettingsStore();
            var clips = new ClipboardStore();
            var view = new DashboardView(store, clips);
            body(store, clips, view);
        });
}
