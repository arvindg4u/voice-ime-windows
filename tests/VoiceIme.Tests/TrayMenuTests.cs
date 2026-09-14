using System.Linq;
using Xunit;

namespace VoiceIme.Tests;

public sealed class TrayMenuTests
{
    [Fact]
    public void IdleOrder_MatchesHandyIdleLayout()
    {
        var items = TrayMenu.Items(busy: false, version: "v1.0.0", hasTranscript: true)
            .ToList();

        Assert.Equal(
            new[]
            {
                TrayMenuAction.VersionHeader,
                TrayMenuAction.Separator,
                TrayMenuAction.CopyLastTranscript,
                TrayMenuAction.Separator,
                TrayMenuAction.History,
                TrayMenuAction.Separator,
                TrayMenuAction.Settings,
                TrayMenuAction.Separator,
                TrayMenuAction.Quit,
            },
            items.Select(i => i.Action));
    }

    [Fact]
    public void BusyOrder_InsertsCancelAfterVersionHeader()
    {
        var items = TrayMenu.Items(busy: true, version: "v1.0.0", hasTranscript: true)
            .ToList();

        Assert.Equal(
            new[]
            {
                TrayMenuAction.VersionHeader,
                TrayMenuAction.Separator,
                TrayMenuAction.Cancel,
                TrayMenuAction.Separator,
                TrayMenuAction.CopyLastTranscript,
                TrayMenuAction.Separator,
                TrayMenuAction.History,
                TrayMenuAction.Separator,
                TrayMenuAction.Settings,
                TrayMenuAction.Separator,
                TrayMenuAction.Quit,
            },
            items.Select(i => i.Action));
    }

    [Fact]
    public void VersionHeader_IsDisabled_AndShowsVersion()
    {
        var idle = TrayMenu.Items(busy: false, version: "v2.3.4", hasTranscript: true);
        var busy = TrayMenu.Items(busy: true, version: "v2.3.4", hasTranscript: true);

        Assert.Equal("Voice IME v2.3.4", idle[0].Label);
        Assert.False(idle[0].Enabled);
        Assert.Equal("Voice IME v2.3.4", busy[0].Label);
        Assert.False(busy[0].Enabled);
    }

    [Fact]
    public void SettingsLabel_CarriesHandyShortcutHint()
    {
        var items = TrayMenu.Items(busy: false, version: "v1.0.0", hasTranscript: true);

        Assert.Equal(
            TrayMenu.SettingsLabel,
            Assert.Single(items, i => i.Action == TrayMenuAction.Settings).Label);
        Assert.Contains("Ctrl+,", TrayMenu.SettingsLabel);
    }

    [Fact]
    public void CopyLastTranscript_DisabledWhenHistoryEmpty()
    {
        var empty = TrayMenu.Items(busy: false, version: "v1.0.0", hasTranscript: false);
        var full = TrayMenu.Items(busy: false, version: "v1.0.0", hasTranscript: true);

        Assert.False(Assert.Single(empty, i => i.Action == TrayMenuAction.CopyLastTranscript).Enabled);
        Assert.True(Assert.Single(full, i => i.Action == TrayMenuAction.CopyLastTranscript).Enabled);
    }

    [Fact]
    public void History_AlwaysEnabled_WithHistoryLabel()
    {
        foreach (var busy in new[] { false, true })
        {
            var items = TrayMenu.Items(busy, version: "v1.0.0", hasTranscript: false);

            var history = Assert.Single(items, i => i.Action == TrayMenuAction.History);
            Assert.True(history.Enabled);
            Assert.Equal(TrayMenu.HistoryLabel, history.Label);
        }
    }

    [Fact]
    public void Quit_AlwaysEnabled()
    {
        foreach (var busy in new[] { false, true })
        {
            var items = TrayMenu.Items(busy, version: "v1.0.0", hasTranscript: false);

            Assert.True(Assert.Single(items, i => i.Action == TrayMenuAction.Quit).Enabled);
        }
    }
}
