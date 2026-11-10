// C# Scripting/ScnVm.Layers.cs: drawing into 32-bit pictures ("layers": text and button pictures made
// by op_055A with 4 bytes a pixel, alpha first). FUN_004436F0 / 004437C0 draws one sprite entry over
// such a picture with "over" blending: a = 255 - (255 - sa)(255 - da)/255 done as (0xFE01 - (255-sa)
// (255-da)) * 0x10203 >> 24 in 32 bits, and each channel (s sa 255 + (255 - sa) da d) * (2^24 / a)
// >> 32. Source alpha 255 or a transparent destination pixel copies the source; BGR runs become opaque.
#include "scripting/ScnVm.h"
#include "scripting/ParallelRows.h"

#include <algorithm>
#include <cstring>

namespace openshiina::scripting {

namespace {

/// Where the data of a run ends (methods 0-1 none, 2 BGR each, 3 one BGR, 4 ABGR each, 5+ one ABGR;
/// ScnVm.Sprites.cpp's).
int RunEnd(int method, int count, int data)
{
    return method < 2 ? data : method == 2 ? data + count * 3 : method == 3 ? data + 3 : method == 4 ? data + count * 4 : data + 4;
}

struct Run
{
    int Method, Count, Data;
};

/// A run header at offset o of a row read into row from address (ScnVm.Sprites.cpp's).
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

/// 2^24 / a for each a of Over (its division done once).
struct Inverses
{
    uint32_t Of[256] = {};
    constexpr Inverses()
    {
        for (uint32_t a = 1; a < 256; a++)
            Of[a] = 0x1000000 / a;
    }
};
constexpr Inverses kInverses;

uint32_t Dword(const uint8_t* p)
{
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}

void PutDword(uint8_t* p, uint32_t v) { std::memcpy(p, &v, 4); }

/// One source pixel (alpha, blue, green, red from the low byte up) over a destination pixel.
uint32_t Over(uint32_t dst, uint32_t source)
{
    uint32_t sa = source & 0xFF;
    if (sa == 0)
        return dst;
    uint32_t da = dst & 0xFF;
    if (sa == 0xFF || da == 0)
        return source;
    uint32_t a = ((0xFE01 - (0xFF - sa) * (0xFF - da)) * 0x10203) >> 24;
    if (a == 0)
        return dst & 0xFFFFFF00;
    uint32_t inverse = kInverses.Of[a], result = a;
    for (int k = 8; k <= 24; k += 8)
    {
        uint32_t s = (source >> k) & 0xFF, d = (dst >> k) & 0xFF;
        uint32_t sum = s * sa * 0xFF + (0xFF - sa) * da * d;
        result |= uint32_t(uint64_t(sum) * inverse >> 32) << k;
    }
    return result;
}

} // namespace

void ScnVm::DrawOnPicture(int picture, int frame, int entry)
{
    // FUN_00411130: draws the sprite entry over a frame of a 32-bit picture
    if (picture == 0 || uint32_t(frame) > uint32_t(Read32(picture + 4)))
        return;
    int f = Read32(picture + 8 + 4 * frame);
    if (f == 0)
        return;
    int width = Read32(f), height = Read32(f + 4);
    int pixels = Read32(f + 0x14) + 8;
    DrawSprite32(entry, pixels, width, height, width * 4 + 8, picture);
}

void ScnVm::DrawSprite32(int entry, int pixels, int targetWidth, int targetHeight, int pitch, int targetPicture)
{
    // FUN_004437C0 (through 004436F0 with no clip rectangle): one entry, clipped to the target
    int picture = Picture(Read32(entry));
    uint32_t frameIndex = uint32_t(Read32(entry + 4));
    if (picture == 0 || frameIndex >= uint32_t(Read32(picture + 4)))
        return;
    int frame = Read32(picture + 8 + 4 * int(frameIndex));
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
    if (rows <= 0 || visible <= 0)
        return;
    // A frame of another picture: its rows go into rows of their own, shared out between the cores
    // when there are enough of them (a picture drawn over itself keeps to this thread)
    if (picture != targetPicture && ParallelRows::Parts(rows, int64_t(rows) * visible) > 1)
    {
        std::vector<int> pointers(static_cast<size_t>(rows));
        for (int i = 0; i < rows; i++)
            pointers[size_t(i)] = Read32(row + i * 4);
        ParallelRows::For(rows, int64_t(rows) * visible, [&](int first, int last) {
            for (int i = first; i < last; i++)
                DrawRow32(pointers[size_t(i)], dst + i * pitch, skip, visible);
        });
        return;
    }
    for (; rows > 0; rows--, row += 4, dst += pitch)
        DrawRow32(Read32(row), dst, skip, visible);
}

void ScnVm::DrawRow32(int p, int dst, int skip, int visible)
{
    // The row and the target's pixels in place (the row copied when it shares bytes with them)
    if (visible <= 0)
        return;
    const uint8_t* src = ReadRow(p, dst, visible * 4, skip + visible);
    uint8_t* target = Bytes(dst, visible * 4);
    int o = 2, d = 0;
    int remaining = visible;
    while (skip > 0)
    {
        auto [method, count, data] = ReadRunAt(src, p, o);
        o = RunEnd(method, count, data);
        skip -= count;
        if (skip > 0)
            continue;
        if (skip == 0)
            break;
        int part = -skip, start = count - part;
        int at = method == 2 ? data + start * 3 : method == 4 ? data + start * 4 : data;
        if (method < 2)
        {
            d += part * 4;
            remaining -= part;
        }
        else
        {
            int n = std::min(part, remaining);
            DrawRun32(method, n, src + at, target + d);
            d += n * 4;
            remaining -= n;
        }
        if (remaining <= 0)
            return;
        break;
    }
    while (remaining > 0)
    {
        auto [method, count, data] = ReadRunAt(src, p, o);
        o = RunEnd(method, count, data);
        if (method < 2)
        {
            d += count * 4;
            remaining -= count;
            continue;
        }
        int n = std::min(count, remaining);
        DrawRun32(method, n, src + data, target + d);
        d += n * 4;
        remaining -= n;
    }
}

void ScnVm::DrawRun32(int method, int n, const uint8_t* source, uint8_t* target)
{
    // A run drawn straight into the picture (pixels read and written unaligned)
    if (n <= 0)
        return;
    size_t length = size_t(n) * 4;
    switch (method)
    {
    case 2:
    {
        // 3-byte colours, opaque
        for (size_t i = 0, s = 0; i < length; i += 4, s += 3)
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
        uint32_t color = Dword(source) << 8 | 0xFF;
        for (size_t i = 0; i < length; i += 4)
            PutDword(target + i, color);
        break;
    }
    case 4:
    {
        // ABGR colours over the target
        for (size_t i = 0; i < length; i += 4)
            PutDword(target + i, Over(Dword(target + i), Dword(source + i)));
        break;
    }
    default:
    {
        // One ABGR colour over the target
        uint32_t color = Dword(source);
        for (size_t i = 0; i < length; i += 4)
            PutDword(target + i, Over(Dword(target + i), color));
        break;
    }
    }
}

void ScnVm::RegisterLayers()
{
    // 0562 picture, frame, x, y, source slot, source frame: draw a frame over a 32-bit picture
    // (FUN_0041F380: a shown entry with priority 0)
    Register(0x0562, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[6];
        for (int k = 0; k < 6; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        int entry = vm.LayerEntry();
        vm.Write32(entry, v[4]);
        vm.Write32(entry + 4, v[5]);
        vm.Write32(entry + 8, int(0x80000000u));
        vm.Write32(entry + 0xC, 0);
        vm.Write32(entry + 0x10, v[2]);
        vm.Write32(entry + 0x14, v[3]);
        vm.Write32(entry + 0x18, 0);
        vm.Write32(entry + 0x1C, 0);
        vm.DrawOnPicture(vm.Picture(v[0]), v[1], entry);
        return 0;
    });
    // 0563 dst, frame, x, y, A, frame, ax, ay, B, frame, bx, by, w, h, a, b (FUN_0041F410 ->
    // FUN_004111A0, v2.47): each byte of the 32-bit pixels (alpha too) A * a / 256 plus B * b / 256,
    // at most 255, into dst's frame. The rectangle is clipped to dst's frame (FUN_00417790), A's and
    // B's places moving with its left and top edges. Pure Love!'s cross-fade of two pictures
    Register(0x0563, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[16];
        for (int k = 0; k < 16; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        vm.BlendPictures(v);
        return 0;
    });
}

int ScnVm::PictureFrame(int slot, int frame)
{
    // A frame of a picture slot (its header address), or 0
    int picture = Picture(slot);
    if (picture == 0 || uint32_t(frame) > uint32_t(Read32(picture + 4)))
        return 0;
    return Read32(picture + 8 + 4 * frame);
}

void ScnVm::BlendPictures(const int* v)
{
    // FUN_004111A0 for op 0563
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
    r = std::min(r, dw);
    btm = std::min(btm, dh);
    int width = r - l, rows = btm - t;
    if (width <= 0 || rows <= 0)
        return;
    uint32_t fa = uint32_t(v[14]), fb = uint32_t(v[15]);
    openshiina::Bytes lineA(size_t(width) * 4), lineB(size_t(width) * 4), line(size_t(width) * 4);
    // the rows follow each other in a frame (each with an 8-byte head)
    int pa = Read32(a + 0x14 + 4 * ay) + 8 + 4 * ax, strideA = Read32(a) * 4 + 8;
    int pb = Read32(b + 0x14 + 4 * by) + 8 + 4 * bx, strideB = Read32(b) * 4 + 8;
    int pd = Read32(dst + 0x14 + 4 * t) + 8 + 4 * l, strideD = dw * 4 + 8;
    for (int y = 0; y < rows; y++, pa += strideA, pb += strideB, pd += strideD)
    {
        ReadBytes(pa, lineA.data(), width * 4);
        ReadBytes(pb, lineB.data(), width * 4);
        for (size_t k = 0; k < line.size(); k++)
            line[k] = uint8_t(std::min(255u, (((lineA[k] * fa) >> 8) & 0xFF) + (((lineB[k] * fb) >> 8) & 0xFF)));
        WriteBytes(pd, line);
    }
}

int ScnVm::LayerEntry()
{
    // A scratch sprite entry for the layer opcodes
    return m_layerEntry != 0 ? m_layerEntry : m_layerEntry = Allocate(0x20);
}

} // namespace openshiina::scripting
