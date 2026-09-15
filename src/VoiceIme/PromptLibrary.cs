using System;
using System.Collections.Generic;
using System.Text.Json;

namespace VoiceIme;

/// <summary>
/// A single named prompt in the library: the display key plus the
/// system-instruction text sent with every transcription while active.
/// Immutable record — edits replace the entry, never mutate it.
/// </summary>
public sealed record PromptEntry(string Name, string Text);

/// <summary>
/// Task 7 (Handy parity gaps): prompt-library pure logic. All public members
/// are side-effect-free and WPF-free, so they unit-test on any OS. Bounds:
/// at most <see cref="MaxPrompts"/> entries, names ≤
/// <see cref="MaxPromptNameLength"/> chars, texts ≤
/// <see cref="MaxPromptTextLength"/> chars (the 2000-char convention the
/// Gemini screen already enforced on the single custom prompt).
///
/// Migration rule (documented per brief): the legacy <c>customPrompt</c>
/// field seeds the library ONLY when the stored list is empty after coercion
/// and the legacy text is non-blank — one entry named <c>Default</c>,
/// selected active, holding the trimmed legacy text verbatim (even if
/// overlong, so the backend bytes stay identical until the user edits).
/// When the list is already non-empty the legacy field is ignored — the
/// library owns the text, and the store mirrors the active text back into
/// the legacy field on load/save so pre-library readers still see current
/// text. Re-running on a migrated store is a no-op (list non-empty), hence
/// idempotent. Name matching is <see cref="StringComparison.OrdinalIgnoreCase"/>
/// everywhere (lookup, upsert, delete, dedupe).
/// </summary>
public static class PromptLibrary
{
    /// <summary>Maximum entries kept; the 21st upsert is rejected.</summary>
    public const int MaxPrompts = 20;

    /// <summary>Maximum prompt-name length.</summary>
    public const int MaxPromptNameLength = 60;

    /// <summary>Maximum prompt-text length (the existing 2000 convention).</summary>
    public const int MaxPromptTextLength = 2000;

    /// <summary>Name of the entry seeded from a legacy custom prompt.</summary>
    public const string DefaultPromptName = "Default";

