using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VoiceIme.Views;

/// <summary>
/// Gemini section screen (Handy post-processing API group port): Handy's
/// provider/model pickers are replaced by our fixed Gemini fields — stacked
/// Base URL, API keys (mono, one per line), and Model with reset-to-defaults
/// — plus the custom-prompt group (multi-line editor, char-count hint, tip).
/// The Test button sends the 440 Hz tone through <see cref="LlmClient"/> and
/// shows the result inline. Viewmodel-less code-behind bound to
/// <see cref="SettingsStore"/> — every write validates-then-commits and
/// surfaces failures inline, never throws out of an event handler.
/// Hosted by <see cref="MainWindow"/> via
/// RegisterSectionView(MainSection.Gemini, view).
/// </summary>
public partial class GeminiSettingsView : System.Windows.Controls.UserControl
{
    internal const int MaxPromptLength = 2000;

    private readonly SettingsStore _settings;
    private readonly Action<SettingsStore> _saver;

    /// <summary>
    /// The shared <see cref="SettingsStore"/> this view edits — the F1
    /// shared-instance wiring asserts the app's live store lands here.
    /// </summary>
    internal SettingsStore BoundSettings => _settings;
    private readonly Func<byte[]> _recordTone;
    private readonly Func<byte[], IReadOnlyList<string>, string, string, string, Task<(string Transcript, int UsedIndex)>> _transcribeAsync;

    public GeminiSettingsView()
        : this(SettingsStore.Load())
    {
    }

    internal GeminiSettingsView(
        SettingsStore settings,
        Action<SettingsStore>? saver = null,
        Func<byte[]>? recordTone = null,
        Func<byte[], IReadOnlyList<string>, string, string, string, Task<(string Transcript, int UsedIndex)>>? transcribeAsync = null)
    {
        _settings = settings;
        _saver = saver ?? (static s => s.Save());
        _recordTone = recordTone ?? (static () => AudioRecorder.TestToneWav());
        _transcribeAsync = transcribeAsync ?? DefaultTranscribeAsync;
        InitializeComponent();
        LoadFromSettings();
    }

    /// <summary>Test seam: Base URL field text.</summary>
    internal string BaseUrlText
    {
        get => BaseUrlBox.Text;
        set => BaseUrlBox.Text = value;
    }

    /// <summary>Test seam: API keys box text.</summary>
    internal string KeysText
    {
        get => KeysBox.Text;
        set => KeysBox.Text = value;
    }

    /// <summary>Test seam: model field text.</summary>
    internal string ModelText
    {
        get => ModelBox.Text;
        set => ModelBox.Text = value;
    }

    /// <summary>Test seam: custom prompt editor text (setting it refreshes the hint).</summary>
    internal string PromptText
    {
        get => PromptBox.Text;
        set => PromptBox.Text = value;
    }

    /// <summary>Test seam: inline status line.</summary>
    internal string StatusMessage => StatusText.Text;

    /// <summary>Test seam: char-count hint text.</summary>
    internal string PromptHintText => PromptHint.Text;

    /// <summary>Test seam: whether the prompt currently exceeds the limit.</summary>
    internal bool IsPromptOverLimit { get; private set; }

    /// <summary>
    /// Reloads the fields from the bound store (the store is the app's live
    /// instance under the F1 wiring, so in-UI saves are already visible to
    /// dictation — this only syncs the boxes when the fields can be stale,
    /// e.g. after Reset elsewhere). Safe to call before Show().
    /// </summary>
    internal void ReloadFromSettings() => LoadFromSettings();

    private void LoadFromSettings()
    {
        BaseUrlBox.Text = _settings.BaseUrl;
        KeysBox.Text = string.Join('\n', _settings.ApiKeys);
        ModelBox.Text = _settings.Model;
        PromptBox.Text = _settings.CustomPrompt;
        UpdatePromptHint();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        // Keys have no meaningful default and wiping them would lock the user
        // out — only Base URL, model, and prompt reset.
        var defaults = new SettingsStore();
        BaseUrlBox.Text = defaults.BaseUrl;
        ModelBox.Text = defaults.Model;
        PromptBox.Text = defaults.CustomPrompt;
        SaveNow();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveNow();

    /// <summary>
    /// Test seam (internal): validates then persists the fields; kept off the
    /// private event handler so tests can drive the validation path directly.
    /// </summary>
    internal bool SaveNow()
    {
        var prompt = PromptBox.Text.Trim();
        var error = ValidatePrompt(prompt);
        if (error is not null)
        {
            StatusText.Text = error;
            return false;
        }

        _settings.BaseUrl = BaseUrlBox.Text.Trim();
        _settings.ApiKeys = SplitKeys(KeysBox.Text);
        _settings.Model = ModelBox.Text.Trim();
        _settings.CustomPrompt = prompt;
        try
        {
            _saver(_settings);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            return false;
        }

        StatusText.Text = "Saved ✓";
        return true;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e) => await TestNowAsync();

    /// <summary>
    /// Test seam (internal): runs the tone through the transcribe pipeline;
    /// kept off the private event handler so tests can inject a fake
    /// pipeline and await the inline result directly.
    /// </summary>
    internal async Task TestNowAsync()
    {
        TestButton.IsEnabled = false;
        StatusText.Text = "Sending test tone…";
        try
        {
            var (transcript, _) = await _transcribeAsync(
                _recordTone(),
                SplitKeys(KeysBox.Text),
                BaseUrlBox.Text.Trim(),
                ModelBox.Text.Trim(),
                PromptBox.Text.Trim());
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

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePromptHint();

    private void UpdatePromptHint()
    {
        var length = PromptBox.Text.Length;
        PromptHint.Text = $"{length}/{MaxPromptLength} characters";
        IsPromptOverLimit = length > MaxPromptLength;
        PromptHint.SetResourceReference(
            TextBlock.ForegroundProperty,
            IsPromptOverLimit ? "HandyError" : "HandyMidGray");
    }

    /// <summary>
    /// Pure helper: null when the prompt fits, otherwise the inline message.
    /// Unit-testable on any OS — no WPF types involved.
    /// </summary>
    internal static string? ValidatePrompt(string prompt) =>
        prompt.Length > MaxPromptLength
            ? $"Custom prompt must be {MaxPromptLength} chars or fewer."
            : null;

    /// <summary>
    /// One key per line; blank lines and surrounding whitespace are dropped.
    /// Same semantics the legacy SettingsWindow used (and LlmClient re-trims).
    /// </summary>
    internal static List<string> SplitKeys(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// Default pipeline: the real <see cref="LlmClient"/> REST contract
    /// (tone bytes, round-robin from key 0, trimmed base URL/model/prompt).
    /// </summary>
    private static async Task<(string Transcript, int UsedIndex)> DefaultTranscribeAsync(
        byte[] wav,
        IReadOnlyList<string> keys,
        string baseUrl,
        string model,
        string prompt)
    {
        using var llm = new LlmClient();
        return await llm.TranscribeAsync(
            wav,
            keys,
            baseUrl,
            model,
            startIndex: 0,
            customPrompt: prompt);
    }
}
