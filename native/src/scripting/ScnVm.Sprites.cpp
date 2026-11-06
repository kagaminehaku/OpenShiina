// C# Scripting/ScnVm.Sprites.cs and ScnVm.SpriteModes.cs: sprite lists and the compositor. A list
// is a table of op_04B5 (32-byte entries) with a count (0x7DDB90[list]); op_04B8 picks the list the
// other opcodes work on. An entry is
//   [0] picture slot  [1] frame  [2] flags  [3] priority  [4] x  [5] y  [6] -  [7] extra (+0x1C)
// flags: bit 31 shown, bits 26-27 special modes, bits 28-30 the blend mode, bits 0-8 its alpha.
// op_04C4 draws a list into a surface (FUN_00412040 -> FUN_00439E10): every shown entry in
// increasing priority (unsigned), each one row by row through its S25 runs (FUN_0043A040 and the
// path for CPUs with MMX and SSE, 0x4409E0 - the one every current PC takes). The modes:
//   bits 28-30 with bit 28 (0x10, 0x30, 0x50, 0x70): adds the sprite to the picture (0x442930)
//   0x40000000: blends; 0x20000000: tints; 0x60000000: paints the tint colour through the
//   sprite's shape (0x442070); 0x08000000: copies the sprite, alpha left out (0x442E8B);
//   0x04000000: draws the sprite's alpha as grey (0x4431C0)
// The C# player draws a large frame's rows on all cores and some pixels four at a time with
// vectors; here they go one at a time for now (the same bytes).
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

namespace {

constexpr int SpriteSize = 0x20;

struct ComposeTarget
{
    int Pixels, Pitch, OriginX, OriginY, Width, Height;
};

uint8_t AddSaturated(int d, int v) { return uint8_t(std::min(255, d + v)); }

void BlendByte(uint8_t* row, int at, int source, int alpha)
{
    int d = row[at];
    row[at] = uint8_t(((source - d) * alpha >> 8) + d);
}

/// Where the data of a run ends (methods 0-1 none, 2 BGR each, 3 one BGR, 4 ABGR each, 5+ one ABGR).
int RunEnd(int method, int count, int data)
{
    return method < 2 ? data : method == 2 ? data + count * 3 : method == 3 ? data + 3 : method == 4 ? data + count * 4 : data + 4;
}

struct Run
{
    int Method, Count, Data;
};

/// A run header at offset o of a row read into row from address (headers are 2-byte aligned in memory).
Run ReadRunAt(const uint8_t* row, int address, int o)
{
    o = ((address + o + 1) & ~1) - address;
    int code = row[o] | row[o + 1] << 8;
    int data = o + 2 + ((code >> 11) & 3);
    int count = code & 0x7FF;
    if (count == 0)
    {
        count = int(LittleEndian::ToUInt32(row + data));
        data += 4;
    }
    return {code >> 13, count, data};
}

/// A run of one colour (method 3): the first pixel, then what is filled copied after itself.
void Fill24(uint8_t* target, int length, uint8_t b, uint8_t g, uint8_t r)
{
    if (length < 3)
        return;
    target[0] = b;
    target[1] = g;
    target[2] = r;
    for (int done = 3; done < length; done *= 2)
        std::memmove(target + done, target, size_t(std::min(done, length - done)));
}

/// Pixels with an alpha each (method 4; the alpha through table for blend 0x40000000): 0xFF copies
/// the colour, 0 leaves the picture, else d + ((s - d) a >> 8)
void DrawAlphaPixels(int n, const uint8_t* src, int s, uint8_t* row, int d, const uint8_t* table)
{
    for (int i = 0; i < n; i++, s += 4, d += 3)
    {
        int alpha = table ? table[src[s]] : src[s];
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
}

} // namespace

/// A blend mode and its tables (FUN_0044346A): for alpha a, T1 = a / 256 and T2 = (256 - a) / 256
/// stepped like FUN_004396B4, MMX weights a and 256 - a. 0x10000000 keeps a up to 0x1FF; the other
/// modes stop at 0x100.
struct ScnVm::SpriteBlend
{
    enum Kind
    {
        Alpha,
        Tint,
        Add,
        Silhouette,
        Opaque,
        Mask,
    };

    Kind Mode;
    int AlphaValue = 0;
    uint8_t T1[256] = {}, T2[256] = {};
    /// Modes 0x20000000 and 0x60000000: the tint colour (entry +0x1C: blue, green, red) and T1 of each.
    int TintColor[3] = {}, TintPart[3] = {};

