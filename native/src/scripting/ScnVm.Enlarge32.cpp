// C# Scripting/ScnVm.Enlarge32.cs: Re: Rem Plus START 866A8 (Maki Fes! 85B38, Oreimo 77108), written
// out: the enlarging case of the scripts' zoom and pan (scale32 is its scaling-down case, subpixel32
// its copy without scaling). Positions are in 1/16 pixels; a destination pixel mixes up to 2 x 2
// source pixels.
//
// The x86 code first makes a table in the scripts' work buffer (l[12]) of the destination columns
// that mix two source columns: across a row an accumulator starts at (16 - sl & 15) dW >> 4 and
// loses sW a destination pixel; while it covers sW the source pixel is used as it is, else the
// pixel mixes the current source pixel and the next, by (acc * 257) / sW and ((sW - acc) * 257) /
// sW (the reciprocal division of scale32), and the accumulator gains dW - sW. A partly covered
// first and last column have entries of their own, their alpha lane scaled by the part covered.
// Rows go the same way down with dH and sH: a partly covered top and bottom row, whole rows from
// one source row, or two mixed. Every formula is the MMX one in 16-bit lanes (pmullw, paddw, >> 8,
// packuswb), quirks kept: after a partly covered first column that used up its source pixel the
// rows from one source row take that pixel again (the next is read only after the pointer moved).
// A pixel is a uint64_t of four 16-bit lanes (X86Ops); the values packed never pass 255, so
// packuswb is a plain narrowing.
#include "scripting/ScnVm.h"

#include <cstring>

#include "scripting/ScaleDivider.h"
#include "scripting/X86Ops.h"

