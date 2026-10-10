// Bitch Nee-chan's blur (START 9F5E6, SSE2): a tent filter on 32-bit pixels, along the rows (radius
// rx, from the source into the destination) and then down the columns of the destination (radius
// ry), written out from its x86 code. Each line runs three sums over its pixels (edges repeated):
// S1 the tent-weighted sum the pixel comes from, S2 and S3 the plain sums of the window's left half
// (with the centre) and right half, kept in a ring of 2r + 1 pixels; a pixel is
// (int)(float(S1) * (256f / ((r + 1)^2 * 255 + 1))), saturated to a byte. A radius of 0 copies the
// rows (or leaves the columns).

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>
    /// The blur into <paramref name="dst"/> (w x h, rows <paramref name="dstStride"/> apart) from
    /// <paramref name="src"/>; false for sizes the x86 code would not take (w or h under 1).
    /// </summary>
    private bool Blur32(int dst, int dstStride, int w, int h, int src, int srcStride, int rx, int ry)
    {
        if (w <= 0 || h <= 0)
            return false;
        var line = new int[Math.Max(w, h) * 4];
        var output = new byte[Math.Max(w, h) * 4];
        if (rx == 0)
        {
            for (int y = 0; y < h; y++)
                CopyMemory(dst + y * dstStride, src + y * srcStride, w * 4);
        }
        else
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                ReadBytes(src + y * srcStride, row);
                for (int k = 0; k < w * 4; k++)
                    line[k] = row[k];
                BlurLine(line, w, rx, output);
                WriteBytes(dst + y * dstStride, output.AsSpan(0, w * 4));
            }
        }
        if (ry == 0)
            return true;
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                int p = dst + y * dstStride + x * 4;
                for (int c = 0; c < 4; c++)
                    line[y * 4 + c] = ReadByte(p + c);
            }
            BlurLine(line, h, ry, output);
            for (int y = 0; y < h; y++)
                WriteBytes(dst + y * dstStride + x * 4, output.AsSpan(y * 4, 4));
        }
        return true;
    }

    /// <summary>One line of <paramref name="n"/> pixels (4 lanes each) through the tent filter of radius <paramref name="r"/>.</summary>
    private static void BlurLine(int[] line, int n, int r, byte[] output)
    {
        float scale = 256f / ((r + 1) * (r + 1) * 255 + 1);
        int size = 2 * r + 1;
        var ring = new int[size * 4];
        Span<int> s1 = stackalloc int[4], s2 = stackalloc int[4], s3 = stackalloc int[4], incoming = stackalloc int[4];
        // The left half: the first pixel r + 1 times, weights 1 to r + 1
        for (int k = 1; k <= r + 1; k++)
            for (int c = 0; c < 4; c++)
            {
                int v = line[c];
                ring[(k - 1) * 4 + c] = v;
                s2[c] += v;
                s1[c] += v * k;
            }
        // The right half: pixels 1 to r (the last repeated), weights r down to 1
        int last = 0;
        for (int j = 1; j <= r; j++)
        {
            if (j <= n - 1)
                last = j;
            for (int c = 0; c < 4; c++)
            {
                int v = line[last * 4 + c];
                ring[(r + j) * 4 + c] = v;
                s3[c] += v;
                s1[c] += v * (r + 1 - j);
            }
        }
        last = Math.Min(r, n - 1);
        for (int c = 0; c < 4; c++)
            incoming[c] = line[last * 4 + c];
        int head = r;
        for (int x = 0; x < n; x++)
        {
            int leave = head - r;
            if (leave < 0)
                leave += size;
            head++;
            if (last < n - 1)
            {
                last++;
                for (int c = 0; c < 4; c++)
                    incoming[c] = line[last * 4 + c];
            }
            if (head >= size)
                head = 0;
            for (int c = 0; c < 4; c++)
            {
                float f = s1[c] * scale;
                int value = f >= -2147483648f && f < 2147483648f ? (int)f : int.MinValue;
                output[x * 4 + c] = (byte)Math.Clamp(value, 0, 255);
                s1[c] -= s2[c];
                s2[c] -= ring[leave * 4 + c];
                ring[leave * 4 + c] = incoming[c];
                s3[c] += incoming[c];
                s1[c] += s3[c];
                int t = ring[head * 4 + c];
                s2[c] += t;
                s3[c] -= t;
            }
        }
    }
}
