// C# Scripting/ScnVm.Enlarge16.cs: Ero-On! START 5DFA5, written out: the enlarging case of the
// scripts' zoom and pan in the first ShiinaRio v2.49 build (ScnVm.Scale16.cpp is its scaling-down
// case). Positions are in 1/16 pixels; each destination pixel mixes up to four source pixels.
//
// The x86 code makes a table of sW weights in the scripts' work buffer (l[12]): entry i is
// (i * 257) / sW as four 16-bit words. Across a row, x starts at dW - ((sl & 15) * sW >> 4): while
// x >= sW the source pixel is used as it is and x -= sW (at 0 the next source pixel comes, and x =
// dW); else it is mixed with the next one by the words of entries x and sW - x, and the next source
// pixel comes with x += dW - sW. Down the picture y works the same way with sH and dH: y >= sH takes
// one source row (y -= sH; at 0 the next row and y = dH), else two rows are mixed by (y << 8) / sH
// and ((sH - y) << 8) / sH (then the next row, y += dH - sH). Every product keeps its low 16 bits
// (pmullw), every sum wraps at 16 bits (paddw), and each mix ends with >> 8.
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

namespace {

/// A destination row of Enlarge16: its source row, and whether it mixes it with the next by Upper and Lower.
struct Enlarge16Row
{
    int Source;
    bool Mixed;
    int Upper, Lower;
};

} // namespace

bool ScnVm::Enlarge16(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb, int table,
                      int scale, bool opaque)
{
    int dW = dr - dl, dH = db - dt, sW = sr - sl, sH = sb - st;
    if (dW == 0 || dH == 0 || sW == 0 || sH == 0)
        return true;
    if (sW < 0 || sH < 0 || dW < sW || dH < sH || dW > 0x7FFFFF || dH > 0x7FFFFF)
        return false;

    // The weight table, written as the x86 code writes it; read back with the entry past its end
    // (x = 0 when dW = sW reads it)
    openshiina::Bytes entries(size_t(sW + 1) * 8);
    for (int i = 0; i < sW; i++)
    {
        uint16_t w = uint16_t(i * scale / sW);
        for (int k = 0; k < 4; k++)
        {
            entries[size_t(i) * 8 + size_t(k) * 2] = uint8_t(w);
            entries[size_t(i) * 8 + size_t(k) * 2 + 1] = uint8_t(w >> 8);
        }
    }
    WriteBytes(table, entries.data(), sW * 8);
    ReadBytes(table + sW * 8, entries.data() + size_t(sW) * 8, 8);
    auto weight = [&](int entry, int lane) { return entries[size_t(entry) * 8 + size_t(lane) * 2] | entries[size_t(entry) * 8 + size_t(lane) * 2 + 1] << 8; };

    // The walk across a row is the same for every row: per destination column the source pixel,
    // and whether it is mixed with the next one (by the words of entries x, sW - x)
    int columns = dW >> 4;
    std::vector<int> from(static_cast<size_t>(columns)), mixLeft(static_cast<size_t>(columns)), mixRight(static_cast<size_t>(columns));   // mixLeft -1: the pixel as it is
    int x = dW - int(uint32_t((sl & 15) * sW) >> 4), at = 0, width = 1;
    for (int c = 0; c < columns; c++)
    {
        from[size_t(c)] = at;
        if (x < sW)
        {
            mixLeft[size_t(c)] = x;
            mixRight[size_t(c)] = sW - x;
            width = std::max(width, at + 2);
            at++;
            x += dW - sW;
        }
        else
        {
            mixLeft[size_t(c)] = -1;
            width = std::max(width, at + 1);
            x -= sW;
            if (x == 0)
            {
                at++;
                x = dW;
            }
        }
    }
    // The two words a mixed column uses, per lane
    std::vector<int> left(size_t(columns) * 4), right(size_t(columns) * 4);
    for (int c = 0; c < columns; c++)
        if (mixLeft[size_t(c)] >= 0)
            for (int k = 0; k < 4; k++)
            {
                left[size_t(c) * 4 + size_t(k)] = weight(mixLeft[size_t(c)], k);
                right[size_t(c) * 4 + size_t(k)] = weight(mixRight[size_t(c)], k);
            }

    // The walk down the picture: per destination row its source row, one or two mixed by
    // (y << 8) / sH and ((sH - y) << 8) / sH
    std::vector<Enlarge16Row> rows(size_t(dH >> 4));
    int y = dH - int(uint32_t((st & 15) * sH) >> 4), source = 0;
    for (auto& row : rows)
    {
        if (y >= sH)
        {
            row = {source, false, 0, 0};
            y -= sH;
            if (y == 0)
            {
                source++;
                y = dH;
            }
        }
        else
        {
            row = {source, true, ((y << 8) / sH) & 0xFFFF, (((sH - y) << 8) / sH) & 0xFFFF};
            source++;
            y += dH - sH;
        }
    }

    int srcBase = src + int(uint32_t(st) >> 4) * srcPitch + int(uint32_t(sl) >> 4) * 4;
    int dstBase = dst + int(uint32_t(dt) >> 4) * dstPitch + int(uint32_t(dl) >> 4) * 4;
    openshiina::Bytes upper(size_t(width) * 4), lower(size_t(width) * 4), output(size_t(columns) * 4);
    std::vector<int> mixed(size_t(width) * 4);
    for (size_t r = 0; r < rows.size(); r++)
    {
        const auto& row = rows[r];
        ReadBytes(srcBase + row.Source * srcPitch, upper.data(), width * 4);
        if (!row.Mixed)
        {
            // One source row
            for (size_t i = 0; i < mixed.size(); i++)
                mixed[i] = upper[i];
        }
        else
        {
            // Two source rows: (a * wa + b * wb) >> 8 in 16 bits
            int wa = row.Upper, wb = row.Lower;
            ReadBytes(srcBase + (row.Source + 1) * srcPitch, lower.data(), width * 4);
            for (size_t i = 0; i < mixed.size(); i++)
                mixed[i] = ((((upper[i] * wa) & 0xFFFF) + ((lower[i] * wb) & 0xFFFF)) & 0xFFFF) >> 8;
        }
        // The opaque build sets the first byte of what it mixes (a row from two, or two pixels)
        bool rowMixed = opaque && row.Mixed;
        for (int c = 0, o = 0; c < columns; c++, o += 4)
        {
            int p = from[size_t(c)] * 4;
            if (mixLeft[size_t(c)] < 0)
            {
                for (int k = 0; k < 4; k++)
                    output[size_t(o + k)] = uint8_t(mixed[size_t(p + k)]);
                if (rowMixed)
                    output[size_t(o)] = 0xFF;
                continue;
            }
            for (int k = 0; k < 4; k++)
            {
                int sum = (((mixed[size_t(p + k)] * left[size_t(o + k)]) & 0xFFFF) + ((mixed[size_t(p + 4 + k)] * right[size_t(o + k)]) & 0xFFFF)) & 0xFFFF;
                output[size_t(o + k)] = uint8_t(sum >> 8);
            }
            if (opaque)
                output[size_t(o)] = 0xFF;
        }
        WriteBytes(dstBase + int(r) * dstPitch, output);
    }
    return true;
}

} // namespace openshiina::scripting
