// Message text laid out the way the engine's text system does (START.SCN, style bank 0):
// ＭＳ ゴシック 24 px, fixed advances of 23 px (full width) and 11 px (half width), 29 px
// lines, line breaks with the engine's kinsoku table, and gaiji characters (① ...) drawn from
// GAIJI.S25. The front end draws the characters at these positions.

namespace OpenShiina.Story;

public static class MessageLayout
{
    public const int FullAdvance = 23, HalfAdvance = 11, LineHeight = 29, FontHeight = 24;

    // Kinsoku table of START.SCN ("_P..."): characters that may not start a line, and that may not end one
    private const string NoLineStart = "。，、．：；゛゜ヽヾゝゞ々）〕］｝〉》」』】°′″℃￠％‰”―　・ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ";
    private const string NoLineEnd = "（〔［｛〈《「『【￥＄￡";

    /// <summary>Half-width characters (ASCII and half-width kana) take one byte in Shift-JIS and 11 px on screen.</summary>
    public static bool IsHalfWidth(char c) => c < 0x80 || (c >= '｡' && c <= 'ﾟ');

    /// <summary>Frame of GAIJI.S25 for a gaiji character (① = 0, ...), or -1.</summary>
    public static int GaijiIndex(char c) => c >= '①' && c <= '⑳' ? c - '①' : -1;

    /// <summary>Top-left corner of every character, wrapping at <paramref name="width"/>.</summary>
    public static List<(double X, double Y)> Layout(string text, double width)
    {
        var positions = new List<(double X, double Y)>(text.Length);
        double x = 0, y = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                positions.Add((x, y));
                x = 0;
                y += LineHeight;
                continue;
            }
            int advance = IsHalfWidth(c) ? HalfAdvance : FullAdvance;
            bool overflows = x > 0 && x + advance > width;
            // An opening bracket that would be the last character of the line moves to the next one
            bool lonelyOpener = x > 0 && NoLineEnd.Contains(c) && i + 1 < text.Length &&
                                x + advance + (IsHalfWidth(text[i + 1]) ? HalfAdvance : FullAdvance) > width;
            if ((overflows && !NoLineStart.Contains(c)) || lonelyOpener)
            {
                x = 0;
                y += LineHeight;
            }
            positions.Add((x, y));
            x += advance;
        }
        return positions;
    }

    /// <summary>How many lines the text takes at <paramref name="width"/>.</summary>
    public static int LineCount(string text, double width)
    {
        var positions = Layout(text, width);
        return positions.Count == 0 ? 0 : (int)(positions[^1].Y / LineHeight) + 1;
    }
}
