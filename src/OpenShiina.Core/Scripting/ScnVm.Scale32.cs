// Oreimo START 78380, written out: scales a rectangle of a 32-bit picture into another with
// positions in 1/16 pixels by area averaging (the MMX path of the scripts' zoom and pan). Every
// destination pixel is the weighted sum of the source pixels its rectangle covers.
//
// The x86 code first makes a table per destination row and per destination column (24-byte
// entries in the scripts' work buffers): flags (0x80 a partly covered first source pixel, 0x40 a
// partly covered last one; columns may add 0x20, which only picks a copy of the same loop),
// the number of whole source pixels in between, and the weights of the two partial ones. A
// weight is x * K / S with K = 0x5ADC for rows and 0x5ADD for columns, S the source size: the
// division is a multiplication by a rounded reciprocal of the low 32 bits of x * K. Then for
// each destination pixel, with wy and wx the weights of a source row and column:
//   w = pmaddwd([wy, wy], [wx, wx]) >>> 21 (signed 16-bit words, 0-2047)
//   channel = (sum of byte * w, in 16 bits) >> 8; byte 3 = FF when all source pixels have FF.
// One quirk is kept: the loop is picked once per destination row (by its row entry and first
// column); rows other than 0xC0 with up to one whole source row then take the general loop
// (0x79160), which for the partly covered source rows takes the first column's pixel from a
// register (the previous pixel's last column, mm2 / mm4) and leaves it out of the FF test -
// for every destination pixel of the row but the first.
// The tables are made here with the same integer steps; the sums are the same modulo 65536 in
// any order, so the pixels come out byte for byte (SCNBOOT_VERIFY_NATIVE compares them).

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private readonly record struct ScaleEntry(int Flags, int Count, uint First, uint Last);

    /// <summary>The reciprocal division of the x86 code: low32(x * K) / S, rounded as it rounds.</summary>
    private readonly struct ScaleDivider
    {
        private readonly uint m_k, m_multiplier;
        private readonly int m_shift, m_bias;

        public ScaleDivider(uint s, uint k)
        {
            m_k = k;
            m_shift = 31 - System.Numerics.BitOperations.LeadingZeroCount(s);
            if (s == 1u << m_shift)
            {
                m_bias = -1;
                return;
            }
            ulong dividend = (ulong)(1u << m_shift) << 32;
            ulong q = dividend / s, r = dividend % s;
            m_bias = r <= s >> 1 ? 1 : 0;
            m_multiplier = (uint)q + (uint)(m_bias ^ 1);
        }

        public uint Divide(uint x)
        {
            uint t = x * m_k;
            if (m_bias >= 0)
            {
                uint biased = t + (uint)m_bias;
                t = biased < t ? m_multiplier : (uint)((ulong)biased * m_multiplier >> 32);
            }
            return t >> m_shift;
        }
    }

    /// <summary>
    /// The table of one axis (0x78380-0x78748 for rows, 0x78748-0x78AA9 for columns); null when
    /// the x86 code would not end (no destination pixel, or a destination pixel smaller than a
    /// source pixel: the routine only scales down).
    /// </summary>
    private static List<ScaleEntry>? ScaleTable(uint s, uint d, uint sourceStart, uint destinationStart, uint destinationEnd, uint k, bool columns)
    {
        var divider = new ScaleDivider(s, k);
        uint esi = (sourceStart & 15) * d >> 4;
        uint lead = (0 - destinationStart) & 15;
        uint n = ((d - lead) >> 4) + (lead != 0 ? 1u : 0);
        if (n == 0 || n > 0x10000)
            return null;
        var table = new List<ScaleEntry>((int)n + 1);
        bool first = lead != 0;
        for (uint i = 0; i < n; i++)
        {
            uint count = 0, flags = 0, eax = 0, ebx, wFirst = 0, wLast = 0;
            if (first)
            {
                first = false;
                ebx = lead * s >> 4;
                eax = (16 - (sourceStart & 15)) * d >> 4;
                if (ebx < eax)
                    goto Last;
                if (ebx == eax)
                {
                    if (esi == 0)
                        goto SubtractOnce;
                    if (columns)
                        flags |= 0x20;
                    ebx = 0;
                    goto First;
                }
                if (esi == 0)
                    goto SubtractOnce;
                if (ebx >= d)
                    goto Generic;
                ebx -= eax;
                eax = d - esi;
                if (columns)
                    flags |= 0x20;
                goto First;
            }
            ebx = s;
        Generic:
            if (esi == 0)
                goto Whole;
            ebx = ebx + esi - d;
            eax = d - esi;
        First:
            wFirst = divider.Divide(eax);
            flags |= 0x80;
            esi = 0;
        Whole:
            while (ebx >= d)
            {
                ebx -= d;
                count++;
            }
            goto AfterWhole;
        SubtractOnce:
            do
            {
                ebx -= d;
                count++;
            }
            while (ebx >= d);
        AfterWhole:
            if (ebx == 0)
                goto Store;
        Last:
            wLast = divider.Divide(ebx);
            flags |= 0x40;
            esi += ebx;
        Store:
            // Upscaling makes the unsigned steps above wrap (the x86 code then runs away)
            if (count > (s >> 4) + 2)
                return null;
            table.Add(new ScaleEntry((int)flags, (int)count, wFirst, wLast));
        }
        uint end = destinationEnd & 15;
        if (end != 0)
        {
            uint count = 0, flags = 0, wFirst = 0, wLast = 0;
            uint ebx = end * s >> 4;
            if (esi != 0)
            {
                uint eax = d - esi;
                if (eax >= ebx)
                {
                    eax = ebx;
                    ebx = 0;
                }
                else
                    ebx -= eax;
                wFirst = divider.Divide(eax);
                flags |= 0x80;
            }
            while (ebx >= d)
            {
                ebx -= d;
                count++;
            }
            if (ebx != 0)
            {
                wLast = divider.Divide(ebx);
                flags |= 0x40;
            }
            if (count > (s >> 4) + 2)
                return null;
            table.Add(new ScaleEntry((int)flags, (int)count, wFirst, wLast));
        }
        return table;
    }

    /// <summary>pmaddwd of two [w, w] pairs, >>> 21 (packssdw cannot saturate 0-2047).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint ScaleWeight(uint a, uint b)
    {
        int d = (short)a * (short)b + (short)(a >> 16) * (short)(b >> 16);
        return (uint)d >> 21;
    }

    /// <summary>
    /// START 78380 written out (see the top of the file). Positions are in 1/16 pixels: the
    /// destination rectangle (left, top, right, bottom) and the source one. False when the
    /// x86 code would not end (it only scales down).
    /// </summary>
    public bool Scale32(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
    {
        uint dw = (uint)(dr - dl), dh = (uint)(db - dt), sw = (uint)(sr - sl), sh = (uint)(sb - st);
        if (dw == 0 || dh == 0 || sw == 0 || sh == 0)
            return true;
        var rows = ScaleTable(sh, dh, (uint)st, (uint)dt, (uint)db, 0x5ADC, columns: false);
        var cols = ScaleTable(sw, dw, (uint)sl, (uint)dl, (uint)dr, 0x5ADD, columns: true);
        if (rows == null || cols == null)
            return false;
        var divRows = new ScaleDivider(sh, 0x5ADC);
        var divCols = new ScaleDivider(sw, 0x5ADD);
        uint fullRow = divRows.Divide(dh), fullCol = divCols.Divide(dw);

        // The source pixels of each destination column: from colFrom (the partial first one
        // when 0x80), whole ones in [colA, colB), the partial last one at colB when 0x40
        int nc = cols.Count;
        var colFrom = new int[nc];
        var colA = new int[nc];
        var colB = new int[nc];
        int x = 0, width = 0;
        for (int c = 0; c < nc; c++)
        {
            var e = cols[c];
            colFrom[c] = x;
            colA[c] = x + ((e.Flags & 0x80) != 0 ? 1 : 0);
            colB[c] = colA[c] + e.Count;
            width = Math.Max(width, colB[c] + ((e.Flags & 0x40) != 0 ? 1 : 0));
            x += e.Count + ((e.Flags & 0x80) != 0 ? 1 : 0);
        }

        int srcX = src + (int)((uint)st >> 4) * srcPitch + (int)((uint)sl >> 4) * 4;
        int dstRow = dst + (int)((uint)dt >> 4) * dstPitch + (int)((uint)dl >> 4) * 4;
        int bytes = width * 4;
        var colWx = new uint[nc * 2];
        var colShape = new int[nc * 4];
        for (int c = 0; c < nc; c++)
        {
            colWx[2 * c] = cols[c].First;
            colWx[2 * c + 1] = cols[c].Last;
            colShape[4 * c] = colFrom[c];
            colShape[4 * c + 1] = colA[c];
            colShape[4 * c + 2] = colB[c];
            colShape[4 * c + 3] = cols[c].Flags & 0xC0;
        }
        // The first source row of every destination row; the pages of all rows exist before the
        // rows are shared out between threads (the page table makes pages on first use)
        int nr = rows.Count;
        var rowY = new int[nr + 1];
        for (int r = 0; r < nr; r++)
            rowY[r + 1] = rowY[r] + rows[r].Count + ((rows[r].Flags & 0x80) != 0 ? 1 : 0);
        int sourceRows = rowY[nr] + 2;
        for (int r = 0; r < sourceRows; r++)
        {
            ReadByte(srcX + r * srcPitch);
            ReadByte(srcX + r * srcPitch + bytes - 1);
        }
        for (int r = 0; r < nr; r++)
        {
            ReadByte(dstRow + r * dstPitch);
            ReadByte(dstRow + r * dstPitch + nc * 4 - 1);
        }
        if (Accelerator is { } gpu
            && Scale32OnGpu(gpu, rows, rowY, cols, colFrom, width, srcX, srcPitch, dstRow, dstPitch, fullRow, fullCol))
            return true;
        // About two bands a core
        int Band = Math.Max(8, (nr + 2 * Environment.ProcessorCount - 1) / (2 * Environment.ProcessorCount));
        int bands = (nr + Band - 1) / Band;
        void RunBand(int band)
        {
            var line = new byte[bytes];
            // The whole source rows of a destination row: sums per channel (16 bits, as the MMX
            // sums wrap) and the AND of their bytes (byte 3 tells whether every one is opaque)
            var sum = new ushort[bytes];
            var and = new byte[bytes];
            var first = new ScaleRow(width);
            var whole = new ScaleRow(width);
            var last = new ScaleRow(width);
            var output = new byte[nc * 4];
            var accLo = new ulong[nc];
            var accHi = new ulong[nc];
            var clear = new int[nc];
            for (int r = band * Band, end = Math.Min(nr, r + Band); r < end; r++)
            {
                var e = rows[r];
                bool hasFirst = (e.Flags & 0x80) != 0, hasLast = (e.Flags & 0x40) != 0;
                int p = rowY[r];
                if (hasFirst)
                {
                    ReadBytes(srcX + p++ * srcPitch, line);
                    first.Set(line);
                }
                if (e.Count > 0)
                {
                    Array.Clear(sum);
                    Array.Fill(and, (byte)0xFF);
                    for (int k = 0; k < e.Count; k++)
                    {
                        ReadBytes(srcX + p++ * srcPitch, line);
                        AddRow(line, sum, and);
                    }
                    whole.Set(sum, and);
                }
                if (hasLast)
                {
                    ReadBytes(srcX + p * srcPitch, line);
                    last.Set(line);
                }

                // the general loop's register copy of the shared first column (see the top)
                bool ownLoop = e.Flags == 0xC0 && e.Count <= 1;
                Array.Clear(accLo);
                Array.Clear(accHi);
                Array.Clear(clear);
                if (hasFirst)
                    first.AddTo(e.First, fullCol, colWx, colShape, accLo, accHi, clear, skipShared: !ownLoop);
                if (e.Count > 0)
                    whole.AddTo(fullRow, fullCol, colWx, colShape, accLo, accHi, clear, skipShared: false);
                if (hasLast)
                    last.AddTo(e.Last, fullCol, colWx, colShape, accLo, accHi, clear, skipShared: !ownLoop);
                for (int c = 0; c < nc; c++)
                {
                    ulong lo = accLo[c], hi = accHi[c];
                    output[c * 4] = (byte)(lo >> 8);
                    output[c * 4 + 1] = (byte)(hi >> 8);
                    output[c * 4 + 2] = (byte)(lo >> 40);
                    output[c * 4 + 3] = clear[c] == 0 ? (byte)0xFF : (byte)(hi >> 40);
                }
                WriteBytes(dstRow + r * dstPitch, output);
            }
        }
        // Rows are independent: bands of them run on all cores (the result does not depend on it)
        if (bands > 1 && (long)nr * nc >= 20000)
            Parallel.For(0, bands, RunBand);
        else
            for (int band = 0; band < bands; band++)
                RunBand(band);
        return true;
    }

    /// <summary>
    /// One kind of source row of a destination row (the partial first row, the sum of the whole
    /// rows, the partial last row) as prefix sums over x: channels 0 and 2 in one 64-bit word,
    /// 1 and 3 in the other (32-bit lanes, values kept modulo 65536), and a count of the pixels
    /// that are not opaque.
    /// </summary>
    private sealed class ScaleRow(int width)
    {
        private const ulong Lanes = 0x0000FFFF0000FFFF;
        private readonly ulong[] m_lo = new ulong[width + 1], m_hi = new ulong[width + 1];
        private readonly int[] m_clear = new int[width + 1];

        public void Set(byte[] row)
        {
            ulong lo = 0, hi = 0;
            int clear = 0;
            for (int x = 0, i = 0; x < m_lo.Length - 1; x++, i += 4)
            {
                lo += row[i] | (ulong)row[i + 2] << 32;
                hi += row[i + 1] | (ulong)row[i + 3] << 32;
                clear += row[i + 3] == 0xFF ? 0 : 1;
                m_lo[x + 1] = lo;
                m_hi[x + 1] = hi;
                m_clear[x + 1] = clear;
            }
        }

        public void Set(ushort[] sum, byte[] and)
        {
            ulong lo = 0, hi = 0;
            int clear = 0;
            for (int x = 0, i = 0; x < m_lo.Length - 1; x++, i += 4)
            {
                lo += sum[i] | (ulong)sum[i + 2] << 32;
                hi += sum[i + 1] | (ulong)sum[i + 3] << 32;
                clear += and[i + 3] == 0xFF ? 0 : 1;
                m_lo[x + 1] = lo;
                m_hi[x + 1] = hi;
                m_clear[x + 1] = clear;
            }
        }

        /// <summary>
        /// Every destination column's parts of this row, each times its weight with the row
        /// weight wy, into accLo / accHi (mod 65536 a lane); counts the pixels that are not
        /// opaque. With skipShared the shared first column of every column but the first is left
        /// out of the count (the general loop's register copy).
        /// </summary>
        public void AddTo(uint wy, uint fullCol, uint[] colWx, int[] shape, ulong[] accLo, ulong[] accHi, int[] clear, bool skipShared)
        {
            ulong[] lo = m_lo, hi = m_hi;
            int[] cl = m_clear;
            uint wWhole = ScaleWeight(fullCol, wy);
            for (int c = 0, n = accLo.Length; c < n; c++)
            {
                int f = shape[4 * c], a = shape[4 * c + 1], b = shape[4 * c + 2], flags = shape[4 * c + 3];
                ulong l = accLo[c], h = accHi[c];
                int nonOpaque = 0;
                if ((flags & 0x80) != 0)
                {
                    uint w = ScaleWeight(colWx[2 * c], wy);
                    l += (lo[f + 1] - lo[f]) * w;
                    h += (hi[f + 1] - hi[f]) * w;
                    if (!(skipShared && c > 0))
                        nonOpaque += cl[f + 1] - cl[f];
                }
                if (b > a)
                {
                    l += ((lo[b] - lo[a]) & Lanes) * wWhole;
                    h += ((hi[b] - hi[a]) & Lanes) * wWhole;
                    nonOpaque += cl[b] - cl[a];
                }
                if ((flags & 0x40) != 0)
                {
                    uint w = ScaleWeight(colWx[2 * c + 1], wy);
                    l += (lo[b + 1] - lo[b]) * w;
                    h += (hi[b + 1] - hi[b]) * w;
                    nonOpaque += cl[b + 1] - cl[b];
                }
                accLo[c] = l & Lanes;
                accHi[c] = h & Lanes;
                clear[c] += nonOpaque;
            }
        }
    }

    /// <summary>Adds a row of bytes to 16-bit sums (wrapping) and ANDs it into a mask, with SIMD.</summary>
    private static void AddRow(byte[] row, ushort[] sum, byte[] and)
    {
        int i = 0;
        int n = System.Numerics.Vector<byte>.Count;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            for (; i + n <= row.Length; i += n)
            {
                var v = new System.Numerics.Vector<byte>(row, i);
                System.Numerics.Vector.Widen(v, out var lo, out var hi);
                (new System.Numerics.Vector<ushort>(sum, i) + lo).CopyTo(sum, i);
                (new System.Numerics.Vector<ushort>(sum, i + n / 2) + hi).CopyTo(sum, i + n / 2);
                (new System.Numerics.Vector<byte>(and, i) & v).CopyTo(and, i);
            }
        }
        for (; i < row.Length; i++)
        {
            sum[i] += row[i];
            and[i] &= row[i];
        }
    }
}
