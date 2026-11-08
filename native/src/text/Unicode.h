// What the C# player has from .NET for a translation's text (ScnGaiji): UTF-8 and code points,
// string.Normalize(NormalizationForm.FormC) and StringInfo's text elements (the extended grapheme
// clusters of UAX #29: a letter with its marks, CR LF, a Hangul syllable, an emoji sequence, a
// pair of regional indicators). Tables from tools/gen_unicode.py.
#pragma once

#include <string>
#include <string_view>
#include <vector>

namespace openshiina::Text {

/// UTF-8 as code points (a broken sequence becomes U+FFFD, as .NET's decoder makes it).
std::u32string Utf8ToCodePoints(std::string_view utf8);
std::string CodePointsToUtf8(std::u32string_view text);

/// Normalization Form C.
std::u32string Nfc(std::u32string_view text);

/// The text elements of a text, in order.
std::vector<std::u32string> TextElements(std::u32string_view text);

} // namespace openshiina::Text
