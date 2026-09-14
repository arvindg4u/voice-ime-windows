using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceIme;

/// <summary>
/// Persists settings to %AppData%/VoiceIme/settings.json.
/// API keys are DPAPI-encrypted (CurrentUser scope) at rest — the Windows
/// equivalent of Android's EncryptedSharedPreferences. Never logged.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public List<string> ApiKeys { get; set; } = [];
    public string Model { get; set; } = "gemini-2.5-flash";
    public string CustomPrompt { get; set; } = "";
    public int KeyCursor { get; set; }

    // Task 3 (Handy UI port): General settings screen fields. Defaults apply
    // to old settings files that predate these fields (no schemaVersion bump —
    // Task 9 owns versioning). All writes validate-then-commit: invalid values
    // coerce to defaults with a warning, never throw.
    public string Hotkey { get; set; } = HotkeyChord.DefaultChord;
    public string ActivationMode { get; set; } = ActivationModes.Toggle;

    /// <summary>Device name, or "" for the system default.</summary>
    public string Microphone { get; set; } = "";
    public bool MuteWhileRecording { get; set; }

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
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new SettingsStore();
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var store = new SettingsStore
            {
                BaseUrl = Get(root, "baseUrl", "https://generativelanguage.googleapis.com/v1beta"),
                Model = Get(root, "model", "gemini-2.5-flash"),
                CustomPrompt = Get(root, "customPrompt", ""),
                KeyCursor = root.TryGetProperty("keyCursor", out var c) && c.TryGetInt32(out var ci) ? ci : 0,
                Hotkey = CoerceHotkey(Get(root, "hotkey", HotkeyChord.DefaultChord)),
                ActivationMode = CoerceActivationMode(Get(root, "activationMode", ActivationModes.Toggle)),
                Microphone = Get(root, "microphone", ""),
                MuteWhileRecording = root.TryGetProperty("muteWhileRecording", out var m)
                    && m.ValueKind == JsonValueKind.True,
            };
            if (root.TryGetProperty("apiKeysProtected", out var p))
            {
                var cipher = Convert.FromBase64String(p.GetString() ?? "");
                var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                store.ApiKeys = System.Text.Encoding.UTF8.GetString(plain)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                CryptographicOperations.ZeroMemory(plain);
            }
            return store;
        }
        catch
        {
            return new SettingsStore();
        }
    }

    public void Save()
    {
        Hotkey = CoerceHotkey(Hotkey);
        ActivationMode = CoerceActivationMode(ActivationMode);
        Microphone ??= "";
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var joined = string.Join('\n', ApiKeys);
        var plain = System.Text.Encoding.UTF8.GetBytes(joined);
        var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        var root = new
        {
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
    internal static string CoerceHotkey(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && HotkeyChord.TryParse(value, out _, out _))
        {
            return value.Trim();
        }

        System.Diagnostics.Trace.WriteLine($"[Settings] Invalid hotkey {value ?? "<null>"} — using default {HotkeyChord.DefaultChord}.");
        return HotkeyChord.DefaultChord;
    }

    internal static string CoerceActivationMode(string? value)
    {
        if (ActivationModes.IsValid(value))
        {
            return value!;
        }

        System.Diagnostics.Trace.WriteLine($"[Settings] Invalid activationMode {value ?? "<null>"} — using default {ActivationModes.Toggle}.");
        return ActivationModes.Toggle;
    }

    private static string Get(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? fallback
            : fallback;
}
