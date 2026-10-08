// The compositor's other modes, as the MMX + SSE path draws them (0x4409E0; checked byte for byte
// against the executable's code on the x86 interpreter):
//   bits 28-30 with bit 28 (0x10, 0x30, 0x50, 0x70): adds the sprite to the picture (0x442930)
//   0x60000000: paints the tint colour through the sprite's shape (0x442070)
//   0x08000000: copies the sprite, alpha left out (0x442E8B)
//   0x04000000: draws the sprite's alpha as grey (0x4431C0)

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private static byte AddSaturated(int d, int v) => (byte)Math.Min(255, d + v);

    /// <summary>
    /// Mode 0x10000000 and the others with bit 28 (0x442930): d + T1[c], saturated. BGR runs of
    /// 5 pixels or more take MMX for whole 8-byte groups from an 8-byte aligned byte:
    /// (c * a &amp; 0xFFFF) >> 8, which can differ from the table.
    /// </summary>
    private static void DrawRunAdd(int method, int n, byte[] src, int s, byte[] row, int d, int address, SpriteBlend blend)
    {
        byte[] t1 = blend.T1;
        switch (method)
        {
            case 2:
            {
                int bytes = n * 3, k = 0;
                if (n >= 5)
                {
                    for (; ((address + k) & 7) != 0; k++)
                        row[d + k] = AddSaturated(row[d + k], t1[src[s + k]]);
                    int a = blend.Alpha;
                    for (int end = k + ((bytes - k) & ~7); k < end; k++)
                        row[d + k] = AddSaturated(row[d + k], (src[s + k] * a & 0xFFFF) >> 8);
                }
                for (; k < bytes; k++)
                    row[d + k] = AddSaturated(row[d + k], t1[src[s + k]]);
                return;
            }
            case 3:
            {
                // (the MMX groups add the same bytes)
                int b = t1[src[s]], g = t1[src[s + 1]], r = t1[src[s + 2]];
                for (int i = 0; i < n; i++, d += 3)
                {
                    row[d] = AddSaturated(row[d], b);
                    row[d + 1] = AddSaturated(row[d + 1], g);
                    row[d + 2] = AddSaturated(row[d + 2], r);
                }
                return;
            }
            case 4:
                for (int i = 0; i < n; i++, s += 4, d += 3)
                {
                    int alpha = src[s];
                    for (int k = 0; k < 3; k++)
                        row[d + k] = AddSaturated(row[d + k], t1[src[s + 1 + k]] * alpha >> 8);
                }
                return;
            default:
            {
                int alpha = src[s];
                int b = t1[src[s + 1]] * alpha >> 8, g = t1[src[s + 2]] * alpha >> 8, r = t1[src[s + 3]] * alpha >> 8;
                for (int i = 0; i < n; i++, d += 3)
                {
                    row[d] = AddSaturated(row[d], b);
                    row[d + 1] = AddSaturated(row[d + 1], g);
                    row[d + 2] = AddSaturated(row[d + 2], r);
                }
                return;
            }
        }
    }

    /// <summary>Mode 0x08000000 (0x442E8B): the colours, alpha left out. One-colour ABGR runs write blue, green, blue.</summary>
    private static void DrawRunOpaque(int method, int n, byte[] src, int s, byte[] row, int d)
    {
        switch (method)
        {
            case 2:
                Array.Copy(src, s, row, d, n * 3);
                return;
            case 3:
                for (int i = 0; i < n; i++, d += 3)
                {
                    row[d] = src[s];
                    row[d + 1] = src[s + 1];
                    row[d + 2] = src[s + 2];
                }
                return;
            case 4:
                for (int i = 0; i < n; i++, s += 4, d += 3)
                {
                    row[d] = src[s + 1];
                    row[d + 1] = src[s + 2];
                    row[d + 2] = src[s + 3];
                }
                return;
            default:
                for (int i = 0; i < n; i++, d += 3)
                {
                    row[d] = src[s + 1];
                    row[d + 1] = src[s + 2];
                    row[d + 2] = src[s + 1];
                }
                return;
        }
    }

    /// <summary>Mode 0x04000000 (0x4431C0): the alpha as grey; colour runs are opaque (white).</summary>
    private static void DrawRunMask(int method, int n, byte[] src, int s, byte[] row, int d)
    {
        switch (method)
        {
            case 2:
            case 3:
                Array.Fill(row, (byte)0xFF, d, n * 3);
                return;
            case 4:
                for (int i = 0; i < n; i++, s += 4, d += 3)
                    row[d] = row[d + 1] = row[d + 2] = src[s];
                return;
            default:
                for (int i = 0; i < n; i++, d += 3)
                    row[d] = row[d + 1] = row[d + 2] = src[s];
                return;
        }
    }

    /// <summary>
    /// One row of mode 0x60000000 (0x442070): the tint colour t through the sprite's shape. ABGR
    /// runs draw at once, d + ((t - d) * T1[alpha] >> 8). Opaque runs (BGR, methods 2 and 3) are
    /// only counted and drawn later (0x4424C0) where the next run that is not of method 1-3
    /// starts, or at the end of the row - after a method 1 run that is further on.
    /// </summary>
    private void DrawRowSilhouette(int p, int dst, int skip, int visible, SpriteBlend blend)
    {
        if (visible <= 0)
            return;
        byte[] src = ReadRow(p);
        int rowBytes = visible * 3;
        bool direct = TryDirect(dst, rowBytes, out byte[] row, out int d0);
        if (!direct)
        {
            row = RowTarget(rowBytes);
            d0 = 0;
            ReadBytes(dst, row.AsSpan(0, rowBytes));
        }
        int o = 2, d = d0, remaining = visible, pending = 0;
        // Left clip: the run that crosses the edge goes on from there
        while (skip > 0)
        {
            var (method, count, data) = ReadRun(src, p, o);
            o = RunEnd(method, count, data);
            skip -= count;
            if (skip > 0)
                continue;
            if (skip == 0)
                break;
            int part = -skip, start = count - part;
            int n = Math.Min(part, remaining);
            switch (method)
            {
                case 0:
                case 1:
                    d += part * 3;
                    remaining -= part;
                    break;
                case 2:
                case 3:
                    pending += n;
                    remaining -= n;
                    break;
                default:
                    PaintTint(method, n, src, method == 4 ? data + start * 4 : data, row, d, blend);
                    d += n * 3;
                    remaining -= n;
                    break;
            }
            break;
        }
        while (remaining > 0)
        {
            var (method, count, data) = ReadRun(src, p, o);
            o = RunEnd(method, count, data);
            if (pending > 0 && method is not (1 or 2 or 3))
            {
                PaintPending(pending, row, d, dst + d - d0, blend);
                d += pending * 3;
                pending = 0;
            }
            if (method < 2)
            {
                d += count * 3;
                remaining -= count;
                continue;
            }
            int n = Math.Min(count, remaining);
            if (method < 4)
                pending += n;
            else
            {
                PaintTint(method, n, src, data, row, d, blend);
                d += n * 3;
            }
            remaining -= n;
        }
        if (!direct)
            WriteBytes(dst, row.AsSpan(0, rowBytes));
        if (pending > 0)
        {
            // A method 1 run at the end can take the drawing past the row
            int at = dst + d - d0;
            if (d - d0 + pending * 3 <= rowBytes && direct)
                PaintPending(pending, row, d, at, blend);
            else
            {
                var bytes = ReadBytes(at, pending * 3);
                PaintPending(pending, bytes, 0, at, blend);
                WriteBytes(at, bytes);
            }
        }
    }

    /// <summary>ABGR runs of mode 0x60000000: each pixel's alpha gives the weight of the tint.</summary>
    private static void PaintTint(int method, int n, byte[] src, int s, byte[] row, int d, SpriteBlend blend)
    {
        int[] tint = blend.Tint;
        for (int i = 0; i < n; i++, d += 3)
        {
            int weight = blend.T1[src[s]];
            for (int k = 0; k < 3; k++)
                row[d + k] = (byte)(row[d + k] + ((tint[k] - row[d + k]) * weight >> 8));
            if (method == 4)
                s += 4;
        }
    }

    /// <summary>
    /// 0x4424C0: n opaque pixels of mode 0x60000000 at row[d] (memory address <paramref name="address"/>).
    /// Under 15 pixels, or before a 4-byte aligned pixel, T2[d] + T1[t]; then 4 pixels as three
    /// dwords of those sums (carries cross the bytes) to reach 8-byte alignment; whole groups of 8
    /// pixels in MMX, d + ((t - d) * a >> 8); then 4 pixels as dwords and the rest from the tables.
    /// </summary>
    private static void PaintPending(int n, byte[] row, int d, int address, SpriteBlend blend)
    {
        byte[] t2 = blend.T2;
        int[] part = blend.TintPart, tint = blend.Tint;
        int start = d;
        void Table(int pixels)
        {
            for (; pixels > 0; pixels--, d += 3)
            {
                row[d] = (byte)(t2[row[d]] + part[0]);
                row[d + 1] = (byte)(t2[row[d + 1]] + part[1]);
                row[d + 2] = (byte)(t2[row[d + 2]] + part[2]);
            }
        }
        void Dwords()
        {
            for (int j = 0; j < 3; j++, d += 4)
            {
                uint v = 0, add = 0;
                for (int k = 0; k < 4; k++)
                {
                    v |= (uint)t2[row[d + k]] << 8 * k;
                    add |= (uint)part[(j * 4 + k) % 3] << 8 * k;
                }
                BitConverter.TryWriteBytes(row.AsSpan(d), v + add);
            }
        }
        if (n < 15)
        {
            Table(n);
            return;
        }
        int head = address & 3;
        Table(head);
        n -= head;
        if ((address + d - start & 4) != 0)
        {
            Dwords();
            n -= 4;
        }
        int a = blend.Alpha;
        for (int groups = n >> 3; groups > 0; groups--)
            for (int q = 0; q < 24; q++, d++)
                row[d] = (byte)(row[d] + ((tint[q % 3] - row[d]) * a >> 8));
        n &= 7;
        if (n >= 4)
        {
            Dwords();
            n -= 4;
        }
        Table(n);
    }
}
