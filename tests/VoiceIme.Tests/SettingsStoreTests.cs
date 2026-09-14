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

    [Fact]
    public void Load_FrozenV0Fixture_GainsSchemaVersionAndNewDefaults()
    {
        // Task 9 owns this: frozen v0 bytes (only baseUrl/model/keys circa
        // Task 1) must load with schemaVersion 1 plus every Task 3 field at
        // its default, without warnings — v0→v1 is a clean migration.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0}""");

            // Act
            var loaded = SettingsStore.Load();

            // Assert
            Assert.Equal(SettingsStore.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("https://example.invalid", loaded.BaseUrl);
            Assert.Equal("m", loaded.Model);
            Assert.Equal(HotkeyChord.DefaultChord, loaded.Hotkey);
            Assert.Equal(ActivationModes.Toggle, loaded.ActivationMode);
            Assert.Equal("", loaded.Microphone);
            Assert.False(loaded.MuteWhileRecording);
            Assert.False(loaded.HasLoadWarnings);
            Assert.Empty(loaded.LoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_SalvagesSurvivingFieldsOverDefaults()
    {
        // Arrange: truncated file — whole-parse fails, but the hotkey and
        // model pairs are intact in the raw text. Uses the path seam; the
        // real %AppData% file is never touched.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var corrupt = """{"baseUrl":"https://example.invalid","model":"salvaged-model","hotkey":"Alt+F4","customPrompt":""";
            File.WriteAllText(path, corrupt);

            // Act
            var loaded = SettingsStore.Load();

            // Assert
            Assert.Equal("salvaged-model", loaded.Model);
            Assert.Equal("Alt+F4", loaded.Hotkey);
            Assert.Equal(ActivationModes.Toggle, loaded.ActivationMode);
            Assert.Equal(SettingsStore.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.True(loaded.HasLoadWarnings);

            // Never wipes: the corrupt bytes stay for manual recovery.
            Assert.Equal(corrupt, File.ReadAllText(path));
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadEnumString_CoercesToDefaultWithWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"hotkey":"nonsense-chord","activationMode":"hold"}""");

            // Act
            var loaded = SettingsStore.Load();

            // Assert
            Assert.Equal(HotkeyChord.DefaultChord, loaded.Hotkey);
            Assert.Equal(ActivationModes.Toggle, loaded.ActivationMode);
            Assert.Equal(SettingsStore.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.True(loaded.HasLoadWarnings);
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("hotkey"));
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("activationMode"));

            // Never wipes: the original file is untouched.
            Assert.Contains("nonsense-chord", File.ReadAllText(path));
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyLooseShapes_AcceptedWithoutWarnings()
    {
        // Legacy writers stored numbers/bools as strings or bare numbers —
        // the custom converter still honours them, with no warnings.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"schemaVersion":"1","baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":"3","activationMode":"push_to_talk","microphone":"Mic","muteWhileRecording":1}""");

            // Act
            var loaded = SettingsStore.Load();

            // Assert
            Assert.Equal(3, loaded.KeyCursor);
            Assert.Equal(ActivationModes.PushToTalk, loaded.ActivationMode);
            Assert.True(loaded.MuteWhileRecording);
            Assert.False(loaded.HasLoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsWithoutWarnings()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            // Act (file does not exist)
            var loaded = SettingsStore.Load();

            // Assert
            Assert.Equal(SettingsStore.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.False(loaded.HasLoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
        }
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var store = new SettingsStore { Hotkey = "bogus", ActivationMode = "bogus" };
        store.Migrate();
        var warningsAfterFirst = store.LoadWarnings.Count;
        store.Migrate();

        Assert.Equal(SettingsStore.CurrentSchemaVersion, store.SchemaVersion);
        Assert.Equal(HotkeyChord.DefaultChord, store.Hotkey);
        Assert.Equal(ActivationModes.Toggle, store.ActivationMode);
        Assert.Equal(warningsAfterFirst, store.LoadWarnings.Count);
    }
}
