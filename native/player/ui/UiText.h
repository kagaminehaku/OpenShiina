// The player's own text (its screens, not the game's): the Switch's shared fonts (the standard one
// with Japanese, then Nintendo's extension with the buttons' symbols) drawn through FreeType into a
// coverage atlas the Renderer draws glyphs from; each glyph made once per size.
#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "ui/Renderer.h"

namespace openshiina::player {

class UiText
{
public:
    explicit UiText(Renderer& renderer);
    ~UiText();
    UiText(const UiText&) = delete;
    UiText& operator=(const UiText&) = delete;

    /// Draws UTF-8 text with its top left at (x, y), size pixels high; returns its width.
    float Draw(std::string_view text, float x, float y, float size, uint32_t color);
    /// The width of text drawn at size.
    float Measure(std::string_view text, float size);
    /// Text cut to width with "…" when it is longer.
    std::string Fit(std::string_view text, float size, float width);

private:
    struct Glyph
    {
        Rect Source;
        float Left = 0, Top = 0, Advance = 0;
    };
    const Glyph& Find(uint32_t codepoint, int size);

    Renderer& m_renderer;
    void* m_library = nullptr;
    std::vector<void*> m_faces;
    Texture m_atlas;
    int m_shelfX = 0, m_shelfY = 0, m_shelfHeight = 0;
    std::unordered_map<uint64_t, Glyph> m_glyphs;
};

/// The code points of UTF-8 text, one at a time (a bad byte is U+FFFD).
uint32_t NextCodepoint(std::string_view text, size_t& at);

} // namespace openshiina::player
