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
    public const string ExeFileName = "VoiceIme.exe";

    /// <summary>
    /// EXE path for the logon shortcut. Prefers the running process path;
    /// falls back to the app directory plus the known EXE name. Pure and
    /// unit-testable — and it keeps the single-file-incompatible
    /// <c>Assembly.Location</c> fallback out of App (IL3000).
    /// </summary>
    public static string ExePathForShortcut(string? processPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory must not be blank.", nameof(baseDirectory));
        }

        return Path.Combine(baseDirectory, ExeFileName);
    }

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
