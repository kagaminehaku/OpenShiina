// Text the games keep as Shift-JIS (code page 932) as UTF-8, and the comparisons of names
// without regard to case that the C# player makes with StringComparer.OrdinalIgnoreCase.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace openshiina::Text {

/// Code page 932 bytes as UTF-8 (a byte pair with no character becomes "?").
std::string Cp932ToUtf8(const uint8_t* data, size_t length);
inline std::string Cp932ToUtf8(std::string_view raw) { return Cp932ToUtf8(reinterpret_cast<const uint8_t*>(raw.data()), raw.size()); }

/// UTF-8 as code page 932, as .NET's encoder gives it: a character with two codes gets the one
/// outside NEC's selection of IBM's characters (lead bytes ED / EE), one with none "?".
std::string Utf8ToCp932(std::string_view utf8);

/// The code page 932 code of a character as .NET's encoder gives it (a byte, or lead * 256 + trail),
/// or -1 when it has none.
int Cp932Code(char32_t c);

/// The text up to its first 0 byte (or all of it), from code page 932.
std::string CString(const uint8_t* data, size_t length);

/// The text with the letters A-Z, a-z and their full-width forms folded to upper case.
std::string FoldCase(std::string_view utf8);

bool EqualsIgnoreCase(std::string_view a, std::string_view b);
bool ContainsIgnoreCase(std::string_view text, std::string_view part);
bool LessIgnoreCase(std::string_view a, std::string_view b);

} // namespace openshiina::Text
