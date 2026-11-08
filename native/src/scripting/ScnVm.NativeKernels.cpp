// C# Scripting/ScnVm.NativeKernels.cs: C++ versions of embedded x86 routines that run hot, written by
// hand: the zooms, pans, blends and blurs of the scripts' pictures. Each is found by the signature
// of the whole routine (X86Routine) and gives the bytes the x86 code gives.
//
// Most come in builds with the same instructions: the v2.47 one ends with "ret 4", the v2.49 one
// (which v2.50 games share for some routines) with "ret"; the padding between their blocks differs
// too, so their signatures do. Some games carry builds of their own (a v2.50 scale32 of v2.47's
// blocks with "ret", an SSE2 one, Can Fes!'s): each is registered with its signature. (The C#
// player's xorstream, found by its bytes, is ScnVm::NativeByPattern in ScnVm.Native.cpp.)
#include "scripting/ScnVm.h"
#include "scripting/ParallelRows.h"

#include <algorithm>
#include <cstring>

#include "scripting/NativeHelpers.h"

namespace openshiina::scripting {

void ScnVm::RegisterNativeKernels()
{
    // Sorcery Jokers START 429E0 (MMX): a 1280 x 720 movie frame (BGR, rows of 1920 pixels)
    // enlarged to 1920 x 1080 in rows as long, each 2 x 2 pixels a b / c d becoming 3 x 3 (per byte,
    // rounded down): a, (a + b) >> 1, b / (a + c) >> 1, (a + b + c + d) >> 2, (b + d) >> 1 / c,
    // (c + d) >> 1, d. l[0] dst, l[1] src. It keeps a mask (0xFFFFFF, 0) and its count of row pairs
    // (360, down to 0) in the 12 bytes 0x30 before itself, and finds them with a call to the next
    // instruction
    RegisterNative("5CACF83E36A7430DE10BFB2C07321C6A72C6CC87", "movie 3:2",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.EnlargeMovie(c, a); });

