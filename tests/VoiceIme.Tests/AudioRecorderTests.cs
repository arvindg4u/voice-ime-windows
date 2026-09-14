using System;
using Xunit;

namespace VoiceIme.Tests;

public sealed class AudioRecorderTests
{
    [Fact]
    public void PcmToWav_WrapsPcmIn44ByteRiffHeader()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };

        var wav = AudioRecorder.PcmToWav(pcm);

        Assert.Equal(48, wav.Length);
        Assert.Equal("RIFF", Chunk(wav, 0));
        Assert.Equal("WAVE", Chunk(wav, 8));
        Assert.Equal("fmt ", Chunk(wav, 12));
        Assert.Equal("data", Chunk(wav, 36));
        // data length little-endian at 40
        Assert.Equal(4, wav[40]);
        // payload copied verbatim
        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public void TestToneWav_IsOneSecondOf16kHzMono()
    {
        var wav = AudioRecorder.TestToneWav();

        Assert.Equal(44 + 16000 * 2, wav.Length);
        Assert.Equal("RIFF", Chunk(wav, 0));
    }

    [Fact]
    public void Magnitude_ZeroIsZero_AndClampedToOne()
    {
        Assert.Equal(0f, AudioRecorder.Magnitude(0f));
        Assert.Equal(1f, AudioRecorder.Magnitude(10f));
        var mid = AudioRecorder.Magnitude(0.05f);
        Assert.InRange(mid, 0f, 1f);
    }

    [Fact]
    public void IsSilent_RespectsThreshold()
    {
        Assert.True(AudioRecorder.IsSilent(0.001f));
        Assert.False(AudioRecorder.IsSilent(0.5f));
    }

    private static string Chunk(byte[] b, int offset) =>
        System.Text.Encoding.ASCII.GetString(b, offset, 4);
}
