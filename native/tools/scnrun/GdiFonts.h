// IScnFonts with Windows GDI (C# Platform/GdiFonts.cs), the font engine the game itself uses, so
// text comes out with the same glyphs as in the C# player's ScnBoot. One memory device context does
// all the work; the game's "A" calls are made with the matching "W" calls after decoding the
// Shift-JIS text, as GDI does for a SHIFTJIS_CHARSET font. For scnrun on Windows only (the gaiji of
// translated scenarios are not drawn).
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

    void* m_dc;
    void* m_systemFont;
    std::map<int, void*> m_fonts;
    int m_next = 1;
};

} // namespace openshiina::tools
