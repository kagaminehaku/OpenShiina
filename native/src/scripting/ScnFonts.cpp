// C# Scripting/ScnFonts.cs
#include "scripting/ScnFonts.h"

#include <algorithm>
#include <tuple>

namespace openshiina::scripting {

bool ScnFontRequest::operator<(const ScnFontRequest& o) const
{
    return std::tie(Height, Width, Escapement, Orientation, Weight, Italic, Underline, StrikeOut, CharSet, Quality, PitchAndFamily, Face)
         < std::tie(o.Height, o.Width, o.Escapement, o.Orientation, o.Weight, o.Italic, o.Underline, o.StrikeOut, o.CharSet, o.Quality,
                    o.PitchAndFamily, o.Face);
}

ScnGlyph ScnGlyph::Blank(int advance)
{
    ScnGlyph g;
    g.CellIncX = int16_t(advance);
    return g;
}

ScnGlyph ScnGlyph::Compose(const std::vector<ScnGlyph>& glyphs)
{
    int pen = 0, left = 0x7FFFFFFF, right = int(0x80000000), top = int(0x80000000), bottom = 0x7FFFFFFF;
    std::vector<int> at(glyphs.size());
    for (size_t i = 0; i < glyphs.size(); i++)
    {
        const auto& g = glyphs[i];
        at[i] = pen;
        pen += g.CellIncX;
        if (std::all_of(g.Bits.begin(), g.Bits.end(), [](uint8_t b) { return b == 0; }))
            continue;
        left = std::min(left, at[i] + g.OriginX);
        right = std::max(right, at[i] + g.OriginX + g.BlackBoxX);
        top = std::max(top, g.OriginY);
        bottom = std::min(bottom, g.OriginY - g.BlackBoxY);
    }
    if (right <= left || top <= bottom)
        return Blank(pen);
    int width = right - left, height = top - bottom, pitch = (width + 3) & ~3;
    Bytes bits(size_t(pitch) * size_t(height));
    for (size_t i = 0; i < glyphs.size(); i++)
    {
        const auto& g = glyphs[i];
        int ox = at[i] + g.OriginX - left, oy = top - g.OriginY;
        for (int y = 0; y < g.BlackBoxY; y++)
            for (int x = 0; x < g.BlackBoxX; x++)
            {
                size_t from = size_t(y * g.Pitch + x);
                if (from >= g.Bits.size() || g.Bits[from] == 0)
                    continue;
                size_t to = size_t((oy + y) * pitch + ox + x);
                bits[to] = std::max(bits[to], g.Bits[from]);
            }
    }
    ScnGlyph composed;
    composed.BlackBoxX = width;
    composed.BlackBoxY = height;
    composed.OriginX = left;
    composed.OriginY = top;
    composed.CellIncX = int16_t(pen);
    composed.Pitch = pitch;
    composed.Bits = std::move(bits);
    return composed;
}

} // namespace openshiina::scripting
