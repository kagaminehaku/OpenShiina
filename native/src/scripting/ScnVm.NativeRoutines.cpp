// C# Scripting/ScnVm.NativeRoutines.cs: the smaller embedded x86 routines written by hand -
// copies, strings, tables, checksums, and the pixel loops that are not zooms or blends (sepia,
// thumbnails, a same-size zoom). Each is found by the signature of the whole routine (one per
// build) and gives the bytes the x86 code gives; cases it does not take (blocks that overlap in a
// way the result depends on, counts the x86 code would wrap round to 4 billion) run as x86 code
// (RunX86). The builds differ by engine version (v2.47's routines end with "ret 4", v2.49's and
// v2.50's with "ret"); the offsets named are in the START.SCN of a game of that version (Oreimo
// Plus, Sena Plus, Re: Rem Plus) unless said otherwise.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <bit>

#include "text/Text.h"

namespace openshiina::scripting {

namespace {

struct Span64
{
    int64_t Start, Length;
};

/// Two blocks of memory share no byte.
bool Apart(int64_t a, int64_t aLength, int64_t b, int64_t bLength) { return a + aLength <= b || b + bLength <= a; }
bool Apart(Span64 a, Span64 b) { return Apart(a.Start, a.Length, b.Start, b.Length); }

/// The bytes rows of rowBytes at start + k * stride (k below rows) lie in.
Span64 RowsSpan(int start, int stride, int rows, int64_t rowBytes)
{
    int64_t first = start, last = start + int64_t(stride) * (rows - 1);
    int64_t low = std::min(first, last);
    return {low, std::max(first, last) + rowBytes - low};
}

uint32_t ByteSwap(uint32_t v) { return v << 24 | (v & 0xFF00) << 8 | (v >> 8 & 0xFF00) | v >> 24; }

bool IsLead(int b) { return (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC); }

/// a-z to A-Z (the x86 code compares signed bytes: 80-FF stay).
void UpperAscii(uint8_t* bytes, size_t length)
{
    for (size_t i = 0; i < length; i++)
        if (bytes[i] >= 0x61 && bytes[i] <= 0x7A)
            bytes[i] = uint8_t(bytes[i] - 0x20);
}

bool Contains(const openshiina::Bytes& haystack, const std::string& needle)
{
    return std::search(haystack.begin(), haystack.end(), needle.begin(), needle.end()) != haystack.end();
}

} // namespace

void ScnVm::RegisterNativeRoutines()
{
    // Aneiro START 88717: a lone "ret", called after each file the scripts load themselves
    RegisterNative("381CEBC19F264AFD8F65390B065E73D7B30254FC", "ret", [](ScnVm&, ScnContext&, const ScnNativeArgs&) {});

    // START 75A84 / 75EE4 / 85384: the CPU's features (CPUID: 1 Intel, 2 AMD, 10h MMX, 20h SSE, 40h
    // SSE2...) into l[0] and into the dword before the routine (gCPUID). The CPU the interpreter
    // reports (an Intel with MMX, no SSE) gives 0x11, so START takes its routines without SSE2. A
    // START that will not run without SSE2 is told SSE and SSE2 as well (0x71)
    RegisterNative({"B6FF80115E4E2727617C10E780653984637EDB0D", "9C94055F3764EB20CCEA440A6A01BE9363DBC93F"}, "cpuid",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       static const std::string noSse2 = Text::Utf8ToCp932("SSE2 をサポートしていません");
                       static const std::string noSse2Jp = Text::Utf8ToCp932("ＣＰＵが《ＳＳＥ２》に対応していません");
                       auto code = vm.ReadBytes(c.CodeBase, c.CodeSize);
                       int features = Contains(code, noSse2) || Contains(code, noSse2Jp) || Contains(code, "does not supported to SSE2") ? 0x71 : 0x11;
                       vm.SetL(a, 0, features);
                       vm.Write32(vm.m_nativeTarget - 4, features);
                   });

