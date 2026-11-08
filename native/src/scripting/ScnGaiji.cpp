// C# Scripting/ScnGaiji.cs (see ScnGaiji.h)
#include "scripting/ScnGaiji.h"

#include <optional>

#include "text/Text.h"
#include "text/Unicode.h"

namespace openshiina::scripting {

namespace {

// Lead bytes Shift-JIS (cp932) leaves free; trail bytes 40-7E and 80-FC (188 a lead)
constexpr uint8_t kLeads[] = {0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0x85, 0x86, 0xEB, 0xEC, 0xEF};

/// A text element in Shift-JIS: its bytes, or none when Shift-JIS has no such character (or they
/// would be more than four bytes).
std::optional<Bytes> Encode(const std::u32string& element)
{
    Bytes bytes;
    for (char32_t c : element)
    {
        int code = Text::Cp932Code(c);
        if (code < 0)
            return std::nullopt;
        if (code > 0xFF)
            bytes.push_back(uint8_t(code >> 8));
        bytes.push_back(uint8_t(code));
    }
    if (bytes.size() > 4 || bytes.empty())
        return std::nullopt;
    return bytes;
}

/// char.IsWhiteSpace
bool IsWhiteSpace(char32_t c)
{
    return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029
        || c == 0x202F || c == 0x205F || c == 0x3000;
}

/// string.Trim
std::u32string Trim(const std::u32string& s)
{
    size_t a = 0, b = s.size();
    while (a < b && IsWhiteSpace(s[a]))
        a++;
    while (b > a && IsWhiteSpace(s[b - 1]))
        b--;
    return s.substr(a, b - a);
}

} // namespace

ScnGaiji::ScnGaiji()
{
    m_end = CodeFor("");
}

ScnGaiji::Kind ScnGaiji::KindOf(int code) const
{
    auto found = m_texts.find(code);
    if (found == m_texts.end())
        return Kind::None;
    return found->second.empty() ? Kind::End : found->second == " " ? Kind::Space : Kind::Letters;
}

int ScnGaiji::CodeFor(const std::string& text)
{
    if (auto found = m_codes.find(text); found != m_codes.end())
        return found->second;
    int n = int(m_codes.size());
    if (n >= Codes)
        return -1;
    int trail = n % 188;
    int code = kLeads[n / 188] << 8 | (trail < 63 ? 0x40 + trail : 0x80 + trail - 63);
    m_codes[text] = code;
    m_texts[code] = text;
    return code;
}

Bytes ScnGaiji::Scenario(const Bytes& utf8)
{
    std::u32string text = Text::Nfc(Text::Utf8ToCodePoints(std::string_view(reinterpret_cast<const char*>(utf8.data()) + 3, utf8.size() - 3)));
    Bytes output;
    output.reserve(text.size() * 2);
    size_t start = 0;
    while (start < text.size())
    {
        size_t end = text.find(U'\n', start);
        end = end == std::u32string::npos ? text.size() : end + 1;
        std::u32string line = text.substr(start, end - start);
        std::u32string body = Trim(line);
        size_t lineEnd = line.size();
        while (lineEnd > 0 && (line[lineEnd - 1] == U'\r' || line[lineEnd - 1] == U'\n'))
            lineEnd--;
        if (!body.empty() && body[0] != U';' && body[0] != U'$' && body[0] != U'【')
            Message(line.substr(0, lineEnd), output, true);
        else if (!body.empty() && body[0] == U'【')
            Message(line.substr(0, lineEnd), output, false);
        else
            Plain(line.substr(0, lineEnd), output);
        for (size_t k = lineEnd; k < line.size(); k++)
            output.push_back(uint8_t(line[k]));
        start = end;
    }
    return output;
}

Bytes ScnGaiji::Text(const std::string& text)
{
    Bytes output;
    Message(Text::Nfc(Text::Utf8ToCodePoints(text)), output, true);
    return output;
}

void ScnGaiji::Plain(const std::u32string& line, Bytes& output)
{
    // A comment, command or speaker line: Shift-JIS, ASCII as it is, gaiji for what Shift-JIS lacks
    for (const std::u32string& element : Text::TextElements(line))
    {
        if (element.size() == 1 && element[0] < 0x80)
            output.push_back(uint8_t(element[0]));
        else if (auto bytes = Encode(element))
            output.insert(output.end(), bytes->begin(), bytes->end());
        else
            AddCode(Text::CodePointsToUtf8(element), output);
    }
}

void ScnGaiji::Message(const std::u32string& line, Bytes& output, bool end)
{
    // A message line (or a speaker's): what Shift-JIS has as it is (but ASCII), the rest as gaiji: a
    // space a code, the letters of a word two a code; End after a message
    std::string pending;
    bool hasPending = false;
    auto flush = [&] {
        if (hasPending)
            AddCode(pending, output);
        hasPending = false;
    };
    for (const std::u32string& e : Text::TextElements(line))
    {
        bool ascii = e.size() == 1 && e[0] < 0x80;
        if (!ascii)
            if (auto bytes = Encode(e))
            {
                flush();
                output.insert(output.end(), bytes->begin(), bytes->end());
                continue;
            }
        if (e == U" " || e == U"\t")
        {
            flush();
            AddCode(" ", output);
            continue;
        }
        std::string element = Text::CodePointsToUtf8(e);
        if (hasPending && PairsLeft(pending + element))
        {
            AddCode(pending + element, output);
            hasPending = false;
        }
        else
        {
            flush();
            pending = element;
            hasPending = true;
        }
    }
    flush();
    if (end)
        AddCode("", output);
}

void ScnGaiji::AddCode(const std::string& text, Bytes& output)
{
    int code = CodeFor(text);
    if (code < 0)
    {
        m_missing++;
        output.push_back(0x81); // no code left: a full-width "?"
        output.push_back(0x48);
        return;
    }
    output.push_back(uint8_t(code >> 8));
    output.push_back(uint8_t(code));
}

} // namespace openshiina::scripting
