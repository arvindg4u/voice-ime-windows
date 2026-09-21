using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace VoiceIme;

/// <summary>
/// NAudio WaveOut render-device enumeration for the Sound group. Mirrors
/// <see cref="MicrophoneDevices"/>: audio-stack failures (no devices, headless
/// CI) yield an empty list — never throws, so the output dropdown always falls
/// back to "System default". A matching selection is used for test-tone
/// playback; an unavailable selection falls back to the system default.
/// </summary>
public static class OutputDevices
{
    public const string SystemDefaultLabel = "System default";

    public static IReadOnlyList<string> ListNames()
    {
        var names = new List<string>();
        try
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
            {
                try
                {
                    names.Add(WaveOut.GetCapabilities(i).ProductName);
                }
                catch
                {
                    // Skip one unreadable device, keep the rest.
                }
            }
        }
        catch
        {
            // No audio stack (headless CI) — caller shows System default only.
        }

        return names;
    }

    /// <summary>
    /// Resolves a persisted product name to its current WaveOut index, or -1
    /// when the stored device is empty/unavailable (caller uses system default).
    /// </summary>
    public static int FindIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        var selected = name.Trim();
        try
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
            {
                try
                {
                    if (string.Equals(
                        WaveOut.GetCapabilities(i).ProductName,
                        selected,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
                catch
                {
                    // Keep looking if one device is unreadable.
                }
            }
        }
        catch
        {
            // Fall back to the system/default output.
        }

        return -1;
    }

    /// <summary>
    /// Matches the short WaveOut product name used by the tone player with a
    /// CoreAudio friendly name used by endpoint mute. WinMM limits
    /// <see cref="WaveOutCapabilities.ProductName"/> to 32 characters, so an
    /// exact match is not always possible even for the same physical endpoint.
    /// Prefix matching is deliberately limited to the longer persisted name to
    /// avoid treating a generic short label as a unique device.
    /// </summary>
    internal static bool NamesMatch(string? waveOutName, string? coreAudioName)
    {
        if (string.IsNullOrWhiteSpace(waveOutName)
            || string.IsNullOrWhiteSpace(coreAudioName))
        {
            return false;
        }

        var wave = waveOutName.Trim();
        var core = coreAudioName.Trim();
        if (string.Equals(wave, core, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return wave.Length >= 16
            && (core.StartsWith(wave, StringComparison.OrdinalIgnoreCase)
                || wave.StartsWith(core, StringComparison.OrdinalIgnoreCase));
    }
}
