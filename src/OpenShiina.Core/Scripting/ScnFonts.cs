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
public sealed record ScnGlyph(int BlackBoxX, int BlackBoxY, int OriginX, int OriginY, short CellIncX, short CellIncY, int Pitch, byte[] Bits);

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
}
