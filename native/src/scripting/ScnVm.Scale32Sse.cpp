// C# Scripting/ScnVm.Scale32Sse.cs: Bitch Nee-chan START C7C70 (Bitch Gakuen CA1E0, the same
// signature), written out: the SSE2 build of scale32 (ScnVm.Scale32.cpp), which a START that will
// not run without SSE2 takes for its zooms and for the picture behind its menus.
//
// The same plan as the MMX build: a table per destination row and per destination column (48-byte
// entries in the work buffers: flags, the count of whole source pixels, the weights of the partly
// covered first (0x80) and last (0x40) ones), then every destination pixel the sum of its source
// pixels times their weights. Here the weights and the sums are single floats:
//   - an axis's step is r = 2 * r0 - x * r0 * r0 with r0 = rcpps(x), x the source size (the
//     interpreter's rcpps is exact 1 / x); a partial weight is (float)v * r, the whole one
//     (float)d * r, d the destination size;
//   - a pixel's channels as floats, times (column weight * row weight), added up in the order of
//     the x86 code (rows in order, in a row the columns in order), then cvtps2dq (to nearest even),
//     packssdw, packuswb. Float sums depend on their order, so the order is kept (and each product
//     rounds before its sum: the build does not fuse them).
// What the x86 code does that a plain reading would not:
//   - rows 0xC0 with no whole row or one (two or three source rows) have their own loops, which go
//     across the rows column by column; in the three-row loop the partial last column weighs the
//     first source row with the middle row's weight and the middle row with the first's;
//   - the first destination column of an axis that starts and ends inside one source pixel (a
//     weight equal to the rest) sets 0x20 in the loop counter instead of the flags, so the column
//     table runs on for 32 more entries (when its bit 5 was clear) and the entry after the last
//     whole destination column is one of those instead of the partial one made for it;
//   - a partial first column after the first is not read from memory in the first and last source
//     rows (and in the two- and three-row loops) but taken from a register holding the previous
//     column's partial last one: the same pixel when the previous column has one (it always does in
//     the tables seen), else whatever the register held: those run as x86 code.
#include "scripting/ScnVm.h"
#include "scripting/ParallelRows.h"
#include "scripting/X86Ops.h"

#include <algorithm>
#include <climits>
#include <cmath>
#include <cstring>
#include <format>
#include <string>

#if defined(__aarch64__)
#include <arm_neon.h>
#elif defined(_M_X64) || defined(__x86_64__)
#include <emmintrin.h>
#endif

namespace openshiina::scripting {

namespace {

struct SseScaleEntry
{
    int Flags;
    uint32_t Count;
    float First, Last;
};

/// Four float lanes (a pixel's channels): the SSE2 build's own instructions on a PC, NEON's that do
/// the same on the Switch (single floats, each product rounded before its sum, as mulps then
/// addps), else one lane after the other.
#if defined(__aarch64__)
struct F4
{
    float32x4_t V = vdupq_n_f32(0);

    F4& operator+=(const F4& o)
    {
        V = vaddq_f32(V, o.V);
        return *this;
    }
};

F4 operator*(const F4& a, float w) { return {vmulq_n_f32(a.V, w)}; }
#elif defined(_M_X64) || defined(__x86_64__)
struct F4
{
    __m128 V = _mm_setzero_ps();

    F4& operator+=(const F4& o)
    {
        V = _mm_add_ps(V, o.V);
        return *this;
    }
};

F4 operator*(const F4& a, float w) { return {_mm_mul_ps(a.V, _mm_set1_ps(w))}; }
#else
struct F4
{
    float V[4] = {};

