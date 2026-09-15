using System;

namespace VoiceIme;

/// <summary>
/// Capture-channel choice (Handy selected_channel port, Windows subset):
/// mono, stereo, or mix-down average. Persisted as strings in
/// <see cref="SettingsStore"/>. Unknown values coerce to <see cref="Mono"/> —
/// never throw on a bad file. Capture still opens the default device format;
/// this screen only persists the choice.
/// </summary>
public static class AudioChannels
{
    public const string Mono = "mono";
    public const string Stereo = "stereo";
    public const string Average = "average";

    public static bool IsValid(string? value) =>
        string.Equals(value, Mono, StringComparison.Ordinal)
        || string.Equals(value, Stereo, StringComparison.Ordinal)
        || string.Equals(value, Average, StringComparison.Ordinal);

    public static string LabelFor(string? channel) => channel switch
    {
        Stereo => "Stereo",
        Average => "Mix-down (average)",
        _ => "Mono",
    };

    public static string ChannelForLabel(string? label) => label switch
    {
        "Stereo" => Stereo,
        "Mix-down (average)" => Average,
        _ => Mono,
    };
}
