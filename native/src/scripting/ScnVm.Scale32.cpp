// C# Scripting/ScnVm.Scale32.cs: Oreimo START 78380, written out: scales a rectangle of a 32-bit
// picture into another with positions in 1/16 pixels by area averaging (the MMX path of the
// scripts' zoom and pan). Every destination pixel is the weighted sum of the source pixels its
// rectangle covers.
//
// The x86 code first makes a table per destination row and per destination column (24-byte
// entries in the scripts' work buffers): flags (0x80 a partly covered first source pixel, 0x40 a
// partly covered last one; columns may add 0x20, which only picks a copy of the same loop), the
// number of whole source pixels in between, and the weights of the two partial ones. A weight is
// x * K / S with K = 0x5ADC for rows and 0x5ADD for columns, S the source size: the division is a
// multiplication by a rounded reciprocal of the low 32 bits of x * K (ScaleDivider). Then for each
// destination pixel, with wy and wx the weights of a source row and column:
//   w = pmaddwd([wy, wy], [wx, wx]) >>> 21 (signed 16-bit words, 0-2047)
//   channel = (sum of byte * w, in 16 bits) >> 8; byte 3 = FF when all source pixels have FF.
// One quirk is kept: the loop is picked once per destination row (by its row entry and first
// column); rows other than 0xC0 with up to one whole source row then take the general loop
// (0x79160), which for the partly covered source rows takes the first column's pixel from a
// register (the previous pixel's last column, mm2 / mm4) and leaves it out of the FF test - for
// every destination pixel of the row but the first.
// The tables are made here with the same integer steps; the sums are the same modulo 65536 in
// any order, so the pixels come out byte for byte.
//
// Can Fes! START 73A40 is the same code but for three "add dl,dl" the first-column copy of the
// general loop (0x20) lacks: its test of the partly covered last source pixel looks at 0x80,
// which a 0x20 entry always has. So every destination row taking that loop adds the first
// column's last source pixel even when the entry has no 0x40, with the weight its table entry does
// not hold then: whatever the work buffer had there (a pair of dwords, one for channels 0 and 2,
// one for 1 and 3). This build writes its tables into the work buffers as the x86 code does and
// reads that pair after (lastAlways).
#include "scripting/ScnVm.h"
#include "scripting/ParallelRows.h"

#include <algorithm>
#include <optional>

#include "scripting/ScaleDivider.h"

