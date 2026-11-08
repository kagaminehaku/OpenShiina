// gaijicheck <folder>: the translation code (game/TranslationTables, scripting/ScnGaiji) on a folder
// of test data - text.tsv, names.tsv, strings.tsv and strings.txt, a Shift-JIS scenario.txt the
// tables translate and a translated direct.txt - printing what it makes of each piece: the tables'
// rows, the scenario with its lines translated, both as Shift-JIS and gaiji, each string's text,
// and the codes given out. The C# player's code prints the same lines for the same folder.
#include <cstdio>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

#include "game/TranslationTables.h"
#include "platform/Files.h"
#include "scripting/ScnGaiji.h"
#include "text/Unicode.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

using namespace openshiina;

namespace {

std::string Hex(const std::optional<Bytes>& b)
{
    if (!b)
        return "none";
    static const char* digits = "0123456789ABCDEF";
    std::string s;
    for (uint8_t x : *b)
    {
        s += digits[x >> 4];
        s += digits[x & 15];
    }
    return s;
}

std::string Escape(const std::string& utf8)
{
    std::string s;
    char buffer[16];
    for (char32_t c : Text::Utf8ToCodePoints(utf8))
        if (c >= 0x20 && c < 0x80)
            s += char(c);
        else
        {
            std::snprintf(buffer, sizeof buffer, "\\u%X", unsigned(c));
            s += buffer;
        }
    return s;
}

int Run(const std::string& folder)
{
    auto read = [&](const std::string& name) -> std::optional<Bytes> {
        Bytes data;
        if (!Files::ReadAll(folder + "/" + name, data))
            return std::nullopt;
        return data;
    };
    auto tables = game::TranslationTables::Read(read);
    std::printf("tables: %zu lines, %zu names, %zu strings\n", tables.Lines.size(), tables.Names.size(), tables.Strings.size());
    for (const auto& l : tables.Lines)
        std::printf("line %s:%d [%s] [%s] [%s]\n", Escape(l.File).c_str(), l.Line, Escape(l.Speaker).c_str(), Escape(l.Original).c_str(),
                    Escape(l.Translation).c_str());
    auto applied = tables.Apply("scenario.txt", *read("scenario.txt"));
    std::printf("applied: %s\n", Hex(applied).c_str());
    scripting::ScnGaiji gaiji;
    if (applied)
        std::printf("scenario: %s\n", Hex(gaiji.Scenario(*applied)).c_str());
    std::printf("direct: %s\n", Hex(gaiji.Scenario(*read("direct.txt"))).c_str());
    for (const auto& [original, translation] : tables.TranslatedStrings())
        std::printf("string [%s] %s\n", Escape(original).c_str(), Hex(gaiji.Text(translation)).c_str());
    Bytes file = *read("strings.txt");
    std::string text = Text::CodePointsToUtf8(Text::Utf8ToCodePoints(std::string_view(reinterpret_cast<const char*>(file.data()) + 3, file.size() - 3)));
    for (size_t start = 0; start <= text.size();)
    {
        size_t end = text.find('\n', start);
        if (end == std::string::npos)
            end = text.size();
        std::string raw = text.substr(start, end - start);
        while (!raw.empty() && raw.back() == '\r')
            raw.pop_back();
        if (size_t tab = raw.find('\t'); tab != std::string::npos)
            std::printf("strings.txt [%s] %s\n", Escape(raw.substr(0, tab)).c_str(), Hex(gaiji.Text(raw.substr(tab + 1))).c_str());
        start = end + 1;
    }
    for (const auto& [code, t] : gaiji.Texts())
        std::printf("code %04X [%s]\n", unsigned(code), Escape(t).c_str());
    std::printf("used %d, missing %d\n", gaiji.Used(), gaiji.Missing());
    return 0;
}

} // namespace

#ifdef _WIN32
int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
        return 2;
    return Run(Files::ToUtf8(std::filesystem::path(argv[1])));
}
#else
int main(int argc, char** argv)
{
    if (argc < 2)
        return 2;
    return Run(argv[1]);
}
#endif
