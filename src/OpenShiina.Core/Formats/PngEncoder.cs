// Writes a PixelImage as a PNG file (8-bit RGBA or RGB, one IDAT, adaptive row filters), for
// the save thumbnails. Extraction in the WPF app keeps the WPF encoder.

using System.IO;
using System.IO.Compression;
using System.Text;

namespace OpenShiina.Formats;

public static class PngEncoder
{
    private static readonly byte[] s_signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    public static byte[] Encode(PixelImage image)
    {
        bool alpha = image.Layout == PixelLayout.Bgra32;
        int bpp = alpha ? 4 : 3;
        int rowBytes = image.Width * bpp;

        // Rows in RGB(A) order, each with the filter that gives the smallest sum of magnitudes
        var raw = new byte[(rowBytes + 1) * image.Height];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var candidate = new byte[rowBytes];
        for (int y = 0; y < image.Height; y++)
        {
            int src = y * image.Stride;
            for (int x = 0; x < image.Width; x++, src += bpp)
            {
                int d = x * bpp;
                current[d] = image.Pixels[src + 2];
                current[d + 1] = image.Pixels[src + 1];
                current[d + 2] = image.Pixels[src];
                if (alpha)
                    current[d + 3] = image.Pixels[src + 3];
            }
            int row = y * (rowBytes + 1);
            long best = long.MaxValue;
            for (byte filter = 0; filter <= 4; filter++)
            {
                long cost = Filter(filter, current, previous, bpp, candidate);
                if (cost < best)
                {
                    best = cost;
                    raw[row] = filter;
                    Buffer.BlockCopy(candidate, 0, raw, row + 1, rowBytes);
                }
            }
            (previous, current) = (current, previous);
        }

        using var output = new MemoryStream();
        output.Write(s_signature);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)image.Width);
        WriteBigEndian(header, 4, (uint)image.Height);
        header[8] = 8;                          // bits per channel
        header[9] = (byte)(alpha ? 6 : 2);      // RGBA / RGB
        WriteChunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    /// <summary>Filters a row into <paramref name="result"/>; returns the sum of the bytes as signed values.</summary>
    private static long Filter(byte type, byte[] row, byte[] above, int bpp, byte[] result)
    {
        long cost = 0;
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
            int predictor = type switch
            {
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            byte value = (byte)(row[i] - predictor);
            result[i] = value;
            cost += value < 128 ? value : 256 - value;
        }
        return cost;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int at, uint value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }

    // CRC-32 of PNG chunks (reflected, polynomial 0xEDB88320)
    private static readonly uint[] s_crcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (byte b in data)
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
