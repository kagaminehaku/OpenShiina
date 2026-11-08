// C# Scripting/ScnGaiji.cs: translated scenarios. The engine reads its scenario files (.TXT) as
// Shift-JIS and START makes half-width characters full-width, so a translation could only use what
// Shift-JIS has, and English came out spaced as full-width. A .TXT that starts with a UTF-8 byte
// order mark is turned into Shift-JIS as the scripts read it, the characters Shift-JIS lacks
// (Vietnamese letters, for one) and the Latin text of its message lines given codes Shift-JIS
// leaves free (gaiji: the user-defined area F040-F9FC and the unused lead bytes 85, 86, EB, EC,
// EF): two-byte characters to the scripts, so START leaves them alone, and drawn by the font
// engines as the text they stand for, with its own advances (IScnFonts::Gaiji).
//
// START breaks a message after a number of characters ("_r" after 33 in Bitch Nee-chan's window),
// not by their widths. In a message line a code stands for a letter, a pair of letters of one
// word, or a space, and the line ends with a code of nothing (End): the text system (ScnVm.Text)
// leaves out a "_r" that comes after a translation's letter and not after End, and breaks the line
// itself before a word that would cross the right edge.
//
// A scenario's lines: ";" a comment, "$" a command (its ASCII stays ASCII), "【name】" the speaker
// (a translated name is drawn as a message's letters; a game that looks names up, for voices or
// name plates as Oreimo Plus's NWINTBL.BIN, needs the original names kept), anything else a
// message line. Texts are UTF-8 here (UTF-16 in the C# player); normalization and text elements
// come from text/Unicode.
#pragma once

#include <map>
#include <string>
#include <unordered_map>

#include "io/Binary.h"

namespace openshiina::scripting {

class ScnGaiji
{
public:
    /// How many codes there are in all.
    static constexpr int Capacity = 15 * 188;

    ScnGaiji();

    /// The text each code given out stands for (the font engines read it).
    const std::map<int, std::string>& Texts() const { return m_texts; }
    /// How many codes have been given out.
    int Used() const { return int(m_codes.size()); }
    /// Letters or spaces that found no code left (drawn as a full-width "?").
    int Missing() const { return m_missing; }
    /// The code that ends a translated message line (it stands for nothing).
    int End() const { return m_end; }

    /// What a code stands for in a text: not a translation's, a letter (or pair), a space, or End.
    enum class Kind
    {
        None,
        Letters,
        Space,
        End
    };

    /// The kind of the two-byte character code.
    Kind KindOf(int code) const;

    /// The code for a text (a cell: up to two letters, a space, or nothing), or -1 when no code is left.
    int CodeFor(const std::string& text);

    /// A file starts with the UTF-8 byte order mark.
    static bool IsUtf8(const uint8_t* data, size_t length) { return length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF; }
    static bool IsUtf8(const Bytes& data) { return IsUtf8(data.data(), data.size()); }

    /// A translated scenario (UTF-8 with its byte order mark) as the scripts read one: Shift-JIS and gaiji.
    Bytes Scenario(const Bytes& utf8);
    /// A translated text as a message line of the scripts (Shift-JIS and gaiji, End after it), for strings.txt.
    Bytes Text(const std::string& text);

private:
    static constexpr int Codes = Capacity;
    // Codes kept back from pairs for single letters
    static constexpr int KeptForLetters = 400;

    std::unordered_map<std::string, int> m_codes;
    std::map<int, std::string> m_texts;
    int m_missing = 0;
    int m_end = 0;

    bool PairsLeft(const std::string& pair) const { return m_codes.count(pair) != 0 || int(m_codes.size()) < Codes - KeptForLetters; }
    void Plain(const std::u32string& line, Bytes& output);
    void Message(const std::u32string& line, Bytes& output, bool end);
    void AddCode(const std::string& text, Bytes& output);
};

} // namespace openshiina::scripting
