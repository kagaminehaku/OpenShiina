// The text system: 38 text records (0x4C7D28, 0x161C bytes each) that read a string with
// control codes ("_H17_X8_c255,255,255" ...) and draw it into a surface character by character.
// Records 18-37 are scratch copies used to measure a line before it is aligned. The records are
// kept in VM memory with the executable's layout, so copies, snapshots and field writes behave
// the same. Glyphs come from the platform (IScnFonts: GetGlyphOutline GGO_GRAY8_BITMAP) and are
// blended here exactly as FUN_004325A0 does.
//
// Ported from FUN_00432F00 (one step: control codes and one character), FUN_00431960 (the "_X"
// codes), FUN_004325A0 (draw a glyph), FUN_00434BA0 / 00434580 (measure a line for alignment),
// FUN_004317F0 (numbers in codes), FUN_00431730 (set the text), FUN_004343D0 (defaults).
// Opcodes: 0084 draw a string now, 0083 draw it over frames (task flag 8), 0096 select a record,
// 00A0 / 00A1 snapshot and redraw incrementally, 0066 read a character, 0078 / 0079 position,
// 00B4 / 00B6 layer and offset.

using System.Text;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    public const int TextRecords = 38, ScratchRecords = 18;
    private const int TextRegion = 0x09000000, RecordSize = 0x161C;
    private const int SnapshotRegion = 0x09100000, SnapshotSize = 0x2C48, SnapshotLimit = 0x2C44;
    private const int MacroRegion = 0x09200000;

    // Record fields (dword indices, as the decompiled code numbers them)
    private const int RIndex = 0, RFont = 1, RState = 2, RHeight = 3, RWidth = 4, RWeight = 7, RItalic = 8,
        RStrikeOut = 10, RQuality = 0xB, RBackSurface = 0x4C, RBkMode = 0x4D, RAlpha = 0x4E, RBkAlpha = 0x4F,
        RShadowAlpha = 0x50, RShadowX = 0x54, RShadowY = 0x55, REdgeX = 0x56, REdgeY = 0x57, RPtr = 0x58,
        RLeft = 0x59, RX = 0x5A, RY = 0x5B, RPrevX = 0x5C, RPrevY = 0x5D, RPitch = 0x5E, RLineHeight = 0x5F,
        RWidePitch = 0x60, RSpacing = 0x61, RWaits = 0x62, RCharTime = 0x63, RCharDelay = 0x64,
        RWaitStart = 0x65, RWait = 0x66, RRight = 0x67, RL = 0x68, RLineStart = 0x69, RIndent = 0x6A,
        RWrapped = 0x6B, RSkipKeys = 0x6C, RKinsoku = 0x6D, RZenkaku = 0x16E, RZenkakuSpace = 0x16F,
        RKatakana = 0x170, RAfterChar = 0x171, RBeforeChar = 0x172, REffects = 0x173, RFontDirty = 0x174,
        RAntialias = 0x175, RColorDirty = 0x176, RAlign = 0x17F, RAligned = 0x180, RMeasuring = 0x181,
        RFontMade = 0x182, RChar = 0x183, RRect = 0x184, RPixels = 0x188, RSurfaceWidth = 0x189,
        RSurfaceHeight = 0x18A, RSurfacePitch = 0x18B, RTextR = 0x18C, RTextG = 0x18D, RTextB = 0x18E,
        RDepth = 0x18F, RStack = 0x190, RCount = 0x578, RCountMark = 0x579, RMeasured = 0x57A,
        RLayer = 0x57B, RLayerIndex = 0x57C, RGlyphW = 0x57D, RGlyphH = 0x57E, ROffsetX = 0x57F,
        ROffsetY = 0x580, RGlyphsDirty = 0x581, RRubyW = 0x582, RRubyH = 0x583, RFixedW = 0x584,
        RFixedH = 0x585, RContext = 0x586;

    // Byte offsets of strings and colours in a record
    private const int OFace = 0x30, OColor = 0x144, OEdgeColor = 0x147, OShadowColor = 0x14A, OBkColor = 0x14D,
        OKinsoku1 = 0x1B8, OKinsoku2 = 0x2B8, OKinsoku3 = 0x3B8, OIndents = 0x4B8;

    // Half-width -> full-width Shift-JIS (0x485504): 256 for "_k", 256 for "_K" (katakana)
    private static readonly byte[] s_zenkaku = Convert.FromBase64String(
        "QIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBSYFogZSBkIGTgZWBZoFpgWqBloF7gUOBfIFEgV6BT4JQglGCUoJTglSCVYJWgleCWIJGgUeBg4GBgYSBSIGXgWCCYYJigmOCZIJlgmaCZ4JogmmCaoJrgmyCbYJugm+CcIJxgnKCc4J0gnWCdoJ3gniCeYJtgY+BboFPgVGBZYGBgoKCg4KEgoWChoKHgoiCiYKKgouCjIKNgo6Cj4KQgpGCkoKTgpSClYKWgpeCmIKZgpqCb4FigXCBYIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUKBdYF2gUGBRYHwgp+CoYKjgqWCp4LhguOC5YLBgluBoIKigqSCpoKogqmCq4Ktgq+CsYKzgrWCt4K5gruCvYK/gsKCxILGgsiCyYLKgsuCzILNgtCC04LWgtmC3ILdgt6C34LgguKC5ILmgueC6ILpguqC64LtgvGCSoFLgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFJgWiBlIGQgZOBlYFmgWmBaoGWgXuBQ4F8gUSBXoFPglCCUYJSglOCVIJVglaCV4JYgkaBR4GDgYGBhIFIgZeBYIJhgmKCY4JkgmWCZoJngmiCaYJqgmuCbIJtgm6Cb4JwgnGCcoJzgnSCdYJ2gneCeIJ5gm2Bj4FugU+BUYFlgYGCgoKDgoSChYKGgoeCiIKJgoqCi4KMgo2CjoKPgpCCkYKSgpOClIKVgpaCl4KYgpmCmoJvgWKBcIFggUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQoF1gXaBQYFFgZKDQINCg0SDRoNIg4ODhYOHg2KDW4FBg0ODRYNHg0mDSoNMg06DUINSg1SDVoNYg1qDXINeg2CDY4Nlg2eDaYNqg2uDbINtg26DcYN0g3eDeoN9g36DgIOBg4KDhIOGg4iDiYOKg4uDjIONg4+Dk4NKgUuBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgQ==");

    // Glyphs a record has made (0x4FC550), cleared with its font (FUN_004314E0)
    private readonly Dictionary<int, ScnGlyph>?[] m_glyphs = new Dictionary<int, ScnGlyph>?[TextRecords];
    // Fonts that exist: a deleted handle stays in records and fails as GDI's would
    private readonly HashSet<int> m_liveFonts = new();
    // "_(" / "_)" saved positions (0x5E3A68 / 0x5E3AB4)
    private readonly int[] m_savedX = new int[19], m_savedY = new int[19];
    // The record "{" ruby text is laid out with (0x485500, "_u")
    private int m_rubyRecord = 16;
    // The local buffer of "_i/text/"
    private int m_indentScratch;

    /// <summary>The surface the window shows (0x13B43E4); text drawn into it invalidates the window.</summary>
    public int DisplaySurface => EngineGlobals.GetValueOrDefault(0x13B43E4);

    /// <summary>Text was drawn into the shown surface (InvalidateRect): the host should present it.</summary>
    public bool ScreenInvalidated { get; set; }

    public int RecordAddress(int index) => TextRegion + index * RecordSize;

    private int G(int rec, int field) => Read32(rec + 4 * field);
    private void S(int rec, int field, int value) => Write32(rec + 4 * field, value);

    /// <summary>FUN_004343D0: a record's defaults.</summary>
    private void ResetRecord(int index)
    {
        int rec = RecordAddress(index);
        FillMemory(rec, RecordSize, 0);
        S(rec, RIndex, index);
        S(rec, RHeight, 0x10);
        S(rec, RWeight, 400);
        WriteBytes(rec + OFace, [0x82, 0x6C, 0x82, 0x72, 0x20, 0x83, 0x53, 0x83, 0x56, 0x83, 0x62, 0x83, 0x4E, 0]); // ＭＳ ゴシック
        S(rec, RBackSurface, -1);
        S(rec, RBkMode, 1);
        S(rec, RAlpha, 0x100);
        S(rec, RBkAlpha, 0x100);
        S(rec, RShadowAlpha, 0x100);
        WriteBytes(rec + OColor, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        S(rec, RShadowX, 1);
        S(rec, RShadowY, 1);
        S(rec, REdgeX, 1);
        S(rec, REdgeY, 1);
        S(rec, RPitch, 8);
        S(rec, RWidePitch, 0x10);
        S(rec, RLineHeight, 0x10);
        S(rec, RWaits, 1);
        S(rec, RRight, 0x280);
        S(rec, RLineStart, 1);
        S(rec, RZenkaku, 1);
        S(rec, RAfterChar, -1);
        S(rec, RBeforeChar, -1);
        S(rec, RFontDirty, 1);
        S(rec, 0x178, 0xFFFF);
        for (int i = 0x179; i <= 0x17E; i++)
            S(rec, i, 0xFF);
        S(rec, RLayer, -1);
    }

    /// <summary>FUN_00431730: the record's text, optionally with a new position.</summary>
    private void SetText(int rec, int x, int y, int text)
    {
        if (x != -1)
        {
            S(rec, RX, x);
            S(rec, RLeft, x);
        }
        if (y != -1)
            S(rec, RY, y);
        S(rec, RPrevX, G(rec, RX));
        S(rec, RPtr, text);
        S(rec, RState, 1);
        S(rec, RWaits, 1);
        S(rec, RPrevY, G(rec, RY));
        S(rec, RWait, 0);
        if (ReadByte(rec + OIndents + 1) != 0)
            S(rec, RIndent, 0);
        S(rec, RMeasured, 0);
        S(rec, RCount, 0);
        S(rec, RKinsoku, 0);
        S(rec, RAligned, 0);
    }

    /// <summary>FUN_004314E0: forget the glyphs made with the record's font.</summary>
    private void ClearGlyphs(int rec) => m_glyphs[G(rec, RIndex) % TextRecords]?.Clear();

    /// <summary>DeleteObject: false for a handle that is not a live font.</summary>
    private bool DeleteFont(int font)
    {
        if (!m_liveFonts.Remove(font))
            return false;
        m_host.Fonts?.DeleteFont(font);
        return true;
    }

    /// <summary>FUN_00431550: delete the record's font and its glyphs.</summary>
    private void ReleaseFont(int rec)
    {
        int font = G(rec, RFont);
        if (font != 0)
        {
            if (!DeleteFont(font))
                return;
            S(rec, RFont, 0);
            S(rec, RFontDirty, 1);
        }
        ClearGlyphs(rec);
    }

    /// <summary>CreateFontA with the record's fields (and FUN_004314E0 first).</summary>
    private void CreateRecordFont(int rec)
    {
        ClearGlyphs(rec);
        int length = 0;
        while (length < 31 && ReadByte(rec + OFace + length) != 0)
            length++;
        string face = Encoding.GetEncoding(932).GetString(ReadBytes(rec + OFace, length));
        var request = new ScnFontRequest(G(rec, RHeight), G(rec, RWidth), G(rec, 5), G(rec, 6), G(rec, RWeight),
            G(rec, RItalic), G(rec, 9), G(rec, RStrikeOut), 0x80, G(rec, RQuality), 1, face);
        int font = m_host.Fonts?.CreateFont(request) ?? 0;
        if (font == 0)
            font = 0x7F000000 + m_liveFonts.Count;   // no font engine: a stand-in handle
        m_liveFonts.Add(font);
        S(rec, RFont, font);
        S(rec, RFontDirty, 0);
        S(rec, RFontMade, 1);
        if (G(rec, RGlyphsDirty) != 0)
        {
            ClearGlyphs(rec);
            S(rec, RGlyphsDirty, 0);
        }
    }

    /// <summary>
    /// The recreate step of FUN_00432F00 / 00434580: DeleteObject the old font (a failure leaves
    /// the record as it is) and CreateFontA a new one.
    /// </summary>
    private void RenewFont(int rec)
    {
        int font = G(rec, RFont);
        if (font != 0)
        {
            if (!DeleteFont(font))
                return;
            S(rec, RFont, 0);
            S(rec, RFontDirty, 1);
        }
        CreateRecordFont(rec);
    }

    /// <summary>The font a device context draws with while the record's font is selected (SelectObject fails for a dead one).</summary>
    private int SelectedFont(int rec)
    {
        int font = G(rec, RFont);
        return font != 0 && m_liveFonts.Contains(font) ? font : 0;
    }

    private (int Width, int Height) Extent(int font, ReadOnlySpan<byte> text) =>
        m_host.Fonts?.TextExtent(font, text) ?? (0, 0);

    private static bool IsLead(byte b) => b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;

    // The character being drawn, [RChar] (NUL-terminated within its 4 bytes)
    private byte[] CharBytes(int rec, int length) => ReadBytes(rec + 4 * RChar, length);

    #region Numbers and strings in control codes

    /// <summary>FUN_004317F0: a decimal or hex number ("0x"), signed, ended by "," "." or a space.</summary>
    private int ParseNumber(int rec)
    {
        int p = G(rec, RPtr);
        byte ch = ReadByte(p++);
        S(rec, RPtr, p);
        while (true)
        {
            if (ch == 0)
            {
                S(rec, RPtr, p - 1);
                return 0;
            }
            if (ch != ' ')
                break;
            ch = ReadByte(p++);
            S(rec, RPtr, p);
        }
        int start = p;
        S(rec, RPtr, p - 1);
        ch = ReadByte(p - 1);
        if (ch == '{')
            return NamedValue(rec);
        int sign = 1;
        if (ch == '-')
        {
            sign = -1;
            S(rec, RPtr, start);
        }
        else if (ch == '+')
            S(rec, RPtr, start);

        int value = 0;
        bool hex = false;
        while (true)
        {
            int at;
            byte b;
            while (true)
            {
                at = G(rec, RPtr);
                b = ReadByte(at);
                S(rec, RPtr, at + 1);
                if (b != 'x')
                    break;
                hex = true;
            }
            if (!hex)
            {
                if (b is < (byte)'0' or > (byte)'9')
                {
                    if (b != '.' && b != ',' && b != ' ')
                        S(rec, RPtr, at);
                    return sign * value;
                }
                value = b - '0' + value * 10;
                continue;
            }
            if (b > '9')
            {
                if (b is (< (byte)'A' or > (byte)'F') and (< (byte)'a' or > (byte)'f'))
                {
                    S(rec, RPtr, at);
                    return sign * value;
                }
                b = (byte)((b & 0xDF) - 7);
            }
            if (b < '0')
            {
                if (b != '.' && b != ',' && b != ' ')
                    S(rec, RPtr, at);
                return sign * value;
            }
            value = (value - 3) * 16 + b;
        }
    }

    /// <summary>"{name}" in a number: the task's named variable (FUN_00401610 / 00434F60), then an optional ",".</summary>
    private int NamedValue(int rec)
    {
        int p = G(rec, RPtr) + 1, end = p;
        while (ReadByte(end) is not (0 or (byte)'}'))
            end++;
        string name = Encoding.GetEncoding(932).GetString(ReadBytes(p, end - p));
        if (ReadByte(end) == '}')
            end++;
        S(rec, RPtr, end);
        int value = Read32(NamedAddress(m_slots[G(rec, RContext)], name));
        if (ReadByte(G(rec, RPtr)) == ',')
            S(rec, RPtr, G(rec, RPtr) + 1);
        return value;
    }

    /// <summary>Copies up to "/" (eaten) or the end of the text into dst, at most max bytes, NUL-terminated.</summary>
    private void ReadField(int rec, int dst, int max)
    {
        int n = 0;
        while (true)
        {
            int p = G(rec, RPtr);
            byte b = ReadByte(p);
            S(rec, RPtr, p + 1);
            if (b == '/')
                break;
            if (b == 0)
            {
                S(rec, RPtr, p);
                break;
            }
            WriteByte(dst + n++, b);
            if (n >= max)
                break;
        }
        WriteByte(dst + n, 0);
    }

    private bool PrevIsComma(int rec) => ReadByte(G(rec, RPtr) - 1) == ',';

    /// <summary>FUN_00445B72 on a code page 932 system: strstr that steps whole characters.</summary>
    private bool FindIn(int haystack, ReadOnlySpan<byte> needle)
    {
        int needleLength = needle.IndexOf((byte)0);
        if (needleLength < 0)
            needleLength = needle.Length;
        int last = haystack + StringLength(haystack) - needleLength;
        for (int p = haystack; ReadByte(p) != 0 && p <= last; p += IsLead(ReadByte(p)) && ReadByte(p + 1) != 0 ? 2 : 1)
        {
            int q = 0;
            if (ReadByte(p) != 0)
            {
                do
                {
                    if (q >= needleLength || ReadByte(p + q) != needle[q])
                        break;
                    q++;
                } while (ReadByte(p + q) != 0);
            }
            if (q >= needleLength)
                return true;
        }
        return false;
    }

    #endregion

    #region Control codes (FUN_00431960)

    private void ControlCode(int rec, int surface)
    {
        int code = G(rec, RPtr);
        byte op = ReadByte(code);
        byte next = ReadByte(code + 1);
        S(rec, RPtr, code + 1);
        switch (op)
        {
            case (byte)'(':
            {
                int n = ParseNumber(rec);
                if (n is >= 0 and < 19)
                {
                    m_savedX[n] = G(rec, RX);
                    m_savedY[n] = G(rec, RY);
                }
                return;
            }
            case (byte)')':
            {
                int n = ParseNumber(rec);
                if (n is >= 0 and < 19)
                {
                    S(rec, RX, m_savedX[n]);
                    S(rec, RY, m_savedY[n]);
                }
                return;
            }
            case (byte)'A':
                S(rec, RZenkaku, 0);
                return;
            case (byte)'B':
                S(rec, RBackSurface, ParseNumber(rec));
                return;
            case (byte)'E':
                WriteByte(rec + OEdgeColor, (byte)ParseNumber(rec));
                WriteByte(rec + OEdgeColor + 1, (byte)ParseNumber(rec));
                WriteByte(rec + OEdgeColor + 2, (byte)ParseNumber(rec));
                S(rec, REdgeX, ParseNumber(rec));
                S(rec, REdgeY, ParseNumber(rec));
                return;
            case (byte)'F':
                S(rec, RFontDirty, 1);
                S(rec, RGlyphsDirty, 1);
                ReadField(rec, rec + OFace, 0xFE);
                return;
            case (byte)'G':
                for (int i = 0x178; i <= 0x17E; i++)
                    S(rec, i, ParseNumber(rec));
                return;
            case (byte)'H':
                S(rec, RFontDirty, 1);
                S(rec, RGlyphsDirty, 1);
                S(rec, RHeight, ParseNumber(rec));
                return;
            case (byte)'I':
                S(rec, RItalic, ParseNumber(rec));
                FontChanged(rec);
                return;
            case (byte)'K':
                S(rec, RKatakana, 1);
                return;
            case (byte)'L':
                S(rec, RL, ParseNumber(rec));
                return;
            case (byte)'O':
                S(rec, RBkMode, 2);
                return;
            case (byte)'P':
                // _Pnot-at-line-start//not-at-line-end//hanging/  (kinsoku lists)
                ReadField(rec, rec + OKinsoku1, 0xFE);
                if (ReadByte(G(rec, RPtr)) == '/')
                {
                    S(rec, RPtr, G(rec, RPtr) + 1);
                    ReadField(rec, rec + OKinsoku2, 0xFE);
                }
                if (ReadByte(G(rec, RPtr)) == '/')
                {
                    S(rec, RPtr, G(rec, RPtr) + 1);
                    ReadField(rec, rec + OKinsoku3, 0xFE);
                }
                return;
            case (byte)'R':
            case (byte)'Y':
                S(rec, RLineHeight, ParseNumber(rec));
                return;
            case (byte)'S':
                if (next == 'a')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RShadowAlpha, ParseNumber(rec));
                    return;
                }
                WriteByte(rec + OShadowColor, (byte)ParseNumber(rec));
                WriteByte(rec + OShadowColor + 1, (byte)ParseNumber(rec));
                WriteByte(rec + OShadowColor + 2, (byte)ParseNumber(rec));
                S(rec, RShadowX, ParseNumber(rec));
                S(rec, RShadowY, ParseNumber(rec));
                if (PrevIsComma(rec))
                    S(rec, RShadowAlpha, ParseNumber(rec));
                return;
            case (byte)'T':
                S(rec, RBkMode, 1);
                return;
            case (byte)'W':
                S(rec, RWait, ParseNumber(rec));
                S(rec, RWaitStart, (int)m_host.Milliseconds);
                return;
            case (byte)'X':
                if (next == 'Z')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RWidePitch, ParseNumber(rec));
                    return;
                }
                {
                    int pitch = ParseNumber(rec);
                    S(rec, RPitch, pitch);
                    S(rec, RWidePitch, pitch * 2);
                    if (PrevIsComma(rec))
                        S(rec, RSpacing, ParseNumber(rec));
                }
                return;
            case (byte)'Z':
                S(rec, RZenkaku, 1);
                S(rec, RZenkakuSpace, 0);
                return;
            case (byte)'a':
                S(rec, RAlign, ParseNumber(rec));
                if (G(rec, RMeasuring) != 0)
                {
                    S(rec, RMeasuring, 0);
                    return;
                }
                S(rec, RAligned, 0);
                AlignLine(rec, surface);
                return;
            case (byte)'b':
                if (next == 'a')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RBkAlpha, ParseNumber(rec));
                    return;
                }
                WriteByte(rec + OBkColor, (byte)ParseNumber(rec));
                WriteByte(rec + OBkColor + 1, (byte)ParseNumber(rec));
                WriteByte(rec + OBkColor + 2, (byte)ParseNumber(rec));
                if (PrevIsComma(rec))
                    S(rec, RBkAlpha, ParseNumber(rec));
                return;
            case (byte)'c':
                if (next == 'a')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RAlpha, ParseNumber(rec));
                    return;
                }
                WriteByte(rec + OColor, (byte)ParseNumber(rec));
                WriteByte(rec + OColor + 1, (byte)ParseNumber(rec));
                WriteByte(rec + OColor + 2, (byte)ParseNumber(rec));
                if (PrevIsComma(rec))
                    S(rec, RAlpha, ParseNumber(rec));
                if (surface != -1)
                    UseColor(rec, OColor);
                return;
            case (byte)'d':
                S(rec, RGlyphW, ParseNumber(rec));
                if (PrevIsComma(rec))
                    S(rec, RGlyphH, ParseNumber(rec));
                return;
            case (byte)'e':
                S(rec, REffects, ParseNumber(rec));
                return;
            case (byte)'f':
                S(rec, RWeight, ParseNumber(rec));
                FontChanged(rec);
                return;
            case (byte)'g':
                S(rec, RLayer, ParseNumber(rec));
                if (PrevIsComma(rec))
                    S(rec, RLayerIndex, ParseNumber(rec));
                return;
            case (byte)'h':
                S(rec, RWidth, ParseNumber(rec));
                FontChanged(rec);
                return;
            case (byte)'i':
                if (next != '/')
                {
                    FillMemory(rec + OIndents, 0x100, 0);
                    S(rec, RIndent, ParseNumber(rec));
                    return;
                }
                S(rec, RPtr, code + 2);
                if (ReadByte(code + 2) == '/')
                {
                    S(rec, RPtr, code + 3);
                    FillMemory(rec + OIndents, 0x100, 0);
                    return;
                }
                {
                    // _i/text/: one more string a wrapped line is indented after
                    int at = 0;
                    while (ReadByte(rec + OIndents + at) != 0 || ReadByte(rec + OIndents + at + 1) != 0)
                        at++;
                    int dst = rec + OIndents + at + 1;
                    m_indentScratch = m_indentScratch != 0 ? m_indentScratch : Allocate(0x100);
                    ReadField(rec, m_indentScratch, 0xFE);
                    WriteBytes(dst, ReadBytes(m_indentScratch, StringLength(m_indentScratch) + 1));
                }
                return;
            case (byte)'k':
                S(rec, RKatakana, 0);
                return;
            case (byte)'l':
                S(rec, RRight, ParseNumber(rec));
                return;
            case (byte)'n':
                throw new NotSupportedException("Text code _n (swap text records) is not supported yet");
            case (byte)'o':
                S(rec, RStrikeOut, ParseNumber(rec));
                FontChanged(rec);
                return;
            case (byte)'p':
                S(rec, RFixedW, ParseNumber(rec));
                if (PrevIsComma(rec))
                    S(rec, RFixedH, ParseNumber(rec));
                return;
            case (byte)'q':
                if (next == '+')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RAntialias, 1);
                    return;
                }
                if (next == '-')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RAntialias, 0);
                    return;
                }
                S(rec, RQuality, ParseNumber(rec));
                FontChanged(rec);
                return;
            case (byte)'r':
                if (G(rec, RMeasuring) != 0)
                {
                    S(rec, RMeasuring, 0);
                    return;
                }
                if (next == '!')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RLineStart, 0);
                }
                if (G(rec, RLineStart) == 0)
                {
                    S(rec, RX, G(rec, RLeft));
                    S(rec, RY, G(rec, RY) + G(rec, RLineHeight));
                    S(rec, RX, G(rec, RX) + G(rec, RIndent));
                }
                S(rec, RAligned, 0);
                AlignLine(rec, surface);
                return;
            case (byte)'s':
                S(rec, RSkipKeys, ParseNumber(rec) & 0xFF);
                return;
            case (byte)'t':
                if (next == '/')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RBeforeChar, ParseNumber(rec));
                    return;
                }
                if (next != '!')
                {
                    S(rec, RAfterChar, ParseNumber(rec));
                    return;
                }
                {
                    S(rec, RPtr, code + 2);
                    int slot = ParseNumber(rec);
                    m_slots[slot].TextRecord = G(rec, RIndex);
                    if (G(rec, RMeasuring) == 0)
                        RunSlotSync(slot);
                }
                return;
            case (byte)'u':
            {
                m_rubyRecord = ParseNumber(rec);
                int ruby = RecordAddress(m_rubyRecord);
                S(ruby, RRubyW, ParseNumber(rec));
                S(ruby, RRubyH, ParseNumber(rec));
                return;
            }
            case (byte)'w':
                S(rec, RCharDelay, ParseNumber(rec));
                S(rec, RCharTime, (int)m_host.Milliseconds);
                return;
            case (byte)'x':
                if (next is (byte)'-' or (byte)'+')
                {
                    // the sign is skipped: both move right (as the executable does)
                    S(rec, RPtr, code + 2);
                    int dx = ParseNumber(rec);
                    S(rec, RX, G(rec, RX) + dx);
                    S(rec, RLeft, G(rec, RX));
                    return;
                }
                {
                    int x = ParseNumber(rec);
                    S(rec, RX, x);
                    S(rec, RLeft, x);
                }
                return;
            case (byte)'y':
                if (next is (byte)'-' or (byte)'+')
                {
                    S(rec, RPtr, code + 2);
                    S(rec, RY, G(rec, RY) + ParseNumber(rec));
                    return;
                }
                S(rec, RY, ParseNumber(rec));
                return;
            case (byte)'z':
                S(rec, RZenkaku, 1);
                S(rec, RZenkakuSpace, 1);
                return;
        }
    }

    private void FontChanged(int rec)
    {
        S(rec, RFontDirty, 1);
        S(rec, RGlyphsDirty, 1);
    }

    /// <summary>SetTextColor: the colour the glyphs are blended with ([0x18C..0x18E] = R, G, B).</summary>
    private void UseColor(int rec, int offset)
    {
        S(rec, RTextR, ReadByte(rec + offset));
        S(rec, RTextG, ReadByte(rec + offset + 1));
        S(rec, RTextB, ReadByte(rec + offset + 2));
    }

    #endregion

    #region Alignment (FUN_00434BA0 / 00434580)

    /// <summary>"_a": measure the rest of the line in the scratch copy, then centre or right-align it.</summary>
    private void AlignLine(int rec, int surface)
    {
        if (surface == -1 || G(rec, RAlign) == 0 || G(rec, RAligned) != 0)
            return;
        int index = G(rec, RIndex);
        int copy = RecordAddress(index + ScratchRecords);
        CopyMemory(copy, rec, RecordSize);
        S(copy, RIndex, index + ScratchRecords);
        S(copy, RFontMade, 0);
        S(copy, RX, 0);
        MeasureLine(copy);
        int count = G(copy, RCount);
        int width = G(copy, RX);
        S(copy, RIndex, index);
        int font = G(copy, RFont);
        bool cleared = true;
        if (font != 0)
        {
            if (DeleteFont(font))
            {
                S(copy, RFont, 0);
                S(copy, RFontDirty, 1);
            }
            else
                cleared = false;
        }
        if (cleared)
            ClearGlyphs(copy);    // the copy's index is the record's again: its glyphs go
        S(rec, RCount, count);
        S(rec, RMeasured, width);
        switch (G(rec, RAlign) & 0xF)
        {
            case 1:
                S(rec, RX, G(rec, RLeft) - (int)((uint)width >> 1));
                break;
            case 2:
                S(rec, RX, G(rec, RLeft) - width);
                break;
        }
        S(rec, RAligned, 1);
    }

    /// <summary>FUN_00434580: advance through the line without drawing, to its end ("_r") or the end of the text.</summary>
    private void MeasureLine(int rec)
    {
        S(rec, RMeasuring, 1);
        while (true)
        {
            int at = G(rec, RPtr);
            byte b = ReadByte(at);
            byte next = ReadByte(at + 1);
            S(rec, RPtr, at + 1);
            bool literal = false;
            switch (b)
            {
                case 0:
                    if (G(rec, RDepth) == 0)
                    {
                        S(rec, RMeasuring, 0);
                        return;
                    }
                    S(rec, RDepth, G(rec, RDepth) - 1);
                    S(rec, RPtr, G(rec, RStack + G(rec, RDepth)));
                    continue;
                case (byte)'$':
                case (byte)'}':
                    if (next != b)
                        continue;
                    literal = true;
                    break;
                case (byte)'*':
                    if (next == b) { literal = true; break; }
                    PushMacro(rec);
                    continue;
                case (byte)'+':
                    if (next == b) { literal = true; break; }
                    DefineMacro(rec);
                    continue;
                case (byte)'@':
                    if (next == b) { literal = true; break; }
                    S(rec, RKatakana, G(rec, RKatakana) == 0 ? 1 : 0);
                    continue;
                case (byte)'_':
                    if (next == b) { literal = true; break; }
                    ControlCode(rec, 0);
                    if (G(rec, RMeasuring) == 0)
                        return;
                    continue;
                case (byte)'{':
                    if (next == b) { literal = true; break; }
                    S(rec, RPtr, at + 2);
                    for (byte c = next; c != '}';)
                    {
                        int p = G(rec, RPtr);
                        c = ReadByte(p);
                        S(rec, RPtr, p + 1);
                    }
                    continue;
                case (byte)'|':
                    if (next == b) { literal = true; break; }
                    S(rec, RZenkaku, G(rec, RZenkaku) == 0 ? 1 : 0);
                    continue;
                case (byte)'~':
                    if (next == b) { literal = true; break; }
                    Tilde(rec, at);
                    continue;
            }
            S(rec, RPtr, literal ? at + 1 : at);
            if (G(rec, RFontDirty) != 0)
                RenewFont(rec);
            int length = TakeChar(rec);
            int pitch = G(rec, RPitch);
            if (pitch == -1)
            {
                int w = G(rec, RFixedW) != 0 ? G(rec, RFixedW) : Extent(SelectedFont(rec), CharBytes(rec, length)).Width;
                S(rec, RX, G(rec, RX) + w);
            }
            else
            {
                if (length == 2)
                    pitch = G(rec, RWidePitch);
                S(rec, RX, G(rec, RX) + pitch + G(rec, RSpacing) * length);
            }
            S(rec, RCount, G(rec, RCount) + length);
        }
    }

    #endregion

    #region Macros and small codes shared by drawing and measuring

    /// <summary>"*n": continue with macro n, coming back here at its end.</summary>
    private void PushMacro(int rec)
    {
        uint n = (uint)ParseNumber(rec);
        if (n >= 0x101)
            return;
        int depth = G(rec, RDepth);
        if (depth < 1000)
            S(rec, RStack + depth, G(rec, RPtr));
        S(rec, RDepth, depth + 1);
        if ((uint)(depth + 1) < 1000)
            S(rec, RPtr, MacroRegion + (int)n * 0x100);
    }

    /// <summary>"+n text/": macro n is the text.</summary>
    private void DefineMacro(int rec)
    {
        int n = ParseNumber(rec);
        if (n is < 0 or > 0x100)
            throw new NotSupportedException($"Text macro {n} is outside the table");
        ReadField(rec, MacroRegion + n * 0x100, 0xFE);
    }

    private void Tilde(int rec, int at)
    {
        byte sign = ReadByte(at + 1);
        if (sign is (byte)'-' or (byte)'+')
        {
            S(rec, RPtr, at + 2);
            EngineGlobals[0x5E3B00] = EngineGlobals.GetValueOrDefault(0x5E3B00) + ParseNumber(rec);
        }
        else
            EngineGlobals[0x5E3B00] = ParseNumber(rec);
    }

    /// <summary>
    /// Puts the character at the text pointer into [RChar] (a half-width one made full-width in
    /// "_Z" mode), moves past it and returns its length in bytes.
    /// </summary>
    private int TakeChar(int rec)
    {
        int p = G(rec, RPtr);
        byte b = ReadByte(p);
        int length = IsLead(b) ? 2 : 1;
        S(rec, RChar, 0);
        WriteBytes(rec + 4 * RChar, ReadBytes(p, length));
        if (length == 1 && G(rec, RZenkaku) != 0 && (G(rec, RZenkakuSpace) != 0 || b != ' '))
        {
            int i = b + (G(rec, RKatakana) != 0 ? 0x100 : 0);
            WriteBytes(rec + 4 * RChar, s_zenkaku.AsSpan(2 * i, 2));
            length = 2;
        }
        S(rec, RPtr, p + (IsLead(b) ? 2 : 1));
        return length;
    }

    #endregion

    #region Drawing (FUN_00432F00)

    /// <summary>
    /// One step of a record's text (FUN_00432F00): control codes up to the next character, which is
    /// drawn into <paramref name="surface"/> (-1: none, only the codes take effect). Stops at the end
    /// of the text (RState 0) or when a wait is due. <paramref name="invalidate"/>: the surface is
    /// on screen.
    /// </summary>
    private void TextStep(int rec, int surface, bool invalidate)
    {
        AlignLine(rec, surface);
        S(rec, RState, 2);
        if (G(rec, RSkipKeys) != 0)
        {
            int keys = G(rec, RSkipKeys) & 0xFF, buttons = TextSkipButtons();
            if ((keys & 1) != 0 && (buttons & 0x20) != 0)
                S(rec, RWaits, 0);
            if ((keys & 2) != 0 && (buttons & 0x10) != 0)
                S(rec, RWaits, 0);
            if ((keys & 4) != 0 && (buttons & 0x100) != 0)
                S(rec, RWaits, 0);
            if ((keys & 8) != 0 && EngineGlobals.GetValueOrDefault(0x13B52B4) != 0)
                S(rec, RWaits, 0);
        }
        if (G(rec, RWaits) != 0 && G(rec, RWait) != 0)
        {
            if ((uint)((int)m_host.Milliseconds - G(rec, RWaitStart)) < (uint)G(rec, RWait))
            {
                Thread.Sleep(1);
                return;
            }
            S(rec, RWait, 0);
        }
        if (surface != -1)
        {
            UseColor(rec, OColor);
            S(rec, RPixels, SurfaceField(surface, 2));
            S(rec, RSurfaceWidth, SurfaceField(surface, 7));
            S(rec, RSurfaceHeight, SurfaceField(surface, 8));
            S(rec, RSurfacePitch, SurfaceField(surface, 10));
        }

        while (true)
        {
            int at = G(rec, RPtr);
            byte b = ReadByte(at);
            byte next = ReadByte(at + 1);
            S(rec, RPtr, at + 1);
            bool literal = false;
            switch (b)
            {
                case 0:
                {
                    int depth = G(rec, RDepth);
                    if (depth == 0)
                    {
                        S(rec, RState, 0);
                        return;
                    }
                    S(rec, RDepth, depth - 1);
                    S(rec, RPtr, G(rec, RStack + depth - 1));
                    continue;
                }
                case (byte)'$':
                    if (next == b) { literal = true; break; }
                    EngineGlobals[0x5E3AA8] = G(rec, RX);
                    EngineGlobals[0x5E3AF4] = G(rec, RY);
                    S(rec, RCountMark, G(rec, RCount));
                    continue;
                case (byte)'*':
                    if (next == b) { literal = true; break; }
                    PushMacro(rec);
                    continue;
                case (byte)'+':
                    if (next == b) { literal = true; break; }
                    DefineMacro(rec);
                    continue;
                case (byte)'@':
                    if (next == b) { literal = true; break; }
                    S(rec, RKatakana, G(rec, RKatakana) == 0 ? 1 : 0);
                    continue;
                case (byte)'_':
                    if (next == b) { literal = true; break; }
                    ControlCode(rec, surface);
                    if (G(rec, RWait) != 0)
                        return;
                    continue;
                case (byte)'{':
                case (byte)'}':
                    if (next == b) { literal = true; break; }
                    throw new NotSupportedException("Ruby text ({...}) is not supported yet");
                case (byte)'|':
                    if (next == b) { literal = true; break; }
                    S(rec, RZenkaku, G(rec, RZenkaku) == 0 ? 1 : 0);
                    continue;
                case (byte)'~':
                    if (next == b) { literal = true; break; }
                    Tilde(rec, at);
                    continue;
            }
            S(rec, RPtr, literal ? at + 1 : at);
            if (!DrawChar(rec, surface, invalidate))
                return;
        }
    }

    /// <summary>The character part of FUN_00432F00; false when the step ends after it (a wait).</summary>
    private bool DrawChar(int rec, int surface, bool invalidate)
    {
        uint now = m_host.Milliseconds;
        if (G(rec, RWaits) != 0 && G(rec, RCharDelay) != 0)
        {
            if ((uint)((int)now - G(rec, RCharTime)) < (uint)G(rec, RCharDelay))
                return false;
            S(rec, RCharTime, (int)m_host.Milliseconds);
        }
        bool dc = surface != -1;
        if (dc && G(rec, RFontDirty) != 0)
            RenewFont(rec);
        int font = dc ? SelectedFont(rec) : 0;
        if ((G(rec, RColorDirty) & 1) != 0)
        {
            S(rec, RColorDirty, G(rec, RColorDirty) & ~1);
            UseColor(rec, OColor);
        }
        if ((G(rec, RColorDirty) & 2) != 0)
            S(rec, RColorDirty, G(rec, RColorDirty) & ~2);
        if (G(rec, RIndent) == 0)
            FindIndent(rec, font, dc);

        int length = TakeChar(rec);
        byte[] ch = CharBytes(rec, 4);
        (int Width, int Height) ext = default;

        // Wrap before a character that would cross the right edge and may not end a line
        int x = G(rec, RX), pitch = G(rec, RPitch);
        uint end;
        if (pitch == -1 && dc)
        {
            ext = FixedOr(rec, font, ch, length);
            end = (uint)(x + ext.Width);
        }
        else
        {
            if (length == 2)
                pitch = G(rec, RWidePitch);
            end = (uint)(x + pitch + G(rec, RSpacing) * length);
        }
        if ((uint)G(rec, RRight) <= end && FindIn(rec + OKinsoku2, ch))
        {
            int oldX = G(rec, RX);
            S(rec, RX, G(rec, RLeft));
            S(rec, RPrevY, G(rec, RY));
            S(rec, RPrevX, oldX);
            S(rec, RY, G(rec, RY) + G(rec, RLineHeight));
            S(rec, RX, G(rec, RX) + G(rec, RIndent));
            S(rec, RLineStart, 1);
        }
        // A character that may not start a line goes back to the end of the previous one
        if (G(rec, RKinsoku) == 0)
        {
            if ((G(rec, RX) == G(rec, RLeft) || G(rec, RWrapped) != 0) && G(rec, RLineStart) != 0)
            {
                int p = G(rec, RPtr);
                var following = new byte[4];
                int n = IsLead(ReadByte(p)) ? 2 : 1;
                ReadBytes(p, n).CopyTo(following, 0);
                bool back;
                if (!FindIn(rec + OKinsoku3, ch))
                    back = FindIn(rec + OKinsoku1, ch);
                else
                    back = FindIn(rec + OKinsoku3, following) || !FindIn(rec + OKinsoku1, following);
                if (back)
                {
                    S(rec, RX, G(rec, RPrevX));
                    S(rec, RY, G(rec, RPrevY));
                    S(rec, RKinsoku, 1);
                }
                S(rec, RWrapped, 0);
            }
        }
        else
            S(rec, RKinsoku, 0);

        if (dc)
            DrawWithEffects(rec, surface, invalidate, font, ch, length);

        if (G(rec, RAfterChar) != -1)
        {
            m_slots[G(rec, RAfterChar)].TextRecord = G(rec, RIndex);
            RunSlotSync(G(rec, RAfterChar));
        }

        // Advance
        pitch = G(rec, RPitch);
        if (pitch == -1 && dc)
        {
            ext = FixedOr(rec, font, ch, length);
            S(rec, RX, G(rec, RX) + ext.Width);
        }
        else
        {
            if (length == 2)
                pitch = G(rec, RWidePitch);
            S(rec, RX, G(rec, RX) + pitch);
            S(rec, RX, G(rec, RX) + G(rec, RSpacing) * length);
        }
        S(rec, RCount, G(rec, RCount) + length);
        S(rec, RLineStart, 0);
        if ((uint)G(rec, RRight) <= (uint)G(rec, RX))
        {
            S(rec, RPrevX, G(rec, RX));
            S(rec, RX, G(rec, RLeft));
            S(rec, RPrevY, G(rec, RY));
            S(rec, RY, G(rec, RY) + G(rec, RLineHeight));
            S(rec, RX, G(rec, RX) + G(rec, RIndent));
            S(rec, RLineStart, 1);
            S(rec, RWrapped, 1);
        }
        return !(dc && G(rec, RWaits) != 0 && (G(rec, RWait) != 0 || G(rec, RCharDelay) != 0));
    }

    private (int Width, int Height) FixedOr(int rec, int font, byte[] ch, int length) =>
        G(rec, RFixedW) != 0 ? (G(rec, RFixedW), G(rec, RFixedH)) : Extent(font, ch.AsSpan(0, length));

    /// <summary>With no indent yet: the first "_i/" string found in the rest of the text sets it.</summary>
    private void FindIndent(int rec, int font, bool dc)
    {
        int at = 1;
        while (ReadByte(rec + OIndents + at) != 0)
        {
            int s = rec + OIndents + at;
            int length = StringLength(s);
            byte[] needle = ReadBytes(s, length);
            int text = G(rec, RPtr), textLength = StringLength(text);
            if (ReadBytes(text, textLength).AsSpan().IndexOf(needle) >= 0)
            {
                int indent;
                if (G(rec, RPitch) == -1)
                {
                    int w = G(rec, RFixedW) != 0 ? G(rec, RFixedW) : dc ? Extent(font, needle).Width : 0;
                    indent = G(rec, RSpacing) * length - G(rec, RLeft) + G(rec, RX) + w;
                }
                else
                    indent = length / 2 * G(rec, RWidePitch) + G(rec, RSpacing) * length - G(rec, RLeft) + G(rec, RX);
                S(rec, RIndent, indent);
                return;
            }
            at += length + 1;
        }
    }

    /// <summary>The drawing part of FUN_00432F00: background copy, shadow, edges, the character.</summary>
    private void DrawWithEffects(int rec, int surface, bool invalidate, int font, byte[] ch, int length)
    {
        var ext = FixedOr(rec, font, ch, length);
        int x = G(rec, RX), y = G(rec, RY);
        int left = x, top = y, right = x + ext.Width, bottom = y + ext.Height;
        int effects = G(rec, REffects);
        int sx = G(rec, RShadowX), sy = G(rec, RShadowY), ex = G(rec, REdgeX), ey = G(rec, REdgeY);
        if ((effects & 1) != 0)
        {
            left = Math.Min(left, sx + x);
            top = Math.Min(top, y + sy);
            right = Math.Max(right, right + sx);
            bottom = Math.Max(bottom, bottom + sy);
        }
        if ((effects & 0x1FE) != 0)
        {
            if ((effects & 0x40) != 0) { left -= ex; top -= ey; }
            if ((effects & 0x80) != 0) top -= ey;
            if ((effects & 0x100) != 0) { right += ex; top -= ey; }
            if ((effects & 0x10) != 0) left -= ex;
            if ((effects & 0x20) != 0) right += ex;
            if ((effects & 2) != 0) { left -= ex; bottom += ey; }
            if ((effects & 4) != 0) bottom += ey;
            if ((effects & 8) != 0) { right += ex; bottom += ey; }
        }
        if ((effects & 0xC00) != 0)
        {
            left -= ex;
            top -= ey;
            right += ex;
            bottom += ey;
        }
        S(rec, RRect, left);
        S(rec, RRect + 1, top);
        S(rec, RRect + 2, right);
        S(rec, RRect + 3, bottom);

        int skip = 0;
        if (G(rec, RBeforeChar) != -1)
        {
            int slot = G(rec, RBeforeChar);
            m_slots[slot].TextRecord = G(rec, RIndex);
            skip = RunSlotSync(slot);
        }
        if ((skip & 2) == 0 && G(rec, RBackSurface) != -1)
            BitBlt(surface, left, top, right - left, bottom - top, G(rec, RBackSurface), left, top);
        if ((skip & 1) != 0)
            return;

        bool restore = false;
        if ((effects & 1) != 0)
        {
            UseColor(rec, OShadowColor);
            S(rec, RAlpha, G(rec, RShadowAlpha));
            DrawGlyph(rec, surface, font, sx + x, sy + y, ch, length);
            restore = true;
        }
        if ((effects & 0x1FE) != 0)
        {
            UseColor(rec, OEdgeColor);
            if ((effects & 0x40) != 0) DrawGlyph(rec, surface, font, x - ex, y - ey, ch, length);
            if ((effects & 0x80) != 0) DrawGlyph(rec, surface, font, x, y - ey, ch, length);
            if ((effects & 0x100) != 0) DrawGlyph(rec, surface, font, ex + x, y - ey, ch, length);
            if ((effects & 0x10) != 0) DrawGlyph(rec, surface, font, x - ex, y, ch, length);
            if ((effects & 0x20) != 0) DrawGlyph(rec, surface, font, ex + x, y, ch, length);
            if ((effects & 2) != 0) DrawGlyph(rec, surface, font, x - ex, ey + y, ch, length);
            if ((effects & 4) != 0) DrawGlyph(rec, surface, font, x, ey + y, ch, length);
            if ((effects & 8) != 0) DrawGlyph(rec, surface, font, ex + x, ey + y, ch, length);
            restore = true;
        }
        if ((effects & 0x400) != 0)
        {
            UseColor(rec, OEdgeColor);
            for (int dx = -G(rec, REdgeX); dx <= G(rec, REdgeX); dx++)
                for (int dy = -G(rec, REdgeY); dy <= G(rec, REdgeY); dy++)
                    DrawGlyph(rec, surface, font, x + dx, y + dy, ch, length);
            restore = true;
        }
        if ((effects & 0x800) != 0)
        {
            UseColor(rec, OEdgeColor);
            DrawGlyph(rec, surface, font, x, y, ch, length | unchecked((int)0x80000000));
        }
        if (restore)
            UseColor(rec, OColor);
        DrawGlyph(rec, surface, font, x, y, ch, length);
        if (invalidate && G(rec, RLayer) == -1)
        {
            ScreenInvalidated = true;
            FrameShown = true;
        }
    }

    /// <summary>BitBlt SRCCOPY between two 24-bit surfaces, clipped to both.</summary>
    public void BitBlt(int dst, int x, int y, int width, int height, int src, int sx, int sy)
    {
        if (src is < 0 or >= SurfaceCount)
            return;
        int dw = SurfaceField(dst, 7), dh = SurfaceField(dst, 8), sw = SurfaceField(src, 7), sh = SurfaceField(src, 8);
        if (SurfaceField(src, 2) == 0 || SurfaceField(dst, 2) == 0)
            return;
        if (x < 0) { sx -= x; width += x; x = 0; }
        if (y < 0) { sy -= y; height += y; y = 0; }
        if (sx < 0) { x -= sx; width += sx; sx = 0; }
        if (sy < 0) { y -= sy; height += sy; sy = 0; }
        width = Math.Min(width, Math.Min(dw - x, sw - sx));
        height = Math.Min(height, Math.Min(dh - y, sh - sy));
        if (width <= 0 || height <= 0)
            return;
        int dp = SurfaceField(dst, 10), sp = SurfaceField(src, 10);
        int d = SurfaceField(dst, 2) + y * dp + x * 3, s = SurfaceField(src, 2) + sy * sp + sx * 3;
        for (int row = 0; row < height; row++, d += dp, s += sp)
            WriteBytes(d, ReadBytes(s, width * 3));
    }

    /// <summary>The buttons that skip text waits ("_s" keys); input is not wired yet.</summary>
    private int TextSkipButtons() => 0;

    /// <summary>
    /// FUN_004325A0 with antialiasing on ("_q+"): the GGO_GRAY8 glyph blended into the surface,
    /// a = (coverage * 255 >> 6) * alpha >> 8, d += (c - d) * a >> 8 in 32-bit unsigned arithmetic.
    /// Bit 31 of <paramref name="length"/> draws every pixel as an (2 edge x + 1) x (2 edge y + 1)
    /// block (the "_e" 0x800 outline).
    /// </summary>
    private void DrawGlyph(int rec, int surface, int font, int x, int y, byte[] ch, int length)
    {
        var fonts = m_host.Fonts;
        if (fonts == null)
            return;
        int count = length & 0x7FFFFFFF;
        if (G(rec, RAntialias) == 0)
            throw new NotSupportedException("Text without antialiasing (TextOutA) is not supported yet");
        if (G(rec, RBkMode) == 2)
            throw new NotSupportedException("Opaque text background (_O) is not supported yet");
        if (G(rec, RLayer) != -1)
            throw new NotSupportedException($"Text into layer {G(rec, RLayer)} (_g) is not supported yet");

        int code = count == 1 ? (ushort)(sbyte)ch[0] : ch[0] << 8 | ch[1];
        var cache = m_glyphs[G(rec, RIndex) % TextRecords] ??= new Dictionary<int, ScnGlyph>();
        if (!cache.TryGetValue(code, out var glyph))
            cache[code] = glyph = fonts.Glyph(font, code);

        int ascent = fonts.Ascent(font);
        int alpha = G(rec, RAlpha);
        int gx = glyph.OriginX + x;
        int top = ascent - glyph.OriginY + y;
        int right = glyph.BlackBoxX + gx, bottom = glyph.BlackBoxY + top;
        // FUN_00417790: clip to the surface
        int cl = gx, ct = top, cr = right, cb = bottom;
        int width = G(rec, RSurfaceWidth), height = G(rec, RSurfaceHeight);
        if (width < cl || height < ct)
            return;
        if (cl < 0)
        {
            if (cr < 1)
                return;
            cl = 0;
        }
        if (ct < 0)
        {
            if (cb < 1)
                return;
            ct = 0;
        }
        cr = Math.Min(cr, width);
        cb = Math.Min(cb, height);

        uint firstCol = (uint)(cl - gx), firstRow = (uint)(ct - top);
        uint endCol = (uint)(glyph.BlackBoxX - right + cr), endRow = (uint)(glyph.BlackBoxY - bottom + cb);
        if (endRow <= firstRow)
            return;
        uint r = (uint)G(rec, RTextR), g = (uint)G(rec, RTextG), b = (uint)G(rec, RTextB);
        int pixels = G(rec, RPixels), pitch = G(rec, RSurfacePitch);
        int limit = pixels + pitch * height;

        void Blend(int p, uint a)
        {
            if (p < pixels || p + 3 > limit)
                return;
            uint d0 = ReadByte(p), d1 = ReadByte(p + 1), d2 = ReadByte(p + 2);
            WriteByte(p, (byte)(((b - d0) * a >> 8) + d0));
            WriteByte(p + 1, (byte)(((g - d1) * a >> 8) + d1));
            WriteByte(p + 2, (byte)(((r - d2) * a >> 8) + d2));
        }

        int src = (int)(firstRow * glyph.Pitch + firstCol);
        int row = ct;
        for (uint gy = firstRow; gy < endRow; gy++, src += glyph.Pitch, row++)
        {
            if (firstCol >= endCol)
                continue;
            int dst = pitch * row + cl * 3 + pixels;
            for (uint col = 0; col < endCol - firstCol; col++, dst += 3)
            {
                byte v = glyph.Bits.Length > src + col ? glyph.Bits[src + (int)col] : (byte)0;
                if (v == 0)
                    continue;
                uint a = ((uint)v * 0xFF >> 6) * (uint)alpha >> 8;
                if ((length & 0x80000000) == 0)
                {
                    Blend(dst, a);
                    continue;
                }
                int ex = G(rec, REdgeX), ey = G(rec, REdgeY);
                int corner = dst + pitch * -ey + ex * -3;
                for (int dy = -ey; dy <= ey; dy++, corner += pitch)
                    for (int dx = -ex, q = corner; dx <= ex; dx++, q += 3)
                        Blend(q, a);
            }
        }
    }

    #endregion

    #region Opcodes

    private int CurrentRecord(ScnContext c) => RecordAddress(c.TextRecord);

    /// <summary>The flag-8 part of the main loop: one text step of a task's 0083 text.</summary>
    private void StepTaskText(ScnContext c)
    {
        int rec = CurrentRecord(c);
        int surface = c.TextSurface;
        S(rec, RContext, c.Slot);
        TextStep(rec, surface is >= 0 and < SurfaceCount ? surface : -1, surface == DisplaySurface);
        if (G(rec, RState) == 0)
            c.Flags &= ~8;
    }

    private void RegisterText()
    {
        for (int i = 0; i < TextRecords; i++)
            ResetRecord(i);

        // 0096 n: the task's text record; its font is made again
        Register(0x0096, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            if (n is < 0 or >= TextRecords)
                return 2;
            c.TextRecord = n;
            int rec = vm.RecordAddress(n);
            vm.S(rec, RFontDirty, 1);
            vm.S(rec, RGlyphsDirty, 0);
            return 0;
        });
        // 0084 surface, text: lay out and draw the whole text now (-1: control codes only)
        Register(0x0084, (vm, c, i) =>
        {
            int surface = vm.Value(c, i.Args[0]), text = vm.Value(c, i.Args[1]);
            int rec = vm.CurrentRecord(c);
            vm.SetText(rec, -1, -1, text);
            bool invalidate = surface == vm.DisplaySurface;
            if (surface is < -1 or >= SurfaceCount)
                surface = -1;
            vm.S(rec, RContext, c.Slot);
            while (vm.G(rec, RState) != 0)
                vm.TextStep(rec, surface, invalidate);
            return 0;
        });
        // 0083 surface, text: draw the text over the next frames; the task waits (flag 8)
        Register(0x0083, (vm, c, i) =>
        {
            c.TextSurface = vm.Value(c, i.Args[0]);
            int text = vm.Value(c, i.Args[1]);
            int rec = vm.CurrentRecord(c);
            vm.SetText(rec, -1, -1, text);
            c.Flags |= 8;
            if ((vm.G(rec, RSkipKeys) & 8) != 0)
                vm.EngineGlobals[0x13B52B4] = 0;
            return 0;
        });
        // 0078 x, y: the text position (and the left margin)
        Register(0x0078, (vm, c, i) =>
        {
            int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
            int rec = vm.CurrentRecord(c);
            vm.S(rec, RX, x);
            vm.S(rec, RLeft, x);
            vm.S(rec, RY, y);
            return 0;
        });
        // 0079 x, y: where the text is
        Register(0x0079, (vm, c, i) =>
        {
            int rec = vm.CurrentRecord(c);
            vm.Store(c, i.Args[0], vm.G(rec, RX));
            vm.Store(c, i.Args[1], vm.G(rec, RY));
            return 0;
        });
        // 0066 v: the next character of the text (1 or 2 bytes), and move past it
        Register(0x0066, (vm, c, i) =>
        {
            int rec = vm.CurrentRecord(c);
            int p = vm.G(rec, RPtr);
            int length = IsLead(vm.ReadByte(p)) ? 2 : 1;
            int value = length == 2 ? vm.ReadByte(p) | vm.ReadByte(p + 1) << 8 : vm.ReadByte(p);
            vm.S(rec, RPtr, p + length);
            vm.Store(c, i.Args[0], value);
            return 0;
        });
        // 00B4 layer, index: text goes into a layer ("_g"); 00B6 x, y: offset there
        Register(0x00B4, (vm, c, i) =>
        {
            int rec = vm.CurrentRecord(c);
            vm.S(rec, RLayer, vm.Value(c, i.Args[0]));
            vm.S(rec, RLayerIndex, vm.Value(c, i.Args[1]));
            return 0;
        });
        Register(0x00B6, (vm, c, i) =>
        {
            int rec = vm.CurrentRecord(c);
            vm.S(rec, ROffsetX, vm.Value(c, i.Args[0]));
            vm.S(rec, ROffsetY, vm.Value(c, i.Args[1]));
            return 0;
        });
        // 00A0 text: set the text and keep a snapshot of the record to redraw from
        Register(0x00A0, (vm, c, i) =>
        {
            int text = vm.Value(c, i.Args[0]);
            int rec = vm.CurrentRecord(c);
            vm.SetText(rec, -1, -1, text);
            int snapshot = SnapshotRegion + c.TextRecord * SnapshotSize;
            vm.Write32(snapshot + SnapshotLimit, text);
            vm.CopyMemory(snapshot, rec, RecordSize);
            return 0;
        });
        // 00A1 slot, v (FUN_004157C0): redraw the snapshot's text into the slot's text surface -
        // what was shown before at once, then on with the waits; v = still running
        Register(0x00A1, (vm, c, i) =>
        {
            int owner = vm.Value(c, i.Args[0]);
            int rec = vm.CurrentRecord(c);
            int snapshot = SnapshotRegion + c.TextRecord * SnapshotSize;
            vm.CopyMemory(rec, snapshot, RecordSize);
            int waits = vm.G(rec, RWaits);
            vm.S(rec, RContext, c.Slot);
            int surface = owner is >= 0 and < Slots ? vm.m_slots[owner].TextSurface : -1;
            if (surface is < 0 or >= SurfaceCount)
                surface = -1;
            int limit = vm.Read32(snapshot + SnapshotLimit);
            if ((uint)vm.G(rec, RPtr) < (uint)limit)
            {
                byte saved = vm.ReadByte(limit);
                vm.WriteByte(limit, 0);
                vm.S(rec, RWaits, 0);
                vm.TextStep(rec, surface, false);
                vm.WriteByte(limit, saved);
                vm.S(rec, RPtr, vm.G(rec, RPtr) - 1);
                if (saved != 0)
                    vm.S(rec, RState, 2);
            }
            vm.S(rec, RWaits, waits);
            vm.ReleaseFont(rec);
            if (vm.G(rec, RState) != 0)
            {
                vm.S(rec, RFontDirty, 1);
                vm.TextStep(rec, surface, false);
                vm.ReleaseFont(rec);
            }
            vm.Write32(snapshot + SnapshotLimit, vm.G(rec, RPtr));
            vm.S(snapshot, RCharTime, vm.G(rec, RCharTime));
            vm.S(snapshot, RWaitStart, vm.G(rec, RWaitStart));
            vm.S(snapshot, RWaits, vm.G(rec, RWaits));
            vm.Store(c, i.Args[1], vm.G(rec, RState));
            return 0;
        });
    }

    #endregion
}
