// IScnFonts with Windows GDI, the font engine the game itself uses, so text comes out with the
// same glyphs. One memory device context does all the work; the game's "A" calls are made with
// the matching "W" calls after decoding the Shift-JIS text, as GDI does for a SHIFTJIS_CHARSET font.

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
    private int m_next = 1;

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
        return id;
    }

    public void DeleteFont(int font)
    {
        if (m_fonts.Remove(font, out var handle))
            DeleteObject(handle);
    }

    private void Select(int font) =>
        SelectObject(m_dc, font != 0 && m_fonts.TryGetValue(font, out var handle) ? handle : m_systemFont);

    public (int Width, int Height) TextExtent(int font, ReadOnlySpan<byte> text)
    {
        Select(font);
        string s = s_sjis.GetString(text);
        return GetTextExtentPoint32W(m_dc, s, s.Length, out var size) ? (size.Cx, size.Cy) : (0, 0);
    }

    public int Ascent(int font)
    {
        Select(font);
        return GetTextMetricsW(m_dc, out var tm) ? tm.Ascent : 0;
    }

    public ScnGlyph Glyph(int font, int code)
    {
        Select(font);
        byte[] bytes = code > 0xFF ? [(byte)(code >> 8), (byte)code] : [(byte)code];
        string s = s_sjis.GetString(bytes);
        uint ch = s.Length > 0 ? s[0] : '?';
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
        foreach (var handle in m_fonts.Values)
            DeleteObject(handle);
        m_fonts.Clear();
        DeleteDC(m_dc);
    }

    private const int SystemFont = 13;
    private const byte ShiftJisCharSet = 0x80;

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