    // START 75B51 / 75FAF: the save thumbnail, 100 x 75 from an 800 x 600 BGR picture: each byte the
    // average of an 8 x 8 block. Ero-On! 5D7A0: 16 x 12 from blocks of 50 x 50. l[0] dst, l[4] src
    RegisterNative({"DCEE38C4FB3BC259CFB217ABD2D2F712B23BB57A", "1444F8C617F489497F12A55553446AC99F217C14"}, "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 8, 100, 75); });
    RegisterNative("1174F52A9A080EAEF9B086E491846FF3D5B35EB8", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 50, 16, 12); });
    // Can Fes! START 71211: 68 x 51 from blocks of 11 x 11
    RegisterNative("4D0B738FCFB0B45B214E4DFE0C8F5F46129F0B09", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 11, 68, 51); });
    // Ren'ai Saimin START 865B1: 84 x 63 from blocks of 9 x 9; Saimin Seikatsu START 72760: 76 x 56
    // from blocks of 10 x 10
    RegisterNative("B51FD501F22CF51D019EF26160C1C35D6154CB2C", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 9, 84, 63); });
    RegisterNative("13CE585B31BB34E5FA674268BD852030B126B3E6", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 10, 76, 56); });
    // Imouto Smile START 6D500: 80 x 60 from blocks of 10 x 10
    RegisterNative("98EF46C517E4F797E130C2D3003A186ACE788B2F", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 10, 80, 60); });
    // Shojo Mama START 6DF9F (Miko Dashi's 63DD0): 104 x 78 from blocks of 7 x 7; Bloody Rondo
    // START A224F: 244 x 183 from blocks of 3 x 3
    RegisterNative({"406D794CB83B2D60453AF6F8308F363D17998154", "E911B635E098CED69A383DEBA48A1FED4FFDA179"}, "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 7, 104, 78); });
    RegisterNative("B989AD7F42B182825C37945A4E8FDB2D4805E5F9", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 3, 244, 183); });
    // Cleavage START 2901E: 96 x 72 from blocks of 8 x 8, Azu Plus's code
    RegisterNative("EB31FA3CE0C4767074D322AC5A636A0B5C68D427", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 8, 96, 72); });
    // The rows of those thumbnails from l[0] (one after the other) to l[1], 2,400 bytes apart
    RegisterNative({"051CABA443EBCE28334111E6AF738B88F7FA7EB3", "C003D60A4E7F37520E7404C93B6592FCB4123B9A"}, "rows 104 x 78",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 1), 2400, vm.NativeArg(a, 0), 312, 78, 312); });
    RegisterNative("6BA61D3CAF2525A790BFE440BE6267D30CD8BC65", "rows 244 x 183",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 1), 2400, vm.NativeArg(a, 0), 732, 183, 732); });
    // Cleavage 29313: 72 rows of 288 bytes
    RegisterNative("C4E2EF445D41ADB0A605D700A9FD74D4FD8C18F7", "rows 96 x 72",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 1), 2400, vm.NativeArg(a, 0), 288, 72, 288); });
    // Doushite Daite Kurenai no!? START 8822F: 232 x 174 from blocks of 3 x 3
    RegisterNative("EFC1A7C2555C74261AD0BADB864569B4BF210B9D", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 3, 232, 174); });
    // Chuuchuu Nurse START 70036AF3 / 70036B67: 160 x 120 from blocks of 5 x 5, and 400 x 300 from 2 x 2
    RegisterNative("42515BD6BA1FCC24A41CA6A734692CC90E6577DB", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 5, 160, 120); });
    RegisterNative("2853439284672B39391C2DEA48F204032D81382A", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 2, 400, 300); });
    // Gensou no Idea START A3F6F (1280 x 720): 192 x 108 from blocks of 6 x 6, rows of 3,840 bytes
    RegisterNative("A358C4BDF44489F7DFB8A57F3BF7D3B7E63A31FF", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 6, 192, 108, 3840); });
    // Gensou no Idea F5440 (and others' padding): l[3] dwords from l[4] to l[0], each XORed with the
    // byte l[5] in all four bytes; a count under 1 runs as x86 code
    RegisterNative({"304020D68894542344EA2C8F8D67586EF480BE6F", "EC6DD9EC7E160D8BA47F26DB38B9A688C63951ED",
                    "1C6C23B7891138CF24BA9870804BA7213B69A774"},
                   "xor dwords", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), count = vm.NativeArg(a, 3), src = vm.NativeArg(a, 4);
                       uint32_t key = uint32_t(vm.NativeArg(a, 5));
                       key |= key << 8;
                       key |= key << 16;
                       if (count <= 0 || count > 0x4000000)
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       if (dst == src || Apart(dst, count * int64_t(4), src, count * int64_t(4)))
                           // a dword is read before it is written: in place too
                           vm.MapDwords(dst, src, count * 4, [key](uint32_t x) { return x ^ key; });
                       else
                           for (int i = 0; i < count; i++)
                               vm.Write32(dst + 4 * i, vm.Read32(src + 4 * i) ^ int(key));
                   });
    // Ao no Juuai 188BE (v2.34): the same 100 x 75 thumbnail, f[0] src, f[1] dst (Boku Igai: "ret")
    RegisterNative({"FBBBFACF8D7BA58AC19D6E3581A07D1C7E6E96BB", "90D9CC3BB0ED87CFB81342B8E2A4EB00FDF1AB18"}, "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.ThumbnailAt(c, a, 8, 100, 75, vm.Read32(a.F + 4), vm.Read32(a.F)); });
    // Ran→Sem 6741B: the same, its rows 2,400 bytes apart; Boku Igai 58AB1 ("ret")
    RegisterNative({"D9ABA7030C439C1EE095B165A3AE1098350BD572", "DDCBD9B3052D1E5DBBEE7D6C0400BCA5684C7B5C"}, "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       vm.ThumbnailAt(c, a, 8, 100, 75, vm.Read32(a.F + 4), vm.Read32(a.F), 2400, 2400);
                   });
    // Ayakashibito START2 12B77 (v2.34): 80 x 60 from blocks of 10 x 10, the same arguments
    RegisterNative("D9036751E14B2CD371970392C16FD29EB60CF5A8", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.ThumbnailAt(c, a, 10, 80, 60, vm.Read32(a.F + 4), vm.Read32(a.F)); });

    // The Pillagers of Raillore START 3DAE54 / 3DAEA7 (SSE2): a 960 x 540 / 640 x 360 32-bit picture
    // enlarged two / three times into 1920 x 1080. l[0] dst, l[1] its stride, l[2] src, l[3] its stride
    RegisterNative("1A0CA904E74263F80CC1A2BBAC2E6AF86C1C838C", "enlarge x2",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeWhole(c, a, 2, 960, 540); });
    RegisterNative("F96B154D20F92ABAA59589044921B28E7DADBAE9", "enlarge x3",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeWhole(c, a, 3, 640, 360); });
    // Raillore 3DAFE3: 480 x 270 four times; 3D9DFC (MMX): 1280 x 720 into 1920 x 1080, each 2 x 2
    // pixels into 3 x 3
    RegisterNative("5D890BED7B136019B7D8848E93992501D2519721", "enlarge x4",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeWhole(c, a, 4, 480, 270); });
    RegisterNative("11726E4688DF316677B49171683930D9ED17D545", "enlarge 3:2",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeThreeHalves(c, a, 640, 360); });
    // Nukige Mitai na Shima ni Sunderu 1 and 2: their pictures made 1600 x 900
    RegisterNative("4D34AEE67A84463DE527AA823BF6703BAFC5F16E", "enlarge x2",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeWhole(c, a, 2, 800, 450); });
    RegisterNative("EBAD70D00F39BA77E3B4272838E6A6DF725D5EE5", "enlarge x4",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeWhole(c, a, 4, 400, 225); });
    RegisterNative("45C0801C7C075557BECA31CE724D5A3899BA3671", "enlarge x3",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeWhole(c, a, 3, 532, 300, 4, 16); });
    RegisterNative("6E629A2EDD2C4A7A623FDAA306E08BCDDB859BE9", "enlarge 3:2",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeThreeHalves(c, a, 533, 300, 2, 4); });
    // Nukitashi's save thumbnail: CCA40, 233 x 131 made 32-bit; D343C, its mask, 44 x 44 of a
    // picture 1600 wide; D3590, the frame of a 348 x 208 picture cleared; D3644, the first byte of
    // each of its pixels times l[2], / 256 (l[0], rows l[1] bytes apart)
    RegisterNative("2034E898B55E6D1D2BA78066F7C3EECB13900E83", "24 to 32 bits",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.To32Bits(c, a, 233, 131); });
    RegisterNative("0CE30104B494F4C8DAC2285DDD309A45CA43381C", "mask44",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Samples(c, a, 44, 4800); });
    RegisterNative("37DC5CFF62785C36A6B9C66344AB58AAE8861E43", "clear frame",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.ClearFrame(c, a); });
    RegisterNative("0DD98CFE48694D28F52A04E8BE4C8E5B2A312B50", "scale first bytes", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        constexpr int Width = 348, Height = 208;
        int dst = vm.NativeArg(a, 0), stride = vm.NativeArg(a, 1);
        uint32_t factor = uint32_t(vm.NativeArg(a, 2));
        if (stride < Width * 4)
        {
            vm.RunX86(c, a);
            return;
        }
        for (int y = 0; y < Height; y++)
        {
            uint8_t* row = vm.Bytes(dst + y * stride, Width * 4);
            for (int x = 0; x < Width * 4; x += 4)
                row[x] = uint8_t(row[x] * factor >> 8);
        }
    });
    // Raillore 3D243F: the 750 x 420 thumbnail of a 1920-pixel wide 24-bit picture, each byte the
    // average of 2 x 2; 3D24B7: 420 rows of 2,250 bytes from l[4] (rows 5,760 bytes apart) to l[0]
    // one after the other; 3D2765: the other way, from l[0] to l[1], rows l[2] bytes apart
    RegisterNative("A15FAA3A37D529AE5C73C12F7D5647E0F9DBF7ED", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 2, 750, 420, 5760); });
    RegisterNative("EDC2AAF8FECE0B007FE9E8E6A84DB7491C835855", "rows 750 x 420",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 0), 2250, vm.NativeArg(a, 4), 5760, 420, 2250); });
    RegisterNative("9AAFA46F7AE7F2DC37E76C780EDEC870012BB309", "rows 750 x 420", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        vm.CopyRows(c, a, vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 0), 2250, 420, 2250);
    });
    // Raillore 3D27BC: 750 x 420 pixels of 24 bits into 32 bits; Oni ga Kuru CD7A0: 238 x 133;
    // Doushite Daite Kurenai no!? 8855E: 232 x 174
    RegisterNative("BB68DCB924B8DB5AEF56E5452A05737BD1DF9BFA", "24 to 32 bits",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.To32Bits(c, a, 232, 174); });
    RegisterNative("4E7AFE31B6653D60E34B305DB2A5E68023317F69", "24 to 32 bits",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.To32Bits(c, a, 750, 420); });
    RegisterNative("62C3E0DAEA9FBD9D4945C17A3F12DBC00A96F7FA", "24 to 32 bits",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.To32Bits(c, a, 238, 133); });
    // The save thumbnails of the 1280 x 720 games (rows of 3,840 bytes): Shinigami no Testament
    // 116 x 65 from 11 x 11; Tojita Sekai no Tori Colony 240 x 135 from 5 x 5; Love & Piss 232 x 130
    RegisterNative("F9C7EC158AD7FFF292E04686FA8D4254D94CEFF1", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 11, 116, 65, 3840); });
    RegisterNative("75728C1CF427254DADD9B1E12944A9352FA74A7B", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 5, 240, 135, 3840); });
    RegisterNative("5AFCED6431EF9C381EDAAE59D1055B5F3DE655F7", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 5, 232, 130, 3840); });
    // Rows of a thumbnail from l[0] (one after the other) to l[1]
    RegisterNative("703F2D11977DBC27CBAF7A16F1EC6D9A2212CB62", "rows",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 1), 3840, vm.NativeArg(a, 0), 348, 65, 348); });
    RegisterNative("21DFA52521953EAFA0CFA2FED49D420C0D21CF52", "rows",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 1), 3840, vm.NativeArg(a, 0), 720, 135, 720); });
    RegisterNative("E0E81F9785A0F274BCC8FD6AB2B644AF349B9ADC", "rows", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        vm.CopyRows(c, a, vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 0), 720, 135, 720);
    });
    RegisterNative("1B4BCE0BEC3B4D1CD9E7C9606521224E6C26C6B4", "rows",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 1), 3072, vm.NativeArg(a, 0), 450, 84, 450); });
    // Gohoushi Nurse 86C14 and Lovecha 89924: 99 rows of 528 bytes, 3,840 apart
    RegisterNative("16F409F8E433571CD49C781CBDA5B49C512E8D1A", "rows",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.CopyRows(c, a, vm.NativeArg(a, 1), 3840, vm.NativeArg(a, 0), 528, 99, 528); });
    // Testament 827CD: l[2] bytes from l[1] to l[0], each dword inverted - eight bytes a step when
    // l[3] is 0, else four. A count of 0 runs as x86 code
    RegisterNative("2E87AFFFFF1105B79D31E2277BFF3CB1DA38B2E8", "copy inverted", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
        uint32_t bytes = uint32_t(vm.NativeArg(a, 2));
        int64_t words = vm.NativeArg(a, 3) == 0 ? int64_t(bytes >> 3) * 2 : int64_t(bytes >> 2);
        if (words == 0 || words > 0x4000000 || (dst != src && !Apart(dst, words * 4, src, words * 4)))
        {
            vm.RunX86(c, a);
            return;
        }
        auto data = vm.ReadBytes(src, int(words) * 4);
        for (auto& b : data)
            b = uint8_t(~b);
        vm.WriteBytes(dst, data);
    });
    // Kanojo ga Ore ni Kureta Mono 970DC: 22 x 22 bytes to l[1], each the first byte of a pixel of
    // the 24-bit picture at l[0] (rows of 3,072 bytes) * 64 / 255
    RegisterNative("697ECC2DF28991A03AF2B0849DD0BB1CFEC99854", "mask 22 x 22", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        constexpr int Side = 22, Stride = 3072;
        int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1);
        if (!Apart(dst, Side * Side, src, Stride * (Side - 1) + Side * 3))
        {
            vm.RunX86(c, a);
            return;
        }
        openshiina::Bytes output(Side * Side), row(Side * 3);
        for (int y = 0; y < Side; y++)
        {
            vm.ReadBytes(src + y * Stride, row.data(), int(row.size()));
            for (int x = 0; x < Side; x++)
                output[size_t(y * Side + x)] = uint8_t((row[size_t(3 * x)] << 6) / 255);
        }
        vm.WriteBytes(dst, output);
    });
    // Hana to Otome ni Shukufuku o Royal Bouquet 4CAEC, Tekoire Princess! 2FEBC, Imouto Smile 6D5EC,
    // Sadistic Little 5AE8C, Yokorenbo 2B5CF: dwords at l[1] XORed with a key stream from l[0]
    RegisterNative("B4937B7F8A71BA951426F3E44AC8D0ED313E669A", "xor stream",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.XorStream(c, a, 0xD9F44); });
    RegisterNative("9598BC877F66A2208AD03940D710BD0620B464F4", "xor stream",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.XorStream(c, a, 0x1A7518); });
    RegisterNative("A470F7A6AA684E2AF9F1EBF445D60C63C4269657", "xor stream",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.XorStream(c, a, 0x141C20); });
    RegisterNative("6CDD92F21FABE3FBF5353F8D5A210AED96E9EF86", "xor stream",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.XorStream(c, a, 0x69BE0); });
    RegisterNative("1EB7C3A5BABAEC65C179ACD912C8032A77D690BC", "xor stream",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.XorStream(c, a, 0x6089C); });
    // Raillore EFCLIB 92F0 (Tori Colony 666C): l[1] dwords at l[0] inverted; a count under 1 runs as x86
    RegisterNative({"F79705977D2B17A02E3A733E04EF5BA87A031A92", "C4E79AD74DF00F25B64339C72CF808E9E7627276"}, "invert dwords",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int at = vm.NativeArg(a, 0), count = vm.NativeArg(a, 1);
                       if (count <= 0 || count > 0x4000000)
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       uint8_t* bytes = vm.Bytes(at, count * 4);
                       for (int i = 0; i < count * 4; i++)
                           bytes[i] ^= 0xFF;
                   });
    // Raillore 3D9CA7: the first byte of each pixel of 1920 x 1080 at l[0] times (the second byte of
    // l[1]'s pixel + 1), >> 8; both rows l[2] bytes apart
    RegisterNative("B830BE90B783CA51533E8B9E1E52F05F6CBCB9C5", "mask byte 0", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        constexpr int Width = 1920, Height = 1080, RowBytes = Width * 4;
        int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1), stride = vm.NativeArg(a, 2);
        auto span = RowsSpan(dst, stride, Height, RowBytes);
        if (std::abs(int64_t(stride)) < RowBytes || (dst != src && !Apart(span, RowsSpan(src, stride, Height, RowBytes))))
        {
            vm.RunX86(c, a);
            return;
        }
        openshiina::Bytes mask(RowBytes);
        for (int y = 0; y < Height; y++, dst += stride, src += stride)
        {
            // (the second bytes are read before the first ones are written: in place too)
            vm.ReadBytes(src, mask.data(), RowBytes);
            uint8_t* row = vm.Bytes(dst, RowBytes);
            for (int x = 0; x < RowBytes; x += 4)
                row[x] = uint8_t(row[x] * (mask[size_t(x + 1)] + 1) >> 8);
        }
    });
    // Raillore 39A40C: the width of the string at l[0] - each byte's width from a table of 256, "[" /
    // "{" choosing l[1]'s / l[3]'s table, "]" / "}" l[2]'s again, a lead byte l[4] for its two bytes;
    // l[0] = the width, l[1] = the table in use at the end
    RegisterNative("6257404FB455FA61F6AEEC6B7E158251BF6C0B06", "text width", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int p = vm.NativeArg(a, 0), table = vm.NativeArg(a, 2), wide = vm.NativeArg(a, 4), width = 0;
        for (int b; (b = vm.ReadByte(p)) != 0;)
        {
            if (b == 0x5B)
            {
                table = vm.NativeArg(a, 1);
                p++;
            }
            else if (b == 0x5D || b == 0x7D)
            {
                table = vm.NativeArg(a, 2);
                p++;
            }
            else if (b == 0x7B)
            {
                table = vm.NativeArg(a, 3);
                p++;
            }
            else if (IsLead(b))
            {
                width += wide;
                p += 2;
            }
            else
            {
                width += vm.ReadByte(table + b);
                p++;
            }
        }
        vm.SetL(a, 0, width);
        vm.SetL(a, 1, table);
    });

    // Ao no Juuai 7F562 (v2.34): l[0] = the index of the first word l[1] in the string of words at
    // l[0] (before its 0), else -1. Boku Igai 69231: "ret"
    RegisterNative({"974E8152ACDBC5F4413632178D75F40431C4A296", "D9C263218CAF74B4CC2C4D3B24E32216D31F01FC"}, "wcsindex",
                   [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
                       int start = vm.NativeArg(a, 0), result = -1;
                       uint16_t ch = uint16_t(vm.NativeArg(a, 1));
                       for (int p = start;; p += 2)
                       {
                           uint16_t w = vm.Read16(p);
                           if (w == 0)
                               break;
                           if (w == ch)
                           {
                               result = int(uint32_t(p - start) >> 1);
                               break;
                           }
                       }
                       vm.SetL(a, 0, result);
                   });

    // START 75E25 / 76273 (Ero-On! 5DA73): 600 rows of 2400 bytes from l[1] (one after the other) to
    // l[0], rows 2404 bytes apart
    RegisterNative({"F1057FFC43DC4027C0B8A90D039081D9AB4A4FF0", "87545B986579B5F66308666715D8D25592FF6D1D"}, "rows600",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
                       constexpr int Rows = 600, RowBytes = 2400, Stride = 2404;
                       auto dstSpan = RowsSpan(dst, Stride, Rows, RowBytes);
                       if (!Apart(dstSpan, {src, int64_t(Rows) * RowBytes}))
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       for (int y = 0; y < Rows; y++)
                           vm.MoveMemory(dst + y * Stride, src + y * RowBytes, RowBytes);
                   });

    // START 75EC3 / 76311: 75 rows of 300 bytes from l[0] (one after the other) to l[1], rows 2400
    // bytes apart; Ero-On! 5DB11: 12 rows of 48 bytes; Re: Rem Plus 85674: 90 rows of 480 bytes, 3840
    // apart; Kuroneko Plus 7637C (and the v2.49 games after it): 75 rows of 300 bytes, l[2] apart
    RegisterNative({"EF78DA983F119F2B9CC9911836AC09DCBD855592", "674C88AE8D38EC510159CD09D1D58E75E64EABA9"}, "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 75, 300, 2400); });
    RegisterNative("9721452713208958DEE71DA51F85540180359028", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 12, 48, 2400); });
    RegisterNative("C4839FCEF4CF13538D043A595B66E1D7B9B04E21", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 90, 480, 3840); });
    RegisterNative("0D838E63CAFB1E7CC81D190CFCDE10611E8598BA", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 75, 300, vm.NativeArg(a, 2)); });
    // Imouto Smile START 6D8C0: 60 rows of 240 bytes, l[2] apart
    RegisterNative("BEEA1D31822C5C23C3EACC3DBE9424F89897724B", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 60, 240, vm.NativeArg(a, 2)); });
    // Chiccha na Hanayome, Doushite Daite Kurenai no!?, Love & Piss
    RegisterNative("D8A858C959C1C8906AF1F02A9BF4F2B2AC6940E4", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 174, 696, 2400); });
    RegisterNative("D9795CF83BFC96E0F066C4BFA6DF8DA484186E3D", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 130, 696, 3840); });
    RegisterNative("8477782400863E751B269B913DC8D4319F6F5BDF", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 75, 396, 3840); });
    RegisterNative("1253D6DD445B3CE501A06A6ED7C8F50CC32F44BF", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 75, 396, vm.NativeArg(a, 2)); });
    // Can Fes! START 71583: 51 rows of 204 bytes, 2400 apart
    RegisterNative("1249F52FDF84986E2917C68EBA9DC210A93F1B1C", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 51, 204, 2400); });
    // Bitch Nee-chan C11C4: 128 rows of 684 bytes, 3840 apart; Bitch Gakuen C3724: 128 rows of 681
    RegisterNative("796FFAA089B4BBA15CDDB190D35F8529B95949A7", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 128, 684, 3840); });
    RegisterNative("EC35C823FB602E8AA31C44D28AE1C1FD15808C7B", "block",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlockCopy(c, a, 128, 681, 3840); });

    // Bitch Nee-chan C7C2C (and others): 25 x 25 samples of the first byte of 24-bit pixels from
    // l[0] (rows 3840 bytes apart) to l[1], each b * 64 / 255. Chiccha na Hanayome C04AC: 26 x 26
    RegisterNative("36FEBAB2B4673E7C14FF5B69625F879475C6F6AE", "samples25",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Samples(c, a, 25); });
    RegisterNative("06344B77ECB765B91AC2094D95D2976F0E228EE7", "samples26",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Samples(c, a, 26); });

    // Bitch Gakuen 2B8BC (Bitch Shimai 2B70C): strrchr for Shift-JIS text: l[0] = the address of the
    // last byte l[2] in the string at l[1], lead bytes taking their next byte with them, else 0
    RegisterNative("B15B347864344219C0D7C41EFA26FB360D393ED8", "strrchr", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int s = vm.NativeArg(a, 1), found = 0;
        uint8_t ch = uint8_t(vm.NativeArg(a, 2));
        for (int b; s != 0 && (b = vm.ReadByte(s)) != 0; s++)
        {
            if (IsLead(b))
            {
                if (vm.ReadByte(++s) == 0)
                    break;
            }
            else if (b == ch)
                found = s;
        }
        vm.SetL(a, 0, found);
    });

    // START 75F62 / 763AC / 85844: f[2] = the index of the first word f[0] in the string of words at
    // f[1] (before its 0), else -1
    RegisterNative({"625BEDD645CDC8948BCDADC78515BD3D979A5D1A", "D8162510516400B08E35C7772F9CDAACC826BE5C"}, "wcsindex",
                   [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
                       uint16_t ch = uint16_t(vm.Read32(a.F));
                       int start = vm.Read32(a.F + 4), result = -1;
                       for (int p = start;; p += 2)
                       {
                           uint16_t w = vm.Read16(p);
                           if (w == 0)
                               break;
                           if (w == ch)
                           {
                               result = int(uint32_t(p - start) >> 1);
                               break;
                           }
                       }
                       vm.Write32(a.F + 8, result);
                   });

    // START 75FAB / 763F3 / 8588C: f[2] = the offset of the first byte f[0] in the string at f[1]
    // (before its 0), else -1
    RegisterNative({"1BA6C16D9E3B19F01F3582BE297FFA7A675F3603", "FC6BD4250CAC523AB50FF78AAA7D8E8EBDD925B4"}, "strindex",
                   [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
                       uint8_t ch = uint8_t(vm.Read32(a.F));
                       int start = vm.Read32(a.F + 4), result = -1;
                       for (int p = start;; p++)
                       {
                           uint8_t b = vm.ReadByte(p);
                           if (b == 0)
                               break;
                           if (b == ch)
                           {
                               result = p - start;
                               break;
                           }
                       }
                       vm.Write32(a.F + 8, result);
                   });

    // START 75FEE / 76434 / 858D0 (and others): l[0] = NOT l[1], l[2] bytes, by 8 bytes (l[3] = 0:
    // the count rounded down to 8) or by 4
    RegisterNative({"8EE6D4D8C30D61F6379F0F7FA42BAB648AE0483E", "0CF868D5FF21E737808C7235BF0D164BFDE48B4F",
                    "516F6F4090493362A1590006FF9F3031DFFC9346", "039192F71200D43AF29A5F5AE0682CCAC4317F2C",
                    "7B2B7B77BACDED981FFB2FEBE08F68CBFB09DB09", "CD4D7E16634D0824BA915D35DFE8555BC0971D1B",
                    "F09D5672C0E378DB7DEAB4703FE65D4FB6983E76"},
                   "not", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
                       uint32_t count = uint32_t(vm.NativeArg(a, 2));
                       int64_t bytes = vm.NativeArg(a, 3) == 0 ? int64_t(count >> 3) * 8 : int64_t(count >> 2) * 4;
                       if (bytes == 0 || bytes > 0x10000000 || (dst != src && !Apart(dst, bytes, src, bytes)))
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       vm.MapDwords(dst, src, int(bytes), [](uint32_t v) { return ~v; });
                   });

    // START 7633C / 7678A / 85C1C (Ero-On! 5DC80): strnicmp of l[0] and l[1] (A-Z as a-z), at most
    // l[2] bytes; l[3] = the count left where they differ or one ends (0 = equal)
    RegisterNative({"2D36B031F4C93E1FD11A2474378A26F53A9CB62A", "DAB4F2DE807F4120AB0D07F77C7F4C686804200D"}, "strnicmp",
                   [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
                       int p = vm.NativeArg(a, 0), q = vm.NativeArg(a, 1);
                       uint32_t n = uint32_t(vm.NativeArg(a, 2));
                       auto lower = [](uint8_t b) { return b >= 0x41 && b <= 0x5A ? uint8_t(b | 0x20) : b; };
                       while (n != 0)
                       {
                           uint8_t x = lower(vm.ReadByte(p)), y = lower(vm.ReadByte(q));
                           if (x != y || --n == 0 || x == 0 || y == 0)
                               break;
                           p++;
                           q++;
                       }
                       vm.SetL(a, 3, int(n));
                   });

    // START 76042 / 76490 / 85920 (MMX): sepia of BGR pixels, l[2] bytes (rounded down to 24) from
    // l[1] to l[0]: y = (151 b0 + 77 b1 + 29 b2) >> 8, then its bytes (y * 145, y * 200, y * 240) >> 8
    RegisterNative({"B9D719E48EB9DD041EA49683ADA8C7F1602FB1FC", "649843201017621317B4493924B9AF51F5FDD445"}, "sepia",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
                       uint32_t count = uint32_t(vm.NativeArg(a, 2));
                       int64_t bytes = int64_t(count / 24) * 24;
                       // under 24 bytes the x86 loop's count wraps round
                       if (bytes == 0 || (dst != src && !Apart(dst, bytes, src, bytes)))
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       constexpr int Chunk = 24 * 4096;
                       openshiina::Bytes buffer(Chunk);
                       for (int64_t done = 0; done < bytes; done += Chunk)
                       {
                           int n = int(std::min<int64_t>(Chunk, bytes - done));
                           vm.ReadBytes(src + int(done), buffer.data(), n);
                           for (int i = 0; i < n; i += 3)
                           {
                               int y = (buffer[size_t(i)] * 151 + buffer[size_t(i + 1)] * 77 + buffer[size_t(i + 2)] * 29) >> 8;
                               buffer[size_t(i)] = uint8_t(y * 145 >> 8);
                               buffer[size_t(i + 1)] = uint8_t(y * 200 >> 8);
                               buffer[size_t(i + 2)] = uint8_t(y * 240 >> 8);
                           }
                           vm.WriteBytes(dst + int(done), buffer.data(), n);
                       }
                   });

    // START 7963C / 79A8A / 88BDC (Ero-On! 5EB4A; MMX): a rectangle of 32-bit pixels. l[0] dst, l[1]
    // src, l[2] / l[3] their row strides, l[4] width, l[5] height
    RegisterNative({"6E76FCA4FA78FA7B47C9153B7BFEEA95FF609582", "6F192CD4BA3ACAB3A5F79FE84D6B6CA9A08E6C36",
                    "992F9776E50F5FBD5AB6733AD893836B4C13458E"},
                   "copy32", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1), sd = vm.NativeArg(a, 2), ss = vm.NativeArg(a, 3);
                       uint32_t width = uint32_t(vm.NativeArg(a, 4)), height = uint32_t(vm.NativeArg(a, 5));
                       if (width == 0 || height == 0)
                           return;
                       int64_t rowBytes = int64_t(width) * 4;
                       if (rowBytes > 0x10000000 || height > 0x1000000)
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       auto dstSpan = RowsSpan(dst, sd, int(height), rowBytes);
                       if (!Apart(dstSpan, RowsSpan(src, ss, int(height), rowBytes)))
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       for (int y = 0; y < int(height); y++)
                           vm.MoveMemory(dst + y * sd, src + y * ss, int(rowBytes));
                   });

    // EFCLIB 1E9E (and others' padding): the negative and other fills: l[3] dwords from l[4] to
    // l[0], each XORed with l[5]'s byte in every byte
    RegisterNative({"EB59CB1AF7084D666150A58C5866638D49AAC562", "BD87E97A68BDF28ECD10B4E3EBDBD6799D5A0F1D",
                    "997E3DD49B9B61739720F69D2E180ADAC4C9D884", "D299BA54CDEF926F62CDC0733BC018D56CDA54D5",
                    "683B763EC4CA9666F13D288DF45400CB2F498B4E", "D7BB0B77AF051FEA644DBDCBAB9E624522D31AF8",
                    "1FD120C0ED9408FF1CFA70D8D389AE41A6C97FD9", "0AFC04E0141EBB21410CD23BEFECF1869702A705",
                    "17C12A8F901EFC316D09685A7A84F78F067502D2", "4DCD44C703951F8B10388CCB125A577E7A167FAF"},
                   "xor32", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 4);
                       uint32_t count = uint32_t(vm.NativeArg(a, 3)), v = uint32_t(vm.NativeArg(a, 5));
                       v |= v << 8;
                       v |= v << 16;
                       int64_t bytes = int64_t(count) * 4;
                       if (count == 0 || bytes > 0x10000000 || (dst != src && !Apart(dst, bytes, src, bytes)))
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       vm.MapDwords(dst, src, int(bytes), [v](uint32_t x) { return x ^ v; });
                   });

    // EFCLIB 54A0 / 549E / 6D79 (and others' padding): l[1] dwords at l[0] inverted in place
    RegisterNative({"4158148EFBFBC3AA4CE88FF4D6D5D2B9C5707615", "8DC1060C677A722C3CACF4865B7E506824EB7942",
                    "863B772CB1F64D67213C7F27F74AEEC6471CBD3A", "98CFC5C0D6A29A93EC9E2A728E0017AEE4027AE1",
                    "459983DF2974D9D4EE797EEC75AAC21D02DC41C9", "13C8A2EA74D9219B5D33A3F1C2B47AE8D9B9B8FA"},
                   "invert32", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0);
                       uint32_t count = uint32_t(vm.NativeArg(a, 1));
                       if (count == 0 || count > 0x4000000)
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       vm.MapDwords(dst, dst, int(count) * 4, [](uint32_t x) { return ~x; });
                   });

    // Ero-On! START 5DCCA (MMX): case 0 of its zoom, a rectangle of 32-bit pixels moved by a
    // sixteenth of a pixel
    RegisterNative("1AFA154B046DABF3E9E3ECE7BCD4562FCCFE18D5", "move16", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Move16(c, a); });

    // Re: Rem Plus START 85774: BGR to 32-bit pixels (bytes FF, B, G, R). l[0] src (rows one after
    // the other), l[1] dst, l[2] width, l[3] height, l[4] dst stride
    RegisterNative("A0298E9169EFB1ED73FB780ABAB186C3C879C91B", "bgr to 32", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1), width = vm.NativeArg(a, 2), height = vm.NativeArg(a, 3), sd = vm.NativeArg(a, 4);
        if (height == 0)
            return;
        if (width < 0 || height < 0 || width > 0x1000000 || height > 0x100000)
        {
            vm.RunX86(c, a);
            return;
        }
        auto dstSpan = RowsSpan(dst, sd, height, width * int64_t(4));
        if (width > 0 && !Apart(dstSpan, {src, int64_t(width) * 3 * height}))
        {
            vm.RunX86(c, a);
            return;
        }
        openshiina::Bytes row(size_t(width) * 3), output(size_t(width) * 4);
        for (int y = 0; y < height; y++, src += width * 3, dst += sd)
        {
            vm.ReadBytes(src, row.data(), int(row.size()));
            for (int x = 0; x < width; x++)
            {
                output[size_t(4 * x)] = 0xFF;
                output[size_t(4 * x + 1)] = row[size_t(3 * x)];
                output[size_t(4 * x + 2)] = row[size_t(3 * x + 1)];
                output[size_t(4 * x + 3)] = row[size_t(3 * x + 2)];
            }
            vm.WriteBytes(dst, output);
        }
    });

    // Re: Rem Plus START 857DC: 32-bit pixels to BGR (bytes 1, 2, 3 of each). l[0] src, l[1] dst
    // (rows one after the other), l[2] width, l[3] height, l[4] src stride
    RegisterNative("BF29AD3A71C286A4A98B9FBCE1752CCFA4A5FB6B", "32 to bgr", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1), width = vm.NativeArg(a, 2), height = vm.NativeArg(a, 3), ss = vm.NativeArg(a, 4);
        if (height == 0)
            return;
        if (width < 0 || height < 0 || width > 0x1000000 || height > 0x100000)
        {
            vm.RunX86(c, a);
            return;
        }
        int64_t dstBytes = int64_t(width) * 3 * height;
        if (width > 0 && !Apart(RowsSpan(src, ss, height, width * int64_t(4)), {dst, dstBytes}))
        {
            vm.RunX86(c, a);
            return;
        }
        openshiina::Bytes row(size_t(width) * 4), output(size_t(width) * 3);
        for (int y = 0; y < height; y++, src += ss, dst += width * 3)
        {
            vm.ReadBytes(src, row.data(), int(row.size()));
            for (int x = 0; x < width; x++)
            {
                output[size_t(3 * x)] = row[size_t(4 * x + 1)];
                output[size_t(3 * x + 1)] = row[size_t(4 * x + 2)];
                output[size_t(3 * x + 2)] = row[size_t(4 * x + 3)];
            }
            vm.WriteBytes(dst, output);
        }
    });

    // Oni ga Kuru START D14F0: in the l[1] bytes at l[0], each 81 40 made 87 40, looked for at every
    // byte (not by characters), as the x86 code counts
    RegisterNative("4C42D9E01CAAD99B51CB398660F6EDF9DCC2FE21", "space 8740", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int p = vm.NativeArg(a, 0), n = vm.NativeArg(a, 1);
        if (n == 0 || --n == 0)
            return;
        do
        {
            if (vm.Read16(p) == 0x4081)
            {
                vm.Write16(p, 0x4087);
                p += 2;
                if (--n == 0)
                    break;
                n--;
            }
            else
            {
                p++;
                n--;
            }
        } while (n != 0);
    });

    // Re: Rem Plus START 88FEC: the string at l[1] to l[0] with half-width characters (20-7E) made
    // full-width by the table of words inside the routine (+34h + 2 c); the lead bytes take their
    // next byte with them
    RegisterNative("B32EF050EF5EBAAAD4944D5CA7C0946711407AFA", "fullwidth", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1), table = vm.m_nativeTarget + 0x34;
        for (int ch; (ch = vm.ReadByte(src)) != 0;)
        {
            if (IsLead(ch))
            {
                vm.Write16(dst, vm.Read16(src));
                src += 2;
                dst += 2;
            }
            else if (ch >= 0x20 && ch <= 0x7E)
            {
                vm.Write16(dst, vm.Read16(table + 2 * ch));
                src++;
                dst += 2;
            }
            else
                vm.WriteByte(dst++, vm.ReadByte(src++));
        }
        vm.WriteByte(dst, 0);
    });

    // Re: Rem Plus START 8911E: l[2] BGR pixels from l[1] to l[0] with their saturation changed by
    // l[3] through the tables inside the routine (+A72h); Oni ga Kuru D1523: +A6Dh
    RegisterNative("ED8F3CADF6C30D98C2133F56F7209213D4423519", "saturation",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Saturation(c, a, 0xA72); });
    RegisterNative("08CDE22C44BB4C9077EFF9DF15685E56D14C49E4", "saturation",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Saturation(c, a, 0xA6D); });

    // Re: Rem Plus START 8BB90: swaps l[2] bytes at l[0] and l[1] (dwords, then bytes)
    RegisterNative("341D72D8264C3FAF28CF53828124786719214DCB", "swap", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int p = vm.NativeArg(a, 0), q = vm.NativeArg(a, 1);
        uint32_t n = uint32_t(vm.NativeArg(a, 2));
        if (n > 0x10000000)
        {
            vm.RunX86(c, a);
            return;
        }
        if (Apart(p, n, q, n))
        {
            auto x = vm.ReadBytes(p, int(n)), y = vm.ReadBytes(q, int(n));
            vm.WriteBytes(p, y);
            vm.WriteBytes(q, x);
            return;
        }
        // as the x86 code does it, a dword or byte at a time
        for (uint32_t k = n >> 2; k > 0; k--, p += 4, q += 4)
        {
            int x = vm.Read32(p), y = vm.Read32(q);
            vm.Write32(p, y);
            vm.Write32(q, x);
        }
        for (uint32_t k = n & 3; k > 0; k--, p++, q++)
        {
            uint8_t x = vm.ReadByte(p), y = vm.ReadByte(q);
            vm.WriteByte(p, y);
            vm.WriteByte(q, x);
        }
    });

    // Re: Rem Plus START 8BC00: the montage table at b[202] (its size at +4, 24-byte entries from +8)
    // gets its names (16 bytes, up to a 0) in capitals
    RegisterNative("8248DF0D0FB0946C946217CF79FDE27AA4B3F55A", "montage upper", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int table = vm.Read32(a.B + 0x328);
        uint32_t count = (uint32_t(vm.Read32(table + 4)) - 8) / 24;
        if (count == 0)
        {
            vm.RunX86(c, a);
            return;
        }
        uint8_t name[16];
        for (int entry = table + 8; count > 0; count--, entry += 24)
        {
            vm.ReadBytes(entry, name, 16);
            auto zero = std::find(name, name + 16, uint8_t(0));
            UpperAscii(name, size_t(zero - name));
            vm.WriteBytes(entry, name, 16);
        }
    });

    // Re: Rem Plus START 8BC4C: looks the name at l[0] up in that table by bisection
    RegisterNative("D2DBBF7D7496763A3E96AC2A084D0A60D32C885D", "montage find",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.MontageFind(c, a); });

    // Re: Rem Plus START 8BD18 / 8BD38: l[2] subtracted from / added to l[1] dwords at l[0]
    RegisterNative("6ADF303C50268109E441608629A5D7E07752E4D0", "sub32",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.AddDwords(c, a, int(0u - uint32_t(vm.NativeArg(a, 2)))); });
    RegisterNative("462BA655CE37918B692B5286D22DB1C995B10E25", "add32",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.AddDwords(c, a, vm.NativeArg(a, 2)); });

    // Re: Rem Plus START 8BD58: l[0] = the checksum of l[1] bytes at l[0]: each dword (the last 1-3
    // bytes as one) added, then rotated right by 3
    RegisterNative("639ACC96A94246D7D1420B3161F2E864AC311BA5", "checksum", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int p = vm.NativeArg(a, 0);
        uint32_t n = uint32_t(vm.NativeArg(a, 1)), sum = 0;
        constexpr int Chunk = 1 << 16;
        openshiina::Bytes buffer(Chunk);
        for (uint32_t left = n >> 2; left > 0;)
        {
            int words = int(std::min<uint32_t>(left, Chunk / 4));
            vm.ReadBytes(p, buffer.data(), words * 4);
            for (int k = 0; k < words * 4; k += 4)
                sum = std::rotr(sum + LittleEndian::ToUInt32(&buffer[size_t(k)]), 3);
            p += words * 4;
            left -= uint32_t(words);
        }
        int rest = int(n & 3);
        if (rest != 0)
        {
            uint32_t tail = 0;
            for (int k = rest - 1; k >= 0; k--)
                tail = tail << 8 | vm.ReadByte(p + k);
            sum = std::rotr(sum + tail, 3);
        }
        vm.SetL(a, 0, int(sum));
    });

    // Re: Rem Plus START 8BD9C: a 28 x 28 mask (samples25's code); Oni ga Kuru D419C: 45 x 45 of a
    // picture 1600 wide; Gohoushi Nurse 8D33C and Lovecha 9004C: 24 x 24
    RegisterNative("B0BB28FCE0DED80456A7AC465F671A3E703C7897", "mask28",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Samples(c, a, 28); });
    RegisterNative("B16300E32869D8C30FCC3BDD27211F7E9544C677", "mask24",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Samples(c, a, 24); });
    RegisterNative("84BAB7E3B467E7FF146EE14A147E96ABF7740FA0", "mask45",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Samples(c, a, 45, 4800); });

    // Re: Rem Plus START 904F4: matches the 48-byte records from l[1] against the table at l[2]
    RegisterNative("97B65886703D8A12CE27C061A3EEFBD607A1C556", "match records",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.MatchRecords(c, a); });

    // Libra of the Vampire Princess START E1B0C: mask28 of any size, n x n with n the low byte of
    // l[2] (0: 256). Sorcery Jokers 2184D0: the same from a picture 1920 wide
    RegisterNative("0A844E6B45D4675A2CDE5BA1EA49CCAA0A3AB48B", "mask", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Mask(c, a, 3840); });
    RegisterNative("CDBE0CBE1850ACA2AC91138D33EEED4284359AE3", "mask 1920",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Mask(c, a, 5760); });

    // Sorcery Jokers START 2159B8: a blank S25 picture at l[0] (the Japanese release's 1EE588: the
    // same but nothing cleared first)
    RegisterNative("DE3A498EFE2D198ACE0A0AA10C6CD571E986461A", "blank s25",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlankS25(c, a, true); });
    RegisterNative("C0A25921BDF8B7EF6B50997CB9E7D82673808205", "blank s25",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.BlankS25(c, a, false); });

    // Libra of the Vampire Princess START F777: the Shift-JIS string at l[0] copied to l[1], each
    // single byte among the first five of l[2] written twice; a lead byte takes the byte after it
    RegisterNative("49414E55EAE3AB94EC8890F0BBE7D67BB8039798", "escape copy", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1), set = vm.NativeArg(a, 2);
        uint8_t doubled[5];
        vm.ReadBytes(set, doubled, 5);
        openshiina::Bytes output;
        for (int p = src;;)
        {
            uint8_t b = vm.ReadByte(p);
            if (b == 0)
                break;
            if (IsLead(b))
            {
                output.push_back(b);
                output.push_back(vm.ReadByte(p + 1));
                p += 2;
                continue;
            }
            if (std::find(doubled, doubled + 5, b) != doubled + 5)
                output.push_back(b);
            output.push_back(b);
            p++;
        }
        output.push_back(0);
        vm.WriteBytes(dst, output);
    });

    // Libra of the Vampire Princess START F7C8: the first "[" (l[0] = 1), "{" (2) or lead byte (3) in
    // the text at l[0], l[1] = its address; l[0] = 0 when there is none
    RegisterNative("B9C872F614C7912A386590D67B7FFBA75C4AF240", "find mark", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int p = vm.NativeArg(a, 0), kind = 0;
        for (uint8_t b; (b = vm.ReadByte(p)) != 0; p++)
        {
            kind = b == '[' ? 1 : b == '{' ? 2 : IsLead(b) ? 3 : 0;
            if (kind != 0)
                break;
        }
        vm.SetL(a, 0, kind);
        if (kind != 0)
            vm.SetL(a, 1, p);
    });

    // Libra of the Vampire Princess START A27E4: the width of the text at l[0], 36 for a double-byte
    // character; Sorcery Jokers START 1DBB08: 34
    RegisterNative("5B6AFC75F46CBDFF8B9C53955F1445FBF6C4B8B1", "text width",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.TextWidth(c, a, 0x24); });
    RegisterNative("1F28134971F3C9966E2AA8BCC5D672B3034AAE0D", "text width 34",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.TextWidth(c, a, 0x22); });

    // Sorcery Jokers START 215980: the picture at l[0] made relocatable again (what 04B2 did, undone)
    RegisterNative("FC46690390954614B7823E737C55531ED3C6017B", "unrelocate s25", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int picture = vm.NativeArg(a, 0), frames = vm.Read32(picture + 4);
        if (frames == 0)
            return;
        if (frames < 0 || frames > 0x100000)
        {
            vm.RunX86(c, a);
            return;
        }
        for (int i = 0; i < frames; i++)
        {
            int at = picture + 8 + 4 * i, frame = vm.Read32(at);
            if (frame == 0)
                continue;
            vm.Write32(at, frame - picture);
            int height = vm.Read32(frame + 4);
            for (int y = 0; y < height; y++)
                vm.Write32(frame + 0x14 + 4 * y, vm.Read32(frame + 0x14 + 4 * y) - picture);
        }
    });

    // Sorcery Jokers START 215A56: l[2] BGR pixels from l[1] to l[0], their colour scaled by l[3]
    // towards their brightest channel through a dword table after the routine (+0xA7A). Divisions
    // as idiv, shifts as sar after the x86 code's rounding towards 0
    RegisterNative("3897C1B201D06481C10CFBDD3B60B45DFCF84934", "saturation", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
        uint32_t count = uint32_t(vm.NativeArg(a, 2));
        int k = vm.NativeArg(a, 3), table = vm.m_nativeTarget + 0xA7A;
        if (count == 0)
            return;
        int64_t bytes = int64_t(count) * 3;
        if (bytes > 0x10000000 || (dst != src && !Apart(dst, bytes, src, bytes)))
        {
            vm.RunX86(c, a);
            return;
        }
        auto sar16 = [](int x) { return (x + ((x >> 31) & 0xFFFF)) >> 16; };
        auto sar8 = [](int x) { return (x + ((x >> 31) & 0xFF)) >> 8; };
        auto t = [&](int at, int index) { return vm.Read32(table + at + 4 * index); };
        for (int n = 0; n < int(count); n++, src += 3, dst += 3)
        {
            int b = vm.ReadByte(src), g = vm.ReadByte(src + 1), r = vm.ReadByte(src + 2);
            uint8_t nb, ng, nr;
            if (b > g)
            {
                if (r > b)
                {
                    int e = (r - g) * k, q = ((b - g) << 8) / (r - g);
                    nb = uint8_t(r - sar16(t(0x1000, q) * e));
                    ng = uint8_t(r - sar8(e));
                    nr = uint8_t(r);
                }
                else if (g > r)
                {
                    int e = (b - r) * k, q = ((g - r) << 8) / (b - r);
                    nb = uint8_t(b);
                    ng = uint8_t(b - sar16(t(0, q) * e));
                    nr = uint8_t(b - sar8(e));
                }
                else
                {
                    int e = (b - g) * k, q = ((g - r) << 8) / (b - g);
                    nb = uint8_t(b);
                    ng = uint8_t(b - sar8(e));
                    nr = uint8_t(b - sar16(t(0x1800, q) * e));
                }
            }
            else if (r > g)
            {
                int e = (r - b) * k, q = ((b - g) << 8) / (r - b);
                nb = uint8_t(r - sar8(e));
                ng = uint8_t(r - sar16(t(0x1000, q) * e));
                nr = uint8_t(r);
            }
            else if (b > r)
            {
                int e = (g - r) * k, q = ((r - b) << 8) / (g - r);
                nb = uint8_t(g - sar16(t(0x800, q) * e));
                ng = uint8_t(g);
                nr = uint8_t(g - sar8(e));
            }
            else if (g == b)
                nb = ng = nr = uint8_t(g);
            else
            {
                int e = (g - b) * k, q = ((r - b) << 8) / (g - b);
                nb = uint8_t(g - sar8(e));
                ng = uint8_t(g);
                nr = uint8_t(g - sar16(t(0x800, q) * e));
            }
            vm.WriteByte(dst, nb);
            vm.WriteByte(dst + 1, ng);
            vm.WriteByte(dst + 2, nr);
        }
    });

    // Sorcery Jokers START 1249E: l[1] = the first byte of the text at l[5] that is not a
    // double-byte character, 0 when the text ends first
    RegisterNative("76C5891E51AA8562C14754D009A15228C3B0964E", "skip double bytes", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int p = vm.NativeArg(a, 5);
        uint8_t b;
        while (IsLead(b = vm.ReadByte(p)))
            p += 2;
        vm.SetL(a, 1, b == 0 ? 0 : p);
    });

    // Sorcery Jokers' save thumbnail (START 21198F): 856 x 482 from blocks of 2 x 2 of a picture 1920
    // wide; 211CAE: its 482 rows of 2,568 bytes from l[0] to l[1], l[2] apart; 211CFE: made 32-bit
    RegisterNative("1B616D8D7564F0A1C77F845271C312CE3B4D7158", "thumbnail",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Thumbnail(c, a, 2, 856, 482, 5760); });
    RegisterNative("BE88BAA40723DF58AAAA5A38831998ACFDCB399D", "rows 856 x 482", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        vm.CopyRows(c, a, vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 0), 2568, 482, 2568);
    });
    RegisterNative("044D48B9A8BDED69F0BBBBB73EE8C5A6A76C9592", "24 to 32 bits",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.To32Bits(c, a, 856, 482); });
    // Yoyogi Hitozuma Senmon Gakuin START 28408 (v2.37, MMX; Cleavage START 290D0 the same code):
    // its rule wipe, 360,000 pixels of pictures A (l[1]) and B (l[2]) by the mask M (l[3]) into l[0]
    RegisterNative({"5A3FAD758A876FE96A7F5B31B97E9D27CEF8C1D4", "0493BD7B4F8D747B0470A7B98BCF1DF1F15FB3DE"}, "rule wipe",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       constexpr int Pixels = 360000;
                       int dst = vm.NativeArg(a, 0), pa = vm.NativeArg(a, 1), pb = vm.NativeArg(a, 2), pm = vm.NativeArg(a, 3);
                       int from = vm.NativeArg(a, 4), span = vm.NativeArg(a, 5);
                       if (span == 0 || !Apart(dst, Pixels * int64_t(4), pa, Pixels * int64_t(4)) || !Apart(dst, Pixels * int64_t(4), pb, Pixels * int64_t(4))
                           || !Apart(dst, Pixels * int64_t(4), pm, Pixels * int64_t(4)))
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       int16_t low = int16_t(std::max(from, 0)), high = int16_t(from + span);
                       uint16_t start = uint16_t(from);
                       uint32_t quotient = 0x10000u / uint32_t(span), packed = quotient << 16 | quotient;
                       uint16_t q[2] = {uint16_t(packed), uint16_t(packed >> 16)};
                       auto bytesA = vm.ReadBytes(pa, Pixels * 4), bytesB = vm.ReadBytes(pb, Pixels * 4), mask = vm.ReadBytes(pm, Pixels * 4);
                       openshiina::Bytes output(Pixels * 4);
                       for (size_t i = 0; i < output.size(); i++)
                       {
                           int16_t m = int16_t(mask[i]);
                           bool aboveLow = m > low, belowHigh = high > m, between = aboveLow && belowHigh;
                           int t = between ? (uint16_t(uint16_t(m - start) * q[i & 1]) >> 8) & 0xFF : 0;
                           int rest = between ? uint16_t(-t) & 0xFF : 0;
                           int outside = (aboveLow ? 0 : bytesA[i]) | (belowHigh ? 0 : bytesB[i]);
                           output[i] = uint8_t(uint16_t(bytesA[i] * rest + bytesB[i] * t) >> 8 | outside);
                       }
                       vm.WriteBytes(dst, output);
                   });
    // Miko Dashi START 0086D (v2.47): l[3] = 0 when the l[2] bytes at l[0] and l[1] are the same (or
    // l[2] is 0), else 1
    RegisterNative("8FB1D89A2807A081E0BB98462D2D0323A39C10E3", "memcmp", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        int x = vm.NativeArg(a, 0), y = vm.NativeArg(a, 1), count = vm.NativeArg(a, 2);
        if (count < 0 || count > 0x10000000)
        {
            vm.RunX86(c, a);
            return;
        }
        vm.SetL(a, 3, vm.ReadBytes(x, count) == vm.ReadBytes(y, count) ? 0 : 1);
    });
    // The Japanese Sorcery Jokers' START 0406D: in three squares of 32 x 32 side by side of a picture
    // 1920 wide at l[0] (none when it is 0), how many pixels' first byte is not 0, into l[1..3]
    RegisterNative("865BD2B3A91252B3DE759CEDB32B5D7BAC091417", "count 32x32", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int src = vm.NativeArg(a, 0);
        for (int k = 0; k < 3; k++)
        {
            int count = 0;
            if (src != 0)
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                        if (vm.ReadByte(src + k * 96 + y * 5760 + x * 3) != 0)
                            count++;
            vm.SetL(a, 1 + k, count);
        }
    });

    // Sorcery Jokers START 211A07: 482 rows of 856 BGR pixels from l[4] (a picture 1920 wide) to
    // l[0], one after the other (blocks that share bytes run as x86)
    RegisterNative("8E6C33413435C5ACB0FE26A1264354E76D077DA0", "copy 856x482", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        constexpr int Rows = 482, RowBytes = 856 * 3, Stride = 5760;
        int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 4);
        if (!Apart({dst, int64_t(Rows) * RowBytes}, RowsSpan(src, Stride, Rows, RowBytes)))
        {
            vm.RunX86(c, a);
            return;
        }
        for (int y = 0; y < Rows; y++)
            vm.WriteBytes(dst + y * RowBytes, vm.ReadBytes(src + y * Stride, RowBytes));
    });
}

