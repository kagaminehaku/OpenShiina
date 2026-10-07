// IScnFonts with SkiaSharp (which Avalonia draws with on every platform), in place of Windows GDI:
// CreateFontA, GetTextExtentPoint32A, GetTextMetricsA and GetGlyphOutlineA with GGO_GRAY8_BITMAP.
// The glyph shapes come from the system's fonts, so text is close to the game's but not pixel for
// pixel. A face the system lacks (the games ask for ＭＳ ゴシック) is replaced by a Japanese font
// that is there - a fixed-pitch one for a gothic, a serif one for a mincho - sized and measured as
// MS Gothic is (its cell is its em, ascent 0.859 em, average width half an em), since the games
// lay their text out for it; a character a face lacks comes from a font that has it.
//
// GDI's terms, as the engine uses them: a font's height > 0 is the cell height (ascent + descent),
// < 0 the em height; a width other than 0 stretches the font to that average character width. A
// glyph is its black box (the smallest rectangle around its ink) in 65 levels of coverage (0-64),
// rows padded to 4 bytes, placed by its origin (left of the box from the pen, top of the box above
// the baseline) and followed by its advance.

using System.Text;
using OpenShiina.IO;
using OpenShiina.Scripting;
using SkiaSharp;

namespace OpenShiina.App;

public sealed class SkiaFonts : IScnFonts, IDisposable
{
    // Japanese faces to use when the one asked for is missing, by how close they are to MS Gothic
    // (fixed pitch first) and to MS Mincho
    private static readonly string[] s_gothic =
    [
        "MS Gothic", "ＭＳ ゴシック", "Noto Sans Mono CJK JP", "Source Han Mono", "IPAGothic", "TakaoGothic", "VL Gothic",
        "Osaka－等幅", "Osaka-Mono", "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP", "IPAexGothic", "Hiragino Sans",
        "Hiragino Kaku Gothic ProN", "Yu Gothic", "Meiryo", "Droid Sans Japanese",
    ];
    private static readonly string[] s_mincho =
    [
        "MS Mincho", "ＭＳ 明朝", "IPAMincho", "TakaoMincho", "Noto Serif CJK JP", "Noto Serif JP", "Source Han Serif JP",
        "IPAexMincho", "Hiragino Mincho ProN", "Yu Mincho",
    ];

    // MS Gothic's measures, in ems
    private const float StandInAscent = 0.859f, StandInAverageWidth = 0.5f;

    private readonly Encoding m_sjis = Encodings.cp932;
    private readonly Dictionary<int, Font> m_fonts = new();
    private readonly Dictionary<string, (SKTypeface, bool)> m_faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SKTypeface?> m_fallbacks = new();
    private readonly Font m_system;
    private int m_next = 1;

    private sealed class Font(SKFont skia, int ascent, int descent)
    {
        public readonly SKFont Skia = skia;
        public readonly int Ascent = ascent, Descent = descent;
        public readonly Dictionary<int, ScnGlyph> Glyphs = new();
    }

    public SkiaFonts()
    {
        // SYSTEM_FONT, the font of a new device context: about 16 pixels high, bold
        m_system = Make(new ScnFontRequest(16, 0, 0, 0, 700, 0, 0, 0, 0x80, 0, 0, "System"));
    }

    public int CreateFont(in ScnFontRequest request)
    {
        int id = m_next++;
        m_fonts[id] = Make(request);
        return id;
    }

    public void DeleteFont(int font)
    {
        if (m_fonts.Remove(font, out var f))
            f.Skia.Dispose();
    }

    private Font Get(int font) => font != 0 && m_fonts.TryGetValue(font, out var f) ? f : m_system;

