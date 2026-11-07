// C# Scripting/ScnVm.Effects.cs: picture effects and the renderer settings: fills, additive and
// subtractive copies, mosaic, waves, ripples, rotation and zoom, picture-to-picture copies. Each
// follows its handler in the executable, quirks included (they are noted where they are).
#include "scripting/ScnVm.h"

#include <algorithm>
#include <bit>
#include <climits>
#include <cmath>
#include <cstring>
#include <numbers>

namespace openshiina::scripting {

namespace {

// Sine and cosine * 256 of 256 steps around the circle (WinMain, 0x4C410C / 0x4C58BC)
struct Circle256
{
    int Sin[256], Cos[256];
    Circle256()
    {
        for (int i = 0; i < 256; i++)
        {
            double a = i * (2 * std::numbers::pi / 256);
            Sin[i] = int(std::sin(a) * 256);
            Cos[i] = int(std::cos(a) * 256);
        }
    }
};

const Circle256& Circle()
{
    static const Circle256 circle;
    return circle;
}

/// A long divided by a double, truncated (fild / fdiv / _ftol2, exactly rather than in 80 bits).
int64_t DivideTruncated(int64_t value, double divisor)
{
    if (divisor == 0 || std::isnan(divisor) || std::isinf(divisor))
        return INT64_MIN;
    uint64_t bits = std::bit_cast<uint64_t>(divisor);
    int exponent = int((bits >> 52) & 0x7FF);
    uint64_t mantissa = bits & 0xFFFFFFFFFFFFFull;
    if (exponent == 0)
        exponent++;
    else
        mantissa |= uint64_t(1) << 52;
    exponent -= 1075;
    bool negative = (value < 0) != (int64_t(bits) < 0);
    uint64_t magnitude = value < 0 ? 0 - uint64_t(value) : uint64_t(value);
    // |value| * 2^-exponent / mantissa, truncated: a long division bit by bit (the remainder stays
    // below the mantissa, under 2^53)
    uint64_t quotient = 0, remainder = 0;
    bool overflow = false;
    int shift = exponent < 0 ? -exponent : 0;
    for (int k = 63; k >= -shift; k--)
    {
        uint64_t bit = k >= 0 ? magnitude >> k & 1 : 0;
        remainder = remainder << 1 | bit;
        overflow |= (quotient >> 63) != 0;
        quotient <<= 1;
        if (remainder >= mantissa)
        {
            remainder -= mantissa;
            quotient |= 1;
        }
    }
    // A divisor of mantissa * 2^exponent: the quotient by the mantissa, then by 2^exponent
    if (exponent > 0)
        quotient = exponent >= 64 ? 0 : quotient >> exponent;
    if (overflow)
        return INT64_MIN;
    if (negative)
        return quotient > (uint64_t(1) << 63) ? INT64_MIN : int64_t(0 - quotient);
    return quotient > uint64_t(INT64_MAX) ? INT64_MIN : int64_t(quotient);
}

int64_t TruncateToLong(double v)
{
    return std::isnan(v) || v >= 9.2233720368547758E18 || v < -9.2233720368547758E18 ? INT64_MIN : int64_t(v);
}

/// v / 256 rounded towards 0 (cdq / and edx, 0FFh / add / sar 8).
int TruncatingShift(int v) { return (v + (v < 0 ? 0xFF : 0)) >> 8; }

float AsFloat(int bits) { return std::bit_cast<float>(bits); }

} // namespace

bool ScnVm::ClipRect(int* r, int* follow, int width, int height)
{
    // FUN_00417790: clips r (left, top, right, bottom) to (0, 0, width, height); false when nothing
    // is left. A rectangle that follows moves its left / top edge with r's, and its right / bottom
    // edge by r's right / bottom (as the executable does)
    if (r[0] > width || r[1] > height)
        return false;
    if (r[0] < 0)
    {
        if (r[2] <= 0)
            return false;
        if (follow)
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
        if (follow)
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

void ScnVm::Shown(int surface)
{
    // Drawing into the display surface ends the host frame; the window shows it only where it is
    // repainted (ScnVm.Paint)
    if (surface == DisplaySurface())
        FrameShown = true;
}

void ScnVm::ShownOnWindow(int surface, int l, int t, int r, int b)
{
    // The effects that paint the window themselves (0564, 0566, 056C) invalidate what they drew
    if (surface != DisplaySurface())
        return;
    InvalidateWindow(l, t, r, b);
    FrameShown = true;
}

void ScnVm::FillSurface(int surface, int l, int t, int r, int b, int color)
{
    // FillRect of a surface with a colour (0x00BBGGRR); 32-bit pixels get 0 in their fourth byte
    int bytes = SurfaceField(surface, 9) >> 3, pitch = SurfaceField(surface, 10), pixels = SurfaceField(surface, 2);
    if (pixels == 0 || r <= l || b <= t || (bytes != 3 && bytes != 4))
        return;
    openshiina::Bytes row(static_cast<size_t>((r - l) * bytes));
    for (size_t i = 0; i < row.size(); i += size_t(bytes))
    {
        row[i] = uint8_t(color >> 16);
        row[i + 1] = uint8_t(color >> 8);
        row[i + 2] = uint8_t(color);
    }
    for (int y = t; y < b; y++)
        WriteBytes(pixels + y * pitch + l * bytes, row);
}

void ScnVm::RippleTables(int surface)
{
    // FUN_00419AC0: distance and angle from the centre of each pixel of a quarter of the surface,
    // made again when the size changes (0x4C7D10 / 0x4C7D14)
    int hw = int(uint32_t(SurfaceField(surface, 7)) >> 1), hh = int(uint32_t(SurfaceField(surface, 8)) >> 1);
    if (hw == m_rippleW && hh == m_rippleH)
        return;
    m_rippleW = hw;
    m_rippleH = hh;
    m_rippleDistance.assign(size_t(hw) * size_t(hh), 0);
    m_rippleAngle.assign(size_t(hw) * size_t(hh), 0);
    for (int r = 0, k = 0; r < hh; r++)
    {
        int dy = hh - r;
        for (int c = 0; c < hw; c++, k++)
        {
            int dx = hw - c;
            m_rippleDistance[size_t(k)] = int(std::sqrt(double(dx) * dx + double(dy) * dy));
            // atan2(-dy, dx) * 128 / pi + 256 in 80-bit floating point: on the diagonal it comes
            // out just under 224 there, where doubles give 224
            m_rippleAngle[size_t(k)] = dx == dy ? uint8_t(223) : uint8_t(int(std::atan2(-dy, dx) * (128 / std::numbers::pi) + 256.0));
        }
    }
}

void ScnVm::RegisterEffects()
{
    // 04D8 n, x, y, w, h, r, g, b (FUN_00417BF0): fill a rectangle of surface n
    Register(0x04D8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[8];
        for (int k = 0; k < 8; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        int n = v[0];
        int r[4] = {v[1], v[2], v[1] + v[3], v[2] + v[4]};
        if (n < 0 || n >= SurfaceCount || !ClipRect(r, nullptr, vm.SurfaceField(n, 7), vm.SurfaceField(n, 8)))
            return 0;
        vm.FillSurface(n, r[0], r[1], r[2], r[3], (v[5] & 0xFF) | (v[6] & 0xFF) << 8 | (v[7] & 0xFF) << 16);
        vm.Shown(n);
        return 0;
    });
    // 04FB / 04FC dst, x, y, w, h, src, sx, sy, a (FUN_0041B7A0 / FUN_0041B9A0): dst + or - src * a
    // / 256 a byte at a time, saturated, 3 bytes a pixel and dst's pitch for both
    Register(0x04FB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.AddSubtract(c, i, true); });
    Register(0x04FC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.AddSubtract(c, i, false); });
    // 0564 dst, x, y, w, h, src, sx, sy, size (FUN_00417FE0): mosaic
    Register(0x0564, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[9];
        for (int k = 0; k < 9; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        vm.Mosaic(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
        return 0;
    });
    // 0566 dst, x, y, w, h, src, sx, sy, phase, step, height, flags (FUN_004183E0): each row (bit
    // 0: each column) of the rectangle turned round by a sine wave
    Register(0x0566, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[12];
        for (int k = 0; k < 12; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        vm.Wave(v);
        return 0;
    });
    // 056C dst, src, phase, frequency, height (FUN_00419C50): circular ripples
    Register(0x056C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[5];
        for (int k = 0; k < 5; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        vm.Ripple(v[0], v[1], v[2], v[3], v[4]);
        return 0;
    });
    // 055F slot, frame, x, y, w, h, src slot, frame, sx, sy (FUN_0041F220 -> FUN_00410DC0): a
    // rectangle from picture to picture
    Register(0x055F, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[10];
        for (int k = 0; k < 10; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        vm.PictureToPicture(vm.Picture(v[0]), v[1], v[2], v[3], vm.Picture(v[6]), v[7], v[8], v[9], v[4], v[5]);
        return 0;
    });
    // 0574 (FUN_004193E0): a 32-bit picture rotated and zoomed into another
    Register(0x0574, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[19];
        for (int k = 0; k < 19; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        vm.RotateZoom(v);
        return 0;
    });
    // 04E8 mode, a, b, c, v (FUN_0040D340): the renderer - 0 GDI (a, b: StretchBlt modes), 2
    // Direct3D, 3 OpenGL (a, b: texture filters). Only GDI is offered: v = 1 for it, 0 for the
    // others (the scripts then fall back to GDI)
    Register(0x04E8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
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
    // 04E7 mode, a, b, c (REMPLUS.EXE 0x42B335): the renderer 04E8 last set (-1 when none was: GDI
    // as RIO.INI has it) and its a, b, c as 04E8 last gave them, the engine's first ones until then
    // (GDI 3, 3; Direct3D 2, 2; OpenGL GL_LINEAR); Boku Igai no Otoko o Shiranai Kanojo's START
    // keeps them to go back to when another renderer fails
    Register(0x04E7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto global = [&](int address, int otherwise) {
            auto found = vm.EngineGlobals.find(address);
            return found == vm.EngineGlobals.end() ? otherwise : found->second;
        };
        int mode = global(0x487F30, -1);
        vm.Store(c, i.Args[0], mode);
        std::optional<std::pair<int, int>> filters;
        switch (mode)
        {
        case 0: filters = std::pair(global(0x482940, 3), global(0x482944, 3)); break;
        case 1: filters = std::pair(0, 0); break;
        case 2: filters = std::pair(2, 2); break;
        case 3: filters = std::pair(9729, 9729); break;
        default: break;
        }
        if (filters)
        {
            vm.Store(c, i.Args[1], filters->first);
            vm.Store(c, i.Args[2], filters->second);
            vm.Store(c, i.Args[3], 0);
        }
        return 0;
    });
    // 04EE x, y, w, h: the part of the window the picture is shown in, 04F0 sx, sy: its scale
    // (floats). Full screen stretches the picture (FUN_00413300); the host does that, so the
    // scripts see the picture shown at (0, 0) at its own size
    Register(0x04EE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], 0);
        vm.Store(c, i.Args[1], 0);
        vm.Store(c, i.Args[2], vm.ScreenWidth);
        vm.Store(c, i.Args[3], vm.ScreenHeight);
        return 0;
    });
    Register(0x04F0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], std::bit_cast<int>(1.0f));
        vm.Store(c, i.Args[1], std::bit_cast<int>(1.0f));
        return 0;
    });
    // 04EF x, y, w, h / 04F1 sx, sy (Oreimo 0x4254B9 / 0x42553D): set them (w, h -1: to the
    // window's right and bottom; the scale sets w and h too). The host places and scales the
    // picture itself, so they change nothing here (Imouto Smile's START gives its saved zoom)
    Register(0x04EF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (const auto& arg : i.Args)
            vm.Value(c, arg);
        return 0;
    });
    Register(0x04F1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        vm.Value(c, i.Args[1]);
        return 0;
    });
}

