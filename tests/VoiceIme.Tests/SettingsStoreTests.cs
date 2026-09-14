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
}
