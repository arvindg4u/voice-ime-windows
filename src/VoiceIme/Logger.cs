using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceIme;

/// <summary>Severity filter. Runtime level via VOICEIME_LOG_LEVEL (default Info).</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Lightweight logger (Task 9): no new packages — writes to
/// <see cref="System.Diagnostics.Trace"/> plus a rotating file at
/// %AppData%/VoiceIme/logs/voiceime.log (500 KB rotation, 1 spare).
/// Secret values and transcript bodies are NEVER logged — only their lengths
/// and key fingerprints. Per-stage latency lines use
/// <see cref="LogLatency"/>; in-flight dictation timings are opt-in Debug.
/// </summary>
public static class Logger
{
    public const long MaxLogBytes = 500L * 1024;
    public const string LogFileName = "voiceime.log";

    private static readonly object Gate = new();
    private static Func<string, string>? _filePathOverride;

    /// <summary>
    /// Test hook: maps the log file name to a temp path (see
    /// <see cref="SettingsStore.SettingsPathOverride"/>). Never set in
    /// production; tests must reset it to null in a finally block.
    /// </summary>
    internal static Func<string, string>? FilePathOverride
    {
        get => _filePathOverride;
        set => _filePathOverride = value;
    }

    /// <summary>Active floor: Debug &lt; Info &lt; Warning &lt; Error.</summary>
    public static LogLevel Level => ParseLevel(Environment.GetEnvironmentVariable("VOICEIME_LOG_LEVEL"));

    public static string LogPath
    {
        get
        {
            if (_filePathOverride is not null)
            {
                return _filePathOverride(LogFileName);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceIme", "logs", LogFileName);
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, RedactFree(message));

    public static void Info(string message) => Write(LogLevel.Info, RedactFree(message));

    public static void Warning(string message) => Write(LogLevel.Warning, RedactFree(message));

    public static void Error(string message) => Write(LogLevel.Error, RedactFree(message));

    /// <summary>
    /// Emits a per-stage latency line (hotkey→capture, capture→response,
    /// response→paste). Records only the duration — no audio or transcript.
    /// </summary>
    public static void LogLatency(string stage, TimeSpan elapsed) =>
        Write(LogLevel.Info, $"latency stage={stage} elapsed_ms={(long)elapsed.TotalMilliseconds}");

    /// <summary>
    /// Logs a transcript event by length only — the body never reaches the log.
    /// </summary>
    public static void LogTranscriptReceived(int charCount) =>
        Write(LogLevel.Info, $"transcript received chars={charCount}");

    /// <summary>
    /// Logs key usage by slot index and a short fingerprint, never the key.
    /// </summary>
    public static void LogKeyUsed(int slotIndex, IReadOnlyList<string>? keys) =>
        Write(LogLevel.Info, $"transcribe key slot={slotIndex} fingerprint={Fingerprint(keys, slotIndex)}");

    /// <summary>
    /// First 4 chars of the SHA-256 hex of the key at the slot; "none" when
    /// missing. A short prefix is enough to tell keys apart in support logs
    /// without exposing usable key material.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<string>? keys, int slotIndex)
    {
        if (keys is null || slotIndex < 0 || slotIndex >= keys.Count)
        {
            return "none";
        }

        var key = keys[slotIndex];
        if (string.IsNullOrEmpty(key))
        {
            return "none";
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..4].ToLowerInvariant();
    }

    internal static LogLevel ParseLevel(string? value)
    {
        if (Enum.TryParse<LogLevel>(value?.Trim(), ignoreCase: true, out var level))
        {
            return level;
        }

        return LogLevel.Info;
    }

    private static void Write(LogLevel level, string message)
    {
        if (level < Level)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Trace.WriteLine("[VoiceIme] " + line);

        lock (Gate)
        {
            try
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the app (disk full, AV lock, …).
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var spare = path + ".1";
            File.Delete(spare);
            File.Move(path, spare);
        }
        catch
        {
            // Best effort — the append below still tries the original path.
        }
    }

    /// <summary>
    /// Last-resort scrub for direct message strings: strips anything that
    /// looks like a Gemini API key (AIza…). Callers should still prefer the
    /// length/fingerprint helpers over logging values at all.
    /// </summary>
    private static string RedactFree(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        return ApiKeyPattern.Replace(message, Notifier.RedactedPlaceholder);
    }

    private static readonly Regex ApiKeyPattern =
        new(@"AIza[0-9A-Za-z\-_]{10,}", RegexOptions.Compiled);
}