int ScnVm::AddSubtract(ScnContext& c, const ScnInstruction& i, bool add)
{
    int v[9];
    for (int k = 0; k < 9; k++)
        v[k] = Value(c, i.Args[size_t(k)]);
    AddSubtract(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], add);
    return 0;
}

void ScnVm::AddSubtract(int dst, int x, int y, int w, int h, int src, int sx, int sy, int a, bool add)
{
    // 04FB / 04FC: dst + or - (src * a >> 8), a byte at a time
    int r[4] = {x, y, x + w, y + h};
    int s[4] = {sx, sy, sx + w, sy + h};
    if (!ClipRect(r, s, SurfaceField(dst, 7), SurfaceField(dst, 8)))
        return;
    int width = r[2] - r[0], rows = r[3] - r[1];
    if (width <= 0 || rows <= 0)
        return;
    int pitch = SurfaceField(dst, 10);
    int d = SurfaceField(dst, 2) + (SurfaceField(dst, 9) >> 3) * r[0] + r[1] * pitch;
    int p = SurfaceField(src, 2) + (SurfaceField(src, 9) >> 3) * s[0] + s[1] * SurfaceField(src, 10);
    int bytes = width * 3, head = bytes & 7;
    std::vector<uint8_t> line(static_cast<size_t>(bytes)), from(static_cast<size_t>(bytes));
    for (int row = 0; row < rows; row++, d += pitch, p += pitch)
    {
        ReadBytes(d, line.data(), bytes);
        ReadBytes(p, from.data(), bytes);
        for (size_t k = 0; k < size_t(bytes); k++)
        {
            int part = (from[k] * a >> 8) & 0xFF;
            if (add)
                line[k] = uint8_t(std::min(255, line[k] + part));
            else if (int(k) >= head)
                line[k] = uint8_t(std::max(0, line[k] - part));
            else
                // The MMX path's first bytes (FUN_004385F0) subtract the other way round
                line[k] = uint8_t(part >= line[k] ? part - line[k] : 0);
        }
        WriteBytes(d, line.data(), bytes);
    }
    Shown(dst);
}