    // Re:Rem Plus / Maki Fes! START 8BBD0: f[0] = the 16 bytes at l[0] against those at l[1] (repe
    // cmpsb, signed bytes: -1, 0, 1); the compare 0294 sorts the montage table with (81,301 entries
    // in Re:Rem Plus, so about 1.5 million calls at start)
    RegisterNative("EE95B19696EABB2722809AF27EBEFAD9A6266667", "compare16", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
        int x = vm.NativeArg(a, 0), y = vm.NativeArg(a, 1), result = 0;
        for (int k = 0; k < 16; k++)
        {
            int8_t p = int8_t(vm.ReadByte(x + k)), q = int8_t(vm.ReadByte(y + k));
            if (p != q)
            {
                result = p > q ? 1 : -1;
                break;
            }
        }
        vm.Write32(a.F, result);
    });

    // Oreimo START 79366 (Sena 797B4; MMX): dst = A' * w + B' * (257 - w) >> 8 byte by byte over
    // 32-bit pixels, w = rB * 257 / (rA + rB); A' takes B's colour where A's alpha is 0 (and B' A's).
    // l[0] dst, l[1] A, l[2] B, l[3..5] their row strides, l[6] width, l[7] height, l[8] rA, l[9] rB.
    // Ero-On!'s START 5E878 is other code that gives the same bytes. Saimin Seikatsu START 7384A: a
    // build that takes two pixels at a time (and a lone first one to align the destination to 8
    // bytes), the same bytes
    RegisterNative({"A4344EBEFD9887DE875827C5FC8F01C6DC49DEEF", "4A177932B7CDFCE9D2FBC516D15DCB24243F0A00",
                    "9B44AD3EA3AFD3BEE7FA87EF120DEC2088989FA9", "F4220D18813B1FBCD4BFE3D56FFECD202AA02DD8"},
                   "blend32", [](ScnVm& vm, ScnContext&, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), pa = vm.NativeArg(a, 1), pb = vm.NativeArg(a, 2);
                       int sd = vm.NativeArg(a, 3), sa = vm.NativeArg(a, 4), sb = vm.NativeArg(a, 5);
                       int width = vm.NativeArg(a, 6), height = vm.NativeArg(a, 7);
                       uint32_t ra = uint32_t(vm.NativeArg(a, 8)), rb = uint32_t(vm.NativeArg(a, 9));
                       if (width == 0 || height == 0 || ra + rb == 0)
                           return;
                       uint32_t w = uint32_t(uint64_t(rb) * 0x101 / (ra + rb)), w2 = 0x101 - w;
                       int bytes = width * 4;
                       openshiina::Bytes spareA, spareB;
                       for (int y = 0; y < height; y++, dst += sd, pa += sa, pb += sb)
                       {
                           // the rows in place (copied when they share bytes with the row written)
                           const uint8_t* rowA = vm.ReadView(pa, bytes, spareA, dst, bytes);
                           const uint8_t* rowB = vm.ReadView(pb, bytes, spareB, dst, bytes);
                           uint8_t* output = vm.Bytes(dst, bytes);
                           for (int p = 0; p < bytes; p += 4)
                           {
                               bool aClear = rowA[p] == 0, bClear = rowB[p] == 0;
                               for (int k = 0; k < 4; k++)
                               {
                                   uint32_t va = aClear && k > 0 ? rowB[p + k] : rowA[p + k];
                                   uint32_t vb = bClear && k > 0 ? rowA[p + k] : rowB[p + k];
                                   uint32_t sum = (va * w + vb * w2) & 0xFFFF;
                                   output[p + k] = uint8_t(std::min(255u, sum >> 8));
                               }
                           }
                       }
                   });

    // Pure Love! (v2.47, MMX, the routine after xorstream): a rectangle of 32-bit pixels moved by a
    // fraction of a pixel, each byte the 2 x 2 source pixels from the one at the same place with the
    // weights (16 - fx)(16 - fy), fx(16 - fy), (16 - fx)fy, fx fy, >> 8 (16-bit lanes: pmullw,
    // paddw). l[0] dst, l[1] src, l[2] / l[3] their row strides, l[4] width, l[5] height, l[6] fx,
    // l[7] fy (sixteenths). Counts of 0 or less run as x86 code. Saimin Seikatsu START 72C9E: a build
    // with a loop of its own for each of fx and fy 0 or not (a plain copy when both are 0), the same bytes
    RegisterNative({"5763B8B12978519D193DA1B8FA85C2C96E97DB7D", "F88736CDB18021E599B041F6292C7AF13E8A3A62"}, "shift32",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1), sd = vm.NativeArg(a, 2), ss = vm.NativeArg(a, 3);
                       int width = vm.NativeArg(a, 4), height = vm.NativeArg(a, 5), fx = vm.NativeArg(a, 6), fy = vm.NativeArg(a, 7);
                       if (width <= 0 || height <= 0 || width > 0x1000000)
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       uint32_t w00 = uint16_t((16 - fx) * (16 - fy)), w01 = uint16_t(fx * (16 - fy));
                       uint32_t w10 = uint16_t((16 - fx) * fy), w11 = uint16_t(fx * fy);
                       int bytes = width * 4;
                       openshiina::Bytes spareA, spareB;
                       for (int y = 0; y < height; y++, dst += sd, src += ss)
                       {
                           // the two source rows in place (copied when they share bytes with the row written)
                           const uint8_t* top = vm.ReadView(src, bytes + 4, spareA, dst, bytes);
                           const uint8_t* bottom = vm.ReadView(src + ss, bytes + 4, spareB, dst, bytes);
                           uint8_t* output = vm.Bytes(dst, bytes);
                           for (int k = 0; k < bytes; k++)
                               output[k] = uint8_t(uint16_t(top[k] * w00 + top[k + 4] * w01 + bottom[k] * w10 + bottom[k + 4] * w11) >> 8);
                       }
                   });

    // Oreimo START 76388 (Sena 767D4; MMX, 0x16157): copies a rectangle of a 32-bit picture to another
    // with positions in 1/16 pixels (bilinear weights at the fractions, partial coverage in the alpha
    // of the edge pixels). Written out in ScnVm.Subpixel32.cpp. l[0] dst, l[1] its row stride, l[2] /
    // l[3] dst x / y, l[4] / l[5] width / height, l[6] src, l[7] its stride, l[8] / l[9] src x / y
    // (all 1/16 pixels)
    RegisterNative({"D39944F06B961BEDC6A0F25C3C4F5A87D184E244", "FDDD8FF834069B7A80B9424B868B9D71F6BDD4E8"}, "subpixel32",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       if (!vm.Subpixel32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4),
                                          vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9)))
                           throw vm.Error(c, "Embedded x86 routine (subpixel32): a rectangle under one pixel (the x86 code would not end)");
                   });

    // Maki Fes! START 850F8, Re: Rem Plus 85C68 (Bitch Nee-chan C17B8, other bytes; Gensou no Idea
    // A4724, v2.49, Re: Rem Plus's instructions with other padding): v2.50's build of the same copy -
    // its loop over the rows from two source rows draws one row fewer and the partly covered bottom
    // row is not drawn (Subpixel32, v250). Arguments as above
    RegisterNative({"A7DB830A1F217956E69E27B70C2AA00DC6544167", "47305BC5708566203ED531C87A4B0B177FA9EC3D",
                    "2C8D9AB9900E63A477E3F11747B09773B312EFD1"},
                   "subpixel32 v2.50", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       if (!vm.Subpixel32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4),
                                          vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9), true))
                           throw vm.Error(c, "Embedded x86 routine (subpixel32): a rectangle under one pixel (the x86 code would not end)");
                   });

    // Sis△Camp START C1E08 (Kyuuketsuki no Libra's C0668 but for the partly covered right pixel; The
    // Pillagers of Raillore 3D2C44, other padding): every row drawn, as v2.47's build draws them
    // (Subpixel32, averageEdge). Arguments as above
    RegisterNative({"5E66FE0CE791241C0CD6FE7991B5368C3CC00308", "60F3511CD794A57D2C8AFDC92BFC3D9822298A92"}, "subpixel32 (Sis\xE2\x96\xB3" "Camp)",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       if (!vm.Subpixel32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4),
                                          vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9), false,
                                          true))
                           throw vm.Error(c, "Embedded x86 routine (subpixel32): a rectangle under one pixel (the x86 code would not end)");
                   });

    // The Pillagers of Raillore START 3D39F9: Sis△Camp's build but for the partly covered bottom row
    // from two source rows, which it does not draw (Subpixel32, averageEdge and bottomTwo false).
    // Arguments as above
    RegisterNative("44E63699447A41599F15C8F84A9A5464F0C0E517", "subpixel32 (Raillore)", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        if (!vm.Subpixel32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4), vm.NativeArg(a, 5),
                           vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9), false, true, false))
            throw vm.Error(c, "Embedded x86 routine (subpixel32): a rectangle under one pixel (the x86 code would not end)");
    });

    // Oreimo START 77108 (Azu 6BBB8; Sena 77556 and the other v2.49 games; Re: Rem Plus 866A8, Maki
    // Fes! 85B38; Sis△Camp C375C, The Pillagers of Raillore 3D459A and Gensou no Idea A5167, Re: Rem
    // Plus's with other padding; MMX): the enlarging case of the same copy, written out in
    // ScnVm.Enlarge32.cpp. Arguments as scale32's; rectangles the C++ version does not take run as x86 code (RunX86)
    RegisterNative({"67FD294324E188D2D3891314AB51537274147A92", "5F055480EAF613D53B5AAF6EE2475EA9FF247A76",
                    "E17C33CA39A48D249E621A1EB13D0FBE757B99D8", "F420A46FA68357CAF25DF581DD343F81250B5636",
                    "7F07033B27136BB4DD8136A878743AAE486A98B9", "3D545DD87ACBF3246E61ECA4D4FFEF7464931B44"},
                   "enlarge32", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       if (!vm.Enlarge32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4),
                                         vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9),
                                         vm.NativeArg(a, 10), vm.NativeArg(a, 11)))
                           vm.RunX86(c, a);
                   });

    // Oreimo START 78380 (Sena 787CE, Re: Rem Plus 87920; MMX, 0x1632C): the scaling case of the same
    // copy (source and destination rectangles of different sizes, scaling down), with two work
    // buffers. Written out in ScnVm.Scale32.cpp. l[0] dst, l[1] its stride, l[2..5] dst left, top,
    // right, bottom, l[6] src, l[7] its stride, l[8..11] src left, top, right, bottom (1/16 pixels),
    // l[12] / l[13] work buffers
    RegisterNative({"3481E54756B5B7C00408FD13CDD6C7A5F211DC97", "9122A668741F24CA756ABDBB886B20C41F54CB61",
                    "B4622533F3686687C2F5D363E992BE9B65E959EB"},
                   "scale32", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       if (!vm.Scale32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4),
                                       vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9),
                                       vm.NativeArg(a, 10), vm.NativeArg(a, 11)))
                           throw vm.Error(c, "Embedded x86 routine (scale32): rectangles it cannot scale (the x86 code would not end)");
                   });

    // Can Fes! START 73A40: scale32 but for three instructions (see ScnVm.Scale32.cpp: the first
    // column's last source pixel), its tables in the work buffers l[12] (columns) and l[13] (rows) as
    // the x86 code leaves them
    RegisterNative("3A1DE43A021F4FED38650FE58BF37857FA1AEED5", "scale32 (Can Fes!)", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
        if (!vm.Scale32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4), vm.NativeArg(a, 5),
                        vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9), vm.NativeArg(a, 10), vm.NativeArg(a, 11),
                        vm.NativeArg(a, 12), vm.NativeArg(a, 13), true))
            throw vm.Error(c, "Embedded x86 routine (scale32): rectangles it cannot scale (the x86 code would not end)");
    });

    // Bitch Nee-chan START C7C70 (Bitch Gakuen CA1E0; Ren'ai Saimin 8A480 the same code ending in "ret
    // 4"): scale32's SSE2 build, written out in ScnVm.Scale32Sse.cpp. Arguments as scale32's; cases
    // the C++ version does not take run as x86 code
    RegisterNative({"A3DCAB9DFB6CC480BF9F3995AEC7212737E5EB95", "C1F86E973B6FCA437C503E634C490888735452BF"}, "scale32 (SSE2 build)",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       if (!vm.Scale32Sse(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4),
                                          vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9),
                                          vm.NativeArg(a, 10), vm.NativeArg(a, 11)))
                           vm.RunX86(c, a);
                   });

    // Bitch Nee-chan START 9F5E6 (SSE2; Bitch Gakuen A1B46, Bitch Shimai A1712: the same code, its two
    // work buffers 4 bytes further on; Chuuni Hime no Teikoku 9406E, 8 bytes; Chiccha na Hanayome
    // 9764A, 20 bytes; Libra of the Vampire Princess B93E3, 27 bytes, its fan disc B90AF, 31 bytes;
    // The Pillagers of Raillore 3B0939, 21 bytes): the blur of ScnVm.Blur32.cpp. l[0] dst, l[1] its
    // stride, l[2] width, l[3] height, l[4] src, l[5] its stride, l[6] / l[7] the radii across and down
    RegisterNative({"1276382986E6312B58F7F1B31302308A07B97B92", "CD7D21630A140CC934220F956BB41155BF171ACF",
                    "E4A694FDFEB6B98B6B486A905F1B3116D4C4DBA9", "1C6CA4B22558BD5DDE1CBD2C2D3FF11E3ED6D1DE",
                    "AD91701A1AAD2E04C4C45F84178FDADBBB22B95D", "C2A3BC695326D0A369634C6A4922F01C4E93C14C",
                    "1D845BAE4B226F73879EBDE7E29E9CF94319678E"},
                   "blur32", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       if (!vm.Blur32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4),
                                      vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7)))
                           vm.RunX86(c, a);
                   });

    // Oreimo START 796E5 (Sena 79B33; MMX, the rule fade of 0x16EB3): dst = S with its alpha byte made
    // from the rule mask M (the mask's top byte m, or 255 - m when dir is set): m <= imin gives 0,
    // m >= imax keeps S's alpha s, between them ((m - imin') * q in 16 bits, signed) * s >> 15 with
    // q = 0x8080 / (imax - imin + 1) and imin' = max(imin - 1, 0) (imin 0: 0). Comparisons are signed
    // 16-bit (pcmpgtw). l[0] dst, l[1] S, l[2] M, l[3..5] their row strides, l[6] width, l[7] height,
    // l[8] imax, l[9] imin, l[10] dir. Re: Rem Plus 88C84 and Maki Fes! 88114 are the same
    // instructions (padding and ret apart)
    RegisterNative({"42BE6C919F9A6508C03AAA5B66020C3951CC7C05", "5D646499AC90624AAED39ACAC0A3AD67B53C2A81",
                    "60E811498D402206EC8562EE495A115C4A828B79"},
                   "rulealpha", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.RuleAlpha(c, a, vm.NativeArg(a, 10) != 0); });
    // Can Fes! START 74D95: the same without dir (ten arguments, the mask never inverted)
    RegisterNative("8100FD05AC66CFA023418D590A8F2FA1C8B678D1", "rulealpha",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.RuleAlpha(c, a, false); });

    // Ero-On! START 5E2D6 (MMX, the scaling-down case of 0x14CC0): the first v2.49 build's own way of
    // Oreimo's scale32, written out in ScnVm.Scale16.cpp. l[0] dst, l[1] its stride, l[2..5] dst
    // left, top, right, bottom, l[6] src, l[7] its stride, l[8..11] src left, top, right, bottom (1/16
    // pixels), l[12] / l[13] the column / row weights, l[14] / l[15] the column / row entries (24
    // bytes each). The x86 code makes the row entries before the column tables: where those run into
    // them, it runs as x86 code instead. Saimin Seikatsu (v2.47) START 732A8 is another build of it,
    // the same bytes
    RegisterNative({"31DAD459219DC78270C7147720CA1959DFEA5AFB", "28E03A81F50DA1148767337586B04780E33409B6"}, "scale16",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) {
                       int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
                       int dl = vm.NativeArg(a, 2), dt = vm.NativeArg(a, 3), dr = vm.NativeArg(a, 4), db = vm.NativeArg(a, 5);
                       int sl = vm.NativeArg(a, 8), sr = vm.NativeArg(a, 10);
                       int64_t columns = uint32_t(dr - dl) >> 4, rows = uint32_t(db - dt) >> 4;
                       int64_t colWeights = vm.NativeArg(a, 12), colEntries = vm.NativeArg(a, 14), rowEntries = vm.NativeArg(a, 15);
                       int64_t rowEnd = rowEntries + rows * 24;
                       auto overlaps = [&](int64_t from, int64_t length) { return from < rowEnd && rowEntries < from + length; };
                       if (overlaps(colWeights, (int64_t(uint32_t(dr - dl)) + 1) * 8) || overlaps(colEntries, columns * 24))
                       {
                           vm.RunX86(c, a);
                           return;
                       }
                       if (!vm.Scale16(dst, pitch, dl, dt, dr, db, vm.NativeArg(a, 6), vm.NativeArg(a, 7), sl, vm.NativeArg(a, 9), sr,
                                       vm.NativeArg(a, 11)))
                           throw vm.Error(c, "Embedded x86 routine (scale16): rectangles it cannot scale (the x86 code would not end)");
                   });

    // Ero-On! START 5DFA5 (MMX, the enlarging case of 0x14BCE), written out in ScnVm.Enlarge16.cpp.
    // l[0] dst, l[1] its stride, l[2..5] dst left, top, right, bottom, l[6] src, l[7] its stride,
    // l[8..11] src left, top, right, bottom (1/16 pixels), l[12] the weight table. Rectangles it does
    // not enlarge run as x86 code. Saimin Seikatsu (v2.47) START 72F77 is another build of it, the
    // same bytes; Ran→Sem's (v2.47) START 9C8AA, Boku Igai no Otoko o Shiranai Kanojo's (v2.49)
    // 6BDFE and Tanetsuke Mura's (v2.48) E89B3, other padding
    RegisterNative({"27A535558D763B2B8F0008F2476F0FA14EB45AAB", "1C0B5DBD306E520A9EB62FA41DF3484C397A4C39",
                    "B36E81F1360892F4CD8E7B6FE2C8AA0765B2FF23", "91176C1C026354F51ED12B93A2BB29146DD8F02D",
                    "9DFA1CCFC1A847CDC5187D8F50D2C764D14068DC"},
                   "enlarge16", [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Enlarge16Native(c, a, 257, false); });
    // Hana to Otome ni Shukufuku o Royal Bouquet START 4D062 (v2.47): the same but for its weight
    // table, i * 256 / sW, and the first byte of each pixel it mixes, made FF (por with 0xFF after
    // packuswb); the pixels it copies keep theirs. Arguments as above. Imouto Smile START 6DB16
    // (v2.47), other padding; Gakushoku no Oba-san The Motion 60A42, ending in "ret"
    RegisterNative({"D6BC2828FE749484BEC89684F01FD4469FF9BA0C", "0169B9BC91EFAFDFFF4DD7F9D7796D093E121ED9", "AF7B6BFFD4E08A2B87186278BDAEE944BE215278"},
                   "enlarge16 (opaque)",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Enlarge16Native(c, a, 256, true); });
    // Nakadashi Trilogy's Miko Dashi START 64396 (v2.47): that weight table, nothing made opaque
    RegisterNative("39773A9714C8846EA6402C83B201C0FEA2A58775", "enlarge16",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.Enlarge16Native(c, a, 256, false); });
    // Miko Dashi START 646B8 (MMX): the scaling-down case beside it, written out in ScnVm.ScaleArea.cpp
    RegisterNative("50C6BCDFA745FC3B3FC7F4785C7C2BC691D7893B", "scale area",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.ScaleArea(c, a, false); });
    // Pure Love! 5A163 and Iyashinbo The Motion 5FBF3 (the same bytes), Gakushoku no Oba-san The
    // Motion 60DA1: the same, each pixel's first byte made FF
    RegisterNative({"DE002226CC93F6BF1FDCC5650EA61E0CE00339C7", "3E60D8F60F11B9D53570CAA6049A3FD0D66955B1"}, "scale area (opaque)",
                   [](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.ScaleArea(c, a, true); });
}

