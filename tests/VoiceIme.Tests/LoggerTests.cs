using System;
using System.IO;
using Xunit;

namespace VoiceIme.Tests;

/// <summary>
/// Task 9: log redaction, rotation, and level parsing. Log file writes redirect
/// to temp files via Logger.FilePathOverride — the real %AppData% log is never
/// touched. VOICEIME_LOG_LEVEL is process-global, so level-setting tests are
/// serialized on <see cref="EnvLock"/> (xUnit runs classes in parallel).
/// </summary>
[Collection("LoggerEnv")]
public sealed class LoggerTests
{
    private readonly object _envLock = EnvLock.Instance;

    [Fact]
    public void ParseLevel_DefaultsToInfo_OnMissingOrUnknown()
    {
        Assert.Equal(LogLevel.Info, Logger.ParseLevel(null));
        Assert.Equal(LogLevel.Info, Logger.ParseLevel(""));
        Assert.Equal(LogLevel.Info, Logger.ParseLevel("verbose"));
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData(" Warning ", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    public void ParseLevel_AcceptsKnownLevels_CaseInsensitive(string value, LogLevel expected)
    {
        Assert.Equal(expected, Logger.ParseLevel(value));
    }

    [Fact]
    public void Info_DoesNotWriteApiKeyValue()
    {
        lock (_envLock)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Logger.FilePathOverride = _ => Path.Combine(dir, Logger.LogFileName);
            var previous = Environment.GetEnvironmentVariable("VOICEIME_LOG_LEVEL");
            Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", "debug");
            try
            {
                const string key = "AIzaSyFakeKeyForTestOnly0123456789";
                Logger.Info($"request failed with key {key} — try again");
                Logger.LogTranscriptReceived(1234);

                var text = File.ReadAllText(Path.Combine(dir, Logger.LogFileName));
                Assert.DoesNotContain(key, text);
                Assert.Contains(Notifier.RedactedPlaceholder, text);
                Assert.DoesNotContain("transcript body", text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", previous);
                Logger.FilePathOverride = null;
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Fingerprint_DerivesFromKey_WithoutExposingIt()
    {
        const string key = "AIzaSyFakeKeyForTestOnly0123456789";
        var fingerprint = Logger.Fingerprint([key], 0);

        Assert.NotEqual(key, fingerprint);
        Assert.DoesNotContain(key, fingerprint);
        Assert.Equal(Logger.Fingerprint([key], 0), fingerprint);
        Assert.NotEqual(Logger.Fingerprint(["AIzaSyAnotherFakeKeyForTest999999"], 0), fingerprint);
        Assert.Equal("none", Logger.Fingerprint(null, 0));
        Assert.Equal("none", Logger.Fingerprint([], 0));
        Assert.Equal("none", Logger.Fingerprint([key], 3));
    }

    [Fact]
    public void LogKeyUsed_And_LogTranscriptReceived_OnlyWriteLengthsAndFingerprints()
    {
        lock (_envLock)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Logger.FilePathOverride = _ => Path.Combine(dir, Logger.LogFileName);
            var previous = Environment.GetEnvironmentVariable("VOICEIME_LOG_LEVEL");
            Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", "debug");
            try
            {
                const string key = "AIzaSyFakeKeyForTestOnly0123456789";
                Logger.LogKeyUsed(0, [key]);
                Logger.LogTranscriptReceived(42);

                var text = File.ReadAllText(Path.Combine(dir, Logger.LogFileName));
                Assert.DoesNotContain(key, text);
                Assert.Contains("slot=0", text);
                Assert.Contains("chars=42", text);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", previous);
                Logger.FilePathOverride = null;
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void LogLatency_WritesStageAndElapsedMs()
    {
        lock (_envLock)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Logger.FilePathOverride = _ => Path.Combine(dir, Logger.LogFileName);
            var previous = Environment.GetEnvironmentVariable("VOICEIME_LOG_LEVEL");
            Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", "debug");
            try
            {
                Logger.LogLatency("hotkey_to_capture", TimeSpan.FromMilliseconds(123));

                var text = File.ReadAllText(Path.Combine(dir, Logger.LogFileName));
                Assert.Contains("hotkey_to_capture", text);
                Assert.Contains("123", text);
            }
            finally
            {
                Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", previous);
                Logger.FilePathOverride = null;
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Error_IsSuppressed_BelowConfiguredLevel()
    {
        lock (_envLock)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Logger.FilePathOverride = _ => Path.Combine(dir, Logger.LogFileName);
            var previous = Environment.GetEnvironmentVariable("VOICEIME_LOG_LEVEL");
            Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", "error");
            try
            {
                Logger.Debug("debug-marker");
                Logger.Info("info-marker");

                Assert.False(File.Exists(Path.Combine(dir, Logger.LogFileName)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", previous);
                Logger.FilePathOverride = null;
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void Write_BeyondHalfMegabyte_RotatesKeepingOneSpare()
    {
        lock (_envLock)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var path = Path.Combine(dir, Logger.LogFileName);
            Logger.FilePathOverride = _ => path;
            var previous = Environment.GetEnvironmentVariable("VOICEIME_LOG_LEVEL");
            Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", "debug");
            try
            {
                // Pre-fill just under the cap, then one write pushes a rotation
                // on the NEXT write — after that, current + .1 spare hold data.
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, new string('x', (int)Logger.MaxLogBytes - 10));
                Logger.Info("first-after-cap");
                Logger.Info("second-after-rotation");

                Assert.True(File.Exists(path));
                Assert.True(File.Exists(path + ".1"));
                Assert.DoesNotContain("second-after-rotation", File.ReadAllText(path + ".1"));
                Assert.Contains("second-after-rotation", File.ReadAllText(path));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VOICEIME_LOG_LEVEL", previous);
                Logger.FilePathOverride = null;
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void SingleInstance_SecondAcquire_Fails_And_Notifies()
    {
        using var first = new SingleInstance(
            (_, _) => (new System.Threading.Mutex(), true),
            (_, _, _) => { },
            showMessage: 99);
        using var second = new SingleInstance(
            (_, _) => (new System.Threading.Mutex(), false),
            (_, _, _) => { },
            showMessage: 99);

        Assert.True(first.Acquire("test-name"));
        Assert.True(first.IsFirstInstance);

        var notified = 0u;
        using var notifier = new SingleInstance(
            (_, _) => (new System.Threading.Mutex(), false),
            (message, _, _) => notified = message,
            showMessage: 99);
        Assert.False(notifier.Acquire("test-name"));
        Assert.False(notifier.IsFirstInstance);
        notifier.NotifyRunningInstance();

        Assert.False(second.Acquire("test-name"));
        Assert.Equal(99u, notified);
    }

    [Fact]
    public void SingleInstance_Acquire_NeverThrows_OnUnexpectedFailure()
    {
        using var gate = new SingleInstance(
            (_, _) => throw new InvalidOperationException("synthetic"),
            (_, _, _) => throw new InvalidOperationException("synthetic"),
            showMessage: 0);

        // Fail-open: an unproven mutex failure runs alone rather than exiting
        // with no instance at all — a duplicate tray icon beats no app.
        Assert.True(gate.Acquire("test-name"));
        Assert.True(gate.IsFirstInstance);

        // Message id 0 (registration failed) and a throwing broadcast both
        // degrade silently — the running window just stays hidden.
        gate.NotifyRunningInstance();
    }
}