namespace openshiina::scripting {

namespace {

using namespace X86Ops;

/// An entry of the column table: the weights of the current and the next source pixel, and the accumulator after it.
struct Enlarge32Column
{
    uint64_t W0 = 0, W1 = 0;
    int Count = 0;
};

enum class Enlarge32Row { Single, Two, WholeSingle, WholeTwo };

/// A row of the destination: its kind, its first source row (and the next for two), its weights.
struct Enlarge32RowWork
{
    Enlarge32Row Kind;
    int Source;
    uint64_t Weight, Upper, Lower;
    int Target;
};

constexpr uint64_t Lanes0x101 = 0x0000'0101'0101'0101;

uint64_t Broadcast(uint32_t v)
{
    uint64_t w = v & 0xFFFF;
    return w | w << 16 | w << 32 | w << 48;
}

/// punpcklbw with 0: a pixel's four bytes as 16-bit lanes.
uint64_t Unpack(uint32_t pixel) { return Punpcklbw(pixel, 0); }

/// packuswb of lanes that are at most 255: the low bytes (a narrowing, as the C# player's).
uint32_t Pack(uint64_t v)
{
    return uint32_t(v & 0xFF) | uint32_t(v >> 16 & 0xFF) << 8 | uint32_t(v >> 32 & 0xFF) << 16 | uint32_t(v >> 48 & 0xFF) << 24;
}

/// The weights of a partly covered column or row that mixes two pixels: lanes 0-2 part * 257 /
/// whole, lane 3 that times covered (sixteenths) / whole >> 4, as the x86 code divides (64-bit
/// dividends, 32-bit quotients).
uint64_t PartWeight(uint32_t part, uint32_t whole, uint32_t covered)
{
    uint64_t product = uint64_t(part) * 257;
    uint32_t q = uint32_t(product / whole);
    uint32_t alpha = uint32_t(uint64_t(uint32_t(product)) * covered / whole) >> 4;
    return (q & 0xFFFFull) * 0x0000'0001'0001'0001ull | uint64_t(alpha & 0xFFFF) << 48;
}

struct Enlarge32Table
{
    std::optional<Enlarge32Column> Left;
    bool LeftTwo;
    std::vector<Enlarge32Column> Middle;
    std::optional<Enlarge32Column> Right;
    bool RightTwo;
    int Start, DW, SW, WholeColumns;
};

/// A source pixel as the row sees it: one row unpacked, or the two rows mixed (>> 8).
uint64_t Enlarge32Pixel(const uint32_t* upper, const uint32_t* lower, int x, bool two, uint64_t wUpper, uint64_t wLower)
{
    return two ? Psrlw(Paddw(Pmullw(Unpack(upper[x]), wUpper), Pmullw(Unpack(lower[x]), wLower)), 8) : Unpack(upper[x]);
}

/// A mix of weighted pixels (>> 8), then the row's weight for the partly covered rows.
uint32_t Enlarge32Out(uint64_t sum, bool weighted, uint64_t rowWeight)
{
    uint64_t v = Psrlw(sum, 8);
    if (weighted)
        v = Psrlw(Pmullw(v, rowWeight), 8);
    return Pack(v);
}

/// A source pixel used whole (and the row's weight for the partly covered rows).
uint32_t Enlarge32Full(uint64_t p, bool weighted, uint64_t rowWeight) { return Pack(weighted ? Psrlw(Pmullw(p, rowWeight), 8) : p); }

/// A destination row of Enlarge32: the walk across, as each kind of row of the x86 code goes.
void Enlarge32Walk(const Enlarge32Table& t, const Enlarge32RowWork& row, const uint32_t* upper, const uint32_t* lower, uint32_t* output)
{
    bool two = row.Kind == Enlarge32Row::Two || row.Kind == Enlarge32Row::WholeTwo;
    bool weighted = row.Kind == Enlarge32Row::Single;       // the partly covered rows from one source row
    uint64_t rowWeight = row.Weight, wUpper = row.Upper, wLower = row.Lower;

    int x = 0, o = 0, ebp = t.Start, m = 0;
    uint64_t cur = Enlarge32Pixel(upper, lower, 0, two, wUpper, wLower);
    if (t.Left)
    {
        const auto& left = *t.Left;
        if (!t.LeftTwo)
        {
            output[o++] = Enlarge32Out(Pmullw(left.W0, cur), weighted, rowWeight);
            ebp -= left.Count;
            if (ebp == 0)
            {
                // the rows from one source row take the same pixel again, the mixed rows the next
                if (two)
                    cur = Enlarge32Pixel(upper, lower, ++x, two, wUpper, wLower);
                else
                    cur = Enlarge32Pixel(upper, lower, x++, two, wUpper, wLower);
                ebp = t.DW;
            }
        }
        else
        {
            uint64_t next = Enlarge32Pixel(upper, lower, x + 1, two, wUpper, wLower);
            output[o++] = Enlarge32Out(Paddw(Pmullw(left.W0, cur), Pmullw(left.W1, next)), weighted, rowWeight);
            cur = next;
            x++;
            ebp = left.Count;
        }
    }
    bool reload = false;
    int sw = t.SW, dwSw = t.DW - t.SW;
    for (int c = t.WholeColumns;;)
    {
        if (ebp >= sw)
        {
            output[o++] = Enlarge32Full(cur, weighted, rowWeight);
            ebp -= sw;
            if (ebp == 0)
            {
                ebp = t.DW;
                x++;
                if (--c == 0)
                {
                    reload = true;
                    break;
                }
                cur = Enlarge32Pixel(upper, lower, x, two, wUpper, wLower);
                continue;
            }
            if (--c == 0)
                break;
            continue;
        }
        const auto& e = t.Middle[size_t(m++)];
        uint64_t next2 = Enlarge32Pixel(upper, lower, x + 1, two, wUpper, wLower);
        output[o++] = Enlarge32Out(Paddw(Pmullw(e.W0, cur), Pmullw(e.W1, next2)), weighted, rowWeight);
        cur = next2;
        x++;
        ebp += dwSw;
        if (--c == 0)
            break;
    }
    if (t.Right)
    {
        const auto& right = *t.Right;
        if (reload)
            cur = Enlarge32Pixel(upper, lower, x, two, wUpper, wLower);
        if (!t.RightTwo)
            output[o] = Enlarge32Out(Pmullw(cur, right.W0), weighted, rowWeight);
        else
            output[o] = Enlarge32Out(Paddw(Pmullw(cur, right.W0), Pmullw(Enlarge32Pixel(upper, lower, x + 1, two, wUpper, wLower), right.W1)),
                                     weighted, rowWeight);
    }
}

} // namespace

bool ScnVm::Enlarge32(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
{
    int dW = dr - dl, dH = db - dt, sW = sr - sl, sH = sb - st;
    if (dW == 0 || dH == 0 || sW == 0 || sH == 0)
        return true;
    if (dW < 0 || dH < 0 || sW < 0 || sH < 0 || dl < 0 || dt < 0 || sl < 0 || st < 0)
        return false;
    int wholeColumns = (dr >> 4) - ((dl + 15) >> 4), wholeRows = (db >> 4) - ((dt + 15) >> 4);
    if (wholeColumns <= 0 || wholeRows <= 0 || wholeColumns > 0x10000 || wholeRows > 0x10000)
        return false;
    uint32_t left = uint32_t(16 - (dl & 15)), right = uint32_t(dr & 15), top = uint32_t(16 - (dt & 15)), bottom = uint32_t(db & 15);
    int start = int(uint32_t((16 - (sl & 15)) * dW) >> 4);
    bool hasLeft = (left & 15) != 0, hasRight = (right & 15) != 0;

    // The column table, made as the x86 code makes it
    ScaleDivider columns(uint32_t(sW), 257);
    Enlarge32Table table{};
    table.Start = start;
    table.DW = dW;
    table.SW = sW;
    table.WholeColumns = wholeColumns;
    int ebp = start;
    if (hasLeft)
    {
        uint32_t covered = uint32_t(left * uint32_t(sW)) >> 4;
        if (uint32_t(ebp) >= covered)
        {
            table.Left = Enlarge32Column{Lanes0x101 | uint64_t(((257 * left) >> 4) & 0xFFFF) << 48, 0, int(covered)};
            ebp -= int(covered);
            if (ebp == 0)
                ebp = dW;
        }
        else
        {
            table.LeftTwo = true;
            if (covered == 0)
                return false;
            uint64_t w1 = PartWeight(covered - uint32_t(ebp), covered, left);
            uint64_t w0 = PartWeight(uint32_t(ebp), covered, left);
            ebp = ebp + dW - int(covered);
            table.Left = Enlarge32Column{w0, w1, ebp};
        }
    }
    for (int c = 0; c < wholeColumns; c++)
    {
        if (ebp >= sW)
        {
            ebp -= sW;
            if (ebp == 0)
                ebp = dW;
            continue;
        }
        table.Middle.push_back({Broadcast(columns.Divide(uint32_t(ebp))), Broadcast(columns.Divide(uint32_t(sW - ebp))), 0});
        ebp += dW - sW;
    }
    if (hasRight)
    {
        uint32_t covered = uint32_t(right * uint32_t(sW)) >> 4;
        if (uint32_t(ebp) >= covered)
            table.Right = Enlarge32Column{Lanes0x101 | uint64_t(((257 * right) >> 4) & 0xFFFF) << 48, 0, 0};
        else
        {
            table.RightTwo = true;
            if (covered == 0)
                return false;
            table.Right = Enlarge32Column{PartWeight(uint32_t(ebp), covered, right), PartWeight(covered - uint32_t(ebp), covered, right), 0};
        }
    }

    // The rows, made as the x86 code goes down the picture
    ScaleDivider rowsDivider(uint32_t(sH), 257);
    std::vector<Enlarge32RowWork> rows;
    int srcRow = src + (st >> 4) * srcPitch + (sl >> 4) * 4;
    int dstRow = dst + (dt >> 4) * dstPitch + (dl >> 4) * 4;
    int acc = int(uint32_t((16 - (st & 15)) * dH) >> 4);
    auto partRow = [&](uint32_t part, bool last) {
        uint32_t covered = uint32_t(part * uint32_t(sH)) >> 4;
        if (uint32_t(acc) >= covered)
        {
            rows.push_back({Enlarge32Row::Single, srcRow, Lanes0x101 | uint64_t(((257 * part) >> 4) & 0xFFFF) << 48, 0, 0, dstRow});
            if (!last)
            {
                acc -= int(covered);
                if (acc == 0)
                {
                    acc = dH;
                    srcRow += srcPitch;
                }
            }
        }
        else
        {
            if (covered == 0)
                return false;
            rows.push_back({Enlarge32Row::Two, srcRow, 0, PartWeight(uint32_t(acc), covered, part), PartWeight(covered - uint32_t(acc), covered, part),
                            dstRow});
            if (!last)
            {
                srcRow += srcPitch;
                acc = acc + dH - int(covered);
            }
        }
        dstRow += dstPitch;
        return true;
    };
    if ((top & 15) != 0 && !partRow(top, false))
        return false;
    for (int r = 0; r < wholeRows; r++)
    {
        if (acc >= sH)
        {
            acc -= sH;
            rows.push_back({Enlarge32Row::WholeSingle, srcRow, 0, 0, 0, dstRow});
            if (acc == 0)
            {
                srcRow += srcPitch;
                acc = dH;
            }
        }
        else
        {
            rows.push_back({Enlarge32Row::WholeTwo, srcRow, 0, Broadcast(rowsDivider.Divide(uint32_t(acc))),
                            Broadcast(rowsDivider.Divide(uint32_t(sH - acc))), dstRow});
            srcRow += srcPitch;
            acc += dH - sH;
        }
        dstRow += dstPitch;
    }
    if ((bottom & 15) != 0 && !partRow(bottom, true))
        return false;

    // The source pixels a row reads (and one more, as the walk reads the next of a pair), and the
    // destination pixels it writes. The pixels are worked on as dwords: the rows are copied in and
    // out (unaligned in script memory)
    int sourcePixels = (sW >> 4) + 8;
    int outPixels = (hasLeft ? 1 : 0) + wholeColumns + (hasRight ? 1 : 0);
    std::vector<uint32_t> upper(static_cast<size_t>(sourcePixels)), lower(static_cast<size_t>(sourcePixels)), output(static_cast<size_t>(outPixels));
    for (const auto& row : rows)
    {
        ReadBytes(row.Source, reinterpret_cast<uint8_t*>(upper.data()), sourcePixels * 4);
        if (row.Kind == Enlarge32Row::Two || row.Kind == Enlarge32Row::WholeTwo)
            ReadBytes(row.Source + srcPitch, reinterpret_cast<uint8_t*>(lower.data()), sourcePixels * 4);
        Enlarge32Walk(table, row, upper.data(), lower.data(), output.data());
        WriteBytes(row.Target, reinterpret_cast<const uint8_t*>(output.data()), outPixels * 4);
    }
    return true;
}

} // namespace openshiina::scripting
