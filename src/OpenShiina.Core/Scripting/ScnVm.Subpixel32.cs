// Oreimo START 76388, written out: copies a rectangle of a 32-bit picture into another with
// positions in 1/16 pixels and no scaling (the MMX path of the scripts' smooth scrolling).
//
// With a / b the distances (in 1/16) from the destination / source position to the next whole
// pixel, the weights are D0 = (b - a) mod 16 and D4 = (a - b) mod 16 across, D8 / DC down, so
// a destination pixel mixes up to 2 x 2 source pixels. The x86 code has a loop per kind of
// destination row - partly covered top row, whole rows, partly covered bottom row, each from
// one source row (when the vertical fractions line up) or two - and per row the partly covered
// left pixel, the whole pixels and the partly covered right pixel; edge pixels scale their
// alpha lane by the covered fraction. Every formula is the MMX one in 16-bit lanes (products and
// sums wrap, >> 4 or >> 8, packuswb), including three quirks of the x86 code: on rows from two
// source rows the edge pixels whose source starts inside a pixel are stored as two unpacked
// 16-bit lanes (no packuswb), and the left one also adds one of its products unshifted.

using System.Runtime.Intrinsics;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>Four 16-bit lanes (16, 16, 16, alpha).</summary>
    private static ulong Lanes4(uint w, uint alpha) => w | (ulong)w << 16 | (ulong)w << 32 | (ulong)alpha << 48;

    /// <summary>
    /// START 76388 written out (see the top of the file). Positions and sizes are in 1/16
    /// pixels. False when the x86 code would not end (less than one whole pixel across or down).
    /// </summary>
    public bool Subpixel32(int dst, int dstPitch, int dtx, int dty, int w, int h, int src, int srcPitch, int stx, int sty)
    {
        if (dtx < 0 || dty < 0 || stx < 0 || sty < 0 || w < 0 || h < 0)
            return true;
        uint ax = (uint)(-dtx) & 15, bx = (uint)(-stx) & 15, ay = (uint)(-dty) & 15, by = (uint)(-sty) & 15;
        uint d0 = (bx - ax) & 15, d4 = (ax - bx) & 15, d8 = (by - ay) & 15, dc = (ay - by) & 15;
        uint middle = (uint)(w - ax) >> 4, tailX = (uint)(w - ax) & 15, srcTailX = (uint)(w - bx) & 15;
        uint rowsMiddle = (uint)(h - ay) >> 4, tailY = (uint)(h - ay) & 15, srcTailY = (uint)(h - by) & 15;
        // The x86 loops count down from these without a check for 0
        if (middle == 0 || rowsMiddle == 0 || middle > 0x10000 || rowsMiddle > 0x10000)
            return false;

        var k = new SubpixelWeights
        {
            AX = ax, BX = bx, TailX = tailX, SrcTailX = srcTailX, Copy = d0 == 0,
            L70 = Lanes4(16, ax), L78 = Lanes4(16, ay), L80 = Lanes4(16, tailX), L88 = Lanes4(16, tailY),
            L50 = Lanes4(d0, bx), L58 = Lanes4(d8, by), L60 = Lanes4(d4, srcTailX), L68 = Lanes4(dc, srcTailY),
            B0 = Lanes4(d0, d0), B8 = Lanes4(d4, d4), C0 = Lanes4(d8, d8), C8 = Lanes4(dc, dc),
            D0 = (ushort)d0, D4 = (ushort)d4, D8 = (ushort)d8, DC = (ushort)dc,
        };

        // Rows: (kind, source row, destination row)
        var rows = new List<(SubpixelRow Kind, int Source, int Target)>();
        int srcRow = src + (int)((uint)sty >> 4) * srcPitch + (int)((uint)stx >> 4) * 4;
        int dstRow = dst + (int)((uint)dty >> 4) * dstPitch + (int)((uint)dtx >> 4) * 4;
        if (ay != 0)
        {
            if (ay > by)
            {
                rows.Add((SubpixelRow.TopTwo, srcRow, dstRow));
                srcRow += srcPitch;
            }
            else
            {
                rows.Add((SubpixelRow.TopOne, srcRow, dstRow));
                if (ay == by)
                    srcRow += srcPitch;
            }
            dstRow += dstPitch;
        }
        var middleKind = ay == by ? SubpixelRow.MiddleOne : SubpixelRow.MiddleTwo;
        for (uint r = 0; r < rowsMiddle; r++)
        {
            rows.Add((middleKind, srcRow, dstRow));
            srcRow += srcPitch;
            dstRow += dstPitch;
        }
        if (tailY != 0)
            rows.Add((tailY > srcTailY ? SubpixelRow.BottomTwo : SubpixelRow.BottomOne, srcRow, dstRow));

        int outPixels = (ax != 0 ? 1 : 0) + (int)middle + (tailX != 0 ? 1 : 0);
        int inBytes = ((int)middle + 3) * 4;
        // The pages exist before the rows are shared out between threads
        foreach (var (_, s, d) in rows)
        {
            ReadByte(s);
            ReadByte(s + inBytes - 1);
            ReadByte(s + srcPitch);
            ReadByte(s + srcPitch + inBytes - 1);
            ReadByte(d);
            ReadByte(d + outPixels * 4 - 1);
        }
        int band = Math.Max(8, (rows.Count + 2 * Environment.ProcessorCount - 1) / (2 * Environment.ProcessorCount));
        int bands = (rows.Count + band - 1) / band;
        void RunBand(int index)
        {
            var p = new byte[inBytes];
            var q = new byte[inBytes];
            var output = new byte[outPixels * 4];
            for (int r = index * band, end = Math.Min(rows.Count, r + band); r < end; r++)
            {
                var (kind, s, d) = rows[r];
                ReadBytes(s, p);
                if (kind is SubpixelRow.TopTwo or SubpixelRow.MiddleTwo or SubpixelRow.BottomTwo)
                    ReadBytes(s + srcPitch, q);
                SubpixelRowPixels(kind, k, p, q, output, (int)middle);
                WriteBytes(d, output);
            }
        }
        if (bands > 1 && (long)rows.Count * outPixels >= 20000)
            Parallel.For(0, bands, RunBand);
        else
            for (int i = 0; i < bands; i++)
                RunBand(i);
        return true;
    }

    private enum SubpixelRow { TopOne, TopTwo, MiddleOne, MiddleTwo, BottomOne, BottomTwo }

    private sealed class SubpixelWeights
    {
        public uint AX, BX, TailX, SrcTailX;
        public bool Copy;
        public ulong L70, L78, L80, L88, L50, L58, L60, L68, B0, B8, C0, C8;
        public ushort D0, D4, D8, DC;
    }

    private static ulong Px(byte[] row, int at) => X86Ops.Punpcklbw(BitConverter.ToUInt32(row, at), 0);

    private static ulong Mul(ulong a, ulong b) => X86Ops.Pmullw(a, b);

    private static ulong Add(ulong a, ulong b) => X86Ops.Paddw(a, b);

    private static void StorePacked(byte[] output, int at, ulong v) =>
        BitConverter.TryWriteBytes(output.AsSpan(at), (uint)X86Ops.Packuswb(v, 0));

    /// <summary>movd without packuswb: lanes 0 and 1 as two 16-bit words.</summary>
    private static void StoreWords(byte[] output, int at, ulong v) =>
        BitConverter.TryWriteBytes(output.AsSpan(at), (uint)v);

    /// <summary>One destination row: left edge, whole pixels, right edge.</summary>
    private static void SubpixelRowPixels(SubpixelRow kind, SubpixelWeights k, byte[] p, byte[] q, byte[] output, int middle)
    {
        bool two = kind is SubpixelRow.TopTwo or SubpixelRow.MiddleTwo or SubpixelRow.BottomTwo;
        // Vertical weights of the edges of rows from two source rows; lane weight of one-row edges
        ulong wyP = kind switch { SubpixelRow.TopTwo => k.L58, _ => k.C0 };
        ulong wyQ = kind switch { SubpixelRow.BottomTwo => k.L68, _ => k.C8 };
        ulong v = kind == SubpixelRow.TopOne ? k.L78 : k.L88;
        int s = 0, o = 0;

        // The partly covered left pixel
        if (k.AX != 0)
        {
            if (k.AX <= k.BX)
            {
                ulong x = two
                    ? Mul(Add(Mul(Px(p, s), wyP), Mul(Px(q, s), wyQ)), k.L70)
                    : kind == SubpixelRow.MiddleOne ? Mul(Px(p, s), k.L70) : Mul(Mul(Px(p, s), k.L70), v);
                StorePacked(output, o, X86Ops.Psrlw(x, two || kind != SubpixelRow.MiddleOne ? 8ul : 4ul));
                if (k.AX == k.BX)
                    s += 4;
            }
            else if (two)
            {
                ulong m1 = Mul(Mul(Px(q, s), k.L50), wyQ);
                ulong x = Add(Add(Mul(Mul(Px(p, s), k.L50), wyP), m1),
                              Add(Mul(Mul(Px(p, s + 4), k.B8), wyP), Mul(Mul(Px(q, s + 4), k.B8), wyQ)));
                StoreWords(output, o, Add(X86Ops.Psrlw(x, 8), m1));
                s += 4;
            }
            else
            {
                ulong x = Add(Mul(Px(p, s), k.L50), Mul(Px(p, s + 4), k.B8));
                if (kind == SubpixelRow.MiddleOne)
                    StorePacked(output, o, X86Ops.Psrlw(x, 4));
                else
                    StorePacked(output, o, X86Ops.Psrlw(Mul(x, v), 8));
                s += 4;
            }
            o += 4;
        }

        // The whole pixels
        if (two)
        {
            if (k.Copy)
                MiddleVertical(p, q, s, output, o, middle, k.D8, k.DC);
            else
                MiddleBilinear(p, q, s, output, o, middle, k);
        }
        else if (k.Copy)
            Array.Copy(p, s, output, o, middle * 4);
        else
            MiddleAcross(p, s, output, o, middle, k.D0, k.D4);
        s += middle * 4;
        o += middle * 4;

        // The partly covered right pixel
        if (k.TailX != 0)
        {
            if (k.TailX <= k.SrcTailX)
            {
                ulong x = two
                    ? Mul(Add(Mul(Px(p, s), wyP), Mul(Px(q, s), wyQ)), k.L80)
                    : kind == SubpixelRow.MiddleOne ? Mul(Px(p, s), k.L80) : Mul(Mul(Px(p, s), k.L80), v);
                StorePacked(output, o, X86Ops.Psrlw(x, two || kind != SubpixelRow.MiddleOne ? 8ul : 4ul));
            }
            else if (two)
            {
                ulong x = Add(Add(Mul(Mul(Px(p, s), k.B0), wyP), Mul(Mul(Px(q, s), k.B0), wyQ)),
                              Add(Mul(Mul(Px(p, s + 4), k.L60), wyP), Mul(Mul(Px(q, s + 4), k.L60), wyQ)));
                StoreWords(output, o, X86Ops.Psrlw(x, 8));
            }
            else
            {
                ulong x = Add(Mul(Px(p, s), k.B0), Mul(Px(p, s + 4), k.L60));
                if (kind == SubpixelRow.MiddleOne)
                    StorePacked(output, o, X86Ops.Psrlw(x, 4));
                else
                    StorePacked(output, o, X86Ops.Psrlw(Mul(x, v), 8));
            }
        }
    }

    // The whole pixels with SIMD: 16-bit lanes, products wrap like pmullw, two pixels at a time.

    private static Vector128<ushort> Load2(byte[] row, int at) =>
        Vector128.WidenLower(Vector128.CreateScalar(BitConverter.ToUInt64(row, at)).AsByte());

    private static void Store2(byte[] output, int at, Vector128<ushort> v, int shift)
    {
        v = Vector128.ShiftRightLogical(v, shift);
        if (shift < 8)
            v = Vector128.Min(v, Vector128.Create((ushort)255));
        ulong bytes = Vector128.Narrow(v, v).AsUInt64().ToScalar();
        BitConverter.TryWriteBytes(output.AsSpan(at), bytes);
    }

    /// <summary>(p[i] D0 + p[i+1] D4) >> 4, packed (one source row).</summary>
    private static void MiddleAcross(byte[] p, int s, byte[] output, int o, int n, ushort d0, ushort d4)
    {
        var w0 = Vector128.Create(d0);
        var w1 = Vector128.Create(d4);
        int i = 0;
        for (; i + 2 <= n; i += 2)
            Store2(output, o + i * 4, Load2(p, s + i * 4) * w0 + Load2(p, s + i * 4 + 4) * w1, 4);
        for (; i < n; i++)
        {
            ulong x = Add(Mul(Px(p, s + i * 4), Lanes4(d0, d0)), Mul(Px(p, s + i * 4 + 4), Lanes4(d4, d4)));
            StorePacked(output, o + i * 4, X86Ops.Psrlw(x, 4));
        }
    }

    /// <summary>(p[i] D8 + q[i] DC) >> 4, packed (two source rows lined up across).</summary>
    private static void MiddleVertical(byte[] p, byte[] q, int s, byte[] output, int o, int n, ushort d8, ushort dc)
    {
        var w0 = Vector128.Create(d8);
        var w1 = Vector128.Create(dc);
        int i = 0;
        for (; i + 2 <= n; i += 2)
            Store2(output, o + i * 4, Load2(p, s + i * 4) * w0 + Load2(q, s + i * 4) * w1, 4);
        for (; i < n; i++)
        {
            ulong x = Add(Mul(Px(p, s + i * 4), Lanes4(d8, d8)), Mul(Px(q, s + i * 4), Lanes4(dc, dc)));
            StorePacked(output, o + i * 4, X86Ops.Psrlw(x, 4));
        }
    }

    /// <summary>(p[i] D0 D8 + p[i+1] D4 D8 + q[i] D0 DC + q[i+1] D4 DC) >> 8, packed.</summary>
    private static void MiddleBilinear(byte[] p, byte[] q, int s, byte[] output, int o, int n, SubpixelWeights k)
    {
        // The weights are products in 16 bits too (pmullw of the broadcast fractions)
        ushort a = (ushort)(k.D0 * k.D8), b = (ushort)(k.D4 * k.D8), c = (ushort)(k.D0 * k.DC), d = (ushort)(k.D4 * k.DC);
        var wa = Vector128.Create(a);
        var wb = Vector128.Create(b);
        var wc = Vector128.Create(c);
        var wd = Vector128.Create(d);
        int i = 0;
        for (; i + 2 <= n; i += 2)
        {
            int at = s + i * 4;
            Store2(output, o + i * 4, Load2(p, at) * wa + Load2(p, at + 4) * wb + Load2(q, at) * wc + Load2(q, at + 4) * wd, 8);
        }
        for (; i < n; i++)
        {
            int at = s + i * 4;
            ulong x = Add(Add(Mul(Px(p, at), Lanes4(a, a)), Mul(Px(p, at + 4), Lanes4(b, b))),
                          Add(Mul(Px(q, at), Lanes4(c, c)), Mul(Px(q, at + 4), Lanes4(d, d))));
            StorePacked(output, o + i * 4, X86Ops.Psrlw(x, 8));
        }
    }
}
