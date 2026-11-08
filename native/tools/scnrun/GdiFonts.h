// IScnFonts with Windows GDI (C# Platform/GdiFonts.cs), the font engine the game itself uses, so
// text comes out with the same glyphs as in the C# player's ScnBoot. One memory device context does
// all the work; the game's "A" calls are made with the matching "W" calls after decoding the
// Shift-JIS text, as GDI does for a SHIFTJIS_CHARSET font. The gaiji of translated scenarios
// (ScnGaiji) are measured and drawn with a Latin face of the font's height (Segoe UI), as there. For
// scnrun on Windows only.
#pragma once

#include <map>
#include <memory>

#include "scripting/ScnFonts.h"

namespace openshiina::tools {

class GdiFonts : public scripting::IScnFonts
{
public:
    GdiFonts();
    ~GdiFonts() override;

    int CreateFont(const scripting::ScnFontRequest& request) override;
    void DeleteFont(int font) override;
    std::pair<int, int> TextExtent(int font, const uint8_t* text, size_t length) override;
    int Ascent(int font) override;
    scripting::ScnGlyph Glyph(int font, int code) override;
    std::vector<std::string> FixedPitchJapaneseFaces() override;
    int AddMemoryFonts(const uint8_t* file, size_t length) override;

private:
    void Select(int font);
    /// The Latin font for the gaiji drawn with font: Segoe UI of its height and weight.
    void* Latin(int font);
    scripting::ScnGlyph GlyphOf(wchar_t ch);

    void* m_dc;
    void* m_systemFont;
    std::map<int, void*> m_fonts;
    std::map<int, scripting::ScnFontRequest> m_requests;
    // The Latin font of each font, for gaiji (made when first needed)
    std::map<int, void*> m_latin;
    int m_next = 1;
};

} // namespace openshiina::tools