    explicit SpriteBlend(Kind kind, int alpha = 0, int tint = 0) : Mode(kind), AlphaValue(alpha)
    {
        if (kind == Opaque || kind == Mask)
            return;
        StepTable(T1, alpha, 256);
        StepTable(T2, 256 - alpha, 256);
        TintColor[0] = tint & 0xFF;
        TintColor[1] = (tint >> 8) & 0xFF;
        TintColor[2] = (tint >> 16) & 0xFF;
        for (int k = 0; k < 3; k++)
            TintPart[k] = T1[TintColor[k]];
    }
};

int ScnVm::SpriteEntry(int list, int index) const { return TableAddress(list) + index * SpriteSize; }

void ScnVm::Compose(int sprites, int count, int pixels, int width, int height, int pitch, const int* clip)
{
    // FUN_00439E10: draws count entries at sprites into a 24-bit picture, clipped to clip (left, top,
    // right, bottom) or the whole picture
    if (count == 0)
        return;
    int originX = 0, originY = 0, clipW = width, clipH = height;
    if (clip)
    {
        if (clip[0] >= width || clip[1] >= height || clip[2] <= 0 || clip[3] <= 0)
            return;
        int l = std::max(clip[0], 0), t = std::max(clip[1], 0);
        clipW = std::min(clip[2], width) - l;
        clipH = std::min(clip[3], height) - t;
        originX = l;
        originY = t;
        pixels += t * pitch + l * 3;
    }
    ComposeTarget target{pixels, pitch, originX, originY, clipW, clipH};
    // In order of priority (unsigned), entries of the same priority in list order - the order of
    // FUN_00439E10's passes. Drawing does not change the entries, so they are gathered once
    std::vector<std::pair<uint64_t, int>> order;
    for (int i = 0; i < count; i++)
    {
        int e = sprites + i * SpriteSize;
        int flags = Read32(e + 8);
        if (flags >= 0 || !SpriteDrawable(flags))
            continue;
        order.emplace_back(uint64_t(uint32_t(Read32(e + 0xC))) << 32 | uint32_t(i), flags);
    }
    std::sort(order.begin(), order.end(), [](const auto& x, const auto& y) { return x.first < y.first; });
    for (const auto& [key, flags] : order)
        DrawSprite(sprites + int(uint32_t(key)) * SpriteSize, flags, &target);
}

bool ScnVm::SpriteDrawable(int& flags)
{
    // The checks of FUN_00439E10 on an entry's flags; may turn a blend into a plain copy
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
            flags &= int(0x8FFFFFFF);
        return true;
    case 0x50000000:
    case 0x60000000:
    case 0x70000000:
        return alpha != 0;
    default:   // 0x20000000, 0x30000000
        if (alpha == 0)
            flags &= int(0x8FFFFFFF);
        return true;
    }
}

void ScnVm::DrawSprite(int entry, int flags, const void* targetPointer)
{
    // FUN_0043A040: clip the frame to the target, then draw its rows
    const auto& target = *static_cast<const ComposeTarget*>(targetPointer);
    int picture = Picture(Read32(entry));
    uint32_t frameIndex = uint32_t(Read32(entry + 4));
    if (picture == 0 || frameIndex >= uint32_t(Read32(picture + 4)))
        return;
    int frame = Read32(picture + 8 + 4 * int(frameIndex));
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

    // The modes as 0x4409E0 sends them on: bits 26-27 first (0x08000000 before 0x04000000), then any
    // mode with bit 28 adds, 0x40000000 blends, 0x60000000 paints the tint, and 0x20000000 tints
    int alpha = flags & 0x1FF, tint = Read32(entry + 0x1C);
    std::optional<SpriteBlend> blend;
    if ((flags & 0x0C000000) != 0)
        blend.emplace((flags & 0x08000000) != 0 ? SpriteBlend::Opaque : SpriteBlend::Mask);
    else
        switch (flags & 0x70000000)
        {
        case 0:
            break;
        case 0x40000000:
            blend.emplace(SpriteBlend::Alpha, alpha);
            break;
        case 0x20000000:
            blend.emplace(SpriteBlend::Tint, std::min(alpha, 0x100), tint);
            break;
        case 0x60000000:
            blend.emplace(SpriteBlend::Silhouette, std::min(alpha, 0x100), tint);
            break;
        case 0x10000000:
            blend.emplace(SpriteBlend::Add, alpha);
            break;
        default:
            blend.emplace(SpriteBlend::Add, std::min(alpha, 0x100));
            break;
        }
    const SpriteBlend* mode = blend ? &*blend : nullptr;
    for (; rows > 0; rows--, row += 4, dst += target.Pitch)
    {
        if (mode && mode->Mode == SpriteBlend::Silhouette)
            DrawRowSilhouette(Read32(row), dst, skip, visible, *mode);
        else
            DrawRowPlain(Read32(row), dst, skip, visible, mode);
    }
}

ScnVm::RunHeader ScnVm::ReadRun(int p)
{
    // A run header at address p (2-byte aligned): method, count, data address
    p = (p + 1) & ~1;
    int code = Read16(p);
    int data = p + 2 + ((code >> 11) & 3);
    int count = code & 0x7FF;
    if (count == 0)
    {
        count = Read32(data);
        data += 4;
    }
    return {code >> 13, count, data};
}