namespace openshiina::scripting {

namespace {

/// The table of one axis (0x78380-0x78748 for rows, 0x78748-0x78AA9 for columns) into table;
/// false when the x86 code would not end (no destination pixel, or a destination pixel smaller
/// than a source pixel: the routine only scales down).
bool ScaleTable(uint32_t s, uint32_t d, uint32_t sourceStart, uint32_t destinationStart, uint32_t destinationEnd, uint32_t k, bool columns,
                std::vector<ScaleEntry>& table)
{
    table.clear();
    ScaleDivider divider(s, k);
    uint32_t esi = (sourceStart & 15) * d >> 4;
    uint32_t lead = (0 - destinationStart) & 15;
    uint32_t n = ((d - lead) >> 4) + (lead != 0 ? 1u : 0);
    if (n == 0 || n > 0x10000)
        return false;
    table.reserve(size_t(n) + 1);
    bool first = lead != 0;
    for (uint32_t i = 0; i < n; i++)
    {
        uint32_t count = 0, flags = 0, eax = 0, ebx = 0, wFirst = 0, wLast = 0;
        if (first)
        {
            first = false;
            ebx = lead * s >> 4;
            eax = (16 - (sourceStart & 15)) * d >> 4;
            if (ebx < eax)
                goto Last;
            if (ebx == eax)
            {
                if (esi == 0)
                    goto SubtractOnce;
                if (columns)
                    flags |= 0x20;
                ebx = 0;
                goto First;
            }
            if (esi == 0)
                goto SubtractOnce;
            if (ebx >= d)
                goto Generic;
            ebx -= eax;
            eax = d - esi;
            if (columns)
                flags |= 0x20;
            goto First;
        }
        ebx = s;
    Generic:
        if (esi == 0)
            goto Whole;
        ebx = ebx + esi - d;
        eax = d - esi;
    First:
        wFirst = divider.Divide(eax);
        flags |= 0x80;
        esi = 0;
    Whole:
        while (ebx >= d)
        {
            ebx -= d;
            count++;
        }
        goto AfterWhole;
    SubtractOnce:
        do
        {
            ebx -= d;
            count++;
        }
        while (ebx >= d);
    AfterWhole:
        if (ebx == 0)
            goto Store;
    Last:
        wLast = divider.Divide(ebx);
        flags |= 0x40;
        esi += ebx;
    Store:
        // Upscaling makes the unsigned steps above wrap (the x86 code then runs away)
        if (count > (s >> 4) + 2)
            return false;
        table.push_back({int(flags), int(count), wFirst, wLast});
    }
    uint32_t end = destinationEnd & 15;
    if (end != 0)
    {
        uint32_t count = 0, flags = 0, wFirst = 0, wLast = 0;
        uint32_t ebx = end * s >> 4;
        if (esi != 0)
        {
            uint32_t eax = d - esi;
            if (eax >= ebx)
            {
                eax = ebx;
                ebx = 0;
            }
            else
                ebx -= eax;
            wFirst = divider.Divide(eax);
            flags |= 0x80;
        }
        while (ebx >= d)
        {
            ebx -= d;
            count++;
        }
        if (ebx != 0)
        {
            wLast = divider.Divide(ebx);
            flags |= 0x40;
        }
        if (count > (s >> 4) + 2)
            return false;
        table.push_back({int(flags), int(count), wFirst, wLast});
    }
    return true;
}

/// pmaddwd of two [w, w] pairs, >>> 21 (packssdw cannot saturate 0-2047).
uint32_t ScaleWeight(uint32_t a, uint32_t b)
{
    int d = int16_t(a) * int16_t(b) + int16_t(a >> 16) * int16_t(b >> 16);
    return uint32_t(d) >> 21;
}

/// One kind of source row of a destination row (the partial first row, the sum of the whole rows,
/// the partial last row) as each destination column's parts of it: its first source pixel, the sum
/// of its whole ones and its last one, channels 0 and 2 in one 64-bit word, 1 and 3 in the other
/// (32-bit lanes; the whole sum kept modulo 65536 a lane), with counts of the pixels that are not
/// opaque. The columns' pixels follow each other (a column's last pixel is the next one's first),
/// so a row is read once.
class ScaleRow
{
public:
    static constexpr uint64_t Lanes = 0x0000FFFF0000FFFFull;

    struct Part
    {
        uint64_t FirstLo = 0, FirstHi = 0, WholeLo = 0, WholeHi = 0, LastLo = 0, LastHi = 0;
        int FirstClear = 0, WholeClear = 0, LastClear = 0;
    };

    void Resize(size_t columns, int width)
    {
        m_parts.resize(columns);
        m_width = width;
    }

    /// A source row of bytes.
    void Set(const uint8_t* row, const std::vector<int>& shape)
    {
        Fill(shape, [row](int x, uint64_t& lo, uint64_t& hi) {
            const uint8_t* p = row + 4 * x;
            lo = p[0] | uint64_t(p[2]) << 32;
            hi = p[1] | uint64_t(p[3]) << 32;
            return p[3] == 0xFF ? 0 : 1;
        });
    }

    /// The whole source rows: their 16-bit sums and the AND of their bytes.
    void Set(const std::vector<uint16_t>& sum, const std::vector<uint8_t>& and_, const std::vector<int>& shape)
    {
        Fill(shape, [&sum, &and_](int x, uint64_t& lo, uint64_t& hi) {
            size_t i = size_t(x) * 4;
            lo = sum[i] | uint64_t(sum[i + 2]) << 32;
            hi = sum[i + 1] | uint64_t(sum[i + 3]) << 32;
            return and_[i + 3] == 0xFF ? 0 : 1;
        });
    }