void ScnVm::TextWidth(ScnContext&, const ScnNativeArgs& a, int wide)
{
    int p = NativeArg(a, 0), table = NativeArg(a, 2), width = 0;
    for (uint8_t b; (b = ReadByte(p)) != 0;)
    {
        if (b == '[')
        {
            table = NativeArg(a, 1);
            p++;
            continue;
        }
        if (b == ']' || b == '}')
        {
            table = NativeArg(a, 2);
            p++;
            continue;
        }
        if (b == '{')
        {
            table = NativeArg(a, 3);
            p++;
            continue;
        }
        if (IsLead(b))
        {
            width += wide;
            p += 2;
            continue;
        }
        width += ReadByte(table + b);
        p++;
    }
    SetL(a, 0, width);
    SetL(a, 1, table);
}

void ScnVm::Mask(ScnContext&, const ScnNativeArgs& a, int stride)
{
    // An n x n mask (n the low byte of l[2], 0: 256) at l[1] from the first byte of each pixel of a
    // BGR picture at l[0]: (v << 6) / 255
    int src = NativeArg(a, 0), dst = NativeArg(a, 1), n = uint8_t(NativeArg(a, 2));
    if (n == 0)
        n = 256;
    for (int y = 0; y < n; y++, src += stride)
        for (int x = 0; x < n; x++)
            WriteByte(dst++, uint8_t((ReadByte(src + 3 * x) << 6) / 255));
}

