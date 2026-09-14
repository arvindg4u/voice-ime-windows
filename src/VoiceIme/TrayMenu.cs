using System.Collections.Generic;

namespace VoiceIme;


/// <summary>Semantic action behind a tray menu row — App maps these to handlers.</summary>
public enum TrayMenuAction
{
    VersionHeader,
    Cancel,
    CopyLastTranscript,
    History,
    Settings,
    Quit,
    Separator,
}

/// <summary>
/// One tray menu row. Pure data (no WinForms) so the Handy menu order is
/// unit-testable on any OS; App translates rows into ToolStripItems.
/// </summary>
public sealed record TrayMenuItem(TrayMenuAction Action, string Label, bool Enabled)
{
    public bool IsSeparator => Action == TrayMenuAction.Separator;
}

/// <summary>
/// Handy tray order. Idle: version (disabled) | Copy Last Transcript |
/// History… | Settings… (Ctrl+,) | Quit. Busy (recording/uploading):
/// version | Cancel | Copy Last Transcript | History… | Settings… | Quit.
/// </summary>
public static class TrayMenu
{
    public const string SettingsLabel = "Settings… (Ctrl+,)";
    public const string CopyLabel = "Copy Last Transcript";
    public const string HistoryLabel = "History…";
    public const string CancelLabel = "Cancel";
    public const string QuitLabel = "Quit";

    public static string VersionLabel(string version) => $"Voice IME {version}";

    public static IReadOnlyList<TrayMenuItem> Items(bool busy, string version, bool hasTranscript)
    {
        var header = new TrayMenuItem(TrayMenuAction.VersionHeader, VersionLabel(version), Enabled: false);
        var copy = new TrayMenuItem(TrayMenuAction.CopyLastTranscript, CopyLabel, hasTranscript);
        var history = new TrayMenuItem(TrayMenuAction.History, HistoryLabel, Enabled: true);
        var settings = new TrayMenuItem(TrayMenuAction.Settings, SettingsLabel, Enabled: true);
        var quit = new TrayMenuItem(TrayMenuAction.Quit, QuitLabel, Enabled: true);
        var sep = new TrayMenuItem(TrayMenuAction.Separator, "-", Enabled: false);
        return busy
            ? [header, sep,
                new TrayMenuItem(TrayMenuAction.Cancel, CancelLabel, Enabled: true), sep,
                copy, sep, history, sep, settings, sep, quit]
            : [header, sep, copy, sep, history, sep, settings, sep, quit];
    }
}
