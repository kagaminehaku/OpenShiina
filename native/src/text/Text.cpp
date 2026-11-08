#include "text/Text.h"

#include <algorithm>
#include <unordered_map>

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

namespace {

/// The code of each character: the tables backwards, NEC's selection of IBM's last.
const std::unordered_map<uint32_t, uint16_t>& Cp932Codes()
{
    static const std::unordered_map<uint32_t, uint16_t> codes = [] {
        std::unordered_map<uint32_t, uint16_t> map;
        for (int b = 0; b < 256; b++)
            if (LeadIndex(uint8_t(b)) < 0 && (kSingle[b] != 0 || b == 0))
                map.emplace(kSingle[b], uint16_t(b));
        for (int pass = 0; pass < 2; pass++)
            for (int lead = 0x81; lead <= 0xFC; lead++)
            {
                int index = LeadIndex(uint8_t(lead));
                if (index < 0 || (pass == 0) == (lead == 0xED || lead == 0xEE))
                    continue;
                for (int trail = 0x40; trail <= 0xFC; trail++)
                    if (uint16_t c = kDouble[index][trail - 0x40]; c != 0)
                        map.emplace(c, uint16_t(lead << 8 | trail));
            }
        return map;
    }();
    return codes;
}

} // namespace

int Cp932Code(char32_t c)
{
    auto found = Cp932Codes().find(uint32_t(c));
    return found == Cp932Codes().end() ? -1 : found->second;
}

std::string Utf8ToCp932(std::string_view utf8)
{
    const auto& codes = Cp932Codes();
    std::string out;
    out.reserve(utf8.size());
    for (size_t i = 0; i < utf8.size();)
    {
        uint8_t b = uint8_t(utf8[i]);
        uint32_t c;
        int length = b < 0x80 ? 1 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
        if (i + size_t(length) > utf8.size())
            length = 1;
        switch (length)
        {
        case 2: c = (b & 0x1Fu) << 6 | (uint8_t(utf8[i + 1]) & 0x3Fu); break;
        case 3: c = (b & 0x0Fu) << 12 | (uint8_t(utf8[i + 1]) & 0x3Fu) << 6 | (uint8_t(utf8[i + 2]) & 0x3Fu); break;
        case 4: c = 0x110000; break; // outside the tables
        default: c = b < 0x80 ? b : 0x110000; break;
        }
        i += size_t(length);
        auto found = codes.find(c);
        if (found == codes.end())
            out += '?';
        else if (found->second > 0xFF)
        {
            out += char(found->second >> 8);
            out += char(found->second & 0xFF);
        }
        else
            out += char(found->second);
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
