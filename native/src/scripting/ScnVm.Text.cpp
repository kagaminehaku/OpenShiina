// C# Scripting/ScnVm.Text.cs: the text system - 38 text records (0x4C7D28, 0x161C bytes each) that
// read a string with control codes ("_H17_X8_c255,255,255" ...) and draw it into a surface
// character by character. Records 18-37 are scratch copies used to measure a line before it is
// aligned. The records are kept in VM memory with the executable's layout, so copies, snapshots
// and field writes behave the same. Glyphs come from the platform (IScnFonts: GetGlyphOutline
// GGO_GRAY8_BITMAP) and are blended here exactly as FUN_004325A0 does.
//
// Ported from FUN_00432F00 (one step: control codes and one character), FUN_00431960 (the "_X"
// codes), FUN_004325A0 (draw a glyph), FUN_00434BA0 / 00434580 (measure a line for alignment),
// FUN_004317F0 (numbers in codes), FUN_00431730 (set the text), FUN_004343D0 (defaults). Opcodes:
// 0084 draw a string now, 0083 draw it over frames (task flag 8), 0096 select a record, 00A0 /
// 00A1 snapshot and redraw incrementally, 0066 read a character, 0078 / 0079 position, 00B4 /
// 00B6 layer and offset.
//
// Translations: a translated scenario's characters Shift-JIS lacks are gaiji (ScnGaiji), drawn
// with their own advances; START's breaks after so many characters inside a translated message
// are left out and the lines broken by their widths. Texts the scripts draw from their own code
// are drawn translated when strings.txt or the tables have the whole of them (Translated).
#include "scripting/ScnVm.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <format>
#include <stdexcept>

#include "text/Text.h"
#include "text/Unicode.h"
#include "util/Encoding.h"

namespace openshiina::scripting {

namespace {

/// While the player measures where the time goes (ScnVm::RoutineTimes): a part of the text's work
/// timed under its name, beside the embedded routines in the log of a slow stretch.
class TextTime
{
public:
    // (the names are made once: a std::string made from a name at every call, a heap block for the
    // longer ones, was a good part of what the timers measured on the Switch)
    TextTime(ScnVm& vm, const std::string& name) : m_times(vm.RoutineTimes), m_name(name)
    {
        if (m_times)
            m_start = std::chrono::steady_clock::now();
    }
    ~TextTime()
    {
        if (!m_times)
            return;
        auto& time = (*m_times)[m_name];
        time.Milliseconds += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - m_start).count();
        time.Calls++;
    }
    TextTime(const TextTime&) = delete;
    TextTime& operator=(const TextTime&) = delete;

private:
    std::unordered_map<std::string, ScnVm::RoutineTime>* m_times;
    const std::string& m_name;
    std::chrono::steady_clock::time_point m_start;
};

// Record fields (dword indices, as the decompiled code numbers them)
enum : int
{
    RIndex = 0, RFont = 1, RState = 2, RHeight = 3, RWidth = 4, RWeight = 7, RItalic = 8, RStrikeOut = 10, RQuality = 0xB,
    RBackSurface = 0x4C, RBkMode = 0x4D, RAlpha = 0x4E, RBkAlpha = 0x4F, RShadowAlpha = 0x50, RShadowX = 0x54, RShadowY = 0x55,
    REdgeX = 0x56, REdgeY = 0x57, RPtr = 0x58, RLeft = 0x59, RX = 0x5A, RY = 0x5B, RPrevX = 0x5C, RPrevY = 0x5D, RPitch = 0x5E,
    RLineHeight = 0x5F, RWidePitch = 0x60, RSpacing = 0x61, RWaits = 0x62, RCharTime = 0x63, RCharDelay = 0x64, RWaitStart = 0x65,
    RWait = 0x66, RRight = 0x67, RL = 0x68, RLineStart = 0x69, RIndent = 0x6A, RWrapped = 0x6B, RSkipKeys = 0x6C, RKinsoku = 0x6D,
    RZenkaku = 0x16E, RZenkakuSpace = 0x16F, RKatakana = 0x170, RAfterChar = 0x171, RBeforeChar = 0x172, REffects = 0x173,
    RFontDirty = 0x174, RAntialias = 0x175, RColorDirty = 0x176, RAlign = 0x17F, RAligned = 0x180, RMeasuring = 0x181,
    RFontMade = 0x182, RChar = 0x183, RRect = 0x184, RPixels = 0x188, RSurfaceWidth = 0x189, RSurfaceHeight = 0x18A,
    RSurfacePitch = 0x18B, RTextR = 0x18C, RTextG = 0x18D, RTextB = 0x18E, RDepth = 0x18F, RStack = 0x190, RCount = 0x578,
    RCountMark = 0x579, RMeasured = 0x57A, RLayer = 0x57B, RLayerIndex = 0x57C, RGlyphW = 0x57D, RGlyphH = 0x57E,
    ROffsetX = 0x57F, ROffsetY = 0x580, RGlyphsDirty = 0x581, RRubyW = 0x582, RRubyH = 0x583, RFixedW = 0x584, RFixedH = 0x585,
    RContext = 0x586,
};

// Byte offsets of strings and colours in a record
enum : int
{
    OFace = 0x30, OColor = 0x144, OEdgeColor = 0x147, OShadowColor = 0x14A, OBkColor = 0x14D, OKinsoku1 = 0x1B8, OKinsoku2 = 0x2B8,
    OKinsoku3 = 0x3B8, OIndents = 0x4B8,
};

constexpr int SnapshotRegion = 0x09100000, SnapshotSize = 0x2C48, SnapshotLimit = 0x2C44;
constexpr int MacroRegion = 0x09200000;
constexpr size_t MadeGlyphsMax = 16384;

// Half-width -> full-width Shift-JIS (0x485504): 256 for "_k", 256 for "_K" (katakana)
const openshiina::Bytes& Zenkaku()
{
    static const openshiina::Bytes table = Encoding::FromBase64(
        "QIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBSYFogZSBkIGTgZWBZoFpgWqBloF7gUOBfIFEgV6BT4JQ"
        "glGCUoJTglSCVYJWgleCWIJGgUeBg4GBgYSBSIGXgWCCYYJigmOCZIJlgmaCZ4JogmmCaoJrgmyCbYJugm+CcIJxgnKCc4J0gnWCdoJ3gniCeYJtgY+BboFPgVGBZYGBgoKC"
        "g4KEgoWChoKHgoiCiYKKgouCjIKNgo6Cj4KQgpGCkoKTgpSClYKWgpeCmIKZgpqCb4FigXCBYIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFA"
        "gUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUKBdYF2gUGBRYHwgp+CoYKjgqWCp4LhguOC5YLBgluBoIKigqSCpoKogqmCq4Ktgq+CsYKzgrWCt4K5gruCvYK/gsKCxILGgsiC"
        "yYLKgsuCzILNgtCC04LWgtmC3ILdgt6C34LgguKC5ILmgueC6ILpguqC64LtgvGCSoFLgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFA"
        "gUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFJgWiBlIGQgZOBlYFmgWmB"
        "aoGWgXuBQ4F8gUSBXoFPglCCUYJSglOCVIJVglaCV4JYgkaBR4GDgYGBhIFIgZeBYIJhgmKCY4JkgmWCZoJngmiCaYJqgmuCbIJtgm6Cb4JwgnGCcoJzgnSCdYJ2gneCeIJ5"
        "gm2Bj4FugU+BUYFlgYGCgoKDgoSChYKGgoeCiIKJgoqCi4KMgo2CjoKPgpCCkYKSgpOClIKVgpaCl4KYgpmCmoJvgWKBcIFggUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCB"
        "QIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQoF1gXaBQYFFgZKDQINCg0SDRoNIg4ODhYOHg2KDW4FBg0ODRYNHg0mDSoNMg06DUINSg1SDVoNY"
        "g1qDXINeg2CDY4Nlg2eDaYNqg2uDbINtg26DcYN0g3eDeoN9g36DgIOBg4KDhIOGg4iDiYOKg4uDjIONg4+Dk4NKgUuBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCB"
        "QIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgUCBQIFAgQ==");
    return table;
}

bool IsLead(int b) { return (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC); }

// 0xFB403C / 0xFA0F90 (FUN_00408EB0): for destination alpha da and source alpha sa (index da | sa
// << 8), the source's share of the result and the result's alpha
struct LayerTables
{
    uint8_t Share[0x10000], Alpha[0x10000];