    private Font Make(in ScnFontRequest r)
    {
        var style = new SKFontStyle(r.Weight <= 0 ? 400 : r.Weight, (int)SKFontStyleWidth.Normal,
            r.Italic != 0 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var (face, standIn) = Face(r.Face, style);
        var font = new SKFont(face, 1) { Edging = SKFontEdging.Antialias, Subpixel = false, Hinting = SKFontHinting.Normal };
        // Height: > 0 the cell (ascent + descent), < 0 the em, 0 the default
        if (standIn)
        {
            // Measured as MS Gothic: its cell is its em
            float em = r.Height > 0 ? r.Height : r.Height < 0 ? -r.Height : 16;
            font.Size = em;
            if (r.Width > 0)
                font.ScaleX = r.Width / (em * StandInAverageWidth);
            int ascent = (int)Math.Round(em * StandInAscent), cellHeight = (int)Math.Round(em);
            return new Font(font, ascent, cellHeight - ascent);
        }
        var unit = font.Metrics;
        float cell = Math.Max(0.01f, unit.Descent - unit.Ascent);
        font.Size = r.Height > 0 ? r.Height / cell : r.Height < 0 ? -r.Height : 16;
        if (r.Width > 0 && font.Metrics.AverageCharacterWidth > 0)
            font.ScaleX = r.Width / font.Metrics.AverageCharacterWidth;
        var m = font.Metrics;
        return new Font(font, (int)Math.Round(-m.Ascent), (int)Math.Round(m.Descent));
    }

    /// <summary>The face asked for, or a stand-in for it (then measured as MS Gothic).</summary>
    private (SKTypeface Face, bool StandIn) Face(string name, SKFontStyle style)
    {
        string key = $"{name}/{style.Weight}/{style.Slant}";
        if (m_faces.TryGetValue(key, out var known))
            return known;
        var manager = SKFontManager.Default;
        // MS Gothic and MS Mincho themselves are measured by their own metrics, which are these
        if (Matching(manager, name, style) is { } own && !IsMsFace(name))
            return m_faces[key] = (own, false);
        bool serif = name.Contains("明朝") || name.Contains("Mincho", StringComparison.OrdinalIgnoreCase);
        var face = (serif ? s_mincho : s_gothic).Concat(s_gothic).Select(n => Matching(manager, n, style)).FirstOrDefault(f => f != null)
            ?? manager.MatchCharacter(null, style, ["ja"], 'あ')
            ?? SKTypeface.Default;
        return m_faces[key] = (face, true);
    }

    private static bool IsMsFace(string name) =>
        name.Replace('Ｍ', 'M').Replace('Ｓ', 'S').Replace('　', ' ').Trim() is "MS ゴシック" or "MS 明朝" or "MS Gothic" or "MS Mincho";

    /// <summary>The face of that family name, if the system has it (not a stand-in for it).</summary>
    private static SKTypeface? Matching(SKFontManager manager, string name, SKFontStyle style)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var face = manager.MatchFamily(name, style);
        return face != null && face.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase) ? face : null;
    }

    /// <summary>A face for a character the font lacks.</summary>
    private SKTypeface? FallbackFor(int ch, SKFontStyle style)
    {
        if (!m_fallbacks.TryGetValue(ch, out var face))
            m_fallbacks[ch] = face = SKFontManager.Default.MatchCharacter(null, style, ["ja"], ch);
        return face;
    }

    private string Decode(int code)
    {
        byte[] bytes = code > 0xFF ? [(byte)(code >> 8), (byte)code] : [(byte)code];
        return m_sjis.GetString(bytes);
    }

    /// <summary>The font to draw a character with, and its glyph: the font's own, or a fallback's.</summary>
    private (SKFont Font, ushort Glyph, bool Owned) GlyphFor(Font f, string s)
    {
        int ch = s.Length > 0 ? char.ConvertToUtf32(s, 0) : '?';
        ushort glyph = f.Skia.GetGlyph(ch);
        if (glyph != 0 || char.IsWhiteSpace((char)ch))
            return (f.Skia, glyph, false);
        if (FallbackFor(ch, f.Skia.Typeface.FontStyle) is { } other)
        {
            var font = new SKFont(other, f.Skia.Size, f.Skia.ScaleX) { Edging = f.Skia.Edging, Subpixel = false, Hinting = f.Skia.Hinting };
            ushort g = font.GetGlyph(ch);
            if (g != 0)
                return (font, g, true);
            font.Dispose();
        }
        return (f.Skia, glyph, false);
    }

    public (int Width, int Height) TextExtent(int font, ReadOnlySpan<byte> text)
    {
        var f = Get(font);
        string s = m_sjis.GetString(text);
        int width = 0;
        for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
        {
            var (skia, glyph, owned) = GlyphFor(f, s.Substring(i, char.IsSurrogatePair(s, i) ? 2 : 1));
            width += Advance(skia, glyph);
            if (owned)
                skia.Dispose();
        }
        return (width, f.Ascent + f.Descent);
    }

    // GDI advances are whole pixels
    private static int Advance(SKFont font, ushort glyph) => (int)Math.Round(font.GetGlyphWidths([glyph])[0]);

    public int Ascent(int font) => Get(font).Ascent;

    public ScnGlyph Glyph(int font, int code)
    {
        var f = Get(font);
        if (f.Glyphs.TryGetValue(code, out var cached))
            return cached;
        var (skia, glyph, owned) = GlyphFor(f, Decode(code));
        try
        {
            return f.Glyphs[code] = Render(skia, glyph);
        }
        finally
        {
            if (owned)
                skia.Dispose();
        }
    }

    private static ScnGlyph Render(SKFont font, ushort glyph)
    {
        short advance = (short)Advance(font, glyph);
        using var path = font.GetGlyphPath(glyph);
        var bounds = path?.Bounds ?? SKRect.Empty;
        if (path == null || bounds.IsEmpty)
            return new ScnGlyph(1, 1, 0, 0, advance, 0, 4, new byte[4]);     // a blank glyph: GDI's 1 x 1 box
        int left = (int)Math.Floor(bounds.Left), top = (int)Math.Floor(bounds.Top);
        int width = (int)Math.Ceiling(bounds.Right) - left, height = (int)Math.Ceiling(bounds.Bottom) - top;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-left, -top);
            canvas.DrawPath(path, paint);
        }
        // The black box: the rows and columns with ink
        var alpha = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;
        int x0 = width, x1 = -1, y0 = height, y1 = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (alpha[y * stride + x] != 0)
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
        if (x1 < 0)
            return new ScnGlyph(1, 1, 0, 0, advance, 0, 4, new byte[4]);
        int boxX = x1 - x0 + 1, boxY = y1 - y0 + 1, pitch = (boxX + 3) & ~3;
        var bits = new byte[pitch * boxY];
        for (int y = 0; y < boxY; y++)
            for (int x = 0; x < boxX; x++)
                bits[y * pitch + x] = (byte)((alpha[(y0 + y) * stride + x0 + x] * 64 + 127) / 255);
        // Origin: the box's left from the pen, its top above the baseline
        return new ScnGlyph(boxX, boxY, left + x0, -(top + y0), advance, 0, pitch, bits);
    }

    public IReadOnlyList<byte[]> FixedPitchJapaneseFaces()
    {
        var faces = new List<byte[]>();
        foreach (string family in SKFontManager.Default.FontFamilies.Distinct().Order())
        {
            using var face = SKFontManager.Default.MatchFamily(family);
            if (face == null || !face.IsFixedPitch || face.GetGlyph('あ') == 0 || family.StartsWith('@'))
                continue;
            var bytes = m_sjis.GetBytes(family);
            faces.Add(bytes.Length > 31 ? bytes[..31] : bytes);
        }
        return faces;
    }

    public void Dispose()
    {
        foreach (var f in m_fonts.Values)
            f.Skia.Dispose();
        m_fonts.Clear();
        m_system.Skia.Dispose();
    }
}
