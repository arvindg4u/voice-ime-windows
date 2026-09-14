using System.Windows;

namespace VoiceIme;

/// <summary>
/// Base URL · API keys · model · custom prompt, persisted via SettingsStore.
/// The Test button sends the 440 Hz tone through the full pipeline so the user
/// can verify keys/model/prompt before dictating.
/// </summary>
public partial class SettingsWindow : Window
{
    private SettingsStore _settings = SettingsStore.Load();

    public SettingsWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        _settings = SettingsStore.Load();
        BaseUrlBox.Text = _settings.BaseUrl;
        KeysBox.Text = string.Join('\n', _settings.ApiKeys);
        ModelBox.Text = _settings.Model;
        PromptBox.Text = _settings.CustomPrompt;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text.Trim();
        if (prompt.Length > 2000)
        {
            StatusText.Text = "Custom prompt must be 2000 chars or fewer.";
            return;
        }
        _settings.BaseUrl = BaseUrlBox.Text.Trim();
        _settings.ApiKeys = SplitKeys(KeysBox.Text);
        _settings.Model = ModelBox.Text.Trim();
        _settings.CustomPrompt = prompt;
        _settings.Save();
        StatusText.Text = "Saved ✓";
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        StatusText.Text = "Sending test tone…";
        try
        {
            using var llm = new LlmClient();
            var (transcript, _) = await llm.TranscribeAsync(
                AudioRecorder.TestToneWav(),
                SplitKeys(KeysBox.Text),
                BaseUrlBox.Text.Trim(),
                ModelBox.Text.Trim(),
                startIndex: 0,
                customPrompt: PromptBox.Text.Trim());
            StatusText.Text = $"Test OK — transcript: {transcript}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Test failed: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static List<string> SplitKeys(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
