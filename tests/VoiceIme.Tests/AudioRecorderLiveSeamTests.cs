using Xunit;

namespace VoiceIme.Tests;

public sealed class AudioRecorderLiveSeamTests
{
    [Fact]
    public void PcmToWav_RoundTrip_PreservesPcmBytes()
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF);
        var wav = AudioRecorder.PcmToWav(pcm);
        var back = LiveProtocol.WavToMono16kPcm(wav);
        Assert.Equal(pcm, back);
    }

    [Fact]
    public void TestToneWav_IsValidLiveSource()
    {
        var pcm = LiveProtocol.WavToMono16kPcm(AudioRecorder.TestToneWav());
        Assert.Equal(16000 * 2, pcm.Length); // 1 s × 16 kHz × 16-bit mono
        Assert.Equal(0, pcm.Length % LiveProtocol.ChunkBytes); // chunks evenly
    }

    [Fact]
    public void AudioRecorder_ExposesPcmChunkEvent()
    {
        using var recorder = new AudioRecorder();
        void Handler(byte[] chunk) { }
        recorder.PcmChunkAvailable += Handler;
        recorder.PcmChunkAvailable -= Handler;
    }
}
