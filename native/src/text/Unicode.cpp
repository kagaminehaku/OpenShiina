// See Unicode.h
#include "text/Unicode.h"

#include <algorithm>
#include <cstdint>

namespace openshiina::Text {

namespace {

struct CombiningRange
{
    char32_t First, Last;
    uint8_t Class;
};

struct Decomposition
{
    char32_t Code, First, Second;
};

struct Composition
{
    char32_t First, Second, Composite;
};

struct CodeRange
{
    char32_t First, Last;
};

#include "text/UnicodeTables.inc"

// Hangul syllables (Unicode 3.12)
constexpr char32_t SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
constexpr int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

template <typename T, size_t N>
bool InRanges(const T (&ranges)[N], char32_t c)
{
    auto it = std::upper_bound(std::begin(ranges), std::end(ranges), c, [](char32_t v, const T& r) { return v < r.First; });
    return it != std::begin(ranges) && c <= (it - 1)->Last;
}

uint8_t CombiningClass(char32_t c)
{
    auto it = std::upper_bound(std::begin(kCombining), std::end(kCombining), c, [](char32_t v, const CombiningRange& r) { return v < r.First; });
    return it != std::begin(kCombining) && c <= (it - 1)->Last ? (it - 1)->Class : 0;
}

void Decompose(char32_t c, std::u32string& out)
{
    if (c >= SBase && c < SBase + SCount)
    {
        int s = int(c - SBase);
        out += char32_t(LBase + s / NCount);
        out += char32_t(VBase + (s % NCount) / TCount);
        if (s % TCount != 0)
            out += char32_t(TBase + s % TCount);
        return;
    }
    auto it = std::lower_bound(std::begin(kDecompose), std::end(kDecompose), c, [](const Decomposition& d, char32_t v) { return d.Code < v; });
    if (it == std::end(kDecompose) || it->Code != c)
    {
        out += c;
        return;
    }
    Decompose(it->First, out);
    if (it->Second != 0)
        Decompose(it->Second, out);
}

char32_t Compose(char32_t a, char32_t b)
{
    if (a >= LBase && a < LBase + LCount && b >= VBase && b < VBase + VCount)
        return SBase + char32_t((int(a - LBase) * VCount + int(b - VBase)) * TCount);
    if (a >= SBase && a < SBase + SCount && (a - SBase) % TCount == 0 && b > TBase && b < TBase + TCount)
        return a + (b - TBase);
    auto it = std::lower_bound(std::begin(kCompose), std::end(kCompose), std::pair(a, b),
                               [](const Composition& c, const std::pair<char32_t, char32_t>& v) { return c.First < v.first || (c.First == v.first && c.Second < v.second); });
    return it != std::end(kCompose) && it->First == a && it->Second == b ? it->Composite : 0;
}

// Grapheme cluster break classes, as far as they matter here
enum class Break
{
    Other, CR, LF, Control, Extend, ZWJ, Regional, L, V, T, LV, LVT, Pictographic
};

Break BreakOf(char32_t c)
{
    if (c == '\r')
        return Break::CR;
    if (c == '\n')
        return Break::LF;
    if (c == 0x200D)
        return Break::ZWJ;
    if (InRanges(kControl, c))
        return Break::Control;
    if (InRanges(kExtend, c))
        return Break::Extend;
    if (c >= 0x1F1E6 && c <= 0x1F1FF)
        return Break::Regional;
    if (c >= 0x1100 && c <= 0x115F)
        return Break::L;
    if (c >= 0xA960 && c <= 0xA97C)
        return Break::L;
    if (c >= 0x1160 && c <= 0x11A7)
        return Break::V;
    if (c >= 0xD7B0 && c <= 0xD7C6)
        return Break::V;
    if (c >= 0x11A8 && c <= 0x11FF)
        return Break::T;
    if (c >= 0xD7CB && c <= 0xD7FB)
        return Break::T;
    if (c >= SBase && c < SBase + SCount)
        return (c - SBase) % TCount == 0 ? Break::LV : Break::LVT;
    if ((c >= 0x1F000 && c <= 0x1FAFF) || (c >= 0x2600 && c <= 0x27BF) || c == 0x00A9 || c == 0x00AE || (c >= 0x2190 && c <= 0x21FF))
        return Break::Pictographic;
    return Break::Other;
}

} // namespace

std::u32string Utf8ToCodePoints(std::string_view utf8)
{
    std::u32string out;
    out.reserve(utf8.size());
    for (size_t i = 0; i < utf8.size();)
    {
        uint8_t b = uint8_t(utf8[i]);
        int length = b < 0x80 ? 1 : b >= 0xF0 && b <= 0xF4 ? 4 : b >= 0xE0 ? 3 : b >= 0xC2 && b < 0xE0 ? 2 : 0;
        char32_t c = length == 1 ? b : length == 2 ? (b & 0x1Fu) : length == 3 ? (b & 0x0Fu) : (b & 0x07u);
        size_t n = 1;
        bool good = length > 0;
        for (; good && n < size_t(length); n++)
        {
            if (i + n >= utf8.size() || (uint8_t(utf8[i + n]) & 0xC0) != 0x80)
            {
                good = false;
                break;
            }
            c = c << 6 | (uint8_t(utf8[i + n]) & 0x3Fu);
        }
        if (good && ((length == 3 && (c < 0x800 || (c >= 0xD800 && c <= 0xDFFF))) || (length == 4 && (c < 0x10000 || c > 0x10FFFF))))
            good = false;
        out += good ? c : char32_t(0xFFFD);
        i += good ? size_t(length) : std::max<size_t>(n, 1);
    }
    return out;
}

std::string CodePointsToUtf8(std::u32string_view text)
{
    std::string out;
    out.reserve(text.size());
    for (char32_t c : text)
    {
        if (c < 0x80)
            out += char(c);
        else if (c < 0x800)
        {
            out += char(0xC0 | (c >> 6));
            out += char(0x80 | (c & 0x3F));
        }
        else if (c < 0x10000)
        {
            out += char(0xE0 | (c >> 12));
            out += char(0x80 | ((c >> 6) & 0x3F));
            out += char(0x80 | (c & 0x3F));
        }
        else
        {
            out += char(0xF0 | (c >> 18));
            out += char(0x80 | ((c >> 12) & 0x3F));
            out += char(0x80 | ((c >> 6) & 0x3F));
            out += char(0x80 | (c & 0x3F));
        }
    }
    return out;
}

std::u32string Nfc(std::u32string_view text)
{
    // Canonical decomposition, the marks put in order, then composition (UAX #15)
    std::u32string d;
    d.reserve(text.size());
    for (char32_t c : text)
        Decompose(c, d);
    for (size_t i = 1; i < d.size(); i++)
    {
        uint8_t cc = CombiningClass(d[i]);
        if (cc == 0)
            continue;
        size_t j = i;
        while (j > 0 && CombiningClass(d[j - 1]) > cc)
        {
            std::swap(d[j - 1], d[j]);
            j--;
        }
    }
    // (the composition of UAX #15's sample code: a character joins the last starter when nothing
    // between them blocks it)
    std::u32string out;
    if (d.empty())
        return out;
    out.reserve(d.size());
    out += d[0];
    size_t starter = 0;
    int lastClass = CombiningClass(d[0]) != 0 ? 256 : 0;
    for (size_t i = 1; i < d.size(); i++)
    {
        char32_t c = d[i];
        int cc = CombiningClass(c);
        if (char32_t composite = Compose(out[starter], c); composite != 0 && (lastClass < cc || lastClass == 0))
        {
            out[starter] = composite;
            continue;
        }
        if (cc == 0)
            starter = out.size();
        lastClass = cc;
        out += c;
    }
    return out;
}

std::vector<std::u32string> TextElements(std::u32string_view text)
{
    std::vector<std::u32string> elements;
    size_t start = 0;
    int regional = 0;
    bool pictographicRun = false;
    for (size_t i = 1; i <= text.size(); i++)
    {
        bool split = i == text.size();
        if (!split)
        {
            Break a = BreakOf(text[i - 1]), b = BreakOf(text[i]);
            if (a == Break::Regional)
                regional++;
            else
                regional = 0;
            if (a == Break::Pictographic)
                pictographicRun = true;
            else if (a != Break::Extend && a != Break::ZWJ)
                pictographicRun = false;
            if (a == Break::CR && b == Break::LF)
                split = false;
            else if (a == Break::CR || a == Break::LF || a == Break::Control || b == Break::CR || b == Break::LF || b == Break::Control)
                split = true;
            else if (a == Break::L && (b == Break::L || b == Break::V || b == Break::LV || b == Break::LVT))
                split = false;
            else if ((a == Break::LV || a == Break::V) && (b == Break::V || b == Break::T))
                split = false;
            else if ((a == Break::LVT || a == Break::T) && b == Break::T)
                split = false;
            else if (b == Break::Extend || b == Break::ZWJ)
                split = false;
            else if (a == Break::ZWJ && b == Break::Pictographic && pictographicRun)
                split = false;
            else if (a == Break::Regional && b == Break::Regional)
                split = regional % 2 == 0;
            else
                split = true;
        }
        if (split)
        {
            elements.emplace_back(text.substr(start, i - start));
            start = i;
        }
    }
    return elements;
}

} // namespace openshiina::Text
