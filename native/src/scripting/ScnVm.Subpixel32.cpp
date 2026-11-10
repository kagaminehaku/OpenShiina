// C# Scripting/ScnVm.Subpixel32.cs: Oreimo START 76388, written out: copies a rectangle of a 32-bit
// picture into another with positions in 1/16 pixels and no scaling (the MMX path of the scripts'
// smooth scrolling).
//
// With a / b the distances (in 1/16) from the destination / source position to the next whole
// pixel, the weights are D0 = (b - a) mod 16 and D4 = (a - b) mod 16 across, D8 / DC down, so a
// destination pixel mixes up to 2 x 2 source pixels. The x86 code has a loop per kind of
// destination row - partly covered top row, whole rows, partly covered bottom row, each from one
// source row (when the vertical fractions line up) or two - and per row the partly covered left
// pixel, the whole pixels and the partly covered right pixel; edge pixels scale their alpha lane
// by the covered fraction. Every formula is the MMX one in 16-bit lanes (products and sums wrap,
// >> 4 or >> 8, packuswb), including three quirks of the x86 code: on rows from two source rows
// the edge pixels whose source starts inside a pixel are stored as two unpacked 16-bit lanes (no
// packuswb), and the left one also adds one of its products unshifted.
#include "scripting/ScnVm.h"
#include "scripting/ParallelRows.h"

#include <cstring>

#include "scripting/X86Ops.h"

