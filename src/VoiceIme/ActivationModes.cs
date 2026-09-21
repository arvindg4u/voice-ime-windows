using System;

namespace VoiceIme;

/// <summary>
/// Activation modes for the dictation hotkey (Handy shortcut_activation
/// port). Persisted as strings in <see cref="SettingsStore"/>. v1 behavior
/// is implemented by <see cref="DictationCoordinator"/> for hold-or-toggle,
/// push-to-talk, and toggle sessions.
/// </summary>
public static class ActivationModes
{
    public const string HoldOrToggle = "hold_or_toggle";
    public const string PushToTalk = "push_to_talk";
    public const string Toggle = "toggle";

    public static bool IsValid(string? value) =>
        string.Equals(value, HoldOrToggle, StringComparison.Ordinal)
        || string.Equals(value, PushToTalk, StringComparison.Ordinal)
        || string.Equals(value, Toggle, StringComparison.Ordinal);

    public static string LabelFor(string? mode) => mode switch
    {
        HoldOrToggle => "Hold or toggle",
        PushToTalk => "Push to talk",
        Toggle => "Toggle",
        _ => "Toggle",
    };

    public static string ModeForLabel(string? label) => label switch
    {
        "Hold or toggle" => HoldOrToggle,
        "Push to talk" => PushToTalk,
        _ => Toggle,
    };
}
