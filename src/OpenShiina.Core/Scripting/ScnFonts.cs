// What the text system needs from the platform's font engine. The executable draws text with
// Windows GDI: CreateFontA, GetTextExtentPoint32A, GetTextMetricsA and GetGlyphOutlineA with
// GGO_GRAY8_BITMAP (65 levels of coverage), and blends the glyphs into its 24-bit pictures itself.
// The blending is done by the interpreter, so a platform only has to supply these; on Windows
// GdiFonts gives the very same glyphs as the game, elsewhere the system's font engine comes close.

namespace OpenShiina.Scripting;

/// <summary>The arguments of CreateFontA.</summary>
public readonly record struct ScnFontRequest(
    int Height, int Width, int Escapement, int Orientation, int Weight,
    int Italic, int Underline, int StrikeOut, int CharSet, int Quality, int PitchAndFamily,
    string Face);

/// <summary>A glyph as GetGlyphOutline gives it with GGO_GRAY8_BITMAP and the identity matrix.</summary>
/// <param name="Bits">Rows of <paramref name="Pitch"/> bytes (BlackBoxX rounded up to 4), each 0-64.</param>
public sealed record ScnGlyph(int BlackBoxX, int BlackBoxY, int OriginX, int OriginY, short CellIncX, short CellIncY, int Pitch, byte[] Bits)
{
    /// <summary>GDI's glyph of no ink (a 1 x 1 box of nothing) with that advance.</summary>
    public static ScnGlyph Blank(int advance) => new(1, 1, 0, 0, (short)advance, 0, 4, new byte[4]);

    /// <summary>
    /// Glyphs side by side as one, each at the advances of those before it (a gaiji that stands
    /// for several letters, ScnGaiji): the box around their ink, the sum of their advances.
    /// </summary>
    public static ScnGlyph Compose(IReadOnlyList<ScnGlyph> glyphs)
    {
        int pen = 0, left = int.MaxValue, right = int.MinValue, top = int.MinValue, bottom = int.MaxValue;
        var at = new int[glyphs.Count];
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            at[i] = pen;
            pen += g.CellIncX;
            if (!g.Bits.AsSpan().ContainsAnyExcept((byte)0))
                continue;
            left = Math.Min(left, at[i] + g.OriginX);
            right = Math.Max(right, at[i] + g.OriginX + g.BlackBoxX);
            top = Math.Max(top, g.OriginY);
            bottom = Math.Min(bottom, g.OriginY - g.BlackBoxY);
        }
        if (right <= left || top <= bottom)
            return Blank(pen);
        int width = right - left, height = top - bottom, pitch = (width + 3) & ~3;
        var bits = new byte[pitch * height];
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            int ox = at[i] + g.OriginX - left, oy = top - g.OriginY;
            for (int y = 0; y < g.BlackBoxY; y++)
                for (int x = 0; x < g.BlackBoxX; x++)
                {
                    int from = y * g.Pitch + x;
                    if (from >= g.Bits.Length || g.Bits[from] == 0)
                        continue;
                    int to = (oy + y) * pitch + ox + x;
                    bits[to] = Math.Max(bits[to], g.Bits[from]);
                }
        }
        return new ScnGlyph(width, height, left, top, (short)pen, 0, pitch, bits);
    }
}

/// <summary>A platform font engine. Text is Shift-JIS (code page 932) as the scripts have it.</summary>
public interface IScnFonts
{
    /// <summary>A new font; returns its handle, never reused, or 0 when it cannot be made.</summary>
    int CreateFont(in ScnFontRequest request);

    /// <summary>Deletes a font made by CreateFont.</summary>
    void DeleteFont(int font);

    /// <summary>GetTextExtentPoint32; font 0 is the font a new device context has (SYSTEM_FONT).</summary>
    (int Width, int Height) TextExtent(int font, ReadOnlySpan<byte> text);

    /// <summary>tmAscent of GetTextMetrics.</summary>
    int Ascent(int font);

    /// <summary>
    /// GetGlyphOutline of a character code as the executable passes it: a single byte, or lead
    /// byte * 256 + trail byte (values above 0xFF with a lead byte of 0xFF come from sign extension).
    /// </summary>
    ScnGlyph Glyph(int font, int code);

    /// <summary>
    /// The characters the gaiji codes of translated scenarios stand for (ScnGaiji): a code of
    /// these is drawn and measured as its text, with a font that has it; null when there are none.
    /// </summary>
    IReadOnlyDictionary<int, string>? Gaiji { get; set; }

    /// <summary>
    /// The faces op_006F lists (EnumFontFamiliesExA for SHIFTJIS_CHARSET, FUN_00415630): TrueType,
    /// fixed pitch, not the vertical "@" faces; names in Shift-JIS, at most 31 bytes.
    /// </summary>
    IReadOnlyList<byte[]> FixedPitchJapaneseFaces() => [];

    /// <summary>
    /// AddFontMemResourceEx: the fonts of a TrueType / OpenType file in memory, for this font engine
    /// alone (CreateFont finds them by their family name); how many were added, 0 when none.
    /// </summary>
    int AddMemoryFonts(ReadOnlySpan<byte> file) => 0;
}
