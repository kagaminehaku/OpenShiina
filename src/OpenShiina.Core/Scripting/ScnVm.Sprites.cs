// Sprite lists and the compositor. A list is a table of op_04B5 (32-byte entries) with a count
// (0x7DDB90[list]); op_04B8 picks the list the other opcodes work on. An entry is
//   [0] picture slot  [1] frame  [2] flags  [3] priority  [4] x  [5] y  [6] -  [7] extra (+0x1C)
// flags: bit 31 shown, bits 26-27 special modes, bits 28-30 the blend mode, bits 0-8 its alpha
// (ScnVm.SpriteModes.cs).
// op_04C4 draws a list into a surface (FUN_00412040 -> FUN_00439E10): every shown entry in
// increasing priority (unsigned), each one row by row through its S25 runs (FUN_0043A040 and the
// path for CPUs with MMX and SSE, 0x4409E0 - the one every current PC takes).

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private const int SpriteLists = 256, SpriteSize = 0x20;
    private readonly int[] m_spriteCounts = new int[SpriteLists];
    private int m_spriteList;

    private int SpriteEntry(int list, int index) => TableAddress(list) + index * SpriteSize;

    /// <summary>
    /// FUN_00439E10: draws count entries at <paramref name="sprites"/> into a 24-bit picture,
    /// clipped to <paramref name="clip"/> (left, top, right, bottom) or the whole picture.
    /// </summary>
    public void Compose(int sprites, int count, int pixels, int width, int height, int pitch, (int L, int T, int R, int B)? clip)
    {
        if (count == 0)
            return;
        int originX = 0, originY = 0, clipW = width, clipH = height;
        if (clip is { } r)
        {
            if (r.L >= width || r.T >= height || r.R <= 0 || r.B <= 0)
                return;
            int l = Math.Max(r.L, 0), t = Math.Max(r.T, 0);
            clipW = Math.Min(r.R, width) - l;
            clipH = Math.Min(r.B, height) - t;
            originX = l;
            originY = t;
            pixels += t * pitch + l * 3;
        }
        var target = new ComposeTarget(pixels, pitch, originX, originY, clipW, clipH);
        // In order of priority (unsigned), entries of the same priority in list order - the
        // order of FUN_00439E10's passes (each draws the current priority and finds the next).
        // Drawing does not change the entries, so they are gathered once and sorted.
        m_composeOrder.Clear();
        for (int i = 0; i < count; i++)
        {
            int e = sprites + i * SpriteSize;
            int flags = Read32(e + 8);
            if (flags >= 0 || !SpriteDrawable(e, ref flags))
                continue;
            m_composeOrder.Add(((ulong)(uint)Read32(e + 0xC) << 32 | (uint)i, flags));
        }
        m_composeOrder.Sort((x, y) => x.Key.CompareTo(y.Key));
        foreach (var (key, flags) in m_composeOrder)
            DrawSprite(sprites + (int)(uint)key * SpriteSize, flags, target);
    }

    private readonly List<(ulong Key, int Flags)> m_composeOrder = new();

    private readonly record struct ComposeTarget(int Pixels, int Pitch, int OriginX, int OriginY, int Width, int Height);

    /// <summary>The checks of FUN_00439E10 on an entry's flags; may turn a blend into a plain copy.</summary>
    private bool SpriteDrawable(int entry, ref int flags)
    {
        if ((flags & 0x0C000000) != 0)
            return true;
        int mode = flags & 0x70000000, alpha = flags & 0x1FF;
        switch (mode)
        {
            case 0:
                return true;
            case 0x10000000:
                return alpha != 0;
            case 0x40000000:
                if (alpha == 0)
                    return false;
                if (alpha >= 0x100)
                    flags &= unchecked((int)0x8FFFFFFF);
                return true;
            case 0x50000000:
            case 0x60000000:
            case 0x70000000:
                return alpha != 0;
            default:   // 0x20000000, 0x30000000
                if (alpha == 0)
                    flags &= unchecked((int)0x8FFFFFFF);
                return true;
        }
    }

    /// <summary>FUN_0043A040: clip the frame to the target, then draw its rows.</summary>
    private void DrawSprite(int entry, int flags, ComposeTarget target)
    {
        int picture = Picture(Read32(entry));
        uint frameIndex = (uint)Read32(entry + 4);
        if (picture == 0 || frameIndex >= (uint)Read32(picture + 4))
            return;
        int frame = Read32(picture + 8 + 4 * (int)frameIndex);
        if (frame == 0 || Read32(frame) == 0)
            return;
        int width = Read32(frame), rows = Read32(frame + 4);
        int dst = target.Pixels;
        int y = Read32(entry + 0x14) - target.OriginY + Read32(frame + 0xC);
        int bottom = rows + y;
        int row;
        if (y < 0)
        {
            if (bottom <= 0)
                return;
            rows += y;
            row = frame + 0x14 - y * 4;
        }
        else
        {
            if (y >= target.Height)
                return;
            dst += y * target.Pitch;
            row = frame + 0x14;
        }
        if (bottom - target.Height > 0)
            rows -= bottom - target.Height;

        int x = Read32(entry + 0x10) - target.OriginX + Read32(frame + 8);
        int right = width + x - 1;
        int skip, visible;
        if (x < 0)
        {
            if (right < 0)
                return;
            skip = -x;
            visible = right >= target.Width ? target.Width : width - skip;
        }
        else
        {
            skip = 0;
            if (x >= target.Width)
                return;
            dst += x * 3;
            visible = right >= target.Width ? width + target.Width - right - 1 : width;
        }

        // The modes as 0x4409E0 sends them on: bits 26-27 first (0x08000000 before 0x04000000),
        // then any mode with bit 28 adds, 0x40000000 blends, 0x60000000 paints the tint, and
        // 0x20000000 tints
        int alpha = flags & 0x1FF, tint = Read32(entry + 0x1C);
        SpriteBlend? blend = (flags & 0x0C000000) != 0
            ? (flags & 0x08000000) != 0 ? SpriteBlend.Opaque : SpriteBlend.Mask
            : (flags & 0x70000000) switch
            {
                0 => null,
                0x40000000 => new SpriteBlend(BlendKind.Alpha, alpha),
                0x20000000 => new SpriteBlend(BlendKind.Tint, Math.Min(alpha, 0x100), tint),
                0x60000000 => new SpriteBlend(BlendKind.Silhouette, Math.Min(alpha, 0x100), tint),
                0x10000000 => new SpriteBlend(BlendKind.Add, alpha),
                _ => new SpriteBlend(BlendKind.Add, Math.Min(alpha, 0x100)),
            };
        void DrawRow(int pointer, int at)
        {
            if (blend?.Kind == BlendKind.Silhouette)
                DrawRowSilhouette(pointer, at, skip, visible, blend);
            else
                DrawRowPlain(pointer, at, skip, visible, blend);
        }
        // Each row of a frame goes into its own destination row, so a large frame's rows are
        // shared out between the cores (the sprites themselves stay in order); the pages they
        // touch exist first, as the page table makes pages on first use
        if (rows >= 2 * ComposeBand && visible > 0 && target.Pitch >= visible * 3 && (long)rows * visible >= 20000)
        {
            var pointers = new int[rows];
            for (int i = 0; i < rows; i++)
            {
                int p = Read32(row + i * 4);
                pointers[i] = p;
                int end = p + Read16(p) + 16;
                for (int at = p; at < end; at += 0x4000)
                    ReadByte(at);
                ReadByte(end - 1);
                int d = dst + i * target.Pitch;
                ReadByte(d);
                ReadByte(d + visible * 3 - 1);
            }
            Parallel.For(0, (rows + ComposeBand - 1) / ComposeBand, band =>
            {
                for (int i = band * ComposeBand, last = Math.Min(rows, i + ComposeBand); i < last; i++)
                    DrawRow(pointers[i], dst + i * target.Pitch);
            });
            return;
        }
        for (; rows > 0; rows--, row += 4, dst += target.Pitch)
            DrawRow(Read32(row), dst);
    }

    private const int ComposeBand = 16;

    private enum BlendKind { Alpha, Tint, Add, Silhouette, Opaque, Mask }

    /// <summary>
    /// A blend mode and its tables (FUN_0044346A): for alpha a, T1 = a / 256 and T2 =
    /// (256 - a) / 256 stepped like FUN_004396B4 (0x492EC0 / 0x493EC0), MMX weights a and 256 - a.
    /// 0x10000000 keeps a up to 0x1FF; the other modes stop at 0x100.
    /// </summary>
    private sealed class SpriteBlend
    {
        public static readonly SpriteBlend Opaque = new(BlendKind.Opaque), Mask = new(BlendKind.Mask);

        public readonly BlendKind Kind;
        public readonly int Alpha;
        public readonly byte[] T1 = new byte[256], T2 = new byte[256];
        /// <summary>Modes 0x20000000 and 0x60000000: the tint colour (entry +0x1C: blue, green, red) and T1 of each.</summary>
        public readonly int[] Tint = new int[3], TintPart = new int[3];

        public SpriteBlend(BlendKind kind, int alpha = 0, int tint = 0)
        {
            Kind = kind;
            Alpha = alpha;
            if (kind is BlendKind.Opaque or BlendKind.Mask)
                return;
            StepTable(T1, alpha, 256);
            StepTable(T2, 256 - alpha, 256);
            Tint = [tint & 0xFF, (tint >> 8) & 0xFF, (tint >> 16) & 0xFF];
            TintPart = [T1[Tint[0]], T1[Tint[1]], T1[Tint[2]]];
        }
    }

    /// <summary>
    /// The runs of mode 0x20000000 (0x441710): the sprite's colours are mixed with the tint
    /// colour t, c' = T2[c] + T1[t] (byte tables of (256 - a) / 256 and a / 256), then drawn
    /// like plain runs. BGR runs of 15 pixels or more take MMX for whole groups of 8 pixels
    /// from a 4-byte aligned pixel (after one 4-pixel table block when it is not 8-byte
    /// aligned): c + ((t - c) * a >> 8) in 16-bit words, which can differ from the tables by 1.
    /// </summary>
    private static void DrawRunTint(int method, int n, byte[] src, int s, byte[] row, int d, int address, SpriteBlend blend)
    {
        int[] tint = blend.Tint, part = blend.TintPart;
        byte[] t2 = blend.T2;
        switch (method)
        {
            case 2:
            {
                void Table(int pixels)
                {
                    for (int k = 0; k < pixels * 3; k++, s++, d++, address++)
                        row[d] = (byte)(t2[src[s]] + part[k % 3]);
                }
                if (n < 15)
                {
                    Table(n);
                    return;
                }
                int head = address & 3;
                Table(head);
                n -= head;
                if ((address & 4) != 0)
                {
                    Table(4);
                    n -= 4;
                }
                int a = blend.Alpha;
                for (int groups = n >> 3; groups > 0; groups--)
                {
                    for (int k = 0; k < 24; k++, s++, d++, address++)
                    {
                        int c = src[s];
                        row[d] = (byte)((((tint[k % 3] - c) * a & 0xFFFF) >> 8) + c);
                    }
                }
                n &= 7;
                if (n >= 4)
                {
                    Table(4);
                    n -= 4;
                }
                Table(n);
                return;
            }
            case 3:
            {
                byte b = (byte)(t2[src[s]] + part[0]), g = (byte)(t2[src[s + 1]] + part[1]), r = (byte)(t2[src[s + 2]] + part[2]);
                for (int i = 0; i < n; i++, d += 3)
                {
                    row[d] = b;
                    row[d + 1] = g;
                    row[d + 2] = r;
                }
                return;
            }
            case 4:
                for (int i = 0; i < n; i++, s += 4, d += 3)
                {
                    int alpha = src[s];
                    for (int k = 0; k < 3; k++)
                        BlendByte(row, d + k, t2[src[s + 1 + k]] + part[k], alpha);
                }
                return;
            default:
            {
                int alpha = src[s];
                int b = t2[src[s + 1]] + part[0], g = t2[src[s + 2]] + part[1], r = t2[src[s + 3]] + part[2];
                for (int i = 0; i < n; i++, d += 3)
                {
                    BlendByte(row, d, b, alpha);
                    BlendByte(row, d + 1, g, alpha);
                    BlendByte(row, d + 2, r, alpha);
                }
                return;
            }
        }
    }

    /// <summary>A run header at offset <paramref name="o"/> of a row read into <paramref name="row"/>
    /// from <paramref name="address"/> (headers are 2-byte aligned in memory).</summary>
    private static (int Method, int Count, int Data) ReadRun(byte[] row, int address, int o)
    {
        o = ((address + o + 1) & ~1) - address;
        int code = row[o] | row[o + 1] << 8;
        int data = o + 2 + ((code >> 11) & 3);
        int count = code & 0x7FF;
        if (count == 0)
        {
            count = BitConverter.ToInt32(row, data);
            data += 4;
        }
        return (code >> 13, count, data);
    }

    /// <summary>A run header at address <paramref name="p"/> (2-byte aligned): method, count, data address.</summary>
    private (int Method, int Count, int Data) ReadRun(int p)
    {
        p = p + 1 & ~1;
        int code = Read16(p);
        int data = p + 2 + ((code >> 11) & 3);
        int count = code & 0x7FF;
        if (count == 0)
        {
            count = Read32(data);
            data += 4;
        }
        return (code >> 13, count, data);
    }

    /// <summary>The row of a frame from its row pointer: its 16-bit length and the runs after it.</summary>
    private byte[] ReadRow(int p)
    {
        int n = Read16(p) + 16;
        if (t_rowSource == null || t_rowSource.Length < n)
            t_rowSource = new byte[Math.Max(n, 0x4000)];
        ReadBytes(p, t_rowSource.AsSpan(0, n));
        return t_rowSource;
    }

    /// <summary>The thread's buffer for a destination row that crosses pages.</summary>
    private static byte[] RowTarget(int bytes)
    {
        if (t_rowTarget == null || t_rowTarget.Length < bytes)
            t_rowTarget = new byte[Math.Max(bytes, 0x4000)];
        return t_rowTarget;
    }

    // Row buffers of the compositor, used again for every row (the runs of a frame row, and the
    // destination pixels it is drawn over); one pair a thread, as the rows of a sprite are drawn
    // on all cores
    [ThreadStatic]
    private static byte[]? t_rowSource, t_rowTarget;

    /// <summary>Where the data of a run ends (methods 0-1 none, 2 BGR each, 3 one BGR, 4 ABGR each, 5+ one ABGR).</summary>
    private static int RunEnd(int method, int count, int data) => method switch
    {
        < 2 => data,
        2 => data + count * 3,
        3 => data + 3,
        4 => data + count * 4,
        _ => data + 4,
    };

    /// <summary>
    /// One row (0x4409F6, or 0x440F9C with a blend): skip <paramref name="skip"/> pixels of runs,
    /// then draw up to <paramref name="visible"/>. The row's runs and the destination are read
    /// into arrays once; MMX alignment still follows the destination's address.
    /// </summary>
    private void DrawRowPlain(int p, int dst, int skip, int visible, SpriteBlend? blend = null)
    {
        if (visible <= 0)
            return;
        byte[] src = ReadRow(p);
        int rowBytes = visible * 3;
        // Draw straight into the page that holds the destination row, or into a copy of a row
        // that crosses pages
        bool direct = TryDirect(dst, rowBytes, out byte[] row, out int d0);
        if (!direct)
        {
            row = RowTarget(rowBytes);
            d0 = 0;
            ReadBytes(dst, row.AsSpan(0, rowBytes));
        }
        int o = 2, d = d0;
        int remaining = visible;
        // Left clip: whole runs are passed over, a run that crosses the edge is drawn from it
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
            int at = method switch { 2 => data + start * 3, 4 => data + start * 4, _ => data };
            if (method < 2)
            {
                d += part * 3;
                remaining -= part;
            }
            else
            {
                int n = Math.Min(part, remaining);
                DrawRun(method, n, src, at, row, d, dst + d - d0, blend);
                d += n * 3;
                remaining -= n;
            }
            if (remaining <= 0)
            {
                if (!direct)
                    WriteBytes(dst, row.AsSpan(0, rowBytes));
                return;
            }
            break;
        }
        while (remaining > 0)
        {
            var (method, count, data) = ReadRun(src, p, o);
            o = RunEnd(method, count, data);
            if (method < 2)
            {
                d += count * 3;
                remaining -= count;
                continue;
            }
            int n = Math.Min(count, remaining);
            DrawRun(method, n, src, data, row, d, dst + d - d0, blend);
            d += n * 3;
            remaining -= n;
        }
        if (!direct)
            WriteBytes(dst, row.AsSpan(0, rowBytes));
    }

    private static void BlendByte(byte[] row, int at, int source, int alpha)
    {
        int d = row[at];
        row[at] = (byte)(((source - d) * alpha >> 8) + d);
    }

    /// <summary>A run into the row: n pixels from src[s] to row[d] (row[d] is at memory address <paramref name="address"/>).</summary>
    private static void DrawRun(int method, int n, byte[] src, int s, byte[] row, int d, int address, SpriteBlend? blend)
    {
        switch (blend?.Kind)
        {
            case BlendKind.Alpha:
                DrawRunAlpha(method, n, src, s, row, d, address, blend!);
                return;
            case BlendKind.Tint:
                DrawRunTint(method, n, src, s, row, d, address, blend!);
                return;
            case BlendKind.Add:
                DrawRunAdd(method, n, src, s, row, d, address, blend!);
                return;
            case BlendKind.Opaque:
                DrawRunOpaque(method, n, src, s, row, d);
                return;
            case BlendKind.Mask:
                DrawRunMask(method, n, src, s, row, d);
                return;
        }
        switch (method)
        {
            case 2:
                Array.Copy(src, s, row, d, n * 3);
                return;
            case 3:
            {
                byte b = src[s], g = src[s + 1], r = src[s + 2];
                for (int i = 0; i < n; i++, d += 3)
                {
                    row[d] = b;
                    row[d + 1] = g;
                    row[d + 2] = r;
                }
                return;
            }
            case 4:
                for (int i = 0; i < n; i++, s += 4, d += 3)
                {
                    int a = src[s];
                    if (a == 0xFF)
                    {
                        row[d] = src[s + 1];
                        row[d + 1] = src[s + 2];
                        row[d + 2] = src[s + 3];
                    }
                    else if (a != 0)
                    {
                        BlendByte(row, d, src[s + 1], a);
                        BlendByte(row, d + 1, src[s + 2], a);
                        BlendByte(row, d + 2, src[s + 3], a);
                    }
                }
                return;
            default:
            {
                int a = src[s];
                byte b = src[s + 1], g = src[s + 2], r = src[s + 3];
                for (int i = 0; i < n; i++, d += 3)
                {
                    BlendByte(row, d, b, a);
                    BlendByte(row, d + 1, g, a);
                    BlendByte(row, d + 2, r, a);
                }
                return;
            }
        }
    }

    /// <summary>The runs of mode 0x40000000 (0x440F9C: the MMX + SSE path).</summary>
    private static void DrawRunAlpha(int method, int n, byte[] src, int s, byte[] row, int d, int address, SpriteBlend blend)
    {
        switch (method)
        {
            case 2:
            {
                // two sources: the run's pixels at a, the picture at 256 - a
                int a = blend.Alpha, b = 256 - a;
                byte Table(int i) => (byte)(blend.T1[src[s + i]] + blend.T2[row[d + i]]);
                byte Block(int i) => (byte)Math.Min(255, (src[s + i] * a + row[d + i] * b & 0xFFFF) >> 8);
                int left = n * 3, at = 0, head = -address & 7;
                if (head != 0)
                {
                    for (int h = head & 3; h > 0 && left > 0; h--, at++, left--)
                        row[d + at] = Table(at);
                    if (left > 0 && left < 4)
                    {
                        for (; left > 0; at++, left--)
                            row[d + at] = Table(at);
                    }
                    else if (left > 0 && (head & 4) != 0)
                    {
                        for (int q = 0; q < 4; q++)
                            row[d + at + q] = Block(at + q);
                        at += 4;
                        left -= 4;
                    }
                }
                for (; left >= 8; at += 8, left -= 8)
                    for (int q = 0; q < 8; q++)
                        row[d + at + q] = Block(at + q);
                if (left >= 4)
                {
                    for (int q = 0; q < 4; q++)
                        row[d + at + q] = Block(at + q);
                    at += 4;
                    left -= 4;
                }
                for (; left > 0; at++, left--)
                    row[d + at] = Table(at);
                return;
            }
            case 3:
            {
                // one colour scaled by a, the picture by the table / MMX at 256 - a
                int a = blend.Alpha, b = 256 - a;
                byte c0 = (byte)(src[s] * a >> 8), c1 = (byte)(src[s + 1] * a >> 8), c2 = (byte)(src[s + 2] * a >> 8);
                void TablePixel(int at)
                {
                    row[at] = (byte)(blend.T2[row[at]] + c0);
                    row[at + 1] = (byte)(blend.T2[row[at + 1]] + c1);
                    row[at + 2] = (byte)(blend.T2[row[at + 2]] + c2);
                }
                int left = n;
                if (left < 15)
                {
                    for (; left > 0; left--, d += 3)
                        TablePixel(d);
                    return;
                }
                int abs = address;
                for (; (abs & 7) != 0; left--, d += 3, abs += 3)
                    TablePixel(d);
                for (int groups = left >> 3; groups > 0; groups--, d += 24, abs += 24)
                    for (int q = 0; q < 24; q++)
                    {
                        byte c = (q % 3) switch { 0 => c0, 1 => c1, _ => c2 };
                        row[d + q] = (byte)(Math.Min(255, row[d + q] * b >> 8) + c);
                    }
                for (left &= 7; left > 0; left--, d += 3)
                    TablePixel(d);
                return;
            }
            case 4:
                for (int i = 0; i < n; i++, s += 4, d += 3)
                {
                    int alpha = blend.T1[src[s]];
                    if (alpha == 0xFF)
                    {
                        row[d] = src[s + 1];
                        row[d + 1] = src[s + 2];
                        row[d + 2] = src[s + 3];
                    }
                    else if (alpha != 0)
                    {
                        BlendByte(row, d, src[s + 1], alpha);
                        BlendByte(row, d + 1, src[s + 2], alpha);
                        BlendByte(row, d + 2, src[s + 3], alpha);
                    }
                }
                return;
            default:
            {
                int alpha = blend.T1[src[s]];
                byte b0 = src[s + 1], g0 = src[s + 2], r0 = src[s + 3];
                for (int i = 0; i < n; i++, d += 3)
                {
                    BlendByte(row, d, b0, alpha);
                    BlendByte(row, d + 1, g0, alpha);
                    BlendByte(row, d + 2, r0, alpha);
                }
                return;
            }
        }
    }

    /// <summary>FUN_004116A0: a frame's rectangle (x, y, x + width, y + height), or null.</summary>
    public (int L, int T, int R, int B)? FrameRect(int slot, int frame)
    {
        int picture = Picture(slot);
        if (picture == 0 || (uint)Read32(picture + 4) <= (uint)frame)
            return null;
        int f = Read32(picture + 8 + 4 * frame);
        if (f == 0)
            return null;
        int x = Read32(f + 8), y = Read32(f + 0xC);
        return (x, y, x + Read32(f), y + Read32(f + 4));
    }

    /// <summary>
    /// FUN_004114B0: from the highest priority down (unsigned; shown or not), the first entry
    /// whose frame has a run of method 1 or more under (x, y) gives <paramref name="hit"/> = its
    /// word 6. An entry with a missing picture or frame ends the search.
    /// </summary>
    public bool HitTest(int sprites, int count, int x, int y, ref int hit)
    {
        for (uint current = uint.MaxValue; ;)
        {
            uint next = 0;
            bool more = false;
            for (int i = count - 1; i >= 0; i--)
            {
                int e = sprites + i * SpriteSize;
                uint priority = (uint)Read32(e + 0xC);
                if (priority != current)
                {
                    if (priority < current && next <= priority)
                    {
                        next = priority;
                        more = true;
                    }
                    continue;
                }
                int picture = Picture(Read32(e));
                if (picture == 0)
                    return false;
                uint frameIndex = (uint)Read32(e + 4);
                if ((uint)Read32(picture + 4) <= frameIndex)
                    return false;
                int f = Read32(picture + 8 + 4 * (int)frameIndex);
                if (f == 0)
                    return false;
                int width = Read32(f), height = Read32(f + 4);
                if (width == 0 || height == 0)
                    continue;
                int top = Read32(f + 0xC) + Read32(e + 0x14);
                if (top > y || y >= height + top)
                    continue;
                int row = Read32(f + 0x14 + 4 * (y - top));
                if (row == 0)
                    continue;
                int left = Read32(f + 8) + Read32(e + 0x10);
                if (left > x || x >= width + left)
                    continue;
                int p = row + 2, end = left, remaining = width;
                while (true)
                {
                    var (method, n, data) = ReadRun(p);
                    end += n;
                    p = RunEnd(method, n, data);
                    if (x < end)
                    {
                        if (method != 0)
                        {
                            hit = Read32(e + 0x18);
                            return true;
                        }
                        break;
                    }
                    remaining -= n;
                    if (remaining == 0)
                        break;
                }
            }
            if (!more)
                return true;
            current = next;
        }
    }

    /// <summary>StretchBlt SRCCOPY: a copy when the sizes match, else nearest pixels.</summary>
    public void StretchCopy(int dst, int x, int y, int w, int h, int src, int sx, int sy, int sw, int sh)
    {
        if (w == sw && h == sh)
        {
            BitBlt(dst, x, y, w, h, src, sx, sy);
            return;
        }
        if (w == 0 || h == 0 || sw == 0 || sh == 0)
            return;
        int dw = SurfaceField(dst, 7), dh = SurfaceField(dst, 8), dp = SurfaceField(dst, 10), d0 = SurfaceField(dst, 2);
        int swid = SurfaceField(src, 7), shei = SurfaceField(src, 8), spitch = SurfaceField(src, 10), s0 = SurfaceField(src, 2);
        for (int row = 0; row < Math.Abs(h); row++)
        {
            int ty = y + row;
            int fy = sy + (int)((long)row * sh / h);
            if (ty < 0 || ty >= dh || fy < 0 || fy >= shei)
                continue;
            for (int col = 0; col < Math.Abs(w); col++)
            {
                int tx = x + col, fx = sx + (int)((long)col * sw / w);
                if (tx < 0 || tx >= dw || fx < 0 || fx >= swid)
                    continue;
                CopyPixel24(d0 + ty * dp + tx * 3, s0 + fy * spitch + fx * 3);
            }
        }
    }

    /// <summary>
    /// A 24-bit pixel: its three bytes read, then written (a pixel at a time, as the stretches go,
    /// with no buffer: a 3-byte array a pixel made 051E take 190 ms on a phone).
    /// </summary>
    private void CopyPixel24(int dst, int src)
    {
        byte b = ReadByte(src), g = ReadByte(src + 1), r = ReadByte(src + 2);
        if ((dst & (PageSize - 1)) <= PageSize - 3)
        {
            var page = Page(dst);
            int o = dst & (PageSize - 1);
            page[o] = b;
            page[o + 1] = g;
            page[o + 2] = r;
            return;
        }
        WriteByte(dst, b);
        WriteByte(dst + 1, g);
        WriteByte(dst + 2, r);
    }

    /// <summary>
    /// The engine's stretch (051E with bit 31): 16.16 steps, a table of byte steps per column of
    /// the destination, rows from the scaled source row (FUN_0041E180, quirks kept).
    /// </summary>
    public void SoftStretch(int dst, int dx, int dy, int dw, int dh, int src, int sx, int sy, int sw, int sh)
    {
        if (dw == 0 || dh == 0)
            return;
        int xstep = (int)(((long)sw << 16) / dw), ystep = (int)(((long)sh << 16) / dh);
        int width = SurfaceField(dst, 7);
        var table = new int[Math.Max(width, 0)];
        int previous = sx * 3;
        for (int i = 0, acc = 0; i < width; i++, acc += xstep)
        {
            int position = ((acc >> 16) + sx) * 3;
            table[i] = position - previous;
            previous = position;
        }
        int startX, skipX;
        if (dx < 0)
        {
            skipX = -(xstep * dx) >> 16;
            startX = 0;
        }
        else
        {
            startX = dx;
            skipX = 0;
        }
        int endX = Math.Min(dx + dw, width);
        int rowAcc, startY;
        if (dy < 0)
        {
            rowAcc = -(ystep * dy);
            startY = 0;
        }
        else
        {
            startY = dy;
            rowAcc = 0;
        }
        int endY = Math.Min(dy + dh, ScreenHeight);
        int srcPitch = SurfaceField(src, 10), dstPitch = SurfaceField(dst, 10);
        int srcBase = SurfaceField(src, 2) + skipX * 3 + srcPitch * sy;
        int dstRow = SurfaceField(dst, 2) + dstPitch * startY + startX * 3;
        for (int y = startY; y < endY; y++, rowAcc += ystep, dstRow += dstPitch)
        {
            int p = (rowAcc >> 16) * srcPitch + srcBase, q = dstRow;
            for (int x = startX; x < endX; x++, q += 3)
            {
                p += table[x];
                CopyPixel24(q, p);
            }
        }
    }

    private void RegisterSprites()
    {
        // 04B8 n: the list the sprite opcodes use; 04B9 n, v: v = list n's table
        Register(0x04B8, (vm, c, i) => { vm.m_spriteList = vm.Value(c, i.Args[0]) & (SpriteLists - 1); return 0; });
        Register(0x04B9, (vm, c, i) =>
        {
            vm.Store(c, i.Args[1], vm.TableAddress(vm.Value(c, i.Args[0])));
            return 0;
        });
        // 04BA: empty the list; 04BB n: n entries; 04BC v: v = how many
        Register(0x04BA, (vm, c, i) => { vm.m_spriteCounts[vm.m_spriteList] = 0; return 0; });
        Register(0x04BB, (vm, c, i) => { vm.m_spriteCounts[vm.m_spriteList] = vm.Value(c, i.Args[0]); return 0; });
        Register(0x04BC, (vm, c, i) => { vm.Store(c, i.Args[0], vm.m_spriteCounts[vm.m_spriteList]); return 0; });
        // 04BD slot, frame, flags, priority, x, y, a, b: add an entry (FUN_00411FE0)
        Register(0x04BD, (vm, c, i) =>
        {
            int list = vm.m_spriteList;
            int e = vm.SpriteEntry(list, vm.m_spriteCounts[list]);
            for (int k = 0; k < 8; k++)
                vm.Write32(e + 4 * k, vm.Value(c, i.Args[k]));
            vm.m_spriteCounts[list]++;
            return 0;
        });
        // 04C4 n: draw the list into surface n
        Register(0x04C4, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            if (n is < 0 or >= SurfaceCount || vm.SurfaceField(n, 2) == 0)
                return 0;
            int list = vm.m_spriteList;
            if (vm.Trace != null)
                vm.TraceLine(c, $"04C4 list {list} ({vm.m_spriteCounts[list]} entries) into surface {n}");
            vm.Compose(vm.TableAddress(list), vm.m_spriteCounts[list], vm.SurfaceField(n, 2),
                vm.SurfaceField(n, 7), vm.SurfaceField(n, 8), vm.SurfaceField(n, 10), null);
            return 0;
        });
        // 04C5 n, l, t, r, b: draw the list into surface n, clipped to the rectangle
        Register(0x04C5, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            int l = vm.Value(c, i.Args[1]), t = vm.Value(c, i.Args[2]), r = vm.Value(c, i.Args[3]), b = vm.Value(c, i.Args[4]);
            if (n is < 0 or >= SurfaceCount || vm.SurfaceField(n, 2) == 0)
                return 0;
            int list = vm.m_spriteList;
            if (vm.Trace != null)
                vm.TraceLine(c, $"04C5 list {list} ({vm.m_spriteCounts[list]} entries) into surface {n}, {l},{t}-{r},{b}");
            vm.Compose(vm.TableAddress(list), vm.m_spriteCounts[list], vm.SurfaceField(n, 2),
                vm.SurfaceField(n, 7), vm.SurfaceField(n, 8), vm.SurfaceField(n, 10), (l, t, r, b));
            return 0;
        });
        // 04C7 x, y, v: v = word 6 of the list entry with a visible pixel at (x, y), -1 for none
        Register(0x04C7, (vm, c, i) =>
        {
            int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
            int list = vm.m_spriteList;
            int hit = -1;
            vm.HitTest(vm.TableAddress(list), vm.m_spriteCounts[list], x, y, ref hit);
            vm.Store(c, i.Args[2], hit);
            return 0;
        });
        // 04C8 slot, frame, l, t, r, b: the frame's rectangle (its offset and size); 04CB slot,
        // frame, w, h: its size (FUN_004116A0; a missing frame is a script error)
        Register(0x04C8, (vm, c, i) =>
        {
            if (vm.FrameRect(vm.Value(c, i.Args[0]), vm.Value(c, i.Args[1])) is not { } r)
                return 2;
            vm.Store(c, i.Args[2], r.L);
            vm.Store(c, i.Args[3], r.T);
            vm.Store(c, i.Args[4], r.R);
            vm.Store(c, i.Args[5], r.B);
            return 0;
        });
        Register(0x04CB, (vm, c, i) =>
        {
            if (vm.FrameRect(vm.Value(c, i.Args[0]), vm.Value(c, i.Args[1])) is not { } r)
                return 2;
            vm.Store(c, i.Args[2], r.R - r.L);
            vm.Store(c, i.Args[3], r.B - r.T);
            return 0;
        });
        // 04C9 slot, frame, v: v = 1 when the picture has that frame
        Register(0x04C9, (vm, c, i) =>
        {
            int picture = vm.Picture(vm.Value(c, i.Args[0]));
            uint frame = (uint)vm.Value(c, i.Args[1]);
            bool has = picture != 0 && (uint)vm.Read32(picture + 4) > frame && vm.Read32(picture + 8 + 4 * (int)frame) != 0;
            vm.Store(c, i.Args[2], has ? 1 : 0);
            return 0;
        });
        // 04D3 v / 04D4 v: engine settings 0x4880B0 / 0x4880B4
        Register(0x04D3, (vm, c, i) => { vm.EngineGlobals[0x4880B0] = vm.Value(c, i.Args[0]); return 0; });
        Register(0x04D4, (vm, c, i) => { vm.EngineGlobals[0x4880B4] = vm.Value(c, i.Args[0]); return 0; });
        // 051E dst, x, y, w, h, src, sx, sy, sw, sh, rop (FUN_0041E180): StretchBlt; with bit 31
        // of rop the engine's own nearest-pixel stretch
        Register(0x051E, (vm, c, i) =>
        {
            var v = new int[11];
            for (int k = 0; k < 11; k++)
                v[k] = vm.Value(c, i.Args[k]);
            int dst = v[0], src = v[5];
            if (v[10] < 0)
                vm.SoftStretch(dst, v[1], v[2], v[3], v[4], src, v[6], v[7], v[8], v[9]);
            else if (v[10] == 0xCC0020)
                vm.StretchCopy(dst, v[1], v[2], v[3], v[4], src, v[6], v[7], v[8], v[9]);
            else
                return 2;
            if (dst == vm.DisplaySurface)
            {
                vm.PresentedByDirect3D(v[1], v[2], v[1] + v[3], v[2] + v[4]);
                vm.FrameShown = true;
            }
            return 0;
        });
        // 04E2 dst, x, y, w, h, src, sx, sy: copy a rectangle between surfaces (FUN_00417900),
        // clipped to the destination only
        Register(0x04E2, (vm, c, i) =>
        {
            int dstSurface = vm.Value(c, i.Args[0]);
            int x = vm.Value(c, i.Args[1]), y = vm.Value(c, i.Args[2]);
            int w = vm.Value(c, i.Args[3]), h = vm.Value(c, i.Args[4]);
            int srcSurface = vm.Value(c, i.Args[5]);
            int sx = vm.Value(c, i.Args[6]), sy = vm.Value(c, i.Args[7]);
            if (vm.Trace != null)
                vm.TraceLine(c, $"04E2 into surface {dstSurface} at {x},{y} {w}x{h} from surface {srcSurface} at {sx},{sy}");
            int l = x, t = y, r = x + w, b = y + h;
            int dw = vm.SurfaceField(dstSurface, 7), dh = vm.SurfaceField(dstSurface, 8);
            if (dw < l || dh < t)
                return 0;
            if (l < 0)
            {
                if (r < 1)
                    return 0;
                sx -= l;
                l = 0;
            }
            if (t < 0)
            {
                if (b < 1)
                    return 0;
                sy -= t;
                t = 0;
            }
            r = Math.Min(r, dw);
            b = Math.Min(b, dh);
            int bytes = vm.SurfaceField(srcSurface, 9) >> 3;
            int width = r - l, rows = b - t;
            if (width == 0 || rows == 0)
                return 0;
            int srcPitch = vm.SurfaceField(srcSurface, 10), dstPitch = vm.SurfaceField(dstSurface, 10);
            int src = vm.SurfaceField(srcSurface, 2) + sy * srcPitch + sx * bytes;
            int dst = vm.SurfaceField(dstSurface, 2) + t * dstPitch + l * (vm.SurfaceField(dstSurface, 9) >> 3);
            for (; rows > 0; rows--, src += srcPitch, dst += dstPitch)
                vm.CopyMemory(dst, src, bytes * width);
            if (dstSurface == vm.DisplaySurface)
            {
                // Not the original's (FUN_00417900 only draws): the window is repainted where the
                // copy went. START's fade (slot 212, 0x197AF) skipped with Ctrl copies the title
                // into the display surface without 07D0, after a WM_PAINT has shown the surface
                // it was restored from - the title stayed white but where the pointer passed.
                vm.InvalidateWindow(l, t, r, b);
                vm.FrameShown = true;
            }
            return 0;
        });
    }
}
