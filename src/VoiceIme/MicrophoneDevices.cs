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
    /// Resolves the persisted product name to its current WaveIn index.
    /// Device indices can change after unplug/replug. The WaveIn mapper index
    /// (-1) is the real system/default input; returning it for an empty or
    /// unavailable selection avoids silently choosing the first microphone.
    /// </summary>
    public static int FindIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        try
        {
            for (var i = 0; i < WaveIn.DeviceCount; i++)
            {
                try
                {
                    if (string.Equals(
                        WaveIn.GetCapabilities(i).ProductName,
                        name.Trim(),
                        StringComparison.Ordinal))
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
            // Fall back to WaveIn's mapper/default index.
        }

        return -1;
    }
}
