// What the text system needs from the platform's font engine (C# Scripting/ScnFonts.cs). The
// executable draws text with Windows GDI: CreateFontA, GetTextExtentPoint32A, GetTextMetricsA and
// GetGlyphOutlineA with GGO_GRAY8_BITMAP (65 levels of coverage), and blends the glyphs into its
// 24-bit pictures itself. The blending is done by the interpreter, so a platform only has to supply
// these.
#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <vector>

#include "io/Binary.h"

namespace openshiina::scripting {

/// The arguments of CreateFontA (the face in Shift-JIS).
struct ScnFontRequest
{
    int Height = 0, Width = 0, Escapement = 0, Orientation = 0, Weight = 0;
    int Italic = 0, Underline = 0, StrikeOut = 0, CharSet = 0, Quality = 0, PitchAndFamily = 0;
    std::string Face;

    bool operator<(const ScnFontRequest& o) const;
    bool operator==(const ScnFontRequest& o) const = default;
};

/// A glyph as GetGlyphOutline gives it with GGO_GRAY8_BITMAP and the identity matrix: Bits are rows
/// of Pitch bytes (BlackBoxX rounded up to 4), each 0-64.
struct ScnGlyph
{
    int BlackBoxX = 1, BlackBoxY = 1, OriginX = 0, OriginY = 0;
    int16_t CellIncX = 0, CellIncY = 0;
    int Pitch = 4;
    Bytes Bits = Bytes(4);

    /// GDI's glyph of no ink (a 1 x 1 box of nothing) with that advance.
    static ScnGlyph Blank(int advance);
    /// Glyphs side by side as one, each at the advances of those before it: the box around their
    /// ink, the sum of their advances.
    static ScnGlyph Compose(const std::vector<ScnGlyph>& glyphs);
};

/// A platform font engine. Text is Shift-JIS (code page 932) as the scripts have it.
class IScnFonts
{
public:
    virtual ~IScnFonts() = default;

    /// A new font; returns its handle, never reused, or 0 when it cannot be made.
    virtual int CreateFont(const ScnFontRequest& request) = 0;
    /// Deletes a font made by CreateFont.
    virtual void DeleteFont(int font) = 0;
    /// GetTextExtentPoint32; font 0 is the font a new device context has (SYSTEM_FONT).
    virtual std::pair<int, int> TextExtent(int font, const uint8_t* text, size_t length) = 0;
    /// tmAscent of GetTextMetrics.
    virtual int Ascent(int font) = 0;
    /// GetGlyphOutline of a character code as the executable passes it: a single byte, or lead byte
    /// * 256 + trail byte.
    virtual ScnGlyph Glyph(int font, int code) = 0;
    /// The characters the gaiji codes of translated scenarios stand for, or null when there are none.
    const std::map<int, std::string>* Gaiji = nullptr;
    /// The faces op_006F lists: TrueType, fixed pitch, not the vertical "@" faces; names in
    /// Shift-JIS, at most 31 bytes.
    virtual std::vector<std::string> FixedPitchJapaneseFaces() { return {}; }
    /// AddFontMemResourceEx: the fonts of a TrueType / OpenType file in memory, for this font engine
    /// alone; how many were added, 0 when none.
    virtual int AddMemoryFonts(const uint8_t*, size_t) { return 0; }
};

} // namespace openshiina::scripting
