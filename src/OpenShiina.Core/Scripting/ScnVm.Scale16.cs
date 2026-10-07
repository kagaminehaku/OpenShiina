// Ero-On! START 5E2D6, written out: the scaling-down case of the scripts' zoom and pan in the
// first ShiinaRio v2.49 build (Oreimo's 78380 does the same job its own way, ScnVm.Scale32.cs).
// Positions are in 1/16 pixels; every destination pixel is the weighted sum of the source pixels
// its rectangle covers.
//
// For each axis the x86 code first makes a table of n + 1 weights (n the destination size, s the
// source size): v starts at 0 and acc at s / 2; after each entry acc -= 0x100, and when that
// does not leave it above 0, v += 1 and acc += s. v[n] is the weight of a whole source pixel.
// Then an entry per destination row and column (24 bytes in the scripts' work buffers): flags
// (0x80 a partly covered first source pixel, 0x40 a partly covered last one, which is also the
// next entry's first), the number of whole source pixels between, and the weights of the two
// partial ones (v[n - r] and v[r'] for the parts r, r' of the pixel inside), one of which gets
// 1 more when the entry's weights add up to less than 0x100. Per destination pixel, with wy and
// wx the weights of a source row and column:
//   w = saturate16(pmaddwd([wy, wy], [wx, wx]) >>> 8)
//   channel = (sum of byte * w, in 16 bits) >> 8; byte 3 = FF when every source pixel has FF.
// The sums are the same modulo 65536 in any order, so the pixels come out byte for byte
// (SCNBOOT_VERIFY_NATIVE compares them).

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>An axis of Scale16: the weights v[0..n] and an entry per destination pixel.</summary>
    private static (uint Whole, ScaleEntry[] Entries)? Scale16Axis(uint n, uint s, uint start)
    {
        var v = new uint[n + 1];
        uint acc = s >> 1, w = 0;
        for (uint i = 0; ; i++)
        {
            v[i] = w;
            if (i == n)
                break;
            uint before = acc;
            acc -= 0x100;
            if (before <= 0x100)
            {
                w++;
                acc += s;
            }
        }
        uint whole = v[n];
        int count = (int)(n >> 4);
        var entries = new ScaleEntry[count];
        uint r = (start & 15) * n >> 4;
        for (int k = 0; k < count; k++)
        {
            int flags = 0, wholes = 0;
            uint rest = s, sum = 0, first = 0, last = 0, head = 0;
            if (r != 0)
            {
                head = n - r;
                rest = s + r - n;
                first = v[head];
                flags |= 0x80;
                sum += first;
                r = 0;
            }
            if (rest >= n)
            {
                uint q = rest / n;
                if (q > int.MaxValue)
                    return null;
                wholes = (int)q;
                sum += whole * q;
                rest -= q * n;
            }
            if (rest != 0)
            {
                last = v[rest];
                flags |= 0x40;
                sum += last;
                r = rest;
            }
            if (sum < 0x100 && flags != 0)
            {
                if (flags == 0x80 || (flags == 0xC0 && head >= rest))
                    first++;
                else
                    last++;
            }
            entries[k] = new ScaleEntry(flags, wholes, first, last);
        }
        return (whole, entries);
    }

    /// <summary>pmaddwd of two weights as [w, w] dwords, >>> 8, packssdw: the weight of one source pixel.</summary>
    private static uint Scale16Weight(uint a, uint b)
    {
        int sum = (short)(a & 0xFFFF) * (short)(b & 0xFFFF) + (short)(a >> 16) * (short)(b >> 16);
        return (uint)Math.Min((uint)sum >> 8, 0x7FFFu);
    }

    /// <summary>
    /// Ero-On! START 5E2D6: the rectangle (sl, st)-(sr, sb) of a 32-bit picture into (dl, dt)-(dr, db)
    /// of another, scaling down (all 1/16 pixels). False when the x86 code would not end.
    /// </summary>
    public bool Scale16(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
    {
        uint dw = (uint)(dr - dl), dh = (uint)(db - dt), sw = (uint)(sr - sl), sh = (uint)(sb - st);
        if (dw == 0 || dh == 0 || sw == 0 || sh == 0)
            return true;
        if (dw > 0x7FFFFF || dh > 0x7FFFFF || sw > 0x7FFFFF || sh > 0x7FFFFF)
            return false;
        var rowAxis = Scale16Axis(dh, sh, (uint)st);
        var colAxis = Scale16Axis(dw, sw, (uint)sl);
        if (rowAxis is not var (wholeRow, rows) || colAxis is not var (wholeCol, cols))
            return false;

        // The source pixels of each destination column: the partial first one at colFrom (0x80),
        // then the whole ones, then the partial last one (0x40)
        int nc = cols.Length, width = 0;
        var colFrom = new int[nc];
        for (int c = 0, x = 0; c < nc; c++)
        {
            var e = cols[c];
            colFrom[c] = x;
            int a = x + ((e.Flags & 0x80) != 0 ? 1 : 0);
            width = Math.Max(width, a + e.Count + ((e.Flags & 0x40) != 0 ? 1 : 0));
            x = a + e.Count;
        }
        // Where each destination row's source rows start (it shares its last partial one with
        // the next row)
        int nr = rows.Length;
        var rowFrom = new int[nr];
        for (int r = 0, y = 0; r < nr; r++)
        {
            rowFrom[r] = y;
            y += rows[r].Count + ((rows[r].Flags & 0x80) != 0 ? 1 : 0);
        }
        int srcBase = src + (int)((uint)st >> 4) * srcPitch + (int)((uint)sl >> 4) * 4;
        int dstBase = dst + (int)((uint)dt >> 4) * dstPitch + (int)((uint)dl >> 4) * 4;
        int bytes = width * 4;

        void RunBand(int band)
        {
            var line = new byte[bytes];
            // A kind of source row of the destination row: per channel the sum of its bytes (the
            // whole rows share a weight, and the sums only count modulo 65536) and whether every
            // pixel is opaque
            var sum = new int[bytes];
            var opaque = new bool[width];
            var acc = new int[nc * 4];
            var clear = new bool[nc];
            var output = new byte[nc * 4];
            for (int r = band * Scale16Band, end = Math.Min(nr, r + Scale16Band); r < end; r++)
            {
                var row = rows[r];
                Array.Clear(acc);
                Array.Clear(clear);
                int y = rowFrom[r];
                if ((row.Flags & 0x80) != 0)
                {
                    ReadBytes(srcBase + y++ * srcPitch, line);
                    Scale16Load(line, sum, opaque, add: false);
                    Scale16Columns(sum, opaque, row.First, wholeCol, cols, colFrom, acc, clear);
                }
                if (row.Count > 0)
                {
                    for (int k = 0; k < row.Count; k++)
                    {
                        ReadBytes(srcBase + y++ * srcPitch, line);
                        Scale16Load(line, sum, opaque, add: k > 0);
                    }
                    Scale16Columns(sum, opaque, wholeRow, wholeCol, cols, colFrom, acc, clear);
                }
                if ((row.Flags & 0x40) != 0)
                {
                    ReadBytes(srcBase + y * srcPitch, line);
                    Scale16Load(line, sum, opaque, add: false);
                    Scale16Columns(sum, opaque, row.Last, wholeCol, cols, colFrom, acc, clear);
                }
                for (int c = 0, o = 0; c < nc; c++, o += 4)
                {
                    output[o] = (byte)(acc[o] >> 8);
                    output[o + 1] = (byte)(acc[o + 1] >> 8);
                    output[o + 2] = (byte)(acc[o + 2] >> 8);
                    output[o + 3] = clear[c] ? (byte)(acc[o + 3] >> 8) : (byte)0xFF;
                }
                WriteBytes(dstBase + r * dstPitch, output);
            }
        }
        // Rows are independent: bands of them run on all cores (the result does not depend on it)
        int bands = (nr + Scale16Band - 1) / Scale16Band;
        if (bands > 1 && (long)nr * nc >= 20000)
            Parallel.For(0, bands, RunBand);
        else
            for (int band = 0; band < bands; band++)
                RunBand(band);
        return true;
    }

    private const int Scale16Band = 16;

    /// <summary>A source row into the sums (or added to them) and the opaque flags.</summary>
    private static void Scale16Load(byte[] line, int[] sum, bool[] opaque, bool add)
    {
        if (!add)
        {
            for (int i = 0; i < line.Length; i++)
                sum[i] = line[i];
            for (int x = 0, i = 3; x < opaque.Length; x++, i += 4)
                opaque[x] = line[i] == 0xFF;
            return;
        }
        for (int i = 0; i < line.Length; i++)
            sum[i] += line[i];
        for (int x = 0, i = 3; x < opaque.Length; x++, i += 4)
            opaque[x] &= line[i] == 0xFF;
    }

    /// <summary>One kind of source row, of row weight wy, into every destination column's sums.</summary>
    private static void Scale16Columns(int[] sum, bool[] opaque, uint wy, uint wholeCol, ScaleEntry[] cols, int[] colFrom, int[] acc, bool[] clear)
    {
        int wWhole = (int)Scale16Weight(wholeCol, wy);
        for (int c = 0, o = 0; c < cols.Length; c++, o += 4)
        {
            var e = cols[c];
            int x = colFrom[c];
            int a0 = acc[o], a1 = acc[o + 1], a2 = acc[o + 2], a3 = acc[o + 3];
            bool notOpaque = clear[c];
            if ((e.Flags & 0x80) != 0)
            {
                int w = (int)Scale16Weight(e.First, wy), i = x * 4;
                a0 += sum[i] * w; a1 += sum[i + 1] * w; a2 += sum[i + 2] * w; a3 += sum[i + 3] * w;
                notOpaque |= !opaque[x++];
            }
            if (e.Count > 0)
            {
                int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int n = 0, i = x * 4; n < e.Count; n++, i += 4)
                {
                    s0 += sum[i]; s1 += sum[i + 1]; s2 += sum[i + 2]; s3 += sum[i + 3];
                    notOpaque |= !opaque[x + n];
                }
                a0 += s0 * wWhole; a1 += s1 * wWhole; a2 += s2 * wWhole; a3 += s3 * wWhole;
                x += e.Count;
            }
            if ((e.Flags & 0x40) != 0)
            {
                int w = (int)Scale16Weight(e.Last, wy), i = x * 4;
                a0 += sum[i] * w; a1 += sum[i + 1] * w; a2 += sum[i + 2] * w; a3 += sum[i + 3] * w;
                notOpaque |= !opaque[x];
            }
            // The sums count modulo 65536 (the MMX words wrap)
            acc[o] = a0 & 0xFFFF; acc[o + 1] = a1 & 0xFFFF; acc[o + 2] = a2 & 0xFFFF; acc[o + 3] = a3 & 0xFFFF;
            clear[c] = notOpaque;
        }
    }
}