void ScnVm::Mosaic(int dst, int x, int y, int w, int h, int src, int sx, int sy, int size)
{
    // 0564: blocks of size x size pixels of one colour. The colour of a block comes from src at
    // (size / 2 + size * column, size / 2 + size * row), counted from 0 rather than from the
    // rectangle, and with dst's pitch and pixel size; a size under 1 copies
    int s[4] = {sx, sy, sx + w, sy + h};
    int d[4] = {x, y, x + w, y + h};
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
    uint8_t colour[3];
    for (int top = s[1]; top < bottom; top += size)
    {
        int end = std::min(top + size, bottom);
        if (bottom <= sampleRow)
            sampleRow = bottom - 1;
        if (s[0] < right)
        {
            int sampleColumn = half;
            for (int left = s[0]; left < right; left += size)
            {
                ReadBytes(source + sampleColumn * bytes + sampleRow * pitch, colour, 3);
                sampleColumn += size;
                if (right <= sampleColumn)
                    sampleColumn = right - 1;
                int n = right < left + size ? right - left : size;
                openshiina::Bytes run(static_cast<size_t>(n * 3));
                for (size_t k = 0; k < run.size(); k++)
                    run[k] = colour[k % 3];
                for (int row = top; row < end; row++)
                    WriteBytes(target + (left - s[0] + x) * bytes + (row - s[1] + y) * pitch, run);
            }
        }
        sampleRow += size;
    }
    ShownOnWindow(dst, x, y, x + w, y + h);
}

