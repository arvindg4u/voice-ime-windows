using System;
using System.IO;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>DPAPI round-trip — Windows only; skipped elsewhere (CI is windows-latest).</summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void Save_Load_RoundTripsKeys_WithoutPlaintextOnDisk()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only

        // Temp-file redirect (see below): the real %AppData% file is never
        // touched, so a crash mid-test cannot destroy developer settings.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var store = new SettingsStore
            {
                ApiKeys = ["synthetic-key-1", "synthetic-key-2"],
                Model = "synthetic-model",
            };
            store.Save();

            var reloaded = SettingsStore.Load();
            Assert.Equal(["synthetic-key-1", "synthetic-key-2"], reloaded.ApiKeys);
            Assert.Equal("synthetic-model", reloaded.Model);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("synthetic-key-1", raw);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_Load_RoundTripsGeneralSettings()
    {
        if (!OperatingSystem.IsWindows()) return; // Save uses DPAPI

        // Redirect to a temp file: the real %AppData% file is never touched,
        // so parallel xUnit classes cannot interleave on it.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            // Arrange
            var store = new SettingsStore
            {
                Hotkey = "Alt+F4",
                ActivationMode = ActivationModes.PushToTalk,
                Microphone = "Synthetic Mic",
                MuteWhileRecording = true,
            };
            store.Save();

            // Act
            var reloaded = SettingsStore.Load();

            // Assert
            Assert.Equal("Alt+F4", reloaded.Hotkey);
            Assert.Equal(ActivationModes.PushToTalk, reloaded.ActivationMode);
            Assert.Equal("Synthetic Mic", reloaded.Microphone);
            Assert.True(reloaded.MuteWhileRecording);

            var raw = File.ReadAllText(path);
            Assert.Contains("Alt+F4", raw);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OldFileWithoutNewFields_UsesDefaults()
    {
        // Arrange: pre-Task-3 file — no hotkey/activationMode/microphone keys,
        // and no apiKeysProtected so no DPAPI call happens (any-OS safe).
        // Written to a temp file via the path seam; the developer's real
        // settings are never touched.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
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
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
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
