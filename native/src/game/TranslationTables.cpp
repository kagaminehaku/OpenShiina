// C# Game/ScenarioText.cs (see TranslationTables.h)
#include "game/TranslationTables.h"

#include <charconv>

#include "text/Text.h"
#include "text/Unicode.h"

namespace openshiina::game {

namespace {

/// char.IsWhiteSpace
bool IsWhiteSpace(char32_t c)
{
    return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029
        || c == 0x202F || c == 0x205F || c == 0x3000;
}

/// The text has something but white space (string.Trim().Length > 0).
bool HasText(const std::string& utf8)
{
    for (char32_t c : Text::Utf8ToCodePoints(utf8))
        if (!IsWhiteSpace(c))
            return true;
    return false;
}

/// The first character after white space, or 0.
char32_t FirstNonSpace(const std::string& utf8)
{
    for (char32_t c : Text::Utf8ToCodePoints(utf8))
        if (!IsWhiteSpace(c))
            return c;
    return 0;
}

std::string Upper(const std::string& name) { return Text::FoldCase(name); }

/// A line of a scenario: its text (no line end), its line end, and whether it is a message line.
struct ScenarioLine
{
    std::string Text, Ending;
    bool Message;
};

/// ScenarioText.Lines: a scenario's lines (Shift-JIS, or UTF-8 after a byte order mark).
std::vector<ScenarioLine> ScenarioLines(const Bytes& data)
{
    bool utf8 = data.size() >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
    std::string text = utf8 ? Text::CodePointsToUtf8(Text::Utf8ToCodePoints(std::string_view(reinterpret_cast<const char*>(data.data()) + 3, data.size() - 3)))
                            : Text::Cp932ToUtf8(data.data(), data.size());
    std::vector<ScenarioLine> lines;
    for (size_t start = 0; start < text.size();)
    {
        size_t end = text.find('\n', start);
        end = end == std::string::npos ? text.size() : end + 1;
        size_t textEnd = end;
        while (textEnd > start && (text[textEnd - 1] == '\r' || text[textEnd - 1] == '\n'))
            textEnd--;
        std::string line = text.substr(start, textEnd - start);
        char32_t first = FirstNonSpace(line);
        bool message = first != 0 && first != U';' && first != U'$' && first != U'【';
        lines.push_back({line, text.substr(textEnd, end - textEnd), message});
        start = end;
    }
    return lines;
}

std::string Utf16ToUtf8(const uint8_t* p, size_t length, bool bigEndian)
{
    std::u32string out;
    for (size_t i = 0; i + 1 < length; i += 2)
    {
        char32_t u = bigEndian ? char32_t(p[i] << 8 | p[i + 1]) : char32_t(p[i] | p[i + 1] << 8);
        if (u >= 0xD800 && u <= 0xDBFF && i + 3 < length)
        {
            char32_t v = bigEndian ? char32_t(p[i + 2] << 8 | p[i + 3]) : char32_t(p[i + 2] | p[i + 3] << 8);
            if (v >= 0xDC00 && v <= 0xDFFF)
            {
                out += 0x10000 + ((u - 0xD800) << 10) + (v - 0xDC00);
                i += 2;
                continue;
            }
        }
        out += u >= 0xD800 && u <= 0xDFFF ? char32_t(0xFFFD) : u;
    }
    if (length % 2 != 0)
        out += char32_t(0xFFFD);
    return Text::CodePointsToUtf8(out);
}

} // namespace

namespace Tsv {

std::string Clean(const std::string& text)
{
    std::string out;
    out.reserve(text.size());
    for (size_t i = 0; i < text.size(); i++)
    {
        if (text[i] == '\r' && i + 1 < text.size() && text[i + 1] == '\n')
        {
            out += ' ';
            i++;
        }
        else
            out += text[i] == '\t' || text[i] == '\r' || text[i] == '\n' ? ' ' : text[i];
    }
    return out;
}

std::string Decode(const Bytes& file)
{
    // StreamReader's byte order marks: UTF-8, UTF-16 LE / BE, UTF-32 LE (then UTF-8 without one)
    const uint8_t* p = file.data();
    size_t n = file.size();
    if (n >= 4 && p[0] == 0xFF && p[1] == 0xFE && p[2] == 0 && p[3] == 0)
    {
        std::u32string out;
        for (size_t i = 4; i + 3 < n; i += 4)
            out += char32_t(LittleEndian::ToUInt32(p + i));
        return Text::CodePointsToUtf8(out);
    }
    if (n >= 2 && p[0] == 0xFF && p[1] == 0xFE)
        return Utf16ToUtf8(p + 2, n - 2, false);
    if (n >= 2 && p[0] == 0xFE && p[1] == 0xFF)
        return Utf16ToUtf8(p + 2, n - 2, true);
    size_t skip = n >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF ? 3 : 0;
    return Text::CodePointsToUtf8(Text::Utf8ToCodePoints(std::string_view(reinterpret_cast<const char*>(p) + skip, n - skip)));
}

std::vector<std::vector<std::string>> Read(const std::string& text, size_t fields)
{
    std::vector<std::vector<std::string>> rows;
    std::vector<std::string> row;
    bool header = true;
    auto endRow = [&] {
        if (header)
            header = false;
        else if (row.size() > 1 || !row[0].empty())
        {
            while (row.size() < fields)
                row.emplace_back();
            rows.push_back(row);
        }
        row.clear();
    };
    size_t i = 0;
    while (i <= text.size())
    {
        // A field: quoted when it starts with a quote and the closing one ends it
        std::string field;
        size_t end = std::string::npos;
        if (i < text.size() && text[i] == '"')
        {
            size_t j = i + 1;
            std::string quoted;
            while (j < text.size())
            {
                if (text[j] == '"')
                {
                    if (j + 1 < text.size() && text[j + 1] == '"')
                    {
                        quoted += '"';
                        j += 2;
                        continue;
                    }
                    break;
                }
                quoted += text[j++];
            }
            if (j < text.size() && (j + 1 == text.size() || text[j + 1] == '\t' || text[j + 1] == '\r' || text[j + 1] == '\n'))
            {
                field = quoted;
                end = j + 1;
            }
        }
        if (end == std::string::npos)
        {
            end = i;
            while (end < text.size() && text[end] != '\t' && text[end] != '\r' && text[end] != '\n')
                end++;
            field = text.substr(i, end - i);
        }
        row.push_back(field);
        if (end >= text.size())
        {
            endRow();
            break;
        }
        if (text[end] == '\t')
        {
            i = end + 1;
            continue;
        }
        i = end + (text[end] == '\r' && end + 1 < text.size() && text[end + 1] == '\n' ? 2 : 1);
        endRow();
        if (i == text.size())
            break;
    }
    return rows;
}

} // namespace Tsv

TranslationTables TranslationTables::Read(const std::function<std::optional<Bytes>(const std::string&)>& file)
{
    TranslationTables tables;
    if (auto text = file(TextTable))
        for (auto& row : Tsv::Read(Tsv::Decode(*text), 4))
        {
            size_t colon = row[0].rfind(':');
            if (colon == std::string::npos || colon == 0)
                continue;
            // int.TryParse: digits with an optional sign and white space around
            std::string number = row[0].substr(colon + 1);
            size_t a = number.find_first_not_of(" \t\r\n"), b = number.find_last_not_of(" \t\r\n");
            if (a == std::string::npos)
                continue;
            number = number.substr(a, b - a + 1);
            if (!number.empty() && number[0] == '+')
                number = number.substr(1);
            int line = 0;
            auto [ptr, ec] = std::from_chars(number.data(), number.data() + number.size(), line);
            if (ec != std::errc() || ptr != number.data() + number.size())
                continue;
            tables.Lines.push_back({row[0].substr(0, colon), line, row[1], row[2], row[3]});
        }
    auto strings = [](const Bytes& data, std::vector<StringRow>& into) {
        for (auto& row : Tsv::Read(Tsv::Decode(data), 2))
            if (!row[0].empty() && row[0][0] != '#')
                into.push_back({row[0], row[1]});
    };
    if (auto names = file(NameTable))
        strings(*names, tables.Names);
    if (auto list = file(StringTable))
        strings(*list, tables.Strings);
    return tables;
}

std::optional<Bytes> TranslationTables::Apply(const std::string& fileName, const Bytes& scenario)
{
    if (!m_byFile)
    {
        m_byFile.emplace();
        for (const TextRow& l : Lines)
            if (HasText(l.Translation))
                (*m_byFile)[Upper(l.File)].emplace(l.Line, &l);
    }
    auto found = m_byFile->find(Upper(fileName));
    if (found == m_byFile->end())
        return std::nullopt;
    const auto& rows = found->second;
    auto lines = ScenarioLines(scenario);
    std::string output = "\xEF\xBB\xBF";
    int done = 0;
    for (size_t i = 0; i < lines.size(); i++)
    {
        const ScenarioLine& line = lines[i];
        std::string text = line.Text;
        if (auto row = rows.find(int(i) + 1); row != rows.end() && line.Message && Tsv::Clean(line.Text) == row->second->Original)
        {
            text = Tsv::Clean(row->second->Translation);
            done++;
        }
        output += text;
        output += line.Ending;
    }
    if (done == 0)
        return std::nullopt;
    return Bytes(output.begin(), output.end());
}

std::vector<std::pair<std::string, std::string>> TranslationTables::TranslatedStrings() const
{
    std::vector<std::pair<std::string, std::string>> list;
    for (const auto* table : {&Names, &Strings})
        for (const StringRow& s : *table)
            if (!s.Original.empty() && HasText(s.Translation))
                list.emplace_back(Tsv::Clean(s.Original), Tsv::Clean(s.Translation));
    return list;
}

} // namespace openshiina::game
