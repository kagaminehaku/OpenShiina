// Translated scenarios. The engine reads its scenario files (.TXT) as Shift-JIS and START makes
// half-width characters full-width, so a translation could only use what Shift-JIS has, and
// English came out spaced as full-width. A .TXT that starts with a UTF-8 byte order mark is
// turned into Shift-JIS as the scripts read it, the characters Shift-JIS lacks (Vietnamese
// letters, for one) and the Latin text of its message lines given codes Shift-JIS leaves free
// (gaiji: the user-defined area F040-F9FC and the unused lead bytes 85, 86, EB, EC, EF): two-byte
// characters to the scripts, so START leaves them alone, and drawn by the font engines as the
// text they stand for, with its own advances (IScnFonts.Gaiji).
//
// START breaks a message after a number of characters ("_r" after 33 in Bitch Nee-chan's
// window), not by their widths. In a message line a code stands for a letter, a pair of letters
// of one word, or a space, and the line ends with a code of nothing (End): the text system
// (ScnVm.Text) leaves out a "_r" that comes after a translation's letter and not after End, and
// breaks the line itself before a word that would cross the right edge.
//
// A scenario's lines: ";" a comment, "$" a command (its ASCII stays ASCII), "【name】" the
// speaker (a translated name is drawn as a message's letters; a game that looks names up, for
// voices or name plates as Oreimo Plus's NWINTBL.BIN, needs the original names kept), anything
// else a message line.

using System.Globalization;
using System.Text;
using OpenShiina.IO;

namespace OpenShiina.Scripting;

/// <summary>The gaiji codes of a game session: the text each stands for, given out as translated files are read.</summary>
public sealed class ScnGaiji
{
    // Lead bytes Shift-JIS (cp932) leaves free; trail bytes 40-7E and 80-FC (188 a lead)
    private static readonly byte[] s_leads = [0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0x85, 0x86, 0xEB, 0xEC, 0xEF];
    private const int Codes = 15 * 188;
    // Codes kept back from pairs for single letters
    private const int KeptForLetters = 400;

    private readonly Dictionary<string, int> m_codes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> m_texts = new();

    /// <summary>The text each code given out stands for (the font engines read it).</summary>
    public IReadOnlyDictionary<int, string> Texts => m_texts;

    /// <summary>The code that ends a translated message line (it stands for nothing).</summary>
    public int End { get; }

    public ScnGaiji()
    {
        End = CodeFor("");
    }

    /// <summary>What a code stands for in a text: not a translation's, a letter (or pair), a space, or End.</summary>
    public enum Kind { None, Letters, Space, End }

    /// <summary>The kind of the two-byte character <paramref name="code"/>.</summary>
    public Kind KindOf(int code) => !m_texts.TryGetValue(code, out var text) ? Kind.None
        : text.Length == 0 ? Kind.End
        : text == " " ? Kind.Space
        : Kind.Letters;

    /// <summary>The code for a text (a cell: up to two letters, a space, or nothing), or -1 when no code is left.</summary>
    public int CodeFor(string text)
    {
        if (m_codes.TryGetValue(text, out int code))
            return code;
        int n = m_codes.Count;
        if (n >= Codes)
            return -1;
        int trail = n % 188;
        code = s_leads[n / 188] << 8 | (trail < 63 ? 0x40 + trail : 0x80 + trail - 63);
        m_codes[text] = code;
        m_texts[code] = text;
        return code;
    }

    /// <summary>A code for a pair of letters can still be given out (else they go a letter a cell).</summary>
    private bool PairsLeft(string pair) => m_codes.ContainsKey(pair) || m_codes.Count < Codes - KeptForLetters;

    /// <summary>A file starts with the UTF-8 byte order mark.</summary>
    public static bool IsUtf8(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;

    /// <summary>A translated scenario (UTF-8 with its byte order mark) as the scripts read one: Shift-JIS and gaiji.</summary>
    public byte[] Scenario(ReadOnlySpan<byte> utf8)
    {
        string text = Encoding.UTF8.GetString(utf8[3..]).Normalize(NormalizationForm.FormC);
        var sjis = (Encoding)Encodings.cp932.Clone();
        sjis.EncoderFallback = EncoderFallback.ExceptionFallback;
        var output = new List<byte>(text.Length * 2);
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            end = end < 0 ? text.Length : end + 1;
            string line = text[start..end];
            string body = line.Trim();
            int lineEnd = line.Length;
            while (lineEnd > 0 && line[lineEnd - 1] is '\r' or '\n')
                lineEnd--;
            if (body.Length > 0 && body[0] is not (';' or '$' or '【'))
                Message(sjis, line[..lineEnd], output, end: true);
            else if (body.Length > 0 && body[0] == '【')
                Message(sjis, line[..lineEnd], output, end: false);
            else
                Plain(sjis, line[..lineEnd], output);
            foreach (char c in line[lineEnd..])
                output.Add((byte)c);
            start = end;
        }
        return output.ToArray();
    }

