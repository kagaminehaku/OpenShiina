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

        // Per column: the source pixels it reads (relative to the start) with their weights
        int nc = cols.Count;
        var colStart = new int[nc];
        var colFirst = new bool[nc];
        var colParts = new List<(int Offset, uint Weight)>[nc];
        int x = 0, maxX = 0;
        for (int c = 0; c < nc; c++)
        {
            var e = cols[c];
            var parts = new List<(int, uint)>();
            int p = x;
            colStart[c] = x;
            if ((e.Flags & 0x80) != 0)
                parts.Add((p++, e.First));
            colFirst[c] = (e.Flags & 0x80) != 0;
            for (int k = 0; k < e.Count; k++)
                parts.Add((p++, fullCol));
            if ((e.Flags & 0x40) != 0)
                parts.Add((p++, e.Last));
            colParts[c] = parts;
            maxX = Math.Max(maxX, p);
            x += e.Count + ((e.Flags & 0x80) != 0 ? 1 : 0);
        }

        // Rows the same way; then every (row part, column part) weight once
        int srcX = src + (int)((uint)st >> 4) * srcPitch + (int)((uint)sl >> 4) * 4;
        int dstRow = dst + (int)((uint)dt >> 4) * dstPitch + (int)((uint)dl >> 4) * 4;
        var line = new byte[maxX * 4];
        var output = new byte[nc * 4];
        var sums = new ulong[nc * 2];
        var opaque = new bool[nc];
        int y = 0;
        foreach (var e in rows)
        {
            Array.Clear(sums);
            Array.Fill(opaque, true);
            int p = y;
            bool ownLoop = e.Flags == 0xC0 && e.Count <= 1;
            void AddRow(int row, uint wy, bool partial)
            {
                ReadBytes(srcX + row * srcPitch, line);
                for (int c = 0; c < nc; c++)
                {
                    ulong lo = 0, hi = 0;
                    bool all = opaque[c];
                    // the general loop's register copy of the shared first column (see the top)
                    bool skipFirst = partial && !ownLoop && c > 0 && colFirst[c];
                    foreach (var (offset, wx) in colParts[c])
                    {
                        uint w = ScaleWeight(wx, wy);
                        int o = offset * 4;
                        // channels 0 and 2 in one word, 1 and 3 in the other: 32-bit lanes
                        lo += (line[o] | (ulong)line[o + 2] << 32) * w;
                        hi += (line[o + 1] | (ulong)line[o + 3] << 32) * w;
                        if (skipFirst)
                            skipFirst = false;
                        else
                            all &= line[o + 3] == 0xFF;
                    }
                    sums[2 * c] = (sums[2 * c] + lo) & 0x0000FFFF0000FFFF;
                    sums[2 * c + 1] = (sums[2 * c + 1] + hi) & 0x0000FFFF0000FFFF;
                    opaque[c] = all;
                }
            }
            if ((e.Flags & 0x80) != 0)
                AddRow(p++, e.First, partial: true);
            for (int k = 0; k < e.Count; k++)
                AddRow(p++, fullRow, partial: false);
            if ((e.Flags & 0x40) != 0)
                AddRow(p, e.Last, partial: true);
            for (int c = 0; c < nc; c++)
            {
                ulong lo = sums[2 * c], hi = sums[2 * c + 1];
                output[c * 4] = (byte)(lo >> 8);
                output[c * 4 + 1] = (byte)(hi >> 8);
                output[c * 4 + 2] = (byte)(lo >> 40);
                output[c * 4 + 3] = opaque[c] ? (byte)0xFF : (byte)(hi >> 40);
            }
            WriteBytes(dstRow, output);
            dstRow += dstPitch;
            y += e.Count + ((e.Flags & 0x80) != 0 ? 1 : 0);
        }
        return true;
    }
}
