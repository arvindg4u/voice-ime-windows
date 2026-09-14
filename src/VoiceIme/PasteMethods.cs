using System;

namespace VoiceIme;

/// <summary>
/// Paste methods for transcript delivery (Handy paste_method port, Windows
/// subset): the chord sent after the transcript lands on the clipboard.
/// Persisted as strings in <see cref="SettingsStore"/>. Unknown values
/// coerce to <see cref="CtrlV"/> — never throw on a bad file.
/// </summary>
public static class PasteMethods
{
    public const string CtrlV = "ctrlV";
    public const string ShiftInsert = "shiftInsert";
    public const string CtrlShiftV = "ctrlShiftV";

    public static bool IsValid(string? value) =>
        string.Equals(value, CtrlV, StringComparison.Ordinal)
        || string.Equals(value, ShiftInsert, StringComparison.Ordinal)
        || string.Equals(value, CtrlShiftV, StringComparison.Ordinal);

    public static string LabelFor(string? method) => method switch
    {
        ShiftInsert => "Clipboard (Shift+Insert)",
        CtrlShiftV => "Clipboard (Ctrl+Shift+V)",
        _ => "Clipboard (Ctrl+V)",
    };

    public static string MethodForLabel(string? label) => label switch
    {
        "Clipboard (Shift+Insert)" => ShiftInsert,
        "Clipboard (Ctrl+Shift+V)" => CtrlShiftV,
        _ => CtrlV,
    };
}
