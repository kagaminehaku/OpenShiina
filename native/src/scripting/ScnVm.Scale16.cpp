// C# Scripting/ScnVm.Scale16.cs: Ero-On! START 5E2D6, written out: the scaling-down case of the
// scripts' zoom and pan in the first ShiinaRio v2.49 build (Oreimo's 78380 does the same job its
// own way, ScnVm.Scale32.cpp). Positions are in 1/16 pixels; every destination pixel is the
// weighted sum of the source pixels its rectangle covers.
//
// For each axis the x86 code first makes a table of n + 1 weights (n the destination size, s the
// source size): v starts at 0 and acc at s / 2; after each entry acc -= 0x100, and when that does
// not leave it above 0, v += 1 and acc += s. v[n] is the weight of a whole source pixel. Then an
// entry per destination row and column (24 bytes in the scripts' work buffers): flags (0x80 a
// partly covered first source pixel, 0x40 a partly covered last one, which is also the next
// entry's first), the number of whole source pixels between, and the weights of the two partial
// ones (v[n - r] and v[r'] for the parts r, r' of the pixel inside), one of which gets 1 more when
// the entry's weights add up to less than 0x100. Per destination pixel, with wy and wx the weights
// of a source row and column:
//   w = saturate16(pmaddwd([wy, wy], [wx, wx]) >>> 8)
//   channel = (sum of byte * w, in 16 bits) >> 8; byte 3 = FF when every source pixel has FF.
// The sums are the same modulo 65536 in any order, so the pixels come out byte for byte.
#include "scripting/ScnVm.h"
#include "scripting/ParallelRows.h"

#include <algorithm>
#include <climits>

#include "scripting/ScaleDivider.h"

