using System;
using System.IO;

namespace VoiceIme;

/// <summary>Reconcile decision for the logon shortcut — App executes it.</summary>
public enum StartupAction
{
    None,
    Create,
    Remove,
}

/// <summary>
/// Autostart shell helpers (Handy autostart port, Startup-folder subset).
/// Pure: the shortcut path builds from an injected folder and the
/// create/remove decision is a bool function, so both are unit-testable on
/// any OS. The real Startup folder and the .lnk bytes are touched only by
/// App — tests never go near them.
/// </summary>
public static class StartupShell
{
    public const string ShortcutFileName = "Voice IME.lnk";

    /// <summary>
    /// Full path of the logon shortcut inside a Startup folder. The folder is
    /// injected (production passes SpecialFolder.Startup) so tests can point
    /// anywhere. Throws on a blank folder — a programming error, not data.
    /// </summary>
    public static string ShortcutPath(string startupFolder)
    {
        if (string.IsNullOrWhiteSpace(startupFolder))
        {
            throw new ArgumentException("Startup folder must not be blank.", nameof(startupFolder));
        }

        return Path.Combine(startupFolder, ShortcutFileName);
    }

    /// <summary>
    /// Create when opted in but missing, remove when opted out but present,
    /// otherwise leave the folder alone (external drift reconciles at launch).
    /// </summary>
    public static StartupAction DecideReconcile(bool autostart, bool shortcutExists) =>
        (autostart, shortcutExists) switch
        {
            (true, false) => StartupAction.Create,
            (false, true) => StartupAction.Remove,
            _ => StartupAction.None,
        };
}
