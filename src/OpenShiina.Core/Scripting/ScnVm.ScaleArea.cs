// Miko Dashi (Nakadashi Trilogy, v2.47) START 646B8, written out: the scaling-down case of the
// scripts' zoom and pan (case 2 of its START 19B52; case 1 is enlarge16, case 0 shift32). l[0]
// dst, l[1] its stride, l[2..5] dst left, top, right, bottom, l[6] src, l[7] its stride, l[8..11]
// src left, top, right, bottom (1/16 pixels), l[12] and l[13] work buffers for the weights of a
// column and of a row.
//
// With dW, dH, sW, sH the sizes in 1/16 pixels, the x86 code first writes the weights
// x[i] = i * 256 / sW (i = 0..dW) into l[12] and y[i] = i * 256 / sH (i = 0..dH) into l[13], each
// as four words. A source pixel counts dW units across (dH down) and a destination pixel takes
// sW (sH) of them: its first source pixel the part of it not taken by the pixel before
// (x[dW - r]), the whole ones after it x[dW], the last the units left (x[left]), which the next
// destination pixel starts in. The weight of a source pixel is (wx * wy & 0xFFFF) >> 8 and every
// channel the sum of byte * weight in 16 bits, >> 8 (MMX words: pmullw, paddw, psrlw).
//
// The x86 code takes one of four copies of its loops, by whether each axis is made smaller by
// half or more (2 * d <= s: its loops over whole pixels run at least once without a test) or
// less (they test first); the same sums. A destination larger than its source, or sizes that
// are not positive, run as x86 code. The same bytes as the x86 code (ScnTools routinecheck).
//
// Pure Love! START 5A163, Iyashinbo The Motion 5FBF3 and Gakushoku no Oba-san The Motion 60DA1
// (v2.47) are the same code but for the first byte of each pixel they write, made FF (por with
// 0xFF), as Royal Bouquet's enlarge16.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private void ScaleArea(ScnContext c, ScnNativeArgs a, bool opaque)
    {
        int dst = NativeArg(a, 0), dstStride = NativeArg(a, 1), dtx = NativeArg(a, 2), dty = NativeArg(a, 3);
        int dW = NativeArg(a, 4) - dtx, dH = NativeArg(a, 5) - dty;
        int src = NativeArg(a, 6), srcStride = NativeArg(a, 7), stx = NativeArg(a, 8), sty = NativeArg(a, 9);
        int sW = NativeArg(a, 10) - stx, sH = NativeArg(a, 11) - sty;
        int tableX = NativeArg(a, 12), tableY = NativeArg(a, 13);
        if (dW == 0 || dH == 0 || sW == 0 || sH == 0)
            return;
        int cols = dW >> 4, rows = dH >> 4;
        if (dW < 16 || dH < 16 || sW < dW || sH < dH || sW > 0x100000 || sH > 0x100000
            || dtx < 0 || dty < 0 || stx < 0 || sty < 0 || dstStride < cols * 4 || srcStride <= 0)
        {
            RunX86(c, a);
            return;
        }
        var wx = AreaWeights(dW, sW);
        var wy = AreaWeights(dH, sH);
        int start = src + (sty >> 4) * srcStride + (stx >> 4) * 4;
        int target = dst + (dty >> 4) * dstStride + (dtx >> 4) * 4;
        // the source rows and columns the destination takes: up to sH / dH + 1 rows past each
        // row's start, and as many of them as the destination has rows
        long srcRows = (long)rows * sH / dH + 2, srcBytes = ((long)cols * sW / dW + 2) * 4;
        var dstSpan = RowsSpan(target, dstStride, rows, cols * 4);
        if (!Apart(dstSpan, RowsSpan(start, srcStride, (int)Math.Min(srcRows, 0x100000), srcBytes))
            || !Apart(dstSpan, (tableX, (dW + 1) * 8L)) || !Apart(dstSpan, (tableY, (dH + 1) * 8L)))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegions(c, a, ((int)dstSpan.Start, (int)dstSpan.Length), (tableX, (dW + 1) * 8), (tableY, (dH + 1) * 8));
        WriteAreaWeights(tableX, wx);
        WriteAreaWeights(tableY, wy);

        ushort wholeX = wx[dW], wholeY = wy[dH];
        int rx0 = (int)((uint)((stx & 15) * dW) >> 4), ry = (int)((uint)((sty & 15) * dH) >> 4);
        var sum = new ushort[4];
        for (int row = 0; row < rows; row++, target += dstStride)
        {
            int rx = rx0, column = start, at = target;
            for (int col = 0; col < cols; col++, at += 4)
            {
                Array.Clear(sum);
                // the rows of the source this pixel takes
                int line = column, left = sH;
                if (ry != 0)
                {
                    AddRow(line, wy[dH - ry], rx);
                    left = sH - dH + ry;
                    line += srcStride;
                }
                for (; left >= dH; left -= dH, line += srcStride)
                    AddRow(line, wholeY, rx);
                if (left != 0)
                    AddRow(line, wy[left], rx);
                for (int k = 0; k < 4; k++)
                    WriteByte(at + k, (byte)(sum[k] >> 8));
                if (opaque)
                    WriteByte(at, 0xFF);
                // the next pixel's first column
                int across = sW;
                if (rx != 0)
                {
                    across = sW + rx - dW;
                    column += 4;
                }
                for (; across >= dW; across -= dW)
                    column += 4;
                rx = across;
            }
            // the next row's first source row
            int down = sH;
            if (ry != 0)
            {
                down = sH - dH + ry;
                start += srcStride;
            }
            for (; down >= dH; down -= dH)
                start += srcStride;
            ry = down;
        }

        // A row of the source from p with the row weight w, as the pixel takes it (its first
        // column rx units into the source pixel at p)
        void AddRow(int p, ushort w, int rx)
        {
            int units = sW;
            if (rx != 0)
            {
                Add(p, AreaWeight(wx[dW - rx], w));
                units = sW + rx - dW;
                p += 4;
            }
            if (units >= dW)
            {
                ushort whole = AreaWeight(wholeX, w);
                for (; units >= dW; units -= dW, p += 4)
                    Add(p, whole);
            }
            if (units != 0)
                Add(p, AreaWeight(wx[units], w));
        }

        void Add(int p, ushort w)
        {
            for (int k = 0; k < 4; k++)
                sum[k] = (ushort)(sum[k] + (ushort)(ReadByte(p + k) * w));
        }
    }

    /// <summary>i * 256 / s for i = 0..n, as the words the x86 code keeps.</summary>
    private static ushort[] AreaWeights(int n, int s)
    {
        var weights = new ushort[n + 1];
        for (int i = 0; i <= n; i++)
            weights[i] = (ushort)(i * 256 / s);
        return weights;
    }

    /// <summary>A table of weights as the x86 code writes it: each weight four times, a word each.</summary>
    private void WriteAreaWeights(int address, ushort[] weights)
    {
        var bytes = new byte[weights.Length * 8];
        for (int i = 0; i < weights.Length; i++)
            for (int k = 0; k < 4; k++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 8 + 2 * k), weights[i]);
        WriteBytes(address, bytes);
    }

    /// <summary>A pixel's weight from its column's and row's (pmullw, then psrlw 8).</summary>
    private static ushort AreaWeight(ushort x, ushort y) => (ushort)((ushort)(x * y) >> 8);
}