    /// Every destination column's parts of this row, each times its weight with the row weight
    /// wy, into accLo / accHi (mod 65536 a lane); counts the pixels that are not opaque. With
    /// skipShared the shared first column of every column but the first is left out of the count
    /// (the general loop's register copy). With firstLast the first column also gets its last
    /// source pixel with those weights (Can Fes!'s build, without 0x40).
    void AddTo(uint32_t wy, uint32_t fullCol, const std::vector<uint32_t>& colWx, const std::vector<int>& shape, std::vector<uint64_t>& accLo,
               std::vector<uint64_t>& accHi, std::vector<int>& clear, bool skipShared, const std::optional<std::pair<uint32_t, uint32_t>>& firstLast) const
    {
        uint32_t wWhole = ScaleWeight(fullCol, wy);
        for (size_t c = 0, n = accLo.size(); c < n; c++)
        {
            const Part& p = m_parts[c];
            int a = shape[4 * c + 1], b = shape[4 * c + 2], flags = shape[4 * c + 3];
            uint64_t l = accLo[c], h = accHi[c];
            int nonOpaque = 0;
            if ((flags & 0x80) != 0)
            {
                uint32_t w = ScaleWeight(colWx[2 * c], wy);
                l += p.FirstLo * w;
                h += p.FirstHi * w;
                if (!(skipShared && c > 0))
                    nonOpaque += p.FirstClear;
            }
            if (b > a)
            {
                l += (p.WholeLo & Lanes) * wWhole;
                h += (p.WholeHi & Lanes) * wWhole;
                nonOpaque += p.WholeClear;
            }
            if ((flags & 0x40) != 0)
            {
                uint32_t w = ScaleWeight(colWx[2 * c + 1], wy);
                l += p.LastLo * w;
                h += p.LastHi * w;
                nonOpaque += p.LastClear;
            }
            if (c == 0 && firstLast)
            {
                l += p.LastLo * ScaleWeight(firstLast->first, wy);
                h += p.LastHi * ScaleWeight(firstLast->second, wy);
                nonOpaque += p.LastClear;
            }
            accLo[c] = l & Lanes;
            accHi[c] = h & Lanes;
            clear[c] += nonOpaque;
        }
    }

private:
    /// pixel(x, lo, hi) gives source pixel x's lanes and whether it is not opaque.
    template <typename Pixel>
    void Fill(const std::vector<int>& shape, Pixel pixel)
    {
        for (size_t c = 0, n = m_parts.size(); c < n; c++)
        {
            int f = shape[4 * c], a = shape[4 * c + 1], b = shape[4 * c + 2];
            Part& p = m_parts[c];
            p.FirstClear = f < m_width ? pixel(f, p.FirstLo, p.FirstHi) : 0;
            uint64_t lo = 0, hi = 0;
            int nonOpaque = 0;
            for (int x = a; x < b; x++)
            {
                uint64_t pl, ph;
                nonOpaque += pixel(x, pl, ph);
                lo += pl;
                hi += ph;
            }
            p.WholeLo = lo;
            p.WholeHi = hi;
            p.WholeClear = nonOpaque;
            if (b < m_width)
                p.LastClear = pixel(b, p.LastLo, p.LastHi);
            else
            {
                p.LastLo = p.LastHi = 0;
                p.LastClear = 0;
            }
        }
    }

