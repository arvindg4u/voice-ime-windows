using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VoiceIme.Views;

/// <summary>
/// Audio group atoms for <see cref="GeneralSettingsView"/>: the microphone
/// picker, the live mic-level meter, the display-gain slider and the
/// audio-feedback toggle.
///
/// <para>This partial used to also carry a mute toggle, a capture-channel
/// picker and an output-device picker. None of the three was honoured anywhere
/// in the capture or playback path — they persisted a value and changed
/// nothing, and each carried the help text "Stored for now". A control that
/// does nothing is worse than a missing one: it reads as a working feature and
/// erodes trust in the ones that do work. They are gone; the <c>MuteWhileRecording</c>,
/// <c>Channel</c> and <c>OutputDevice</c> fields remain on
/// <see cref="SettingsStore"/> so existing settings files still load, and can
/// come back with UI when the capture path actually consumes them.</para>
/// </summary>
public partial class GeneralSettingsView
{
    /// <summary>Test seam: display-gain volume 0–100.</summary>
    internal int VolumePercent => (int)Math.Round(VolumeSlider.Value);

    /// <summary>Test seam: volume percent label.</summary>
    internal string VolumeLabelText => VolumeValue.Text;

    /// <summary>Test seam: feedback toggle state.</summary>
    internal bool? FeedbackChecked => FeedbackCheck.IsChecked;

    /// <summary>Test seam: whether a mic test is currently running.</summary>
    internal bool IsMicTestRunning => _micTestRunning;

    /// <summary>
    /// Percent label for a display-gain value. Pure — unit-testable on any OS.
    /// </summary>
    internal static string FormatVolume(int volume) =>
        $"{SettingsStore.ClampVolume(volume)}%";

    private bool _micTestRunning;

    private void RefreshSoundGroup()
    {
        VolumeSlider.ValueChanged -= VolumeSlider_ValueChanged;
        try
        {
            VolumeSlider.Value = _settings.Volume;
            VolumeValue.Text = FormatVolume(_settings.Volume);
        }
        finally
        {
            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        }

        FeedbackCheck.IsChecked = _settings.AudioFeedback;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ApplyVolume((int)Math.Round(e.NewValue));

    /// <summary>
    /// Test seam (internal): persists the display-gain value, clamps it, and
    /// re-syncs the slider and its label. Never throws.
    /// </summary>
    internal void ApplyVolume(int value)
    {
        var clamped = SettingsStore.ClampVolume(value);
        _settings.Volume = clamped;
        if (!VolumeSlider.Value.Equals((double)clamped))
        {
            VolumeSlider.Value = clamped;
        }

        VolumeValue.Text = FormatVolume(clamped);
        TrySaveSettings(out _);
    }

    private void FeedbackCheck_Click(object sender, RoutedEventArgs e) =>
        ApplyFeedback(FeedbackCheck.IsChecked == true);

    /// <summary>
    /// Test seam (internal): persists the post-insert tone toggle (tone
    /// playback itself lives in <see cref="SoundFeedback"/>).
    /// </summary>
    internal void ApplyFeedback(bool enabled)
    {
        _settings.AudioFeedback = enabled;
        TrySaveSettings(out _);
        // Sync the toggle: siblings ApplyVolume re-syncs its control, and
        // FeedbackChecked reads IsChecked directly.
        FeedbackCheck.IsChecked = enabled;
    }

    // ------------------------------------------------------------------
    // Microphone test
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts the live level meter. A moving meter is the only way a user can
    /// tell that the microphone is actually hearing them — otherwise a muted,
    /// blocked or wrong device is indistinguishable from a failed
    /// transcription until after they have finished speaking.
    /// </summary>
    private void MicTestButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMicTest();

    /// <summary>
    /// Test seam (internal): flips the meter on or off. The level source is
    /// the same <c>LevelChanged</c> feed the overlay uses, so this never opens
    /// a second capture stream.
    /// </summary>
    internal void ToggleMicTest()
    {
        if (_micTestRunning)
        {
            StopMicTest();
        }
        else
        {
            StartMicTest();
        }
    }

    private void StartMicTest()
    {
        _micTestRunning = true;
        MicTestButton.Content = "Stop";
        MicTestHelp.Text = "Listening — speak normally. The bar should track your voice.";
        MicLevelBar.Value = 0;
    }

    private void StopMicTest()
    {
        _micTestRunning = false;
        MicTestButton.Content = "Test";
        MicTestHelp.Text =
            "Speak to check the level meter moves. If it stays flat, the mic is muted or blocked.";
        MicLevelBar.Value = 0;
    }

    /// <summary>
    /// Feeds a 0–1 input level into the meter. Called from the app's existing
    /// recorder level feed; a no-op while the test is not running so the meter
    /// stays still the rest of the time.
    /// </summary>
    internal void OnInputLevel(float level01)
    {
        if (!_micTestRunning)
        {
            return;
        }

        var scaled = SoundFeedback.ApplyDisplayGain(level01, _settings.Volume);
        MicLevelBar.Value = Math.Clamp(scaled * 100.0, 0, 100);
    }
}