    /// <summary>A translated text as a message line of the scripts (Shift-JIS and gaiji, End after it), for strings.txt.</summary>
    public byte[] Text(string text)
    {
        var sjis = (Encoding)Encodings.cp932.Clone();
        sjis.EncoderFallback = EncoderFallback.ExceptionFallback;
        var output = new List<byte>(text.Length * 2);
        Message(sjis, text.Normalize(NormalizationForm.FormC), output, end: true);
        return output.ToArray();
    }

    /// <summary>A comment, command or speaker line: Shift-JIS, ASCII as it is, gaiji for what Shift-JIS lacks.</summary>
    private void Plain(Encoding sjis, string line, List<byte> output)
    {
        var bytes = new byte[4];
        var elements = StringInfo.GetTextElementEnumerator(line);
        while (elements.MoveNext())
        {
            string element = (string)elements.Current;
            if (element.Length == 1 && element[0] < 0x80)
                output.Add((byte)element[0]);
            else if (Encode(sjis, element, bytes) is int n and > 0)
                output.AddRange(bytes.AsSpan(0, n));
            else
                AddCode(element, output);
        }
    }

    /// <summary>
    /// A message line (or a speaker's): what Shift-JIS has as it is (but ASCII), the rest as
    /// gaiji: a space a code, the letters of a word two a code; End after a message.
    /// </summary>
    private void Message(Encoding sjis, string line, List<byte> output, bool end)
    {
        var bytes = new byte[4];
        string? pending = null;
        void Flush()
        {
            if (pending != null)
                AddCode(pending, output);
            pending = null;
        }
        var elements = StringInfo.GetTextElementEnumerator(line);
        while (elements.MoveNext())
        {
            string element = (string)elements.Current;
            bool ascii = element.Length == 1 && element[0] < 0x80;
            if (!ascii && Encode(sjis, element, bytes) is int n and > 0)
            {
                Flush();
                output.AddRange(bytes.AsSpan(0, n));
                continue;
            }
            if (element is " " or "\t")
            {
                Flush();
                AddCode(" ", output);
                continue;
            }
            if (pending != null && PairsLeft(pending + element))
            {
                AddCode(pending + element, output);
                pending = null;
            }
            else
            {
                Flush();
                pending = element;
            }
        }
        Flush();
        if (end)
            AddCode("", output);
    }

    private void AddCode(string text, List<byte> output)
    {
        int code = CodeFor(text);
        if (code < 0)
        {
            output.AddRange([0x81, 0x48]);    // no code left: a full-width "?"
            return;
        }
        output.Add((byte)(code >> 8));
        output.Add((byte)code);
    }

    /// <summary>A character in Shift-JIS into <paramref name="bytes"/>: its length, or 0 when Shift-JIS has no such character.</summary>
    private static int Encode(Encoding sjis, string element, byte[] bytes)
    {
        try
        {
            return sjis.GetByteCount(element) <= bytes.Length ? sjis.GetBytes(element, 0, element.Length, bytes, 0) : 0;
        }
        catch (EncoderFallbackException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Shift-JIS text as the font engines draw it, the gaiji of <paramref name="texts"/> as the
    /// text they stand for; <paramref name="gaiji"/>: which of the result's chars came from gaiji
    /// (null when none did).
    /// </summary>
    public static string Decode(Encoding sjis, ReadOnlySpan<byte> text, IReadOnlyDictionary<int, string>? texts, out bool[]? gaiji)
    {
        gaiji = null;
        if (texts == null || texts.Count <= 1 || !HasGaijiLead(text))
            return sjis.GetString(text);
        var result = new StringBuilder(text.Length);
        var from = new List<bool>(text.Length);
        int run = 0;
        void Flush(ReadOnlySpan<byte> part)
        {
            string s = sjis.GetString(part);
            result.Append(s);
            for (int k = 0; k < s.Length; k++)
                from.Add(false);
        }
        for (int i = 0; i < text.Length; )
        {
            byte b = text[i];
            bool lead = b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;
            if (lead && i + 1 < text.Length && texts.TryGetValue(b << 8 | text[i + 1], out var mapped))
            {
                Flush(text[run..i]);
                result.Append(mapped);
                for (int k = 0; k < mapped.Length; k++)
                    from.Add(true);
                i += 2;
                run = i;
                continue;
            }
            i += lead && i + 1 < text.Length ? 2 : 1;
        }
        Flush(text[run..]);
        gaiji = from.ToArray();
        return result.ToString();
    }

    private static bool HasGaijiLead(ReadOnlySpan<byte> text)
    {
        foreach (byte b in text)
            if (Array.IndexOf(s_leads, b) >= 0)
                return true;
        return false;
    }
}
