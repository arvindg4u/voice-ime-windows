using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Serializes every test that touches the process-global
/// <c>SettingsStore.SettingsPathOverride</c> hook. xUnit runs test collections
/// (not classes) in parallel, so without this, one class resetting the static
/// override mid-test redirects another class's Save/Load to the wrong file.
/// Mirrors <c>LoggerEnvCollection</c>. Keep this class otherwise empty — the
/// definition must live on a separate class, never on a test class itself.
/// </summary>
[CollectionDefinition("SettingsFile")]
public sealed class SettingsFileCollection : ICollectionFixture<SettingsFileLock>
{
}

/// <summary>Shared lock instance for the SettingsFile collection.</summary>
public sealed class SettingsFileLock
{
    public static readonly object Instance = new();
}
