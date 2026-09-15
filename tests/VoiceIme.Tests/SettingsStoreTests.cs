using System;
using System.IO;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>DPAPI round-trip — Windows only; skipped elsewhere (CI is windows-latest).</summary>
[Collection("SettingsFile")]
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
            Assert.True(reloaded.ApiKeys.SequenceEqual(["synthetic-key-1", "synthetic-key-2"]));
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

            // Self-diagnosing: parse the raw file BEFORE Load so a future
            // failure shows whether Save coerced the value or Load read a
            // different file. Parsed (not substring) because System.Text.Json
            // escapes "+" as + on the wire — the value is correct when
            // the parsed "hotkey" property equals, regardless of escaping.
            var rawAfterSave = File.ReadAllText(path);
            Assert.Equal(
                "Alt+F4",
                System.Text.Json.JsonDocument.Parse(rawAfterSave)
                    .RootElement.GetProperty("hotkey").GetString());

            // Act
            var reloaded = SettingsStore.Load();

            // Assert
            Assert.Equal("Alt+F4", reloaded.Hotkey);
            Assert.Equal(ActivationModes.PushToTalk, reloaded.ActivationMode);
            Assert.Equal("Synthetic Mic", reloaded.Microphone);
            Assert.True(reloaded.MuteWhileRecording);

            var raw = File.ReadAllText(path);
            Assert.Equal(
                "Alt+F4",
                System.Text.Json.JsonDocument.Parse(raw)
                    .RootElement.GetProperty("hotkey").GetString());
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
            Assert.Equal(AppThemes.System, loaded.Theme);
            Assert.Equal(AppLanguages.English, loaded.Language);
            Assert.False(loaded.HasLoadWarnings);
            Assert.Empty(loaded.LoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(AppThemes.System)]
    [InlineData(AppThemes.Light)]
    [InlineData(AppThemes.Dark)]
    public void CoerceTheme_Valid_KeepsValue(string value)
    {
        Assert.Equal(value, SettingsStore.CoerceTheme(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("charcoal")]
    [InlineData("SYSTEM")]
    public void CoerceTheme_Invalid_FallsBackToSystem(string? value)
    {
        Assert.Equal(AppThemes.System, SettingsStore.CoerceTheme(value));
    }

    [Theory]
    [InlineData(AppLanguages.English)]
    public void CoerceLanguage_Valid_KeepsValue(string value)
    {
        Assert.Equal(value, SettingsStore.CoerceLanguage(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fr")]
    [InlineData("EN")]
    public void CoerceLanguage_Invalid_FallsBackToEnglish(string? value)
    {
        Assert.Equal(AppLanguages.English, SettingsStore.CoerceLanguage(value));
    }

    [Fact]
    public void Save_Load_RoundTripsAboutSettings()
    {
        if (!OperatingSystem.IsWindows()) return; // Save uses DPAPI

        // Temp-file redirect: the real %AppData% file is never touched.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var store = new SettingsStore
            {
                Theme = AppThemes.Dark,
                Language = AppLanguages.English,
            };
            store.Save();

            var reloaded = SettingsStore.Load();

            Assert.Equal(AppThemes.Dark, reloaded.Theme);
            Assert.Equal(AppLanguages.English, reloaded.Language);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OldFileWithoutAboutFields_UsesDefaults()
    {
        // Pre-Task-2 file — no theme/language keys, and no
        // apiKeysProtected so no DPAPI call happens (any-OS safe).
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(AppThemes.System, loaded.Theme);
            Assert.Equal(AppLanguages.English, loaded.Language);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadThemeAndLanguage_CoercesToDefaultsWithWarning()
    {
        // Unknown preference strings coerce, never throw.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"theme":"charcoal","language":"fr"}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(AppThemes.System, loaded.Theme);
            Assert.Equal(AppLanguages.English, loaded.Language);
            Assert.True(loaded.HasLoadWarnings);
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("theme"));
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("language"));
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    // Task 6 (Handy parity gaps): Sound atoms + cancel key. Additive
    // fields — Load/Save round-trips carry them, old files default, bad
    // values coerce, never throw.

    [Theory]
    [InlineData(-1000, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void ClampVolume_ClampsTo0To100(int value, int expected)
    {
        Assert.Equal(expected, SettingsStore.ClampVolume(value));
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("stereo")]
    [InlineData("average")]
    public void CoerceChannel_Valid_KeepsValue(string value)
    {
        Assert.Equal(value, SettingsStore.CoerceChannel(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("surround")]
    [InlineData("Stereo")]
    public void CoerceChannel_Invalid_FallsBackToMono(string? value)
    {
        Assert.Equal(AudioChannels.Mono, SettingsStore.CoerceChannel(value));
    }

    [Theory]
    [InlineData("Esc")]
    [InlineData("esc")]
    [InlineData("Escape")]
    [InlineData("F5")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Alt+F4")]
    public void CoerceCancelHotkey_Valid_KeepsValue(string value)
    {
        Assert.Equal(value.Trim(), SettingsStore.CoerceCancelHotkey(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    public void CoerceCancelHotkey_Invalid_FallsBackToEsc(string? value)
    {
        Assert.Equal(SettingsStore.DefaultCancelHotkey, SettingsStore.CoerceCancelHotkey(value));
    }

    [Fact]
    public void Load_OldFileWithoutTask6Fields_UsesDefaults()
    {
        // Pre-Task-6 file (Task-5-era keys only) — no DPAPI blob, any-OS safe.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(AudioChannels.Mono, loaded.Channel);
            Assert.Equal("", loaded.OutputDevice);
            Assert.Equal(SettingsStore.DefaultVolume, loaded.Volume);
            Assert.False(loaded.AudioFeedback);
            Assert.Equal(SettingsStore.DefaultCancelHotkey, loaded.CancelHotkey);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadTask6Values_CoerceWithWarnings()
    {
        // Bad channel/cancel-key coerce with warnings; a bad volume clamps
        // silently (same contract as historyLimit). Any-OS safe.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"channel":"surround","outputDevice":"Speakers","volume":9999,"audioFeedback":true,"cancelHotkey":"bogus"}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(AudioChannels.Mono, loaded.Channel);
            Assert.Equal("Speakers", loaded.OutputDevice);
            Assert.Equal(SettingsStore.MaxVolume, loaded.Volume);
            Assert.True(loaded.AudioFeedback);
            Assert.Equal(SettingsStore.DefaultCancelHotkey, loaded.CancelHotkey);
            Assert.True(loaded.HasLoadWarnings);
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("channel"));
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("cancelHotkey"));
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_Load_RoundTripsSoundAndCancel()
    {
        if (!OperatingSystem.IsWindows()) return; // Save uses DPAPI

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var store = new SettingsStore
            {
                Channel = AudioChannels.Average,
                OutputDevice = "Synthetic Speakers",
                Volume = 42,
                AudioFeedback = true,
                CancelHotkey = "Ctrl+Esc",
            };
            store.Save();

            var reloaded = SettingsStore.Load();

            Assert.Equal(AudioChannels.Average, reloaded.Channel);
            Assert.Equal("Synthetic Speakers", reloaded.OutputDevice);
            Assert.Equal(42, reloaded.Volume);
            Assert.True(reloaded.AudioFeedback);
            Assert.Equal("Ctrl+Esc", reloaded.CancelHotkey);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_SalvagesSurvivingTask6Fields()
    {
        // Truncated file — whole-parse fails, but the intact pairs salvage.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var corrupt = """{"baseUrl":"https://example.invalid","channel":"stereo","volume":42,"audioFeedback":true,"cancelHotkey":"Ctrl+Esc","customPrompt":""";
            File.WriteAllText(path, corrupt);

            var loaded = SettingsStore.Load();

            Assert.Equal(AudioChannels.Stereo, loaded.Channel);
            Assert.Equal(42, loaded.Volume);
            Assert.True(loaded.AudioFeedback);
            Assert.Equal("Ctrl+Esc", loaded.CancelHotkey);
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

    [Theory]
    [InlineData(PasteMethods.CtrlV)]
    [InlineData(PasteMethods.ShiftInsert)]
    [InlineData(PasteMethods.CtrlShiftV)]
    public void CoercePasteMethod_Valid_KeepsValue(string value)
    {
        Assert.Equal(value, SettingsStore.CoercePasteMethod(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ctrl_v")]
    [InlineData("hold")]
    public void CoercePasteMethod_Invalid_FallsBackToCtrlV(string? value)
    {
        Assert.Equal(PasteMethods.CtrlV, SettingsStore.CoercePasteMethod(value));
    }

    [Theory]
    [InlineData(-1000, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(int.MaxValue, 500)]
    public void ClampHistoryLimit_ClampsTo10To500(int value, int expected)
    {
        Assert.Equal(expected, SettingsStore.ClampHistoryLimit(value));
    }

    [Fact]
    public void Save_Load_RoundTripsAdvancedSettings()
    {
        if (!OperatingSystem.IsWindows()) return; // Save uses DPAPI

        // Temp-file redirect: the real %AppData% file is never touched.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var store = new SettingsStore
            {
                StartHidden = true,
                Autostart = true,
                ShowTrayIcon = false,
                ShowOverlay = OverlayModes.Minimal,
                AutoSubmit = true,
                PasteMethod = PasteMethods.ShiftInsert,
                HistoryLimit = 250,
            };
            store.Save();

            var reloaded = SettingsStore.Load();

            Assert.True(reloaded.StartHidden);
            Assert.True(reloaded.Autostart);
            Assert.False(reloaded.ShowTrayIcon);
            Assert.Equal(OverlayModes.Minimal, reloaded.ShowOverlay);
            Assert.True(reloaded.AutoSubmit);
            Assert.Equal(PasteMethods.ShiftInsert, reloaded.PasteMethod);
            Assert.Equal(250, reloaded.HistoryLimit);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OldFileWithoutAdvancedFields_UsesDefaults()
    {
        // Pre-Task-1 file — none of the six new keys, and no
        // apiKeysProtected so no DPAPI call happens (any-OS safe).
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0}""");

            var loaded = SettingsStore.Load();

            Assert.False(loaded.StartHidden);
            Assert.False(loaded.Autostart);
            Assert.True(loaded.ShowTrayIcon);
            Assert.Equal(OverlayModes.Full, loaded.ShowOverlay);
            Assert.False(loaded.AutoSubmit);
            Assert.Equal(PasteMethods.CtrlV, loaded.PasteMethod);
            Assert.Equal(SettingsStore.DefaultHistoryLimit, loaded.HistoryLimit);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadPasteMethod_CoercesToDefaultWithWarning()
    {
        // Handy's snake_case value is not in our Windows subset — coerced,
        // never thrown. The out-of-range limit clamps silently (10–500).
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"pasteMethod":"ctrl_v","historyLimit":9999}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(PasteMethods.CtrlV, loaded.PasteMethod);
            Assert.Equal(500, loaded.HistoryLimit);
            Assert.True(loaded.HasLoadWarnings);
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("pasteMethod"));
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    // Task 8 (Handy parity gaps): ShowOverlay graduated bool→string.
    // Legacy Task-1 JSON bools map silently (true→full, false→none); bad
    // strings warn and fall back to full; no version bump; never throw.

    [Fact]
    public void Load_LegacyBoolShowOverlayTrue_CoercesToFull()
    {
        // Pre-Task-8 file with the Task-1 bool — no DPAPI blob, any-OS safe.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"showOverlay":true}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(OverlayModes.Full, loaded.ShowOverlay);
            Assert.False(loaded.HasLoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyBoolShowOverlayFalse_CoercesToNone()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"showOverlay":false}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(OverlayModes.None, loaded.ShowOverlay);
            Assert.False(loaded.HasLoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_StringShowOverlay_RoundTripsModes()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"showOverlay":"minimal"}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(OverlayModes.Minimal, loaded.ShowOverlay);
            Assert.False(loaded.HasLoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadShowOverlay_CoercesToFullWithWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"showOverlay":"hologram"}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(OverlayModes.Full, loaded.ShowOverlay);
            Assert.True(loaded.HasLoadWarnings);
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("showOverlay"));
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OldFileWithoutAutoSubmit_DefaultsFalse()
    {
        // Pre-Task-8 file carries no autoSubmit — additive, off by default.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"showOverlay":"full"}""");

            var loaded = SettingsStore.Load();

            Assert.False(loaded.AutoSubmit);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    // Task 7 (Handy parity gaps): prompt library. Additive fields —
    // Load/Save round-trips carry them, old files default, bad values
    // coerce, legacy customPrompt migrates idempotently, never throw.

    [Fact]
    public void Load_OldFileWithoutPromptFields_UsesDefaults()
    {
        // Pre-Task-7 file (Task-6-era keys only) — no DPAPI blob, any-OS safe.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0}""");

            var loaded = SettingsStore.Load();

            Assert.Empty(loaded.Prompts);
            Assert.Equal("", loaded.ActivePrompt);
            Assert.Equal("", loaded.ActivePromptText);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyCustomPrompt_MigratesToDefaultEntry()
    {
        // A pre-library file WITH text seeds a single Default entry, selected
        // active; the legacy field mirrors the active text back.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"polish it","keyCursor":0}""");

            var loaded = SettingsStore.Load();

            Assert.True(loaded.Prompts.SequenceEqual(
                [new PromptEntry(PromptLibrary.DefaultPromptName, "polish it")]));
            Assert.Equal(PromptLibrary.DefaultPromptName, loaded.ActivePrompt);
            Assert.Equal("polish it", loaded.ActivePromptText);
            Assert.Equal("polish it", loaded.CustomPrompt);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_Migration_IsIdempotentAcrossReloads()
    {
        // Save the migrated store, reload twice: still a single Default
        // entry — never duplicates. Windows-only (Save uses DPAPI).
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"legacy text","keyCursor":0}""");

            var once = SettingsStore.Load();
            once.Save();
            var twice = SettingsStore.Load();
            twice.Save();
            var thrice = SettingsStore.Load();

            Assert.Single(thrice.Prompts);
            Assert.Equal(PromptLibrary.DefaultPromptName, thrice.ActivePrompt);
            Assert.Equal("legacy text", thrice.ActivePromptText);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PromptsAlreadyPresent_IgnoresLegacyCustomPrompt()
    {
        // Library owns the text once non-empty (documented rule) — the stale
        // legacy value is overwritten by the mirror, not merged in.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"stale","keyCursor":0,"prompts":[{"name":"Work","text":"lib text"}],"activePrompt":"Work"}""");

            var loaded = SettingsStore.Load();

            Assert.True(loaded.Prompts.SequenceEqual([new PromptEntry("Work", "lib text")]));
            Assert.Equal("Work", loaded.ActivePrompt);
            Assert.Equal("lib text", loaded.ActivePromptText);
            Assert.Equal("lib text", loaded.CustomPrompt);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadPromptValues_CoerceWithWarnings()
    {
        // Overlong coerces (truncated name/text warn), the unknown active
        // name falls back to the first entry with a warning, a non-array
        // prompts value yields the empty library with a warning. Any-OS safe.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var longName = new string('n', 61);
            var longText = new string('x', 2001);
            File.WriteAllText(path,
                """{"baseUrl":"https://example.invalid","model":"m","customPrompt":"","keyCursor":0,"prompts":[{"name":"""" + longName + """","text":"""" + longText + """},{"name":"Work","text":"kept"},{"name":"work","text":"dup"},{"nope":1},"stray"],"activePrompt":"Ghost"}""");

            var loaded = SettingsStore.Load();

            Assert.Equal(2, loaded.Prompts.Count);
            Assert.Equal(60, loaded.Prompts[0].Name.Length);
            Assert.Equal(2000, loaded.Prompts[0].Text.Length);
            Assert.Equal("Work", loaded.Prompts[1].Name);
            Assert.Equal(loaded.Prompts[0].Name, loaded.ActivePrompt);
            Assert.True(loaded.HasLoadWarnings);
            Assert.Contains(loaded.LoadWarnings, w => w.Contains("Ghost"));
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_Load_RoundTripsPromptLibrary()
    {
        if (!OperatingSystem.IsWindows()) return; // Save uses DPAPI

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var store = new SettingsStore
            {
                Prompts =
                [
                    new PromptEntry("Work", "polish it"),
                    new PromptEntry("Mail", "short replies"),
                ],
                ActivePrompt = "Mail",
            };
            store.Save();

            var reloaded = SettingsStore.Load();

            Assert.True(reloaded.Prompts.SequenceEqual(
                [new PromptEntry("Work", "polish it"), new PromptEntry("Mail", "short replies")]));
            Assert.Equal("Mail", reloaded.ActivePrompt);
            Assert.Equal("short replies", reloaded.ActivePromptText);
            Assert.Equal("short replies", reloaded.CustomPrompt);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_SalvagesActivePromptName()
    {
        // Truncated file — whole-parse fails; the surviving activePrompt name
        // salvages (the prompts array cannot regex-survive, so the legacy
        // customPrompt reseeds Default through Migrate). Any-OS safe.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        SettingsStore.SettingsPathOverride = () => path;
        try
        {
            var corrupt = """{"baseUrl":"https://example.invalid","customPrompt":"salvaged","activePrompt":"Active",""";
            File.WriteAllText(path, corrupt);

            var loaded = SettingsStore.Load();

            Assert.True(loaded.Prompts.SequenceEqual(
                [new PromptEntry(PromptLibrary.DefaultPromptName, "salvaged")]));
            Assert.Equal(PromptLibrary.DefaultPromptName, loaded.ActivePrompt);
            Assert.Equal("salvaged", loaded.ActivePromptText);
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
}
