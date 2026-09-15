using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VoiceIme.Views;

/// <summary>
/// Task 7 prompt-library group (partial of <see cref="GeminiSettingsView"/>):
/// saved-prompt picker, name field, and Save (upsert by name) / Delete
/// buttons. The multi-line editor always shows the ACTIVE prompt's text —
/// every write validates-then-commits through <see cref="SettingsStore"/>
/// and surfaces failures inline, never throwing out of a handler.
/// </summary>
public partial class GeminiSettingsView
{
    /// <summary>Test seam: selected library name (null when nothing selected).</summary>
    internal string? SelectedPromptName => PromptSelector.SelectedItem as string;

    /// <summary>Test seam: prompt-name field text.</summary>
    internal string PromptName
    {
        get => PromptNameBox.Text;
        set => PromptNameBox.Text = value;
    }

    /// <summary>Test seam: library dropdown item count.</summary>
    internal int PromptCount => PromptSelector.Items.Count;

    /// <summary>Test seam: library names in dropdown order.</summary>
    internal List<string> PromptNames =>
        PromptSelector.Items.Cast<object>().Select(static item => item as string ?? "").ToList();

    /// <summary>
    /// Writes validated editor text into the active library entry, seeding a
    /// Default entry when the library is empty and the text is non-blank (so
    /// a plain Save behaves like the legacy field did). The legacy mirror
    /// always follows the editor text here — Save/Load re-derive it.
    /// </summary>
    private void CommitPromptText(string prompt)
    {
        var activeIndex = PromptLibrary.IndexOf(_settings.Prompts, _settings.ActivePrompt);
        if (activeIndex >= 0)
        {
            _settings.Prompts[activeIndex] = _settings.Prompts[activeIndex] with { Text = prompt };
        }
        else if (prompt.Length > 0 && _settings.Prompts.Count < PromptLibrary.MaxPrompts)
        {
            _settings.Prompts.Add(new PromptEntry(PromptLibrary.DefaultPromptName, prompt));
            _settings.ActivePrompt = PromptLibrary.DefaultPromptName;
        }

        _settings.CustomPrompt = prompt;
    }

    private void RefreshPromptList()
    {
        PromptSelector.SelectionChanged -= PromptSelector_SelectionChanged;
        try
        {
            PromptSelector.Items.Clear();
            foreach (var entry in _settings.Prompts)
            {
                PromptSelector.Items.Add(entry.Name);
            }

            PromptSelector.SelectedItem =
                PromptLibrary.IndexOf(_settings.Prompts, _settings.ActivePrompt) >= 0
                    ? _settings.ActivePrompt
                    : null;
            DeletePromptButton.IsEnabled =
                PromptLibrary.IndexOf(_settings.Prompts, PromptSelector.SelectedItem as string) >= 0;
        }
        finally
        {
            PromptSelector.SelectionChanged += PromptSelector_SelectionChanged;
        }
    }

    private void PromptSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectPrompt(PromptSelector.SelectedItem as string);

    /// <summary>
    /// Test seam (internal): switches the active prompt, committing the
    /// outgoing editor text into its entry first (validate-then-commit — an
    /// overlong edit blocks the switch and restores the picker). Unknown
    /// names are rejected with an inline error.
    /// </summary>
    internal bool SelectPrompt(string? name)
    {
        var target = (name ?? "").Trim();
        if (string.Equals(target, _settings.ActivePrompt, StringComparison.OrdinalIgnoreCase))
        {
            RefreshPromptList();
            return true;
        }

        var index = PromptLibrary.IndexOf(_settings.Prompts, target);
        if (index < 0)
        {
            StatusText.Text = $"Unknown prompt \"{target}\".";
            RefreshPromptList();
            return false;
        }

        var text = PromptBox.Text.Trim();
        if (PromptLibrary.ValidateText(text) is { } error)
        {
            StatusText.Text = error;
            RefreshPromptList();
            return false;
        }

        var current = PromptLibrary.IndexOf(_settings.Prompts, _settings.ActivePrompt);
        if (current >= 0)
        {
            _settings.Prompts[current] = _settings.Prompts[current] with { Text = text };
        }

        _settings.ActivePrompt = _settings.Prompts[index].Name;
        _settings.CustomPrompt = _settings.ActivePromptText;
        if (!PersistLibrary())
        {
            RefreshPromptList();
            return false;
        }

        PromptBox.Text = _settings.ActivePromptText;
        PromptNameBox.Text = _settings.ActivePrompt;
        StatusText.Text = "Saved ✓";
        RefreshPromptList();
        return true;
    }

    private void SavePromptButton_Click(object sender, RoutedEventArgs e) => SavePrompt();

    /// <summary>
    /// Test seam (internal): upserts the editor text under the name field
    /// (case-insensitive match updates in place, keeping canonical casing;
    /// otherwise appends and selects). Bounds reject with inline errors.
    /// </summary>
    internal bool SavePrompt()
    {
        var name = PromptNameBox.Text.Trim();
        if (PromptLibrary.ValidateName(name) is { } nameError)
        {
            StatusText.Text = nameError;
            return false;
        }

        var text = PromptBox.Text.Trim();
        if (PromptLibrary.ValidateText(text) is { } textError)
        {
            StatusText.Text = textError;
            return false;
        }

        var index = PromptLibrary.IndexOf(_settings.Prompts, name);
        if (index >= 0)
        {
            _settings.Prompts[index] = _settings.Prompts[index] with { Text = text };
            name = _settings.Prompts[index].Name;
        }
        else
        {
            if (_settings.Prompts.Count >= PromptLibrary.MaxPrompts)
            {
                StatusText.Text = $"Prompt library is full (max {PromptLibrary.MaxPrompts}).";
                return false;
            }

            _settings.Prompts.Add(new PromptEntry(name, text));
        }

        _settings.ActivePrompt = name;
        _settings.CustomPrompt = _settings.ActivePromptText;
        if (!PersistLibrary())
        {
            return false;
        }

        StatusText.Text = "Saved ✓";
        RefreshPromptList();
        return true;
    }

    private void DeletePromptButton_Click(object sender, RoutedEventArgs e) => DeletePrompt();

    /// <summary>
    /// Test seam (internal): deletes the selected entry. Deleting the active
    /// entry falls back to the first remaining one (blank editor when the
    /// library empties); deleting a non-active entry keeps the active text.
    /// </summary>
    internal bool DeletePrompt()
    {
        var index = PromptLibrary.IndexOf(_settings.Prompts, PromptSelector.SelectedItem as string);
        if (index < 0)
        {
            StatusText.Text = "Select a prompt to delete.";
            return false;
        }

        var wasActive = string.Equals(
            _settings.Prompts[index].Name, _settings.ActivePrompt, StringComparison.OrdinalIgnoreCase);
        _settings.Prompts.RemoveAt(index);
        if (wasActive)
        {
            _settings.ActivePrompt = _settings.Prompts.Count > 0 ? _settings.Prompts[0].Name : "";
        }

        _settings.CustomPrompt =
            _settings.Prompts.Count == 0 ? "" : _settings.ActivePromptText;
        if (!PersistLibrary())
        {
            return false;
        }

        PromptBox.Text = _settings.ActivePromptText;
        PromptNameBox.Text = _settings.ActivePrompt;
        StatusText.Text = "Saved ✓";
        RefreshPromptList();
        return true;
    }

    private bool PersistLibrary()
    {
        try
        {
            _saver(_settings);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
            return false;
        }

        return true;
    }
}
