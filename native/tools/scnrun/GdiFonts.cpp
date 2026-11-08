// C# Platform/GdiFonts.cs
#include "GdiFonts.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#undef CreateFont

#include <algorithm>
#include <string>
#include <vector>

#include "text/Unicode.h"

namespace openshiina::tools {

namespace {

std::wstring WideOfUtf8(const std::string& utf8)
{
    std::wstring wide;
    for (char32_t c : Text::Utf8ToCodePoints(utf8))
        if (c >= 0x10000)
        {
            wide += wchar_t(0xD800 + ((c - 0x10000) >> 10));
            wide += wchar_t(0xDC00 + ((c - 0x10000) & 0x3FF));
        }
        else
            wide += wchar_t(c);
    return wide;
}

bool IsLeadByte(uint8_t b) { return (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC); }

constexpr uint8_t kGaijiLeads[] = {0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0x85, 0x86, 0xEB, 0xEC, 0xEF};

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
    for (auto& [id, font] : m_latin)
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
    m_requests[id] = r;
    return id;
}

void GdiFonts::DeleteFont(int font)
{
    auto found = m_fonts.find(font);
    if (found == m_fonts.end())
        return;
    DeleteObject(HGDIOBJ(found->second));
    m_fonts.erase(found);
    m_requests.erase(font);
    if (auto latin = m_latin.find(font); latin != m_latin.end())
    {
        DeleteObject(HGDIOBJ(latin->second));
        m_latin.erase(latin);
    }
}

void* GdiFonts::Latin(int font)
{
    if (auto found = m_latin.find(font); found != m_latin.end())
        return found->second;
    scripting::ScnFontRequest r;
    if (auto made = font != 0 ? m_requests.find(font) : m_requests.end(); made != m_requests.end())
        r = made->second;
    else
    {
        r.Height = 16;
        r.Weight = 700;
        r.CharSet = 0x80;
    }
    HFONT handle = CreateFontW(r.Height, 0, r.Escapement, r.Orientation, r.Weight, DWORD(r.Italic), DWORD(r.Underline), DWORD(r.StrikeOut),
                               DEFAULT_CHARSET, 0, 0, DWORD(r.Quality), 0, L"Segoe UI");
    m_latin[font] = handle;
    return handle;
}

void GdiFonts::Select(int font)
{
    auto found = font != 0 ? m_fonts.find(font) : m_fonts.end();
    SelectObject(HDC(m_dc), HGDIOBJ(found != m_fonts.end() ? found->second : m_systemFont));
}

std::pair<int, int> GdiFonts::TextExtent(int font, const uint8_t* text, size_t length)
{
    Select(font);
    // ScnGaiji.Decode: the text as the font engine draws it, the gaiji as the text they stand for
    bool hasGaiji = false;
    if (Gaiji != nullptr && Gaiji->size() > 1)
        for (size_t i = 0; i < length && !hasGaiji; i++)
            hasGaiji = std::find(std::begin(kGaijiLeads), std::end(kGaijiLeads), text[i]) != std::end(kGaijiLeads);
    if (!hasGaiji)
    {
        std::wstring s = Wide(text, length);
        SIZE size;
        return GetTextExtentPoint32W(HDC(m_dc), s.c_str(), int(s.size()), &size) ? std::pair(int(size.cx), int(size.cy)) : std::pair(0, 0);
    }
    std::wstring s;
    std::vector<bool> fromGaiji;
    size_t run = 0;
    auto flush = [&](size_t end) {
        std::wstring part = Wide(text + run, end - run);
        s += part;
        fromGaiji.insert(fromGaiji.end(), part.size(), false);
    };
    for (size_t i = 0; i < length;)
    {
        bool lead = IsLeadByte(text[i]);
        if (lead && i + 1 < length)
            if (auto mapped = Gaiji->find(text[i] << 8 | text[i + 1]); mapped != Gaiji->end())
            {
                flush(i);
                std::wstring w = WideOfUtf8(mapped->second);
                s += w;
                fromGaiji.insert(fromGaiji.end(), w.size(), true);
                i += 2;
                run = i;
                continue;
            }
        i += lead && i + 1 < length ? 2 : 1;
    }
    flush(length);
    // Runs of the font's characters and of gaiji, the height the font's
    TEXTMETRICW tm;
    int width = 0, height = GetTextMetricsW(HDC(m_dc), &tm) ? int(tm.tmHeight) : 0;
    for (size_t i = 0; i < s.size();)
    {
        size_t end = i + 1;
        while (end < s.size() && fromGaiji[end] == fromGaiji[i])
            end++;
        if (fromGaiji[i])
            SelectObject(HDC(m_dc), HGDIOBJ(Latin(font)));
        else
            Select(font);
        SIZE size;
        if (GetTextExtentPoint32W(HDC(m_dc), s.c_str() + i, int(end - i), &size))
            width += int(size.cx);
        i = end;
    }
    return {width, height};
}

int GdiFonts::Ascent(int font)
{
    Select(font);
    TEXTMETRICW tm;
    return GetTextMetricsW(HDC(m_dc), &tm) ? int(tm.tmAscent) : 0;
}

scripting::ScnGlyph GdiFonts::Glyph(int font, int code)
{
    if (Gaiji != nullptr)
        if (auto found = Gaiji->find(code); found != Gaiji->end())
        {
            // a translation's letters (none, one or two) with the Latin font
            SelectObject(HDC(m_dc), HGDIOBJ(Latin(font)));
            std::wstring text = WideOfUtf8(found->second);
            if (text.size() == 1)
                return GlyphOf(text[0]);
            std::vector<scripting::ScnGlyph> glyphs;
            for (wchar_t ch : text)
                glyphs.push_back(GlyphOf(ch));
            return scripting::ScnGlyph::Compose(glyphs);
        }
    Select(font);
    uint8_t bytes[2];
    size_t n = 0;
    if (code > 0xFF)
        bytes[n++] = uint8_t(code >> 8);
    bytes[n++] = uint8_t(code);
    std::wstring s = Wide(bytes, n);
    return GlyphOf(s.empty() ? L'?' : s[0]);
}

scripting::ScnGlyph GdiFonts::GlyphOf(wchar_t ch)
{
    // GetGlyphOutlineW of a character in the selected font
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
