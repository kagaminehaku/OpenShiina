// Re: Rem Plus START 866A8 (Maki Fes! 85B38, Oreimo 77108), written out: the enlarging case of the
// scripts' zoom and pan (scale32 is its scaling-down case, subpixel32 its copy without scaling).
// Positions are in 1/16 pixels; a destination pixel mixes up to 2 x 2 source pixels.
//
// The x86 code first makes a table in the scripts' work buffer (l[12]) of the destination columns
// that mix two source columns: across a row an accumulator starts at (16 - sl & 15) dW >> 4 and
// loses sW a destination pixel; while it covers sW the source pixel is used as it is, else the
// pixel mixes the current source pixel and the next, by (acc * 257) / sW and ((sW - acc) * 257) /
// sW (the reciprocal division of scale32), and the accumulator gains dW - sW. A partly covered
// first and last column have entries of their own, their alpha lane scaled by the part covered.
// Rows go the same way down with dH and sH: a partly covered top and bottom row, whole rows from
// one source row, or two mixed. Every formula is the MMX one in 16-bit lanes (pmullw, paddw, >> 8,
// packuswb), quirks kept: after a partly covered first column that used up its source pixel the
// rows from one source row take that pixel again (the next is read only after the pointer moved).
// The rows are drawn on all cores where the destination is apart from the source.

