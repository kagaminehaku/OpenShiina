// IScnFonts on the Switch, in place of Windows GDI (CreateFontA, GetTextExtentPoint32A, GetTextMetricsA
// and GetGlyphOutlineA with GGO_GRAY8_BITMAP): the C# player's SkiaFonts, with FreeType and the
// Switch's own fonts (the system's shared fonts: its Japanese one first, then the others for a
// character it lacks). The games ask for ＭＳ ゴシック, which the Switch does not have: its stand-in is
// sized and measured as MS Gothic is (its cell is its em, ascent 0.859 em), and, the Switch's font
// being proportional where MS Gothic is fixed-pitch, its advances are MS Gothic's too - a whole em a
// double-byte character, half an em a single-byte one, each glyph centred in its cell - since the
// games lay their text out for it. Fonts the scripts add from their archives (AddFontMemResourceEx:
// Libra of the Vampire Princess's Noto Serif, Roboto...) are found by their family name first, and
// measured by their own metrics.
//
// GDI's terms, as the engine uses them: a font's height > 0 is the cell height (ascent + descent), < 0
// the em height; a width other than 0 stretches the font to that average character width. A glyph is
// its black box (the smallest rectangle around its ink) in 65 levels of coverage (0-64), rows padded to
// 4 bytes, placed by its origin (left of the box from the pen, top of the box above the baseline) and
// followed by its advance.
#include "FreeTypeFonts.h"

#include <algorithm>
#include <cmath>

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_SYNTHESIS_H
#include <switch.h>

#include "text/Text.h"