namespace openshiina::scripting {

namespace {

struct Scale16Axis
{
    uint32_t Whole;
    std::vector<ScaleEntry> Entries;
};

/// An axis of Scale16: the weights v[0..n] and an entry per destination pixel.
std::optional<Scale16Axis> MakeScale16Axis(uint32_t n, uint32_t s, uint32_t start)
{
    std::vector<uint32_t> v(size_t(n) + 1);
    uint32_t acc = s >> 1, w = 0;
    for (uint32_t i = 0;; i++)
    {
        v[i] = w;
        if (i == n)
            break;
        uint32_t before = acc;
        acc -= 0x100;
        if (before <= 0x100)
        {
            w++;
            acc += s;
        }
    }
    Scale16Axis axis{v[n], {}};
    int count = int(n >> 4);
    axis.Entries.resize(size_t(count));
    uint32_t r = (start & 15) * n >> 4;
    for (int k = 0; k < count; k++)
    {
        int flags = 0, wholes = 0;
        uint32_t rest = s, sum = 0, first = 0, last = 0, head = 0;
        if (r != 0)
        {
            head = n - r;
            rest = s + r - n;
            first = v[head];
            flags |= 0x80;
            sum += first;
            r = 0;
        }
        if (rest >= n)
        {
            uint32_t q = rest / n;
            if (q > uint32_t(INT_MAX))
                return std::nullopt;
            wholes = int(q);
            sum += axis.Whole * q;
            rest -= q * n;
        }
        if (rest != 0)
        {
            last = v[rest];
            flags |= 0x40;
            sum += last;
            r = rest;
        }
        if (sum < 0x100 && flags != 0)
        {
            if (flags == 0x80 || (flags == 0xC0 && head >= rest))
                first++;
            else
                last++;
        }
        axis.Entries[size_t(k)] = {flags, wholes, first, last};
    }
    return axis;
}

/// pmaddwd of two weights as [w, w] dwords, >>> 8, packssdw: the weight of one source pixel.
uint32_t Scale16Weight(uint32_t a, uint32_t b)
{
    int sum = int16_t(a & 0xFFFF) * int16_t(b & 0xFFFF) + int16_t(a >> 16) * int16_t(b >> 16);
    return std::min(uint32_t(sum) >> 8, 0x7FFFu);
}

/// A source row into the sums (or added to them) and the opaque flags.
void Scale16Load(const openshiina::Bytes& line, std::vector<int>& sum, std::vector<uint8_t>& opaque, bool add)
{
    if (!add)
    {
        for (size_t i = 0; i < line.size(); i++)
            sum[i] = line[i];
        for (size_t x = 0, i = 3; x < opaque.size(); x++, i += 4)
            opaque[x] = line[i] == 0xFF;
        return;
    }
    for (size_t i = 0; i < line.size(); i++)
        sum[i] += line[i];
    for (size_t x = 0, i = 3; x < opaque.size(); x++, i += 4)
        opaque[x] &= line[i] == 0xFF;
}

/// One kind of source row, of row weight wy, into every destination column's sums.
void Scale16Columns(const std::vector<int>& sum, const std::vector<uint8_t>& opaque, uint32_t wy, uint32_t wholeCol,
                    const std::vector<ScaleEntry>& cols, const std::vector<int>& colFrom, std::vector<int>& acc, std::vector<uint8_t>& clear)
{
    int wWhole = int(Scale16Weight(wholeCol, wy));
    for (size_t c = 0, o = 0; c < cols.size(); c++, o += 4)
    {
        const auto& e = cols[c];
        int x = colFrom[c];
        int a0 = acc[o], a1 = acc[o + 1], a2 = acc[o + 2], a3 = acc[o + 3];
        bool notOpaque = clear[c] != 0;
        if ((e.Flags & 0x80) != 0)
        {
            int w = int(Scale16Weight(e.First, wy)), i = x * 4;
            a0 += sum[size_t(i)] * w;
            a1 += sum[size_t(i) + 1] * w;
            a2 += sum[size_t(i) + 2] * w;
            a3 += sum[size_t(i) + 3] * w;
            notOpaque |= !opaque[size_t(x++)];
        }
        if (e.Count > 0)
        {
            int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            for (int n = 0, i = x * 4; n < e.Count; n++, i += 4)
            {
                s0 += sum[size_t(i)];
                s1 += sum[size_t(i) + 1];
                s2 += sum[size_t(i) + 2];
                s3 += sum[size_t(i) + 3];
                notOpaque |= !opaque[size_t(x + n)];
            }
            a0 += s0 * wWhole;
            a1 += s1 * wWhole;
            a2 += s2 * wWhole;
            a3 += s3 * wWhole;
            x += e.Count;
        }
        if ((e.Flags & 0x40) != 0)
        {
            int w = int(Scale16Weight(e.Last, wy)), i = x * 4;
            a0 += sum[size_t(i)] * w;
            a1 += sum[size_t(i) + 1] * w;
            a2 += sum[size_t(i) + 2] * w;
            a3 += sum[size_t(i) + 3] * w;
            notOpaque |= !opaque[size_t(x)];
        }
        // The sums count modulo 65536 (the MMX words wrap)
        acc[o] = a0 & 0xFFFF;
        acc[o + 1] = a1 & 0xFFFF;
        acc[o + 2] = a2 & 0xFFFF;
        acc[o + 3] = a3 & 0xFFFF;
        clear[c] = notOpaque;
    }
}

} // namespace

bool ScnVm::Scale16(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
{
    uint32_t dw = uint32_t(dr - dl), dh = uint32_t(db - dt), sw = uint32_t(sr - sl), sh = uint32_t(sb - st);
    if (dw == 0 || dh == 0 || sw == 0 || sh == 0)
        return true;
    if (dw > 0x7FFFFF || dh > 0x7FFFFF || sw > 0x7FFFFF || sh > 0x7FFFFF)
        return false;
    auto rowAxis = MakeScale16Axis(dh, sh, uint32_t(st));
    auto colAxis = MakeScale16Axis(dw, sw, uint32_t(sl));
    if (!rowAxis || !colAxis)
        return false;
    uint32_t wholeRow = rowAxis->Whole, wholeCol = colAxis->Whole;
    const auto& rows = rowAxis->Entries;
    const auto& cols = colAxis->Entries;

    // The source pixels of each destination column: the partial first one at colFrom (0x80), then
    // the whole ones, then the partial last one (0x40)
    size_t nc = cols.size();
    int width = 0;
    std::vector<int> colFrom(nc);
    for (size_t c = 0, x = 0; c < nc; c++)
    {
        const auto& e = cols[c];
        colFrom[c] = int(x);
        size_t a = x + ((e.Flags & 0x80) != 0 ? 1 : 0);
        width = std::max(width, int(a) + e.Count + ((e.Flags & 0x40) != 0 ? 1 : 0));
        x = a + size_t(e.Count);
    }
    // Where each destination row's source rows start (it shares its last partial one with the next row)
    size_t nr = rows.size();
    std::vector<int> rowFrom(nr);
    for (size_t r = 0, y = 0; r < nr; r++)
    {
        rowFrom[r] = int(y);
        y += size_t(rows[r].Count) + ((rows[r].Flags & 0x80) != 0 ? 1 : 0);
    }
    int srcBase = src + int(uint32_t(st) >> 4) * srcPitch + int(uint32_t(sl) >> 4) * 4;
    int dstBase = dst + int(uint32_t(dt) >> 4) * dstPitch + int(uint32_t(dl) >> 4) * 4;
    int bytes = width * 4;

    // Rows are independent: bands of them run on all cores (the result does not depend on it)
    ParallelRows::For(int(nr), int64_t(nr) * int64_t(nc), [&](int from, int to) {
        openshiina::Bytes line(size_t(bytes), 0);
        // A kind of source row of the destination row: per channel the sum of its bytes (the whole rows
        // share a weight, and the sums only count modulo 65536) and whether every pixel is opaque
        std::vector<int> sum(static_cast<size_t>(bytes)), acc(nc * 4);
        std::vector<uint8_t> opaque(static_cast<size_t>(width)), clear(nc);
        openshiina::Bytes output(nc * 4);
        for (size_t r = size_t(from); r < size_t(to); r++)
        {
            const auto& row = rows[r];
            std::fill(acc.begin(), acc.end(), 0);
            std::fill(clear.begin(), clear.end(), uint8_t(0));
            int y = rowFrom[r];
            if ((row.Flags & 0x80) != 0)
            {
                ReadBytes(srcBase + y++ * srcPitch, line.data(), bytes);
                Scale16Load(line, sum, opaque, false);
                Scale16Columns(sum, opaque, row.First, wholeCol, cols, colFrom, acc, clear);
            }
            if (row.Count > 0)
            {
                for (int k = 0; k < row.Count; k++)
                {
                    ReadBytes(srcBase + y++ * srcPitch, line.data(), bytes);
                    Scale16Load(line, sum, opaque, k > 0);
                }
                Scale16Columns(sum, opaque, wholeRow, wholeCol, cols, colFrom, acc, clear);
            }
            if ((row.Flags & 0x40) != 0)
            {
                ReadBytes(srcBase + y * srcPitch, line.data(), bytes);
                Scale16Load(line, sum, opaque, false);
                Scale16Columns(sum, opaque, row.Last, wholeCol, cols, colFrom, acc, clear);
            }
            for (size_t c = 0, o = 0; c < nc; c++, o += 4)
            {
                output[o] = uint8_t(acc[o] >> 8);
                output[o + 1] = uint8_t(acc[o + 1] >> 8);
                output[o + 2] = uint8_t(acc[o + 2] >> 8);
                output[o + 3] = clear[c] ? uint8_t(acc[o + 3] >> 8) : uint8_t(0xFF);
            }
            WriteBytes(dstBase + int(r) * dstPitch, output);
        }
    });
    return true;
}

} // namespace openshiina::scripting
