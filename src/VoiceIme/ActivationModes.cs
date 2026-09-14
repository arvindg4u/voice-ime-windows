using System;

namespace VoiceIme;

/// <summary>
/// Activation modes for the dictation hotkey (Handy shortcut_activation
/// port). Persisted as strings in <see cref="SettingsStore"/>. v1 behavior
/// is toggle for every mode — the Task 8 coordinator makes the other modes
/// live; this screen only persists the choice.
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
