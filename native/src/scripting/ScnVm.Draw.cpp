// C# Scripting/ScnVm.Draw.cs: picture kernels of the executable, done the way it does them on a
// CPU with MMX (the path the game takes on any current PC), so the pictures match it pixel for
// pixel. The C# player spreads the rows over the cores; here they go one after the other for now
// (the same bytes: no row reads what another writes).
#include "scripting/ScnVm.h"
#include "scripting/ParallelRows.h"

#include <algorithm>

namespace openshiina::scripting {

void ScnVm::StepTable(uint8_t* table, int weight, int total)
{
    if (weight == 0)
    {
        std::fill(table, table + 256, uint8_t(0));
        return;
    }
    uint32_t counter = uint32_t(total) >> 1, value = 0;
    for (int i = 0;;)
    {
        table[i] = uint8_t(value);
        if (++i == 256)
            return;
        if (counter >= uint32_t(weight))
            counter -= uint32_t(weight);
        else
        {
            counter = counter - uint32_t(weight) + uint32_t(total);
            value++;
        }
    }
}

namespace {

// One row of BlendRows into output (its destination at dst): the tables for the bytes up to an
// 8-byte aligned address and the last 1-3, the MMX blocks between (16-bit lanes). level: a grey
// level (tableA has it, m and k the blocks), no rowB. The rows may be the output itself: each byte
// is read before it is written
void BlendRow(const uint8_t* rowA, const uint8_t* rowB, uint8_t* output, int n, int dst, const uint8_t* tableA, const uint8_t* tableB,
              int m, int k, bool level)
{
    int head = -dst & 7;
    // The x86 code's order: 1-3 bytes from the tables to a 4-byte boundary, a 4-byte block to an
    // 8-byte one, 8-byte blocks, a 4-byte block, the last 1-3 bytes from the tables
    int start = std::min(head & 3, n), rest = n - start;
    int afterFour = rest - (rest >= 4 && (head & 4) != 0 ? 4 : 0);
    int end = n - afterFour % 4;
    if (level)
    {
        for (int i = 0; i < start; i++)
            output[i] = tableA[rowA[i]];
        uint16_t vm = uint16_t(m), vk = uint16_t(k);
        for (int i = start; i < end; i++)
        {
            uint16_t v = uint16_t(uint16_t(uint16_t(rowA[i] * vm) >> 8) + vk);
            output[i] = uint8_t(std::min<uint16_t>(v, 255));
        }
        for (int i = end; i < n; i++)
            output[i] = tableA[rowA[i]];
        return;
    }
    uint16_t wa = uint16_t(m), wb = uint16_t(256 - m);
    for (int i = 0; i < start; i++)
        output[i] = uint8_t(tableA[rowA[i]] + tableB[rowB[i]]);
    // (A * m + B * (256 - m)) >> 8 in 16-bit lanes (the products and their sum wrap)
    for (int i = start; i < end; i++)
        output[i] = uint8_t(uint16_t(uint16_t(rowA[i] * wa) + uint16_t(rowB[i] * wb)) >> 8);
    for (int i = end; i < n; i++)
        output[i] = uint8_t(tableA[rowA[i]] + tableB[rowB[i]]);
}

} // namespace

void ScnVm::BlendRows(int dst, int source, int source2, int a, int b, int width, int height, int pitch)
{
    // FUN_004396F0 with MMX: dst = A * a / (a + b) + B * b / (a + b) byte by byte, for width pixels
    // (3 bytes each) on height rows that all have pitch. source2 below 0x100 is a grey level
    int sum = a + b;
    if (sum == 0)
        return;
    bool level = uint32_t(source2) < 0x100;
    uint8_t tableA[256] = {}, tableB[256] = {};
    int m, k = 0;
    if (level)
    {
        for (int v = 0; v < 256; v++)
            tableA[v] = uint8_t((uint64_t(uint32_t(source2)) * uint32_t(b) + uint64_t(uint32_t(a)) * uint32_t(v)) / uint32_t(sum));
        m = int(uint64_t(uint32_t(a)) * 256 / uint32_t(sum));
        k = int(uint64_t(uint32_t(b)) * uint32_t(source2) / uint32_t(sum));
    }
    else
    {
        StepTable(tableA, a, sum);
        StepTable(tableB, b, sum);
        m = int(uint64_t(uint32_t(a)) * 256 / uint32_t(sum));
    }
    int rowBytes = width * 3;
    // A source row that starts inside the destination row (not on it) is copied first: the row is
    // read whole before it is written, as the x86 code reads ahead
    auto shifted = [&](int from) { return from != dst && std::abs(int64_t(from) - dst) < rowBytes; };
    bool copyA = shifted(source), copyB = !level && shifted(source2);
    auto rows = [&](int from, int to) {
        openshiina::Bytes bufferA(copyA ? size_t(rowBytes) : 0), bufferB(copyB ? size_t(rowBytes) : 0);
        for (int y = from; y < to; y++)
        {
            int at = dst + y * pitch;
            const uint8_t* rowA;
            if (copyA)
            {
                ReadBytes(source + y * pitch, bufferA.data(), rowBytes);
                rowA = bufferA.data();
            }
            else
                rowA = Bytes(source + y * pitch, rowBytes);
            const uint8_t* rowB = nullptr;
            if (!level)
            {
                if (copyB)
                {
                    ReadBytes(source2 + y * pitch, bufferB.data(), rowBytes);
                    rowB = bufferB.data();
                }
                else
                    rowB = Bytes(source2 + y * pitch, rowBytes);
            }
            BlendRow(rowA, rowB, Bytes(at, rowBytes), rowBytes, at, tableA, tableB, m, k, level);
        }
    };
    // The rows go on several cores where no row reads what another writes: a source that is the
    // destination itself or apart from it (a source a few rows off the destination is read after
    // the rows above were written, so those are drawn in turn)
    auto region = [&](int start) { return start + int64_t(height - 1) * pitch + rowBytes; };
    auto apart = [&](int from) { return from == dst || region(from) <= dst || region(dst) <= from; };
    if (pitch >= rowBytes && apart(source) && (level || apart(source2)))
        ParallelRows::For(height, int64_t(width) * height, rows);
    else
        rows(0, height);
}
void ScnVm::ShowAtLevel(int source, int level)
{
    // Surface 0 = surface source at a brightness 0-255 (0 black, 255 a copy)
    int screen = SurfaceField(0, 2), from = SurfaceField(source, 2);
    int width = SurfaceField(0, 7), height = SurfaceField(0, 8), pitch = SurfaceField(0, 10);
    int size = pitch * height;
    if (level == 0)
        FillMemory(screen, size, 0);
    else if (level == 255)
        CopyMemory(screen, from, size);
    else
        BlendRows(screen, from, 0, level, 255 - level, width, height, pitch);
}

void ScnVm::RuleBlend(int dst, int a, int b, int rule, int pixels, int t)
{
    // FUN_00437B40 with MMX: a rule (mask picture) transition, byte by byte over pixels * 3 / 4
    // dwords. r = the rule's byte (inverted with bit 31 of t). Soft: k = clamp(r + t - 255, 0, 255),
    // out = A k >> 8 without B, else min(65535, A k + B (256 - k)) >> 8. Hard (bit 30): r > 256 - t
    // picks A, else B (or 0)
    int bytes = int(uint32_t(pixels * 3) >> 2) * 4;
    if (bytes <= 0)
        return;
    bool invert = t < 0, hard = (t & 0x40000000) != 0;
    int value = t & 0x3FFFFFFF;
    int16_t threshold = int16_t(0x100 - value);
    auto part = [&](int count, const uint8_t* ra, const uint8_t* sa, const uint8_t* sb, uint8_t* out) {
        for (int i = 0; i < count; i++)
        {
            int r = invert ? ra[i] ^ 0xFF : ra[i];
            if (hard)
            {
                bool take = r > threshold;
                out[i] = take ? sa[i] : b != 0 ? sb[i] : uint8_t(0);
                continue;
            }
            int sum = r + value;
            int k = std::min(255, std::max(0, (sum & 0xFFFF) - 255));
            if ((uint32_t(sum) >> 16) != 0)
                k = 255;
            out[i] = b == 0 ? uint8_t(std::min(255, sa[i] * k >> 8)) : uint8_t(std::min(255, std::min(0xFFFF, sa[i] * k + sb[i] * (256 - k)) >> 8));
        }
    };
    // Byte i of the output takes byte i of each input only: in parts of 64 KB on all cores, where
    // every input is the output itself or apart from it (else all is read first, as the engine
    // reads it)
    auto apart = [&](int from) { return from == dst || int64_t(from) + bytes <= dst || int64_t(dst) + bytes <= from; };
    if (apart(rule) && apart(a) && (b == 0 || apart(b)) && IsContiguous(dst, bytes) && IsContiguous(rule, bytes) && IsContiguous(a, bytes)
        && (b == 0 || IsContiguous(b, bytes)))
    {
        // in place (a byte is read before it is written)
        constexpr int Chunk = 1 << 16;
        ParallelRows::For((bytes + Chunk - 1) / Chunk, bytes / 4, [&](int first, int last) {
            int from = first * Chunk, count = std::min(last * Chunk, bytes) - from;
            part(count, Bytes(rule + from, count), Bytes(a + from, count), b != 0 ? Bytes(b + from, count) : nullptr, Bytes(dst + from, count));
        });
        return;
    }
    auto ra = ReadBytes(rule, bytes), sa = ReadBytes(a, bytes);
    openshiina::Bytes sb = b != 0 ? ReadBytes(b, bytes) : openshiina::Bytes();
    openshiina::Bytes out(static_cast<size_t>(bytes));
    part(bytes, ra.data(), sa.data(), b != 0 ? sb.data() : nullptr, out.data());
    WriteBytes(dst, out);
}
void ScnVm::RegisterDraw()
{
    // 0500 level: surface 0 (the one on screen) = surface 1 at a brightness 0-255 (FUN_00411F10);
    // 256 or the same level again changes nothing
    Register(0x0500, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int level = vm.Value(c, i.Args[0]);
        vm.EngineGlobals[0x4880D8] = level;
        if (level == 0x100 || level == vm.m_screenLevel)
            return 0;
        vm.m_screenLevel = level;
        vm.ShowAtLevel(1, level);
        return 0;
    });
    // 04C6 n, show: surface 0 = surface n at the brightness of the last 0500 (FUN_004120F0, always
    // redone); show: InvalidateRect
    Register(0x04C6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        if (n != 0)
        {
            int level = vm.EngineGlobalOr(0x4880D8, 0x100);
            vm.m_screenLevel = -1;
            if (level != 0x100 && level != -1)
            {
                vm.m_screenLevel = level;
                vm.ShowAtLevel(n, level);
            }
        }
        if (vm.Value(c, i.Args[1]) != 0)
        {
            vm.InvalidateWindow();
            vm.FrameShown = true;
        }
        return 0;
    });
    // 04F6 dst, x, y, A, ax, ay, B, bx, by, w, h, a, b (FUN_0041B4F0): dst = A * a / (a + b) + B * b /
    // (a + b) over a rectangle, clipped to dst. B with bit 31 is a grey level (its low byte). All
    // three use dst's pitch
    Register(0x04F6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int v[13];
        for (int k = 0; k < 13; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        int dstSurface = v[0], srcA = v[3], srcB = v[6];
        int l = v[1], t = v[2], r = v[1] + v[9], btm = v[2] + v[10];
        int ax = v[4], ay = v[5], bx = v[7], by = v[8];
        int dw = vm.SurfaceField(dstSurface, 7), dh = vm.SurfaceField(dstSurface, 8);
        // FUN_00417790 with the two source rectangles following the destination
        if (dw < l || dh < t)
            return 0;
        if (l < 0)
        {
            if (r < 1)
                return 0;
            ax -= l;
            bx -= l;
            l = 0;
        }
        if (t < 0)
        {
            if (btm < 1)
                return 0;
            ay -= t;
            by -= t;
            t = 0;
        }
        r = std::min(r, dw);
        btm = std::min(btm, dh);
        int width = r - l, rows = btm - t;
        if (width == 0 || rows == 0)
            return 0;
        int pitch = vm.SurfaceField(dstSurface, 10);
        int bytesA = vm.SurfaceField(srcA, 9) >> 3;
        int dst = vm.SurfaceField(dstSurface, 2) + (vm.SurfaceField(dstSurface, 9) >> 3) * l + pitch * t;
        int a = vm.SurfaceField(srcA, 2) + vm.SurfaceField(srcA, 10) * ay + bytesA * ax;
        int offsetB = bytesA * bx + (srcB < 0 ? 0 : vm.SurfaceField(srcB & 0xFF, 10) * by);
        int b = (srcB < 0 ? srcB & 0xFF : vm.SurfaceField(srcB & 0xFF, 2)) + offsetB;
        vm.BlendRows(dst, a, b, v[11], v[12], width, rows, pitch);
        if (dstSurface == vm.DisplaySurface())
            vm.FrameShown = true;
        return 0;
    });
    // 0568 dst, A, B (-1: none), rule, t: a rule transition between surfaces (FUN_004182E0)
    Register(0x0568, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int dst = vm.Value(c, i.Args[0]), a = vm.Value(c, i.Args[1]), b = vm.Value(c, i.Args[2]);
        int rule = vm.Value(c, i.Args[3]);
        uint32_t t = uint32_t(vm.Value(c, i.Args[4]));
        if ((t & 0x3FFFFFFF) > 0x200)
            t = (t & 0x3FFFF000) + 0x200;
        int pixels = vm.SurfaceField(dst, 8) * vm.SurfaceField(dst, 7);
        vm.RuleBlend(vm.SurfaceField(dst, 2), vm.SurfaceField(a, 2), b == -1 ? 0 : vm.SurfaceField(b, 2), vm.SurfaceField(rule, 2), pixels, int(t));
        if (dst == vm.DisplaySurface())
        {
            vm.InvalidateWindow();
            vm.FrameShown = true;
        }
        return 0;
    });
    // 07D0 l, t, r, b: InvalidateRect - the next WM_PAINT shows that part of the display surface
    Register(0x07D0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int l = vm.Value(c, i.Args[0]), t = vm.Value(c, i.Args[1]), r = vm.Value(c, i.Args[2]), b = vm.Value(c, i.Args[3]);
        vm.InvalidateWindow(l, t, r, b);
        vm.FrameShown = true;
        return 0;
    });
    // 002A ms: Sleep - the scripts wait for time to pass; the host frame ends
    Register(0x002A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.SleepRequested = std::max(vm.SleepRequested, vm.Value(c, i.Args[0]));
        vm.FrameShown = true;
        return 0;
    });
}

} // namespace openshiina::scripting
