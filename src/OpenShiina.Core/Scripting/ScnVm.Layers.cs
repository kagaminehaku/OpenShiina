// Drawing into 32-bit pictures ("layers": text and button pictures made by op_055A with 4 bytes a
// pixel, alpha first). FUN_004436F0 / 004437C0 draws one sprite entry over such a picture with
// "over" blending: a = 255 - (255 - sa)(255 - da)/255 done as (0xFE01 - (255-sa)(255-da)) *
// 0x10203 >> 24 in 32 bits, and each channel (s sa 255 + (255 - sa) da d) * (2^24 / a) >> 32.
// Source alpha 255 or a transparent destination pixel copies the source; BGR runs become opaque.

using System.Buffers.Binary;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>
    /// FUN_00411130: draws the sprite entry at <paramref name="entry"/> over frame
    /// <paramref name="frame"/> of a 32-bit picture.
    /// </summary>
    public void DrawOnPicture(int picture, int frame, int entry)
    {
        if (picture == 0 || (uint)frame > (uint)Read32(picture + 4))
            return;
        int f = Read32(picture + 8 + 4 * frame);
        if (f == 0)
            return;
        int width = Read32(f), height = Read32(f + 4);
        int pixels = Read32(f + 0x14) + 8;
        DrawSprite32(entry, pixels, width, height, width * 4 + 8);
    }

    /// <summary>FUN_004437C0 (through 004436F0 with no clip rectangle): one entry, clipped to the target.</summary>
    private void DrawSprite32(int entry, int pixels, int targetWidth, int targetHeight, int pitch)
    {
        int picture = Picture(Read32(entry));
        uint frameIndex = (uint)Read32(entry + 4);
        if (picture == 0 || frameIndex >= (uint)Read32(picture + 4))
            return;
        int frame = Read32(picture + 8 + 4 * (int)frameIndex);
        if (frame == 0 || Read32(frame) == 0)
            return;
        int width = Read32(frame), rows = Read32(frame + 4);
        int dst = pixels;
        int y = Read32(entry + 0x14) + Read32(frame + 0xC);
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
            if (y >= targetHeight)
                return;
            dst += y * pitch;
            row = frame + 0x14;
        }
        if (bottom - targetHeight > 0)
            rows -= bottom - targetHeight;

        int x = Read32(entry + 0x10) + Read32(frame + 8);
        int right = width + x - 1;
        int skip, visible;
        if (x < 0)
        {
            if (right < 0)
                return;
            skip = -x;
            visible = right >= targetWidth ? targetWidth : width - skip;
        }
        else
        {
            skip = 0;
            if (x >= targetWidth)
                return;
            dst += x * 4;
            visible = right >= targetWidth ? width + targetWidth - right - 1 : width;
        }
        for (; rows > 0; rows--, row += 4, dst += pitch)
            DrawRow32(Read32(row), dst, skip, visible);
    }

    private void DrawRow32(int p, int dst, int skip, int visible)
    {
        p += 2;
        int remaining = visible;
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
                dst += part * 4;
                remaining -= part;
            }
            else
            {
                int n = Math.Min(part, remaining);
                DrawRun32(method, n, at, dst);
                dst += n * 4;
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
                dst += count * 4;
                remaining -= count;
                continue;
            }
            int n = Math.Min(count, remaining);
            DrawRun32(method, n, data, dst);
            dst += n * 4;
            remaining -= n;
        }
    }

    // A run's source bytes when they cannot be read in place (they share bytes with the target)
    private byte[]? m_runSource;

    /// <summary>A run drawn straight into the picture (pixels read and written unaligned).</summary>
    private void DrawRun32(int method, int n, int data, int dst)
    {
        if (n <= 0)
            return;
        var target = Bytes(dst, n * 4);
        switch (method)
        {
            case 2:
            {
                // 3-byte colours, opaque
                var source = ReadView(data, n * 3, ref m_runSource, dst, n * 4);
                for (int i = 0, s = 0; i < target.Length; i += 4, s += 3)
                {
                    target[i] = 0xFF;
                    target[i + 1] = source[s];
                    target[i + 2] = source[s + 1];
                    target[i + 3] = source[s + 2];
                }
                break;
            }
            case 3:
            {
                // One opaque colour
                uint color = (uint)(Read32(data) << 8 | 0xFF);
                for (int i = 0; i < target.Length; i += 4)
                    BinaryPrimitives.WriteUInt32LittleEndian(target[i..], color);
                break;
            }
            case 4:
            {
                // ABGR colours over the target
                var source = ReadView(data, n * 4, ref m_runSource, dst, n * 4);
                for (int i = 0; i < target.Length; i += 4)
                    BinaryPrimitives.WriteUInt32LittleEndian(target[i..],
                        Over(BinaryPrimitives.ReadUInt32LittleEndian(target[i..]), BinaryPrimitives.ReadUInt32LittleEndian(source[i..])));
                break;
            }
            default:
            {
                // One ABGR colour over the target
                uint color = (uint)Read32(data);
                for (int i = 0; i < target.Length; i += 4)
                    BinaryPrimitives.WriteUInt32LittleEndian(target[i..], Over(BinaryPrimitives.ReadUInt32LittleEndian(target[i..]), color));
                break;
            }
        }
    }

    /// <summary>One source pixel (alpha, blue, green, red from the low byte up) over a destination pixel.</summary>
    private static uint Over(uint dst, uint source)
    {
        uint sa = source & 0xFF;
        if (sa == 0)
            return dst;
        uint da = dst & 0xFF;
        if (sa == 0xFF || da == 0)
            return source;
        uint a = unchecked((0xFE01 - (0xFF - sa) * (0xFF - da)) * 0x10203) >> 24;
        if (a == 0)
            return dst & 0xFFFFFF00;
        uint inverse = 0x1000000 / a, result = a;
        for (int k = 8; k <= 24; k += 8)
        {
            uint s = (source >> k) & 0xFF, d = (dst >> k) & 0xFF;
            uint sum = s * sa * 0xFF + (0xFF - sa) * da * d;
            result |= (uint)((ulong)sum * inverse >> 32) << k;
        }
        return result;
    }

    private void RegisterLayers()
    {
        // 0562 picture, frame, x, y, source slot, source frame: draw a frame over a 32-bit picture
        // (FUN_0041F380: a shown entry with priority 0)
        Register(0x0562, (vm, c, i) =>
        {
            var v = new int[6];
            for (int k = 0; k < 6; k++)
                v[k] = vm.Value(c, i.Args[k]);
            int entry = vm.LayerEntry;
            vm.Write32(entry, v[4]);
            vm.Write32(entry + 4, v[5]);
            vm.Write32(entry + 8, unchecked((int)0x80000000));
            vm.Write32(entry + 0xC, 0);
            vm.Write32(entry + 0x10, v[2]);
            vm.Write32(entry + 0x14, v[3]);
            vm.Write32(entry + 0x18, 0);
            vm.Write32(entry + 0x1C, 0);
            vm.DrawOnPicture(vm.Picture(v[0]), v[1], entry);
            return 0;
        });
    }

    // A scratch sprite entry for the layer opcodes
    private int m_layerEntry;
    private int LayerEntry => m_layerEntry != 0 ? m_layerEntry : m_layerEntry = Allocate(0x20);
}
