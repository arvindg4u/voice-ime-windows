using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Serializes every test that constructs WPF controls
/// (<c>Application.LoadComponent</c> is not thread-safe — concurrent
/// <c>InitializeComponent</c> calls race inside <c>System.IO.Packaging</c>,
/// dotnet/wpf#297). Every class that news up a Window, UserControl, or view
/// belongs here. Mirrors <c>LoggerEnvCollection</c>. Keep this class
/// otherwise empty — the definition must live on a separate class, never on
/// a test class itself.
/// Linux note: membership here means the test constructs real WPF controls,
/// which cannot execute on Linux (Windows-only desktop runtime, no window
/// station). Such tests pass vacuously on Linux/headless runners; only
/// windows-latest CI (.github/workflows/windows.yml) gives full GUI-level
/// coverage — Linux runs prove compile + OS-independent logic only.
/// </summary>
[CollectionDefinition("WpfSta")]
public sealed class WpfStaCollection : ICollectionFixture<WpfStaLock>
{
}

/// <summary>Shared lock instance for the WpfSta collection.</summary>
public sealed class WpfStaLock
{
    public static readonly object Instance = new();
}
