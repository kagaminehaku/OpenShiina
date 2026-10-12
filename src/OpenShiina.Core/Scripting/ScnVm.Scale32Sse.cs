// Bitch Nee-chan START C7C70 (Bitch Gakuen CA1E0, the same signature), written out: the SSE2 build
// of scale32 (ScnVm.Scale32.cs), which a START that will not run without SSE2 takes for its zooms
// and for the picture behind its menus (every frame there: 700 ms a call translated).
//
// The same plan as the MMX build: a table per destination row and per destination column
// (48-byte entries in the work buffers: flags, the count of whole source pixels, the weights of
// the partly covered first (0x80) and last (0x40) ones), then every destination pixel the sum of
// its source pixels times their weights. Here the weights and the sums are single floats:
//   - an axis's step is r = 2 * r0 - x * r0 * r0 with r0 = rcpps(x), x the source size (the
//     interpreter's rcpps is exact 1 / x); a partial weight is (float)v * r, the whole one
//     (float)d * r, d the destination size;
//   - a pixel's channels as floats, times (column weight * row weight), added up in the order
//     of the x86 code (rows in order, in a row the columns in order), then cvtps2dq (to nearest
//     even), packssdw, packuswb. Float sums depend on their order, so the order is kept.
// What the x86 code does that a plain reading would not:
//   - rows 0xC0 with no whole row or one (two or three source rows) have their own loops, which
//     go across the rows column by column; in the three-row loop the partial last column weighs
//     the first source row with the middle row's weight and the middle row with the first's;
//   - the first destination column of an axis that starts and ends inside one source pixel (a
//     weight equal to the rest) sets 0x20 in the loop counter instead of the flags, so the column
//     table runs on for 32 more entries (when its bit 5 was clear) and the entry after the last
//     whole destination column is one of those instead of the partial one made for it;
//   - a partial first column after the first is not read from memory in the first and last
//     source rows (and in the two- and three-row loops) but taken from a register holding the
//     previous column's partial last one: the same pixel when the previous column has one (it
//     always does in the tables seen), else whatever the register held: those run as x86 code.
// Rows are independent and run on several cores.

