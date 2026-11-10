// The player's own text (see UiText.h)
#include "ui/UiText.h"

#include <switch.h>

#include <ft2build.h>
#include FT_FREETYPE_H

#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>

namespace openshiina::player {

namespace {

constexpr int AtlasSize = 1024;

FT_Face Face(void* f) { return static_cast<FT_Face>(f); }

} // namespace

uint32_t NextCodepoint(std::string_view text, size_t& at)
{
    auto byte = [&](size_t i) { return uint8_t(text[i]); };
    uint8_t b = byte(at);
    int extra = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
    if (extra == 0)
    {
        at++;
        return b;
    }
    if (extra < 0 || at + size_t(extra) >= text.size())
    {
        at++;
        return 0xFFFD;
    }
    uint32_t c = b & (0x3F >> extra);
    for (int k = 1; k <= extra; k++)
    {
        uint8_t n = byte(at + size_t(k));
        if ((n & 0xC0) != 0x80)
        {
            at++;
            return 0xFFFD;
        }
        c = c << 6 | (n & 0x3F);
    }
    at += size_t(extra) + 1;
    return c;
}

UiText::UiText(Renderer& renderer) : m_renderer(renderer)
{
    m_atlas = renderer.CreateTexture(AtlasSize, AtlasSize, true);
    FT_Library library = nullptr;
    if (FT_Init_FreeType(&library) != 0)
        return;
    m_library = library;
    if (R_FAILED(plInitialize(PlServiceType_User)))
        return;
    for (PlSharedFontType type : {PlSharedFontType_Standard, PlSharedFontType_NintendoExt, PlSharedFontType_ChineseSimplified, PlSharedFontType_KO})
    {
        PlFontData data{};
        FT_Face face = nullptr;
        if (R_SUCCEEDED(plGetSharedFontByType(&data, type))
            && FT_New_Memory_Face(library, static_cast<const FT_Byte*>(data.address), FT_Long(data.size), 0, &face) == 0)
            m_faces.push_back(face);
    }
    // then a font of the SD card's or the romfs', for what the console's lack (Vietnamese: the
    // Switch has no such language, its fonts may not have all of its letters)
    for (const char* path : {"sdmc:/OpenShiina/font.ttf", "sdmc:/OpenShiina/font.otf", "romfs:/ui/vietnamese.ttf"})
    {
        FT_Face face = nullptr;
        if (FT_New_Face(library, path, 0, &face) == 0)
        {
            m_faces.push_back(face);
            m_extraFonts.push_back(path);
        }
    }
}

UiText::~UiText()
{
    for (void* face : m_faces)
        FT_Done_Face(Face(face));
    if (m_library)
        FT_Done_FreeType(static_cast<FT_Library>(m_library));
    plExit();
}

std::u32string UiText::Missing(std::string_view text)
{
    std::u32string missing;
    for (size_t at = 0; at < text.size();)
    {
        uint32_t c = NextCodepoint(text, at);
        bool found = false;
        for (void* f : m_faces)
            found |= FT_Get_Char_Index(Face(f), c) != 0;
        if (!found && missing.find(char32_t(c)) == std::u32string::npos)
            missing += char32_t(c);
    }
    return missing;
}

const UiText::Glyph& UiText::Find(uint32_t codepoint, int size)
{
    uint64_t key = uint64_t(codepoint) << 16 | uint64_t(size);
    if (auto found = m_glyphs.find(key); found != m_glyphs.end())
        return found->second;
    Glyph g;
    for (void* f : m_faces)
    {
        FT_Face face = Face(f);
        FT_UInt index = FT_Get_Char_Index(face, codepoint);
        if (index == 0)
            continue;
        FT_Set_Pixel_Sizes(face, 0, FT_UInt(size));
        if (FT_Load_Glyph(face, index, FT_LOAD_RENDER) != 0)
            break;
        FT_GlyphSlot slot = face->glyph;
        int w = int(slot->bitmap.width), h = int(slot->bitmap.rows);
        // a shelf of the atlas (all the glyphs made again when it is full)
        if (m_shelfX + w + 1 > AtlasSize)
        {
            m_shelfX = 0;
            m_shelfY += m_shelfHeight + 1;
            m_shelfHeight = 0;
        }
        if (m_shelfY + h + 1 > AtlasSize)
        {
            m_glyphs.clear();
            std::memset(m_atlas.Pixels, 0, size_t(m_atlas.Stride) * size_t(m_atlas.Height));
            m_shelfX = m_shelfY = m_shelfHeight = 0;
        }
        for (int y = 0; y < h; y++)
            std::memcpy(m_atlas.Pixels + size_t(m_shelfY + y) * m_atlas.Stride + size_t(m_shelfX), slot->bitmap.buffer + y * slot->bitmap.pitch,
                        size_t(w));
        g.Source = {float(m_shelfX), float(m_shelfY), float(w), float(h)};
        g.Left = float(slot->bitmap_left);
        // the top below the line's top: the ascender at this size
        g.Top = float(face->size->metrics.ascender) / 64.0f - float(slot->bitmap_top);
        g.Advance = float(slot->advance.x) / 64.0f;
        m_shelfX += w + 1;
        m_shelfHeight = std::max(m_shelfHeight, h);
        break;
    }
    return m_glyphs.emplace(key, g).first->second;
}

float UiText::Draw(std::string_view text, float x, float y, float size, uint32_t color)
{
    int px = std::max(1, int(std::lround(size)));
    float start = x;
    for (size_t at = 0; at < text.size();)
    {
        const Glyph& g = Find(NextCodepoint(text, at), px);
        if (g.Source.W > 0)
            m_renderer.Glyph(m_atlas, {std::round(x + g.Left), std::round(y + g.Top), g.Source.W, g.Source.H}, g.Source, color);
        x += g.Advance;
    }
    return x - start;
}

float UiText::Measure(std::string_view text, float size)
{
    int px = std::max(1, int(std::lround(size)));
    float w = 0;
    for (size_t at = 0; at < text.size();)
        w += Find(NextCodepoint(text, at), px).Advance;
    return w;
}

std::string UiText::Fit(std::string_view text, float size, float width)
{
    if (Measure(text, size) <= width)
        return std::string(text);
    const char* ellipsis = "\xE2\x80\xA6";
    float room = width - Measure(ellipsis, size);
    int px = std::max(1, int(std::lround(size)));
    float w = 0;
    size_t at = 0, end = 0;
    while (at < text.size())
    {
        float advance = Find(NextCodepoint(text, at), px).Advance;
        if (w + advance > room)
            break;
        w += advance;
        end = at;
    }
    return std::string(text.substr(0, end)) + ellipsis;
}

} // namespace openshiina::player
