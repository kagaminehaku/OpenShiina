// Picture effects and the renderer settings: fills, additive and subtractive copies, mosaic,
// waves, ripples, rotation and zoom, picture-to-picture copies. Each follows its handler in the
// executable, quirks included (they are noted where they are).

using System.Numerics;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>
    /// FUN_00417790: clips <paramref name="r"/> (left, top, right, bottom) to (0, 0, width, height);
    /// false when nothing is left. A rectangle that follows moves its left / top edge with r's,
    /// and its right / bottom edge by r's right / bottom (as the executable does).
    /// </summary>
    private static bool ClipRect(Span<int> r, Span<int> follow, int width, int height)
    {
        if (r[0] > width || r[1] > height)
            return false;
        if (r[0] < 0)
        {
            if (r[2] <= 0)
                return false;
            if (!follow.IsEmpty)
            {
                follow[0] -= r[0];
                follow[2] -= r[2];
            }
            r[0] = 0;
        }
        if (r[1] < 0)
        {
            if (r[3] <= 0)
                return false;
            if (!follow.IsEmpty)
            {
                follow[1] -= r[1];
                follow[3] -= r[3];
            }
            r[1] = 0;
        }
        if (r[2] > width)
            r[2] = width;
        if (r[3] > height)
            r[3] = height;
        return true;
    }

    /// <summary>
    /// Drawing into the display surface ends the host frame; the window shows it only where it is
    /// repainted (ScnVm.Paint).
    /// </summary>
    private void Shown(int surface)
    {
        if (surface == DisplaySurface)
            FrameShown = true;
    }

    /// <summary>The effects that paint the window themselves (0564, 0566, 056C) invalidate what they drew.</summary>
    private void ShownOnWindow(int surface, int l, int t, int r, int b)
    {
        if (surface != DisplaySurface)
            return;
        InvalidateWindow(l, t, r, b);
        FrameShown = true;
    }

    /// <summary>FillRect of a surface with a colour (0x00BBGGRR); 32-bit pixels get 0 in their fourth byte.</summary>
    public void FillSurface(int surface, int l, int t, int r, int b, int color)
    {
        int bytes = SurfaceField(surface, 9) >> 3, pitch = SurfaceField(surface, 10), pixels = SurfaceField(surface, 2);
        if (pixels == 0 || r <= l || b <= t || bytes is not (3 or 4))
            return;
        var row = new byte[(r - l) * bytes];
        for (int i = 0; i < row.Length; i += bytes)
        {
            row[i] = (byte)(color >> 16);
            row[i + 1] = (byte)(color >> 8);
            row[i + 2] = (byte)color;
        }
        for (int y = t; y < b; y++)
            WriteBytes(pixels + y * pitch + l * bytes, row);
    }

    // Sine and cosine * 256 of 256 steps around the circle (WinMain, 0x4C410C / 0x4C58BC)
    private static readonly int[] s_sin256 = new int[256], s_cos256 = new int[256];

    static ScnVm()
    {
        for (int i = 0; i < 256; i++)
        {
            double a = i * (2 * Math.PI / 256);
            s_sin256[i] = (int)(Math.Sin(a) * 256);
            s_cos256[i] = (int)(Math.Cos(a) * 256);
        }
    }

    // FUN_00419AC0: distance and angle from the centre of each pixel of a quarter of the
    // surface, made again when the size changes (0x4C7D10 / 0x4C7D14)
    private int[] m_rippleDistance = [];
    private byte[] m_rippleAngle = [];
    private int m_rippleW = -1, m_rippleH = -1;

    private void RippleTables(int surface)
    {
        int hw = (int)((uint)SurfaceField(surface, 7) >> 1), hh = (int)((uint)SurfaceField(surface, 8) >> 1);
        if (hw == m_rippleW && hh == m_rippleH)
            return;
        m_rippleW = hw;
        m_rippleH = hh;
        m_rippleDistance = new int[hw * hh];
        m_rippleAngle = new byte[hw * hh];
        for (int r = 0, k = 0; r < hh; r++)
        {
            int dy = hh - r;
            for (int c = 0; c < hw; c++, k++)
            {
                int dx = hw - c;
                m_rippleDistance[k] = (int)Math.Sqrt((double)dx * dx + (double)dy * dy);
                // atan2(-dy, dx) * 128 / pi + 256 in 80-bit floating point: on the diagonal it
                // comes out just under 224 there, where doubles give 224
                m_rippleAngle[k] = dx == dy ? (byte)223 : (byte)(int)(Math.Atan2(-dy, dx) * (128 / Math.PI) + 256.0);
            }
        }
    }

    /// <summary>A long divided by a double, truncated (fild / fdiv / _ftol2, exactly rather than in 80 bits).</summary>
    private static long DivideTruncated(long value, double divisor)
    {
        if (divisor == 0 || double.IsNaN(divisor) || double.IsInfinity(divisor))
            return long.MinValue;
        long bits = BitConverter.DoubleToInt64Bits(divisor);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent++;
        else
            mantissa |= 1L << 52;
        exponent -= 1075;
        BigInteger num = value, den = mantissa;
        if (exponent < 0)
            num <<= -exponent;
        else
            den <<= exponent;
        if (bits < 0)
            num = -num;
        var q = BigInteger.Divide(num, den);
        return q > long.MaxValue || q < long.MinValue ? long.MinValue : (long)q;
    }

    private static long TruncateToLong(double v) =>
        double.IsNaN(v) || v >= 9.2233720368547758E18 || v < -9.2233720368547758E18 ? long.MinValue : (long)v;

    private void RegisterEffects()
    {
        // 04D8 n, x, y, w, h, r, g, b (FUN_00417BF0): fill a rectangle of surface n
        Register(0x04D8, (vm, c, i) =>
        {
            var v = new int[8];
            for (int k = 0; k < 8; k++)
                v[k] = vm.Value(c, i.Args[k]);
            int n = v[0];
            Span<int> r = [v[1], v[2], v[1] + v[3], v[2] + v[4]];
            if (n is < 0 or >= SurfaceCount || !ClipRect(r, [], vm.SurfaceField(n, 7), vm.SurfaceField(n, 8)))
                return 0;
            vm.FillSurface(n, r[0], r[1], r[2], r[3], (v[5] & 0xFF) | (v[6] & 0xFF) << 8 | (v[7] & 0xFF) << 16);
            vm.Shown(n);
            return 0;
        });
        // 04FB / 04FC dst, x, y, w, h, src, sx, sy, a (FUN_0041B7A0 / FUN_0041B9A0): dst + or -
        // src * a / 256 a byte at a time, saturated, 3 bytes a pixel and dst's pitch for both
        Register(0x04FB, (vm, c, i) => vm.AddSubtract(c, i, true));
        Register(0x04FC, (vm, c, i) => vm.AddSubtract(c, i, false));
        // 0564 dst, x, y, w, h, src, sx, sy, size (FUN_00417FE0): mosaic
        Register(0x0564, (vm, c, i) =>
        {
            var v = new int[9];
            for (int k = 0; k < 9; k++)
                v[k] = vm.Value(c, i.Args[k]);
            vm.Mosaic(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
            return 0;
        });
        // 0566 dst, x, y, w, h, src, sx, sy, phase, step, height, flags (FUN_004183E0): each
        // row (bit 0: each column) of the rectangle turned round by a sine wave
        Register(0x0566, (vm, c, i) =>
        {
            var v = new int[12];
            for (int k = 0; k < 12; k++)
                v[k] = vm.Value(c, i.Args[k]);
            vm.Wave(v);
            return 0;
        });
        // 056C dst, src, phase, frequency, height (FUN_00419C50): circular ripples
        Register(0x056C, (vm, c, i) =>
        {
            var v = new int[5];
            for (int k = 0; k < 5; k++)
                v[k] = vm.Value(c, i.Args[k]);
            vm.Ripple(v[0], v[1], v[2], v[3], v[4]);
            return 0;
        });
        // 055F slot, frame, x, y, w, h, src slot, frame, sx, sy (FUN_0041F220 -> FUN_00410DC0):
        // a rectangle from picture to picture
        Register(0x055F, (vm, c, i) =>
        {
            var v = new int[10];
            for (int k = 0; k < 10; k++)
                v[k] = vm.Value(c, i.Args[k]);
            vm.PictureToPicture(vm.Picture(v[0]), v[1], v[2], v[3], vm.Picture(v[6]), v[7], v[8], v[9], v[4], v[5]);
            return 0;
        });
        // 0574 (FUN_004193E0): a 32-bit picture rotated and zoomed into another
        Register(0x0574, (vm, c, i) =>
        {
            var v = new int[19];
            for (int k = 0; k < 19; k++)
                v[k] = vm.Value(c, i.Args[k]);
            vm.RotateZoom(v);
            return 0;
        });
        // 04E8 mode, a, b, c, v (FUN_0040D340): the renderer - 0 GDI (a, b: StretchBlt modes),
        // 2 Direct3D, 3 OpenGL (a, b: texture filters). Only GDI is offered: v = 1 for it, 0 for
        // the others (the scripts then fall back to GDI).
        Register(0x04E8, (vm, c, i) =>
        {
            int mode = vm.Value(c, i.Args[0]), a = vm.Value(c, i.Args[1]), b = vm.Value(c, i.Args[2]);
            vm.Value(c, i.Args[3]);
            if (mode == 0)
            {
                vm.EngineGlobals[0x482940] = a;
                vm.EngineGlobals[0x482944] = b;
                vm.EngineGlobals[0x487F30] = 0;
            }
            vm.Store(c, i.Args[4], mode == 0 ? 1 : 0);
            return 0;
        });
        // 04EE x, y, w, h: the part of the window the picture is shown in, 04F0 sx, sy: its
        // scale (floats). Full screen stretches the picture (FUN_00413300); the host does that,
        // so the scripts see the picture shown at (0, 0) at its own size.
        Register(0x04EE, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], 0);
            vm.Store(c, i.Args[1], 0);
            vm.Store(c, i.Args[2], vm.ScreenWidth);
            vm.Store(c, i.Args[3], vm.ScreenHeight);
            return 0;
        });
        Register(0x04F0, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], BitConverter.SingleToInt32Bits(1f));
            vm.Store(c, i.Args[1], BitConverter.SingleToInt32Bits(1f));
            return 0;
        });
    }

    private int AddSubtract(ScnContext c, ScnInstruction i, bool add)
    {
        var v = new int[9];
        for (int k = 0; k < 9; k++)
            v[k] = Value(c, i.Args[k]);
        AddSubtract(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], add);
        return 0;
    }

    /// <summary>04FB / 04FC: dst + or - (src * a >> 8), a byte at a time.</summary>
    public void AddSubtract(int dst, int x, int y, int w, int h, int src, int sx, int sy, int a, bool add)
    {
        Span<int> r = [x, y, x + w, y + h];
        Span<int> s = [sx, sy, sx + w, sy + h];
        if (!ClipRect(r, s, SurfaceField(dst, 7), SurfaceField(dst, 8)))
            return;
        int width = r[2] - r[0], rows = r[3] - r[1];
        if (width <= 0 || rows <= 0)
            return;
        int pitch = SurfaceField(dst, 10);
        int d = SurfaceField(dst, 2) + (SurfaceField(dst, 9) >> 3) * r[0] + r[1] * pitch;
        int p = SurfaceField(src, 2) + (SurfaceField(src, 9) >> 3) * s[0] + s[1] * SurfaceField(src, 10);
        int bytes = width * 3, head = bytes & 7;
        var line = new byte[bytes];
        var from = new byte[bytes];
        for (int row = 0; row < rows; row++, d += pitch, p += pitch)
        {
            ReadBytes(d, line);
            ReadBytes(p, from);
            for (int k = 0; k < bytes; k++)
            {
                int part = (from[k] * a >> 8) & 0xFF;
                if (add)
                    line[k] = (byte)Math.Min(255, line[k] + part);
                else if (k >= head)
                    line[k] = (byte)Math.Max(0, line[k] - part);
                else
                    // The MMX path's first bytes (FUN_004385F0) subtract the other way round
                    line[k] = (byte)(part >= line[k] ? part - line[k] : 0);
            }
            WriteBytes(d, line);
        }
        Shown(dst);
    }

    /// <summary>
    /// 0564: blocks of size x size pixels of one colour. The colour of a block comes from src
    /// at (size / 2 + size * column, size / 2 + size * row), counted from 0 rather than from the
    /// rectangle, and with dst's pitch and pixel size; a size under 1 copies.
    /// </summary>
    private void Mosaic(int dst, int x, int y, int w, int h, int src, int sx, int sy, int size)
    {
        Span<int> s = [sx, sy, sx + w, sy + h];
        Span<int> d = [x, y, x + w, y + h];
        if (!ClipRect(s, d, SurfaceField(dst, 7), SurfaceField(dst, 8)))
            return;
        int right = d[2] - d[0] + s[0], bottom = s[1] + (d[3] - d[1]);
        if (size < 1)
        {
            BitBlt(dst, d[0], d[1], d[2] - d[0], d[3] - d[1], src, s[0], s[1]);
            ShownOnWindow(dst, x, y, x + w, y + h);
            return;
        }
        int pitch = SurfaceField(dst, 10), bytes = SurfaceField(dst, 9) >> 3;
        int target = SurfaceField(dst, 2), source = SurfaceField(src, 2);
        int half = size / 2;
        int sampleRow = half;
        Span<byte> colour = stackalloc byte[3];
        for (int top = s[1]; top < bottom; top += size)
        {
            int end = Math.Min(top + size, bottom);
            if (bottom <= sampleRow)
                sampleRow = bottom - 1;
            if (s[0] < right)
            {
                int sampleColumn = half;
                for (int left = s[0]; left < right; left += size)
                {
                    ReadBytes(source + sampleColumn * bytes + sampleRow * pitch, colour);
                    sampleColumn += size;
                    if (right <= sampleColumn)
                        sampleColumn = right - 1;
                    int n = right < left + size ? right - left : size;
                    var run = new byte[n * 3];
                    for (int k = 0; k < run.Length; k++)
                        run[k] = colour[k % 3];
                    for (int row = top; row < end; row++)
                        WriteBytes(target + (left - s[0] + x) * bytes + (row - s[1] + y) * pitch, run);
                }
            }
            sampleRow += size;
        }
        ShownOnWindow(dst, x, y, x + w, y + h);
    }

    /// <summary>
    /// 0566: rows (or with bit 0 of the flags columns) of src turned round by
    /// sin(phase / 256 + row * step / 256) * height / 256 pixels into dst, with BitBlt. The
    /// rectangle only decides whether anything is drawn; columns are w pixels high (as in the
    /// executable).
    /// </summary>
    private void Wave(int[] v)
    {
        int dst = v[0], x = v[1], y = v[2], w = v[3], h = v[4], src = v[5], sx = v[6], sy = v[7];
        int step = v[9], height = v[10];
        Span<int> r = [x, y, x + w, y + h];
        Span<int> s = [sx, sy, sx + w, sy + h];
        if (!ClipRect(r, s, SurfaceField(dst, 7), SurfaceField(dst, 8)))
            return;
        uint phase = (uint)(v[8] * 0x100 + step);
        if ((v[11] & 1) == 0)
        {
            for (int row = 0; row < h; row++)
            {
                int offset = s_sin256[(phase >> 8) & 0xFF] * height >> 16;
                phase += (uint)step;
                int ty = y + row, fy = sy + row;
                if (offset < 0)
                {
                    BitBlt(dst, x, ty, w + offset, 1, src, sx - offset, fy);
                    BitBlt(dst, x + w + offset, ty, -offset, 1, src, sx, fy);
                }
                else
                {
                    BitBlt(dst, x, ty, offset, 1, src, sx + w - offset, fy);
                    BitBlt(dst, x + offset, ty, w - offset, 1, src, sx, fy);
                }
            }
        }
        else
        {
            for (int column = 0; column < w; column++)
            {
                int offset = s_sin256[(phase >> 8) & 0xFF] * height >> 16;
                phase += (uint)step;
                int tx = x + column, fx = sx + column;
                if (offset < 0)
                {
                    BitBlt(dst, tx, y, 1, w + offset, src, fx, sy - offset);
                    BitBlt(dst, tx, y + w + offset, 1, -offset, src, fx, sy);
                }
                else
                {
                    BitBlt(dst, tx, y, 1, offset, src, fx, sy + w - offset);
                    BitBlt(dst, tx, y + offset, 1, w - offset, src, fx, sy);
                }
            }
        }
        ShownOnWindow(dst, x, y, x + w, y + h);
    }

    /// <summary>
    /// 056C: each pixel of a quarter of dst and its three mirror images take src moved along
    /// the line from the centre by sin(((distance - phase / 256) * frequency) &amp; 0xFF) * height /
    /// 256 pixels (black outside). Rows of dst are 3 * width bytes apart, rows of src dst's pitch.
    /// </summary>
    private void Ripple(int dst, int src, int phase, int frequency, int height)
    {
        int w = SurfaceField(dst, 7), h = SurfaceField(dst, 8), pitch = SurfaceField(dst, 10);
        int target = SurfaceField(dst, 2), source = SurfaceField(src, 2);
        RippleTables(dst);
        int hw = w / 2, hh = h / 2;
        int shift = -(phase >> 8);
        sbyte multiplier = (sbyte)frequency;
        Span<byte> pixel = stackalloc byte[3];
        Span<byte> black = stackalloc byte[3];
        int k = 0;
        for (int r = 0; r < hh; r++)
        {
            int top = target + r * 3 * w, bottom = target + (h - 1 - r) * 3 * w;
            for (int col = 0; col < hw; col++, k++)
            {
                int index = (byte)((sbyte)(byte)(m_rippleDistance[k] + shift) * multiplier);
                int amount = TruncatingShift(s_sin256[index] * height);
                int angle = m_rippleAngle[k];
                int ox = TruncatingShift(s_cos256[angle] * amount), oy = TruncatingShift(s_sin256[angle] * amount);
                int left = ox + col, right = w - ox - col - 1, up = r - oy, down = oy - r + h - 1;
                int tl = top + col * 3, tr = top + (w - 1 - col) * 3, bl = bottom + col * 3, br = bottom + (w - 1 - col) * 3;
                if (left < 0 || right >= w || up < 0 || down >= h)
                {
                    WriteBytes(tl, black);
                    WriteBytes(tr, black);
                    WriteBytes(bl, black);
                    WriteBytes(br, black);
                    continue;
                }
                ReadBytes(source + up * pitch + left * 3, pixel);
                WriteBytes(tl, pixel);
                ReadBytes(source + up * pitch + right * 3, pixel);
                WriteBytes(tr, pixel);
                ReadBytes(source + down * pitch + left * 3, pixel);
                WriteBytes(bl, pixel);
                ReadBytes(source + down * pitch + right * 3, pixel);
                WriteBytes(br, pixel);
            }
        }
        // InvalidateRect(NULL): all of the window
        ShownOnWindow(dst, 0, 0, ScreenWidth, ScreenHeight);
    }

    /// <summary>v / 256 rounded towards 0 (cdq / and edx, 0FFh / add / sar 8).</summary>
    private static int TruncatingShift(int v) => (v + (v < 0 ? 0xFF : 0)) >> 8;

    /// <summary>
    /// FUN_00410DC0: a rectangle of a picture's frame into another frame. The rectangle is
    /// clipped to the destination frame's size; 3-byte pixels going into 4-byte frames get FF
    /// in front and only 3/4 of the row is written, 4-byte pixels going into 3-byte frames lose
    /// their first byte. Rows are copied forwards a dword at a time, so a frame copied onto
    /// itself repeats what it has just written.
    /// </summary>
    public void PictureToPicture(int dstPicture, int dstFrame, int x, int y, int srcPicture, int srcFrame, int sx, int sy, int w, int h)
    {
        if (dstPicture == 0 || srcPicture == 0)
            return;
        int FrameOf(int picture, int frame) => (uint)frame > (uint)Read32(picture + 4) ? 0 : Read32(picture + 8 + 4 * frame);
        int df = FrameOf(dstPicture, dstFrame), sf = FrameOf(srcPicture, srcFrame);
        if (df == 0 || sf == 0)
            return;
        int dstW = Read32(df), dstH = Read32(df + 4);
        Span<int> s = [sx, sy, sx + w, sy + h];
        Span<int> d = [x, y, x + dstW, y + dstH];
        if (!ClipRect(s, d, dstW, dstH))
            return;
        int width = s[2] - s[0], rows = s[3] - s[1];
        int srcBytes = Read32(Read32(sf + 0x14)) < 0 ? 4 : 3, dstBytes = Read32(Read32(df + 0x14)) < 0 ? 4 : 3;
        int line = srcBytes * width;
        int from = Read32(sf + 0x14 + 4 * s[1]) + srcBytes * s[0] + 8;
        int to = Read32(df + 0x14 + 4 * d[1]) + dstBytes * d[0] + 8;
        int srcStride = Read32(sf) * srcBytes + 8, dstStride = dstW * dstBytes + 8;
        for (; rows > 0; rows--, from += srcStride, to += dstStride)
        {
            if (line <= 0)
                continue;
            if (srcBytes == dstBytes)
                CopyForward(to, from, line);
            else if (to < from + line + 4 && from < to + line / srcBytes * dstBytes + 4)
                ConvertInPlace(to, from, line, srcBytes);
            else if (srcBytes == 3)
            {
                var row = ReadBytes(from, (line + 3) / 4 * 3);
                var wide = new byte[(line + 3) / 4 * 4];
                for (int k = 0, q = 0; k < wide.Length; k += 4, q += 3)
                {
                    wide[k] = 0xFF;
                    wide[k + 1] = row[q];
                    wide[k + 2] = row[q + 1];
                    wide[k + 3] = row[q + 2];
                }
                WriteBytes(to, wide);
            }
            else
            {
                var row = ReadBytes(from, line);
                var narrow = new byte[line / 4 * 3];
                for (int k = 0, q = 0; q < narrow.Length; k += 4, q += 3)
                {
                    narrow[q] = row[k + 1];
                    narrow[q + 1] = row[k + 2];
                    narrow[q + 2] = row[k + 3];
                }
                WriteBytes(to, narrow);
            }
        }
    }

    /// <summary>A row of 055F between 3- and 4-byte pixels over memory it reads: byte by byte, in the executable's order.</summary>
    private void ConvertInPlace(int to, int from, int line, int srcBytes)
    {
        if (srcBytes == 3)
        {
            for (int k = 0; k < line; k += 4, to += 4, from += 3)
            {
                WriteByte(to, 0xFF);
                WriteByte(to + 1, ReadByte(from));
                WriteByte(to + 2, ReadByte(from + 1));
                WriteByte(to + 3, ReadByte(from + 2));
            }
            return;
        }
        for (int k = 0; k < line; k += 4, to += 3, from += 4)
        {
            WriteByte(to, ReadByte(from + 1));
            WriteByte(to + 1, ReadByte(from + 2));
            WriteByte(to + 2, ReadByte(from + 3));
        }
    }

    /// <summary>rep movsd then rep movsb: forwards, a dword at a time (overlaps repeat what was written).</summary>
    private void CopyForward(int dst, int src, int count)
    {
        if (dst <= src || dst >= src + count)
        {
            CopyMemory(dst, src, count);
            return;
        }
        int start = src, length = dst + count - src;
        var span = ReadBytes(start, length);
        int s = 0, d = dst - src;
        for (int n = count >> 2; n > 0; n--, s += 4, d += 4)
            BitConverter.TryWriteBytes(span.AsSpan(d), BitConverter.ToInt32(span, s));
        for (int n = count & 3; n > 0; n--)
            span[d++] = span[s++];
        WriteBytes(start, span);
    }

    /// <summary>
    /// 0574 dst slot, frame, x, y, w, h, src slot, frame, sx, sy, sw, sh, tx, ty, angle, zoom x,
    /// zoom y, flags, colour (floats for angle and zoom; FUN_004193E0): every pixel of
    /// the rectangle of a 32-bit frame takes the src pixel its position turned by the angle
    /// (degrees) round the rectangle's centre, divided by the zoom and moved by (tx, ty) falls
    /// on, in 32.32 fixed point; outside src's rectangle it keeps its colour (flags bit 0) or
    /// gets the colour.
    /// </summary>
    private void RotateZoom(int[] v)
    {
        int dstPicture = Picture(v[0]), srcPicture = Picture(v[6]);
        if (dstPicture == 0 || srcPicture == 0)
            return;
        int FrameOf(int picture, int frame) => (uint)frame > (uint)Read32(picture + 4) ? 0 : Read32(picture + 8 + 4 * frame);
        int df = FrameOf(dstPicture, v[1]), sf = FrameOf(srcPicture, v[7]);
        if (df == 0 || sf == 0)
            return;
        int x = v[2], y = v[3], w = v[4], h = v[5];
        int dstBase = PicturePixel(dstPicture, v[1], 0, 0, 4), srcBase = PicturePixel(srcPicture, v[7], 0, 0, 4);
        int cx = -((w + x + x) / 2), cy = -((h + y + y) / 2);
        int dstStride = Read32(df) * 4 + 8, srcStride = Read32(sf) * 4 + 8;
        int sx = v[8], sy = v[9], sxEnd = sx + v[10], syEnd = sy + v[11];
        int tx = v[12], ty = v[13];
        double angle = BitConverter.Int32BitsToSingle(v[14]);
        double zoomX = BitConverter.Int32BitsToSingle(v[15]), zoomY = BitConverter.Int32BitsToSingle(v[16]);
        bool keep = (v[17] & 1) != 0;
        int colour = v[18];
        // FUN_00417850: the rectangle within the frame
        int l = x, t = y, r = x + w, b = y + h, fw = Read32(df), fh = Read32(df + 4);
        if (l > fw || t > fh)
            return;
        if (l < 0)
        {
            if (r <= 0)
                return;
            l = 0;
        }
        if (t < 0)
        {
            if (b <= 0)
                return;
            t = 0;
        }
        r = Math.Min(r, fw);
        b = Math.Min(b, fh);

        double radians = angle * (Math.PI / 180);
        long sin = TruncateToLong(Math.Sin(radians) * 4294967296.0), cos = TruncateToLong(Math.Cos(radians) * 4294967296.0);
        long u0 = DivideTruncated(unchecked(cx * cos - cy * sin + ((long)tx << 32)), zoomX);
        long v0 = DivideTruncated(unchecked(cy * cos + cx * sin + ((long)ty << 32)), zoomY);
        long dux = DivideTruncated(cos, zoomX), dvx = DivideTruncated(sin, zoomY);
        double turned = radians + Math.PI / 2;
        long duy = TruncateToLong(Math.Cos(turned) * 4294967296.0 / zoomX), dvy = TruncateToLong(Math.Sin(turned) * 4294967296.0 / zoomY);

        var row = new byte[Math.Max(0, (r - l) * 4)];
        for (int yy = t; yy < b; yy++)
        {
            long u = unchecked(yy * duy + u0), vv = unchecked(yy * dvy + v0);
            int at = dstBase + yy * dstStride + l * 4;
            ReadBytes(at, row);
            for (int xx = l, k = 0; xx < r; xx++, k += 4)
            {
                int fx = (int)(u >> 32), fy = (int)(vv >> 32);
                if (fx >= sx && fx < sxEnd && fy >= sy && fy < syEnd)
                    BitConverter.TryWriteBytes(row.AsSpan(k), Read32(srcBase + fy * srcStride + fx * 4));
                else if (!keep)
                    BitConverter.TryWriteBytes(row.AsSpan(k), colour);
                u = unchecked(u + dux);
                vv = unchecked(vv + dvx);
            }
            WriteBytes(at, row);
        }
    }
}
