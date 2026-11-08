// C# Game/ScenarioText.cs, the parts the player reads: a translation as tables, kept in the game
// folder's translation\ (OpenShiina Tools makes and edits them):
//
//   text.tsv     id, speaker, original, translation: a message line of the scenarios a row; the id
//                is the scenario file and the line ("PRO01_01.TXT:36")
//   names.tsv    original, translation: the speakers' names (inside 【】), drawn translated
//   strings.tsv  original, translation: other texts the scripts draw from their own code (choices,
//                menus), drawn translated when the whole text is the original
//
// The tables are UTF-8 (or what their byte order mark says) with tabs between the fields, a header
// row first; a field that starts and ends with a double quote is read as spreadsheets write one.
// An empty translation keeps the original. The player applies text.tsv as it reads a scenario: the
// lines whose original is the game's are replaced, and the scenario goes on as a translated one
// (UTF-8, ScnGaiji). Texts are UTF-8 here.
#pragma once

#include <functional>
#include <map>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "io/Binary.h"

namespace openshiina::game {

class TranslationTables
{
public:
    static constexpr const char* TextTable = "text.tsv";
    static constexpr const char* NameTable = "names.tsv";
    static constexpr const char* StringTable = "strings.tsv";

    struct TextRow
    {
        std::string File;
        int Line = 0;
        std::string Speaker, Original, Translation;
    };
    struct StringRow
    {
        std::string Original, Translation;
    };

    std::vector<TextRow> Lines;
    std::vector<StringRow> Names, Strings;

    /// The tables a reader gives by their names (none for a missing one).
    static TranslationTables Read(const std::function<std::optional<Bytes>(const std::string&)>& file);

    /// scenario (the Shift-JIS file of that name) with the lines text.tsv translates, as a translated
    /// scenario (UTF-8 with a byte order mark); none when it has none (or none whose original is the
    /// game's line).
    std::optional<Bytes> Apply(const std::string& fileName, const Bytes& scenario);

    /// The names and strings with a translation, names first.
    std::vector<std::pair<std::string, std::string>> TranslatedStrings() const;

private:
    // Translated lines by scenario file (upper case) and line, made on first use
    std::optional<std::map<std::string, std::map<int, const TextRow*>>> m_byFile;
};

namespace Tsv {

/// A field's text on one line: tabs and line ends as spaces.
std::string Clean(const std::string& text);
/// A table file's text as UTF-8: UTF-8, or what its byte order mark says (Excel saves UTF-16).
std::string Decode(const Bytes& file);
/// The rows of a table after its header, each with at least fields fields (missing ones empty).
std::vector<std::vector<std::string>> Read(const std::string& text, size_t fields);

} // namespace Tsv

} // namespace openshiina::game