template <typename F> void ScnVm::MapDwords(int dst, int src, int bytes, F f)
{
    // dst = f(src) a dword at a time (dst is src or apart from it)
    constexpr int Chunk = 1 << 16;
    openshiina::Bytes buffer(Chunk);
    for (int done = 0; done < bytes; done += Chunk)
    {
        int n = std::min(Chunk, bytes - done);
        ReadBytes(src + done, buffer.data(), n);
        for (int k = 0; k + 4 <= n; k += 4)
        {
            uint32_t word;
            std::memcpy(&word, &buffer[size_t(k)], 4);
            word = f(word);
            std::memcpy(&buffer[size_t(k)], &word, 4);
        }
        WriteBytes(dst + done, buffer.data(), n);
    }
}

void ScnVm::EnlargeWhole(ScnContext& c, const ScnNativeArgs& a, int n, int width, int height, int srcBack, int dstBack)
{
    // A width x height 32-bit picture (l[2], rows l[3] bytes apart) each pixel repeated n times
    // across and down into l[0] (rows l[1] bytes apart); with srcBack / dstBack each row starts so
    // many bytes before where the last ended
    int dst = NativeArg(a, 0), dstStride = NativeArg(a, 1), src = NativeArg(a, 2), srcStride = NativeArg(a, 3);
    int inBytes = width * 4, outBytes = inBytes * n;
    auto target = RowsSpan(dst, dstStride, height * n, outBytes);
    if (std::abs(int64_t(dstStride)) < outBytes || (dstBack > 0 && dstStride < outBytes + dstBack)
        || !Apart(target, RowsSpan(src, srcStride, height, inBytes)))
    {
        RunX86(c, a);
        return;
    }
    openshiina::Bytes input(static_cast<size_t>(inBytes)), output(static_cast<size_t>(outBytes));
    for (int y = 0; y < height; y++, src += srcStride - srcBack, dst -= dstBack)
    {
        ReadBytes(src, input.data(), inBytes);
        for (int x = 0; x < width; x++)
            for (int k = 0; k < n; k++)
                std::memcpy(&output[size_t((x * n + k) * 4)], &input[size_t(x * 4)], 4);
        for (int k = 0; k < n; k++, dst += dstStride)
            WriteBytes(dst, output);
    }
}

