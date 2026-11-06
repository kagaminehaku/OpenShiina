#include "text/Text.h"

#include <algorithm>

namespace openshiina::Text {

namespace {

#include "text/Cp932Table.inc"

int LeadIndex(uint8_t b)
{
    if (b >= 0x81 && b <= 0x9F) return b - 0x81;
    if (b >= 0xE0 && b <= 0xFC) return b - 0xE0 + 0x1F;
    return -1;
}

void AppendUtf8(std::string& out, uint32_t c)
{
    if (c < 0x80)
        out += char(c);
    else if (c < 0x800)
    {
        out += char(0xC0 | c >> 6);
        out += char(0x80 | (c & 0x3F));
    }
    else
    {
        out += char(0xE0 | c >> 12);
        out += char(0x80 | (c >> 6 & 0x3F));
        out += char(0x80 | (c & 0x3F));
    }
}

} // namespace

std::string Cp932ToUtf8(const uint8_t* data, size_t length)
{
    std::string out;
    out.reserve(length + length / 2);
    for (size_t i = 0; i < length; i++)
    {
        uint8_t b = data[i];
        int lead = LeadIndex(b);
        if (lead < 0)
        {
            uint16_t c = kSingle[b];
            AppendUtf8(out, c != 0 || b == 0 ? c : '?');
            continue;
        }
        if (i + 1 < length && data[i + 1] >= 0x40 && data[i + 1] <= 0xFC)
        {
            uint16_t c = kDouble[lead][data[i + 1] - 0x40];
            if (c != 0)
            {
                AppendUtf8(out, c);
                i++;
                continue;
            }
        }
        out += '?';
    }
    return out;
}

std::string CString(const uint8_t* data, size_t length)
{
    const uint8_t* end = std::find(data, data + length, uint8_t(0));
    return Cp932ToUtf8(data, size_t(end - data));
}

// ASCII and the full-width Latin letters (U+FF41-FF5A, EF BD 81-9A in UTF-8): what the games'
// names use. .NET folds every letter of Unicode; Greek or Cyrillic names would differ.
std::string FoldCase(std::string_view utf8)
{
    std::string out(utf8);
    for (size_t i = 0; i < out.size(); i++)
    {
        unsigned char c = static_cast<unsigned char>(out[i]);
        if (c >= 'a' && c <= 'z')
            out[i] = char(c - 0x20);
        else if (c == 0xEF && i + 2 < out.size() && static_cast<unsigned char>(out[i + 1]) == 0xBD)
        {
            unsigned char t = static_cast<unsigned char>(out[i + 2]);
            if (t >= 0x81 && t <= 0x9A)
            {
                out[i + 1] = '\xBC';
                out[i + 2] = char(t + 0x20);
            }
            i += 2;
        }
    }
    return out;
}

bool EqualsIgnoreCase(std::string_view a, std::string_view b)
{
    return a.size() == b.size() && FoldCase(a) == FoldCase(b);
}

bool ContainsIgnoreCase(std::string_view text, std::string_view part)
{
    return FoldCase(text).find(FoldCase(part)) != std::string::npos;
}

bool LessIgnoreCase(std::string_view a, std::string_view b)
{
    return FoldCase(a) < FoldCase(b);
}

} // namespace openshiina::Text
