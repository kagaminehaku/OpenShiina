// Ero-On! START 5DFA5, written out: the enlarging case of the scripts' zoom and pan in the first
// ShiinaRio v2.49 build (ScnVm.Scale16.cs is its scaling-down case). Positions are in 1/16
// pixels; each destination pixel mixes up to four source pixels.
//
// The x86 code makes a table of sW weights in the scripts' work buffer (l[12]): entry i is
// (i * 257) / sW as four 16-bit words. Across a row, x starts at dW - ((sl & 15) * sW >> 4):
// while x >= sW the source pixel is used as it is and x -= sW (at 0 the next source pixel comes,
// and x = dW); else it is mixed with the next one by the words of entries x and sW - x, and the
// next source pixel comes with x += dW - sW. Down the picture y works the same way with sH and dH:
// y >= sH takes one source row (y -= sH; at 0 the next row and y = dH), else two rows are mixed by
// (y << 8) / sH and ((sH - y) << 8) / sH (then the next row, y += dH - sH). Every product keeps
// its low 16 bits (pmullw), every sum wraps at 16 bits (paddw), and each mix ends with >> 8.
// Both walks are made first, so the rows can be drawn on all cores (or on the GPU).

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>A destination row of Enlarge16: its source row, and whether it mixes it with the next by Upper and Lower.</summary>
    private readonly record struct Enlarge16Row(int Source, bool Mixed, int Upper, int Lower);

    /// <summary>
    /// Ero-On! START 5DFA5: the rectangle (sl, st)-(sr, sb) of a 32-bit picture into (dl, dt)-(dr, db)
    /// of another, enlarging (1/16 pixels), with the weight table at <paramref name="table"/>.
    /// False when the rectangles are not ones it enlarges (the scripts then use the x86 code).
    /// </summary>
    public bool Enlarge16(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb, int table)
    {
        int dW = dr - dl, dH = db - dt, sW = sr - sl, sH = sb - st;
        if (dW == 0 || dH == 0 || sW == 0 || sH == 0)
            return true;
        if (sW < 0 || sH < 0 || dW < sW || dH < sH || dW > 0x7FFFFF || dH > 0x7FFFFF)
            return false;

        // The weight table, written as the x86 code writes it; read back with the entry past its
        // end (x = 0 when dW = sW reads it)
        var entries = new byte[(sW + 1) * 8];
        for (int i = 0; i < sW; i++)
        {
            ushort w = (ushort)(i * 257 / sW);
            for (int k = 0; k < 4; k++)
                BitConverter.TryWriteBytes(entries.AsSpan(i * 8 + k * 2), w);
        }
        WriteBytes(table, entries.AsSpan(0, sW * 8));
        ReadBytes(table + sW * 8, entries.AsSpan(sW * 8, 8));
        int Weight(int entry, int lane) => entries[entry * 8 + lane * 2] | entries[entry * 8 + lane * 2 + 1] << 8;

        // The walk across a row is the same for every row: per destination column the source
        // pixel, and whether it is mixed with the next one (by the words of entries x, sW - x)
        int columns = dW >> 4;
        var from = new int[columns];
        var mixLeft = new int[columns];     // -1: the pixel as it is
        var mixRight = new int[columns];
        int x = dW - (int)((uint)((sl & 15) * sW) >> 4), at = 0, width = 1;
        for (int c = 0; c < columns; c++)
        {
            from[c] = at;
            if (x < sW)
            {
                mixLeft[c] = x;
                mixRight[c] = sW - x;
                width = Math.Max(width, at + 2);
                at++;
                x += dW - sW;
            }
            else
            {
                mixLeft[c] = -1;
                width = Math.Max(width, at + 1);
                x -= sW;
                if (x == 0)
                {
                    at++;
                    x = dW;
                }
            }
        }
        // The two words a mixed column uses, per lane
        var left = new int[columns * 4];
        var right = new int[columns * 4];
        for (int c = 0; c < columns; c++)
            if (mixLeft[c] >= 0)
                for (int k = 0; k < 4; k++)
                {
                    left[c * 4 + k] = Weight(mixLeft[c], k);
                    right[c * 4 + k] = Weight(mixRight[c], k);
                }

        // The walk down the picture: per destination row its source row, one or two mixed by
        // (y << 8) / sH and ((sH - y) << 8) / sH
        var rows = new Enlarge16Row[dH >> 4];
        int y = dH - (int)((uint)((st & 15) * sH) >> 4), source = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            if (y >= sH)
            {
                rows[r] = new Enlarge16Row(source, false, 0, 0);
                y -= sH;
                if (y == 0)
                {
                    source++;
                    y = dH;
                }
            }
            else
            {
                rows[r] = new Enlarge16Row(source, true, (y << 8) / sH & 0xFFFF, ((sH - y) << 8) / sH & 0xFFFF);
                source++;
                y += dH - sH;
            }
        }

        int srcBase = src + (int)((uint)st >> 4) * srcPitch + (int)((uint)sl >> 4) * 4;
        int dstBase = dst + (int)((uint)dt >> 4) * dstPitch + (int)((uint)dl >> 4) * 4;
        var timing = ChooseGpu("enlarge16", (long)rows.Length * columns);
        if (timing.Gpu)
        {
            if (Enlarge16OnGpu(Accelerator!, rows, from, mixLeft, left, right, width, srcBase, srcPitch, dstBase, dstPitch))
            {
                GpuDone("enlarge16", timing);
                return true;
            }
            timing = GpuFailed("enlarge16");
        }
        void RunBand(int band)
        {
            var upper = new byte[width * 4];
            var lower = new byte[width * 4];
            var mixed = new int[width * 4];
            var output = new byte[columns * 4];
            for (int r = band * Enlarge16Band, end = Math.Min(rows.Length, r + Enlarge16Band); r < end; r++)
            {
                var row = rows[r];
                ReadBytes(srcBase + row.Source * srcPitch, upper);
                if (!row.Mixed)
                {
                    // One source row
                    for (int i = 0; i < mixed.Length; i++)
                        mixed[i] = upper[i];
                }
                else
                {
                    // Two source rows: (a * wa + b * wb) >> 8 in 16 bits
                    int wa = row.Upper, wb = row.Lower;
                    ReadBytes(srcBase + (row.Source + 1) * srcPitch, lower);
                    for (int i = 0; i < mixed.Length; i++)
                        mixed[i] = ((upper[i] * wa & 0xFFFF) + (lower[i] * wb & 0xFFFF) & 0xFFFF) >> 8;
                }
                for (int c = 0, o = 0; c < columns; c++, o += 4)
                {
                    int p = from[c] * 4;
                    if (mixLeft[c] < 0)
                    {
                        for (int k = 0; k < 4; k++)
                            output[o + k] = (byte)mixed[p + k];
                        continue;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        int sum = (mixed[p + k] * left[o + k] & 0xFFFF) + (mixed[p + 4 + k] * right[o + k] & 0xFFFF) & 0xFFFF;
                        output[o + k] = (byte)(sum >> 8);
                    }
                }
                WriteBytes(dstBase + r * dstPitch, output);
            }
        }
        // Rows are independent: bands of them run on all cores
        int bands = (rows.Length + Enlarge16Band - 1) / Enlarge16Band;
        if (bands > 1 && (long)rows.Length * columns >= 20000)
            Parallel.For(0, bands, RunBand);
        else
            for (int band = 0; band < bands; band++)
                RunBand(band);
        GpuDone("enlarge16", timing);
        return true;
    }

    private const int Enlarge16Band = 16;
}