namespace openshiina::player {

using scripting::ScnGlyph;

namespace {

// MS Gothic's measures, in ems
constexpr float StandInAscent = 0.859f, StandInAverageWidth = 0.5f;

FT_Library Library(void* library) { return static_cast<FT_Library>(library); }

bool IsLead(int b) { return (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC); }

/// The first character of UTF-8 text.
uint32_t FirstCodePoint(const std::string& utf8)
{
    if (utf8.empty())
        return '?';
    auto b = [&](size_t i) { return uint32_t(uint8_t(utf8[i])); };
    uint32_t c = b(0);
    if (c < 0x80)
        return c;
    if ((c & 0xE0) == 0xC0 && utf8.size() >= 2)
        return (c & 0x1F) << 6 | (b(1) & 0x3F);
    if ((c & 0xF0) == 0xE0 && utf8.size() >= 3)
        return (c & 0x0F) << 12 | (b(1) & 0x3F) << 6 | (b(2) & 0x3F);
    if (utf8.size() >= 4)
        return (c & 0x07) << 18 | (b(1) & 0x3F) << 12 | (b(2) & 0x3F) << 6 | (b(3) & 0x3F);
    return '?';
}

/// A character code as the executable passes it (one byte, or lead * 256 + trail) as Unicode.
uint32_t Decode(int code)
{
    uint8_t bytes[2] = {uint8_t(code >> 8), uint8_t(code)};
    return code > 0xFF ? FirstCodePoint(Text::Cp932ToUtf8(bytes, 2)) : FirstCodePoint(Text::Cp932ToUtf8(bytes + 1, 1));
}

} // namespace

struct FreeTypeFonts::Face
{
    FT_Face Ft = nullptr;
    std::string Family;
    // The file the face is read from, for the faces the scripts add (the system's stay in its memory)
    std::vector<uint8_t> Bytes;
};

struct FreeTypeFonts::Font
{
    Face* Own = nullptr;        // a face the scripts added, or null: the system's, measured as MS Gothic
    float Size = 16, ScaleX = 1;
    bool Bold = false, Italic = false;
    int Ascent = 14, Descent = 2;
    std::map<int, ScnGlyph> Glyphs;
};

FreeTypeFonts::FreeTypeFonts()
{
    FT_Library library = nullptr;
    if (FT_Init_FreeType(&library) != 0)
        return;
    m_library = library;
    if (R_FAILED(plInitialize(PlServiceType_User)))
        return;
    // Japanese first, then the others for what it lacks
    const PlSharedFontType types[] = {PlSharedFontType_Standard, PlSharedFontType_NintendoExt, PlSharedFontType_ChineseSimplified,
                                      PlSharedFontType_ExtChineseSimplified, PlSharedFontType_ChineseTraditional, PlSharedFontType_KO};
    for (auto type : types)
    {
        PlFontData data;
        if (R_FAILED(plGetSharedFontByType(&data, type)))
            continue;
        auto face = std::make_unique<Face>();
        if (FT_New_Memory_Face(library, static_cast<const FT_Byte*>(data.address), FT_Long(data.size), 0, &face->Ft) != 0)
            continue;
        face->Family = face->Ft->family_name ? face->Ft->family_name : "";
        m_faces.push_back(std::move(face));
    }
    // SYSTEM_FONT, the font of a new device context: about 16 pixels high, bold
    scripting::ScnFontRequest system;
    system.Height = 16;
    system.Weight = 700;
    system.Face = "System";
    m_system = Make(system);
}

FreeTypeFonts::~FreeTypeFonts()
{
    for (auto* list : {&m_faces, &m_memoryFaces})
        for (auto& face : *list)
            FT_Done_Face(face->Ft);
    if (m_library)
        FT_Done_FreeType(Library(m_library));
    plExit();
}

std::unique_ptr<FreeTypeFonts::Font> FreeTypeFonts::Make(const scripting::ScnFontRequest& r)
{
    auto font = std::make_unique<Font>();
    font->Bold = r.Weight >= 600;
    font->Italic = r.Italic != 0;
    std::string family = Text::Cp932ToUtf8(r.Face);
    for (auto& face : m_memoryFaces)
        if (Text::EqualsIgnoreCase(face->Family, family))
            font->Own = face.get();
    if (font->Own)
    {
        // A face the scripts added: measured by its own metrics. Height > 0 the cell, < 0 the em
        FT_Face ft = font->Own->Ft;
        float units = float(ft->units_per_EM), cell = float(std::max<FT_Short>(1, FT_Short(ft->ascender - ft->descender)));
        font->Size = r.Height > 0 ? r.Height * units / cell : r.Height < 0 ? float(-r.Height) : 16;
        if (r.Width > 0)
            font->ScaleX = r.Width / (font->Size * StandInAverageWidth);
        font->Ascent = int(std::lround(ft->ascender * font->Size / units));
        font->Descent = int(std::lround(-ft->descender * font->Size / units));
        return font;
    }
    // Measured as MS Gothic: its cell is its em
    float em = r.Height > 0 ? float(r.Height) : r.Height < 0 ? float(-r.Height) : 16;
    font->Size = em;
    if (r.Width > 0)
        font->ScaleX = r.Width / (em * StandInAverageWidth);
    font->Ascent = int(std::lround(em * StandInAscent));
    font->Descent = int(std::lround(em)) - font->Ascent;
    return font;
}

int FreeTypeFonts::CreateFont(const scripting::ScnFontRequest& request)
{
    int id = m_next++;
    m_fonts[id] = Make(request);
    return id;
}

void FreeTypeFonts::DeleteFont(int font) { m_fonts.erase(font); }

FreeTypeFonts::Font& FreeTypeFonts::Get(int font)
{
    auto found = font != 0 ? m_fonts.find(font) : m_fonts.end();
    return found != m_fonts.end() ? *found->second : *m_system;
}

int FreeTypeFonts::AddMemoryFonts(const uint8_t* data, size_t length)
{
    if (!m_library)
        return 0;
    // Each face of the file (a collection has several)
    int added = 0;
    for (FT_Long index = 0; index < 64; index++)
    {
        auto face = std::make_unique<Face>();
        face->Bytes.assign(data, data + length);
        if (FT_New_Memory_Face(Library(m_library), face->Bytes.data(), FT_Long(face->Bytes.size()), index, &face->Ft) != 0)
            break;
        face->Family = face->Ft->family_name ? face->Ft->family_name : "";
        FT_Long faces = face->Ft->num_faces;
        m_memoryFaces.push_back(std::move(face));
        added++;
        if (index + 1 >= faces)
            break;
    }
    return added;
}

int FreeTypeFonts::Advance(Font& f, int code)
{
    // A stand-in's: MS Gothic's (a face the scripts added gives its glyphs' own)
    return int(std::lround(f.Size * (code > 0xFF ? 1 : StandInAverageWidth) * f.ScaleX));
}

std::pair<int, int> FreeTypeFonts::TextExtent(int font, const uint8_t* text, size_t length)
{
    Font& f = Get(font);
    int width = 0;
    for (size_t i = 0; i < length; i++)
    {
        int code = text[i];
        if (IsLead(code) && i + 1 < length)
            code = code << 8 | text[++i];
        width += f.Own ? Glyph(font, code).CellIncX : Advance(f, code);
    }
    return {width, f.Ascent + f.Descent};
}

int FreeTypeFonts::Ascent(int font) { return Get(font).Ascent; }

ScnGlyph FreeTypeFonts::Glyph(int font, int code)
{
    Font& f = Get(font);
    auto cached = f.Glyphs.find(code);
    if (cached != f.Glyphs.end())
        return cached->second;
    return f.Glyphs[code] = Render(f, code);
}

ScnGlyph FreeTypeFonts::Render(Font& f, int code)
{
    uint32_t ch = Decode(code);
    // The face to draw with: the font's own, or the first of the system's that has the character
    FT_Face face = nullptr;
    FT_UInt index = 0;
    if (f.Own && (index = FT_Get_Char_Index(f.Own->Ft, ch)) != 0)
        face = f.Own->Ft;
    for (size_t k = 0; !face && k < m_faces.size(); k++)
        if ((index = FT_Get_Char_Index(m_faces[k]->Ft, ch)) != 0)
            face = m_faces[k]->Ft;
    int cell = f.Own ? 0 : Advance(f, code);
    if (!face)
        return ScnGlyph::Blank(cell);
    FT_Set_Pixel_Sizes(face, 0, FT_UInt(std::max(1L, std::lround(f.Size))));
    // The width stretched, italics slanted when the face has none of its own
    FT_Matrix matrix{FT_Fixed(f.ScaleX * 0x10000), f.Italic && !(face->style_flags & FT_STYLE_FLAG_ITALIC) ? FT_Fixed(0x3333) : 0, 0, 0x10000};
    FT_Set_Transform(face, &matrix, nullptr);
    if (FT_Load_Glyph(face, index, FT_LOAD_NO_BITMAP | FT_LOAD_TARGET_NORMAL) != 0)
        return ScnGlyph::Blank(cell);
    if (f.Bold && !(face->style_flags & FT_STYLE_FLAG_BOLD))
        FT_GlyphSlot_Embolden(face->glyph);
    int ownAdvance = int(std::lround(face->glyph->advance.x / 64.0));
    int advance = f.Own ? ownAdvance : cell;
    if (FT_Render_Glyph(face->glyph, FT_RENDER_MODE_NORMAL) != 0)
        return ScnGlyph::Blank(advance);
    const FT_Bitmap& bitmap = face->glyph->bitmap;
    // The black box: the rows and columns with ink
    int width = int(bitmap.width), height = int(bitmap.rows), x0 = width, x1 = -1, y0 = height, y1 = -1;
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (bitmap.buffer[y * bitmap.pitch + x] != 0)
            {
                x0 = std::min(x0, x);
                x1 = std::max(x1, x);
                y0 = std::min(y0, y);
                y1 = std::max(y1, y);
            }
    if (x1 < 0)
        return ScnGlyph::Blank(advance);
    ScnGlyph glyph;
    glyph.BlackBoxX = x1 - x0 + 1;
    glyph.BlackBoxY = y1 - y0 + 1;
    glyph.Pitch = (glyph.BlackBoxX + 3) & ~3;
    glyph.Bits.assign(size_t(glyph.Pitch) * size_t(glyph.BlackBoxY), 0);
    for (int y = 0; y < glyph.BlackBoxY; y++)
        for (int x = 0; x < glyph.BlackBoxX; x++)
            glyph.Bits[size_t(y * glyph.Pitch + x)] = uint8_t((bitmap.buffer[(y0 + y) * bitmap.pitch + x0 + x] * 64 + 127) / 255);
    // Origin: the box's left from the pen (a stand-in's glyph centred in its MS Gothic cell), its top
    // above the baseline
    glyph.OriginX = face->glyph->bitmap_left + x0 + (f.Own ? 0 : (cell - ownAdvance) / 2);
    glyph.OriginY = face->glyph->bitmap_top - y0;
    glyph.CellIncX = int16_t(advance);
    glyph.CellIncY = 0;
    return glyph;
}

std::vector<std::string> FreeTypeFonts::FixedPitchJapaneseFaces()
{
    // The Switch has no font of its own by name: the one the games ask for, which the stand-in is
    return {Text::Utf8ToCp932("ＭＳ ゴシック")};
}

} // namespace openshiina::player