const uint8_t* ScnVm::ReadRow(int p, int dst, int bytes, int pixels)
{
    // The row of a frame from its row pointer (its 16-bit length and the runs after it), read in
    // place; copied first when it shares bytes with the destination row (or runs past the top of
    // the 4 GB with the 16 bytes read past its runs). A length of 0 is a row the scripts made
    // themselves without one: its runs are walked over for its pixels
    int n = Read16(p) + 16;
    if (n == 16)
    {
        int at = p + 2;
        for (int seen = 0; seen < pixels;)
        {
            auto run = ReadRun(at);
            if (run.Count <= 0)
                break;
            at = RunEnd(run.Method, run.Count, run.Data);
            seen += run.Count;
        }
        n = at - p + 16;
    }
    if ((int64_t(p) + n <= dst || int64_t(dst) + bytes <= p) && IsContiguous(p, n))
        return Bytes(p, n);
    if (m_rowSource.size() < size_t(n))
        m_rowSource.resize(std::max<size_t>(size_t(n), 0x4000));
    ReadBytes(p, m_rowSource.data(), n);
    return m_rowSource.data();
}

void ScnVm::DrawRowPlain(int p, int dst, int skip, int visible, const SpriteBlend* blend)
{
    // One row (0x4409F6, or 0x440F9C with a blend): skip pixels of runs, then draw up to visible.
    // MMX alignment follows the destination's address
    if (visible <= 0)
        return;
    int rowBytes = visible * 3;
    const uint8_t* src = ReadRow(p, dst, rowBytes, skip + visible);
    // drawn straight into script memory
    uint8_t* row = Bytes(dst, rowBytes);
    int o = 2, d = 0;
    int remaining = visible;
    // Left clip: whole runs are passed over, a run that crosses the edge is drawn from it
    while (skip > 0)
    {
        auto run = ReadRunAt(src, p, o);
        o = RunEnd(run.Method, run.Count, run.Data);
        skip -= run.Count;
        if (skip > 0)
            continue;
        if (skip == 0)
            break;
        int part = -skip, start = run.Count - part;
        int at = run.Method == 2 ? run.Data + start * 3 : run.Method == 4 ? run.Data + start * 4 : run.Data;
        if (run.Method < 2)
        {
            d += part * 3;
            remaining -= part;
        }
        else
        {
            int n = std::min(part, remaining);
            DrawRun(run.Method, n, src, at, row, d, dst + d, blend);
            d += n * 3;
            remaining -= n;
        }
        if (remaining <= 0)
            return;
        break;
    }
    while (remaining > 0)
    {
        auto run = ReadRunAt(src, p, o);
        o = RunEnd(run.Method, run.Count, run.Data);
        if (run.Method < 2)
        {
            d += run.Count * 3;
            remaining -= run.Count;
            continue;
        }
        int n = std::min(run.Count, remaining);
        DrawRun(run.Method, n, src, run.Data, row, d, dst + d, blend);
        d += n * 3;
        remaining -= n;
    }
}

