// Sprite lists and the compositor. A list is a table of op_04B5 (32-byte entries) with a count
// (0x7DDB90[list]); op_04B8 picks the list the other opcodes work on. An entry is
//   [0] picture slot  [1] frame  [2] flags  [3] priority  [4] x  [5] y  [6] -  [7] extra (+0x1C)
// flags: bit 31 shown, bits 26-27 special modes, bits 28-30 the blend mode, bits 0-8 its alpha.
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
        // In order of priority: everything at the current one, then the next larger one
        for (uint current = 0; ;)
        {
            uint next = uint.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int e = sprites + i * SpriteSize;
                int flags = Read32(e + 8);
                if (flags >= 0 || !SpriteDrawable(e, ref flags))
                    continue;
                uint priority = (uint)Read32(e + 0xC);
                if (priority == current)
                    DrawSprite(e, flags, target);
                else if (priority > current && priority <= next)
                    next = priority;
            }
            if (next == uint.MaxValue)
                break;
            current = next;
        }
    }

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

        if ((flags & 0x0C000000) != 0)
            throw new NotSupportedException($"Sprite mode {flags & 0x0C000000:X8} is not supported yet");
        if ((flags & 0x70000000) != 0)
            throw new NotSupportedException($"Sprite blend {flags & 0x70000000:X8} (alpha {flags & 0x1FF}) is not supported yet");
        for (; rows > 0; rows--, row += 4, dst += target.Pitch)
            DrawRowPlain(Read32(row), dst, skip, visible);
    }

    /// <summary>A run header at <paramref name="p"/> (2-byte aligned): method, count, data address.</summary>
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
    /// One row with no blend mode (0x4409F6): skip <paramref name="skip"/> pixels of runs, then
    /// draw up to <paramref name="visible"/>. Blends are d + ((s - d) * a >> 8) (arithmetic shift),
    /// low byte - the MMX blocks give the same bytes; alpha 255 in a per-pixel run copies.
    /// </summary>
    private void DrawRowPlain(int p, int dst, int skip, int visible)
    {
        p += 2;
        int remaining = visible;
        // Left clip: whole runs are passed over, a run that crosses the edge is drawn from it
        while (skip > 0)
        {
            var (method, count, data) = ReadRun(p);
            p = RunEnd(method, count, data);
            skip -= count;
            if (skip > 0)
                continue;
            if (skip == 0)
                break;
            int part = -skip, start = count - part;
            int at = method switch { 2 => data + start * 3, 4 => data + start * 4, _ => data };
            if (method < 2)
            {
                dst += part * 3;
                remaining -= part;
            }
            else
            {
                int n = Math.Min(part, remaining);
                DrawRun(method, n, at, dst);
                dst += n * 3;
                remaining -= n;
            }
            if (remaining <= 0)
                return;
            break;
        }
        while (remaining > 0)
        {
            var (method, count, data) = ReadRun(p);
            p = RunEnd(method, count, data);
            if (method < 2)
            {
                dst += count * 3;
                remaining -= count;
                continue;
            }
            int n = Math.Min(count, remaining);
            DrawRun(method, n, data, dst);
            dst += n * 3;
            remaining -= n;
        }
    }

    private void DrawRun(int method, int n, int data, int dst)
    {
        switch (method)
        {
            case 2:
                CopyMemory(dst, data, n * 3);
                return;
            case 3:
            {
                byte b = ReadByte(data), g = ReadByte(data + 1), r = ReadByte(data + 2);
                for (int i = 0; i < n; i++, dst += 3)
                {
                    WriteByte(dst, b);
                    WriteByte(dst + 1, g);
                    WriteByte(dst + 2, r);
                }
                return;
            }
            case 4:
                for (int i = 0; i < n; i++, data += 4, dst += 3)
                {
                    int a = ReadByte(data);
                    if (a == 0xFF)
                    {
                        WriteByte(dst, ReadByte(data + 1));
                        WriteByte(dst + 1, ReadByte(data + 2));
                        WriteByte(dst + 2, ReadByte(data + 3));
                    }
                    else if (a != 0)
                    {
                        BlendByte(dst, ReadByte(data + 1), a);
                        BlendByte(dst + 1, ReadByte(data + 2), a);
                        BlendByte(dst + 2, ReadByte(data + 3), a);
                    }
                }
                return;
            default:
            {
                int a = ReadByte(data);
                byte b = ReadByte(data + 1), g = ReadByte(data + 2), r = ReadByte(data + 3);
                for (int i = 0; i < n; i++, dst += 3)
                {
                    BlendByte(dst, b, a);
                    BlendByte(dst + 1, g, a);
                    BlendByte(dst + 2, r, a);
                }
                return;
            }
        }
    }

    private void BlendByte(int at, int source, int alpha)
    {
        int d = ReadByte(at);
        WriteByte(at, (byte)(((source - d) * alpha >> 8) + d));
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
            vm.Compose(vm.TableAddress(list), vm.m_spriteCounts[list], vm.SurfaceField(n, 2),
                vm.SurfaceField(n, 7), vm.SurfaceField(n, 8), vm.SurfaceField(n, 10), null);
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
                vm.ScreenInvalidated = true;
                vm.FrameShown = true;
            }
            return 0;
        });
    }
}
