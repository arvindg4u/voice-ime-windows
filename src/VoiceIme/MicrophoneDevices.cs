using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace VoiceIme;

/// <summary>
/// NAudio WaveIn capture-device enumeration for the Sound group. Audio-stack
/// failures (no devices, headless CI) yield an empty list — never throws, so
/// the microphone dropdown always falls back to "System default".
/// </summary>
public static class MicrophoneDevices
{
    public const string SystemDefaultLabel = "System default";

    public static IReadOnlyList<string> ListNames()
    {
        var names = new List<string>();
        try
        {
            for (var i = 0; i < WaveIn.DeviceCount; i++)
            {
                try
                {
                    names.Add(WaveIn.GetCapabilities(i).ProductName);
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
    /// Maps the stored selection (the persisted product name, or "" for the
    /// system default) to its NAudio device index. Returns null when the
    /// selection is blank/unset or the device is no longer present — the caller
    /// then leaves the recorder on its default device rather than failing the
    /// capture. Pure over the supplied name list, so it unit-tests without an
    /// audio stack.
    /// </summary>
    public static int? ResolveDeviceNumber(string? storedName, IReadOnlyList<string>? deviceNames = null)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return null;
        }

        var names = deviceNames ?? ListNames();
        var wanted = storedName.Trim();
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], wanted, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }
}