void ScnVm::ClearFrame(ScnContext& c, const ScnNativeArgs& a)
{
    // Nukitashi's frame cleared in a 348 x 208 32-bit picture at l[0] (rows l[1] bytes apart): l[2]
    // rows at the top, l[3] at the bottom, and between them l[4] pixels at the left and l[5] at the
    // right. A count of 0 runs as x86 code
    constexpr int Width = 348, Height = 208;
    int dst = NativeArg(a, 0), stride = NativeArg(a, 1), top = NativeArg(a, 2), bottom = NativeArg(a, 3);
    int left = NativeArg(a, 4), right = NativeArg(a, 5), middle = Height - top - bottom;
    if (top <= 0 || bottom <= 0 || left <= 0 || right <= 0 || middle <= 0 || top > Height || bottom > Height || left > Width
        || right > Width || stride < Width * 4)
    {
        RunX86(c, a);
        return;
    }
    for (int y = 0; y < top; y++)
        FillMemory(dst + y * stride, Width * 4, 0);
    for (int y = 0; y < bottom; y++)
        FillMemory(dst + (Height - bottom + y) * stride, Width * 4, 0);
    for (int y = 0; y < middle; y++)
    {
        FillMemory(dst + (top + y) * stride, left * 4, 0);
        FillMemory(dst + (top + y) * stride + (Width - right) * 4, right * 4, 0);
    }
}

