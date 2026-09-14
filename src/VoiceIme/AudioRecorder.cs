using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace VoiceIme;

/// <summary>
/// Captures 16 kHz mono 16-bit PCM via WaveIn (format requested from the OS
/// mixer, so the WAV header always matches the bytes) and encodes to WAV.
/// Mirrors Android AudioRecorder: stop button + 5 min auto-stop, no VAD library.
/// Raises <see cref="LevelChanged"/> with a perceptual 0..1 level for the waveform.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const long MaxDurationMs = 300_000L;
    public const float SilencePeakThreshold = 0.008f;

    private WaveInEvent? _capture;
    private MemoryStream? _pcm;
    private DateTime _startUtc;
    private bool _disposed;

    public event Action<float>? LevelChanged;
    public event Action? AutoStopped;

    public bool IsRecording => _capture is not null;

    /// <summary>Pure helper: true when peak is below the silence threshold.</summary>
    public static bool IsSilent(float peak, float threshold = SilencePeakThreshold) =>
        peak < threshold;

    /// <summary>
    /// Perceptual level 0..1: raw RMS is near-flat for quiet signals, so this
    /// expands the low end to drive the visible level meter.
    /// </summary>
    public static float Magnitude(float rms) =>
        Math.Clamp(1.0f - MathF.Pow(0.1f, 24.0f * rms), 0f, 1f);

    /// <summary>1 s 440 Hz sine at 0.5 amplitude as WAV — for the settings Test button.</summary>
    public static byte[] TestToneWav(int sampleRate = SampleRate)
    {
        var frames = sampleRate;
        var pcm = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var sample = (int)(Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 0.5 * 32767);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return PcmToWav(pcm, sampleRate);
    }

    /// <summary>Wraps raw 16-bit mono PCM in a 44-byte RIFF header.</summary>
    public static byte[] PcmToWav(byte[] pcm, int sampleRate = SampleRate)
    {
        var totalLen = 44 + pcm.Length;
        var out_ = new byte[totalLen];
        WriteAscii(out_, 0, "RIFF");
        WriteIntLe(out_, 4, totalLen - 8);
        WriteAscii(out_, 8, "WAVE");
        WriteAscii(out_, 12, "fmt ");
        WriteIntLe(out_, 16, 16);
        WriteShortLe(out_, 20, 1); // PCM
        WriteShortLe(out_, 22, Channels);
        WriteIntLe(out_, 24, sampleRate);
        WriteIntLe(out_, 28, sampleRate * Channels * 2);
        WriteShortLe(out_, 32, (short)(Channels * 2));
        WriteShortLe(out_, 34, 16);
        WriteAscii(out_, 36, "data");
        WriteIntLe(out_, 40, pcm.Length);
        Buffer.BlockCopy(pcm, 0, out_, 44, pcm.Length);
        return out_;
    }

    public void Start()
    {
        if (_capture is not null) return;
        _pcm = new MemoryStream();
        _startUtc = DateTime.UtcNow;
        _capture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 100,
        };
        _capture.DataAvailable += (_, e) =>
        {
            _pcm.Write(e.Buffer, 0, e.BytesRecorded);
            var level = ComputePeak(e.Buffer, e.BytesRecorded);
            LevelChanged?.Invoke(Magnitude(level));
            if ((DateTime.UtcNow - _startUtc).TotalMilliseconds >= MaxDurationMs)
                AutoStopped?.Invoke();
        };
        _capture.StartRecording();
    }

    /// <summary>Stops capture and returns the full WAV. Runs off the UI thread.</summary>
    public async Task<byte[]> StopAsync(CancellationToken ct = default)
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null || _pcm is null) return PcmToWav(Array.Empty<byte>());
        await Task.Run(() =>
        {
            try { capture.StopRecording(); } catch { /* already stopped */ }
            capture.Dispose();
        }, ct);
        var pcm = _pcm.ToArray();
        _pcm.Dispose();
        _pcm = null;
        var wav = PcmToWav(pcm);
        Array.Clear(pcm, 0, pcm.Length);
        return wav;
    }

    public void Cancel()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        try { capture?.StopRecording(); } catch { /* already stopped */ }
        capture?.Dispose();
        _pcm?.Dispose();
        _pcm = null;
    }

    private static float ComputePeak(byte[] buffer, int bytes)
    {
        var peak = 0;
        for (var i = 0; i + 1 < bytes; i += 2)
        {
            var s = (short)(buffer[i] | (buffer[i + 1] << 8));
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak / 32768f;
    }

    private static void WriteAscii(byte[] b, int o, string s)
    {
        for (var i = 0; i < s.Length; i++) b[o + i] = (byte)s[i];
    }

    private static void WriteIntLe(byte[] b, int o, int v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    private static void WriteShortLe(byte[] b, int o, short v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }
}
