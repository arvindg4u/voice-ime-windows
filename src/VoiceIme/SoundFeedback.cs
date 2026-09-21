using System;
using System.IO;
using System.Media;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace VoiceIme;

/// <summary>
/// Task 6 sound effects (Handy AudioFeedback port): applies the persisted
/// display gain to overlay levels and plays the 440 Hz confirmation tone
/// after a successful paste when <see cref="SettingsStore.AudioFeedback"/> is
/// on. Display gain only — capture gain is never touched. Tone bytes come
/// from <see cref="AudioRecorder.TestToneWav"/> and are never persisted.
/// </summary>
public static class SoundFeedback
{
    // A 1-second test tone should finish well before this. A bounded wait
    // prevents a wedged named WaveOut driver from hanging a settings task.
    private const int NamedDevicePlaybackTimeoutMs = 5000;

    /// <summary>
    /// Scales a perceptual 0..1 level by the persisted display gain (0 mutes
    /// the meter, 100 is full scale). Pure — unit-testable on any OS.
    /// </summary>
    public static float ApplyDisplayGain(float level, int volume) =>
        Math.Clamp(level, 0f, 1f) * (SettingsStore.ClampVolume(volume) / 100f);

    /// <summary>
    /// Plays the 440 Hz tone locally after a successful paste when feedback
    /// is enabled. The stream/player stay alive for the whole playback and
    /// the UI thread never blocks. No-op when disabled; failures only warn.
    /// </summary>
    public static void PlayPostPasteTone(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AudioFeedback)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PlayTone(settings);
            }
            catch (Exception ex)
            {
                Logger.Warning($"post-paste tone failed: {ex.GetType().Name}");
            }
        });
    }

    /// <summary>
    /// Plays the settings test tone using the selected render device. An empty
    /// output selection deliberately keeps SoundPlayer's system-default path;
    /// a named selection uses NAudio's matching WaveOut device.
    /// </summary>
    public static void PlayTestTone(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PlayTone(settings);
    }

    private static void PlayTone(SettingsStore settings)
    {
        var tone = AudioRecorder.TestToneWav();
        try
        {
            using var stream = new MemoryStream(tone);
            var deviceNumber = OutputDevices.FindIndex(settings.OutputDevice);
            if (deviceNumber < 0)
            {
                // Empty or unplugged selection: preserve the OS default behavior.
                using var player = new SoundPlayer(stream);
                player.PlaySync();
                return;
            }

            using var reader = new WaveFileReader(stream);
            using var output = new WaveOutEvent
            {
                DeviceNumber = deviceNumber,
            };
            output.Init(reader);
            output.Play();
            var started = Stopwatch.GetTimestamp();
            while (output.PlaybackState == PlaybackState.Playing)
            {
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= NamedDevicePlaybackTimeoutMs)
                {
                    output.Stop();
                    break;
                }

                Thread.Sleep(20);
            }
        }
        finally
        {
            Array.Clear(tone, 0, tone.Length);
        }
    }
}
