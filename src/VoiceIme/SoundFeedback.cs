using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

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
    /// <summary>
    /// Scales a perceptual 0..1 level by the persisted display gain (0 mutes
    /// the meter, 100 is full scale). Pure — unit-testable on any OS.
    /// </summary>
    public static float ApplyDisplayGain(float level, int volume) =>
        Math.Clamp(level, 0f, 1f) * (SettingsStore.ClampVolume(volume) / 100f);

    /// <summary>
    /// Plays the 440 Hz tone locally after a successful paste when feedback
    /// is enabled. Background thread (same PlaySync pattern as the speaker
    /// test): the stream/player stay alive for the whole playback and the UI
    /// thread never blocks. No-op when disabled; failures only warn.
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
                using var stream = new MemoryStream(AudioRecorder.TestToneWav());
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch (Exception ex)
            {
                Logger.Warning($"post-paste tone failed: {ex.GetType().Name}");
            }
        });
    }
}
