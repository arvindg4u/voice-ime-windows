using System;
using System.Windows;
using System.Windows.Controls;

namespace VoiceIme.Views;

/// <summary>
/// Task 6 Sound group atoms (partial of <see cref="GeneralSettingsView"/>):
/// capture-channel picker, output-device picker, display-gain volume slider,
/// and the audio-feedback toggle. Every write validates-then-commits through
/// <see cref="SettingsStore"/> and raises <c>Saved</c> on success (same seam
/// as the microphone row). Volume is display gain for the overlay level meter
/// only — never capture gain (see <see cref="SoundFeedback"/>).
/// </summary>
public partial class GeneralSettingsView
{
    /// <summary>Test seam: selected channel label.</summary>
    internal string? SelectedChannelLabel => ChannelBox.SelectedItem as string;

    /// <summary>Test seam: channel dropdown item count.</summary>
    internal int ChannelItemCount => ChannelBox.Items.Count;

    /// <summary>Test seam: selected output item.</summary>
    internal string? SelectedOutput => OutputBox.SelectedItem as string;

    /// <summary>Test seam: output dropdown item count.</summary>
    internal int OutputItemCount => OutputBox.Items.Count;

    /// <summary>Test seam: volume 0–100.</summary>
    internal int VolumePercent => (int)Math.Round(VolumeSlider.Value);

    /// <summary>Test seam: volume percent label.</summary>
    internal string VolumeLabelText => VolumeValue.Text;

    /// <summary>Test seam: feedback toggle state.</summary>
    internal bool? FeedbackChecked => FeedbackCheck.IsChecked;

    /// <summary>
    /// Percent label for a display-gain value. Pure — unit-testable on any OS.
    /// </summary>
    internal static string FormatVolume(int volume) =>
        $"{SettingsStore.ClampVolume(volume)}%";

    private void RefreshSoundGroup()
    {
        ChannelBox.SelectionChanged -= ChannelBox_SelectionChanged;
        VolumeSlider.ValueChanged -= VolumeSlider_ValueChanged;
        try
        {
            ChannelBox.Items.Clear();
            ChannelBox.Items.Add(AudioChannels.LabelFor(AudioChannels.Mono));
            ChannelBox.Items.Add(AudioChannels.LabelFor(AudioChannels.Stereo));
            ChannelBox.Items.Add(AudioChannels.LabelFor(AudioChannels.Average));
            ChannelBox.SelectedItem = AudioChannels.LabelFor(_settings.Channel);

            VolumeSlider.Value = _settings.Volume;
            VolumeValue.Text = FormatVolume(_settings.Volume);
        }
        finally
        {
            ChannelBox.SelectionChanged += ChannelBox_SelectionChanged;
            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        }

        RefreshOutputList();
        FeedbackCheck.IsChecked = _settings.AudioFeedback;
    }

    private void ChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyChannel(ChannelBox.SelectedItem as string);

    /// <summary>
    /// Test seam (internal): persists a channel label; unknown labels coerce
    /// to mono via <see cref="AudioChannels.ChannelForLabel"/>.
    /// </summary>
    internal void ApplyChannel(string? label)
    {
        _settings.Channel = AudioChannels.ChannelForLabel(label);
        TrySaveSettings(out _);
        // Detach while re-syncing: setting SelectedItem to a different value
        // re-fires SelectionChanged → ApplyChannel → a second save. Same
        // detach pattern as RefreshSoundGroup/RefreshOutputList.
        ChannelBox.SelectionChanged -= ChannelBox_SelectionChanged;
        try
        {
            ChannelBox.SelectedItem = AudioChannels.LabelFor(_settings.Channel);
        }
        finally
        {
            ChannelBox.SelectionChanged += ChannelBox_SelectionChanged;
        }
    }

    private void RefreshOutputButton_Click(object sender, RoutedEventArgs e) => RefreshOutputList();

    private void RefreshOutputList()
    {
        OutputBox.SelectionChanged -= OutputBox_SelectionChanged;
        try
        {
            OutputBox.Items.Clear();
            OutputBox.Items.Add(OutputDevices.SystemDefaultLabel);
            foreach (var name in _listOutputDevices())
            {
                OutputBox.Items.Add(name);
            }

            var stored = _settings.OutputDevice;
            if (string.IsNullOrEmpty(stored))
            {
                OutputBox.SelectedItem = OutputDevices.SystemDefaultLabel;
            }
            else if (OutputBox.Items.Contains(stored))
            {
                OutputBox.SelectedItem = stored;
            }
            else
            {
                // Same unplugged-device pattern as the microphone picker: keep
                // the stored value visible so the display matches persistence.
                var missing = stored + UnavailableSuffix;
                OutputBox.Items.Add(missing);
                OutputBox.SelectedItem = missing;
            }
        }
        finally
        {
            OutputBox.SelectionChanged += OutputBox_SelectionChanged;
        }
    }

    private void OutputBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyOutputDevice(OutputBox.SelectedItem as string);

    /// <summary>
    /// Test seam (internal): persists an output selection; unavailable
    /// entries are ignored so the stored value survives re-plug.
    /// </summary>
    internal void ApplyOutputDevice(string? selected)
    {
        if (string.IsNullOrEmpty(selected)
            || selected.EndsWith(UnavailableSuffix, StringComparison.Ordinal))
        {
            return;
        }

        _settings.OutputDevice = selected == OutputDevices.SystemDefaultLabel ? "" : selected;
        TrySaveSettings(out _);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        ApplyVolume((int)Math.Round(e.NewValue));
    }

    /// <summary>
    /// Test seam (internal): clamps to 0–100, persists, and refreshes the
    /// percent label. Display gain only — capture is untouched.
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
    /// Test seam (internal): persists the post-paste tone toggle (tone
    /// playback itself lives in <see cref="SoundFeedback"/>).
    /// </summary>
    internal void ApplyFeedback(bool enabled)
    {
        _settings.AudioFeedback = enabled;
        TrySaveSettings(out _);
        // Sync the toggle: siblings ApplyChannel/ApplyVolume re-sync their
        // controls, and FeedbackChecked reads IsChecked directly.
        FeedbackCheck.IsChecked = enabled;
    }
}
