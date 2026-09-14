using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>DPAPI round-trip — Windows only; skipped elsewhere (CI is windows-latest).</summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void Save_Load_RoundTripsKeys_WithoutPlaintextOnDisk()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only

        var store = SettingsStore.Load();
        var originalKeys = store.ApiKeys.ToList();
        var originalModel = store.Model;
        try
        {
            store.ApiKeys = ["synthetic-key-1", "synthetic-key-2"];
            store.Model = "synthetic-model";
            store.Save();

            var reloaded = SettingsStore.Load();
            Assert.Equal(["synthetic-key-1", "synthetic-key-2"], reloaded.ApiKeys);
            Assert.Equal("synthetic-model", reloaded.Model);

            var raw = File.ReadAllText(SettingsStore.SettingsPath);
            Assert.DoesNotContain("synthetic-key-1", raw);
        }
        finally
        {
            store.ApiKeys = originalKeys;
            store.Model = originalModel;
            store.Save();
        }
    }

    [Fact]
    public void Save_Load_RoundTripsGeneralSettings()
    {
        if (!OperatingSystem.IsWindows()) return; // Save uses DPAPI

        var store = SettingsStore.Load();
        var originalHotkey = store.Hotkey;
        var originalMode = store.ActivationMode;
        var originalMic = store.Microphone;
        var originalMute = store.MuteWhileRecording;
        try
        {
            // Arrange
            store.Hotkey = "Alt+F4";
            store.ActivationMode = ActivationModes.PushToTalk;
            store.Microphone = "Synthetic Mic";
            store.MuteWhileRecording = true;
            store.Save();

            // Act
            var reloaded = SettingsStore.Load();

            // Assert
            Assert.Equal("Alt+F4", reloaded.Hotkey);
            Assert.Equal(ActivationModes.PushToTalk, reloaded.ActivationMode);
            Assert.Equal("Synthetic Mic", reloaded.Microphone);
            Assert.True(reloaded.MuteWhileRecording);

            var raw = File.ReadAllText(SettingsStore.SettingsPath);
            Assert.Contains("Alt+F4", raw);
        }
        finally
        {
            store.Hotkey = originalHotkey;
            store.ActivationMode = originalMode;
            store.Microphone = originalMic;
            store.MuteWhileRecording = originalMute;
            store.Save();
        }
    }

    [Fact]
    public void Load_OldFileWithoutNewFields_UsesDefaults()
    {
        // Arrange: pre-Task-3 file — no hotkey/activationMode/microphone keys,
        // and no apiKeysProtected so no DPAPI call happens (any-OS safe).
        var path = SettingsStore.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0}""");

            // Act
            var loaded = SettingsStore.Load();

            // Assert
            Assert.Equal(HotkeyChord.DefaultChord, loaded.Hotkey);
            Assert.Equal(ActivationModes.Toggle, loaded.ActivationMode);
            Assert.Equal("", loaded.Microphone);
            Assert.False(loaded.MuteWhileRecording);
        }
        finally
        {
            if (backup is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, backup);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Space")]
    [InlineData("Ctrl")]
    public void CoerceHotkey_Invalid_FallsBackToDefault(string? value)
    {
        Assert.Equal(HotkeyChord.DefaultChord, SettingsStore.CoerceHotkey(value));
    }

    [Fact]
    public void CoerceHotkey_Valid_KeepsTrimmedValue()
    {
        Assert.Equal("Alt+F4", SettingsStore.CoerceHotkey("  Alt+F4  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hold")]
    public void CoerceActivationMode_Invalid_FallsBackToToggle(string? value)
    {
        Assert.Equal(ActivationModes.Toggle, SettingsStore.CoerceActivationMode(value));
    }

    [Fact]
    public void CoerceActivationMode_Valid_KeepsValue()
    {
        Assert.Equal(
            ActivationModes.PushToTalk,
            SettingsStore.CoerceActivationMode(ActivationModes.PushToTalk));
    }
}