void ScnVm::Enlarge16Native(ScnContext& c, const ScnNativeArgs& a, int scale, bool opaque)
{
    // enlarge16's arguments (RegisterNativeKernels) checked, its rectangles enlarged
    int dst = NativeArg(a, 0), pitch = NativeArg(a, 1);
    int dl = NativeArg(a, 2), dt = NativeArg(a, 3), dr = NativeArg(a, 4), db = NativeArg(a, 5);
    int sl = NativeArg(a, 8), st = NativeArg(a, 9), sr = NativeArg(a, 10), sb = NativeArg(a, 11);
    if (sr - sl < 0 || sb - st < 0 || dr - dl < sr - sl || db - dt < sb - st)
    {
        RunX86(c, a);
        return;
    }
    if (!Enlarge16(dst, pitch, dl, dt, dr, db, NativeArg(a, 6), NativeArg(a, 7), sl, st, sr, sb, NativeArg(a, 12), scale, opaque))
        throw Error(c, "Embedded x86 routine (enlarge16): rectangles too large");
}

void ScnVm::RuleAlpha(ScnContext& c, const ScnNativeArgs& a, bool invert)
{
    // The rule fade's alpha (rulealpha, see RegisterNativeKernels); the mask's byte is 255 - m when invert
    int dst = NativeArg(a, 0), ps = NativeArg(a, 1), pm = NativeArg(a, 2);
    int sd = NativeArg(a, 3), ss = NativeArg(a, 4), sm = NativeArg(a, 5);
    int width = NativeArg(a, 6), height = NativeArg(a, 7);
    int imax = NativeArg(a, 8), imin = NativeArg(a, 9);
    if (width == 0 || height == 0)
        return;
    uint32_t divisor = uint32_t(imax - imin + 1);
    if (divisor == 0)
        throw Error(c, "Embedded x86 routine (rule alpha): division by zero");
    uint16_t q = uint16_t(0x8080u / divisor);
    uint16_t bias = uint16_t(imin == 0 ? 0 : imin - 1);
    int16_t max = int16_t(imax), min = int16_t(imin);
    int bytes = width * 4;
    openshiina::Bytes spareS, spareM;
    for (int y = 0; y < height; y++, dst += sd, ps += ss, pm += sm)
    {
        // S's row into the destination row, then its alpha bytes from the mask (the rows in place;
        // copied when they share bytes with the row written)
        const uint8_t* rowS = ReadView(ps, bytes, spareS, dst, bytes);
        const uint8_t* rowM = ReadView(pm, bytes, spareM, dst, bytes);
        uint8_t* output = Bytes(dst, bytes);
        std::memmove(output, rowS, size_t(bytes));
        for (int p = 0; p < bytes; p += 4)
        {
            int16_t m = int16_t(invert ? 255 - rowM[p + 3] : rowM[p + 3]);
            bool belowMax = max > m, inside = belowMax && m > min;
            int s = rowS[p];
            uint16_t w = inside ? uint16_t(m - bias) : uint16_t(0);
            int product = int16_t(uint16_t(uint32_t(w) * q)) * s;
            uint32_t v = uint32_t(product) >> 15;
            output[p] = uint8_t(v | (belowMax ? 0u : uint32_t(s)));
        }
    }
}

