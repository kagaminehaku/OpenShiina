// IScnFonts on the Switch (the C# player's SkiaFonts, with FreeType and the Switch's own fonts): see
// FreeTypeFonts.cpp.
#pragma once

#include <map>
#include <memory>
#include <string>
#include <vector>

#include "scripting/ScnFonts.h"

namespace openshiina::player {

class FreeTypeFonts : public scripting::IScnFonts
{
public:
    FreeTypeFonts();
    ~FreeTypeFonts() override;
    FreeTypeFonts(const FreeTypeFonts&) = delete;
    FreeTypeFonts& operator=(const FreeTypeFonts&) = delete;

    /// The Switch's fonts were found (the shared fonts of the system).
    bool Ready() const { return !m_faces.empty(); }

    int CreateFont(const scripting::ScnFontRequest& request) override;
    void DeleteFont(int font) override;
    std::pair<int, int> TextExtent(int font, const uint8_t* text, size_t length) override;
    int Ascent(int font) override;
    scripting::ScnGlyph Glyph(int font, int code) override;
    std::vector<std::string> FixedPitchJapaneseFaces() override;
    int AddMemoryFonts(const uint8_t* data, size_t length) override;

private:
    struct Face;
    struct Font;

    std::unique_ptr<Font> Make(const scripting::ScnFontRequest& request);
    Font& Get(int font);
    /// The glyph of a character code with a font (its own face, or one that has it).
    scripting::ScnGlyph Render(Font& f, int code);
    /// A stand-in's advance for a character code: MS Gothic's.
    int Advance(Font& f, int code);

    void* m_library = nullptr;
    // The system's faces (Japanese first) and those the scripts added, with the bytes they are read from
    std::vector<std::unique_ptr<Face>> m_faces, m_memoryFaces;
    std::map<int, std::unique_ptr<Font>> m_fonts;
    std::unique_ptr<Font> m_system;
    int m_next = 1;
};

} // namespace openshiina::player