void ScnVm::EnlargeThreeHalves(ScnContext& c, const ScnNativeArgs& a, int pairs, int pairRows, int srcBack, int dstBack)
{
    // pairs x 2 by pairRows x 2 32-bit pixels (l[2], rows l[3] bytes apart) into pairs x 3 by
    // pairRows x 3 (l[0], rows l[1] apart), 2 x 2 into 3 x 3
    int inBytes = pairs * 8, outBytes = pairs * 12;
    int dst = NativeArg(a, 0), dstStride = NativeArg(a, 1), src = NativeArg(a, 2), srcStride = NativeArg(a, 3);
    auto target = RowsSpan(dst, dstStride, pairRows * 3, outBytes);
    if (dstStride < outBytes + dstBack || srcStride < inBytes || !Apart(target, RowsSpan(src, srcStride, pairRows * 2, inBytes)))
    {
        RunX86(c, a);
        return;
    }
    openshiina::Bytes top(static_cast<size_t>(inBytes)), bottom(static_cast<size_t>(inBytes));
    for (int y = 0; y < pairRows; y++)
    {
        int from = src + y * (2 * srcStride - srcBack), to = dst + y * (3 * dstStride - dstBack);
        ReadBytes(from, top.data(), inBytes);
        ReadBytes(from + srcStride, bottom.data(), inBytes);
        uint8_t* r0 = Bytes(to, outBytes);
        uint8_t* r1 = Bytes(to + dstStride, outBytes);
        uint8_t* r2 = Bytes(to + 2 * dstStride, outBytes);
        for (int i = 0, o = 0; i < inBytes; i += 8, o += 12)
            for (int k = 0; k < 4; k++)
            {
                int pa = top[size_t(i + k)], pb = top[size_t(i + 4 + k)], pc = bottom[size_t(i + k)], pd = bottom[size_t(i + 4 + k)];
                r0[o + k] = uint8_t(pa);
                r0[o + 4 + k] = uint8_t((pa + pb) >> 1);
                r0[o + 8 + k] = uint8_t(pb);
                r1[o + k] = uint8_t((pa + pc) >> 1);
                r1[o + 4 + k] = uint8_t((pa + pb + pc + pd) >> 2);
                r1[o + 8 + k] = uint8_t((pb + pd) >> 1);
                r2[o + k] = uint8_t(pc);
                r2[o + 4 + k] = uint8_t((pc + pd) >> 1);
                r2[o + 8 + k] = uint8_t(pd);
            }
    }
}