using static OpenShiina.Scripting.X86Ops;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>An entry of the column table: the weights of the current and the next source pixel, and the accumulator after it.</summary>
    private readonly record struct Enlarge32Column(ulong W0, ulong W1, int Count);

    private enum Enlarge32Row { Single, Two, WholeSingle, WholeTwo }

    /// <summary>A row of the destination: its kind, its first source row (and the next for two), its weights.</summary>
    private readonly record struct Enlarge32RowWork(Enlarge32Row Kind, int Source, ulong Weight, ulong Upper, ulong Lower, int Target);

    private const ulong Lanes0x101 = 0x0000_0101_0101_0101;

    private static ulong Broadcast(uint v)
    {
        ulong w = v & 0xFFFF;
        return w | w << 16 | w << 32 | w << 48;
    }

    /// <summary>
    /// The weights of a partly covered column or row that mixes two pixels: lanes 0-2 part * 257 / whole,
    /// lane 3 that times <paramref name="covered"/> (sixteenths) / whole >> 4, as the x86 code
    /// divides (64-bit dividends, 32-bit quotients).
    /// </summary>
    private static ulong PartWeight(uint part, uint whole, uint covered)
    {
        ulong product = (ulong)part * 257;
        uint q = (uint)(product / whole);
        uint alpha = (uint)((ulong)(uint)product * covered / whole) >> 4;
        return (q & 0xFFFFul) * 0x0000_0001_0001_0001 | (ulong)(alpha & 0xFFFF) << 48;
    }

    /// <summary>
    /// START 866A8: the rectangle (sl, st)-(sr, sb) of a 32-bit picture into (dl, dt)-(dr, db) of
    /// another, enlarging (1/16 pixels). False when the x86 code would not end (no whole column or
    /// row in the destination) or for sizes it does not take: the caller then runs the x86 code.
    /// </summary>
    public bool Enlarge32(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
    {
        int dW = dr - dl, dH = db - dt, sW = sr - sl, sH = sb - st;
        if (dW == 0 || dH == 0 || sW == 0 || sH == 0)
            return true;
        if (dW < 0 || dH < 0 || sW < 0 || sH < 0 || dl < 0 || dt < 0 || sl < 0 || st < 0)
            return false;
        int wholeColumns = (dr >> 4) - ((dl + 15) >> 4), wholeRows = (db >> 4) - ((dt + 15) >> 4);
        if (wholeColumns <= 0 || wholeRows <= 0 || wholeColumns > 0x10000 || wholeRows > 0x10000)
            return false;
        uint left = (uint)(16 - (dl & 15)), right = (uint)(dr & 15), top = (uint)(16 - (dt & 15)), bottom = (uint)(db & 15);
        int start = (int)((uint)((16 - (sl & 15)) * dW) >> 4);
        bool hasLeft = (left & 15) != 0, hasRight = (right & 15) != 0;

        // The column table, made as the x86 code makes it
        var columns = new ScaleDivider((uint)sW, 257);
        Enlarge32Column? leftColumn = null, rightColumn = null;
        bool leftTwo = false, rightTwo = false;
        var middle = new List<Enlarge32Column>();
        int ebp = start;
        if (hasLeft)
        {
            uint covered = (uint)(left * sW) >> 4;
            if ((uint)ebp >= covered)
            {
                leftColumn = new Enlarge32Column(Lanes0x101 | (ulong)(((257 * left) >> 4) & 0xFFFF) << 48, 0, (int)covered);
                ebp -= (int)covered;
                if (ebp == 0)
                    ebp = dW;
            }
            else
            {
                leftTwo = true;
                if (covered == 0)
                    return false;
                ulong w1 = PartWeight(covered - (uint)ebp, covered, left), w0 = PartWeight((uint)ebp, covered, left);
                ebp = ebp + dW - (int)covered;
                leftColumn = new Enlarge32Column(w0, w1, ebp);
            }
        }
        for (int c = 0; c < wholeColumns; c++)
        {
            if (ebp >= sW)
            {
                ebp -= sW;
                if (ebp == 0)
                    ebp = dW;
                continue;
            }
            middle.Add(new Enlarge32Column(Broadcast(columns.Divide((uint)ebp)), Broadcast(columns.Divide((uint)(sW - ebp))), 0));
            ebp += dW - sW;
        }
        if (hasRight)
        {
            uint covered = (uint)(right * sW) >> 4;
            if ((uint)ebp >= covered)
                rightColumn = new Enlarge32Column(Lanes0x101 | (ulong)(((257 * right) >> 4) & 0xFFFF) << 48, 0, 0);
            else
            {
                rightTwo = true;
                if (covered == 0)
                    return false;
                rightColumn = new Enlarge32Column(PartWeight((uint)ebp, covered, right), PartWeight(covered - (uint)ebp, covered, right), 0);
            }
        }

        // The rows, made as the x86 code goes down the picture
        var rowsDivider = new ScaleDivider((uint)sH, 257);
        var rows = new List<Enlarge32RowWork>();
        int srcRow = src + (st >> 4) * srcPitch + (sl >> 4) * 4;
        int dstRow = dst + (dt >> 4) * dstPitch + (dl >> 4) * 4;
        int acc = (int)((uint)((16 - (st & 15)) * dH) >> 4);
        bool PartRow(uint part, bool last)
        {
            uint covered = (uint)(part * sH) >> 4;
            if ((uint)acc >= covered)
            {
                rows.Add(new Enlarge32RowWork(Enlarge32Row.Single, srcRow, Lanes0x101 | (ulong)(((257 * part) >> 4) & 0xFFFF) << 48, 0, 0, dstRow));
                if (!last)
                {
                    acc -= (int)covered;
                    if (acc == 0)
                    {
                        acc = dH;
                        srcRow += srcPitch;
                    }
                }
            }
            else
            {
                if (covered == 0)
                    return false;
                rows.Add(new Enlarge32RowWork(Enlarge32Row.Two, srcRow, 0, PartWeight((uint)acc, covered, part), PartWeight(covered - (uint)acc, covered, part), dstRow));
                if (!last)
                {
                    srcRow += srcPitch;
                    acc = acc + dH - (int)covered;
                }
            }
            dstRow += dstPitch;
            return true;
        }
        if ((top & 15) != 0 && !PartRow(top, last: false))
            return false;
        for (int r = 0; r < wholeRows; r++)
        {
            if (acc >= sH)
            {
                acc -= sH;
                rows.Add(new Enlarge32RowWork(Enlarge32Row.WholeSingle, srcRow, 0, 0, 0, dstRow));
                if (acc == 0)
                {
                    srcRow += srcPitch;
                    acc = dH;
                }
            }
            else
            {
                rows.Add(new Enlarge32RowWork(Enlarge32Row.WholeTwo, srcRow, 0,
                    Broadcast(rowsDivider.Divide((uint)acc)), Broadcast(rowsDivider.Divide((uint)(sH - acc))), dstRow));
                srcRow += srcPitch;
                acc += dH - sH;
            }
            dstRow += dstPitch;
        }
        if ((bottom & 15) != 0 && !PartRow(bottom, last: true))
            return false;

        // The source pixels a row reads (and one more, as the walk reads the next of a pair), and
        // the destination pixels it writes
        int sourcePixels = (sW >> 4) + 8;
        int outPixels = (hasLeft ? 1 : 0) + wholeColumns + (hasRight ? 1 : 0);
        var table = new Enlarge32Table(leftColumn, leftTwo, middle.ToArray(), rightColumn, rightTwo, start, dW, sW, wholeColumns);

        // The rows on all cores where no row reads what another writes
        long srcFrom = rows.Count == 0 ? 0 : rows.Min(r => r.Source), srcTo = rows.Count == 0 ? 0 : rows.Max(r => r.Source) + srcPitch + sourcePixels * 4L;
        long dstFrom = rows.Count == 0 ? 0 : rows.Min(r => r.Target), dstTo = rows.Count == 0 ? 0 : rows.Max(r => r.Target) + outPixels * 4L;
        bool apart = srcTo <= dstFrom || dstTo <= srcFrom;
        void Rows(int from, int to)
        {
            // The thread's row buffers, kept while the sizes stay (the pixels are worked on as
            // dwords, so the rows are copied in and out: unaligned in script memory)
            if (t_enlarge32Rows is not { } buffers || buffers.Upper.Length != sourcePixels || buffers.Output.Length != outPixels)
                t_enlarge32Rows = buffers = (new uint[sourcePixels], new uint[sourcePixels], new uint[outPixels]);
            var (upper, lower, output) = buffers;
            var upperBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(upper.AsSpan());
            var lowerBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(lower.AsSpan());
            for (int r = from; r < to; r++)
            {
                var row = rows[r];
                ReadBytes(row.Source, upperBytes);
                if (row.Kind is Enlarge32Row.Two or Enlarge32Row.WholeTwo)
                    ReadBytes(row.Source + srcPitch, lowerBytes);
                Enlarge32Walk(table, row, upper, lower, output);
                WriteBytes(row.Target, System.Runtime.InteropServices.MemoryMarshal.AsBytes(output.AsSpan()));
            }
        }
        if (apart && rows.Count >= 16 && (long)rows.Count * outPixels >= 20000 && srcPitch > 0 && dstPitch > 0)
        {
            const int Band = 16;
            Parallel.For(0, (rows.Count + Band - 1) / Band, band => Rows(band * Band, Math.Min(rows.Count, (band + 1) * Band)));
        }
        else
            Rows(0, rows.Count);
        return true;
    }

    [ThreadStatic]
    private static (uint[] Upper, uint[] Lower, uint[] Output)? t_enlarge32Rows;

    private sealed record Enlarge32Table(Enlarge32Column? Left, bool LeftTwo, Enlarge32Column[] Middle, Enlarge32Column? Right, bool RightTwo,
                                         int Start, int DW, int SW, int WholeColumns);

    /// <summary>A destination row of Enlarge32: the walk across, as each kind of row of the x86 code goes.</summary>
    private static void Enlarge32Walk(Enlarge32Table t, Enlarge32RowWork row, uint[] upper, uint[] lower, uint[] output)
    {
        bool two = row.Kind is Enlarge32Row.Two or Enlarge32Row.WholeTwo;
        bool weighted = row.Kind == Enlarge32Row.Single;       // the partly covered rows from one source row
        ulong rowWeight = row.Weight, wUpper = row.Upper, wLower = row.Lower;
        // A source pixel as the row sees it: one row unpacked, or the two rows mixed (>> 8)
        ulong Pixel(int x) => two
            ? Psrlw(Paddw(Pmullw(Punpcklbw(upper[x], 0), wUpper), Pmullw(Punpcklbw(lower[x], 0), wLower)), 8)
            : Punpcklbw(upper[x], 0);
        // A mix of weighted pixels (>> 8), then the row's weight for the partly covered rows
        uint Out(ulong sum)
        {
            ulong v = Psrlw(sum, 8);
            if (weighted)
                v = Psrlw(Pmullw(v, rowWeight), 8);
            return (uint)Packuswb(v, 0);
        }
        uint Full(ulong p) => weighted ? (uint)Packuswb(Psrlw(Pmullw(p, rowWeight), 8), 0) : (uint)Packuswb(p, 0);

        int x = 0, o = 0, ebp = t.Start, m = 0;
        ulong cur = Pixel(0);
        if (t.Left is { } left)
        {
            if (!t.LeftTwo)
            {
                output[o++] = Out(Pmullw(left.W0, cur));
                ebp -= left.Count;
                if (ebp == 0)
                {
                    // the rows from one source row take the same pixel again, the mixed rows the next
                    if (two)
                        cur = Pixel(++x);
                    else
                        cur = Pixel(x++);
                    ebp = t.DW;
                }
            }
            else
            {
                ulong next = Pixel(x + 1);
                output[o++] = Out(Paddw(Pmullw(left.W0, cur), Pmullw(left.W1, next)));
                cur = next;
                x++;
                ebp = left.Count;
            }
        }
        bool reload = false;
        for (int c = t.WholeColumns; ; )
        {
            if (ebp >= t.SW)
            {
                output[o++] = Full(cur);
                ebp -= t.SW;
                if (ebp == 0)
                {
                    ebp = t.DW;
                    x++;
                    if (--c == 0)
                    {
                        reload = true;
                        break;
                    }
                    cur = Pixel(x);
                    continue;
                }
                if (--c == 0)
                    break;
                continue;
            }
            var e = t.Middle[m++];
            ulong next2 = Pixel(x + 1);
            output[o++] = Out(Paddw(Pmullw(e.W0, cur), Pmullw(e.W1, next2)));
            cur = next2;
            x++;
            ebp += t.DW - t.SW;
            if (--c == 0)
                break;
        }
        if (t.Right is { } right)
        {
            if (reload)
                cur = Pixel(x);
            if (!t.RightTwo)
                output[o] = Out(Pmullw(cur, right.W0));
            else
                output[o] = Out(Paddw(Pmullw(cur, right.W0), Pmullw(Pixel(x + 1), right.W1)));
        }
    }
}
