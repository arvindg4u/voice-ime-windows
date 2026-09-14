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
}
