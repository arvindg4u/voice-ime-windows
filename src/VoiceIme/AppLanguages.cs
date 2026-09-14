using System;

namespace VoiceIme;

/// <summary>
/// App-language catalog for the About screen (Handy app_language port):
/// English-only in v1. The selection persists in
/// <see cref="SettingsStore"/> and the UI stays English — the view labels
/// this honestly. Unknown values coerce to <see cref="English"/>.
/// </summary>
public static class AppLanguages
{
    public const string English = "en";

    public static bool IsValid(string? value) =>
        string.Equals(value, English, StringComparison.Ordinal);

    public static string LabelFor(string? _) => "English";

    public static string LanguageForLabel(string? _) => English;
}
