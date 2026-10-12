// Bitch Nee-chan's blur (START 9F5E6, SSE2; Bitch Gakuen's too): a tent filter on 32-bit pixels,
// along the rows (radius rx, from the source into the destination) and then down the columns of
// the destination (radius ry), written out from its x86 code. Each line runs three sums over its
// pixels (edges repeated): S1 the tent-weighted sum the pixel comes from, S2 and S3 the plain sums
// of the window's left half (with the centre) and right half, kept in a ring of 2r + 1 pixels; a
// pixel is (int)(float(S1) * (256f / ((r + 1)^2 * 255 + 1))), saturated to a byte. A radius of 0
// copies the rows (or leaves the columns).
// The rows run on several cores; the columns run all at once, a row of the picture a step (the
// sums of every column side by side, in vectors), in place: a row is read into the sums before
// it is written.

using System.Numerics;
using System.Runtime.Intrinsics;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>
    /// The blur into <paramref name="dst"/> (w x h, rows <paramref name="dstStride"/> apart) from
    /// <paramref name="src"/>; false for sizes the x86 code would not take (w or h under 1).
    /// </summary>
    private bool Blur32(int dst, int dstStride, int w, int h, int src, int srcStride, int rx, int ry)
    {
        if (w <= 0 || h <= 0 || rx < 0 || ry < 0)
            return false;
        if (rx == 0)
        {
            for (int y = 0; y < h; y++)
                CopyMemory(dst + y * dstStride, src + y * srcStride, w * 4);
        }
        else
        {
            // Rows that share bytes with other rows' destinations run one after the other
            long srcEnd = src + (long)(h - 1) * srcStride + w * 4L, dstEnd = dst + (long)(h - 1) * dstStride + w * 4L;
            bool apart = src >= dstEnd || dst >= srcEnd || (src == dst && srcStride == dstStride);
            void RunRows(int from, int to)
            {
                var buffers = t_blurBuffers ??= new BlurBuffers();
                var line = buffers.Line(w * 4);
                var output = buffers.Output(w * 4);
                for (int y = from; y < to; y++)
                {
                    var row = Bytes(src + y * srcStride, w * 4);
                    for (int k = 0; k < w * 4; k++)
                        line[k] = row[k];
                    BlurLine(line, w, rx, output, buffers.Ring((2 * rx + 1) * 4));
                    output.AsSpan(0, w * 4).CopyTo(Bytes(dst + y * dstStride, w * 4));
                }
            }
            if (apart && srcStride > 0 && dstStride > 0)
                ParallelRows.For(h, (long)w * h, RunRows);
            else
                RunRows(0, h);
        }
        if (ry == 0)
            return true;
        // Columns are independent: bands of them run on several cores
        int lanes = w * 4, band = Math.Max(64, Vector<int>.Count);
        int bands = (lanes + band - 1) / band;
        ParallelRows.For(bands, (long)w * h, (from, to) =>
            BlurColumns(dst, dstStride, from * band, Math.Min(lanes, to * band), h, ry));
        return true;
    }

    [ThreadStatic]
    private static BlurBuffers? t_blurBuffers;

    /// <summary>A thread's line, output and ring arrays, kept while they are large enough.</summary>
    private sealed class BlurBuffers
    {
        private int[] m_line = [], m_ring = [];
        private byte[] m_output = [];

        public int[] Line(int n) => m_line.Length >= n ? m_line : m_line = new int[n];
        public int[] Ring(int n) => m_ring.Length >= n ? m_ring : m_ring = new int[n];
        public byte[] Output(int n) => m_output.Length >= n ? m_output : m_output = new byte[n];
    }

    /// <summary>
    /// One line of <paramref name="n"/> pixels (4 lanes each, a pixel a vector) through the tent
    /// filter of radius <paramref name="r"/>.
    /// </summary>
    private static void BlurLine(int[] line, int n, int r, byte[] output, int[] ring)
    {
        float scale = 256f / ((r + 1) * (r + 1) * 255 + 1);
        int size = 2 * r + 1;
        Vector128<int> At(int[] array, int pixel) => Vector128.LoadUnsafe(ref array[pixel * 4]);
        void Put(int[] array, int pixel, Vector128<int> v) => v.StoreUnsafe(ref array[pixel * 4]);
        Vector128<int> s1 = default, s2 = default, s3 = default;
        // The left half: the first pixel r + 1 times, weights 1 to r + 1
        var first = At(line, 0);
        for (int k = 1; k <= r + 1; k++)
        {
            Put(ring, k - 1, first);
            s2 += first;
            s1 += first * k;
        }
        // The right half: pixels 1 to r (the last repeated), weights r down to 1
        int last = 0;
        for (int j = 1; j <= r; j++)
        {
            if (j <= n - 1)
                last = j;
            var v = At(line, last);
            Put(ring, r + j, v);
            s3 += v;
            s1 += v * (r + 1 - j);
        }
        last = Math.Min(r, n - 1);
        var incoming = At(line, last);
        int head = r;
        var vScale = Vector128.Create(scale);
        var max = Vector128.Create(255);
        for (int x = 0; x < n; x++)
        {
            int leave = head - r;
            if (leave < 0)
                leave += size;
            head++;
            if (last < n - 1)
            {
                last++;
                incoming = At(line, last);
            }
            if (head >= size)
                head = 0;
            // truncated, saturated (lanes are 0 to 255 * (r + 1)^2: in range)
            var value = Vector128.Min(Vector128.Max(Vector128.ConvertToInt32(Vector128.ConvertToSingle(s1) * vScale), Vector128<int>.Zero), max);
            // the four bytes in one store (the lanes are 0-255): a lane at a time goes through
            // memory each where the runtime keeps vectors there (Mono)
            var bytes = Vector128.Narrow(Vector128.Narrow(value, value), Vector128<short>.Zero);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(x * 4), bytes.AsUInt32().ToScalar());
            s1 -= s2;
            s2 -= At(ring, leave);
            Put(ring, leave, incoming);
            s3 += incoming;
            s1 += s3;
            var t = At(ring, head);
            s2 += t;
            s3 -= t;
        }
    }

    /// <summary>A sum times the scale to a byte: truncated (cvttps2dq), saturated.</summary>
    private static byte BlurByte(float f)
    {
        int value = f >= -2147483648f && f < 2147483648f ? (int)f : int.MinValue;
        return (byte)Math.Clamp(value, 0, 255);
    }

    /// <summary>
    /// The column pass in place for the lanes [first, end) of every row (4 a pixel):
    /// <see cref="BlurLine"/> down those columns at once, their sums side by side; row y is
    /// written after row last (the incoming one) is read.
    /// </summary>
    private void BlurColumns(int dst, int stride, int first, int end, int h, int r)
    {
        int lanes = end - first, size = 2 * r + 1;
        dst += first;
        float scale = 256f / ((r + 1) * (r + 1) * 255 + 1);
        var s1 = new int[lanes];
        var s2 = new int[lanes];
        var s3 = new int[lanes];
        var incoming = new int[lanes];
        var ring = new int[size * lanes];
        Span<byte> Row(int y) => Bytes(dst + y * stride, lanes);
        void Load(int y, int[] into)
        {
            var row = Row(y);
            for (int k = 0; k < lanes; k++)
                into[k] = row[k];
        }

        // The left half: row 0 r + 1 times, weights 1 to r + 1
        Load(0, incoming);
        for (int k = 1; k <= r + 1; k++)
        {
            incoming.CopyTo(ring, (k - 1) * lanes);
            for (int i = 0; i < lanes; i++)
            {
                s2[i] += incoming[i];
                s1[i] += incoming[i] * k;
            }
        }
        // The right half: rows 1 to r (the last repeated), weights r down to 1
        int last = 0;
        for (int j = 1; j <= r; j++)
        {
            if (j <= h - 1)
            {
                last = j;
                Load(last, incoming);
            }
            incoming.CopyTo(ring, (r + j) * lanes);
            for (int i = 0; i < lanes; i++)
            {
                s3[i] += incoming[i];
                s1[i] += incoming[i] * (r + 1 - j);
            }
        }
        last = Math.Min(r, h - 1);
        Load(last, incoming);
        int head = r;
        var vScale = new Vector<float>(scale);
        int vn = Vector<int>.Count;
        // a vector's lanes for the output bytes, stored at once (a lane read at a time by a
        // variable index costs a trip through memory each where vectors are kept there: Mono)
        Span<int> outLanes = stackalloc int[vn];
        for (int y = 0; y < h; y++)
        {
            int leave = head - r;
            if (leave < 0)
                leave += size;
            head++;
            if (last < h - 1)
            {
                last++;
                Load(last, incoming);
            }
            if (head >= size)
                head = 0;
            var output = Row(y);
            int leaveAt = leave * lanes, headAt = head * lanes;
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; i + vn <= lanes; i += vn)
                {
                    var v1 = new Vector<int>(s1, i);
                    var v2 = new Vector<int>(s2, i);
                    var v3 = new Vector<int>(s3, i);
                    var vin = new Vector<int>(incoming, i);
                    // the output: truncated, saturated (lanes are 0 to 255 * (r + 1)^2: in range)
                    var f = Vector.ConvertToInt32(Vector.ConvertToSingle(v1) * vScale);
                    var b = Vector.Min(Vector.Max(f, Vector<int>.Zero), new Vector<int>(255));
                    b.CopyTo(outLanes);
                    for (int k = 0; k < vn; k++)
                        output[i + k] = (byte)outLanes[k];
                    v1 -= v2;
                    v2 -= new Vector<int>(ring, leaveAt + i);
                    vin.CopyTo(ring, leaveAt + i);
                    v3 += vin;
                    v1 += v3;
                    var t = new Vector<int>(ring, headAt + i);
                    v2 += t;
                    v3 -= t;
                    v1.CopyTo(s1, i);
                    v2.CopyTo(s2, i);
                    v3.CopyTo(s3, i);
                }
            }
            for (; i < lanes; i++)
            {
                output[i] = BlurByte(s1[i] * scale);
                s1[i] -= s2[i];
                s2[i] -= ring[leaveAt + i];
                ring[leaveAt + i] = incoming[i];
                s3[i] += incoming[i];
                s1[i] += s3[i];
                int t = ring[headAt + i];
                s2[i] += t;
                s3[i] -= t;
            }
        }
    }
}