    LayerTables()
    {
        for (int da = 0; da < 256; da++)
            for (int sa = 0; sa < 256; sa++)
            {
                int i = da | sa << 8;
                int v;
                if (da == 0)
                    v = 0xFF;
                else
                {
                    double a = sa * (1.0 / 255.0), b = da * (1.0 / 255.0);
                    double q = a / b;
                    v = int(std::trunc(q / (1.0 - a + q) * 255.0));
                    if (v >= 0x100)
                        v = 0xFF;
                }
                Share[i] = uint8_t(v);
                Alpha[i] = uint8_t(0xFF - (0xFF - da) * (0xFF - sa) / 0xFF);
            }
    }
};

const LayerTables& Layer()
{
    static const auto tables = std::make_unique<LayerTables>();
    return *tables;
}

} // namespace

int ScnVm::G(int rec, int field) { return Read32(rec + 4 * field); }
void ScnVm::S(int rec, int field, int value) { Write32(rec + 4 * field, value); }

void ScnVm::ResetRecord(int index)
{
    // FUN_004343D0: a record's defaults
    int rec = RecordAddress(index);
    FillMemory(rec, RecordSize, 0);
    m_fontCharSets.erase(rec);
    S(rec, RIndex, index);
    S(rec, RHeight, 0x10);
    S(rec, RWeight, 400);
    static const uint8_t face[] = {0x82, 0x6C, 0x82, 0x72, 0x20, 0x83, 0x53, 0x83, 0x56, 0x83, 0x62, 0x83, 0x4E, 0}; // ＭＳ ゴシック
    WriteBytes(rec + OFace, face, int(sizeof face));
    S(rec, RBackSurface, -1);
    S(rec, RBkMode, 1);
    S(rec, RAlpha, 0x100);
    S(rec, RBkAlpha, 0x100);
    S(rec, RShadowAlpha, 0x100);
    static const uint8_t white[] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
    WriteBytes(rec + OColor, white, 6);
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

int ScnVm::Translated(int text)
{
    // The text to draw for text: its translation in strings.txt (one original a line:
    // "original<tab>translation", UTF-8) or the tables' names.tsv and strings.tsv when the whole of
    // it is there, else itself
    if (!m_strings)
    {
        m_strings.emplace();
        openshiina::Bytes file;
        if (m_host.ReadLooseFile("translation\\strings.txt", file))
            ReadStrings(file);
        if (auto* tables = Tables())
            for (const auto& [original, translation] : tables->TranslatedStrings())
                AddString(original, translation);
        if (!m_strings->empty())
            if (IScnFonts* fonts = m_host.Fonts())
                fonts->Gaiji = &m_gaiji.Texts();
    }
    if (m_strings->empty() || text == 0)
        return text;
    int length = StringLength(text);
    if (length == 0 || length > 4096)
        return text;
    auto bytes = ReadBytes(text, length);
    auto found = m_strings->find(std::string(bytes.begin(), bytes.end()));
    if (found == m_strings->end())
        return text;
    if (found->second.second == 0)
    {
        openshiina::Bytes copy = found->second.first;
        copy.push_back(0);
        found->second.second = AllocateCopy(copy);
    }
    return found->second.second;
}

void ScnVm::ReadStrings(const openshiina::Bytes& file)
{
    size_t skip = ScnGaiji::IsUtf8(file) ? 3 : 0;
    std::string text = Text::CodePointsToUtf8(Text::Utf8ToCodePoints(std::string_view(reinterpret_cast<const char*>(file.data()) + skip, file.size() - skip)));
    for (size_t start = 0; start <= text.size();)
    {
        size_t end = text.find('\n', start);
        if (end == std::string::npos)
            end = text.size();
        std::string raw = text.substr(start, end - start);
        while (!raw.empty() && raw.back() == '\r')
            raw.pop_back();
        size_t tab = raw.find('\t');
        if (tab != std::string::npos && tab > 0 && raw[0] != '#')
            AddString(raw.substr(0, tab), raw.substr(tab + 1));
        start = end + 1;
    }
}

void ScnVm::AddString(const std::string& original, const std::string& translation)
{
    // A text the scripts may draw and its translation (left out when Shift-JIS cannot write the original)
    std::string bytes;
    for (char32_t c : Text::Utf8ToCodePoints(original))
    {
        int code = Text::Cp932Code(c);
        if (code < 0)
            return;
        if (code > 0xFF)
            bytes += char(code >> 8);
        bytes += char(code & 0xFF);
    }
    (*m_strings)[bytes] = {m_gaiji.Text(translation), 0};
}

void ScnVm::SetText(int rec, int x, int y, int text)
{
    // FUN_00431730: the record's text, optionally with a new position
    m_lastKind[size_t(G(rec, RIndex) % TextRecords)] = ScnGaiji::Kind::None;
    m_cellsOnLine[size_t(G(rec, RIndex) % TextRecords)] = 0;
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

void ScnVm::ClearGlyphs(int rec)
{
    // FUN_004314E0: forget the glyphs made with the record's font
    m_glyphs[size_t(G(rec, RIndex) % TextRecords)].clear();
}

bool ScnVm::DeleteFont(int font)
{
    // DeleteObject: false for a handle that is not a live font
    if (!m_liveFonts.erase(font))
        return false;
    m_fontRequests.erase(font);
    if (auto* fonts = m_host.Fonts())
        fonts->DeleteFont(font);
    return true;
}

void ScnVm::ReleaseFont(int rec)
{
    // FUN_00431550: delete the record's font and its glyphs
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

void ScnVm::CreateRecordFont(int rec)
{
    // CreateFontA with the record's fields (and FUN_004314E0 first)
    ClearGlyphs(rec);
    int length = 0;
    while (length < 31 && ReadByte(rec + OFace + length) != 0)
        length++;
    auto faceBytes = ReadBytes(rec + OFace, length);
    auto charSet = m_fontCharSets.find(rec);
    ScnFontRequest request{G(rec, RHeight), G(rec, RWidth), G(rec, 5), G(rec, 6), G(rec, RWeight), G(rec, RItalic), G(rec, 9),
                           G(rec, RStrikeOut), charSet != m_fontCharSets.end() ? charSet->second.first : 0x80, G(rec, RQuality),
                           charSet != m_fontCharSets.end() ? charSet->second.second : 1, std::string(faceBytes.begin(), faceBytes.end())};
    auto* fonts = m_host.Fonts();
    int font;
    {
        static const std::string name = "text: font made";
        TextTime time(*this, name);
        font = fonts ? fonts->CreateFont(request) : 0;
    }
    if (font == 0)
        font = 0x7F000000 + int(m_liveFonts.size());   // no font engine: a stand-in handle
    m_liveFonts.insert(font);
    m_fontRequests[font] = request;
    S(rec, RFont, font);
    S(rec, RFontDirty, 0);
    S(rec, RFontMade, 1);
    if (G(rec, RGlyphsDirty) != 0)
    {
        ClearGlyphs(rec);
        S(rec, RGlyphsDirty, 0);
    }
}

void ScnVm::RenewFont(int rec)
{
    // The recreate step of FUN_00432F00 / 00434580: DeleteObject the old font (a failure leaves
    // the record as it is) and CreateFontA a new one
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

int ScnVm::SelectedFont(int rec)
{
    // The font a device context draws with while the record's font is selected (SelectObject fails
    // for a dead one)
    int font = G(rec, RFont);
    return font != 0 && m_liveFonts.count(font) ? font : 0;
}

std::pair<int, int> ScnVm::Extent(int font, const uint8_t* text, size_t length)
{
    auto* fonts = m_host.Fonts();
    static const std::string name = "text: measured";
    TextTime time(*this, name);
    return fonts ? fonts->TextExtent(font, text, length) : std::pair(0, 0);
}

openshiina::Bytes ScnVm::CharBytes(int rec, int length) { return ReadBytes(rec + 4 * RChar, length); }

// ---- Numbers and strings in control codes ----

int ScnVm::ParseNumber(int rec)
{
    // FUN_004317F0: a decimal or hex number ("0x"), signed, ended by "," "." or a space
    int p = G(rec, RPtr);
    uint8_t ch = ReadByte(p++);
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
        uint8_t b;
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
            if (b < '0' || b > '9')
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
            if ((b < 'A' || b > 'F') && (b < 'a' || b > 'f'))
            {
                S(rec, RPtr, at);
                return sign * value;
            }
            b = uint8_t((b & 0xDF) - 7);
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

int ScnVm::NamedValue(int rec)
{
    // "{name}" in a number: the task's named variable (FUN_00401610 / 00434F60), then an optional ","
    int p = G(rec, RPtr) + 1, end = p;
    while (ReadByte(end) != 0 && ReadByte(end) != '}')
        end++;
    auto nameBytes = ReadBytes(p, end - p);
    std::string name(nameBytes.begin(), nameBytes.end());
    if (ReadByte(end) == '}')
        end++;
    S(rec, RPtr, end);
    int value = Read32(NamedAddress(m_slots.at(size_t(G(rec, RContext))), name));
    if (ReadByte(G(rec, RPtr)) == ',')
        S(rec, RPtr, G(rec, RPtr) + 1);
    return value;
}

void ScnVm::ReadField(int rec, int dst, int max)
{
    // Copies up to "/" (eaten) or the end of the text into dst, at most max bytes, NUL-terminated
    int n = 0;
    while (true)
    {
        int p = G(rec, RPtr);
        uint8_t b = ReadByte(p);
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

bool ScnVm::PrevIsComma(int rec) { return ReadByte(G(rec, RPtr) - 1) == ','; }

bool ScnVm::FindIn(int haystack, const openshiina::Bytes& needle)
{
    // FUN_00445B72 on a code page 932 system: strstr that steps whole characters
    int needleLength = int(std::find(needle.begin(), needle.end(), uint8_t(0)) - needle.begin());
    int last = haystack + StringLength(haystack) - needleLength;
    for (int p = haystack; ReadByte(p) != 0 && p <= last; p += IsLead(ReadByte(p)) && ReadByte(p + 1) != 0 ? 2 : 1)
    {
        int q = 0;
        if (ReadByte(p) != 0)
        {
            do
            {
                if (q >= needleLength || ReadByte(p + q) != needle[size_t(q)])
                    break;
                q++;
            } while (ReadByte(p + q) != 0);
        }
        if (q >= needleLength)
            return true;
    }
    return false;
}

// ---- Control codes (FUN_00431960) ----

void ScnVm::SwapRecords(int other, int rec)
{
    // "_n": the two records swap their glyphs and then all their bytes; the record of the text keeps
    // its place in it (pointer, position, waits, counts, line state) and draws its font again
    if (other == rec)
        return;
    size_t a = size_t(G(other, RIndex) % TextRecords), b = size_t(G(rec, RIndex) % TextRecords);
    std::swap(m_glyphs[a], m_glyphs[b]);
    std::swap(m_lastKind[a], m_lastKind[b]);
    std::swap(m_cellsOnLine[a], m_cellsOnLine[b]);
    std::swap(m_lineCells[a], m_lineCells[b]);
    auto bytesOther = ReadBytes(other, RecordSize);
    auto bytesRec = ReadBytes(rec, RecordSize);
    WriteBytes(other, bytesRec);
    WriteBytes(rec, bytesOther);
    // (the font's character set goes with the bytes)
    auto setOther = m_fontCharSets.find(other), setRec = m_fontCharSets.find(rec);
    std::optional<std::pair<int, int>> fromOther, fromRec;
    if (setOther != m_fontCharSets.end())
        fromOther = setOther->second;
    if (setRec != m_fontCharSets.end())
        fromRec = setRec->second;
    m_fontCharSets.erase(other);
    m_fontCharSets.erase(rec);
    if (fromRec)
        m_fontCharSets[other] = *fromRec;
    if (fromOther)
        m_fontCharSets[rec] = *fromOther;
    for (int field : {RLeft, RX, RY, RPtr, RWait, RState, RPrevX, RPrevY, RWaits, RMeasured, RCount, RKinsoku, RAligned})
        S(rec, field, G(other, field));
    S(rec, RFontDirty, 1);
    S(rec, RGlyphsDirty, 0);
}

void ScnVm::FontChanged(int rec)
{
    S(rec, RFontDirty, 1);
    S(rec, RGlyphsDirty, 1);
}

void ScnVm::UseColor(int rec, int offset)
{
    // SetTextColor: the colour the glyphs are blended with ([0x18C..0x18E] = R, G, B)
    S(rec, RTextR, ReadByte(rec + offset));
    S(rec, RTextG, ReadByte(rec + offset + 1));
    S(rec, RTextB, ReadByte(rec + offset + 2));
}

void ScnVm::ControlCode(int rec, int surface)
{
    int code = G(rec, RPtr);
    uint8_t op = ReadByte(code);
    uint8_t next = ReadByte(code + 1);
    S(rec, RPtr, code + 1);
    switch (op)
    {
    case '(':
    {
        int n = ParseNumber(rec);
        if (n >= 0 && n < 19)
        {
            m_savedX[n] = G(rec, RX);
            m_savedY[n] = G(rec, RY);
        }
        return;
    }
    case ')':
    {
        int n = ParseNumber(rec);
        if (n >= 0 && n < 19)
        {
            S(rec, RX, m_savedX[n]);
            S(rec, RY, m_savedY[n]);
        }
        return;
    }
    case 'A':
        S(rec, RZenkaku, 0);
        return;
    case 'B':
        S(rec, RBackSurface, ParseNumber(rec));
        return;
    case 'C':
    {
        // _Ccharset[,pitch] (v2.50): CreateFontA's character set and pitch and family for the
        // record's fonts ("_C,": 0; no pitch: 1)
        int charSet;
        if (ReadByte(G(rec, RPtr)) == ',')
        {
            S(rec, RPtr, G(rec, RPtr) + 1);
            charSet = 0;
        }
        else
            charSet = ParseNumber(rec);
        int pitch = ReadByte(G(rec, RPtr) - 1) == ',' ? ParseNumber(rec) : 1;
        m_fontCharSets[rec] = {charSet, pitch};
        S(rec, RFontDirty, 1);
        S(rec, RGlyphsDirty, 1);
        return;
    }
    case 'E':
        WriteByte(rec + OEdgeColor, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OEdgeColor + 1, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OEdgeColor + 2, uint8_t(ParseNumber(rec)));
        S(rec, REdgeX, ParseNumber(rec));
        S(rec, REdgeY, ParseNumber(rec));
        return;
    case 'F':
        S(rec, RFontDirty, 1);
        S(rec, RGlyphsDirty, 1);
        ReadField(rec, rec + OFace, 0xFE);
        return;
    case 'G':
        for (int i = 0x178; i <= 0x17E; i++)
            S(rec, i, ParseNumber(rec));
        return;
    case 'H':
        S(rec, RFontDirty, 1);
        S(rec, RGlyphsDirty, 1);
        S(rec, RHeight, ParseNumber(rec));
        return;
    case 'I':
        S(rec, RItalic, ParseNumber(rec));
        FontChanged(rec);
        return;
    case 'K':
        S(rec, RKatakana, 1);
        return;
    case 'L':
        S(rec, RL, ParseNumber(rec));
        return;
    case 'O':
        S(rec, RBkMode, 2);
        return;
    case 'P':
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
    case 'R':
    case 'Y':
        S(rec, RLineHeight, ParseNumber(rec));
        return;
    case 'S':
        if (next == 'a')
        {
            S(rec, RPtr, code + 2);
            S(rec, RShadowAlpha, ParseNumber(rec));
            return;
        }
        WriteByte(rec + OShadowColor, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OShadowColor + 1, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OShadowColor + 2, uint8_t(ParseNumber(rec)));
        S(rec, RShadowX, ParseNumber(rec));
        S(rec, RShadowY, ParseNumber(rec));
        if (PrevIsComma(rec))
            S(rec, RShadowAlpha, ParseNumber(rec));
        return;
    case 'T':
        S(rec, RBkMode, 1);
        return;
    case 'W':
        S(rec, RWait, ParseNumber(rec));
        S(rec, RWaitStart, int(Clock()));
        return;
    case 'X':
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
    case 'Z':
        S(rec, RZenkaku, 1);
        S(rec, RZenkakuSpace, 0);
        return;
    case 'a':
        S(rec, RAlign, ParseNumber(rec));
        if (G(rec, RMeasuring) != 0)
        {
            S(rec, RMeasuring, 0);
            return;
        }
        S(rec, RAligned, 0);
        AlignLine(rec, surface);
        return;
    case 'b':
        if (next == 'a')
        {
            S(rec, RPtr, code + 2);
            S(rec, RBkAlpha, ParseNumber(rec));
            return;
        }
        WriteByte(rec + OBkColor, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OBkColor + 1, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OBkColor + 2, uint8_t(ParseNumber(rec)));
        if (PrevIsComma(rec))
            S(rec, RBkAlpha, ParseNumber(rec));
        return;
    case 'c':
        if (next == 'a')
        {
            S(rec, RPtr, code + 2);
            S(rec, RAlpha, ParseNumber(rec));
            return;
        }
        WriteByte(rec + OColor, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OColor + 1, uint8_t(ParseNumber(rec)));
        WriteByte(rec + OColor + 2, uint8_t(ParseNumber(rec)));
        if (PrevIsComma(rec))
            S(rec, RAlpha, ParseNumber(rec));
        if (surface != -1)
            UseColor(rec, OColor);
        return;
    case 'd':
        S(rec, RGlyphW, ParseNumber(rec));
        if (PrevIsComma(rec))
            S(rec, RGlyphH, ParseNumber(rec));
        return;
    case 'e':
        S(rec, REffects, ParseNumber(rec));
        return;
    case 'f':
        S(rec, RWeight, ParseNumber(rec));
        FontChanged(rec);
        return;
    case 'g':
        S(rec, RLayer, ParseNumber(rec));
        if (PrevIsComma(rec))
            S(rec, RLayerIndex, ParseNumber(rec));
        return;
    case 'h':
        S(rec, RWidth, ParseNumber(rec));
        FontChanged(rec);
        return;
    case 'i':
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
    case 'k':
        S(rec, RKatakana, 0);
        return;
    case 'l':
        S(rec, RRight, ParseNumber(rec));
        return;
    case 'n':
        SwapRecords(RecordAddress(ParseNumber(rec) % TextRecords), rec);
        return;
    case 'o':
        S(rec, RStrikeOut, ParseNumber(rec));
        FontChanged(rec);
        return;
    case 'p':
        S(rec, RFixedW, ParseNumber(rec));
        if (PrevIsComma(rec))
            S(rec, RFixedH, ParseNumber(rec));
        return;
    case 'q':
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
    case 'r':
    {
        if (G(rec, RMeasuring) != 0)
        {
            S(rec, RMeasuring, 0);
            return;
        }
        // START's break after so many characters inside a translated message (not after its End):
        // the text system breaks those lines by their widths
        size_t index = size_t(G(rec, RIndex) % TextRecords);
        if (m_lastKind[index] == ScnGaiji::Kind::Letters || m_lastKind[index] == ScnGaiji::Kind::Space)
        {
            m_lineCells[index] = std::max(m_lineCells[index], m_cellsOnLine[index]);
            m_cellsOnLine[index] = 0;
            if (next == '!')
                S(rec, RPtr, code + 2);
            return;
        }
        m_cellsOnLine[index] = 0;
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
    }
    case 's':
        S(rec, RSkipKeys, ParseNumber(rec) & 0xFF);
        return;
    case 't':
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
            m_slots.at(size_t(slot)).TextRecord = G(rec, RIndex);
            if (G(rec, RMeasuring) == 0)
                RunSlotSync(slot);
        }
        return;
    case 'u':
    {
        m_rubyRecord = ParseNumber(rec);
        int ruby = RecordAddress(m_rubyRecord);
        S(ruby, RRubyW, ParseNumber(rec));
        S(ruby, RRubyH, ParseNumber(rec));
        return;
    }
    case 'w':
        S(rec, RCharDelay, ParseNumber(rec));
        S(rec, RCharTime, int(Clock()));
        return;
    case 'x':
        if (next == '-' || next == '+')
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
    case 'y':
        if (next == '-' || next == '+')
        {
            S(rec, RPtr, code + 2);
            S(rec, RY, G(rec, RY) + ParseNumber(rec));
            return;
        }
        S(rec, RY, ParseNumber(rec));
        return;
    case 'z':
        S(rec, RZenkaku, 1);
        S(rec, RZenkakuSpace, 1);
        return;
    }
}

// ---- Alignment (FUN_00434BA0 / 00434580) ----

void ScnVm::AlignLine(int rec, int surface)
{
    // "_a": measure the rest of the line in the scratch copy, then centre or right-align it
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
        S(rec, RX, G(rec, RLeft) - int(uint32_t(width) >> 1));
        break;
    case 2:
        S(rec, RX, G(rec, RLeft) - width);
        break;
    }
    S(rec, RAligned, 1);
}

void ScnVm::MeasureLine(int rec)
{
    // FUN_00434580: advance through the line without drawing, to its end ("_r") or the end of the text
    S(rec, RMeasuring, 1);
    while (true)
    {
        int at = G(rec, RPtr);
        uint8_t b = ReadByte(at);
        uint8_t next = ReadByte(at + 1);
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
        case '$':
        case '}':
            if (next != b)
                continue;
            literal = true;
            break;
        case '*':
            if (next == b)
            {
                literal = true;
                break;
            }
            PushMacro(rec);
            continue;
        case '+':
            if (next == b)
            {
                literal = true;
                break;
            }
            DefineMacro(rec);
            continue;
        case '@':
            if (next == b)
            {
                literal = true;
                break;
            }
            S(rec, RKatakana, G(rec, RKatakana) == 0 ? 1 : 0);
            continue;
        case '_':
            if (next == b)
            {
                literal = true;
                break;
            }
            ControlCode(rec, 0);
            if (G(rec, RMeasuring) == 0)
                return;
            continue;
        case '{':
            if (next == b)
            {
                literal = true;
                break;
            }
            S(rec, RPtr, at + 2);
            for (uint8_t c = next; c != '}';)
            {
                int p = G(rec, RPtr);
                c = ReadByte(p);
                S(rec, RPtr, p + 1);
            }
            continue;
        case '|':
            if (next == b)
            {
                literal = true;
                break;
            }
            S(rec, RZenkaku, G(rec, RZenkaku) == 0 ? 1 : 0);
            continue;
        case '~':
            if (next == b)
            {
                literal = true;
                break;
            }
            Tilde(rec, at);
            continue;
        }
        S(rec, RPtr, literal ? at + 1 : at);
        if (G(rec, RFontDirty) != 0)
            RenewFont(rec);
        int length = TakeChar(rec);
        int pitch = G(rec, RPitch);
        auto taken = CharBytes(rec, length);
        m_lastKind[size_t(G(rec, RIndex) % TextRecords)] = KindOf(taken, length);
        m_cellsOnLine[size_t(G(rec, RIndex) % TextRecords)]++;
        if (pitch == -1 || IsGaiji(taken, length))
        {
            int w = G(rec, RFixedW) != 0 && !IsGaiji(taken, length) ? G(rec, RFixedW) : Extent(SelectedFont(rec), taken.data(), taken.size()).first;
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

// ---- Macros and small codes shared by drawing and measuring ----

void ScnVm::PushMacro(int rec)
{
    // "*n": continue with macro n, coming back here at its end
    uint32_t n = uint32_t(ParseNumber(rec));
    if (n >= 0x101)
        return;
    int depth = G(rec, RDepth);
    if (depth < 1000)
        S(rec, RStack + depth, G(rec, RPtr));
    S(rec, RDepth, depth + 1);
    if (uint32_t(depth + 1) < 1000)
        S(rec, RPtr, MacroRegion + int(n) * 0x100);
}

void ScnVm::DefineMacro(int rec)
{
    // "+n text/": macro n is the text
    int n = ParseNumber(rec);
    if (n < 0 || n > 0x100)
        throw std::runtime_error("Text macro " + std::to_string(n) + " is outside the table");
    ReadField(rec, MacroRegion + n * 0x100, 0xFE);
}

void ScnVm::Tilde(int rec, int at)
{
    uint8_t sign = ReadByte(at + 1);
    if (sign == '-' || sign == '+')
    {
        S(rec, RPtr, at + 2);
        EngineGlobals[0x5E3B00] = EngineGlobal(0x5E3B00) + ParseNumber(rec);
    }
    else
        EngineGlobals[0x5E3B00] = ParseNumber(rec);
}

int ScnVm::TakeChar(int rec)
{
    // Puts the character at the text pointer into [RChar] (a half-width one made full-width in "_Z"
    // mode), moves past it and returns its length in bytes
    int p = G(rec, RPtr);
    uint8_t b = ReadByte(p);
    int length = IsLead(b) ? 2 : 1;
    S(rec, RChar, 0);
    WriteBytes(rec + 4 * RChar, ReadBytes(p, length));
    if (length == 1 && G(rec, RZenkaku) != 0 && (G(rec, RZenkakuSpace) != 0 || b != ' '))
    {
        int i = b + (G(rec, RKatakana) != 0 ? 0x100 : 0);
        WriteBytes(rec + 4 * RChar, &Zenkaku()[size_t(2 * i)], 2);
        length = 2;
    }
    S(rec, RPtr, p + (IsLead(b) ? 2 : 1));
    return length;
}

// ---- Drawing (FUN_00432F00) ----

void ScnVm::RubyStart(int rec)
{
    // "{": ruby over the text since "$": the place now kept, the ruby record ("_u") swapped in, and
    // its characters up to "}" spread over the text's width, raised by its height
    int width = (G(rec, RCount) - G(rec, RCountMark)) * G(rec, RPitch);
    EngineGlobals[0x5E3AAC] = G(rec, RX);
    EngineGlobals[0x5E3AF8] = G(rec, RY);
    SwapRecords(RecordAddress(m_rubyRecord), rec);
    S(rec, RX, EngineGlobal(0x5E3AA8));
    S(rec, RY, EngineGlobal(0x5E3AF4));
    int text = G(rec, RPtr), bytes = 0;
    while (ReadByte(text + bytes) != '}' && ReadByte(text + bytes) != 0)
        bytes++;
    int rubyW = G(rec, RRubyW), span;
    if (bytes == 2)
    {
        span = rubyW;
        S(rec, RPitch, 0);
    }
    else
    {
        span = rubyW * bytes / 2;
        if (width - span <= 0 || bytes / 2 == 0)
            S(rec, RPitch, rubyW / 2);
        else
        {
            int pitch = width / (bytes / 2) / 2;
            S(rec, RPitch, pitch);
            span = rubyW + (bytes / 2 - 1) * pitch * 2;
        }
    }
    S(rec, RX, G(rec, RX) + (width - span) / 2);
    S(rec, RWidePitch, G(rec, RPitch) << 1);
    S(rec, RY, G(rec, RY) - G(rec, RRubyH));
}

void ScnVm::TextStep(int rec, int surface, bool invalidate)
{
    // One step of a record's text (FUN_00432F00): control codes up to the next character, which is
    // drawn into surface (-1: none, only the codes take effect). Stops at the end of the text
    // (RState 0) or when a wait is due. invalidate: the surface is on screen
    AlignLine(rec, surface);
    S(rec, RState, 2);
    if (G(rec, RSkipKeys) != 0)
    {
        int keys = G(rec, RSkipKeys) & 0xFF, buttons = RepeatButtons(0x100, Buttons());
        if ((keys & 1) != 0 && (buttons & 0x20) != 0)
            S(rec, RWaits, 0);
        if ((keys & 2) != 0 && (buttons & 0x10) != 0)
            S(rec, RWaits, 0);
        if ((keys & 4) != 0 && (buttons & 0x100) != 0)
            S(rec, RWaits, 0);
        if ((keys & 8) != 0 && EngineGlobal(0x13B52B4) != 0)
            S(rec, RWaits, 0);
    }
    if (G(rec, RWaits) != 0 && G(rec, RWait) != 0)
    {
        if (uint32_t(int(Clock()) - G(rec, RWaitStart)) < uint32_t(G(rec, RWait)))
            return;
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
        uint8_t b = ReadByte(at);
        uint8_t next = ReadByte(at + 1);
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
        case '$':
            if (next == b)
            {
                literal = true;
                break;
            }
            EngineGlobals[0x5E3AA8] = G(rec, RX);
            EngineGlobals[0x5E3AF4] = G(rec, RY);
            S(rec, RCountMark, G(rec, RCount));
            continue;
        case '*':
            if (next == b)
            {
                literal = true;
                break;
            }
            PushMacro(rec);
            continue;
        case '+':
            if (next == b)
            {
                literal = true;
                break;
            }
            DefineMacro(rec);
            continue;
        case '@':
            if (next == b)
            {
                literal = true;
                break;
            }
            S(rec, RKatakana, G(rec, RKatakana) == 0 ? 1 : 0);
            continue;
        case '_':
            if (next == b)
            {
                literal = true;
                break;
            }
            ControlCode(rec, surface);
            if (G(rec, RWait) != 0)
                return;
            continue;
        case '{':
            if (next == b)
            {
                literal = true;
                break;
            }
            RubyStart(rec);
            continue;
        case '}':
            if (next == b)
            {
                literal = true;
                break;
            }
            SwapRecords(RecordAddress(m_rubyRecord), rec);
            S(rec, RX, EngineGlobal(0x5E3AAC));
            S(rec, RY, EngineGlobal(0x5E3AF8));
            continue;
        case '|':
            if (next == b)
            {
                literal = true;
                break;
            }
            S(rec, RZenkaku, G(rec, RZenkaku) == 0 ? 1 : 0);
            continue;
        case '~':
            if (next == b)
            {
                literal = true;
                break;
            }
            Tilde(rec, at);
            continue;
        }
        S(rec, RPtr, literal ? at + 1 : at);
        if (!DrawChar(rec, surface, invalidate))
            return;
    }
}

std::pair<int, int> ScnVm::FixedOr(int rec, int font, const openshiina::Bytes& ch, int length)
{
    if (G(rec, RFixedW) == 0)
        return Extent(font, ch.data(), size_t(length));
    if (!IsGaiji(ch, length))
        return {G(rec, RFixedW), G(rec, RFixedH)};
    // a translation's character takes its own width, in the fixed height
    return {Extent(font, ch.data(), size_t(length)).first, G(rec, RFixedH)};
}

int ScnVm::WordWidth(int rec, int font, const openshiina::Bytes& ch)
{
    // The width of the translated word that starts with ch: its letters up to a space, End or anything else
    int width = Extent(font, ch.data(), 2).first;
    for (int p = G(rec, RPtr);; p += 2)
    {
        auto next = ReadBytes(p, 2);
        if (KindOf(next, 2) != ScnGaiji::Kind::Letters)
            return width;
        width += Extent(font, next.data(), 2).first;
    }
}

int ScnVm::TranslationRight(int rec, int font)
{
    // The right edge of a translated line: START's characters a line (learned) times a full-width
    // character from the left, when that is inside the record's own edge
    static const uint8_t fullWidthSpace[] = {0x81, 0x40};
    int right = G(rec, RRight), cells = m_lineCells[size_t(G(rec, RIndex) % TextRecords)];
    if (cells <= 0)
        return right;
    int cell = G(rec, RFixedW) != 0 ? G(rec, RFixedW) : G(rec, RPitch) != -1 ? G(rec, RWidePitch) : Extent(font, fullWidthSpace, 2).first;
    return cell > 0 ? std::min(right, G(rec, RLeft) + cells * cell) : right;
}

bool ScnVm::DrawChar(int rec, int surface, bool invalidate)
{
    // The character part of FUN_00432F00; false when the step ends after it (a wait)
    uint32_t now = Clock();
    if (G(rec, RWaits) != 0 && G(rec, RCharDelay) != 0)
    {
        if (uint32_t(int(now) - G(rec, RCharTime)) < uint32_t(G(rec, RCharDelay)))
            return false;
        S(rec, RCharTime, int(Clock()));
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
    openshiina::Bytes ch = CharBytes(rec, 4);
    std::pair<int, int> ext{0, 0};
    size_t kindAt = size_t(G(rec, RIndex) % TextRecords);
    ScnGaiji::Kind kind = KindOf(ch, length);
    m_cellsOnLine[kindAt]++;
    if (kind == ScnGaiji::Kind::Letters && m_lastKind[kindAt] != ScnGaiji::Kind::Letters && dc && G(rec, RX) > G(rec, RLeft) + G(rec, RIndent)
        && uint32_t(G(rec, RX) + WordWidth(rec, font, ch)) > uint32_t(TranslationRight(rec, font)))
    {
        int oldX = G(rec, RX);
        S(rec, RX, G(rec, RLeft));
        S(rec, RPrevY, G(rec, RY));
        S(rec, RPrevX, oldX);
        S(rec, RY, G(rec, RY) + G(rec, RLineHeight));
        S(rec, RX, G(rec, RX) + G(rec, RIndent));
        S(rec, RLineStart, 1);
    }
    m_lastKind[kindAt] = kind;

    // Wrap before a character that would cross the right edge and may not end a line
    int x = G(rec, RX), pitch = G(rec, RPitch);
    uint32_t end;
    if ((pitch == -1 && dc) || IsGaiji(ch, length))
    {
        ext = FixedOr(rec, font, ch, length);
        end = uint32_t(x + ext.first);
    }
    else
    {
        if (length == 2)
            pitch = G(rec, RWidePitch);
        end = uint32_t(x + pitch + G(rec, RSpacing) * length);
    }
    if (uint32_t(G(rec, RRight)) <= end && FindIn(rec + OKinsoku2, ch))
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
            openshiina::Bytes following(4);
            int n = IsLead(ReadByte(p)) ? 2 : 1;
            auto read = ReadBytes(p, n);
            std::copy(read.begin(), read.end(), following.begin());
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
        m_slots.at(size_t(G(rec, RAfterChar))).TextRecord = G(rec, RIndex);
        RunSlotSync(G(rec, RAfterChar));
    }

    // Advance
    pitch = G(rec, RPitch);
    if ((pitch == -1 && dc) || IsGaiji(ch, length))
    {
        ext = FixedOr(rec, font, ch, length);
        S(rec, RX, G(rec, RX) + ext.first);
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
    if (uint32_t(G(rec, RRight)) <= uint32_t(G(rec, RX)))
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

void ScnVm::FindIndent(int rec, int font, bool dc)
{
    // With no indent yet: the first "_i/" string found in the rest of the text sets it
    int at = 1;
    while (ReadByte(rec + OIndents + at) != 0)
    {
        int s = rec + OIndents + at;
        int length = StringLength(s);
        auto needle = ReadBytes(s, length);
        int text = G(rec, RPtr), textLength = StringLength(text);
        auto haystack = ReadBytes(text, textLength);
        if (std::search(haystack.begin(), haystack.end(), needle.begin(), needle.end()) != haystack.end() || needle.empty())
        {
            int indent;
            if (G(rec, RPitch) == -1)
            {
                int w = G(rec, RFixedW) != 0 ? G(rec, RFixedW) : dc ? Extent(font, needle.data(), needle.size()).first : 0;
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

void ScnVm::DrawWithEffects(int rec, int surface, bool invalidate, int font, const openshiina::Bytes& ch, int length)
{
    // The drawing part of FUN_00432F00: background copy, shadow, edges, the character
    auto ext = FixedOr(rec, font, ch, length);
    int x = G(rec, RX), y = G(rec, RY);
    int left = x, top = y, right = x + ext.first, bottom = y + ext.second;
    int effects = G(rec, REffects);
    int sx = G(rec, RShadowX), sy = G(rec, RShadowY), ex = G(rec, REdgeX), ey = G(rec, REdgeY);
    if ((effects & 1) != 0)
    {
        left = std::min(left, sx + x);
        top = std::min(top, y + sy);
        right = std::max(right, right + sx);
        bottom = std::max(bottom, bottom + sy);
    }
    if ((effects & 0x1FE) != 0)
    {
        if ((effects & 0x40) != 0)
        {
            left -= ex;
            top -= ey;
        }
        if ((effects & 0x80) != 0)
            top -= ey;
        if ((effects & 0x100) != 0)
        {
            right += ex;
            top -= ey;
        }
        if ((effects & 0x10) != 0)
            left -= ex;
        if ((effects & 0x20) != 0)
            right += ex;
        if ((effects & 2) != 0)
        {
            left -= ex;
            bottom += ey;
        }
        if ((effects & 4) != 0)
            bottom += ey;
        if ((effects & 8) != 0)
        {
            right += ex;
            bottom += ey;
        }
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
        m_slots.at(size_t(slot)).TextRecord = G(rec, RIndex);
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
        if ((effects & 0x40) != 0)
            DrawGlyph(rec, surface, font, x - ex, y - ey, ch, length);
        if ((effects & 0x80) != 0)
            DrawGlyph(rec, surface, font, x, y - ey, ch, length);
        if ((effects & 0x100) != 0)
            DrawGlyph(rec, surface, font, ex + x, y - ey, ch, length);
        if ((effects & 0x10) != 0)
            DrawGlyph(rec, surface, font, x - ex, y, ch, length);
        if ((effects & 0x20) != 0)
            DrawGlyph(rec, surface, font, ex + x, y, ch, length);
        if ((effects & 2) != 0)
            DrawGlyph(rec, surface, font, x - ex, ey + y, ch, length);
        if ((effects & 4) != 0)
            DrawGlyph(rec, surface, font, x, ey + y, ch, length);
        if ((effects & 8) != 0)
            DrawGlyph(rec, surface, font, ex + x, ey + y, ch, length);
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
        DrawGlyph(rec, surface, font, x, y, ch, int(uint32_t(length) | 0x80000000u));
    }
    if (restore)
        UseColor(rec, OColor);
    DrawGlyph(rec, surface, font, x, y, ch, length);
    // InvalidateRect of the character's rectangle (FUN_00417560)
    if (invalidate && G(rec, RLayer) == -1)
    {
        InvalidateWindow(G(rec, RRect), G(rec, RRect + 1), G(rec, RRect + 2), G(rec, RRect + 3));
        FrameShown = true;
    }
}

void ScnVm::BitBlt(int dst, int x, int y, int width, int height, int src, int sx, int sy)
{
    // BitBlt SRCCOPY between two 24-bit surfaces, clipped to both
    if (src < 0 || src >= SurfaceCount)
        return;
    int dw = SurfaceField(dst, 7), dh = SurfaceField(dst, 8), sw = SurfaceField(src, 7), sh = SurfaceField(src, 8);
    if (SurfaceField(src, 2) == 0 || SurfaceField(dst, 2) == 0)
        return;
    if (x < 0)
    {
        sx -= x;
        width += x;
        x = 0;
    }
    if (y < 0)
    {
        sy -= y;
        height += y;
        y = 0;
    }
    if (sx < 0)
    {
        x -= sx;
        width += sx;
        sx = 0;
    }
    if (sy < 0)
    {
        y -= sy;
        height += sy;
        sy = 0;
    }
    width = std::min(width, std::min(dw - x, sw - sx));
    height = std::min(height, std::min(dh - y, sh - sy));
    if (width <= 0 || height <= 0)
        return;
    int dp = SurfaceField(dst, 10), sp = SurfaceField(src, 10);
    int d = SurfaceField(dst, 2) + y * dp + x * 3, s = SurfaceField(src, 2) + sy * sp + sx * 3;
    for (int row = 0; row < height; row++, d += dp, s += sp)
        WriteBytes(d, ReadBytes(s, width * 3));
}

void ScnVm::DrawGlyph(int rec, int, int font, int x, int y, const openshiina::Bytes& ch, int length)
{
    // FUN_004325A0 with antialiasing on ("_q+"): the GGO_GRAY8 glyph blended into the surface,
    // a = (coverage * 255 >> 6) * alpha >> 8, d += (c - d) * a >> 8 in 32-bit unsigned arithmetic.
    // Bit 31 of length draws every pixel as an (2 edge x + 1) x (2 edge y + 1) block (the "_e" 0x800
    // outline). Without antialiasing every pixel the glyph covers at least half is the text
    // colour, no alpha
    auto* fonts = m_host.Fonts();
    if (!fonts)
        return;
    static const std::string drawName = "text: glyph drawn", renderName = "text: glyph made";
    TextTime drawTime(*this, drawName);
    int count = length & 0x7FFFFFFF;
    bool solid = G(rec, RAntialias) == 0;
    if (G(rec, RBkMode) == 2)
        throw std::runtime_error("Opaque text background (_O) is not supported yet");

    int code = count == 1 ? int(uint16_t(int16_t(int8_t(ch[0])))) : ch[0] << 8 | ch[1];
    // v2.50: a character of 009C's pictures comes before the font's
    const ScnGlyph* glyph = Glyph250For(G(rec, RIndex), code);
    ScnGlyph made;
    if (!glyph)
    {
        auto& cache = m_glyphs[size_t(G(rec, RIndex) % TextRecords)];
        auto cached = cache.find(code);
        if (cached == cache.end())
        {
            TextTime renderTime(*this, renderName);
            auto request = m_fontRequests.find(font);
            if (request == m_fontRequests.end())
                made = fonts->Glyph(font, code);
            else
            {
                auto key = std::pair(request->second, code);
                auto known = m_madeGlyphs.find(key);
                if (known != m_madeGlyphs.end())
                    made = known->second;
                else
                {
                    if (m_madeGlyphs.size() >= MadeGlyphsMax)
                        m_madeGlyphs.clear();
                    made = fonts->Glyph(font, code);
                    m_madeGlyphs[key] = made;
                }
            }
            cached = cache.emplace(code, made).first;
        }
        glyph = &cached->second;
    }

    int ascent;
    auto request = m_fontRequests.find(font);
    if (request == m_fontRequests.end())
        ascent = fonts->Ascent(font);
    else
    {
        auto known = m_madeAscents.find(request->second);
        if (known != m_madeAscents.end())
            ascent = known->second;
        else
            ascent = m_madeAscents[request->second] = fonts->Ascent(font);
    }
    int alpha = G(rec, RAlpha);
    bool layer = G(rec, RLayer) != -1;
    if (layer)
    {
        x += G(rec, ROffsetX);
        y += G(rec, ROffsetY);
    }
    int gx = glyph->OriginX + x;
    int top = glyph->OriginY == int(0x80000000) ? y : ascent - glyph->OriginY + y;
    int right = glyph->BlackBoxX + gx, bottom = glyph->BlackBoxY + top;
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
    cr = std::min(cr, width);
    cb = std::min(cb, height);

    uint32_t firstCol = uint32_t(cl - gx), firstRow = uint32_t(ct - top);
    uint32_t endCol = uint32_t(glyph->BlackBoxX - right + cr), endRow = uint32_t(glyph->BlackBoxY - bottom + cb);
    if (endRow <= firstRow)
        return;
    uint32_t r = uint32_t(G(rec, RTextR)), g = uint32_t(G(rec, RTextG)), b = uint32_t(G(rec, RTextB));
    int pixels = G(rec, RPixels), pitch = G(rec, RSurfacePitch);
    int limit = pixels + pitch * height;

    int src0 = int(firstRow * uint32_t(glyph->Pitch) + firstCol);
    if (layer && (uint32_t(length) & 0x80000000u) == 0)
    {
        if (solid)
            throw std::runtime_error("Text without antialiasing (TextOutA) on a layer is not supported yet");
        DrawGlyphOnLayer(rec, *glyph, src0, cl, ct, firstRow, endRow, firstCol, endCol, alpha);
        return;
    }
    // The edge ("_E": each inked pixel drawn over the (2 ex + 1) x (2 ey + 1) around it)
    bool edged = (uint32_t(length) & 0x80000000u) != 0;
    int ex = edged ? G(rec, REdgeX) : 0, ey = edged ? G(rec, REdgeY) : 0;

    // FUN_004325A0's loop: the glyph's rows and columns, each inked pixel blended at the pen (or
    // over its edge), a byte that would leave the surface skipped
    auto drawWith = [&](auto&& blend) {
        int src = src0;
        int row = ct;
        for (uint32_t gy = firstRow; gy < endRow; gy++, src += glyph->Pitch, row++)
        {
            if (firstCol >= endCol)
                continue;
            int dst = pitch * row + cl * 3 + pixels;
            for (uint32_t col = 0; col < endCol - firstCol; col++, dst += 3)
            {
                size_t at = size_t(src) + col;
                uint8_t v = glyph->Bits.size() > at ? glyph->Bits[at] : uint8_t(0);
                if (v == 0)
                    continue;
                uint32_t a = solid ? (v >= 32 ? 256u : 0u) : (uint32_t(v) * 0xFF >> 6) * uint32_t(alpha) >> 8;
                if (a == 0)
                    continue;
                if (!edged)
                {
                    blend(dst, a);
                    continue;
                }
                int corner = dst + pitch * -ey + ex * -3;
                for (int dy = -ey; dy <= ey; dy++, corner += pitch)
                    for (int dx = -ex, q = corner; dx <= ex; dx++, q += 3)
                        blend(q, a);
            }
        }
    };
    // A byte at a time through the 4 GB (each byte looked up and checked, ~30 ns on the Switch: six
    // for a pixel, 150 with an edge of 2, most of 00A1's time there)
    auto slow = [&](int p, uint32_t a) {
        if (p < pixels || p + 3 > limit)
            return;
        uint32_t d0 = ReadByte(p), d1 = ReadByte(p + 1), d2 = ReadByte(p + 2);
        WriteByte(p, uint8_t(((b - d0) * a >> 8) + d0));
        WriteByte(p + 1, uint8_t(((g - d1) * a >> 8) + d1));
        WriteByte(p + 2, uint8_t(((r - d2) * a >> 8) + d2));
    };
    // The surface in place: the same bytes, written in the same order
    uint8_t* base = limit > pixels && IsContiguous(pixels, limit - pixels) ? Bytes(pixels, limit - pixels) : nullptr;
    auto fast = [&](int p, uint32_t a) {
        if (p < pixels || p + 3 > limit)
            return;
        uint8_t* d = base + (p - pixels);
        uint32_t d0 = d[0], d1 = d[1], d2 = d[2];
        d[0] = uint8_t(((b - d0) * a >> 8) + d0);
        d[1] = uint8_t(((g - d1) * a >> 8) + d1);
        d[2] = uint8_t(((r - d2) * a >> 8) + d2);
    };
    if (!base)
    {
        drawWith(slow);
        return;
    }
    if (!TextCheck)
    {
        drawWith(fast);
        return;
    }
    // TextCheck: drawn in place, kept, the surface's rows put back, drawn the old way, compared
    int rowLo = std::max(0, ct - ey - 1), rowHi = std::min(height, cb + ey + 1);
    int64_t lo = std::max<int64_t>(0, int64_t(pitch) * rowLo), hi = std::min<int64_t>(limit - pixels, int64_t(pitch) * rowHi);
    if (hi <= lo)
    {
        drawWith(fast);
        return;
    }
    openshiina::Bytes before(base + lo, base + hi);
    drawWith(fast);
    openshiina::Bytes drawn(base + lo, base + hi);
    std::memcpy(base + lo, before.data(), before.size());
    drawWith(slow);
    TextCheckGlyphs++;
    if (std::memcmp(base + lo, drawn.data(), drawn.size()) != 0)
    {
        size_t k = 0;
        while (k < drawn.size() && base[lo + int64_t(k)] == drawn[k])
            k++;
        TextCheckDiffered++;
        if (TextCheckDiffered <= 20)
            TextChecks.push_back(std::format("glyph {:04X}: the bytes drawn in place differ from offset {} of the surface", code,
                                             lo + int64_t(k)));
    }
}

void ScnVm::DrawGlyphOnLayer(int rec, const ScnGlyph& glyph, int src, int left, int top, uint32_t firstRow, uint32_t endRow,
                             uint32_t firstCol, uint32_t endCol, int alpha)
{
    // The layer branch of FUN_004325A0 ("_g"): the glyph over a 32-bit picture (alpha, blue, green,
    // red) with the tables above; full coverage writes the colour opaque
    const auto& tables = Layer();
    int picture = Picture(G(rec, RLayer));
    int frameIndex = G(rec, RLayerIndex);
    if (picture == 0 || uint32_t(frameIndex) > uint32_t(Read32(picture + 4)))
        return;
    int f = Read32(picture + 8 + 4 * frameIndex);
    if (f == 0)
        return;
    int width = Read32(f), height = Read32(f + 4);
    if (firstCol >= endCol)
        return;
    uint32_t r = uint32_t(G(rec, RTextR)), g = uint32_t(G(rec, RTextG)), b = uint32_t(G(rec, RTextB));
    uint32_t opaque = r << 24 | g << 16 | b << 8 | 0xFF;
    int columns = int(endCol - firstCol);
    // The columns inside the frame: the glyph was clipped to the text's surface, and a column outside
    // the frame would write into a row header
    int colFrom = std::max(0, -left), colTo = std::min(columns, width - left);
    if (colFrom >= colTo)
        return;
    // The frame's rows the glyph covers: (address of its column 0, the glyph's row), from the frame's
    // table of row addresses (f + 0x14, PicturePixel's), read at once
    // (kept from glyph to glyph: a heap block a glyph was a part of its time on the Switch)
    static const std::string rowsName = "text: layer rows", pixelsName = "text: layer pixels";
    auto& rows = m_layerGlyphRows;
    rows.clear();
    {
        TextTime rowsTime(*this, rowsName);
        int skip = std::max(0, -top);
        int first = top + skip, count = std::min(int(endRow - firstRow) - skip, height - first);
        if (count <= 0)
            return;
        int at = f + 0x14 + 4 * first;
        const uint8_t* table = IsContiguous(at, 4 * count) ? Bytes(at, 4 * count) : nullptr;
        src += skip * glyph.Pitch;
        for (int k = 0; k < count; k++, src += glyph.Pitch)
            rows.emplace_back((table ? int(LittleEndian::ToUInt32(table + 4 * k)) : Read32(at + 4 * k)) + 8 + left * 4, src);
    }
    // A row in place when it is one piece of the scripts' memory (the same bytes, in the same order,
    // as Read32 / Write32 a pixel at a time through the 4 GB, which was most of 00A1's time on the
    // Switch with a game that writes its text on layers)
    // A pixel d of the frame with the glyph's coverage a16 over it (not full: that is the colour opaque)
    auto blend = [&](uint32_t d, uint32_t a16) {
        uint32_t index = (d & 0xFF) | a16;
        uint32_t t = tables.Share[index];
        uint32_t rb = (d >> 8) & 0xFF00FF;
        return ((((((r << 16 | b) - rb) * t >> 8) + rb) & 0xFF00FF) * 0x100)
             + ((((g * 0x10000 - (d & 0xFF0000)) * t >> 8) + (d & 0xFF0000)) & 0xFF0000) + tables.Alpha[index];
    };
    auto drawRows = [&](bool inPlace) {
        for (const auto& [p, from] : rows)
        {
            // (row: the bytes of column colFrom on)
            uint8_t* row = inPlace && IsContiguous(p + colFrom * 4, (colTo - colFrom) * 4) ? Bytes(p + colFrom * 4, (colTo - colFrom) * 4) : nullptr;
            // The glyph's row as it is, when all of it is there
            const uint8_t* bits = size_t(from) + size_t(colTo) <= glyph.Bits.size() ? glyph.Bits.data() + from : nullptr;
            if (row && bits)
            {
                // Nearly always: both in place, nothing to check a pixel
                uint8_t* out = row;
                for (int col = colFrom; col < colTo; col++, out += 4)
                {
                    uint32_t v = bits[col];
                    if (v == 0)
                        continue;
                    uint32_t a16 = (v * 0xFF >> 6) * uint32_t(alpha) & 0xFF00;
                    LittleEndian::Pack(a16 == 0xFF00 ? opaque : blend(LittleEndian::ToUInt32(out), a16), out);
                }
                continue;
            }
            for (int col = colFrom; col < colTo; col++)
            {
                uint8_t v = bits ? bits[col] : glyph.Bits.size() > size_t(from) + size_t(col) ? glyph.Bits[size_t(from) + size_t(col)] : uint8_t(0);
                if (v == 0)
                    continue;
                uint32_t a16 = (uint32_t(v) * 0xFF >> 6) * uint32_t(alpha) & 0xFF00;
                uint32_t value = opaque;
                if (a16 != 0xFF00)
                    value = blend(row ? LittleEndian::ToUInt32(row + (col - colFrom) * 4) : uint32_t(Read32(p + col * 4)), a16);
                if (row)
                    LittleEndian::Pack(value, row + (col - colFrom) * 4);
                else
                    Write32(p + col * 4, int(value));
            }
        }
    };
    if (!TextCheck)
    {
        TextTime pixelsTime(*this, pixelsName);
        drawRows(true);
        return;
    }
    // TextCheck: drawn in place, kept, the rows put back, drawn the old way, compared
    std::vector<openshiina::Bytes> before, drawn;
    for (const auto& [p, from] : rows)
        before.push_back(ReadBytes(p, columns * 4));
    drawRows(true);
    for (const auto& [p, from] : rows)
        drawn.push_back(ReadBytes(p, columns * 4));
    for (size_t k = 0; k < rows.size(); k++)
        WriteBytes(rows[k].first, before[k]);
    drawRows(false);
    TextCheckGlyphs++;
    for (size_t k = 0; k < rows.size(); k++)
        if (ReadBytes(rows[k].first, columns * 4) != drawn[k])
        {
            TextCheckDiffered++;
            if (TextCheckDiffered <= 20)
                TextChecks.push_back(std::format("a glyph on a layer: the bytes drawn in place differ in its row at {:08X}", rows[k].first));
            break;
        }
}

// ---- Opcodes ----

void ScnVm::StepTaskText(ScnContext& c)
{
    // The flag-8 part of the main loop: one text step of a task's 0083 text
    int rec = CurrentRecord(c);
    int surface = c.TextSurface;
    S(rec, RContext, c.Slot);
    int at = G(rec, RPtr);
    TextStep(rec, surface >= 0 && surface < SurfaceCount ? surface : -1, surface == DisplaySurface());
    if (G(rec, RPtr) != at)
        m_textWentOn = true;
    if (G(rec, RState) == 0)
    {
        c.Flags &= ~8;
        m_textWentOn = true;
    }
}

void ScnVm::RegisterText()
{
    for (int i = 0; i < TextRecords; i++)
        ResetRecord(i);

    // 0096 n: the task's text record; its font is made again
    Register(0x0096, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        if (n < 0 || n >= TextRecords)
            return 2;
        c.TextRecord = n;
        int rec = vm.RecordAddress(n);
        vm.S(rec, RFontDirty, 1);
        vm.S(rec, RGlyphsDirty, 0);
        return 0;
    });
    // 0084 surface, text: lay out and draw the whole text now (-1: control codes only)
    Register(0x0084, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int surface = vm.Value(c, i.Args[0]), text = vm.Translated(vm.Value(c, i.Args[1]));
        int rec = vm.CurrentRecord(c);
        vm.SetText(rec, -1, -1, text);
        bool invalidate = surface == vm.DisplaySurface();
        if (surface < -1 || surface >= SurfaceCount)
            surface = -1;
        vm.S(rec, RContext, c.Slot);
        while (vm.G(rec, RState) != 0)
            vm.TextStep(rec, surface, invalidate);
        return 0;
    });
    // 0083 surface, text: draw the text over the next frames; the task waits (flag 8)
    Register(0x0083, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        c.TextSurface = vm.Value(c, i.Args[0]);
        int text = vm.Translated(vm.Value(c, i.Args[1]));
        int rec = vm.CurrentRecord(c);
        vm.SetText(rec, -1, -1, text);
        c.Flags |= 8;
        // Text started is text going on: the frame does not end before its first step
        vm.m_textWentOn = true;
        if ((vm.G(rec, RSkipKeys) & 8) != 0)
            vm.EngineGlobals[0x13B52B4] = 0;
        return 0;
    });
    // 0078 x, y: the text position (and the left margin)
    Register(0x0078, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
        int rec = vm.CurrentRecord(c);
        vm.S(rec, RX, x);
        vm.S(rec, RLeft, x);
        vm.S(rec, RY, y);
        return 0;
    });
    // 0079 x, y: where the text is
    Register(0x0079, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int rec = vm.CurrentRecord(c);
        vm.Store(c, i.Args[0], vm.G(rec, RX));
        vm.Store(c, i.Args[1], vm.G(rec, RY));
        return 0;
    });
    // 0066 v: the next character of the text (1 or 2 bytes), and move past it
    Register(0x0066, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int rec = vm.CurrentRecord(c);
        int p = vm.G(rec, RPtr);
        int length = IsLead(vm.ReadByte(p)) ? 2 : 1;
        int value = length == 2 ? vm.ReadByte(p) | vm.ReadByte(p + 1) << 8 : vm.ReadByte(p);
        vm.S(rec, RPtr, p + length);
        vm.Store(c, i.Args[0], value);
        return 0;
    });
    // 00B4 layer, index: text goes into a layer ("_g"); 00B6 x, y: offset there
    Register(0x00B4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int rec = vm.CurrentRecord(c);
        vm.S(rec, RLayer, vm.Value(c, i.Args[0]));
        vm.S(rec, RLayerIndex, vm.Value(c, i.Args[1]));
        return 0;
    });
    Register(0x00B6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int rec = vm.CurrentRecord(c);
        vm.S(rec, ROffsetX, vm.Value(c, i.Args[0]));
        vm.S(rec, ROffsetY, vm.Value(c, i.Args[1]));
        return 0;
    });
    // 008C / 008D (v2.49): the current record copied aside and back (one place for all records).
    // In v2.47 both are other opcodes, not done
    Register(0x008C, [](ScnVm& vm, ScnContext& c, const ScnInstruction&) {
        if (vm.EngineVersion < 248)
            throw vm.Error(c, "op_008C of v2.47 is not done");
        int rec = vm.CurrentRecord(c);
        size_t index = size_t(vm.G(rec, RIndex) % TextRecords);
        vm.ReadBytes(rec, vm.m_savedRecord.data(), RecordSize);
        vm.m_savedRecordState = {vm.m_lastKind[index], vm.m_cellsOnLine[index], vm.m_lineCells[index]};
        return 0;
    });
    Register(0x008D, [](ScnVm& vm, ScnContext& c, const ScnInstruction&) {
        if (vm.EngineVersion < 248)
            throw vm.Error(c, "op_008D of v2.47 is not done");
        int rec = vm.CurrentRecord(c);
        vm.WriteBytes(rec, vm.m_savedRecord);
        size_t index = size_t(vm.G(rec, RIndex) % TextRecords);
        std::tie(vm.m_lastKind[index], vm.m_cellsOnLine[index], vm.m_lineCells[index]) = vm.m_savedRecordState;
        // the record's font is back without going through its setting: no glyph of the other one stays
        vm.ClearGlyphs(rec);
        return 0;
    });
    // 00A0 text: set the text and keep a snapshot of the record to redraw from
    Register(0x00A0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int text = vm.Translated(vm.Value(c, i.Args[0]));
        int rec = vm.CurrentRecord(c);
        vm.SetText(rec, -1, -1, text);
        int snapshot = SnapshotRegion + c.TextRecord * SnapshotSize;
        vm.Write32(snapshot + SnapshotLimit, text);
        vm.CopyMemory(snapshot, rec, RecordSize);
        return 0;
    });
    // 00A1 slot, v (FUN_004157C0): redraw the snapshot's text into the slot's text surface - what was
    // shown before at once, then on with the waits; v = still running
    Register(0x00A1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int owner = vm.Value(c, i.Args[0]);
        int rec = vm.CurrentRecord(c);
        int snapshot = SnapshotRegion + c.TextRecord * SnapshotSize;
        vm.CopyMemory(rec, snapshot, RecordSize);
        vm.m_lastKind[size_t(vm.G(rec, RIndex) % TextRecords)] = ScnGaiji::Kind::None;
        vm.m_cellsOnLine[size_t(vm.G(rec, RIndex) % TextRecords)] = 0;
        int waits = vm.G(rec, RWaits);
        vm.S(rec, RContext, c.Slot);
        int surface = owner >= 0 && owner < Slots ? vm.m_slots[size_t(owner)].TextSurface : -1;
        if (surface < 0 || surface >= SurfaceCount)
            surface = -1;
        int limit = vm.Read32(snapshot + SnapshotLimit);
        if (uint32_t(vm.G(rec, RPtr)) < uint32_t(limit))
        {
            uint8_t saved = vm.ReadByte(limit);
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

} // namespace openshiina::scripting
