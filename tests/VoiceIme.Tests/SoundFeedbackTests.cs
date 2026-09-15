using System;
using VoiceIme.Views;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Task 6 pure sound/cancel helpers (display gain, arm decision, volume
/// label, channel labels, output enumeration). No WPF construction — runs on
/// any OS, no STA needed. The post-paste tone itself is covered by the
/// disabled-path no-op below; the enabled path needs Windows audio and runs
/// on windows-latest CI via the speaker-test click path.
/// </summary>
public sealed class SoundFeedbackTests
{
    [Theory]
    [InlineData(1f, 100, 1f)]
    [InlineData(1f, 50, 0.5f)]
    [InlineData(1f, 0, 0f)]
    [InlineData(0.5f, 100, 0.5f)]
    [InlineData(2f, 100, 1f)]
    [InlineData(-1f, 100, 0f)]
    [InlineData(1f, 150, 1f)]
    [InlineData(1f, -50, 0f)]
    public void ApplyDisplayGain_ScalesAndClamps(float level, int volume, float expected)
    {
        Assert.Equal(expected, SoundFeedback.ApplyDisplayGain(level, volume), precision: 5);
    }

    [Theory]
    [InlineData(DictationState.Recording, true)]
    [InlineData(DictationState.Uploading, true)]
    [InlineData(DictationState.Idle, false)]
    [InlineData(DictationState.Error, false)]
    public void ShouldArmCancel_OnlyWhileBusy(DictationState state, bool expected)
    {
        // A bare Esc must never be a global always-on registration — the
        // cancel window registers only while busy, then unregisters.
        Assert.Equal(expected, App.ShouldArmCancel(state));
    }

    [Theory]
    [InlineData(42, "42%")]
    [InlineData(0, "0%")]
    [InlineData(100, "100%")]
    [InlineData(-5, "0%")]
    [InlineData(150, "100%")]
    public void FormatVolume_ClampsAndPercents(int volume, string expected)
    {
        Assert.Equal(expected, GeneralSettingsView.FormatVolume(volume));
    }

    [Theory]
    [InlineData(AudioChannels.Mono, "Mono")]
    [InlineData(AudioChannels.Stereo, "Stereo")]
    [InlineData(AudioChannels.Average, "Mix-down (average)")]
    public void AudioChannels_LabelRoundTrip(string channel, string label)
    {
        Assert.Equal(label, AudioChannels.LabelFor(channel));
        Assert.Equal(channel, AudioChannels.ChannelForLabel(label));
    }

    [Theory]
    [InlineData("mono", true)]
    [InlineData("stereo", true)]
    [InlineData("average", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Stereo", false)]
    [InlineData("surround", false)]
    public void AudioChannels_IsValid_AcceptsOnlyStoredForms(string? value, bool expected)
    {
        Assert.Equal(expected, AudioChannels.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("surround")]
    [InlineData("Stereo")]
    public void AudioChannels_ChannelForLabel_Unknown_CoercesToMono(string? label)
    {
        Assert.Equal(AudioChannels.Mono, AudioChannels.ChannelForLabel(label));
    }

    [Fact]
    public void OutputDevices_ListNames_NeverThrows()
    {
        // Headless CI has no audio stack — the output dropdown falls back to
        // "System default" instead of throwing.
        var names = OutputDevices.ListNames();

        Assert.NotNull(names);
    }

    [Fact]
    public void PlayPostPasteTone_Disabled_IsNoOp()
    {
        var store = new SettingsStore { AudioFeedback = false };

        // Must return without throwing (and without spawning playback).
        SoundFeedback.PlayPostPasteTone(store);
    }

    [Fact]
    public void PlayPostPasteTone_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SoundFeedback.PlayPostPasteTone(null!));
    }
}