void ScnVm::EnlargeMovie(ScnContext& c, const ScnNativeArgs& a)
{
    // The 3:2 movie enlargement of Sorcery Jokers (RegisterNativeKernels)
    constexpr int Pitch = 1920 * 3, PairRows = 360, Pairs = 640;
    int dst = NativeArg(a, 0), src = NativeArg(a, 1), scratch = m_nativeTarget - 0x30;
    constexpr int SrcBytes = Pitch * (2 * PairRows - 1) + Pairs * 6, DstBytes = Pitch * 3 * PairRows;
    if (!IsContiguous(src, SrcBytes) || !IsContiguous(dst, DstBytes) || !Apart(uint32_t(src), SrcBytes, uint32_t(dst), DstBytes)
        || !Apart(uint32_t(scratch), 12, uint32_t(src), SrcBytes) || !Apart(uint32_t(scratch), 12, uint32_t(dst), DstBytes))
    {
        RunX86(c, a);
        return;
    }
    // Rows of pairs on all cores
    ParallelRows::For(PairRows, int64_t(1920) * 1080, [&](int from, int to) {
        for (int y = from; y < to; y++)
        {
            const uint8_t* top = Bytes(src + 2 * y * Pitch, Pairs * 6);
            const uint8_t* bottom = Bytes(src + (2 * y + 1) * Pitch, Pairs * 6);
            uint8_t* r0 = Bytes(dst + 3 * y * Pitch, Pitch);
            uint8_t* r1 = Bytes(dst + (3 * y + 1) * Pitch, Pitch);
            uint8_t* r2 = Bytes(dst + (3 * y + 2) * Pitch, Pitch);
            for (int i = 0, o = 0; i < Pairs * 6; i += 6, o += 9)
                for (int k = 0; k < 3; k++)
                {
                    int pa = top[i + k], pb = top[i + 3 + k], pc = bottom[i + k], pd = bottom[i + 3 + k];
                    r0[o + k] = uint8_t(pa);
                    r0[o + 3 + k] = uint8_t((pa + pb) >> 1);
                    r0[o + 6 + k] = uint8_t(pb);
                    r1[o + k] = uint8_t((pa + pc) >> 1);
                    r1[o + 3 + k] = uint8_t((pa + pb + pc + pd) >> 2);
                    r1[o + 6 + k] = uint8_t((pb + pd) >> 1);
                    r2[o + k] = uint8_t(pc);
                    r2[o + 3 + k] = uint8_t((pc + pd) >> 1);
                    r2[o + 6 + k] = uint8_t(pd);
                }
        }
    });
    Write32(scratch, 0xFFFFFF);
    Write32(scratch + 4, 0);
    Write32(scratch + 8, 0);
}

} // namespace openshiina::scripting
