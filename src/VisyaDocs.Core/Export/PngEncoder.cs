using System.Buffers.Binary;
using System.IO.Compression;

namespace VisyaDocs.Core.Export;

/// <summary>Minimal PNG writer: BGRA in, 24 bit RGB out (or 32 bit RGBA with <c>alpha</c>, straight alpha expected).</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(Stream output, ReadOnlySpan<byte> bgra, int width, int height, bool alpha = false)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // bit depth
        header[9] = (byte)(alpha ? 6 : 2);  // RGBA or RGB
        int channels = alpha ? 4 : 3;
        header[10] = 0; header[11] = 0; header[12] = 0;
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + width * channels];
            var previous = new byte[row.Length];
            var filtered = new byte[row.Length];
            for (int y = 0; y < height; y++)
            {
                var src = bgra.Slice(y * width * 4, width * 4);
                for (int x = 0; x < width; x++)
                {
                    int o = 1 + x * channels;
                    row[o] = src[x * 4 + 2];
                    row[o + 1] = src[x * 4 + 1];
                    row[o + 2] = src[x * 4];
                    if (alpha) row[o + 3] = src[x * 4 + 3];
                }
                // "Up" filter: documents have long runs of identical rows, which this compresses well.
                filtered[0] = 2;
                for (int i = 1; i < row.Length; i++) filtered[i] = (byte)(row[i] - previous[i]);
                z.Write(filtered);
                (previous, row) = (row, previous);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        output.Write(buf);
        output.Write(type);
        output.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        output.Write(buf);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
