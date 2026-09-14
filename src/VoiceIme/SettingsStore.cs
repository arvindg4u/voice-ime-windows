using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceIme;

/// <summary>
/// Persists settings to %AppData%/VoiceIme/settings.json.
/// API keys are DPAPI-encrypted (CurrentUser scope) at rest — the Windows
/// equivalent of Android's EncryptedSharedPreferences. Never logged.
///
/// Versioning (Task 9): additive <c>schemaVersion</c> (current 1). Files that
/// predate versioning are treated as v0 — every newer field simply falls back
/// to its default. <see cref="Load"/> never throws and never wipes the file:
/// a corrupt file starts from defaults and salvages whatever fields survive
/// in the raw text; bad enum strings coerce to defaults with an in-memory
/// warning (<see cref="LoadWarnings"/>). <see cref="Migrate"/> stamps the
/// current version and is idempotent — safe to call on every load and save.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>Current on-disk schema. v0 = any file without schemaVersion.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public List<string> ApiKeys { get; set; } = [];
    public string Model { get; set; } = "gemini-2.5-flash";
    public string CustomPrompt { get; set; } = "";
    public int KeyCursor { get; set; }

    // Task 3 (Handy UI port): General settings screen fields. Defaults apply
    // to old settings files that predate these fields. All writes
    // validate-then-commit: invalid values coerce to defaults with a warning,
    // never throw.
    public string Hotkey { get; set; } = HotkeyChord.DefaultChord;
    public string ActivationMode { get; set; } = ActivationModes.Toggle;

    /// <summary>Device name, or "" for the system default.</summary>
    public string Microphone { get; set; } = "";
    public bool MuteWhileRecording { get; set; }

    /// <summary>
    /// Non-fatal problems from the last <see cref="Load"/> (corrupt file,
    /// bad enum string, undecryptable keys, unknown newer version). Empty on
    /// a clean load — including a clean v0 → v1 migration.
    /// </summary>
    public List<string> LoadWarnings { get; } = [];

    /// <summary>True when <see cref="Load"/> had to salvage, coerce, or fall back.</summary>
    public bool HasLoadWarnings => LoadWarnings.Count > 0;

    public static string SettingsPath =>
        SettingsPathOverride?.Invoke()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceIme", "settings.json");

    /// <summary>
    /// Test hook: when set, <see cref="SettingsPath"/> returns this delegate's
    /// value instead of the %AppData% path, so tests redirect Load/Save to temp
    /// files and never touch the developer's real settings. Production never
    /// sets it; tests must reset it to null in a finally block.
    /// </summary>
    internal static Func<string>? SettingsPathOverride { get; set; }

    public static SettingsStore Load()
    {
        var store = new SettingsStore();
        string? raw;
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
            {
                return store;
            }

            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            store.Warn($"Could not read settings file ({ex.GetType().Name}) — using defaults.");
            return store;
        }

        // Whole-parse failure (corrupt JSON, or valid JSON of the wrong shape)
        // falls back to per-field salvage over defaults. The file is NEVER
        // wiped or rewritten here — it stays on disk for manual recovery.
        try
        {
            using var doc = JsonDocument.Parse(raw);
            store.ApplyDocument(doc.RootElement);
        }
        catch (Exception ex)
        {
            store.Warn($"Settings file is not usable ({ex.GetType().Name}) — salvaging fields over defaults.");
            store.SalvageFromRaw(raw);
        }

        store.Migrate();
        return store;
    }

    /// <summary>
    /// Brings any loaded store up to <see cref="CurrentSchemaVersion"/>.
    /// v0 → v1: files without schemaVersion already pick up Task 3's field
    /// defaults through the per-field readers, so stamping the version is the
    /// whole migration. Future versions add <c>if (SchemaVersion &lt; N)</c>
    /// steps above the stamp. Idempotent — re-running changes nothing once
    /// <see cref="SchemaVersion"/> equals <see cref="CurrentSchemaVersion"/>.
    /// </summary>
    public void Migrate()
    {
        Hotkey = CoerceHotkey(Hotkey, Warn);
        ActivationMode = CoerceActivationMode(ActivationMode, Warn);
        Microphone ??= "";
        if (KeyCursor < 0)
        {
            KeyCursor = 0;
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    public void Save()
    {
        Hotkey = CoerceHotkey(Hotkey);
        ActivationMode = CoerceActivationMode(ActivationMode);
        Microphone ??= "";
        SchemaVersion = CurrentSchemaVersion;
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var joined = string.Join('\n', ApiKeys);
        var plain = Encoding.UTF8.GetBytes(joined);
        var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        var root = new
        {
            schemaVersion = SchemaVersion,
            baseUrl = BaseUrl,
            model = Model,
            customPrompt = CustomPrompt,
            keyCursor = KeyCursor,
            hotkey = Hotkey,
            activationMode = ActivationMode,
            microphone = Microphone,
            muteWhileRecording = MuteWhileRecording,
            apiKeysProtected = Convert.ToBase64String(cipher),
        };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(root, JsonOptions));
    }

    /// <summary>
    /// Invalid chords fall back to the default (warns via trace; never
    /// throws — Handy settings pattern). Empty/whitespace is invalid too.
    /// </summary>
    internal static string CoerceHotkey(string? value) => CoerceHotkey(value, null);

    internal static string CoerceHotkey(string? value, Action<string>? onWarning)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && HotkeyChord.TryParse(value, out _, out _))
        {
            return value.Trim();
        }

        var message = $"Invalid hotkey {value ?? "<null>"} — using default {HotkeyChord.DefaultChord}.";
        if (onWarning is not null)
        {
            onWarning(message);
        }
        else
        {
            System.Diagnostics.Trace.WriteLine("[Settings] " + message);
        }

        return HotkeyChord.DefaultChord;
    }

    internal static string CoerceActivationMode(string? value) => CoerceActivationMode(value, null);

    internal static string CoerceActivationMode(string? value, Action<string>? onWarning)
    {
        if (ActivationModes.IsValid(value))
        {
            return value!;
        }

        var message = $"Invalid activationMode {value ?? "<null>"} — using default {ActivationModes.Toggle}.";
        if (onWarning is not null)
        {
            onWarning(message);
        }
        else
        {
            System.Diagnostics.Trace.WriteLine("[Settings] " + message);
        }

        return ActivationModes.Toggle;
    }

    private void ApplyDocument(JsonElement root)
    {
        // Missing schemaVersion means a pre-versioning file: treat as v0.
        SchemaVersion = GetInt(root, "schemaVersion", 0);
        if (SchemaVersion > CurrentSchemaVersion)
        {
            Warn($"Unknown schemaVersion {SchemaVersion} (current {CurrentSchemaVersion}) — keeping values as-is.");
        }

        BaseUrl = GetString(root, "baseUrl", BaseUrl);
        Model = GetString(root, "model", Model);
        CustomPrompt = GetString(root, "customPrompt", CustomPrompt);
        KeyCursor = GetInt(root, "keyCursor", KeyCursor);
        Hotkey = CoerceHotkey(GetString(root, "hotkey", HotkeyChord.DefaultChord), Warn);
        ActivationMode = CoerceActivationMode(GetString(root, "activationMode", ActivationModes.Toggle), Warn);
        Microphone = GetString(root, "microphone", Microphone);
        MuteWhileRecording = GetBool(root, "muteWhileRecording", MuteWhileRecording);

        // Keys decrypt in isolation: a bad blob must not discard the fields
        // already read above.
        if (root.TryGetProperty("apiKeysProtected", out var keys))
        {
            try
            {
                var cipher = Convert.FromBase64String(keys.GetString() ?? "");
                var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                ApiKeys = Encoding.UTF8.GetString(plain)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                CryptographicOperations.ZeroMemory(plain);
            }
            catch (Exception ex)
            {
                Warn($"Could not decrypt API keys ({ex.GetType().Name}) — starting with an empty key list.");
                ApiKeys = [];
            }
        }
    }

    /// <summary>
    /// Best-effort recovery from a file that failed whole-parse: extracts
    /// surviving <c>"name": value</c> pairs with regex over the raw text.
    /// Unmatched fields keep their (default) values; a bad enum still coerces
    /// to its default with a warning. Keys are deliberately NOT salvaged —
    /// they need an intact base64+DPAPI blob, and the untouched file stays on
    /// disk for manual recovery.
    /// </summary>
    private void SalvageFromRaw(string raw)
    {
        var baseUrl = SalvageString(raw, "baseUrl");
        if (baseUrl is not null)
        {
            BaseUrl = baseUrl;
        }

        var model = SalvageString(raw, "model");
        if (model is not null)
        {
            Model = model;
        }

        var prompt = SalvageString(raw, "customPrompt");
        if (prompt is not null)
        {
            CustomPrompt = prompt;
        }

        var hotkey = SalvageString(raw, "hotkey");
        if (hotkey is not null)
        {
            Hotkey = CoerceHotkey(hotkey, Warn);
        }

        var mode = SalvageString(raw, "activationMode");
        if (mode is not null)
        {
            ActivationMode = CoerceActivationMode(mode, Warn);
        }

        var microphone = SalvageString(raw, "microphone");
        if (microphone is not null)
        {
            Microphone = microphone;
        }

        var cursor = SalvageInt(raw, "keyCursor");
        if (cursor.HasValue)
        {
            KeyCursor = cursor.Value;
        }

        var mute = SalvageBool(raw, "muteWhileRecording");
        if (mute.HasValue)
        {
            MuteWhileRecording = mute.Value;
        }

        var version = SalvageInt(raw, "schemaVersion");
        if (version.HasValue)
        {
            SchemaVersion = version.Value;
        }
    }

    private static string? SalvageString(string raw, string name)
    {
        // Ordinary interpolated strings (not raw): the patterns end in a
        // quote, which raw triple-quote delimiters cannot express safely.
        var match = Regex.Match(
            raw,
            $"\"{Regex.Escape(name)}\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"",
            RegexOptions.CultureInvariant);
        return match.Success ? UnescapeJsonString(match.Groups["v"].Value) : null;
    }

    private static int? SalvageInt(string raw, string name)
    {
        var match = Regex.Match(
            raw,
            $"\"{Regex.Escape(name)}\"\\s*:\\s*(?<v>-?\\d+|\"(?:[^\"\\\\]|\\\\.)*\")",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var token = match.Groups["v"].Value;
        if (token.StartsWith('"'))
        {
            var inner = UnescapeJsonString(token[1..^1]);
            return inner is not null && int.TryParse(inner, out var quoted) ? quoted : null;
        }

        return int.TryParse(token, out var number) ? number : null;
    }

    private static bool? SalvageBool(string raw, string name)
    {
        var match = Regex.Match(
            raw,
            $"\"{Regex.Escape(name)}\"\\s*:\\s*(?<v>true|false|-?\\d+|\"(?:[^\"\\\\]|\\\\.)*\")",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var token = match.Groups["v"].Value;
        if (token.StartsWith('"'))
        {
            token = UnescapeJsonString(token[1..^1]) ?? "";
        }

        return token.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => int.TryParse(token, out var number) ? number != 0 : null,
        };
    }

    private static string? UnescapeJsonString(string quotedInner)
    {
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + quotedInner + "\"");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lenient string reader: accepts strings as-is, renders legacy bare
    /// numbers/bools as text, falls back on null, and warns on objects/arrays.
    /// </summary>
    private string GetString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? fallback,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            JsonValueKind.Null => fallback,
            _ => WarnAndFallback(name, fallback),
        };
    }

    /// <summary>
    /// Lenient int reader: accepts numbers plus legacy numeric strings ("3").
    /// </summary>
    private int GetInt(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        if (element.ValueKind != JsonValueKind.Null)
        {
            Warn($"Unexpected shape for \"{name}\" — using default {fallback}.");
        }

        return fallback;
    }

    /// <summary>
    /// Lenient bool reader: accepts booleans plus legacy "true"/"false"/1/0.
    /// </summary>
    private bool GetBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                var text = (element.GetString() ?? "").Trim().ToLowerInvariant();
                if (text is "true" or "1")
                {
                    return true;
                }

                if (text is "false" or "0")
                {
                    return false;
                }

                break;
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var number))
                {
                    return number != 0;
                }

                break;
        }

        if (element.ValueKind != JsonValueKind.Null)
        {
            Warn($"Unexpected shape for \"{name}\" — using default {fallback}.");
        }

        return fallback;
    }

    private string WarnAndFallback(string name, string fallback)
    {
        Warn($"Unexpected shape for \"{name}\" — using defaults for it.");
        return fallback;
    }

    private void Warn(string message)
    {
        LoadWarnings.Add(message);
        System.Diagnostics.Trace.WriteLine("[Settings] " + message);
    }
}
