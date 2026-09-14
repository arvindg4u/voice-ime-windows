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

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceIme", "settings.json");

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
            apiKeysProtected = Convert.ToBase64String(cipher),
        };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(root, JsonOptions));
    }

    private static string Get(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? fallback
            : fallback;
}
