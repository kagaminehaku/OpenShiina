// The Switch player's own words in the language of the setting "Language" (C# Localization.Languages):
// English as the code writes it, the source, and Vietnamese from the table in Strings.cpp, its words
// the C# player's Strings.vi.resx where it has them. A word the table lacks stays English. The
// console's own language ("") is English: the Switch has no Vietnamese.
#pragma once

#include <format>
#include <string>

namespace openshiina::player {

/// The language of the screens: "" (the console's), "en" or "vi".
void SetLanguage(const std::string& code);

/// The screens are in Vietnamese.
bool Vietnamese();

/// The words in the screens' language.
const char* Tr(const char* english);

/// Every Vietnamese word of the table, one after another (to tell whether the fonts draw them).
std::string VietnameseWords();

/// The words in the screens' language with the arguments put in ({} as std::format).
template <class... Args>
std::string Trf(const char* english, const Args&... args)
{
    return std::vformat(Tr(english), std::make_format_args(args...));
}

/// The choices of the setting "Language": the value in settings.json, the name shown.
struct LanguageChoice
{
    const char* Code;
    const char* Name;
};
extern const LanguageChoice kLanguageChoices[3];

} // namespace openshiina::player
