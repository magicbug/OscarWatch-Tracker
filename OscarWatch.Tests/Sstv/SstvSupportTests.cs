using System.IO.Compression;
using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

public sealed class SstvSupportTests
{
    [Theory]
    [InlineData("FM", 145_800.0, 145_799_700L, 0)]
    [InlineData("USB", 435_600.0, 435_599_800L, 200)]
    [InlineData("USB", 435_600.0, 435_600_150L, -150)]
    [InlineData("LSB", 435_600.0, 435_599_800L, -200)]
    [InlineData("USB", 435_600.0, 435_590_000L, 0)]
    [InlineData("USB", 435_600.0, 435_599_200L, 500)]
    public void Feed_forward_follows_the_sideband(string mode, double idealKHz, long rigHz, double expected)
    {
        Assert.Equal(expected, SstvDopplerFeedForward.AudioOffsetHz(mode, idealKHz, rigHz), 6);
    }

    [Fact]
    public void Spectrum_tap_peaks_at_the_tone_and_paces_rows_by_audio_time()
    {
        const int rate = 48000;
        var tap = new OscarWatch.Sstv.SstvSpectrumTap(rate, rowsPerSecond: 10);
        var audio = new float[rate];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1200 * i / rate));

        var rows = new List<float[]>();
        for (var i = 0; i < audio.Length; i += 1000)
        {
            if (tap.Add(audio.AsSpan(i, Math.Min(1000, audio.Length - i))) is { } row)
                rows.Add(row);
        }

        Assert.InRange(rows.Count, 9, 10);
        var last = rows[^1];
        var peak = Array.IndexOf(last, last.Max());
        var peakHz = OscarWatch.Sstv.SstvSpectrumTap.MinHz
            + (OscarWatch.Sstv.SstvSpectrumTap.MaxHz - OscarWatch.Sstv.SstvSpectrumTap.MinHz) * peak / (last.Length - 1);
        Assert.InRange(peakHz, 1170, 1230);
    }

    [Theory]
    [InlineData("20261010_151943Z_ISS_Pd120.png", "ISS", SstvModeId.Pd120)]
    [InlineData("20261010_151943Z_ISS_Pd120_2.png", "ISS", SstvModeId.Pd120)]
    [InlineData("20261010_151943Z_ISS---13-10-2024-1131_Pd120.png", "ISS---13-10-2024-1131", SstvModeId.Pd120)]
    [InlineData("20261010_151943Z_MartinM1.png", null, SstvModeId.MartinM1)]
    public void Picture_names_are_read_back(string name, string? satellite, SstvModeId mode)
    {
        var picture = SstvPictureCatalog.Describe(Path.Combine("x", name), DateTime.UnixEpoch, 10);

        Assert.Equal(new DateTime(2026, 10, 10, 15, 19, 43, DateTimeKind.Utc), picture.ReceivedUtc);
        Assert.Equal(satellite, picture.Satellite);
        Assert.Equal(mode, picture.Mode?.Id);
    }

    [Fact]
    public void Picture_name_round_trips_through_the_stem()
    {
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var stem = SstvPaths.PictureStem(utc, SstvModeTable.ScottieDx, "AO 91");

        var picture = SstvPictureCatalog.Describe(stem + ".png", DateTime.UnixEpoch, 0);

        Assert.Equal(utc, picture.ReceivedUtc);
        Assert.Equal("AO-91", picture.Satellite);
        Assert.Same(SstvModeTable.ScottieDx, picture.Mode);
    }

    [Fact]
    public void Other_picture_names_use_the_file_time()
    {
        var fileTime = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var picture = SstvPictureCatalog.Describe("holiday snap.png", fileTime, 0);

        Assert.Equal(fileTime, picture.ReceivedUtc);
        Assert.Null(picture.Mode);
        Assert.Null(picture.Satellite);
    }

    [Fact]
    public void Feed_forward_is_zero_without_rig_data()
    {
        Assert.Equal(0, SstvDopplerFeedForward.AudioOffsetHz("USB", null, 435_600_000));
        Assert.Equal(0, SstvDopplerFeedForward.AudioOffsetHz("USB", 435_600, null));
    }

    [Fact]
    public void Png_round_trips_pixels()
    {
        var image = new SstvImage(5, 3);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 5; x++)
                image.SetPixel(x, y, x * 50, y * 100, 255 - x * 10);
        }

        using var stream = new MemoryStream();
        SstvPngWriter.Write(image, stream);
        var bytes = stream.ToArray();

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        var idat = FindChunk(bytes, "IDAT");
        using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var pixels = raw.ToArray();
        Assert.Equal(3 * (1 + 5 * 3), pixels.Length);
        for (var y = 0; y < 3; y++)
        {
            Assert.Equal(0, pixels[y * 16]);
            for (var x = 0; x < 5; x++)
            {
                var o = y * 16 + 1 + x * 3;
                Assert.Equal(image.GetPixel(x, y), (pixels[o], pixels[o + 1], pixels[o + 2]));
            }
        }
    }

    [Fact]
    public void Picture_file_names_are_safe()
    {
        var stem = SstvPaths.PictureStem(new DateTime(2024, 10, 12, 7, 31, 5, DateTimeKind.Utc), SstvModeTable.Pd120, "ISS (ZARYA)");
        Assert.Equal("20241012_073105Z_ISS-(ZARYA)_Pd120", stem);
    }

    [Fact]
    public void Clearing_rows_blanks_the_bottom_only()
    {
        var image = new SstvImage(2, 4);
        for (var y = 0; y < 4; y++)
        {
            image.SetPixel(0, y, 200, 200, 200);
            image.SetPixel(1, y, 200, 200, 200);
        }

        image.ClearFromRow(2);

        Assert.Equal(((byte)200, (byte)200, (byte)200), image.GetPixel(1, 1));
        Assert.Equal(((byte)0, (byte)0, (byte)0), image.GetPixel(0, 2));
        Assert.Equal(((byte)0, (byte)0, (byte)0), image.GetPixel(1, 3));
    }

    private static byte[] FindChunk(byte[] png, string type)
    {
        var at = 8;
        while (at < png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var name = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (name == type)
                return png[(at + 8)..(at + 8 + length)];
            at += 12 + length;
        }

        throw new InvalidDataException(type + " chunk not found");
    }
}
