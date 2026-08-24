using OscarWatch.Services;

namespace OscarWatch.Tests;

public class AlertToneWavTests
{
    [Fact]
    public void Create_writes_valid_riff_wave_header()
    {
        var wav = AlertToneWav.Create();

        Assert.True(wav.Length > 44);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);
        Assert.Equal((byte)'f', wav[12]);
        Assert.Equal((byte)'m', wav[13]);
        Assert.Equal((byte)'t', wav[14]);
        Assert.Equal((byte)' ', wav[15]);
        Assert.Equal((byte)'d', wav[36]);
        Assert.Equal((byte)'a', wav[37]);
        Assert.Equal((byte)'t', wav[38]);
        Assert.Equal((byte)'a', wav[39]);
    }

    [Fact]
    public void CreatePcm16_matches_wav_payload_length()
    {
        var wav = AlertToneWav.Create();
        var pcm = AlertToneWav.CreatePcm16();
        var dataBytes = BitConverter.ToInt32(wav, 40);
        Assert.Equal(dataBytes, pcm.Length * sizeof(short));
    }
}
