using System.Collections.Generic;
using NAudio.Wave;

namespace VoiceIme;

/// <summary>
/// NAudio WaveOut render-device enumeration for the Sound group. Mirrors
/// <see cref="MicrophoneDevices"/>: audio-stack failures (no devices, headless
/// CI) yield an empty list — never throws, so the output dropdown always falls
/// back to "System default". The selection is persisted only; playback still
/// uses the default device.
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
}
