// Reads a PNG file as a BGRA PixelImage (straight alpha), for pictures edited and packed back
// into a game: every colour type (grey, RGB, palette, grey + alpha, RGBA), 1 to 16 bits, tRNS,
// and Adam7 interlacing. 16-bit channels keep their high byte.

using System.IO;
using System.IO.Compression;

namespace OpenShiina.Formats;

public static class PngDecoder
{
    private static readonly byte[] s_signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(s_signature);

    public static PixelImage Decode(byte[] data)
    {
        if (!IsPng(data))
            throw new InvalidDataException("Not a PNG file.");
        int width = 0, height = 0, depth = 0, type = -1, interlace = 0;
        byte[]? palette = null, transparency = null;
        var idat = new MemoryStream();
        for (int p = 8; p + 8 <= data.Length;)
        {
            int length = BigEndian(data, p);
            string name = System.Text.Encoding.ASCII.GetString(data, p + 4, 4);
            int body = p + 8;
            if (length < 0 || body + length > data.Length)
                throw new InvalidDataException("A PNG chunk runs past the end of the file.");
            switch (name)
            {
                case "IHDR":
                    width = BigEndian(data, body);
                    height = BigEndian(data, body + 4);
                    depth = data[body + 8];
                    type = data[body + 9];
                    interlace = data[body + 12];
                    break;
                case "PLTE":
                    palette = data.AsSpan(body, length).ToArray();
                    break;
                case "tRNS":
                    transparency = data.AsSpan(body, length).ToArray();
                    break;
                case "IDAT":
                    idat.Write(data, body, length);
                    break;
            }
            if (name == "IEND")
                break;
            p = body + length + 4;
        }
        if (width <= 0 || height <= 0 || type < 0)
            throw new InvalidDataException("The PNG file has no IHDR.");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {type}.") };
        if (depth is not (1 or 2 or 4 or 8 or 16) || (depth < 8 && type is not (0 or 3)) || (depth == 16 && type == 3))
            throw new InvalidDataException($"PNG colour type {type} with {depth} bits.");
        if (type == 3 && palette == null)
            throw new InvalidDataException("The PNG file has a palette type but no palette.");

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        inflate.CopyTo(raw);
        byte[] filtered = raw.ToArray();

        var pixels = new byte[width * height * 4];
        int bitsPerPixel = channels * depth, bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        int at = 0;
        if (interlace == 0)
            at = Pass(filtered, at, width, height, 0, 0, 1, 1);
        else
        {
            ReadOnlySpan<(int X, int Y, int Dx, int Dy)> passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            foreach (var (x0, y0, dx, dy) in passes)
            {
                int w = (width - x0 + dx - 1) / dx, h = (height - y0 + dy - 1) / dy;
                if (w > 0 && h > 0)
                    at = Pass(filtered, at, w, h, x0, y0, dx, dy);
            }
        }
        return new PixelImage(width, height, PixelLayout.Bgra32, pixels);

        // One image (the whole, or a pass of Adam7) of w x h pixels put at (x0 + x * dx, y0 + y * dy)
        int Pass(byte[] src, int start, int w, int h, int x0, int y0, int dx, int dy)
        {
            int stride = (w * bitsPerPixel + 7) / 8;
            var previous = new byte[stride];
            var current = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                if (start + 1 + stride > src.Length)
                    throw new InvalidDataException("The PNG file's pixels end early.");
                int filter = src[start];
                Array.Copy(src, start + 1, current, 0, stride);
                start += 1 + stride;
                Unfilter(filter, current, previous, bytesPerPixel);
                for (int x = 0; x < w; x++)
                {
                    int d = ((y0 + y * dy) * width + x0 + x * dx) * 4;
                    Pixel(current, x, pixels, d);
                }
                (previous, current) = (current, previous);
            }
            return start;
        }

        // Sample i of a row (a channel value scaled to 8 bits, or a palette index)
        int Sample(byte[] row, int i)
        {
            switch (depth)
            {
                case 8: return row[i];
                case 16: return row[i * 2];
                default:
                    int bit = i * depth;
                    int value = row[bit >> 3] >> (8 - depth - (bit & 7)) & ((1 << depth) - 1);
                    return type == 3 ? value : value * 255 / ((1 << depth) - 1);
            }
        }

        // The raw value of sample i (for tRNS, which names grey and RGB keys at the file's depth)
        int RawSample(byte[] row, int i) => depth switch
        {
            16 => row[i * 2] << 8 | row[i * 2 + 1],
            8 => row[i],
            _ => row[i * depth >> 3] >> (8 - depth - (i * depth & 7)) & ((1 << depth) - 1),
        };

        void Pixel(byte[] row, int x, byte[] dst, int d)
        {
            int s = x * channels;
            switch (type)
            {
                case 0:
                {
                    byte v = (byte)Sample(row, s);
                    dst[d] = dst[d + 1] = dst[d + 2] = v;
                    dst[d + 3] = transparency is { Length: >= 2 } && RawSample(row, s) == (transparency[0] << 8 | transparency[1]) ? (byte)0 : (byte)255;
                    break;
                }
                case 2:
                    dst[d + 2] = (byte)Sample(row, s);
                    dst[d + 1] = (byte)Sample(row, s + 1);
                    dst[d] = (byte)Sample(row, s + 2);
                    dst[d + 3] = transparency is { Length: >= 6 }
                                 && RawSample(row, s) == (transparency[0] << 8 | transparency[1])
                                 && RawSample(row, s + 1) == (transparency[2] << 8 | transparency[3])
                                 && RawSample(row, s + 2) == (transparency[4] << 8 | transparency[5]) ? (byte)0 : (byte)255;
                    break;
                case 3:
                {
                    int i = Sample(row, s);
                    if (i * 3 + 2 < palette!.Length)
                    {
                        dst[d + 2] = palette[i * 3];
                        dst[d + 1] = palette[i * 3 + 1];
                        dst[d] = palette[i * 3 + 2];
                    }
                    dst[d + 3] = transparency != null && i < transparency.Length ? transparency[i] : (byte)255;
                    break;
                }
                case 4:
                {
                    byte v = (byte)Sample(row, s);
                    dst[d] = dst[d + 1] = dst[d + 2] = v;
                    dst[d + 3] = (byte)Sample(row, s + 1);
                    break;
                }
                case 6:
                    dst[d + 2] = (byte)Sample(row, s);
                    dst[d + 1] = (byte)Sample(row, s + 1);
                    dst[d] = (byte)Sample(row, s + 2);
                    dst[d + 3] = (byte)Sample(row, s + 3);
                    break;
            }
        }
    }

    private static void Unfilter(int filter, byte[] row, byte[] previous, int bpp)
    {
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + a),
                2 => (byte)(row[i] + b),
                3 => (byte)(row[i] + (a + b) / 2),
                4 => (byte)(row[i] + Paeth(a, b, c)),
                _ => throw new InvalidDataException($"PNG row filter {filter}."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int BigEndian(byte[] data, int p) => data[p] << 24 | data[p + 1] << 16 | data[p + 2] << 8 | data[p + 3];
}