namespace openshiina::scripting {

namespace {

using namespace X86Ops;

enum class SubpixelRow { TopOne, TopTwo, MiddleOne, MiddleTwo, BottomOne, BottomTwo };

bool TwoRows(SubpixelRow kind)
{
    return kind == SubpixelRow::TopTwo || kind == SubpixelRow::MiddleTwo || kind == SubpixelRow::BottomTwo;
}

struct SubpixelWeights
{
    uint32_t AX, BX, TailX, SrcTailX;
    bool Copy, AverageEdge;
    uint64_t L70, L78, L80, L88, L50, L58, L60, L68, B0, B8, C0, C8;
    uint16_t D0, D4, D8, DC;
};

/// Four 16-bit lanes (16, 16, 16, alpha).
uint64_t Lanes4(uint32_t w, uint32_t alpha) { return uint64_t(w) | uint64_t(w) << 16 | uint64_t(w) << 32 | uint64_t(alpha) << 48; }

uint32_t Dword(const uint8_t* row, int at)
{
    uint32_t v;
    std::memcpy(&v, row + at, 4);
    return v;
}

uint64_t Px(const uint8_t* row, int at) { return Punpcklbw(Dword(row, at), 0); }

uint64_t Mul(uint64_t a, uint64_t b) { return Pmullw(a, b); }

uint64_t Add(uint64_t a, uint64_t b) { return Paddw(a, b); }

void StorePacked(uint8_t* output, int at, uint64_t v)
{
    uint32_t bytes = uint32_t(Packuswb(v, 0));
    std::memcpy(output + at, &bytes, 4);
}

/// movd without packuswb: lanes 0 and 1 as two 16-bit words.
void StoreWords(uint8_t* output, int at, uint64_t v)
{
    uint32_t words = uint32_t(v);
    std::memcpy(output + at, &words, 4);
}

/// (p[i] D0 + p[i+1] D4) >> 4, packed (one source row).
void MiddleAcross(const uint8_t* p, int s, uint8_t* output, int o, int n, uint16_t d0, uint16_t d4)
{
    uint64_t w0 = Lanes4(d0, d0), w1 = Lanes4(d4, d4);
    for (int i = 0; i < n; i++)
        StorePacked(output, o + i * 4, Psrlw(Add(Mul(Px(p, s + i * 4), w0), Mul(Px(p, s + i * 4 + 4), w1)), 4));
}

/// (p[i] D8 + q[i] DC) >> 4, packed (two source rows lined up across).
void MiddleVertical(const uint8_t* p, const uint8_t* q, int s, uint8_t* output, int o, int n, uint16_t d8, uint16_t dc)
{
    uint64_t w0 = Lanes4(d8, d8), w1 = Lanes4(dc, dc);
    for (int i = 0; i < n; i++)
        StorePacked(output, o + i * 4, Psrlw(Add(Mul(Px(p, s + i * 4), w0), Mul(Px(q, s + i * 4), w1)), 4));
}

/// (p[i] D0 D8 + p[i+1] D4 D8 + q[i] D0 DC + q[i+1] D4 DC) >> 8, packed.
void MiddleBilinear(const uint8_t* p, const uint8_t* q, int s, uint8_t* output, int o, int n, const SubpixelWeights& k)
{
    // The weights are products in 16 bits too (pmullw of the broadcast fractions)
    uint16_t a = uint16_t(k.D0 * k.D8), b = uint16_t(k.D4 * k.D8), c = uint16_t(k.D0 * k.DC), d = uint16_t(k.D4 * k.DC);
    uint64_t wa = Lanes4(a, a), wb = Lanes4(b, b), wc = Lanes4(c, c), wd = Lanes4(d, d);
    for (int i = 0; i < n; i++)
    {
        int at = s + i * 4;
        uint64_t x = Add(Add(Mul(Px(p, at), wa), Mul(Px(p, at + 4), wb)), Add(Mul(Px(q, at), wc), Mul(Px(q, at + 4), wd)));
        StorePacked(output, o + i * 4, Psrlw(x, 8));
    }
}

/// One destination row: left edge, whole pixels, right edge.
void SubpixelRowPixels(SubpixelRow kind, const SubpixelWeights& k, const uint8_t* p, const uint8_t* q, uint8_t* output, int middle)
{
    bool two = TwoRows(kind);
    // Vertical weights of the edges of rows from two source rows; lane weight of one-row edges
    uint64_t wyP = kind == SubpixelRow::TopTwo ? k.L58 : k.C0;
    uint64_t wyQ = kind == SubpixelRow::BottomTwo ? k.L68 : k.C8;
    uint64_t v = kind == SubpixelRow::TopOne ? k.L78 : k.L88;
    int s = 0, o = 0;

    // The partly covered left pixel
    if (k.AX != 0)
    {
        if (k.AX <= k.BX)
        {
            uint64_t x = two ? Mul(Add(Mul(Px(p, s), wyP), Mul(Px(q, s), wyQ)), k.L70)
                         : kind == SubpixelRow::MiddleOne ? Mul(Px(p, s), k.L70)
                                                          : Mul(Mul(Px(p, s), k.L70), v);
            StorePacked(output, o, Psrlw(x, two || kind != SubpixelRow::MiddleOne ? 8 : 4));
            if (k.AX == k.BX)
                s += 4;
        }
        else if (two)
        {
            uint64_t m1 = Mul(Mul(Px(q, s), k.L50), wyQ);
            uint64_t x = Add(Add(Mul(Mul(Px(p, s), k.L50), wyP), m1), Add(Mul(Mul(Px(p, s + 4), k.B8), wyP), Mul(Mul(Px(q, s + 4), k.B8), wyQ)));
            StoreWords(output, o, Add(Psrlw(x, 8), m1));
            s += 4;
        }
        else
        {
            uint64_t x = Add(Mul(Px(p, s), k.L50), Mul(Px(p, s + 4), k.B8));
            if (kind == SubpixelRow::MiddleOne)
                StorePacked(output, o, Psrlw(x, 4));
            else
                StorePacked(output, o, Psrlw(Mul(x, v), 8));
            s += 4;
        }
        o += 4;
    }

    // The whole pixels
    if (two)
    {
        if (k.Copy)
            MiddleVertical(p, q, s, output, o, middle, k.D8, k.DC);
        else
            MiddleBilinear(p, q, s, output, o, middle, k);
    }
    else if (k.Copy)
        std::memcpy(output + o, p + s, size_t(middle) * 4);
    else
        MiddleAcross(p, s, output, o, middle, k.D0, k.D4);
    s += middle * 4;
    o += middle * 4;

    // The partly covered right pixel
    if (k.TailX != 0)
    {
        if (k.TailX <= k.SrcTailX)
        {
            uint64_t x = two ? Mul(Add(Mul(Px(p, s), wyP), Mul(Px(q, s), wyQ)), k.L80)
                         : kind == SubpixelRow::MiddleOne ? Mul(Px(p, s), k.L80)
                                                          : Mul(Mul(Px(p, s), k.L80), v);
            StorePacked(output, o, Psrlw(x, two || kind != SubpixelRow::MiddleOne ? 8 : 4));
        }
        else if (k.AverageEdge && kind == SubpixelRow::MiddleTwo)
        {
            for (int j = 0; j < 4; j++)
                output[o + j] = uint8_t((p[s + j] + q[s + j] + p[s + 4 + j] + q[s + 4 + j]) >> 2);
        }
        else if (two)
        {
            uint64_t x = Add(Add(Mul(Mul(Px(p, s), k.B0), wyP), Mul(Mul(Px(q, s), k.B0), wyQ)),
                             Add(Mul(Mul(Px(p, s + 4), k.L60), wyP), Mul(Mul(Px(q, s + 4), k.L60), wyQ)));
            StoreWords(output, o, Psrlw(x, 8));
        }
        else
        {
            uint64_t x = Add(Mul(Px(p, s), k.B0), Mul(Px(p, s + 4), k.L60));
            if (kind == SubpixelRow::MiddleOne)
                StorePacked(output, o, Psrlw(x, 4));
            else
                StorePacked(output, o, Psrlw(Mul(x, v), 8));
        }
    }
}

struct SubpixelRowWork
{
    SubpixelRow Kind;
    int Source, Target;
};

} // namespace

bool ScnVm::Subpixel32(int dst, int dstPitch, int dtx, int dty, int w, int h, int src, int srcPitch, int stx, int sty, bool v250, bool averageEdge,
                       bool bottomTwo)
{
    if (dtx < 0 || dty < 0 || stx < 0 || sty < 0 || w < 0 || h < 0)
        return true;
    uint32_t ax = uint32_t(-dtx) & 15, bx = uint32_t(-stx) & 15, ay = uint32_t(-dty) & 15, by = uint32_t(-sty) & 15;
    uint32_t d0 = (bx - ax) & 15, d4 = (ax - bx) & 15, d8 = (by - ay) & 15, dc = (ay - by) & 15;
    uint32_t middle = uint32_t(w - int(ax)) >> 4, tailX = uint32_t(w - int(ax)) & 15, srcTailX = uint32_t(w - int(bx)) & 15;
    uint32_t rowsMiddle = uint32_t(h - int(ay)) >> 4, tailY = uint32_t(h - int(ay)) & 15, srcTailY = uint32_t(h - int(by)) & 15;
    // The x86 loops count down from these without a check for 0
    if (middle == 0 || rowsMiddle == 0 || middle > 0x10000 || rowsMiddle > 0x10000)
        return false;

    SubpixelWeights k{};
    k.AX = ax;
    k.BX = bx;
    k.TailX = tailX;
    k.SrcTailX = srcTailX;
    k.Copy = d0 == 0;
    k.AverageEdge = averageEdge;
    k.L70 = Lanes4(16, ax);
    k.L78 = Lanes4(16, ay);
    k.L80 = Lanes4(16, tailX);
    k.L88 = Lanes4(16, tailY);
    k.L50 = Lanes4(d0, bx);
    k.L58 = Lanes4(d8, by);
    k.L60 = averageEdge ? Lanes4(d4, d4) : Lanes4(d4, srcTailX);
    k.L68 = Lanes4(dc, srcTailY);
    k.B0 = Lanes4(d0, d0);
    k.B8 = Lanes4(d4, d4);
    k.C0 = Lanes4(d8, d8);
    k.C8 = Lanes4(dc, dc);
    k.D0 = uint16_t(d0);
    k.D4 = uint16_t(d4);
    k.D8 = uint16_t(d8);
    k.DC = uint16_t(dc);

    // Rows: (kind, source row, destination row)
    std::vector<SubpixelRowWork> rows;
    int srcRow = src + int(uint32_t(sty) >> 4) * srcPitch + int(uint32_t(stx) >> 4) * 4;
    int dstRow = dst + int(uint32_t(dty) >> 4) * dstPitch + int(uint32_t(dtx) >> 4) * 4;
    if (ay != 0)
    {
        if (ay > by)
        {
            rows.push_back({SubpixelRow::TopTwo, srcRow, dstRow});
            srcRow += srcPitch;
        }
        else
        {
            rows.push_back({SubpixelRow::TopOne, srcRow, dstRow});
            if (ay == by)
                srcRow += srcPitch;
        }
        dstRow += dstPitch;
    }
    auto middleKind = ay == by ? SubpixelRow::MiddleOne : SubpixelRow::MiddleTwo;
    uint32_t drawn = v250 && middleKind == SubpixelRow::MiddleTwo && rowsMiddle >= 2 ? rowsMiddle - 1 : rowsMiddle;
    for (uint32_t r = 0; r < drawn; r++)
    {
        rows.push_back({middleKind, srcRow, dstRow});
        srcRow += srcPitch;
        dstRow += dstPitch;
    }
    if (tailY != 0 && !v250 && (tailY <= srcTailY || bottomTwo))
        rows.push_back({tailY > srcTailY ? SubpixelRow::BottomTwo : SubpixelRow::BottomOne, srcRow, dstRow});

    int outPixels = (ax != 0 ? 1 : 0) + int(middle) + (tailX != 0 ? 1 : 0);
    int inBytes = (int(middle) + 3) * 4;
    int outBytes = outPixels * 4;
    // (the GPU's kernel is the other builds')
    if (!averageEdge && UseGpu("subpixel32", int64_t(rows.size()) * outPixels))
    {
        std::vector<std::array<int, 3>> gpuRows;
        gpuRows.reserve(rows.size());
        for (const auto& [kind, s, d] : rows)
            gpuRows.push_back({int(kind), s, d});
        SubpixelGpuParameters parameters{0, 0, 0, int(middle), ax, bx, tailX, srcTailX, ay, by, tailY, srcTailY, d0, d4, d8, dc, d0 == 0 ? 1 : 0, 0, 0, 0, 0};
        if (!SubpixelOnGpu(gpuRows, srcPitch, int(middle) + 3, outPixels, parameters))
            GpuFailed("subpixel32");
        else if (!GpuCheck)
            return true;
    }
    ParallelRows::For(int(rows.size()), int64_t(rows.size()) * outPixels, [&](int from, int to) {
        // Source rows are read in place (copied when they share bytes with the destination row,
        // which is written as the row is made, or are not one piece of memory) and the destination
        // row is written in place
        openshiina::Bytes spareP, spareQ;
        for (size_t r = size_t(from); r < size_t(to); r++)
        {
            const auto& [kind, s, d] = rows[r];
            const uint8_t* p = ReadView(s, inBytes, spareP, d, outBytes);
            const uint8_t* q = TwoRows(kind) ? ReadView(s + srcPitch, inBytes, spareQ, d, outBytes) : nullptr;
            SubpixelRowPixels(kind, k, p, q, Bytes(d, outBytes), int(middle));
        }
    });
    GpuDone("subpixel32");
    return true;
}

} // namespace openshiina::scripting
