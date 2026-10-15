// IScnFonts with Windows GDI, the font engine the game itself uses, so text comes out with the
// same glyphs. One memory device context does all the work; the game's "A" calls are made with
// the matching "W" calls after decoding the Shift-JIS text, as GDI does for a SHIFTJIS_CHARSET font.
// The gaiji of translated scenarios (ScnGaiji) are measured and drawn with a Latin face of the
// font's height (Segoe UI), which has every Vietnamese letter; text without them is as before.
// Fonts a game adds from its archives go to GDI as the game gives them (AddFontMemResourceEx).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OpenShiina.Platform;

[SupportedOSPlatform("windows")]
public sealed class GdiFonts : IScnFonts, IDisposable
{
    private static readonly Encoding s_sjis;
    private readonly IntPtr m_dc;
    private readonly IntPtr m_systemFont;
    private readonly Dictionary<int, IntPtr> m_fonts = new();
    private readonly Dictionary<int, ScnFontRequest> m_requests = new();
    // The Latin font of each font, for gaiji (made when first needed)
    private readonly Dictionary<int, IntPtr> m_latin = new();
    private int m_next = 1;

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, string>? Gaiji { get; set; }

    static GdiFonts()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        s_sjis = Encoding.GetEncoding(932);
    }

    public GdiFonts()
    {
        m_dc = CreateCompatibleDC(IntPtr.Zero);
        m_systemFont = GetStockObject(SystemFont);
    }

    public int CreateFont(in ScnFontRequest r)
    {
        var font = CreateFontW(r.Height, r.Width, r.Escapement, r.Orientation, r.Weight,
            (uint)r.Italic, (uint)r.Underline, (uint)r.StrikeOut, (uint)r.CharSet, 0, 0,
            (uint)r.Quality, (uint)r.PitchAndFamily, r.Face);
        if (font == IntPtr.Zero)
            return 0;
        int id = m_next++;
        m_fonts[id] = font;
        m_requests[id] = r;
        return id;
    }

    public int AddMemoryFonts(ReadOnlySpan<byte> file)
    {
        // Private to this process, gone with it
        uint count = 0;
        return AddFontMemResourceEx(file.ToArray(), (uint)file.Length, IntPtr.Zero, ref count) != IntPtr.Zero ? (int)count : 0;
    }

    public void DeleteFont(int font)
    {
        if (m_fonts.Remove(font, out var handle))
            DeleteObject(handle);
        m_requests.Remove(font);
        if (m_latin.Remove(font, out var latin))
            DeleteObject(latin);
    }

    /// <summary>The Latin font for the gaiji drawn with <paramref name="font"/>: Segoe UI of its height and weight.</summary>
    private IntPtr Latin(int font)
    {
        if (m_latin.TryGetValue(font, out var handle))
            return handle;
        var r = font != 0 && m_requests.TryGetValue(font, out var made) ? made : new ScnFontRequest(16, 0, 0, 0, 700, 0, 0, 0, 0x80, 0, 0, "System");
        handle = CreateFontW(r.Height, 0, r.Escapement, r.Orientation, r.Weight, (uint)r.Italic, (uint)r.Underline,
            (uint)r.StrikeOut, DefaultCharSet, 0, 0, (uint)r.Quality, 0, "Segoe UI");
        m_latin[font] = handle;
        return handle;
    }

    private void Select(int font) =>
        SelectObject(m_dc, font != 0 && m_fonts.TryGetValue(font, out var handle) ? handle : m_systemFont);

    public (int Width, int Height) TextExtent(int font, ReadOnlySpan<byte> text)
    {
        Select(font);
        string s = ScnGaiji.Decode(s_sjis, text, Gaiji, out var gaiji);
        if (gaiji == null)
            return GetTextExtentPoint32W(m_dc, s, s.Length, out var size) ? (size.Cx, size.Cy) : (0, 0);
        // Runs of the font's characters and of gaiji, the height the font's
        int width = 0, height = GetTextMetricsW(m_dc, out var tm) ? tm.Height : 0;
        for (int i = 0; i < s.Length; )
        {
            int end = i + 1;
            while (end < s.Length && gaiji[end] == gaiji[i])
                end++;
            if (gaiji[i])
                SelectObject(m_dc, Latin(font));
            else
                Select(font);
            if (GetTextExtentPoint32W(m_dc, s[i..end], end - i, out var run))
                width += run.Cx;
            i = end;
        }
        return (width, height);
    }

    public int Ascent(int font)
    {
        Select(font);
        return GetTextMetricsW(m_dc, out var tm) ? tm.Ascent : 0;
    }

    public ScnGlyph Glyph(int font, int code)
    {
        if (Gaiji != null && Gaiji.TryGetValue(code, out var text))
        {
            // a translation's letters (none, one or two) with the Latin font
            SelectObject(m_dc, Latin(font));
            return text.Length == 1 ? GlyphOf(text[0]) : ScnGlyph.Compose(text.Select(GlyphOf).ToList());
        }
        Select(font);
        byte[] bytes = code > 0xFF ? [(byte)(code >> 8), (byte)code] : [(byte)code];
        string s = s_sjis.GetString(bytes);
        return GlyphOf(s.Length > 0 ? s[0] : '?');
    }

    /// <summary>GetGlyphOutlineW of a character in the selected font.</summary>
    private ScnGlyph GlyphOf(char ch)
    {
        var matrix = new Mat2 { M11 = 1 << 16, M22 = 1 << 16 };
        uint size = GetGlyphOutlineW(m_dc, ch, GgoGray8Bitmap, out var gm, 0, null, ref matrix);
        int pitch = (int)(gm.BlackBoxX + 3 & ~3u);
        if (size == 0 || size == uint.MaxValue)
            size = (uint)(gm.BlackBoxY * pitch);
        var bits = new byte[size];
        if (size > 0)
            GetGlyphOutlineW(m_dc, ch, GgoGray8Bitmap, out gm, size, bits, ref matrix);
        return new ScnGlyph((int)gm.BlackBoxX, (int)gm.BlackBoxY, gm.OriginX, gm.OriginY, gm.CellIncX, gm.CellIncY, pitch, bits);
    }

    public IReadOnlyList<byte[]> FixedPitchJapaneseFaces()
    {
        // EnumFontFamiliesExW and the names in Shift-JIS: the "A" call would give them in the
        // system's code page, which need not be Japanese
        var faces = new List<byte[]>();
        var logFont = new byte[92];
        logFont[0x17] = ShiftJisCharSet;
        EnumFontProc found = (lf, tm, type, _) =>
        {
            // lfCharSet, tmPitchAndFamily & TMPF_TRUETYPE, lfPitchAndFamily & 3 == FIXED_PITCH, not "@..."
            string name = Marshal.PtrToStringUni(lf + 0x1C) ?? "";
            if (Marshal.ReadByte(lf, 0x17) == ShiftJisCharSet && (Marshal.ReadByte(tm, 0x37) & 4) != 0
                && (Marshal.ReadByte(lf, 0x1B) & 3) == 1 && !name.StartsWith('@'))
            {
                var bytes = s_sjis.GetBytes(name);
                faces.Add(bytes.Length > 31 ? bytes[..31] : bytes);
            }
            return 1;
        };
        EnumFontFamiliesExW(m_dc, logFont, found, IntPtr.Zero, 0);
        GC.KeepAlive(found);
        return faces;
    }

    public void Dispose()
    {
        foreach (var handle in m_fonts.Values.Concat(m_latin.Values))
            DeleteObject(handle);
        m_fonts.Clear();
        m_latin.Clear();
        DeleteDC(m_dc);
    }

    private const int SystemFont = 13;
    private const byte ShiftJisCharSet = 0x80;
    private const uint DefaultCharSet = 1;

    private delegate int EnumFontProc(IntPtr logFont, IntPtr textMetric, uint fontType, IntPtr parameter);

    [DllImport("gdi32.dll")]
    private static extern int EnumFontFamiliesExW(IntPtr dc, byte[] logFont, EnumFontProc proc, IntPtr parameter, uint flags);
    private const uint GgoGray8Bitmap = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TextMetric
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AveCharWidth, MaxCharWidth;
        public int Weight, Overhang, DigitizedAspectX, DigitizedAspectY;
        public char FirstChar, LastChar, DefaultChar, BreakChar;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, CharacterSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public uint BlackBoxX, BlackBoxY;
        public int OriginX, OriginY;
        public short CellIncX, CellIncY;
    }

    // MAT2: four FIXED values (16.16), given here as whole ints
    [StructLayout(LayoutKind.Sequential)]
    private struct Mat2 { public int M11, M12, M21, M22; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr AddFontMemResourceEx(byte[] font, uint size, IntPtr reserved, ref uint count);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int count, out Size size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextMetricsW(IntPtr dc, out TextMetric metric);

    [DllImport("gdi32.dll")]
    private static extern uint GetGlyphOutlineW(IntPtr dc, uint ch, uint format, out GlyphMetrics metrics,
        uint size, byte[]? buffer, ref Mat2 matrix);
}
