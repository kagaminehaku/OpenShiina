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
        // 0563 dst, frame, x, y, A, frame, ax, ay, B, frame, bx, by, w, h, a, b (FUN_0041F410 ->
        // FUN_004111A0, v2.47): each byte of the 32-bit pixels (alpha too) A * a / 256 plus
        // B * b / 256, at most 255, into dst's frame. The rectangle is clipped to dst's frame
        // (FUN_00417790), A's and B's places moving with its left and top edges. Pure Love!'s
        // cross-fade of two pictures
        Register(0x0563, (vm, c, i) =>
        {
            var v = new int[16];
            for (int k = 0; k < 16; k++)
                v[k] = vm.Value(c, i.Args[k]);
            vm.BlendPictures(v);
            return 0;
        });
    }

    /// <summary>A frame of a picture slot (its header address), or 0.</summary>
    private int PictureFrame(int slot, int frame)
    {
        int picture = Picture(slot);
        if (picture == 0 || (uint)frame > (uint)Read32(picture + 4))
            return 0;
        return Read32(picture + 8 + 4 * frame);
    }

    /// <summary>FUN_004111A0 for op 0563 (its arguments in <paramref name="v"/>).</summary>
    private void BlendPictures(int[] v)
    {
        int dst = PictureFrame(v[0], v[1]), a = PictureFrame(v[4], v[5]), b = PictureFrame(v[8], v[9]);
        if (dst == 0 || a == 0 || b == 0)
            return;
        int l = v[2], t = v[3], r = v[2] + v[12], btm = v[3] + v[13];
        int ax = v[6], ay = v[7], bx = v[10], by = v[11];
        int dw = Read32(dst), dh = Read32(dst + 4);
        if (l > dw || t > dh)
            return;
        if (l < 0)
        {
            if (r <= 0)
                return;
            ax -= l;
            bx -= l;
            l = 0;
        }
        if (t < 0)
        {
            if (btm <= 0)
                return;
            ay -= t;
            by -= t;
            t = 0;
        }
        r = Math.Min(r, dw);
        btm = Math.Min(btm, dh);
        int width = r - l, rows = btm - t;
        if (width <= 0 || rows <= 0)
            return;
        uint fa = (uint)v[14], fb = (uint)v[15];
        var lineA = new byte[width * 4];
        var lineB = new byte[width * 4];
        var line = new byte[width * 4];
        // the rows follow each other in a frame (each with an 8-byte head)
        int pa = Read32(a + 0x14 + 4 * ay) + 8 + 4 * ax, strideA = Read32(a) * 4 + 8;
        int pb = Read32(b + 0x14 + 4 * by) + 8 + 4 * bx, strideB = Read32(b) * 4 + 8;
        int pd = Read32(dst + 0x14 + 4 * t) + 8 + 4 * l, strideD = dw * 4 + 8;
        for (int y = 0; y < rows; y++, pa += strideA, pb += strideB, pd += strideD)
        {
            ReadBytes(pa, lineA);
            ReadBytes(pb, lineB);
            for (int k = 0; k < line.Length; k++)
                line[k] = (byte)Math.Min(255u, ((lineA[k] * fa) >> 8 & 0xFF) + ((lineB[k] * fb) >> 8 & 0xFF));
            WriteBytes(pd, line);
        }
    }

    // A scratch sprite entry for the layer opcodes
    private int m_layerEntry;
    private int LayerEntry => m_layerEntry != 0 ? m_layerEntry : m_layerEntry = Allocate(0x20);
}
