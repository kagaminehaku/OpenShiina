// C# Platform/GdiFonts.cs
#include "GdiFonts.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#undef CreateFont

#include <string>

namespace openshiina::tools {

namespace {

std::wstring Wide(const uint8_t* text, size_t length)
{
    if (length == 0)
        return {};
    int n = MultiByteToWideChar(932, 0, reinterpret_cast<const char*>(text), int(length), nullptr, 0);
    std::wstring wide(size_t(n), L'\0');
    MultiByteToWideChar(932, 0, reinterpret_cast<const char*>(text), int(length), wide.data(), n);
    return wide;
}

std::string Sjis(const wchar_t* text)
{
    int n = WideCharToMultiByte(932, 0, text, -1, nullptr, 0, nullptr, nullptr);
    std::string bytes(size_t(n > 0 ? n - 1 : 0), '\0');
    if (n > 1)
        WideCharToMultiByte(932, 0, text, -1, bytes.data(), n, nullptr, nullptr);
    return bytes;
}

} // namespace

GdiFonts::GdiFonts()
{
    m_dc = CreateCompatibleDC(nullptr);
    m_systemFont = GetStockObject(SYSTEM_FONT);
}

GdiFonts::~GdiFonts()
{
    for (auto& [id, font] : m_fonts)
        DeleteObject(HGDIOBJ(font));
    DeleteDC(HDC(m_dc));
}

int GdiFonts::CreateFont(const scripting::ScnFontRequest& r)
{
    std::wstring face = Wide(reinterpret_cast<const uint8_t*>(r.Face.data()), r.Face.size());
    HFONT font = CreateFontW(r.Height, r.Width, r.Escapement, r.Orientation, r.Weight, DWORD(r.Italic), DWORD(r.Underline),
                             DWORD(r.StrikeOut), DWORD(r.CharSet), 0, 0, DWORD(r.Quality), DWORD(r.PitchAndFamily), face.c_str());
    if (!font)
        return 0;
    int id = m_next++;
    m_fonts[id] = font;
    return id;
}

void GdiFonts::DeleteFont(int font)
{
    auto found = m_fonts.find(font);
    if (found == m_fonts.end())
        return;
    DeleteObject(HGDIOBJ(found->second));
    m_fonts.erase(found);
}

void GdiFonts::Select(int font)
{
    auto found = font != 0 ? m_fonts.find(font) : m_fonts.end();
    SelectObject(HDC(m_dc), HGDIOBJ(found != m_fonts.end() ? found->second : m_systemFont));
}

std::pair<int, int> GdiFonts::TextExtent(int font, const uint8_t* text, size_t length)
{
    Select(font);
    std::wstring s = Wide(text, length);
    SIZE size;
    return GetTextExtentPoint32W(HDC(m_dc), s.c_str(), int(s.size()), &size) ? std::pair(int(size.cx), int(size.cy)) : std::pair(0, 0);
}

int GdiFonts::Ascent(int font)
{
    Select(font);
    TEXTMETRICW tm;
    return GetTextMetricsW(HDC(m_dc), &tm) ? int(tm.tmAscent) : 0;
}

scripting::ScnGlyph GdiFonts::Glyph(int font, int code)
{
    Select(font);
    uint8_t bytes[2];
    size_t n = 0;
    if (code > 0xFF)
        bytes[n++] = uint8_t(code >> 8);
    bytes[n++] = uint8_t(code);
    std::wstring s = Wide(bytes, n);
    wchar_t ch = s.empty() ? L'?' : s[0];
    MAT2 matrix{};
    matrix.eM11.value = 1;
    matrix.eM22.value = 1;
    GLYPHMETRICS gm{};
    DWORD size = GetGlyphOutlineW(HDC(m_dc), ch, GGO_GRAY8_BITMAP, &gm, 0, nullptr, &matrix);
    int pitch = int((gm.gmBlackBoxX + 3) & ~3u);
    if (size == 0 || size == GDI_ERROR)
        size = DWORD(int(gm.gmBlackBoxY) * pitch);
    scripting::ScnGlyph glyph;
    glyph.Bits.assign(size, 0);
    if (size > 0)
        GetGlyphOutlineW(HDC(m_dc), ch, GGO_GRAY8_BITMAP, &gm, size, glyph.Bits.data(), &matrix);
    glyph.BlackBoxX = int(gm.gmBlackBoxX);
    glyph.BlackBoxY = int(gm.gmBlackBoxY);
    glyph.OriginX = gm.gmptGlyphOrigin.x;
    glyph.OriginY = gm.gmptGlyphOrigin.y;
    glyph.CellIncX = gm.gmCellIncX;
    glyph.CellIncY = gm.gmCellIncY;
    glyph.Pitch = pitch;
    return glyph;
}

std::vector<std::string> GdiFonts::FixedPitchJapaneseFaces()
{
    // EnumFontFamiliesExW and the names in Shift-JIS
    std::vector<std::string> faces;
    LOGFONTW logFont{};
    logFont.lfCharSet = SHIFTJIS_CHARSET;
    EnumFontFamiliesExW(
        HDC(m_dc), &logFont,
        [](const LOGFONTW* lf, const TEXTMETRICW* tm, DWORD, LPARAM parameter) -> int {
            auto& list = *reinterpret_cast<std::vector<std::string>*>(parameter);
            if (lf->lfCharSet == SHIFTJIS_CHARSET && (tm->tmPitchAndFamily & TMPF_TRUETYPE) != 0 && (lf->lfPitchAndFamily & 3) == FIXED_PITCH
                && lf->lfFaceName[0] != L'@')
            {
                std::string bytes = Sjis(lf->lfFaceName);
                list.push_back(bytes.size() > 31 ? bytes.substr(0, 31) : bytes);
            }
            return 1;
        },
        LPARAM(&faces), 0);
    return faces;
}

int GdiFonts::AddMemoryFonts(const uint8_t* file, size_t length)
{
    // Private to this process, gone with it
    DWORD count = 0;
    std::vector<uint8_t> copy(file, file + length);
    return AddFontMemResourceEx(copy.data(), DWORD(length), nullptr, &count) ? int(count) : 0;
}

} // namespace openshiina::tools
