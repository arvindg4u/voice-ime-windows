using System;
using System.IO;
using Xunit;

namespace VoiceIme.Tests;

[Collection("SettingsFile")]
public sealed class SmartModeSettingsTests
{
    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), "voiceime-smart-" + Guid.NewGuid() + ".json");

    private static string UseTempPath()
    {
        var path = NewTempPath();
        SettingsStore.SettingsPathOverride = () => path;
        return path;
    }

    [Fact]
    public void SmartMode_DefaultsTrue()
    {
        Assert.True(new SettingsStore().SmartMode);
    }

    [Fact]
    public void Load_MissingSmartMode_DefaultsTrueWithoutWarnings()
    {
        var path = UseTempPath();
        try
        {
            File.WriteAllText(path, """{"schemaVersion":1,"model":"gemini-2.5-flash"}""");
            var store = SettingsStore.Load();

            Assert.True(store.SmartMode);
            Assert.Empty(store.LoadWarnings);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExplicitFalse_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = UseTempPath();
        try
        {
            var store = SettingsStore.Load();
            store.SmartMode = false;
            store.Save();

            SettingsStore.SettingsPathOverride = () => path;
            Assert.False(SettingsStore.Load().SmartMode);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExplicitTrue_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = UseTempPath();
        try
        {
            var store = SettingsStore.Load();
            store.SmartMode = true;
            store.Save();

            SettingsStore.SettingsPathOverride = () => path;
            Assert.True(SettingsStore.Load().SmartMode);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Salvage_CorruptFileWithFalseSmartMode_RecoversFalse()
    {
        var path = UseTempPath();
        try
        {
            File.WriteAllText(path, """{"schemaVersion": 1, "smartMode": false, "model": """);
            var store = SettingsStore.Load();

            Assert.False(store.SmartMode);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Salvage_CorruptFileWithoutSmartMode_StaysTrue()
    {
        var path = UseTempPath();
        try
        {
            File.WriteAllText(path, """{"schemaVersion": 1, "model": """);
            var store = SettingsStore.Load();

            Assert.True(store.SmartMode);
        }
        finally
        {
            SettingsStore.SettingsPathOverride = null;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
