// C# Scripting/ScnOpcodes.cs
#include "scripting/ScnOpcodes.h"

#include <algorithm>
#include <map>
#include <mutex>
#include <stdexcept>
#include <utility>

#include "io/Binary.h"
#include "platform/Files.h"

namespace openshiina::scripting {

namespace {

using Items = std::vector<std::string>;

// Variable-length instructions (see LayoutItem)
const std::map<int, Items>& Overrides()
{
    static const std::map<int, Items> overrides = {
        {0x03CF, {"N2V"}},                    // local declarations
        {0x03CE, {"N2V"}},                    // global declarations
        {0x0283, {"V", "V", "N2V"}},          // callmod result, slot, arguments
        {0x0281, {"V", "V", "N2V"}},          // call a label with arguments
        {0x0280, {"N2V"}},                    // a called slot takes its arguments
        {0x0259, {"SW"}},
        {0x0263, {"SW"}},                     // gosub by index, the same table
        {0x0209, {"CASE"}},
        {0x02DB, {"VARGS"}},                  // printf-like message
        {0x01A4, {"V", "V", "V", "VARGS", "V"}}, // DLL call: -, dll, function, arguments, result
        {0x05D2, {"M1V"}},                    // movies to play, up to -1
        {0x0001, {"V", "V"}},                 // loadmod slot, file
        {0x0002, {"V", "V"}},
        {0x0213, {"b4", "b4"}},               // loop: counter, target
        {0x039C, {"V", "V"}},                 // rotate right n, v: the handler re-reads v to store it,
        {0x039D, {"V", "V"}},                 // which the table took for a third operand
    };
    return overrides;
}

// Layouts of one engine version only
const std::map<std::string, std::map<int, Items>>& VersionOverrides()
{
    static const std::map<std::string, std::map<int, Items>> overrides = {
        {"ops_v234", {{0x04BD, {"SPRITE234"}}}},
        {"ops_v233", {{0x04BD, {"SPRITE234"}}}},
        {"ops_v231", {{0x04BD, {"SPRITE234"}}}},
        {"ops_v250", {{0x009C, {"GLYPH250"}}}},
    };
    return overrides;
}

LayoutItem Item(const std::string& text)
{
    if (text == "V")
        return {LayoutItem::Operand};
    if (text == "N2V")
        return {LayoutItem::Counted};
    if (text == "SW")
        return {LayoutItem::Switch};
    if (text == "CASE")
        return {LayoutItem::Case};
    if (text == "VARGS")
        return {LayoutItem::UpToFF};
    if (text == "M1V")
        return {LayoutItem::UpToMinus1};
    if (text == "SPRITE234")
        return {LayoutItem::Sprite234};
    if (text == "GLYPH250")
        return {LayoutItem::Glyph250};
    // "b" and a byte count (int.Parse of the rest in the C# player)
    return {LayoutItem::RawBytes, std::stoi(text.substr(1))};
}

std::vector<std::string> Split(const std::string& text, char separator, bool dropEmpty)
{
    std::vector<std::string> parts;
    size_t start = 0;
    while (true)
    {
        size_t end = text.find(separator, start);
        std::string part = text.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (!dropEmpty || !part.empty())
            parts.push_back(part);
        if (end == std::string::npos)
            return parts;
        start = end + 1;
    }
}

bool StartsWith(const std::string& text, const char* prefix) { return text.rfind(prefix, 0) == 0; }

} // namespace

ScnOpcodes::ScnOpcodes(const std::string& table, const std::string& name)
{
    auto lines = Split(table, '\n', false);
    for (size_t l = 1; l < lines.size(); l++)
    {
        std::string line = lines[l];
        if (!line.empty() && line.back() == '\r')
            line.pop_back();
        auto f = Split(line, '\t', false);
        if (f.size() < 3 || f[0].size() != 4)
            continue;
        auto sig = Split(f[2], ' ', true);
        // Runs of "b8" come from a file loader that walks its own structure, not from the script
        if (std::count(sig.begin(), sig.end(), "b8") >= 3)
            sig.erase(std::remove_if(sig.begin(), sig.end(), [](const std::string& s) { return s == "b8" || s == "b"; }), sig.end());
        int op = std::stoi(f[0], nullptr, 16);
        std::vector<LayoutItem> layout;
        for (const auto& item : sig)
            layout.push_back(Item(item));
        m_layouts[size_t(op)] = std::move(layout);
        m_known[size_t(op)] = true;
    }
    auto apply = [this](const std::map<int, Items>& overrides) {
        for (const auto& [op, items] : overrides)
        {
            std::vector<LayoutItem> layout;
            for (const auto& item : items)
                layout.push_back(Item(item));
            m_layouts[size_t(op)] = std::move(layout);
            m_known[size_t(op)] = true;
        }
    };
    apply(Overrides());
    auto version = VersionOverrides().find(name);
    if (version != VersionOverrides().end())
        apply(version->second);
}

std::string ScnOpcodes::TableName(const std::string& version)
{
    return StartsWith(version, "2.47") || StartsWith(version, "2.46") || StartsWith(version, "2.45") || StartsWith(version, "2.36") ? "ops_v247"
        : StartsWith(version, "2.44") ? "ops_v244"
        : StartsWith(version, "2.34") ? "ops_v234"
        : StartsWith(version, "2.33") ? "ops_v233"
        : StartsWith(version, "2.31") ? "ops_v231"
        : StartsWith(version, "2.37") ? "ops_v237"
        : StartsWith(version, "2.50") ? "ops_v250"
        : "ops_v249";
}

const ScnOpcodes& ScnOpcodes::ForVersion(const std::string& folder, const std::string& version)
{
    static std::mutex lock;
    static std::map<std::string, std::unique_ptr<ScnOpcodes>> loaded;
    std::string name = TableName(version);
    std::lock_guard guard(lock);
    auto& table = loaded[name];
    if (!table)
    {
        openshiina::Bytes text;
        if (!Files::ReadAll(Files::Combine(folder, name + ".tsv"), text))
            throw std::runtime_error("The opcode table " + name + " is missing.");
        table.reset(new ScnOpcodes(std::string(text.begin(), text.end()), name));
    }
    return *table;
}

} // namespace openshiina::scripting