    /// <summary>
    /// Null when the name is usable, otherwise the inline message. Pure.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Prompt name is required.";
        }

        if (trimmed.Length > MaxPromptNameLength)
        {
            return $"Prompt name must be {MaxPromptNameLength} characters or fewer.";
        }

        return null;
    }

    /// <summary>
    /// Null when the text fits, otherwise the inline message. Same wording
    /// the Gemini screen already used for the single custom prompt. Pure.
    /// </summary>
    public static string? ValidateText(string text) =>
        text.Length > MaxPromptTextLength
            ? $"Custom prompt must be {MaxPromptTextLength} chars or fewer."
            : null;

    /// <summary>
    /// Index of the entry named <paramref name="name"/> (case-insensitive),
    /// or -1. Pure.
    /// </summary>
    public static int IndexOf(IReadOnlyList<PromptEntry> prompts, string? name)
    {
        var trimmed = (name ?? "").Trim();
        for (var i = 0; i < prompts.Count; i++)
        {
            if (string.Equals(prompts[i].Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Coerce-don't-throw list cleanup: drops null/blank-named entries and
    /// later duplicates (keeps the first, case-insensitive), truncates
    /// overlong names/texts, caps at <see cref="MaxPrompts"/> (keeps the
    /// first 20). Returns a new list; the input is never mutated. Pure
    /// apart from the optional warning callback.
    /// </summary>
    public static List<PromptEntry> CoercePrompts(
        IEnumerable<PromptEntry?>? entries, Action<string>? onWarning = null)
    {
        var clean = new List<PromptEntry>();
        if (entries is null)
        {
            return clean;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new CoerceState();
        foreach (var raw in entries)
        {
            var entry = TryCoerceEntry(raw, seen, state, onWarning);
            if (entry is not null && clean.Count < MaxPrompts)
            {
                clean.Add(entry);
            }
            else if (entry is not null && !state.CapWarned)
            {
                onWarning?.Invoke($"Prompt library capped at {MaxPrompts} entries — extras dropped.");
                state.CapWarned = true;
            }
        }

        return clean;
    }

    /// <summary>
    /// Coerces one raw entry (null when dropped). Tracks the cap warning in
    /// <paramref name="state"/> so the caller stays under the size budget.
    /// </summary>
    private static PromptEntry? TryCoerceEntry(
        PromptEntry? raw,
        HashSet<string> seen,
        CoerceState state,
        Action<string>? onWarning)
    {
        if (raw is null)
        {
            return null;
        }

        var name = (raw.Name ?? "").Trim();
        if (name.Length == 0)
        {
            onWarning?.Invoke("Prompt entry with a blank name was dropped.");
            return null;
        }

        if (name.Length > MaxPromptNameLength)
        {
            name = name[..MaxPromptNameLength];
            onWarning?.Invoke($"Prompt name truncated to {MaxPromptNameLength} characters.");
        }

        if (!seen.Add(name))
        {
            onWarning?.Invoke($"Duplicate prompt \"{name}\" was dropped (keeping the first).");
            return null;
        }

        var text = raw.Text ?? "";
        if (text.Length > MaxPromptTextLength)
        {
            text = text[..MaxPromptTextLength];
            onWarning?.Invoke($"Prompt \"{name}\" truncated to {MaxPromptTextLength} characters.");
        }

        return new PromptEntry(name, text);
    }

    /// <summary>Mutable per-run state for <see cref="CoercePrompts"/>.</summary>
    private sealed class CoerceState
    {
        public bool CapWarned { get; set; }
    }

    /// <summary>
    /// Coerce-don't-throw active-name cleanup: a name matching an entry
    /// (case-insensitive) is kept with canonical casing; anything else falls
    /// back to the first entry, or "" when the library is empty. Pure apart
    /// from the optional warning callback.
    /// </summary>
    public static string CoerceActivePrompt(
        string? active, IReadOnlyList<PromptEntry> prompts, Action<string>? onWarning = null)
    {
        var index = IndexOf(prompts, active);
        if (index >= 0)
        {
            return prompts[index].Name;
        }

        if (prompts.Count == 0)
        {
            return "";
        }

        onWarning?.Invoke($"Unknown activePrompt \"{active}\" — using \"{prompts[0].Name}\".");
        return prompts[0].Name;
    }

    /// <summary>
    /// Legacy migration (see the class doc for the rule). Returns new
    /// values; inputs are never mutated. Pure.
    /// </summary>
    public static (List<PromptEntry> Prompts, string ActivePrompt) MigrateCustomPrompt(
        IReadOnlyList<PromptEntry> prompts, string activePrompt, string? customPrompt)
    {
        var current = new List<PromptEntry>(prompts);
        if (current.Count > 0)
        {
            return (current, activePrompt);
        }

        var legacy = (customPrompt ?? "").Trim();
        if (legacy.Length == 0)
        {
            return (current, activePrompt);
        }

        current.Add(new PromptEntry(DefaultPromptName, legacy));
        return (current, DefaultPromptName);
    }

    /// <summary>
    /// The text the backend must send: the active entry's text, or
    /// <paramref name="fallback"/> (the legacy field) when nothing matches.
    /// Pure.
    /// </summary>
    public static string GetActiveText(
        IReadOnlyList<PromptEntry> prompts, string? activePrompt, string fallback)
    {
        var index = IndexOf(prompts, activePrompt);
        return index >= 0 ? prompts[index].Text : fallback;
    }

    /// <summary>
    /// Tolerant <c>prompts</c> array reader for <see cref="SettingsStore"/>:
    /// missing/null/non-array yields empty (missing is the pre-Task-7
    /// default, not a warning); malformed entries are skipped with a
    /// warning. Length/name cleanup happens later in
    /// <see cref="CoercePrompts"/> — reading stays lossless here.
    /// </summary>
    internal static List<PromptEntry> ReadPrompts(JsonElement root, Action<string>? onWarning)
    {
        var list = new List<PromptEntry>();
        if (!root.TryGetProperty("prompts", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return list;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            onWarning?.Invoke("Unexpected shape for \"prompts\" — using defaults for it.");
            return list;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (TryReadPrompt(item, out var entry, onWarning) && entry is not null)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    private static bool TryReadPrompt(
        JsonElement item, out PromptEntry? entry, Action<string>? onWarning)
    {
        entry = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            onWarning?.Invoke("Unexpected shape for a \"prompts\" entry — skipping it.");
            return false;
        }

        if (!item.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            onWarning?.Invoke("Prompt entry without a name was skipped.");
            return false;
        }

        var text = "";
        if (item.TryGetProperty("text", out var textElement))
        {
            switch (textElement.ValueKind)
            {
                case JsonValueKind.String:
                    text = textElement.GetString() ?? "";
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    text = textElement.GetRawText();
                    break;
                case JsonValueKind.Null:
                    break;
                default:
                    onWarning?.Invoke("Unexpected shape for a prompt \"text\" — skipping the entry.");
                    return false;
            }
        }

        entry = new PromptEntry(nameElement.GetString()!, text);
        return true;
    }
}