    std::vector<Part> m_parts;
    int m_width = 0;
};
/// Adds a row of bytes to 16-bit sums (wrapping) and ANDs it into a mask.
void AddRow(const uint8_t* row, size_t length, std::vector<uint16_t>& sum, std::vector<uint8_t>& and_)
{
    for (size_t i = 0; i < length; i++)
    {
        sum[i] = uint16_t(sum[i] + row[i]);
        and_[i] &= row[i];
    }
}

/// The tables, kept from call to call (a zoomed scene scales the same rectangles every frame).
struct Scale32Work
{
    std::vector<ScaleEntry> Rows, Columns;
    std::vector<int> ColShape, RowY;
    std::vector<uint32_t> ColWx;
};

/// The buffers of one thread's rows.
struct Scale32Rows
{
    std::vector<uint16_t> Sum;
    std::vector<uint8_t> And;
    ScaleRow First, Whole, Last;
    std::vector<uint64_t> AccLo, AccHi;
    std::vector<int> Clear;
};

thread_local Scale32Work t_scale32;
thread_local Scale32Rows t_scale32Rows;

/// A table as the x86 code leaves it in a work buffer: 24 bytes an entry, the flags and the count,
/// then each weight twice when its flag is set (the x86 code leaves the others as they were).
void WriteScaleTable(ScnVm& vm, int at, const std::vector<ScaleEntry>& table)
{
    for (const auto& e : table)
    {
        vm.Write32(at, e.Flags);
        vm.Write32(at + 4, e.Count);
        if ((e.Flags & 0x80) != 0)
        {
            vm.Write32(at + 8, int(e.First));
            vm.Write32(at + 12, int(e.First));
        }
        if ((e.Flags & 0x40) != 0)
        {
            vm.Write32(at + 16, int(e.Last));
            vm.Write32(at + 20, int(e.Last));
        }
        at += 24;
    }
}

} // namespace

int ScnVm::ScaleEntryCount(int start, int end)
{
    uint32_t d = uint32_t(end - start), lead = (0 - uint32_t(start)) & 15;
    return int(std::min<uint32_t>(((d - lead) >> 4) + (lead != 0 ? 1u : 0) + ((uint32_t(end) & 15) != 0 ? 1u : 0), 0x10002));
}

bool ScnVm::Scale32(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb, int columnTable,
                    int rowTable, bool lastAlways)
{
    uint32_t dw = uint32_t(dr - dl), dh = uint32_t(db - dt), sw = uint32_t(sr - sl), sh = uint32_t(sb - st);
    if (dw == 0 || dh == 0 || sw == 0 || sh == 0)
        return true;
    auto& work = t_scale32;
    auto& rows = work.Rows;
    auto& cols = work.Columns;
    if (!ScaleTable(sh, dh, uint32_t(st), uint32_t(dt), uint32_t(db), 0x5ADC, false, rows)
        || !ScaleTable(sw, dw, uint32_t(sl), uint32_t(dl), uint32_t(dr), 0x5ADD, true, cols))
        return false;
    if (columnTable != 0)
        WriteScaleTable(*this, columnTable, cols);
    if (rowTable != 0)
        WriteScaleTable(*this, rowTable, rows);
    // Can Fes!'s build: the first column's last source pixel with the work buffer's weights
    std::optional<std::pair<uint32_t, uint32_t>> firstLast;
    if (lastAlways && !cols.empty() && (cols[0].Flags & 0x60) == 0x20)
        firstLast = std::pair(uint32_t(Read32(columnTable + 16)), uint32_t(Read32(columnTable + 20)));
    ScaleDivider divRows(sh, 0x5ADC), divCols(sw, 0x5ADD);
    uint32_t fullRow = divRows.Divide(dh), fullCol = divCols.Divide(dw);

    // The source pixels of each destination column: from colFrom (the partial first one when
    // 0x80), whole ones in [colA, colB), the partial last one at colB when 0x40
    size_t nc = cols.size();
    work.ColShape.resize(nc * 4);
    work.ColWx.resize(nc * 2);
    int x = 0, width = 0;
    for (size_t c = 0; c < nc; c++)
    {
        const auto& e = cols[c];
        int colA = x + ((e.Flags & 0x80) != 0 ? 1 : 0), colB = colA + e.Count;
        work.ColShape[4 * c] = x;
        work.ColShape[4 * c + 1] = colA;
        work.ColShape[4 * c + 2] = colB;
        work.ColShape[4 * c + 3] = e.Flags & 0xC0;
        work.ColWx[2 * c] = e.First;
        work.ColWx[2 * c + 1] = e.Last;
        width = std::max(width, colB + ((e.Flags & 0x40) != 0 ? 1 : 0));
        x += e.Count + ((e.Flags & 0x80) != 0 ? 1 : 0);
    }
    if (firstLast)
        width = std::max(width, work.ColShape[2] + 1);

    int srcX = src + int(uint32_t(st) >> 4) * srcPitch + int(uint32_t(sl) >> 4) * 4;
    int dstRow = dst + int(uint32_t(dt) >> 4) * dstPitch + int(uint32_t(dl) >> 4) * 4;
    int bytes = width * 4;
    // The first source row of every destination row
    size_t nr = rows.size();
    work.RowY.resize(nr + 1);
    work.RowY[0] = 0;
    for (size_t r = 0; r < nr; r++)
        work.RowY[r + 1] = work.RowY[r] + rows[r].Count + ((rows[r].Flags & 0x80) != 0 ? 1 : 0);

    if (UseGpu("scale32", int64_t(nr) * int64_t(nc)))
    {
        std::vector<std::array<int, 4>> rowEntries, columnEntries;
        std::vector<int> rowFrom(work.RowY.begin(), work.RowY.begin() + ptrdiff_t(nr)), columnFrom(nc);
        for (const auto& e : rows)
            rowEntries.push_back({e.Flags, e.Count, int(e.First), int(e.Last)});
        for (size_t c = 0; c < nc; c++)
        {
            columnEntries.push_back({cols[c].Flags, cols[c].Count, int(cols[c].First), int(cols[c].Last)});
            columnFrom[c] = work.ColShape[4 * c];
        }
        if (!ScaleOnGpu(0, rowEntries, rowFrom, columnEntries, columnFrom, width, srcX, srcPitch, dstRow, dstPitch, fullRow, fullCol, firstLast))
            GpuFailed("scale32");
        else if (!GpuCheck)
            return true;
    }
    // Rows are independent: they run on several cores (the result does not depend on it)
    ParallelRows::For(int(nr), int64_t(nr) * int64_t(nc), [&](int from, int to) {
        auto& b = t_scale32Rows;
        b.Sum.resize(size_t(bytes));
        b.And.resize(size_t(bytes));
        b.First.Resize(nc, width);
        b.Whole.Resize(nc, width);
        b.Last.Resize(nc, width);
        b.AccLo.resize(nc);
        b.AccHi.resize(nc);
        b.Clear.resize(nc);
        for (size_t r = size_t(from); r < size_t(to); r++)
        {
            const auto& e = rows[r];
            bool hasFirst = (e.Flags & 0x80) != 0, hasLast = (e.Flags & 0x40) != 0;
            int p = work.RowY[r];
            // The whole source rows of a destination row: sums per channel (16 bits, as the MMX sums
            // wrap) and the AND of their bytes (byte 3 tells whether every one is opaque); the source
            // rows are read in place, then the destination row written
            if (hasFirst)
                b.First.Set(Bytes(srcX + p++ * srcPitch, bytes), work.ColShape);
            if (e.Count > 0)
            {
                std::fill(b.Sum.begin(), b.Sum.end(), uint16_t(0));
                std::fill(b.And.begin(), b.And.end(), uint8_t(0xFF));
                for (int k = 0; k < e.Count; k++)
                    AddRow(Bytes(srcX + p++ * srcPitch, bytes), size_t(bytes), b.Sum, b.And);
                b.Whole.Set(b.Sum, b.And, work.ColShape);
            }
            if (hasLast)
                b.Last.Set(Bytes(srcX + p * srcPitch, bytes), work.ColShape);

            // the general loop's register copy of the shared first column (see the top)
            bool ownLoop = e.Flags == 0xC0 && e.Count <= 1;
            auto extra = ownLoop ? std::nullopt : firstLast;
            std::fill(b.AccLo.begin(), b.AccLo.end(), 0);
            std::fill(b.AccHi.begin(), b.AccHi.end(), 0);
            std::fill(b.Clear.begin(), b.Clear.end(), 0);
            if (hasFirst)
                b.First.AddTo(e.First, fullCol, work.ColWx, work.ColShape, b.AccLo, b.AccHi, b.Clear, !ownLoop, extra);
            if (e.Count > 0)
                b.Whole.AddTo(fullRow, fullCol, work.ColWx, work.ColShape, b.AccLo, b.AccHi, b.Clear, false, extra);
            if (hasLast)
                b.Last.AddTo(e.Last, fullCol, work.ColWx, work.ColShape, b.AccLo, b.AccHi, b.Clear, !ownLoop, extra);
            uint8_t* output = Bytes(dstRow + int(r) * dstPitch, int(nc) * 4);
            for (size_t c = 0; c < nc; c++)
            {
                uint64_t lo = b.AccLo[c], hi = b.AccHi[c];
                output[c * 4] = uint8_t(lo >> 8);
                output[c * 4 + 1] = uint8_t(hi >> 8);
                output[c * 4 + 2] = uint8_t(lo >> 40);
                output[c * 4 + 3] = b.Clear[c] == 0 ? uint8_t(0xFF) : uint8_t(hi >> 40);
            }
        }
    });
    GpuDone("scale32");
    return true;
}

} // namespace openshiina::scripting
