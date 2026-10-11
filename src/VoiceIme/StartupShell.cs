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
    /// Argument the logon shortcut (and installer autostart entry) passes so
    /// a sign-in launch starts in the tray without flashing the window.
    /// </summary>
    public const string HiddenLaunchArg = "--hidden";

    /// <summary>
    /// True when a command-line argument requests a tray-only (hidden)
    /// launch. Accepts the canonical <c>--hidden</c> plus common aliases so
    /// hand-written shortcuts keep working. Case-insensitive, never throws.
    /// </summary>
    public static bool IsHiddenLaunchArg(string? arg) =>
        arg is not null && arg.Trim().ToLowerInvariant()
            is "--hidden" or "/hidden" or "-hidden"
            or "--tray" or "/tray"
            or "--minimized" or "/minimized" or "--background";

    /// <summary>
    /// Launch visibility decision: an interactive double-click must always
    /// produce something visible, so the window shows unless the user opted
    /// into a hidden start (StartHidden setting) or the launch itself asked
    /// for the tray (autostart shortcut passes <see cref="HiddenLaunchArg"/>).
    /// Pure and unit-testable — App only executes the result.
    /// </summary>
    public static bool ShouldShowWindowAtLaunch(bool startHidden, string[]? args)
    {
        if (startHidden)
        {
            return false;
        }

        if (args is not null)
        {
            foreach (var arg in args)
            {
                if (IsHiddenLaunchArg(arg))
                {
                    return false;
                }
            }
        }

        return true;
    }

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