void ScnVm::CopyRows(ScnContext& c, const ScnNativeArgs& a, int dst, int dstStride, int src, int srcStride, int rows, int rowBytes)
{
    // Rows of rowBytes from src (rows srcStride apart) to dst (dstStride apart); blocks that share
    // bytes run as x86 code
    auto target = RowsSpan(dst, dstStride, rows, rowBytes);
    if (std::abs(int64_t(dstStride)) < rowBytes || !Apart(target, RowsSpan(src, srcStride, rows, rowBytes)))
    {
        RunX86(c, a);
        return;
    }
    openshiina::Bytes row(static_cast<size_t>(rowBytes));
    for (int y = 0; y < rows; y++)
    {
        ReadBytes(src + y * srcStride, row.data(), rowBytes);
        WriteBytes(dst + y * dstStride, row);
    }
}

void ScnVm::XorStream(ScnContext&, const ScnNativeArgs& a, int count)
{
    uint32_t key = uint32_t(NativeArg(a, 0));
    int at = NativeArg(a, 1);
    auto bytes = ReadBytes(at, count * 4);
    for (size_t i = 0; i + 4 <= bytes.size(); i += 4)
    {
        uint32_t word;
        std::memcpy(&word, &bytes[i], 4);
        word ^= key;
        std::memcpy(&bytes[i], &word, 4);
        key = ByteSwap(std::rotl(key + 1, 3));
    }
    WriteBytes(at, bytes);
}

void ScnVm::Thumbnail(ScnContext& c, const ScnNativeArgs& a, int block, int columns, int rows, int stride)
{
    ThumbnailAt(c, a, block, columns, rows, NativeArg(a, 0), NativeArg(a, 4), stride);
}

void ScnVm::ThumbnailAt(ScnContext& c, const ScnNativeArgs& a, int block, int columns, int rows, int dst, int src, int stride, int dstStride)
{
    // Each byte the average of a block of block x block pixels of a BGR picture (rows of stride
    // bytes), written one row after the other or rows dstStride bytes apart
    int rowBytes = columns * 3, inRows = rows * block;
    dstStride = dstStride == 0 ? rowBytes : dstStride;
    int outBytes = (rows - 1) * dstStride + rowBytes;
    if (!Apart(dst, outBytes, src, int64_t(stride) * inRows))
    {
        RunX86(c, a);
        return;
    }
    auto picture = ReadBytes(src, stride * (inRows - 1) + columns * block * 3);
    // Rows apart: the bytes between them stay as they are
    openshiina::Bytes output = dstStride == rowBytes ? openshiina::Bytes(static_cast<size_t>(outBytes)) : ReadBytes(dst, outBytes);
    for (int by = 0; by < rows; by++)
    {
        int o = by * dstStride;
        for (int bx = 0; bx < columns; bx++)
            for (int k = 0; k < 3; k++)
            {
                int sum = 0;
                for (int y = 0; y < block; y++)
                {
                    int p = (by * block + y) * stride + bx * block * 3 + k;
                    for (int x = 0; x < block; x++, p += 3)
                        sum += picture[size_t(p)];
                }
                output[size_t(o++)] = uint8_t(sum / (block * block));
            }
    }
    WriteBytes(dst, output);
}

void ScnVm::BlockCopy(ScnContext& c, const ScnNativeArgs& a, int rows, int rowBytes, int stride)
{
    int src = NativeArg(a, 0), dst = NativeArg(a, 1);
    auto dstSpan = RowsSpan(dst, stride, rows, rowBytes);
    if (!Apart(dstSpan, {src, int64_t(rows) * rowBytes}))
    {
        RunX86(c, a);
        return;
    }
    for (int y = 0; y < rows; y++)
        MoveMemory(dst + y * stride, src + y * rowBytes, rowBytes);
}

void ScnVm::BlankS25(ScnContext& c, const ScnNativeArgs& a, bool clear)
{
    int dst = NativeArg(a, 0), bytes = NativeArg(a, 1), code = NativeArg(a, 2), stride = NativeArg(a, 3);
    int frameBytes = NativeArg(a, 4), frames = NativeArg(a, 5), w = NativeArg(a, 6), h = NativeArg(a, 7);
    int64_t written = 8 + 4 * int64_t(frames) + int64_t(frameBytes) * (frames - 1) + 0x14 + 4 * int64_t(h) + int64_t(stride) * (h - 1) + 8;
    // Counts the x86 code would take round 4 billion times, or that write past the bytes cleared
    if (frames <= 0 || h <= 0 || frameBytes < 0 || stride < 0 || (clear ? bytes < 0 || written > bytes : written > 0x10000000))
    {
        RunX86(c, a);
        return;
    }
    if (clear)
        FillMemory(dst, bytes, 0);
    Write32(dst, 0x353253);
    Write32(dst + 4, frames);
    int frame = dst + 8 + 4 * frames;
    for (int f = 0; f < frames; f++)
        Write32(dst + 8 + 4 * f, 4 * frames + 8 + f * frameBytes);
    for (int f = 0; f < frames; f++, frame += frameBytes)
    {
        Write32(frame, w);
        Write32(frame + 4, h);
        if (!clear)
        {
            Write32(frame + 8, 0);
            Write32(frame + 0xC, 0);
            Write32(frame + 0x10, 0);
        }
        int rows = frame + 0x14, row = rows + 4 * h;
        for (int y = 0; y < h; y++)
            Write32(rows + 4 * y, row + y * stride - dst);
        for (int y = 0; y < h; y++, row += stride)
        {
            Write32(row, int(uint32_t(code) << 16));
            Write32(row + 4, w);
        }
    }
    SetL(a, 8, 0);
}

void ScnVm::Samples(ScnContext&, const ScnNativeArgs& a, int n, int stride)
{
    // n x n samples of the first byte of 24-bit pixels from l[0] to l[1], each b * 64 / 255, in
    // the x86 code's order
    int src = NativeArg(a, 0), dst = NativeArg(a, 1);
    for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
            WriteByte(dst++, uint8_t((ReadByte(src + y * stride + x * 3) << 6) / 255));
}