void ScnVm::Wave(const int* v)
{
    // 0566: rows (or with bit 0 of the flags columns) of src turned round by sin(phase / 256 + row
    // * step / 256) * height / 256 pixels into dst, with BitBlt. The rectangle only decides whether
    // anything is drawn; columns are w pixels high (as in the executable)
    int dst = v[0], x = v[1], y = v[2], w = v[3], h = v[4], src = v[5], sx = v[6], sy = v[7];
    int step = v[9], height = v[10];
    int r[4] = {x, y, x + w, y + h};
    int s[4] = {sx, sy, sx + w, sy + h};
    if (!ClipRect(r, s, SurfaceField(dst, 7), SurfaceField(dst, 8)))
        return;
    const auto& sin256 = Circle().Sin;
    uint32_t phase = uint32_t(v[8]) * 0x100 + uint32_t(step);
    if ((v[11] & 1) == 0)
    {
        for (int row = 0; row < h; row++)
        {
            int offset = sin256[(phase >> 8) & 0xFF] * height >> 16;
            phase += uint32_t(step);
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
            int offset = sin256[(phase >> 8) & 0xFF] * height >> 16;
            phase += uint32_t(step);
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

void ScnVm::Ripple(int dst, int src, int phase, int frequency, int height)
{
    // 056C: each pixel of a quarter of dst and its three mirror images take src moved along the
    // line from the centre by sin(((distance - phase / 256) * frequency) & 0xFF) * height / 256
    // pixels (black outside). Rows of dst are 3 * width bytes apart, rows of src dst's pitch
    int w = SurfaceField(dst, 7), h = SurfaceField(dst, 8), pitch = SurfaceField(dst, 10);
    int target = SurfaceField(dst, 2), source = SurfaceField(src, 2);
    RippleTables(dst);
    const auto& circle = Circle();
    int hw = w / 2, hh = h / 2;
    int shift = -(phase >> 8);
    int8_t multiplier = int8_t(frequency);
    uint8_t pixel[3];
    const uint8_t black[3] = {0, 0, 0};
    int k = 0;
    for (int r = 0; r < hh; r++)
    {
        int top = target + r * 3 * w, bottom = target + (h - 1 - r) * 3 * w;
        for (int col = 0; col < hw; col++, k++)
        {
            int index = uint8_t(int8_t(uint8_t(m_rippleDistance[size_t(k)] + shift)) * multiplier);
            int amount = TruncatingShift(circle.Sin[index] * height);
            int angle = m_rippleAngle[size_t(k)];
            int ox = TruncatingShift(circle.Cos[angle] * amount), oy = TruncatingShift(circle.Sin[angle] * amount);
            int left = ox + col, right = w - ox - col - 1, up = r - oy, down = oy - r + h - 1;
            int tl = top + col * 3, tr = top + (w - 1 - col) * 3, bl = bottom + col * 3, br = bottom + (w - 1 - col) * 3;
            if (left < 0 || right >= w || up < 0 || down >= h)
            {
                WriteBytes(tl, black, 3);
                WriteBytes(tr, black, 3);
                WriteBytes(bl, black, 3);
                WriteBytes(br, black, 3);
                continue;
            }
            ReadBytes(source + up * pitch + left * 3, pixel, 3);
            WriteBytes(tl, pixel, 3);
            ReadBytes(source + up * pitch + right * 3, pixel, 3);
            WriteBytes(tr, pixel, 3);
            ReadBytes(source + down * pitch + left * 3, pixel, 3);
            WriteBytes(bl, pixel, 3);
            ReadBytes(source + down * pitch + right * 3, pixel, 3);
            WriteBytes(br, pixel, 3);
        }
    }
    // InvalidateRect(NULL): all of the window
    ShownOnWindow(dst, 0, 0, ScreenWidth, ScreenHeight);
}

void ScnVm::PictureToPicture(int dstPicture, int dstFrame, int x, int y, int srcPicture, int srcFrame, int sx, int sy, int w, int h)
{
    // FUN_00410DC0: a rectangle of a picture's frame into another frame. The rectangle is clipped
    // to the destination frame's size; 3-byte pixels going into 4-byte frames get FF in front and
    // only 3/4 of the row is written, 4-byte pixels going into 3-byte frames lose their first byte.
    // Rows are copied forwards a dword at a time, so a frame copied onto itself repeats what it has
    // just written
    if (dstPicture == 0 || srcPicture == 0)
        return;
    auto frameOf = [&](int picture, int frame) {
        return uint32_t(frame) > uint32_t(Read32(picture + 4)) ? 0 : Read32(picture + 8 + 4 * frame);
    };
    int df = frameOf(dstPicture, dstFrame), sf = frameOf(srcPicture, srcFrame);
    if (df == 0 || sf == 0)
        return;
    int dstW = Read32(df), dstH = Read32(df + 4);
    int s[4] = {sx, sy, sx + w, sy + h};
    int d[4] = {x, y, x + dstW, y + dstH};
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
            openshiina::Bytes row = ReadBytes(from, (line + 3) / 4 * 3);
            openshiina::Bytes wide(static_cast<size_t>((line + 3) / 4 * 4));
            for (size_t k = 0, q = 0; k < wide.size(); k += 4, q += 3)
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
            openshiina::Bytes row = ReadBytes(from, line);
            openshiina::Bytes narrow(static_cast<size_t>(line / 4 * 3));
            for (size_t k = 0, q = 0; q < narrow.size(); k += 4, q += 3)
            {
                narrow[q] = row[k + 1];
                narrow[q + 1] = row[k + 2];
                narrow[q + 2] = row[k + 3];
            }
            WriteBytes(to, narrow);
        }
    }
}

void ScnVm::ConvertInPlace(int to, int from, int line, int srcBytes)
{
    // A row of 055F between 3- and 4-byte pixels over memory it reads: byte by byte, in the
    // executable's order
    if (srcBytes == 3)
    {
        for (int k = 0; k < line; k += 4, to += 4, from += 3)
        {
            WriteByte(to, 0xFF);
            WriteByte(to + 1, uint8_t(ReadByte(from)));
            WriteByte(to + 2, uint8_t(ReadByte(from + 1)));
            WriteByte(to + 3, uint8_t(ReadByte(from + 2)));
        }
        return;
    }
    for (int k = 0; k < line; k += 4, to += 3, from += 4)
    {
        WriteByte(to, uint8_t(ReadByte(from + 1)));
        WriteByte(to + 1, uint8_t(ReadByte(from + 2)));
        WriteByte(to + 2, uint8_t(ReadByte(from + 3)));
    }
}

void ScnVm::CopyForward(int dst, int src, int count)
{
    // rep movsd then rep movsb: forwards, a dword at a time (overlaps repeat what was written)
    if (dst <= src || dst >= src + count)
    {
        CopyMemory(dst, src, count);
        return;
    }
    int start = src, length = dst + count - src;
    openshiina::Bytes span = ReadBytes(start, length);
    size_t s = 0, d = size_t(dst - src);
    for (int n = count >> 2; n > 0; n--, s += 4, d += 4)
    {
        uint8_t dword[4];
        std::memcpy(dword, &span[s], 4);
        std::memcpy(&span[d], dword, 4);
    }
    for (int n = count & 3; n > 0; n--)
        span[d++] = span[s++];
    WriteBytes(start, span);
}

void ScnVm::RotateZoom(const int* v)
{
    // 0574 dst slot, frame, x, y, w, h, src slot, frame, sx, sy, sw, sh, tx, ty, angle, zoom x,
    // zoom y, flags, colour (floats for angle and zoom; FUN_004193E0): every pixel of the rectangle
    // of a 32-bit frame takes the src pixel its position turned by the angle (degrees) round the
    // rectangle's centre, divided by the zoom and moved by (tx, ty) falls on, in 32.32 fixed point;
    // outside src's rectangle it keeps its colour (flags bit 0) or gets the colour
    int dstPicture = Picture(v[0]), srcPicture = Picture(v[6]);
    if (dstPicture == 0 || srcPicture == 0)
        return;
    auto frameOf = [&](int picture, int frame) {
        return uint32_t(frame) > uint32_t(Read32(picture + 4)) ? 0 : Read32(picture + 8 + 4 * frame);
    };
    int df = frameOf(dstPicture, v[1]), sf = frameOf(srcPicture, v[7]);
    if (df == 0 || sf == 0)
        return;
    int x = v[2], y = v[3], w = v[4], h = v[5];
    int dstBase = PicturePixel(dstPicture, v[1], 0, 0, 4), srcBase = PicturePixel(srcPicture, v[7], 0, 0, 4);
    int cx = -((w + x + x) / 2), cy = -((h + y + y) / 2);
    int dstStride = Read32(df) * 4 + 8, srcStride = Read32(sf) * 4 + 8;
    int sx = v[8], sy = v[9], sxEnd = sx + v[10], syEnd = sy + v[11];
    int tx = v[12], ty = v[13];
    double angle = AsFloat(v[14]);
    double zoomX = AsFloat(v[15]), zoomY = AsFloat(v[16]);
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
    r = std::min(r, fw);
    b = std::min(b, fh);

    double radians = angle * (std::numbers::pi / 180);
    int64_t sin = TruncateToLong(std::sin(radians) * 4294967296.0), cos = TruncateToLong(std::cos(radians) * 4294967296.0);
    // 64-bit sums that wrap, as the C# player's unchecked ones
    auto mul = [](int64_t a, int64_t b) { return uint64_t(a) * uint64_t(b); };
    int64_t u0 = DivideTruncated(int64_t(mul(cx, cos) - mul(cy, sin) + (uint64_t(int64_t(tx)) << 32)), zoomX);
    int64_t v0 = DivideTruncated(int64_t(mul(cy, cos) + mul(cx, sin) + (uint64_t(int64_t(ty)) << 32)), zoomY);
    int64_t dux = DivideTruncated(cos, zoomX), dvx = DivideTruncated(sin, zoomY);
    double turned = radians + std::numbers::pi / 2;
    int64_t duy = TruncateToLong(std::cos(turned) * 4294967296.0 / zoomX), dvy = TruncateToLong(std::sin(turned) * 4294967296.0 / zoomY);

    // Row by row as the x86 code draws it, reading the source as it goes (a frame turned in place
    // reads what it has just drawn)
    if (r - l <= 0 || b - t <= 0)
        return;
    std::vector<uint8_t> row(static_cast<size_t>((r - l) * 4));
    for (int yy = t; yy < b; yy++)
    {
        uint64_t u = mul(yy, duy) + uint64_t(u0), vv = mul(yy, dvy) + uint64_t(v0);
        int at = dstBase + yy * dstStride + l * 4;
        ReadBytes(at, row.data(), int(row.size()));
        for (size_t k = 0; k < row.size(); k += 4)
        {
            int fx = int(int64_t(u) >> 32), fy = int(int64_t(vv) >> 32);
            if (fx >= sx && fx < sxEnd && fy >= sy && fy < syEnd)
                LittleEndian::Pack(uint32_t(Read32(srcBase + fy * srcStride + fx * 4)), &row[k]);
            else if (!keep)
                LittleEndian::Pack(uint32_t(colour), &row[k]);
            u += uint64_t(dux);
            vv += uint64_t(dvx);
        }
        WriteBytes(at, row.data(), int(row.size()));
    }
}

} // namespace openshiina::scripting
