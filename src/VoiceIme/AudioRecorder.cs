using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace VoiceIme;

/// <summary>
/// Captures 16 kHz 16-bit PCM via WaveIn (mono by default; the selected
/// channel mode can request stereo or an averaged mono mix-down). The format
/// is requested from the OS mixer, so the WAV header always matches the bytes.
/// Mirrors Android AudioRecorder: stop button + 5 min auto-stop, no VAD library.
/// Raises <see cref="LevelChanged"/> with a perceptual 0..1 level for the waveform.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const long MaxDurationMs = 300_000L;
    public const float SilencePeakThreshold = 0.008f;

    private readonly object _gate = new();
    private WaveInEvent? _capture;
    private MemoryStream? _pcm;
    private int _channels = Channels;
    private int _inputChannels = Channels;
    private bool _averageDownmix;
    private DateTime _startUtc;
    private bool _autoStopRaised;
    private bool _disposed;

    public event Action<float>? LevelChanged;
    public event Action? AutoStopped;

    /// <summary>
    /// Streaming hook: raw 16-bit PCM at 16 kHz per capture buffer, raised on
    /// the NAudio capture thread. Each subscriber receives its own defensive
    /// copy (NAudio reuses its buffer and event subscribers may run together).
    /// The subscriber owns that copy after the callback starts, including when
    /// its callback throws; the recorder clears only its dispatch copy. App
    /// requests mono for Gemini Live, whose wire format is fixed;
    /// REST/Interactions may honor a selected stereo capture.
    /// </summary>
    public event Action<byte[]>? PcmChunkAvailable;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _capture is not null;
            }
        }
    }

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
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var frames = sampleRate;
        var pcm = new byte[checked(frames * 2)];
        try
        {
            for (var i = 0; i < frames; i++)
            {
                var sample = (int)(Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 0.5 * 32767);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            return PcmToWav(pcm, sampleRate);
        }
        finally
        {
            Array.Clear(pcm, 0, pcm.Length);
        }
    }

    /// <summary>Wraps raw 16-bit mono PCM in a 44-byte RIFF header.</summary>
    public static byte[] PcmToWav(
        byte[] pcm, int sampleRate = SampleRate, int channels = Channels)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        checked
        {
            var totalLen = 44 + pcm.Length;
            var out_ = new byte[totalLen];
            WriteAscii(out_, 0, "RIFF");
            WriteIntLe(out_, 4, totalLen - 8);
            WriteAscii(out_, 8, "WAVE");
            WriteAscii(out_, 12, "fmt ");
            WriteIntLe(out_, 16, 16);
            WriteShortLe(out_, 20, 1); // PCM
            WriteShortLe(out_, 22, (short)channels);
            WriteIntLe(out_, 24, sampleRate);
            WriteIntLe(out_, 28, sampleRate * channels * 2);
            WriteShortLe(out_, 32, (short)(channels * 2));
            WriteShortLe(out_, 34, 16);
            WriteAscii(out_, 36, "data");
            WriteIntLe(out_, 40, pcm.Length);
            Buffer.BlockCopy(pcm, 0, out_, 44, pcm.Length);
            return out_;
        }
    }

    /// <summary>
    /// Starts capture. <paramref name="deviceNumber"/> is a WaveIn device
    /// index; -1 selects the WaveIn system mapper/default when no specific
    /// device was selected. <paramref name="channel"/> requests mono, stereo,
    /// or the safe mono mix-down mode. A failed start fully tears down the
    /// provisional capture so a later attempt is not permanently stuck in
    /// IsRecording.
    /// </summary>
    public void Start(int deviceNumber = -1, string? channel = AudioChannels.Mono)
    {
        var averageDownmix = channel == AudioChannels.Average;
        var outputChannels = channel == AudioChannels.Stereo ? 2 : 1;
        var inputChannels = outputChannels == 2 || averageDownmix ? 2 : 1;
        var capture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, inputChannels),
            BufferMilliseconds = 100,
            // NAudio's -1 WaveIn mapper is the Windows system/default input;
            // preserve it instead of silently selecting device zero.
            DeviceNumber = Math.Max(-1, deviceNumber),
        };
        EventHandler<WaveInEventArgs> handler = (_, e) => OnDataAvailable(capture, e);
        capture.DataAvailable += handler;

        lock (_gate)
        {
            if (_disposed)
            {
                capture.DataAvailable -= handler;
                capture.Dispose();
                throw new ObjectDisposedException(nameof(AudioRecorder));
            }

            if (_capture is not null)
            {
                capture.Dispose();
                return;
            }

            _pcm = new MemoryStream();
            _channels = outputChannels;
            _inputChannels = inputChannels;
            _averageDownmix = averageDownmix;
            _startUtc = DateTime.UtcNow;
            _autoStopRaised = false;
            _capture = capture;
            try
            {
                // Keep publication and StartRecording under the same lock. This
                // prevents Cancel/Stop from disposing the device halfway through
                // NAudio's startup sequence.
                capture.StartRecording();
            }
            catch
            {
                _capture = null;
                _channels = Channels;
                _inputChannels = Channels;
                _averageDownmix = false;
                var pcm = _pcm;
                _pcm = null;
                capture.DataAvailable -= handler;
                capture.Dispose();
                ClearAndDispose(pcm);
                throw;
            }
        }
    }

    /// <summary>Stops capture and returns the full WAV. Runs device teardown off the UI thread.</summary>
    public async Task<byte[]> StopAsync(CancellationToken ct = default)
    {
        WaveInEvent? capture;
        MemoryStream? pcm;
        int channels;
        lock (_gate)
        {
            capture = _capture;
            pcm = _pcm;
            channels = _channels;
            _capture = null;
            _pcm = null;
            _channels = Channels;
            _inputChannels = Channels;
            _averageDownmix = false;
        }

        if (capture is not null)
        {
            // Do not pass ct to Task.Run: cancellation must not skip the
            // device cleanup and leak a detached WaveIn handle. We observe the
            // caller token after teardown instead.
            await Task.Run(() => StopAndDispose(capture), CancellationToken.None)
                .ConfigureAwait(false);
        }

        byte[] raw = Array.Empty<byte>();
        try
        {
            ct.ThrowIfCancellationRequested();
            raw = pcm?.ToArray() ?? Array.Empty<byte>();
            ct.ThrowIfCancellationRequested();
            return PcmToWav(raw, SampleRate, channels);
        }
        finally
        {
            Array.Clear(raw, 0, raw.Length);
            ClearAndDispose(pcm);
        }
    }

    /// <summary>
    /// Cancels capture synchronously and releases both the device and the
    /// in-memory PCM buffer. Safe to call concurrently with StopAsync.
    /// </summary>
    public void Cancel()
    {
        WaveInEvent? capture;
        MemoryStream? pcm;
        lock (_gate)
        {
            capture = _capture;
            pcm = _pcm;
            _capture = null;
            _pcm = null;
            _channels = Channels;
            _inputChannels = Channels;
            _averageDownmix = false;
        }

        if (capture is not null)
        {
            StopAndDispose(capture);
        }

        ClearAndDispose(pcm);
    }

    private void OnDataAvailable(WaveInEvent capture, WaveInEventArgs e)
    {
        byte[] chunk;
        float level;
        bool autoStop;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(capture, _capture) || _pcm is null)
            {
                return;
            }

            var bytes = Math.Clamp(e.BytesRecorded, 0, e.Buffer.Length);
            var frameBytes = _inputChannels * 2;
            bytes -= bytes % frameBytes;
            if (bytes == 0)
            {
                return;
            }

            chunk = _averageDownmix && _inputChannels == 2
                ? StereoToMono(e.Buffer, bytes)
                : e.Buffer[..bytes].ToArray();
            _pcm.Write(chunk, 0, chunk.Length);
            level = Magnitude(ComputePeak(e.Buffer, bytes));
            autoStop = !_autoStopRaised
                && (DateTime.UtcNow - _startUtc).TotalMilliseconds >= MaxDurationMs;
            if (autoStop)
            {
                _autoStopRaised = true;
            }
        }

        // Event subscribers must not be allowed to tear down NAudio's callback
        // thread. Give each subscriber an independent ownership buffer so a
        // later listener cannot clear or mutate bytes accepted by an earlier
        // listener. The recorder clears its dispatch copy after all callbacks;
        // each listener owns its copy once its callback begins, even on throw.
        var pcmListeners = PcmChunkAvailable?.GetInvocationList();
        if (pcmListeners is null)
        {
            Array.Clear(chunk, 0, chunk.Length);
        }
        else
        {
            foreach (var listener in pcmListeners)
            {
                var listenerChunk = chunk.ToArray();
                try
                {
                    ((Action<byte[]>)listener)(listenerChunk);
                }
                catch
                {
                    // The listener may already have retained/enqueued its
                    // buffer before throwing; never clear it behind its back.
                }
            }

            Array.Clear(chunk, 0, chunk.Length);
        }

        try { LevelChanged?.Invoke(level); } catch { /* best effort */ }
        if (autoStop)
        {
            try { AutoStopped?.Invoke(); } catch { /* best effort */ }
        }
    }

    private static void StopAndDispose(WaveInEvent capture)
    {
        try { capture.StopRecording(); } catch { /* already stopped */ }
        try { capture.Dispose(); } catch { /* device already gone */ }
    }

    private static byte[] StereoToMono(byte[] buffer, int bytes)
    {
        var frames = bytes / 4;
        var mono = new byte[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * 4;
            var left = (short)(buffer[offset] | (buffer[offset + 1] << 8));
            var right = (short)(buffer[offset + 2] | (buffer[offset + 3] << 8));
            var average = (left + right) / 2;
            mono[frame * 2] = (byte)average;
            mono[frame * 2 + 1] = (byte)(average >> 8);
        }

        return mono;
    }

    internal static float ComputePeak(byte[] buffer, int bytes)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        bytes = Math.Clamp(bytes, 0, buffer.Length);
        var peak = 0;
        for (var i = 0; i + 1 < bytes; i += 2)
        {
            var s = (short)(buffer[i] | (buffer[i + 1] << 8));
            // Math.Abs(short.MinValue) throws; widen before taking the absolute
            // value because a real microphone can produce exactly -32768.
            var a = Math.Abs((int)s);
            if (a > peak) peak = a;
        }

        return peak / 32768f;
    }

    private static void ClearAndDispose(MemoryStream? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            if (stream.TryGetBuffer(out var segment) && segment.Array is not null)
            {
                Array.Clear(segment.Array, segment.Offset, segment.Count);
            }
        }
        catch
        {
            // Buffer cleanup is best effort; disposal still follows.
        }

        stream.Dispose();
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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Cancel();
    }
}
