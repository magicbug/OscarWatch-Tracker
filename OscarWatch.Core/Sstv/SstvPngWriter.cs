using System.Buffers.Binary;
using System.IO.Compression;

namespace OscarWatch.Core.Sstv;

/// <summary>Writes an <see cref="SstvImage"/> as an 8-bit RGB PNG, without a UI toolkit.</summary>
public static class SstvPngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Save(SstvImage image, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var stream = File.Create(path);
        Write(image, stream);
    }

    public static void Write(SstvImage image, Stream stream)
    {
        stream.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: truecolour
        WriteChunk(stream, "IHDR", header);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var rowBytes = image.Width * 3;
                for (var y = 0; y < image.Height; y++)
                {
                    z.WriteByte(0); // filter: none
                    z.Write(image.Rgb, y * rowBytes, rowBytes);
                }
            }

            WriteChunk(stream, "IDAT", raw.ToArray());
        }

        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        stream.Write(buffer);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