void ScnVm::To32Bits(ScnContext& c, const ScnNativeArgs& a, int width, int height)
{
    // width x height pixels of 24 bits at l[0] (one after the other) into 32 bits at l[1] (rows l[2]
    // bytes apart): FF, then the pixel's three bytes
    int src = NativeArg(a, 0), dst = NativeArg(a, 1), stride = NativeArg(a, 2);
    auto target = RowsSpan(dst, stride, height, width * 4);
    if (std::abs(int64_t(stride)) < width * 4 || !Apart(target, {src, int64_t(width) * 3 * height}))
    {
        RunX86(c, a);
        return;
    }
    openshiina::Bytes input(size_t(width) * 3);
    for (int y = 0; y < height; y++, src += width * 3, dst += stride)
    {
        ReadBytes(src, input.data(), int(input.size()));
        uint8_t* row = Bytes(dst, width * 4);
        for (int x = 0; x < width; x++)
        {
            row[4 * x] = 0xFF;
            row[4 * x + 1] = input[size_t(3 * x)];
            row[4 * x + 2] = input[size_t(3 * x + 1)];
            row[4 * x + 3] = input[size_t(3 * x + 2)];
        }
    }
}

void ScnVm::Move16(ScnContext& c, const ScnNativeArgs& a)
{
    // Each byte (p00 (16 - fx)(16 - fy) + p10 fx (16 - fy) + p01 (16 - fx) fy + p11 fx fy) >> 8 in
    // 16 bits, the products whose weight is 0 left out (fx = fy = 0 copies). l[0] dst, l[1] src,
    // l[2] / l[3] their strides, l[4] width, l[5] height, l[6] fx, l[7] fy
    int dst = NativeArg(a, 0), src = NativeArg(a, 1), sd = NativeArg(a, 2), ss = NativeArg(a, 3);
    int width = NativeArg(a, 4), height = NativeArg(a, 5);
    uint32_t fx = uint32_t(NativeArg(a, 6)), fy = uint32_t(NativeArg(a, 7));
    // a width or height of 0 makes the x86 loops wrap round
    if (width <= 0 || height <= 0 || width > 0x100000 || height > 0x100000)
    {
        RunX86(c, a);
        return;
    }
    bool right = fx != 0, below = fy != 0;
    auto dstSpan = RowsSpan(dst, sd, height, width * int64_t(4));
    if (!Apart(dstSpan, RowsSpan(src, ss, height + (below ? 1 : 0), (width + (right ? 1 : 0)) * int64_t(4))))
    {
        RunX86(c, a);
        return;
    }
    if (!right && !below)
    {
        for (int y = 0; y < height; y++)
            MoveMemory(dst + y * sd, src + y * ss, width * 4);
        return;
    }
    // the weights as the MMX code has them: the low words of the products
    uint32_t w00 = uint16_t((16 - fx) * (16 - fy)), w10 = uint16_t(fx * (16 - fy)), w01 = uint16_t((16 - fx) * fy), w11 = uint16_t(fx * fy);
    int inBytes = (width + (right ? 1 : 0)) * 4;
    openshiina::Bytes top(static_cast<size_t>(inBytes)), bottomRow(below ? size_t(inBytes) : 0), output(size_t(width) * 4);
    openshiina::Bytes& bottom = below ? bottomRow : top;
    for (int y = 0; y < height; y++)
    {
        ReadBytes(src + y * ss, top.data(), inBytes);
        if (below)
            ReadBytes(src + y * ss + ss, bottom.data(), inBytes);
        for (size_t i = 0; i < output.size(); i++)
        {
            uint32_t sum = top[i] * w00;
            if (right)
                sum += top[i + 4] * w10;
            if (below)
                sum += bottom[i] * w01;
            if (right && below)
                sum += bottom[i + 4] * w11;
            output[i] = uint8_t((sum & 0xFFFF) >> 8);
        }
        WriteBytes(dst + y * sd, output);
    }
}

void ScnVm::Saturation(ScnContext& c, const ScnNativeArgs& a, int tableOffset)
{
    int dst = NativeArg(a, 0), src = NativeArg(a, 1), k = NativeArg(a, 3);
    uint32_t count = uint32_t(NativeArg(a, 2));
    int tables = m_nativeTarget + tableOffset;
    if (count == 0)
        return;
    int64_t bytes = int64_t(count) * 3;
    if (bytes > 0x10000000)
    {
        RunX86(c, a);
        return;
    }
    if (dst == src || Apart(dst, bytes, src, bytes))
    {
        auto pixels = ReadBytes(src, int(bytes));
        for (size_t i = 0; i < pixels.size(); i += 3)
        {
            auto p = SaturationPixel(tables, k, pixels[i], pixels[i + 1], pixels[i + 2]);
            pixels[i] = p[0];
            pixels[i + 1] = p[1];
            pixels[i + 2] = p[2];
        }
        WriteBytes(dst, pixels);
        return;
    }
    // blocks that overlap: a pixel read, then written, as the x86 code goes
    for (uint32_t n = 0; n < count; n++, src += 3, dst += 3)
    {
        auto p = SaturationPixel(tables, k, ReadByte(src), ReadByte(src + 1), ReadByte(src + 2));
        WriteByte(dst, p[0]);
        WriteByte(dst + 1, p[1]);
        WriteByte(dst + 2, p[2]);
    }
}

std::array<uint8_t, 3> ScnVm::SaturationPixel(int tables, int k, int b, int g, int r)
{
    // With max and min of its bytes and t a table entry (by which byte is the largest) at
    // ((mid - min) << 8) / (max - min), a byte becomes max - (t (max - min) k) / 65536 or
    // max - ((max - min) k) / 256 (divisions towards 0, in 32 bits)
    auto down = [&](int table, int numerator, int range, int& coarse) {
        int e = range * k;
        int q = (numerator << 8) / range;
        int product = Read32(table + 4 * q) * e;
        coarse = (e < 0 ? e + 0xFF : e) >> 8;
        return (product < 0 ? product + 0xFFFF : product) >> 16;
    };
    int v1, v2;
    if (b > g)
    {
        if (r > b)
        {
            v1 = down(tables + 0x1000, b - g, r - g, v2);
            return {uint8_t(r - v1), uint8_t(r - v2), uint8_t(r)};
        }
        if (g > r)
        {
            v1 = down(tables, g - r, b - r, v2);
            return {uint8_t(b), uint8_t(b - v1), uint8_t(b - v2)};
        }
        v1 = down(tables + 0x1800, g - r, b - g, v2);
        return {uint8_t(b), uint8_t(b - v2), uint8_t(b - v1)};
    }
    if (r > g)
    {
        v1 = down(tables + 0x1000, b - g, r - b, v2);
        return {uint8_t(r - v2), uint8_t(r - v1), uint8_t(r)};
    }
    if (b > r)
    {
        v1 = down(tables + 0x800, r - b, g - r, v2);
        return {uint8_t(g - v1), uint8_t(g), uint8_t(g - v2)};
    }
    if (g == b)
        return {uint8_t(g), uint8_t(g), uint8_t(g)};
    v1 = down(tables + 0x800, r - b, g - b, v2);
    return {uint8_t(g - v2), uint8_t(g), uint8_t(g - v1)};
}

void ScnVm::MontageFind(ScnContext&, const ScnNativeArgs& a)
{
    int key = NativeArg(a, 0);
    int table = Read32(a.B + 0x328);
    int entries = table + 8;
    int count = int((uint32_t(Read32(table + 4)) - 8) / 24);
    SetL(a, 1, entries);
    SetL(a, 5, count);
    SetL(a, 4, count - 1);
    SetL(a, 3, 0);
    auto name = ReadBytes(key, 16);
    auto zero = std::find(name.begin(), name.end(), uint8_t(0));
    int length = zero == name.end() ? 16 : int(zero - name.begin()) + 1;
    UpperAscii(name.data(), size_t(length));
    WriteBytes(key, name.data(), length);
    SetL(a, 2, length);
    openshiina::Bytes probe(static_cast<size_t>(length));
    // repe cmpsb: 0 when the length is the same, else the signed order of the first byte that differs
    auto compare = [&](int index) {
        ReadBytes(entries + index * 24, probe.data(), length);
        int at = int(std::mismatch(probe.begin(), probe.end(), name.begin()).first - probe.begin());
        return at == length ? 0 : int8_t(probe[size_t(at)]) > int8_t(name[size_t(at)]) ? 1 : -1;
    };
    int low = 0, high = count - 1, found;
    while (true)
    {
        if (low >= high)
        {
            found = low;
            if (compare(found) != 0)
            {
                SetL(a, 0, -1);
                return;
            }
            break;
        }
        int middle = int(uint32_t(low + high) >> 1);
        int order = compare(middle);
        if (order == 0)
        {
            found = middle;
            break;
        }
        if (order > 0)
            SetL(a, 4, high = middle - 1);
        else
            SetL(a, 3, low = middle + 1);
    }
    SetL(a, 0, entries + found * 24);
}

void ScnVm::AddDwords(ScnContext& c, const ScnNativeArgs& a, int value)
{
    int p = NativeArg(a, 0);
    uint32_t count = uint32_t(NativeArg(a, 1));
    if (count > 0x4000000)
    {
        RunX86(c, a);
        return;
    }
    MapDwords(p, p, int(count) * 4, [value](uint32_t x) { return x + uint32_t(value); });
}

void ScnVm::MatchRecords(ScnContext&, const ScnNativeArgs& a)
{
    int b = a.B, l = a.Stack;
    openshiina::Bytes record(40), other(40);
    while (Read32(b + 0x174) != Read32(b + 0x170))
    {
        bool matched = false;
        int left = Read32(b + 0x178);
        if (left != 0)
        {
            ReadBytes(Read32(l + 4), record.data(), 40);
            for (int entry = Read32(l + 8);; entry += 0x30)
            {
                if (Read32(entry) == -1)
                    continue;
                ReadBytes(entry, other.data(), 40);
                if (record == other)
                {
                    Write32(entry, -1);
                    Write32(b + 0x178, Read32(b + 0x178) - 1);
                    matched = true;
                    break;
                }
                if (--left == 0)
                    break;
            }
        }
        if (!matched)
            Write32(Read32(Read32(l + 4)) + 0x5C, 1);
        Write32(b + 0x174, Read32(b + 0x174) + 0x30);
        Write32(l + 4, Read32(l + 4) + 0x30);
    }
    if (Read32(b + 0x178) == 0)
        return;
    for (int entry = Read32(l + 8);; entry += 0x30)
    {
        if (Read32(entry) != -1)
            Write32(Read32(entry) + 0x5C, 1);
        int left = Read32(b + 0x178) - 1;
        Write32(b + 0x178, left);
        if (left == 0)
            break;
    }
}

} // namespace openshiina::scripting