void ScnVm::DrawRun(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend* blend)
{
    // A run into the row: n pixels from src[s] to row[d] (row[d] is at memory address address)
    if (blend)
        switch (blend->Mode)
        {
        case SpriteBlend::Alpha:
            DrawRunAlpha(method, n, src, s, row, d, address, *blend);
            return;
        case SpriteBlend::Tint:
            DrawRunTint(method, n, src, s, row, d, address, *blend);
            return;
        case SpriteBlend::Add:
            DrawRunAdd(method, n, src, s, row, d, address, *blend);
            return;
        case SpriteBlend::Opaque:
            DrawRunOpaque(method, n, src, s, row, d);
            return;
        case SpriteBlend::Mask:
            DrawRunMask(method, n, src, s, row, d);
            return;
        default:
            break;
        }
    switch (method)
    {
    case 2:
        std::memmove(row + d, src + s, size_t(n) * 3);
        return;
    case 3:
        Fill24(row + d, n * 3, src[s], src[s + 1], src[s + 2]);
        return;
    case 4:
        DrawAlphaPixels(n, src, s, row, d, nullptr);
        return;
    default:
    {
        int a = src[s];
        uint8_t b = src[s + 1], g = src[s + 2], r = src[s + 3];
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

void ScnVm::DrawRunAlpha(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend& blend)
{
    // The runs of mode 0x40000000 (0x440F9C: the MMX + SSE path)
    switch (method)
    {
    case 2:
    {
        // two sources: the run's pixels at a, the picture at 256 - a
        int a = blend.AlphaValue, b = 256 - a;
        auto table = [&](int i) { return uint8_t(blend.T1[src[s + i]] + blend.T2[row[d + i]]); };
        auto block = [&](int i) { return uint8_t(std::min(255, (src[s + i] * a + row[d + i] * b & 0xFFFF) >> 8)); };
        int left = n * 3, at = 0, head = -address & 7;
        if (head != 0)
        {
            for (int h = head & 3; h > 0 && left > 0; h--, at++, left--)
                row[d + at] = table(at);
            if (left > 0 && left < 4)
            {
                for (; left > 0; at++, left--)
                    row[d + at] = table(at);
            }
            else if (left > 0 && (head & 4) != 0)
            {
                for (int q = 0; q < 4; q++)
                    row[d + at + q] = block(at + q);
                at += 4;
                left -= 4;
            }
        }
        for (; left >= 8; at += 8, left -= 8)
            for (int q = 0; q < 8; q++)
                row[d + at + q] = block(at + q);
        if (left >= 4)
        {
            for (int q = 0; q < 4; q++)
                row[d + at + q] = block(at + q);
            at += 4;
            left -= 4;
        }
        for (; left > 0; at++, left--)
            row[d + at] = table(at);
        return;
    }
    case 3:
    {
        // one colour scaled by a, the picture by the table / MMX at 256 - a
        int a = blend.AlphaValue, b = 256 - a;
        uint8_t c0 = uint8_t(src[s] * a >> 8), c1 = uint8_t(src[s + 1] * a >> 8), c2 = uint8_t(src[s + 2] * a >> 8);
        auto tablePixel = [&](int at) {
            row[at] = uint8_t(blend.T2[row[at]] + c0);
            row[at + 1] = uint8_t(blend.T2[row[at + 1]] + c1);
            row[at + 2] = uint8_t(blend.T2[row[at + 2]] + c2);
        };
        int left = n;
        if (left < 15)
        {
            for (; left > 0; left--, d += 3)
                tablePixel(d);
            return;
        }
        int abs = address;
        for (; (abs & 7) != 0; left--, d += 3, abs += 3)
            tablePixel(d);
        for (int groups = left >> 3; groups > 0; groups--, d += 24, abs += 24)
            for (int q = 0; q < 24; q++)
            {
                uint8_t c = q % 3 == 0 ? c0 : q % 3 == 1 ? c1 : c2;
                row[d + q] = uint8_t(std::min(255, row[d + q] * b >> 8) + c);
            }
        for (left &= 7; left > 0; left--, d += 3)
            tablePixel(d);
        return;
    }
    case 4:
        DrawAlphaPixels(n, src, s, row, d, blend.T1);
        return;
    default:
    {
        int alpha = blend.T1[src[s]];
        uint8_t b0 = src[s + 1], g0 = src[s + 2], r0 = src[s + 3];
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

void ScnVm::DrawRunTint(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend& blend)
{
    // The runs of mode 0x20000000 (0x441710): the sprite's colours are mixed with the tint colour t,
    // c' = T2[c] + T1[t], then drawn like plain runs. BGR runs of 15 pixels or more take MMX for
    // whole groups of 8 pixels from a 4-byte aligned pixel: c + ((t - c) * a >> 8) in 16-bit words
    const int* tint = blend.TintColor;
    const int* part = blend.TintPart;
    const uint8_t* t2 = blend.T2;
    switch (method)
    {
    case 2:
    {
        auto table = [&](int pixels) {
            for (int k = 0; k < pixels * 3; k++, s++, d++, address++)
                row[d] = uint8_t(t2[src[s]] + part[k % 3]);
        };
        if (n < 15)
        {
            table(n);
            return;
        }
        int head = address & 3;
        table(head);
        n -= head;
        if ((address & 4) != 0)
        {
            table(4);
            n -= 4;
        }
        int a = blend.AlphaValue;
        for (int groups = n >> 3; groups > 0; groups--)
            for (int k = 0; k < 24; k++, s++, d++, address++)
            {
                int c = src[s];
                row[d] = uint8_t((((tint[k % 3] - c) * a & 0xFFFF) >> 8) + c);
            }
        n &= 7;
        if (n >= 4)
        {
            table(4);
            n -= 4;
        }
        table(n);
        return;
    }
    case 3:
    {
        uint8_t b = uint8_t(t2[src[s]] + part[0]), g = uint8_t(t2[src[s + 1]] + part[1]), r = uint8_t(t2[src[s + 2]] + part[2]);
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

void ScnVm::DrawRunAdd(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend& blend)
{
    // Mode 0x10000000 and the others with bit 28 (0x442930): d + T1[c], saturated. BGR runs of 5
    // pixels or more take MMX for whole 8-byte groups from an 8-byte aligned byte: (c * a & 0xFFFF) >> 8
    const uint8_t* t1 = blend.T1;
    switch (method)
    {
    case 2:
    {
        int bytes = n * 3, k = 0;
        if (n >= 5)
        {
            for (; ((address + k) & 7) != 0; k++)
                row[d + k] = AddSaturated(row[d + k], t1[src[s + k]]);
            int a = blend.AlphaValue;
            for (int end = k + ((bytes - k) & ~7); k < end; k++)
                row[d + k] = AddSaturated(row[d + k], (src[s + k] * a & 0xFFFF) >> 8);
        }
        for (; k < bytes; k++)
            row[d + k] = AddSaturated(row[d + k], t1[src[s + k]]);
        return;
    }
    case 3:
    {
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

void ScnVm::DrawRunOpaque(int method, int n, const uint8_t* src, int s, uint8_t* row, int d)
{
    // Mode 0x08000000 (0x442E8B): the colours, alpha left out. One-colour ABGR runs write blue,
    // green, blue
    switch (method)
    {
    case 2:
        std::memmove(row + d, src + s, size_t(n) * 3);
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

void ScnVm::DrawRunMask(int method, int n, const uint8_t* src, int s, uint8_t* row, int d)
{
    // Mode 0x04000000 (0x4431C0): the alpha as grey; colour runs are opaque (white)
    switch (method)
    {
    case 2:
    case 3:
        std::memset(row + d, 0xFF, size_t(n) * 3);
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

void ScnVm::DrawRowSilhouette(int p, int dst, int skip, int visible, const SpriteBlend& blend)
{
    // One row of mode 0x60000000 (0x442070): the tint colour t through the sprite's shape. ABGR runs
    // draw at once, d + ((t - d) * T1[alpha] >> 8). Opaque runs (BGR, methods 2 and 3) are only
    // counted and drawn later (0x4424C0) where the next run that is not of method 1-3 starts, or at
    // the end of the row - after a method 1 run that is further on
    if (visible <= 0)
        return;
    int rowBytes = visible * 3;
    const uint8_t* src = ReadRow(p, dst, rowBytes, skip + visible);
    uint8_t* row = Bytes(dst, rowBytes);
    int o = 2, d = 0, remaining = visible, pending = 0;
    // Left clip: the run that crosses the edge goes on from there
    while (skip > 0)
    {
        auto run = ReadRunAt(src, p, o);
        o = RunEnd(run.Method, run.Count, run.Data);
        skip -= run.Count;
        if (skip > 0)
            continue;
        if (skip == 0)
            break;
        int part = -skip, start = run.Count - part;
        int n = std::min(part, remaining);
        switch (run.Method)
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
            PaintTint(run.Method, n, src, run.Method == 4 ? run.Data + start * 4 : run.Data, row, d, blend);
            d += n * 3;
            remaining -= n;
            break;
        }
        break;
    }
    while (remaining > 0)
    {
        auto run = ReadRunAt(src, p, o);
        o = RunEnd(run.Method, run.Count, run.Data);
        if (pending > 0 && run.Method != 1 && run.Method != 2 && run.Method != 3)
        {
            PaintPending(pending, row, d, dst + d, blend);
            d += pending * 3;
            pending = 0;
        }
        if (run.Method < 2)
        {
            d += run.Count * 3;
            remaining -= run.Count;
            continue;
        }
        int n = std::min(run.Count, remaining);
        if (run.Method < 4)
            pending += n;
        else
        {
            PaintTint(run.Method, n, src, run.Data, row, d, blend);
            d += n * 3;
        }
        remaining -= n;
    }
    // A method 1 run at the end can take the drawing past the row (even past the top of the 4 GB:
    // then through a copy)
    if (pending > 0 && IsContiguous(dst + d, pending * 3))
        PaintPending(pending, Bytes(dst + d, pending * 3), 0, dst + d, blend);
    else if (pending > 0)
    {
        auto bytes = ReadBytes(dst + d, pending * 3);
        PaintPending(pending, bytes.data(), 0, dst + d, blend);
        WriteBytes(dst + d, bytes);
    }
}

void ScnVm::PaintTint(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, const SpriteBlend& blend)
{
    // ABGR runs of mode 0x60000000: each pixel's alpha gives the weight of the tint
    for (int i = 0; i < n; i++, d += 3)
    {
        int weight = blend.T1[src[s]];
        for (int k = 0; k < 3; k++)
            row[d + k] = uint8_t(row[d + k] + ((blend.TintColor[k] - row[d + k]) * weight >> 8));
        if (method == 4)
            s += 4;
    }
}

void ScnVm::PaintPending(int n, uint8_t* row, int d, int address, const SpriteBlend& blend)
{
    // 0x4424C0: n opaque pixels of mode 0x60000000 at row[d]. Under 15 pixels, or before a 4-byte
    // aligned pixel, T2[d] + T1[t]; then 4 pixels as three dwords of those sums (carries cross the
    // bytes) to reach 8-byte alignment; whole groups of 8 pixels in MMX, d + ((t - d) * a >> 8); then
    // 4 pixels as dwords and the rest from the tables
    const uint8_t* t2 = blend.T2;
    const int* part = blend.TintPart;
    const int* tint = blend.TintColor;
    int start = d;
    auto table = [&](int pixels) {
        for (; pixels > 0; pixels--, d += 3)
        {
            row[d] = uint8_t(t2[row[d]] + part[0]);
            row[d + 1] = uint8_t(t2[row[d + 1]] + part[1]);
            row[d + 2] = uint8_t(t2[row[d + 2]] + part[2]);
        }
    };
    auto dwords = [&] {
        for (int j = 0; j < 3; j++, d += 4)
        {
            uint32_t v = 0, add = 0;
            for (int k = 0; k < 4; k++)
            {
                v |= uint32_t(t2[row[d + k]]) << 8 * k;
                add |= uint32_t(part[(j * 4 + k) % 3]) << 8 * k;
            }
            uint32_t sum = v + add;
            std::memcpy(row + d, &sum, 4);
        }
    };
    if (n < 15)
    {
        table(n);
        return;
    }
    int head = address & 3;
    table(head);
    n -= head;
    if (((address + d - start) & 4) != 0)
    {
        dwords();
        n -= 4;
    }
    int a = blend.AlphaValue;
    for (int groups = n >> 3; groups > 0; groups--)
        for (int q = 0; q < 24; q++, d++)
            row[d] = uint8_t(row[d] + ((tint[q % 3] - row[d]) * a >> 8));
    n &= 7;
    if (n >= 4)
    {
        dwords();
        n -= 4;
    }
    table(n);
}

std::optional<std::array<int, 4>> ScnVm::FrameRect(int slot, int frame)
{
    // FUN_004116A0: a frame's rectangle (x, y, x + width, y + height), or none
    int picture = Picture(slot);
    if (picture == 0 || uint32_t(Read32(picture + 4)) <= uint32_t(frame))
        return std::nullopt;
    int f = Read32(picture + 8 + 4 * frame);
    if (f == 0)
        return std::nullopt;
    int x = Read32(f + 8), y = Read32(f + 0xC);
    return std::array<int, 4>{x, y, x + Read32(f), y + Read32(f + 4)};
}

std::optional<std::array<int, 4>> ScnVm::FrameRectOrLast(int slot, int frame)
{
    // FrameRect as 04C8 and 04CB take it. Before 2.40 a missing frame is no error: the handler stores
    // a rectangle on the stack that the frame lookup leaves unwritten, here the last one found
    if (auto r = FrameRect(slot, frame))
        return m_lastFrameRect = *r;
    if (EngineVersion < 240)
        return m_lastFrameRect;
    return std::nullopt;
}

bool ScnVm::HitTest(int sprites, int count, int x, int y, int& hit)
{
    // FUN_004114B0: from the highest priority down (unsigned; shown or not), the first entry whose
    // frame has a run of method 1 or more under (x, y) gives hit = its word 6. An entry with a
    // missing picture or frame ends the search
    for (uint32_t current = 0xFFFFFFFF;;)
    {
        uint32_t next = 0;
        bool more = false;
        for (int i = count - 1; i >= 0; i--)
        {
            int e = sprites + i * SpriteSize;
            uint32_t priority = uint32_t(Read32(e + 0xC));
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
            uint32_t frameIndex = uint32_t(Read32(e + 4));
            if (uint32_t(Read32(picture + 4)) <= frameIndex)
                return false;
            int f = Read32(picture + 8 + 4 * int(frameIndex));
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
                auto run = ReadRun(p);
                end += run.Count;
                p = RunEnd(run.Method, run.Count, run.Data);
                if (x < end)
                {
                    if (run.Method != 0)
                    {
                        hit = Read32(e + 0x18);
                        return true;
                    }
                    break;
                }
                remaining -= run.Count;
                if (remaining == 0)
                    break;
            }
        }
        if (!more)
            return true;
        current = next;
    }
}

void ScnVm::StretchCopy(int dst, int x, int y, int w, int h, int src, int sx, int sy, int sw, int sh)
{
    // StretchBlt SRCCOPY: a copy when the sizes match, else nearest pixels
    if (w == sw && h == sh)
    {
        BitBlt(dst, x, y, w, h, src, sx, sy);
        return;
    }
    if (w == 0 || h == 0 || sw == 0 || sh == 0)
        return;
    int dw = SurfaceField(dst, 7), dh = SurfaceField(dst, 8), dp = SurfaceField(dst, 10), d0 = SurfaceField(dst, 2);
    int swid = SurfaceField(src, 7), shei = SurfaceField(src, 8), spitch = SurfaceField(src, 10), s0 = SurfaceField(src, 2);
    for (int row = 0; row < std::abs(h); row++)
    {
        int ty = y + row;
        int fy = sy + int(int64_t(row) * sh / h);
        if (ty < 0 || ty >= dh || fy < 0 || fy >= shei)
            continue;
        for (int col = 0; col < std::abs(w); col++)
        {
            int tx = x + col, fx = sx + int(int64_t(col) * sw / w);
            if (tx < 0 || tx >= dw || fx < 0 || fx >= swid)
                continue;
            CopyPixel24(d0 + ty * dp + tx * 3, s0 + fy * spitch + fx * 3);
        }
    }
}

void ScnVm::CopyPixel24(int dst, int src)
{
    uint8_t b = ReadByte(src), g = ReadByte(src + 1), r = ReadByte(src + 2);
    WriteByte(dst, b);
    WriteByte(dst + 1, g);
    WriteByte(dst + 2, r);
}

void ScnVm::SoftStretch(int dst, int dx, int dy, int dw, int dh, int src, int sx, int sy, int sw, int sh)
{
    // The engine's stretch (051E with bit 31): 16.16 steps, a table of byte steps per column of the
    // destination, rows from the scaled source row (FUN_0041E180, quirks kept)
    if (dw == 0 || dh == 0)
        return;
    int xstep = int((int64_t(sw) << 16) / dw), ystep = int((int64_t(sh) << 16) / dh);
    int width = SurfaceField(dst, 7);
    std::vector<int> table(size_t(std::max(width, 0)));
    int previous = sx * 3;
    for (int i = 0, acc = 0; i < width; i++, acc += xstep)
    {
        int position = ((acc >> 16) + sx) * 3;
        table[size_t(i)] = position - previous;
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
    int endX = std::min(dx + dw, width);
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
    int endY = std::min(dy + dh, ScreenHeight);
    int srcPitch = SurfaceField(src, 10), dstPitch = SurfaceField(dst, 10);
    int srcBase = SurfaceField(src, 2) + skipX * 3 + srcPitch * sy;
    int dstRow = SurfaceField(dst, 2) + dstPitch * startY + startX * 3;
    for (int y = startY; y < endY; y++, rowAcc += ystep, dstRow += dstPitch)
    {
        int p = (rowAcc >> 16) * srcPitch + srcBase, q = dstRow;
        for (int x = startX; x < endX; x++, q += 3)
        {
            p += table[size_t(x)];
            CopyPixel24(q, p);
        }
    }
}

void ScnVm::RegisterSprites()
{
    // 04B8 n: the list the sprite opcodes use; 04B9 n, v: v = list n's table
    Register(0x04B8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.m_spriteListSet = vm.Value(c, i.Args[0]);
        vm.m_spriteList = vm.m_spriteListSet & (SpriteLists - 1);
        return 0;
    });
    // 04B7 v: v = the sprite list 04B8 chose, as it was given (0x0D0AC14)
    Register(0x04B7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.m_spriteListSet);
        return 0;
    });
    Register(0x04B9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[1], vm.TableAddress(vm.Value(c, i.Args[0])));
        return 0;
    });
    // 04BA: empty the list; 04BB n: n entries; 04BC v: v = how many
    Register(0x04BA, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        vm.m_spriteCounts[size_t(vm.m_spriteList)] = 0;
        return 0;
    });
    Register(0x04BB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.m_spriteCounts[size_t(vm.m_spriteList)] = vm.Value(c, i.Args[0]);
        return 0;
    });
    Register(0x04BC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.m_spriteCounts[size_t(vm.m_spriteList)]);
        return 0;
    });
    // 04BD slot, frame, flags, priority, x, y, a, b: add an entry (FUN_00411FE0)
    Register(0x04BD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int list = vm.m_spriteList;
        int e = vm.SpriteEntry(list, vm.m_spriteCounts[size_t(list)]);
        for (int k = 0; k < 8; k++)
            vm.Write32(e + 4 * k, vm.Value(c, i.Args[size_t(k)]));
        vm.m_spriteCounts[size_t(list)]++;
        return 0;
    });
    // 04C4 n: draw the list into surface n
    Register(0x04C4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        if (n < 0 || n >= SurfaceCount || vm.SurfaceField(n, 2) == 0)
            return 0;
        int list = vm.m_spriteList;
        vm.Compose(vm.TableAddress(list), vm.m_spriteCounts[size_t(list)], vm.SurfaceField(n, 2), vm.SurfaceField(n, 7), vm.SurfaceField(n, 8),
                   vm.SurfaceField(n, 10), nullptr);
        return 0;
    });
    // 04C5 n, l, t, r, b: draw the list into surface n, clipped to the rectangle
    Register(0x04C5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        int clip[4] = {vm.Value(c, i.Args[1]), vm.Value(c, i.Args[2]), vm.Value(c, i.Args[3]), vm.Value(c, i.Args[4])};
        if (n < 0 || n >= SurfaceCount || vm.SurfaceField(n, 2) == 0)
            return 0;
        int list = vm.m_spriteList;
        vm.Compose(vm.TableAddress(list), vm.m_spriteCounts[size_t(list)], vm.SurfaceField(n, 2), vm.SurfaceField(n, 7), vm.SurfaceField(n, 8),
                   vm.SurfaceField(n, 10), clip);
        return 0;
    });
    // 04C7 x, y, v: v = word 6 of the list entry with a visible pixel at (x, y), -1 for none
    Register(0x04C7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
        int list = vm.m_spriteList;
        int hit = -1;
        vm.HitTest(vm.TableAddress(list), vm.m_spriteCounts[size_t(list)], x, y, hit);
        vm.Store(c, i.Args[2], hit);
        return 0;
    });
    // 04C8 slot, frame, l, t, r, b: the frame's rectangle; 04CB slot, frame, w, h: its size (a
    // missing frame is a script error; before 2.40 no error, see FrameRectOrLast)
    Register(0x04C8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto r = vm.FrameRectOrLast(vm.Value(c, i.Args[0]), vm.Value(c, i.Args[1]));
        if (!r)
            return 2;
        vm.Store(c, i.Args[2], (*r)[0]);
        vm.Store(c, i.Args[3], (*r)[1]);
        vm.Store(c, i.Args[4], (*r)[2]);
        vm.Store(c, i.Args[5], (*r)[3]);
        return 0;
    });
    Register(0x04CB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto r = vm.FrameRectOrLast(vm.Value(c, i.Args[0]), vm.Value(c, i.Args[1]));
        if (!r)
            return 2;
        vm.Store(c, i.Args[2], (*r)[2] - (*r)[0]);
        vm.Store(c, i.Args[3], (*r)[3] - (*r)[1]);
        return 0;
    });
    // 04C9 slot, frame, v: v = 1 when the picture has that frame
    Register(0x04C9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int picture = vm.Picture(vm.Value(c, i.Args[0]));
        uint32_t frame = uint32_t(vm.Value(c, i.Args[1]));
        bool has = picture != 0 && uint32_t(vm.Read32(picture + 4)) > frame && vm.Read32(picture + 8 + 4 * int(frame)) != 0;
        vm.Store(c, i.Args[2], has ? 1 : 0);
        return 0;
    });
    // 04D3 v / 04D4 v: engine settings 0x4880B0 / 0x4880B4
    Register(0x04D3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.EngineGlobals[0x4880B0] = vm.Value(c, i.Args[0]);
        return 0;
    });
    Register(0x04D4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.EngineGlobals[0x4880B4] = vm.Value(c, i.Args[0]);
        return 0;
    });
    // 051E dst, x, y, w, h, src, sx, sy, sw, sh, rop (FUN_0041E180): StretchBlt; with bit 31 of rop
    // the engine's own nearest-pixel stretch
    Register(0x051E, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[11];
        for (int k = 0; k < 11; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        int dst = v[0], src = v[5];
        if (v[10] < 0)
            vm.SoftStretch(dst, v[1], v[2], v[3], v[4], src, v[6], v[7], v[8], v[9]);
        else if (v[10] == 0xCC0020)
            vm.StretchCopy(dst, v[1], v[2], v[3], v[4], src, v[6], v[7], v[8], v[9]);
        else
            return 2;
        if (dst == vm.DisplaySurface())
        {
            vm.PresentedByDirect3D(v[1], v[2], v[1] + v[3], v[2] + v[4]);
            // v2.34's 051E invalidates what it drew into surface 0 itself
            if (vm.OldEngine())
                vm.InvalidateWindow(std::min(v[1], v[1] + v[3]), std::min(v[2], v[2] + v[4]), std::max(v[1], v[1] + v[3]),
                                    std::max(v[2], v[2] + v[4]));
            vm.FrameShown = true;
        }
        return 0;
    });
    // 04E2 dst, x, y, w, h, src, sx, sy: copy a rectangle between surfaces (FUN_00417900), clipped to
    // the destination only
    Register(0x04E2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
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
        r = std::min(r, dw);
        b = std::min(b, dh);
        int bytes = vm.SurfaceField(srcSurface, 9) >> 3;
        int width = r - l, rows = b - t;
        if (width == 0 || rows == 0)
            return 0;
        int srcPitch = vm.SurfaceField(srcSurface, 10), dstPitch = vm.SurfaceField(dstSurface, 10);
        int src = vm.SurfaceField(srcSurface, 2) + sy * srcPitch + sx * bytes;
        int dst = vm.SurfaceField(dstSurface, 2) + t * dstPitch + l * (vm.SurfaceField(dstSurface, 9) >> 3);
        for (; rows > 0; rows--, src += srcPitch, dst += dstPitch)
            vm.CopyMemory(dst, src, bytes * width);
        if (dstSurface == vm.DisplaySurface())
        {
            // Not the original's (FUN_00417900 only draws): the window is repainted where the copy went
            vm.InvalidateWindow(l, t, r, b);
            vm.FrameShown = true;
        }
        return 0;
    });
}

} // namespace openshiina::scripting