using System.Runtime.Intrinsics;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private readonly record struct SseScaleEntry(int Flags, uint Count, float First, float Last);

    /// <summary>The step of an axis: rcpps(x) (exact here) refined once by Newton's method, as the x86 code does it.</summary>
    private static float SseScaleStep(uint size)
    {
        float x = (int)size;
        float r0 = 1 / x;
        float t = x * r0 * r0;
        return r0 + r0 - t;
    }

    /// <summary>
    /// The table of one axis (C7C70-C7ECE for rows, C7ECE-C80BE for columns) into
    /// <paramref name="table"/> and how many of its entries the copy uses; false when the x86
    /// code would not end or would make counts of billions.
    /// </summary>
    private static bool SseScaleTable(uint s, uint d, uint sourceStart, uint destinationStart, uint destinationEnd, bool columns,
                                      float step, List<SseScaleEntry> table, out int used)
    {
        table.Clear();
        used = 0;
        const uint MaxCount = 1 << 20;
        uint esi = (sourceStart & 15) * d >> 4;
        uint lead = (0 - destinationStart) & 15;
        uint n = ((d - lead) >> 4) + (lead != 0 ? 1u : 0);
        if (n == 0 || n > 0x10000)
            return false;
        used = (int)n;
        uint loops = n;
        uint ebx = 0, ecx = 0, eax = 0;
        int dl = 0;
        float first = 0, last = 0;
        float Weight(uint v) => (int)v * step;

        if (lead == 0)
            goto General;
        ebx = lead * s >> 4;
        eax = (16 - (sourceStart & 15)) * d >> 4;
        if (ebx < eax)
            goto LastPart;
        if (ebx == eax)
        {
            if (esi == 0)
                goto WholeLoop;
            if (columns)
                loops |= 0x20;
            ebx = 0;
            goto FirstPart;
        }
        if (esi == 0)
            goto WholeLoop;
        if (ebx >= d)
            goto Rest;
        ebx -= eax;
        eax = d - esi;
        if (columns)
            dl |= 0x20;
        goto FirstPart;

    General:
        ecx = 0;
        dl = 0;
        ebx = s;
        if (esi == 0)
            goto WholeTest;
    Rest:
        eax = d;
        ebx += esi;
        ebx -= eax;
        eax -= esi;
    FirstPart:
        first = Weight(eax);
        dl |= 0x80;
        esi = 0;
    WholeTest:
        if (ebx < d)
            goto LastTest;
    WholeLoop:
        do
        {
            ebx -= d;
            if (++ecx > MaxCount)
                return false;
        }
        while (ebx >= d);
    LastTest:
        if (ebx == 0)
            goto Store;
    LastPart:
        last = Weight(ebx);
        dl |= 0x40;
        esi += ebx;
    Store:
        table.Add(new SseScaleEntry(dl, ecx, first, last));
        if (table.Count > 0x10100)
            return false;
        if (--loops != 0)
            goto General;

        // A partial last destination pixel: an entry after the others
        eax = destinationEnd & 15;
        if (eax == 0)
            return true;
        used++;
        ebx = eax * s >> 4;
        ecx = 0;
        dl = 0;
        if (esi != 0)
        {
            eax = d - esi;
            if (eax >= ebx)
            {
                first = Weight(ebx);
                ebx = 0;
            }
            else
            {
                first = Weight(eax);
                ebx -= eax;
            }
            dl |= 0x80;
        }
        while (ebx >= d)
        {
            ebx -= d;
            if (++ecx > MaxCount)
                return false;
        }
        if (ebx != 0)
        {
            last = Weight(ebx);
            dl |= 0x40;
        }
        table.Add(new SseScaleEntry(dl, ecx, first, last));
        return true;
    }

    private readonly List<SseScaleEntry> m_sseScaleRows = new(), m_sseScaleColumns = new();
    private int[] m_sseScaleColX = [], m_sseScaleRowY = [];

    /// <summary>
    /// START C7C70 written out (see the top of the file); positions in 1/16 pixels as
    /// <see cref="Scale32"/>'s. False for a case it does not take (the x86 code would not end, a
    /// value from a register the copy does not have, a destination that shares bytes with the
    /// source): the caller runs the x86 code.
    /// </summary>
    public bool Scale32Sse(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
    {
        uint dw = (uint)(dr - dl), dh = (uint)(db - dt), sw = (uint)(sr - sl), sh = (uint)(sb - st);
        if (dw == 0 || dh == 0 || sw == 0 || sh == 0)
            return true;
        if ((int)dw < 0 || (int)dh < 0 || (int)sw < 0 || (int)sh < 0)
            return false;
        float stepY = SseScaleStep(sh), stepX = SseScaleStep(sw);
        float wholeY = (int)dh * stepY, wholeX = (int)dw * stepX;
        float wholeXY = wholeX * wholeY;
        var rows = m_sseScaleRows;
        var cols = m_sseScaleColumns;
        if (!SseScaleTable(sh, dh, (uint)st, (uint)dt, (uint)db, columns: false, stepY, rows, out int nr)
            || !SseScaleTable(sw, dw, (uint)sl, (uint)dl, (uint)dr, columns: true, stepX, cols, out int nc))
            return false;

        // Each destination column's first source pixel; a partial first column after the first
        // is read from memory only when the previous column's partial last one is that pixel
        var colX = Scratch(ref m_sseScaleColX, nc + 1);
        int x = 0;
        for (int c = 0; c < nc; c++)
        {
            if (c > 0 && (cols[c].Flags & 0x80) != 0 && (cols[c - 1].Flags & 0x40) == 0)
                return false;
            colX[c] = x;
            x += (int)cols[c].Count + ((cols[c].Flags & 0x80) != 0 ? 1 : 0);
        }
        int width = x + ((cols[nc - 1].Flags & 0x40) != 0 ? 1 : 0);
        var rowY = Scratch(ref m_sseScaleRowY, nr + 1);
        int y = 0;
        for (int r = 0; r < nr; r++)
        {
            rowY[r] = y;
            y += (int)rows[r].Count + ((rows[r].Flags & 0x80) != 0 ? 1 : 0);
        }
        int height = y + ((rows[nr - 1].Flags & 0x40) != 0 ? 1 : 0);

        int srcX = src + (int)((uint)st >> 4) * srcPitch + (int)((uint)sl >> 4) * 4;
        int dstRow = dst + (int)((uint)dt >> 4) * dstPitch + (int)((uint)dl >> 4) * 4;
        // The pixels are written as they are made: a destination that shares bytes with the
        // source would change pixels still to be read
        long srcEnd = srcX + (long)(height - 1) * srcPitch + width * 4L, dstEnd = dstRow + (long)(nr - 1) * dstPitch + nc * 4L;
        if (srcPitch <= 0 || dstPitch <= 0 || srcX < dstEnd && dstRow < srcEnd)
            return false;

        void RunRows(int from, int to)
        {
            for (int r = from; r < to; r++)
            {
                var row = rows[r];
                var output = Bytes(dstRow + r * dstPitch, nc * 4);
                int top = srcX + rowY[r] * srcPitch;
                if (row.Flags == 0xC0 && row.Count <= 1)
                {
                    var r0 = Bytes(top, width * 4);
                    var r1 = Bytes(top + srcPitch, width * 4);
                    if (row.Count == 0)
                        TwoRows(r0, r1, row, output);
                    else
                        ThreeRows(r0, r1, Bytes(top + 2 * srcPitch, width * 4), row, output);
                    continue;
                }
                GeneralRow(top, row, output);
            }
        }

        // The general loops: the source rows one after the other, in each the pixel's columns
        void GeneralRow(int top, SseScaleEntry row, Span<byte> output)
        {
            // the destination row's source rows in one span, a row srcPitch bytes after the other
            int lines = ((row.Flags & 0x80) != 0 ? 1 : 0) + (int)row.Count + ((row.Flags & 0x40) != 0 ? 1 : 0);
            if (lines == 0)
            {
                for (int c = 0; c < nc; c++)
                    Store(output, c, Vector128<float>.Zero);
                return;
            }
            ReadOnlySpan<byte> block = Bytes(top, (lines - 1) * srcPitch + width * 4);
            float wholeFirst = wholeX * row.First, wholeLast = wholeX * row.Last;
            for (int c = 0; c < nc; c++)
            {
                var col = cols[c];
                var acc = Vector128<float>.Zero;
                int at = 0;
                if ((row.Flags & 0x80) != 0)
                {
                    acc = AddRow(acc, block[at..], colX[c], col, row.First, wholeFirst);
                    at += srcPitch;
                }
                for (uint k = 0; k < row.Count; k++, at += srcPitch)
                    acc = AddRow(acc, block[at..], colX[c], col, wholeY, wholeXY);
                if ((row.Flags & 0x40) != 0)
                    acc = AddRow(acc, block[at..], colX[c], col, row.Last, wholeLast);
                Store(output, c, acc);
            }
        }

        // Rows 0xC0 with no whole row: across the two rows, column by column
        void TwoRows(ReadOnlySpan<byte> r0, ReadOnlySpan<byte> r1, SseScaleEntry row, Span<byte> output)
        {
            float w0 = row.First, w1 = row.Last;
            var whole0 = Vector128.Create(wholeX * w0);
            var whole1 = Vector128.Create(wholeX * w1);
            for (int c = 0; c < nc; c++)
            {
                var col = cols[c];
                var acc = Vector128<float>.Zero;
                int p = colX[c];
                if ((col.Flags & 0x80) != 0)
                {
                    acc += Pixel(r0, p) * Vector128.Create(w0 * col.First);
                    acc += Pixel(r1, p) * Vector128.Create(col.First * w1);
                    p++;
                }
                for (uint k = 0; k < col.Count; k++, p++)
                {
                    acc += Pixel(r0, p) * whole0;
                    acc += Pixel(r1, p) * whole1;
                }
                if ((col.Flags & 0x40) != 0)
                {
                    acc += Pixel(r0, p) * Vector128.Create(w0 * col.Last);
                    acc += Pixel(r1, p) * Vector128.Create(col.Last * w1);
                }
                Store(output, c, acc);
            }
        }

        // Rows 0xC0 with one whole row: across the three rows, column by column (the partial
        // last column with the first two rows' weights swapped, as the x86 code has them)
        void ThreeRows(ReadOnlySpan<byte> r0, ReadOnlySpan<byte> r1, ReadOnlySpan<byte> r2, SseScaleEntry row, Span<byte> output)
        {
            float w0 = row.First, w2 = row.Last;
            var whole0 = Vector128.Create(wholeX * w0);
            var whole1 = Vector128.Create(wholeXY);
            var whole2 = Vector128.Create(wholeX * w2);
            for (int c = 0; c < nc; c++)
            {
                var col = cols[c];
                var acc = Vector128<float>.Zero;
                int p = colX[c];
                if ((col.Flags & 0x80) != 0)
                {
                    acc += Pixel(r0, p) * Vector128.Create(w0 * col.First);
                    acc += Pixel(r1, p) * Vector128.Create(wholeY * col.First);
                    acc += Pixel(r2, p) * Vector128.Create(col.First * w2);
                    p++;
                }
                for (uint k = 0; k < col.Count; k++, p++)
                {
                    acc += Pixel(r0, p) * whole0;
                    acc += Pixel(r1, p) * whole1;
                    acc += Pixel(r2, p) * whole2;
                }
                if ((col.Flags & 0x40) != 0)
                {
                    acc += Pixel(r0, p) * Vector128.Create(wholeY * col.Last);
                    acc += Pixel(r1, p) * Vector128.Create(w0 * col.Last);
                    acc += Pixel(r2, p) * Vector128.Create(col.Last * w2);
                }
                Store(output, c, acc);
            }
        }

        ParallelRows.For(nr, (long)nr * nc * 4, RunRows);
        return true;
    }

    /// <summary>One source row's part of a destination pixel: its first, whole and last columns.</summary>
    private static Vector128<float> AddRow(Vector128<float> acc, ReadOnlySpan<byte> line, int p, SseScaleEntry col, float wy, float wholeWeight)
    {
        if ((col.Flags & 0x80) != 0)
            acc += Pixel(line, p++) * Vector128.Create(col.First * wy);
        var whole = Vector128.Create(wholeWeight);
        for (uint k = 0; k < col.Count; k++)
            acc += Pixel(line, p++) * whole;
        if ((col.Flags & 0x40) != 0)
            acc += Pixel(line, p) * Vector128.Create(col.Last * wy);
        return acc;
    }

    /// <summary>A pixel's four bytes as floats (movd, punpcklbw, punpcklwd, cvtdq2ps).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Pixel(ReadOnlySpan<byte> line, int p)
    {
        uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(line[(p * 4)..]);
        var words = Vector128.WidenLower(Vector128.CreateScalarUnsafe(v).AsByte());
        return Vector128.ConvertToSingle(Vector128.WidenLower(words).AsInt32());
    }

    /// <summary>A sum into its pixel: cvtps2dq (to nearest even), packssdw, packuswb.</summary>
    private static void Store(Span<byte> output, int c, Vector128<float> v)
    {
        // the lanes by constant indices (by a variable one, each goes through memory where the
        // runtime keeps vectors there: Mono)
        output[c * 4] = StoreLane(v.GetElement(0));
        output[c * 4 + 1] = StoreLane(v.GetElement(1));
        output[c * 4 + 2] = StoreLane(v.GetElement(2));
        output[c * 4 + 3] = StoreLane(v.GetElement(3));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static byte StoreLane(float f)
    {
        int i = f >= -2147483648f && f < 2147483648f ? (int)MathF.Round(f, MidpointRounding.ToEven) : int.MinValue;
        return (byte)Math.Clamp(Math.Clamp(i, -32768, 32767), 0, 255);
    }
}
