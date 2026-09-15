using System;

namespace VoiceIme;

/// <summary>
/// Overlay detail modes (Handy show_overlay port): "none" hides the pill,
/// "minimal" hides the waveform bars, "full" is unchanged. Persisted as
/// strings in <see cref="SettingsStore"/>. Task 1 shipped this as a bool —
/// legacy bool values coerce losslessly (true→full, false→none) with no
/// schema bump; unknown values coerce to <see cref="Full"/> with a warning.
/// </summary>
public static class OverlayModes
{
    public const string None = "none";
    public const string Minimal = "minimal";
    public const string Full = "full";

    public static bool IsValid(string? value) =>
        string.Equals(value, None, StringComparison.Ordinal)
        || string.Equals(value, Minimal, StringComparison.Ordinal)
        || string.Equals(value, Full, StringComparison.Ordinal);

    public static string LabelFor(string? mode) => mode switch
    {
        None => "None",
        Minimal => "Minimal",
        _ => "Full",
    };

    public static string ModeForLabel(string? label) => label switch
    {
        "None" => None,
        "Minimal" => Minimal,
        _ => Full,
    };

    /// <summary>
    /// True for legacy Task 1 bool text ("true"/"false", any case) — rendered
    /// back from JSON bools by the lenient settings reader.
    /// </summary>
    public static bool IsLegacyBool(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bad mode strings fall back to full (warns via the same
    /// coerce-don't-throw contract as paste method). Legacy bools map
    /// silently — they are a known prior representation, not bad data.
    /// </summary>
    public static string Coerce(string? value) => Coerce(value, null);

    public static string Coerce(string? value, Action<string>? onWarning)
    {
        if (IsValid(value))
        {
            return value!;
        }

        if (string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return Full;
        }

        if (string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        var message = $"Invalid showOverlay {value ?? "<null>"} — using default {Full}.";
        if (onWarning is not null)
        {
            onWarning(message);
        }
        else
        {
            System.Diagnostics.Trace.WriteLine("[Settings] " + message);
        }

        return Full;
    }

    /// <summary>Whether the pill may raise at all under this mode.</summary>
    public static bool ShouldShowPill(string? mode) =>
        !string.Equals(Coerce(mode), None, StringComparison.Ordinal);

    /// <summary>Whether the recording waveform bars show under this mode.</summary>
    public static bool ShouldShowWaveform(string? mode) =>
        string.Equals(Coerce(mode), Full, StringComparison.Ordinal);
}