    F4& operator+=(const F4& o)
    {
        for (int k = 0; k < 4; k++)
            V[k] += o.V[k];
        return *this;
    }
};

F4 operator*(const F4& a, float w)
{
    F4 r;
    for (int k = 0; k < 4; k++)
        r.V[k] = a.V[k] * w;
    return r;
}
#endif

/// The step of an axis: rcpps(x) (exact here) refined once by Newton's method, as the x86 code does it.
float SseScaleStep(uint32_t size)
{
    float x = float(int(size));
    float r0 = 1 / x;
    float t = x * r0 * r0;
    return r0 + r0 - t;
}

/// The table of one axis (C7C70-C7ECE for rows, C7ECE-C80BE for columns) into table and how many
/// of its entries the copy uses; false when the x86 code would not end or would make counts of billions.
bool SseScaleTable(uint32_t s, uint32_t d, uint32_t sourceStart, uint32_t destinationStart, uint32_t destinationEnd, bool columns, float step,
                   std::vector<SseScaleEntry>& table, int& used)
{
    table.clear();
    used = 0;
    constexpr uint32_t MaxCount = 1 << 20;
    uint32_t esi = (sourceStart & 15) * d >> 4;
    uint32_t lead = (0 - destinationStart) & 15;
    uint32_t n = ((d - lead) >> 4) + (lead != 0 ? 1u : 0);
    if (n == 0 || n > 0x10000)
        return false;
    used = int(n);
    uint32_t loops = n;
    uint32_t ebx = 0, ecx = 0, eax = 0;
    int dl = 0;
    float first = 0, last = 0;
    auto weight = [step](uint32_t v) { return float(int(v)) * step; };

    if (lead == 0)
        goto General;
    ebx = lead * s >> 4;
    eax = (16 - (sourceStart & 15)) * d >> 4;
    if (ebx < eax)
        goto LastPart;
    if (ebx == eax)
    {
        if (esi == 0)
            goto WholeLoop;
        if (columns)
            loops |= 0x20;
        ebx = 0;
        goto FirstPart;
    }
    if (esi == 0)
        goto WholeLoop;
    if (ebx >= d)
        goto Rest;
    ebx -= eax;
    eax = d - esi;
    if (columns)
        dl |= 0x20;
    goto FirstPart;

General:
    ecx = 0;
    dl = 0;
    ebx = s;
    if (esi == 0)
        goto WholeTest;
Rest:
    eax = d;
    ebx += esi;
    ebx -= eax;
    eax -= esi;
FirstPart:
    first = weight(eax);
    dl |= 0x80;
    esi = 0;
WholeTest:
    if (ebx < d)
        goto LastTest;
WholeLoop:
    do
    {
        ebx -= d;
        if (++ecx > MaxCount)
            return false;
    }
    while (ebx >= d);
LastTest:
    if (ebx == 0)
        goto Store;
LastPart:
    last = weight(ebx);
    dl |= 0x40;
    esi += ebx;
Store:
    table.push_back({dl, ecx, first, last});
    if (table.size() > 0x10100)
        return false;
    if (--loops != 0)
        goto General;

    // A partial last destination pixel: an entry after the others
    eax = destinationEnd & 15;
    if (eax == 0)
        return true;
    used++;
    ebx = eax * s >> 4;
    ecx = 0;
    dl = 0;
    if (esi != 0)
    {
        eax = d - esi;
        if (eax >= ebx)
        {
            first = weight(ebx);
            ebx = 0;
        }
        else
        {
            first = weight(eax);
            ebx -= eax;
        }
        dl |= 0x80;
    }
    while (ebx >= d)
    {
        ebx -= d;
        if (++ecx > MaxCount)
            return false;
    }
    if (ebx != 0)
    {
        last = weight(ebx);
        dl |= 0x40;
    }
    table.push_back({dl, ecx, first, last});
    return true;
}

/// A pixel's four bytes as floats (movd, punpcklbw, punpcklwd, cvtdq2ps).
F4 Pixel(const uint8_t* line, int p)
{
    uint32_t bytes;
    std::memcpy(&bytes, line + p * 4, 4);
#if defined(__aarch64__)
    return {vcvtq_f32_u32(vmovl_u16(vget_low_u16(vmovl_u8(vreinterpret_u8_u32(vdup_n_u32(bytes))))))};
#elif defined(_M_X64) || defined(__x86_64__)
    __m128i zero = _mm_setzero_si128();
    return {_mm_cvtepi32_ps(_mm_unpacklo_epi16(_mm_unpacklo_epi8(_mm_cvtsi32_si128(int(bytes)), zero), zero))};
#else
    F4 r;
    for (int k = 0; k < 4; k++)
        r.V[k] = float(uint8_t(bytes >> (8 * k)));
    return r;
#endif
}

#if !defined(_M_X64) && !defined(__x86_64__)
/// f to the nearest integer, ties to even, as cvtps2dq: under 2^22 by adding and taking away
/// 1.5 * 2^23 (the float's last bit is then the units: the sum rounds as cvtps2dq does, in the
/// default rounding, which nothing here changes); nearbyint, slow (it heeds the rounding mode),
/// only for the rest
int RoundEven(float f)
{
    constexpr float Magic = 12582912.0f;
    if (f > -4194304.0f && f < 4194304.0f)
        return int((f + Magic) - Magic);
    return f >= -2147483648.0f && f < 2147483648.0f ? int(std::nearbyint(f)) : INT32_MIN;
}

/// A lane into its byte: cvtps2dq (to nearest even), packssdw, packuswb.
uint8_t StoreLane(float f) { return uint8_t(std::clamp(std::clamp(RoundEven(f), -32768, 32767), 0, 255)); }
#endif

void StorePixel(uint8_t* output, int c, const F4& v)
{
#if defined(__aarch64__)
    // fcvtns rounds to nearest even as cvtps2dq, but saturates where cvtps2dq gives 0x80000000:
    // a lane of 2^31 or more (none in the copies seen) goes the scalar way
    if (vmaxvq_f32(v.V) < 2147483648.0f)
    {
        int16x4_t words = vqmovn_s32(vcvtnq_s32_f32(v.V));
        uint32_t bytes = vget_lane_u32(vreinterpret_u32_u8(vqmovun_s16(vcombine_s16(words, words))), 0);
        std::memcpy(output + c * 4, &bytes, 4);
        return;
    }
    float lanes[4];
    vst1q_f32(lanes, v.V);
    for (int k = 0; k < 4; k++)
        output[c * 4 + k] = StoreLane(lanes[k]);
#elif defined(_M_X64) || defined(__x86_64__)
    __m128i words = _mm_packs_epi32(_mm_cvtps_epi32(v.V), _mm_setzero_si128());
    uint32_t bytes = uint32_t(_mm_cvtsi128_si32(_mm_packus_epi16(words, words)));
    std::memcpy(output + c * 4, &bytes, 4);
#else
    for (int k = 0; k < 4; k++)
        output[c * 4 + k] = StoreLane(v.V[k]);
#endif
}

/// A source row's pixels as floats, each once (the columns share them: a partly covered pixel is
/// the last of one column and the first of the next), four at a time where the vectors are.
void ConvertRow(const uint8_t* line, int count, F4* out)
{
    int p = 0;
#if defined(__aarch64__)
    for (; p + 4 <= count; p += 4)
    {
        uint8x16_t b = vld1q_u8(line + p * 4);
        uint16x8_t low = vmovl_u8(vget_low_u8(b)), high = vmovl_u8(vget_high_u8(b));
        out[p].V = vcvtq_f32_u32(vmovl_u16(vget_low_u16(low)));
        out[p + 1].V = vcvtq_f32_u32(vmovl_u16(vget_high_u16(low)));
        out[p + 2].V = vcvtq_f32_u32(vmovl_u16(vget_low_u16(high)));
        out[p + 3].V = vcvtq_f32_u32(vmovl_u16(vget_high_u16(high)));
    }
#elif defined(_M_X64) || defined(__x86_64__)
    __m128i zero = _mm_setzero_si128();
    for (; p + 4 <= count; p += 4)
    {
        __m128i b = _mm_loadu_si128(reinterpret_cast<const __m128i*>(line + p * 4));
        __m128i low = _mm_unpacklo_epi8(b, zero), high = _mm_unpackhi_epi8(b, zero);
        out[p].V = _mm_cvtepi32_ps(_mm_unpacklo_epi16(low, zero));
        out[p + 1].V = _mm_cvtepi32_ps(_mm_unpackhi_epi16(low, zero));
        out[p + 2].V = _mm_cvtepi32_ps(_mm_unpacklo_epi16(high, zero));
        out[p + 3].V = _mm_cvtepi32_ps(_mm_unpackhi_epi16(high, zero));
    }
#endif
    for (; p < count; p++)
        out[p] = Pixel(line, p);
}

/// One source row's part of a destination pixel: its first, whole and last columns.
F4 AddRow(F4 acc, const F4* line, int p, const SseScaleEntry& col, float wy, float wholeWeight)
{
    if ((col.Flags & 0x80) != 0)
        acc += line[p++] * (col.First * wy);
    for (uint32_t k = 0; k < col.Count; k++)
        acc += line[p++] * wholeWeight;
    if ((col.Flags & 0x40) != 0)
        acc += line[p] * (col.Last * wy);
    return acc;
}

struct SseScaleWork
{
    std::vector<SseScaleEntry> Rows, Columns;
    std::vector<int> ColX, RowY;
};

// Each core's source rows as floats
thread_local std::vector<F4> t_sseLines;

thread_local SseScaleWork t_sseScale;

} // namespace

bool ScnVm::Scale32Sse(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
{
    uint32_t dw = uint32_t(dr - dl), dh = uint32_t(db - dt), sw = uint32_t(sr - sl), sh = uint32_t(sb - st);
    if (dw == 0 || dh == 0 || sw == 0 || sh == 0)
        return true;
    if (int(dw) < 0 || int(dh) < 0 || int(sw) < 0 || int(sh) < 0)
        return false;
    float stepY = SseScaleStep(sh), stepX = SseScaleStep(sw);
    float wholeY = float(int(dh)) * stepY, wholeX = float(int(dw)) * stepX;
    float wholeXY = wholeX * wholeY;
    auto& work = t_sseScale;
    auto& rows = work.Rows;
    auto& cols = work.Columns;
    int nr, nc;
    if (!SseScaleTable(sh, dh, uint32_t(st), uint32_t(dt), uint32_t(db), false, stepY, rows, nr)
        || !SseScaleTable(sw, dw, uint32_t(sl), uint32_t(dl), uint32_t(dr), true, stepX, cols, nc))
        return false;

    // Each destination column's first source pixel; a partial first column after the first is
    // read from memory only when the previous column's partial last one is that pixel
    auto& colX = work.ColX;
    colX.resize(size_t(nc) + 1);
    int x = 0;
    for (int c = 0; c < nc; c++)
    {
        if (c > 0 && (cols[size_t(c)].Flags & 0x80) != 0 && (cols[size_t(c) - 1].Flags & 0x40) == 0)
            return false;
        colX[size_t(c)] = x;
        x += int(cols[size_t(c)].Count) + ((cols[size_t(c)].Flags & 0x80) != 0 ? 1 : 0);
    }
    int width = x + ((cols[size_t(nc) - 1].Flags & 0x40) != 0 ? 1 : 0);
    auto& rowY = work.RowY;
    rowY.resize(size_t(nr) + 1);
    int y = 0;
    for (int r = 0; r < nr; r++)
    {
        rowY[size_t(r)] = y;
        y += int(rows[size_t(r)].Count) + ((rows[size_t(r)].Flags & 0x80) != 0 ? 1 : 0);
    }
    int height = y + ((rows[size_t(nr) - 1].Flags & 0x40) != 0 ? 1 : 0);

    int srcX = src + int(uint32_t(st) >> 4) * srcPitch + int(uint32_t(sl) >> 4) * 4;
    int dstRow = dst + int(uint32_t(dt) >> 4) * dstPitch + int(uint32_t(dl) >> 4) * 4;
    // The pixels are written as they are made: a destination that shares bytes with the source
    // would change pixels still to be read
    int64_t srcEnd = srcX + int64_t(height - 1) * srcPitch + width * int64_t(4), dstEnd = dstRow + int64_t(nr - 1) * dstPitch + nc * int64_t(4);
    if (srcPitch <= 0 || dstPitch <= 0 || (srcX < dstEnd && dstRow < srcEnd))
        return false;

    // Rows are independent: they run on several cores (the result does not depend on it)
    ParallelRows::For(nr, int64_t(nr) * nc * 4, [&](int from, int to) {
        for (int r = from; r < to; r++)
        {
            const auto& row = rows[size_t(r)];
            uint8_t* output = Bytes(dstRow + r * dstPitch, nc * 4);
            int top = srcX + rowY[size_t(r)] * srcPitch;
            // the destination row's source rows as floats
            int lines = ((row.Flags & 0x80) != 0 ? 1 : 0) + int(row.Count) + ((row.Flags & 0x40) != 0 ? 1 : 0);
            auto& converted = t_sseLines;
            if (converted.size() < size_t(lines) * size_t(width))
                converted.resize(size_t(lines) * size_t(width));
            for (int k = 0; k < lines; k++)
                ConvertRow(Bytes(top + k * srcPitch, width * 4), width, converted.data() + size_t(k) * size_t(width));
            if (row.Flags == 0xC0 && row.Count <= 1)
            {
                const F4* r0 = converted.data();
                const F4* r1 = r0 + width;
                if (row.Count == 0)
                {
                    // Rows 0xC0 with no whole row: across the two rows, column by column
                    float w0 = row.First, w1 = row.Last;
                    float whole0 = wholeX * w0, whole1 = wholeX * w1;
                    for (int c = 0; c < nc; c++)
                    {
                        const auto& col = cols[size_t(c)];
                        F4 acc;
                        int p = colX[size_t(c)];
                        if ((col.Flags & 0x80) != 0)
                        {
                            acc += r0[p] * (w0 * col.First);
                            acc += r1[p] * (col.First * w1);
                            p++;
                        }
                        for (uint32_t k = 0; k < col.Count; k++, p++)
                        {
                            acc += r0[p] * whole0;
                            acc += r1[p] * whole1;
                        }
                        if ((col.Flags & 0x40) != 0)
                        {
                            acc += r0[p] * (w0 * col.Last);
                            acc += r1[p] * (col.Last * w1);
                        }
                        StorePixel(output, c, acc);
                    }
                }
                else
                {
                    // Rows 0xC0 with one whole row: across the three rows, column by column (the partial
                    // last column with the first two rows' weights swapped, as the x86 code has them)
                    const F4* r2 = r1 + width;
                    float w0 = row.First, w2 = row.Last;
                    float whole0 = wholeX * w0, whole1 = wholeXY, whole2 = wholeX * w2;
                    for (int c = 0; c < nc; c++)
                    {
                        const auto& col = cols[size_t(c)];
                        F4 acc;
                        int p = colX[size_t(c)];
                        if ((col.Flags & 0x80) != 0)
                        {
                            acc += r0[p] * (w0 * col.First);
                            acc += r1[p] * (wholeY * col.First);
                            acc += r2[p] * (col.First * w2);
                            p++;
                        }
                        for (uint32_t k = 0; k < col.Count; k++, p++)
                        {
                            acc += r0[p] * whole0;
                            acc += r1[p] * whole1;
                            acc += r2[p] * whole2;
                        }
                        if ((col.Flags & 0x40) != 0)
                        {
                            acc += r0[p] * (wholeY * col.Last);
                            acc += r1[p] * (w0 * col.Last);
                            acc += r2[p] * (col.Last * w2);
                        }
                        StorePixel(output, c, acc);
                    }
                }
                continue;
            }
            // The general loops: the source rows one after the other, in each the pixel's columns
            if (lines == 0)
            {
                for (int c = 0; c < nc; c++)
                    StorePixel(output, c, F4{});
                continue;
            }
            const F4* block = converted.data();
            float wholeFirst = wholeX * row.First, wholeLast = wholeX * row.Last;
            for (int c = 0; c < nc; c++)
            {
                const auto& col = cols[size_t(c)];
                F4 acc;
                size_t at = 0;
                if ((row.Flags & 0x80) != 0)
                {
                    acc = AddRow(acc, block + at, colX[size_t(c)], col, row.First, wholeFirst);
                    at += size_t(width);
                }
                for (uint32_t k = 0; k < row.Count; k++, at += size_t(width))
                    acc = AddRow(acc, block + at, colX[size_t(c)], col, wholeY, wholeXY);
                if ((row.Flags & 0x40) != 0)
                    acc = AddRow(acc, block + at, colX[size_t(c)], col, row.Last, wholeLast);
                StorePixel(output, c, acc);
            }
        }
    });
    return true;
}

std::string ScnVm::CheckVectorLanes()
{
    // one lane as the x86 code makes it: cvtps2dq (0x80000000 out of range), packssdw, packuswb
    auto lane = [](float f) {
        int i = f >= -2147483648.0f && f < 2147483648.0f ? int(std::nearbyint(f)) : INT32_MIN;
        return uint8_t(std::clamp(std::clamp(i, -32768, 32767), 0, 255));
    };
    uint64_t seed = 0x9E3779B97F4A7C15ull;
    auto next = [&] {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        return seed;
    };
    // a float of the kinds a sum can be: ordinary, a tie (k + 0.5), small, huge or negative
    auto value = [&] {
        uint64_t r = next();
        float f = float(r >> 40) / float(1 << 16);
        switch (r & 7)
        {
        case 0: return float(int(r >> 8 & 511) - 128) + 0.5f;
        case 1: return f * 1e-3f;
        case 2: return f * 1e7f + 2147483520.0f;
        case 3: return -f * 1e5f;
        case 4: return float(int64_t(r >> 33)) * 4.0f;
        default: return f;
        }
    };
    std::string report;
    int floats = 0, bytes = 0, words = 0;
    for (int n = 0; n < 200000; n++)
    {
        float in[4] = {value(), value(), value(), value()};
        F4 v;
        for (int k = 0; k < 4; k++)
        {
            // each lane put in as a sum of products of ones: exact, the lanes apart
            uint8_t one[16] = {};
            one[k * 4 + k] = 1;
            v += Pixel(one, k) * in[k];
        }
        uint8_t out[4];
        StorePixel(out, 0, v);
        for (int k = 0; k < 4; k++)
            if (out[k] != lane(in[k]) && floats++ < 3)
                report += std::format(" store {} -> {} (x86 {});", in[k], out[k], lane(in[k]));
        uint32_t pixel = uint32_t(next());
        F4 p = Pixel(reinterpret_cast<const uint8_t*>(&pixel), 0) * 1.0f;
        uint8_t back[4];
        StorePixel(back, 0, p);
        if (std::memcmp(back, &pixel, 4) != 0 && bytes++ < 3)
            report += std::format(" pixel {:08X};", pixel);
        uint64_t a = next(), b = n % 3 == 0 ? 0xFFFF'8000'7FFF'0001ull : next();
        auto lanes = [&](auto f) {
            uint64_t r = 0;
            for (int k = 0; k < 4; k++)
                r |= uint64_t(uint16_t(f(uint32_t(X86Ops::W(a, k)), uint32_t(X86Ops::W(b, k))))) << (16 * k);
            return r;
        };
        if ((X86Ops::Pmullw(a, b) != lanes([](uint32_t x, uint32_t y) { return x * y; })
             || X86Ops::Paddw(a, b) != lanes([](uint32_t x, uint32_t y) { return x + y; })
             || X86Ops::Psubw(a, b) != lanes([](uint32_t x, uint32_t y) { return x - y; }))
            && words++ < 3)
            report += std::format(" words {:016X} {:016X};", a, b);
    }
    if (floats + bytes + words != 0)
        report = std::format("{} stores, {} pixels, {} word operations differed:", floats, bytes, words) + report;
    return report;
}

} // namespace openshiina::scripting
