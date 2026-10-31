// C# versions of the smaller embedded x86 routines: copies, strings, tables, checksums, and the
// pixel loops that are not zooms or blends (sepia, thumbnails, a same-size zoom). Each is found
// by the signature of the whole routine (X86Routine.Signature, one per build) like the kernels of
// ScnVm.NativeKernels.cs and gives the bytes the x86 code gives; cases it does not take (blocks
// that overlap in a way the result depends on, counts the x86 code would wrap round to 4
// billion) run as x86 code (RunX86). The builds differ by engine version (v2.47's routines end
// with "ret 4", v2.49's and v2.50's with "ret"); the offsets named are in the START.SCN of a game
// of that version (Oreimo Plus, Sena Plus, Re: Rem Plus) unless said otherwise. Left out: the SSE2
// build of the zoom in those versions, which never runs: their scripts take the MMX paths, as
// the CPU the interpreter reports has no SSE.

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>Two blocks of memory share no byte.</summary>
    private static bool Apart(long a, long aLength, long b, long bLength) => a + aLength <= b || b + bLength <= a;

    /// <summary>The bytes rows of rowBytes at start + k * stride (k below rows) lie in.</summary>
    private static (long Start, long Length) RowsSpan(int start, int stride, int rows, long rowBytes)
    {
        long first = start, last = start + (long)stride * (rows - 1);
        long low = Math.Min(first, last);
        return (low, Math.Max(first, last) + rowBytes - low);
    }

    private static bool Apart((long Start, long Length) a, (long Start, long Length) b) => Apart(a.Start, a.Length, b.Start, b.Length);

    private void SetL(ScnNativeArgs args, int index, int value) => Write32(args.Stack + 4 * index, value);

    private void RegisterNativeRoutines()
    {
        // Aneiro START 88717: a lone "ret" (the routine after it starts at 88718), called after
        // each file the scripts load themselves (0154 / 0156): a step this build leaves empty
        RegisterNative("381CEBC19F264AFD8F65390B065E73D7B30254FC", "ret", (vm, c, a) => { });

        // START 75A84 / 75EE4 / 85384: the CPU's features (CPUID: 1 Intel, 2 AMD, 10h MMX, 20h SSE,
        // 40h SSE2, 80h SSE3...) into l[0] and into the dword before the routine (gCPUID). The
        // CPU the interpreter reports (X86Cpu.Cpuid: an Intel with MMX, no SSE) gives 0x11, so
        // START takes its routines without SSE2 (Re: Rem Plus's own SSE2 zoom too). A START that
        // will not run without SSE2 (Bitch Nee-chan's "SSE2 をサポートしていません", Sorcery
        // Jokers' "CPU does not supported to SSE2"; "ＣＰＵが《ＳＳＥ２》に対応していません" is a
        // game's that none of the games here has, found by the retrom-project fork) is told SSE
        // and SSE2 as well (0x71)
        RegisterNative(["B6FF80115E4E2727617C10E780653984637EDB0D", "9C94055F3764EB20CCEA440A6A01BE9363DBC93F"], "cpuid", (vm, c, a) =>
        {
            var code = vm.ReadBytes(c.CodeBase, c.CodeSize).AsSpan();
            int features = code.IndexOf(Encodings.cp932.GetBytes("SSE2 をサポートしていません")) >= 0
                           || code.IndexOf(Encodings.cp932.GetBytes("ＣＰＵが《ＳＳＥ２》に対応していません")) >= 0
                           || code.IndexOf("does not supported to SSE2"u8) >= 0 ? 0x71 : 0x11;
            // The interpreter's CPUID has no SSE: nothing to verify the SSE2 answer with
            using var verify = features == 0x11 ? vm.VerifyRegions(c, a, (a.Stack, 4), (vm.m_nativeTarget - 4, 4)) : null;
            vm.SetL(a, 0, features);
            vm.Write32(vm.m_nativeTarget - 4, features);
        });

        // START 75B51 / 75FAF: the save thumbnail, 100 x 75 from an 800 x 600 BGR picture: each
        // byte the average of an 8 x 8 block. Ero-On! 5D7A0: 16 x 12 from blocks of 50 x 50.
        // l[0] dst (bytes in a row), l[4] src (rows of 2400 bytes)
        RegisterNative(["DCEE38C4FB3BC259CFB217ABD2D2F712B23BB57A", "1444F8C617F489497F12A55553446AC99F217C14"], "thumbnail",
                       (vm, c, a) => vm.Thumbnail(c, a, 8, 100, 75));
        RegisterNative("1174F52A9A080EAEF9B086E491846FF3D5B35EB8", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 50, 16, 12));
        // Can Fes! START 71211: 68 x 51 from blocks of 11 x 11 (the rest of the 800 x 600 picture left out)
        RegisterNative("4D0B738FCFB0B45B214E4DFE0C8F5F46129F0B09", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 11, 68, 51));
        // Ren'ai Saimin START 865B1 (v2.47): 84 x 63 from blocks of 9 x 9 (the rest left out too);
        // Saimin Seikatsu START 72760: 76 x 56 from blocks of 10 x 10
        RegisterNative("B51FD501F22CF51D019EF26160C1C35D6154CB2C", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 9, 84, 63));
        RegisterNative("13CE585B31BB34E5FA674268BD852030B126B3E6", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 10, 76, 56));
        // Chuuchuu Nurse START 70036AF3 / 70036B67 (v2.45): 160 x 120 from blocks of 5 x 5, and the
        // same code with blocks of 2 x 2 for 400 x 300
        RegisterNative("42515BD6BA1FCC24A41CA6A734692CC90E6577DB", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 5, 160, 120));
        RegisterNative("2853439284672B39391C2DEA48F204032D81382A", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 2, 400, 300));
        // Gensou no Idea START A3F6F (v2.49, 1280 x 720): 192 x 108 from blocks of 6 x 6, rows of 3,840 bytes
        RegisterNative("A358C4BDF44489F7DFB8A57F3BF7D3B7E63A31FF", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 6, 192, 108, 3840));
        // Gensou no Idea F5440 (The Pillagers of Raillore EFCLIB 3568, Chiccha na Hanayome EFCLIB
        // 2C29, other padding): l[3] dwords from l[4] to l[0], each XORed with the byte l[5] in
        // all four bytes (b | b << 8, then | << 16, as the x86 code builds it); a count under 1
        // runs as x86 code
        RegisterNative(["304020D68894542344EA2C8F8D67586EF480BE6F", "EC6DD9EC7E160D8BA47F26DB38B9A688C63951ED",
                        "1C6C23B7891138CF24BA9870804BA7213B69A774"], "xor dwords", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), count = vm.NativeArg(a, 3), src = vm.NativeArg(a, 4);
            uint key = (uint)vm.NativeArg(a, 5);
            key |= key << 8;
            key |= key << 16;
            if (count <= 0 || count > 0x4000000)
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, count * 4);
            if (dst == src || Apart(dst, count * 4L, src, count * 4L))
            {
                // a dword is read before it is written: in place too
                byte[] bytes = vm.ReadBytes(src, count * 4);
                var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
                for (int i = 0; i < words.Length; i++)
                    words[i] ^= key;
                vm.WriteBytes(dst, bytes);
            }
            else
                for (int i = 0; i < count; i++)
                    vm.Write32(dst + 4 * i, vm.Read32(src + 4 * i) ^ (int)key);
        });
        // Ao no Juuai 188BE (START2 18950; v2.34): the same 100 x 75 thumbnail, f[0] src, f[1] dst
        RegisterNative("FBBBFACF8D7BA58AC19D6E3581A07D1C7E6E96BB", "thumbnail",
                       (vm, c, a) => vm.Thumbnail(c, a, 8, 100, 75, vm.Read32(a.F + 4), vm.Read32(a.F)));
        // Ayakashibito START2 12B77 (v2.34): 80 x 60 from blocks of 10 x 10, the same arguments
        RegisterNative("D9036751E14B2CD371970392C16FD29EB60CF5A8", "thumbnail",
                       (vm, c, a) => vm.Thumbnail(c, a, 10, 80, 60, vm.Read32(a.F + 4), vm.Read32(a.F)));

        // The Pillagers of Raillore START 3DAE54 / 3DAEA7 (SSE2): a 960 x 540 / 640 x 360 32-bit
        // picture enlarged two / three times into 1920 x 1080, each pixel repeated across and down.
        // l[0] dst, l[1] its stride, l[2] src, l[3] its stride
        RegisterNative("1A0CA904E74263F80CC1A2BBAC2E6AF86C1C838C", "enlarge x2", (vm, c, a) => vm.EnlargeWhole(c, a, 2, 960, 540));
        RegisterNative("F96B154D20F92ABAA59589044921B28E7DADBAE9", "enlarge x3", (vm, c, a) => vm.EnlargeWhole(c, a, 3, 640, 360));
        // Raillore 3DAFE3: 480 x 270 four times; 3D9DFC (MMX): 1280 x 720 into 1920 x 1080, each 2 x 2
        // pixels into 3 x 3 - the corners as they are, the edges' middles the averages of their
        // two, the middle the average of all four (Sorcery Jokers' movie 3:2 in 32 bits)
        RegisterNative("5D890BED7B136019B7D8848E93992501D2519721", "enlarge x4", (vm, c, a) => vm.EnlargeWhole(c, a, 4, 480, 270));
        RegisterNative("11726E4688DF316677B49171683930D9ED17D545", "enlarge 3:2", (vm, c, a) => vm.EnlargeThreeHalves(c, a));
        // Raillore 3D243F: the 750 x 420 thumbnail of a 1920-pixel wide 24-bit picture, each byte the
        // average of 2 x 2; 3D24B7: 420 rows of 2,250 bytes from l[4] (rows 5,760 bytes apart) to
        // l[0] one after the other; 3D2765: the other way, from l[0] to l[1], rows l[2] bytes apart
        RegisterNative("A15FAA3A37D529AE5C73C12F7D5647E0F9DBF7ED", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 2, 750, 420, 5760));
        RegisterNative("EDC2AAF8FECE0B007FE9E8E6A84DB7491C835855", "rows 750 x 420", (vm, c, a) =>
            vm.CopyRows(c, a, vm.NativeArg(a, 0), 2250, vm.NativeArg(a, 4), 5760, 420, 2250));
        RegisterNative("9AAFA46F7AE7F2DC37E76C780EDEC870012BB309", "rows 750 x 420", (vm, c, a) =>
            vm.CopyRows(c, a, vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 0), 2250, 420, 2250));
        // Raillore 3D27BC: 750 x 420 pixels of 24 bits at l[0] (one after the other) into 32 bits at
        // l[1] (rows l[2] bytes apart): FF, then the pixel's three bytes
        RegisterNative("4E7AFE31B6653D60E34B305DB2A5E68023317F69", "24 to 32 bits", (vm, c, a) =>
        {
            const int Width = 750, Height = 420;
            int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1), stride = vm.NativeArg(a, 2);
            var target = RowsSpan(dst, stride, Height, Width * 4);
            if (Math.Abs((long)stride) < Width * 4 || !Apart(target, (src, (long)Width * 3 * Height)))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, (int)target.Start, (int)target.Length);
            var input = new byte[Width * 3];
            for (int y = 0; y < Height; y++, src += Width * 3, dst += stride)
            {
                vm.ReadBytes(src, input);
                var row = vm.Bytes(dst, Width * 4);
                for (int x = 0; x < Width; x++)
                {
                    row[4 * x] = 0xFF;
                    row[4 * x + 1] = input[3 * x];
                    row[4 * x + 2] = input[3 * x + 1];
                    row[4 * x + 3] = input[3 * x + 2];
                }
            }
        });
        // The save thumbnails of the 1280 x 720 games (24-bit pictures, rows of 3,840 bytes):
        // Shinigami no Testament START 8255F, 116 x 65 from blocks of 11 x 11; Tojita Sekai no Tori
        // Colony START 8824F, 240 x 135 from blocks of 5 x 5. Arguments as Gensou no Idea's
        RegisterNative("F9C7EC158AD7FFF292E04686FA8D4254D94CEFF1", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 11, 116, 65, 3840));
        RegisterNative("75728C1CF427254DADD9B1E12944A9352FA74A7B", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 5, 240, 135, 3840));
        // Rows of a thumbnail from l[0] (one after the other) to l[1]: Testament 82648, 65 rows of 348
        // bytes, 3,840 apart; Tori Colony 884E3, 135 of 720, 3,840 apart, and 8852E, l[2] apart;
        // Kanojo ga Ore ni Kureta Mono 909A4, 84 rows of 450 bytes, 3,072 apart
        RegisterNative("703F2D11977DBC27CBAF7A16F1EC6D9A2212CB62", "rows", (vm, c, a) =>
            vm.CopyRows(c, a, vm.NativeArg(a, 1), 3840, vm.NativeArg(a, 0), 348, 65, 348));
        RegisterNative("21DFA52521953EAFA0CFA2FED49D420C0D21CF52", "rows", (vm, c, a) =>
            vm.CopyRows(c, a, vm.NativeArg(a, 1), 3840, vm.NativeArg(a, 0), 720, 135, 720));
        RegisterNative("E0E81F9785A0F274BCC8FD6AB2B644AF349B9ADC", "rows", (vm, c, a) =>
            vm.CopyRows(c, a, vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 0), 720, 135, 720));
        RegisterNative("1B4BCE0BEC3B4D1CD9E7C9606521224E6C26C6B4", "rows", (vm, c, a) =>
            vm.CopyRows(c, a, vm.NativeArg(a, 1), 3072, vm.NativeArg(a, 0), 450, 84, 450));
        // Testament 827CD: l[2] bytes from l[1] to l[0], each dword inverted - eight bytes a step when
        // l[3] is 0 (the bytes past the last eight left), else four. A count of 0 runs as x86 code
        RegisterNative("2E87AFFFFF1105B79D31E2277BFF3CB1DA38B2E8", "copy inverted", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
            uint bytes = (uint)vm.NativeArg(a, 2);
            long words = vm.NativeArg(a, 3) == 0 ? (bytes >> 3) * 2L : bytes >> 2;
            if (words == 0 || words > 0x4000000 || dst != src && !Apart(dst, words * 4, src, words * 4))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)words * 4);
            var data = vm.ReadBytes(src, (int)words * 4);
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)~data[i];
            vm.WriteBytes(dst, data);
        });
        // Kanojo ga Ore ni Kureta Mono 970DC: 22 x 22 bytes to l[1], each the first byte of a pixel
        // of the 24-bit picture at l[0] (rows of 3,072 bytes) * 64 / 255
        RegisterNative("697ECC2DF28991A03AF2B0849DD0BB1CFEC99854", "mask 22 x 22", (vm, c, a) =>
        {
            const int Side = 22, Stride = 3072;
            int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1);
            if (!Apart(dst, Side * Side, src, Stride * (Side - 1) + Side * 3))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, Side * Side);
            var output = new byte[Side * Side];
            var row = new byte[Side * 3];
            for (int y = 0; y < Side; y++)
            {
                vm.ReadBytes(src + y * Stride, row);
                for (int x = 0; x < Side; x++)
                    output[y * Side + x] = (byte)((row[3 * x] << 6) / 255);
            }
            vm.WriteBytes(dst, output);
        });
        // Hana to Otome ni Shukufuku o Royal Bouquet 4CAEC: 892,740 dwords at l[1] XORed with a key
        // from l[0], after each the key + 1, rotated left by 3, its bytes reversed
        RegisterNative("B4937B7F8A71BA951426F3E44AC8D0ED313E669A", "xor stream", (vm, c, a) =>
        {
            const int Count = 0xD9F44;
            uint key = (uint)vm.NativeArg(a, 0);
            int at = vm.NativeArg(a, 1);
            using var verify = vm.VerifyRegion(c, a, at, Count * 4);
            var bytes = vm.ReadBytes(at, Count * 4);
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
            for (int i = 0; i < words.Length; i++)
            {
                words[i] ^= key;
                key = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(System.Numerics.BitOperations.RotateLeft(key + 1, 3));
            }
            vm.WriteBytes(at, bytes);
        });
        // Raillore EFCLIB 92F0 (Tojita Sekai no Tori Colony EFCLIB 666C, other padding): l[1] dwords at
        // l[0] inverted (XOR FFFFFFFF); a count under 1 runs as x86 code
        RegisterNative(["F79705977D2B17A02E3A733E04EF5BA87A031A92", "C4E79AD74DF00F25B64339C72CF808E9E7627276"], "invert dwords", (vm, c, a) =>
        {
            int at = vm.NativeArg(a, 0), count = vm.NativeArg(a, 1);
            if (count <= 0 || count > 0x4000000)
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, at, count * 4);
            var bytes = vm.Bytes(at, count * 4);
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] ^= 0xFF;
        });
        // Raillore 3D9CA7: the first byte of each pixel of 1920 x 1080 at l[0] times (the second
        // byte of l[1]'s pixel + 1), >> 8 (imul, then ah); both rows l[2] bytes apart
        RegisterNative("B830BE90B783CA51533E8B9E1E52F05F6CBCB9C5", "mask byte 0", (vm, c, a) =>
        {
            const int Width = 1920, Height = 1080, RowBytes = Width * 4;
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1), stride = vm.NativeArg(a, 2);
            var span = RowsSpan(dst, stride, Height, RowBytes);
            if (Math.Abs((long)stride) < RowBytes || dst != src && !Apart(span, RowsSpan(src, stride, Height, RowBytes)))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, (int)span.Start, (int)span.Length);
            var mask = new byte[RowBytes];
            for (int y = 0; y < Height; y++, dst += stride, src += stride)
            {
                // (the second bytes are read before the first ones are written: in place too)
                vm.ReadBytes(src, mask);
                var row = vm.Bytes(dst, RowBytes);
                for (int x = 0; x < RowBytes; x += 4)
                    row[x] = (byte)(row[x] * (mask[x + 1] + 1) >> 8);
            }
        });
        // Raillore 39A40C: the width of the string at l[0] - each byte's width from a table of 256,
        // "[" / "{" choosing l[1]'s / l[3]'s table, "]" / "}" l[2]'s again (the first one), a
        // Shift-JIS lead byte (81-9F, E0-FC) l[4] for its two bytes (the second one is not looked
        // at, a 0 there too); l[0] = the width, l[1] = the table in use at the end
        RegisterNative("6257404FB455FA61F6AEEC6B7E158251BF6C0B06", "text width", (vm, c, a) =>
        {
            int p = vm.NativeArg(a, 0), table = vm.NativeArg(a, 2), wide = vm.NativeArg(a, 4), width = 0;
            using var verify = vm.VerifyRegion(c, a, a.Stack, 8);
            for (int b; (b = vm.ReadByte(p)) != 0;)
            {
                switch (b)
                {
                    case 0x5B: table = vm.NativeArg(a, 1); p++; break;
                    case 0x5D or 0x7D: table = vm.NativeArg(a, 2); p++; break;
                    case 0x7B: table = vm.NativeArg(a, 3); p++; break;
                    case >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC: width += wide; p += 2; break;
                    default: width += vm.ReadByte(table + b); p++; break;
                }
            }
            vm.SetL(a, 0, width);
            vm.SetL(a, 1, table);
        });

        // Ao no Juuai 7F562 (START2 7F7B2; v2.34): l[0] = the index of the first word l[1] in the
        // string of words at l[0] (before its 0), else -1
        RegisterNative("974E8152ACDBC5F4413632178D75F40431C4A296", "wcsindex", (vm, c, a) =>
        {
            int start = vm.NativeArg(a, 0), result = -1;
            ushort ch = (ushort)vm.NativeArg(a, 1);
            using var verify = vm.VerifyRegion(c, a, a.Stack, 4);
            for (int p = start; ; p += 2)
            {
                ushort w = vm.Read16(p);
                if (w == 0)
                    break;
                if (w == ch)
                {
                    result = (int)((uint)(p - start) >> 1);
                    break;
                }
            }
            vm.SetL(a, 0, result);
        });

        // START 75E25 / 76273 (Ero-On! 5DA73): 600 rows of 2400 bytes from l[1] (one after the
        // other) to l[0], rows 2404 bytes apart
        RegisterNative(["F1057FFC43DC4027C0B8A90D039081D9AB4A4FF0", "87545B986579B5F66308666715D8D25592FF6D1D"], "rows600", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
            const int Rows = 600, Bytes = 2400, Stride = 2404;
            var dstSpan = RowsSpan(dst, Stride, Rows, Bytes);
            if (!Apart(dstSpan, (src, (long)Rows * Bytes)))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)dstSpan.Length);
            for (int y = 0; y < Rows; y++)
                vm.MoveMemory(dst + y * Stride, src + y * Bytes, Bytes);
        });

        // START 75EC3 / 76311: 75 rows of 300 bytes from l[0] (one after the other) to l[1], rows
        // 2400 bytes apart (a thumbnail into a picture); Ero-On! 5DB11: 12 rows of 48 bytes;
        // Re: Rem Plus 85674: 90 rows of 480 bytes, 3840 apart; Kuroneko Plus 7637C (and the v2.49
        // games after it, Sena 7635C): 75 rows of 300 bytes, l[2] apart
        RegisterNative(["EF78DA983F119F2B9CC9911836AC09DCBD855592", "674C88AE8D38EC510159CD09D1D58E75E64EABA9"], "block",
                       (vm, c, a) => vm.BlockCopy(c, a, 75, 300, 2400));
        RegisterNative("9721452713208958DEE71DA51F85540180359028", "block", (vm, c, a) => vm.BlockCopy(c, a, 12, 48, 2400));
        RegisterNative("C4839FCEF4CF13538D043A595B66E1D7B9B04E21", "block", (vm, c, a) => vm.BlockCopy(c, a, 90, 480, 3840));
        RegisterNative("0D838E63CAFB1E7CC81D190CFCDE10611E8598BA", "block", (vm, c, a) => vm.BlockCopy(c, a, 75, 300, vm.NativeArg(a, 2)));
        // Chiccha na Hanayome B9224 / B9270: 75 rows of 396 bytes, 3840 apart / l[2] apart
        RegisterNative("8477782400863E751B269B913DC8D4319F6F5BDF", "block", (vm, c, a) => vm.BlockCopy(c, a, 75, 396, 3840));
        RegisterNative("1253D6DD445B3CE501A06A6ED7C8F50CC32F44BF", "block", (vm, c, a) => vm.BlockCopy(c, a, 75, 396, vm.NativeArg(a, 2)));
        // Can Fes! START 71583: 51 rows of 204 bytes, 2400 apart (its 68 x 51 thumbnail into an
        // 800 x 600 picture)
        RegisterNative("1249F52FDF84986E2917C68EBA9DC210A93F1B1C", "block", (vm, c, a) => vm.BlockCopy(c, a, 51, 204, 2400));
        // Bitch Nee-chan C11C4: 128 rows of 684 bytes (0xAB dwords), 3840 apart; Bitch Gakuen
        // C3724: 128 rows of 681 bytes (0xAA dwords and a byte), 3840 apart (every frame of text)
        RegisterNative("796FFAA089B4BBA15CDDB190D35F8529B95949A7", "block", (vm, c, a) => vm.BlockCopy(c, a, 128, 684, 3840));
        RegisterNative("EC35C823FB602E8AA31C44D28AE1C1FD15808C7B", "block", (vm, c, a) => vm.BlockCopy(c, a, 128, 681, 3840));

        // Bitch Nee-chan C7C2C (Bitch Gakuen CA19C, Bitch Shimai C9D6C; once at the start): 25 x 25
        // samples of the first byte of 24-bit pixels from l[0] (rows 3840 bytes apart) to l[1],
        // each b * 64 / 255; read and written in the x86 code's order. Chiccha na Hanayome C04AC:
        // the same code with 26 x 26 (its row step 3840 - 26 * 3)
        RegisterNative("36FEBAB2B4673E7C14FF5B69625F879475C6F6AE", "samples25", (vm, c, a) => vm.Samples(c, a, 25));
        RegisterNative("06344B77ECB765B91AC2094D95D2976F0E228EE7", "samples26", (vm, c, a) => vm.Samples(c, a, 26));

        // Bitch Gakuen 2B8BC (Bitch Shimai 2B70C): strrchr for Shift-JIS text: l[0] = the
        // address of the last byte l[2] in the string at l[1], lead bytes 81-9F and E0-FC
        // taking their next byte with them (a 0 after a lead byte ends the string), else 0
        RegisterNative("B15B347864344219C0D7C41EFA26FB360D393ED8", "strrchr", (vm, c, a) =>
        {
            using var verify = vm.VerifyRegion(c, a, a.Stack, 4);
            int s = vm.NativeArg(a, 1), found = 0;
            byte ch = (byte)vm.NativeArg(a, 2);
            for (int b; s != 0 && (b = vm.ReadByte(s)) != 0; s++)
            {
                if (b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC)
                {
                    if (vm.ReadByte(++s) == 0)
                        break;
                }
                else if (b == ch)
                    found = s;
            }
            vm.SetL(a, 0, found);
        });

        // START 75F62 / 763AC / 85844: f[2] = the index of the first word f[0] in the string of
        // words at f[1] (before its 0), else -1
        RegisterNative(["625BEDD645CDC8948BCDADC78515BD3D979A5D1A", "D8162510516400B08E35C7772F9CDAACC826BE5C"], "wcsindex", (vm, c, a) =>
        {
            ushort ch = (ushort)vm.Read32(a.F);
            int start = vm.Read32(a.F + 4), result = -1;
            using var verify = vm.VerifyRegion(c, a, a.F + 8, 4);
            for (int p = start; ; p += 2)
            {
                ushort w = vm.Read16(p);
                if (w == 0)
                    break;
                if (w == ch)
                {
                    result = (int)((uint)(p - start) >> 1);
                    break;
                }
            }
            vm.Write32(a.F + 8, result);
        });

        // START 75FAB / 763F3 / 8588C: f[2] = the offset of the first byte f[0] in the string at
        // f[1] (before its 0), else -1
        RegisterNative(["1BA6C16D9E3B19F01F3582BE297FFA7A675F3603", "FC6BD4250CAC523AB50FF78AAA7D8E8EBDD925B4"], "strindex", (vm, c, a) =>
        {
            byte ch = (byte)vm.Read32(a.F);
            int start = vm.Read32(a.F + 4), result = -1;
            using var verify = vm.VerifyRegion(c, a, a.F + 8, 4);
            for (int p = start; ; p++)
            {
                byte b = vm.ReadByte(p);
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

        // START 75FEE / 76434 / 858D0 (Ero-On! 5DC31, Aneiro 889E6): l[0] = NOT l[1], l[2] bytes, by 8 bytes
        // (l[3] = 0: the count rounded down to 8) or by 4
        RegisterNative(["8EE6D4D8C30D61F6379F0F7FA42BAB648AE0483E", "0CF868D5FF21E737808C7235BF0D164BFDE48B4F",
                        "516F6F4090493362A1590006FF9F3031DFFC9346", "039192F71200D43AF29A5F5AE0682CCAC4317F2C",
                        "7B2B7B77BACDED981FFB2FEBE08F68CBFB09DB09", "CD4D7E16634D0824BA915D35DFE8555BC0971D1B"], "not", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
            uint count = (uint)vm.NativeArg(a, 2);
            long bytes = vm.NativeArg(a, 3) == 0 ? (count >> 3) * 8L : (count >> 2) * 4L;
            if (bytes == 0 || bytes > 0x10000000 || dst != src && !Apart(dst, bytes, src, bytes))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)bytes);
            vm.MapDwords(dst, src, (int)bytes, v => ~v);
        });

        // START 7633C / 7678A / 85C1C (Ero-On! 5DC80): strnicmp of l[0] and l[1] (A-Z as a-z), at
        // most l[2] bytes; l[3] = the count left where they differ or one ends (0 = equal)
        RegisterNative(["2D36B031F4C93E1FD11A2474378A26F53A9CB62A", "DAB4F2DE807F4120AB0D07F77C7F4C686804200D"], "strnicmp", (vm, c, a) =>
        {
            int p = vm.NativeArg(a, 0), q = vm.NativeArg(a, 1);
            uint n = (uint)vm.NativeArg(a, 2);
            using var verify = vm.VerifyRegion(c, a, a.Stack + 12, 4);
            static byte Lower(byte b) => b is >= 0x41 and <= 0x5A ? (byte)(b | 0x20) : b;
            while (n != 0)
            {
                byte x = Lower(vm.ReadByte(p)), y = Lower(vm.ReadByte(q));
                if (x != y || --n == 0 || x == 0 || y == 0)
                    break;
                p++;
                q++;
            }
            vm.SetL(a, 3, (int)n);
        });

        // START 76042 / 76490 / 85920 (MMX): sepia of BGR pixels, l[2] bytes (rounded down to 24)
        // from l[1] to l[0]: y = (151 b0 + 77 b1 + 29 b2) >> 8 of each pixel's bytes, then its
        // bytes (y * 145, y * 200, y * 240) >> 8
        RegisterNative(["B9D719E48EB9DD041EA49683ADA8C7F1602FB1FC", "649843201017621317B4493924B9AF51F5FDD445"], "sepia", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
            uint count = (uint)vm.NativeArg(a, 2);
            long bytes = count / 24 * 24L;
            // under 24 bytes the x86 loop's count wraps round
            if (bytes == 0 || dst != src && !Apart(dst, bytes, src, bytes))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)bytes);
            const int Chunk = 24 * 4096;
            var buffer = ArrayPool<byte>.Shared.Rent(Chunk);
            for (long done = 0; done < bytes; done += Chunk)
            {
                int n = (int)Math.Min(Chunk, bytes - done);
                var span = buffer.AsSpan(0, n);
                vm.ReadBytes(src + (int)done, span);
                for (int i = 0; i < n; i += 3)
                {
                    int y = (span[i] * 151 + span[i + 1] * 77 + span[i + 2] * 29) >> 8;
                    span[i] = (byte)(y * 145 >> 8);
                    span[i + 1] = (byte)(y * 200 >> 8);
                    span[i + 2] = (byte)(y * 240 >> 8);
                }
                vm.WriteBytes(dst + (int)done, span);
            }
            ArrayPool<byte>.Shared.Return(buffer);
        });

        // START 7963C / 79A8A / 88BDC (Ero-On! 5EB4A; MMX): a rectangle of 32-bit pixels. l[0]
        // dst, l[1] src, l[2] / l[3] their row strides, l[4] width, l[5] height
        RegisterNative(["6E76FCA4FA78FA7B47C9153B7BFEEA95FF609582", "6F192CD4BA3ACAB3A5F79FE84D6B6CA9A08E6C36",
                        "992F9776E50F5FBD5AB6733AD893836B4C13458E"], "copy32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1), sd = vm.NativeArg(a, 2), ss = vm.NativeArg(a, 3);
            uint width = (uint)vm.NativeArg(a, 4), height = (uint)vm.NativeArg(a, 5);
            if (width == 0 || height == 0)
                return;
            long rowBytes = width * 4L;
            if (rowBytes > 0x10000000 || height > 0x1000000)
            {
                vm.RunX86(c, a);
                return;
            }
            var dstSpan = RowsSpan(dst, sd, (int)height, rowBytes);
            if (!Apart(dstSpan, RowsSpan(src, ss, (int)height, rowBytes)))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, (int)dstSpan.Start, (int)dstSpan.Length);
            for (int y = 0; y < height; y++)
                vm.MoveMemory(dst + y * sd, src + y * ss, (int)rowBytes);
        });

        // EFCLIB 1E9E (Re: Rem Plus 2792; Bitch Gakuen and Shimai 285A, Ao no Juuai 1978: other padding): the
        // negative and other fills: l[3] dwords from l[4] to l[0], each XORed with l[5]'s byte in
        // every byte
        RegisterNative(["EB59CB1AF7084D666150A58C5866638D49AAC562", "BD87E97A68BDF28ECD10B4E3EBDBD6799D5A0F1D",
                        "997E3DD49B9B61739720F69D2E180ADAC4C9D884", "D299BA54CDEF926F62CDC0733BC018D56CDA54D5",
                        "683B763EC4CA9666F13D288DF45400CB2F498B4E"], "xor32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 4);
            uint count = (uint)vm.NativeArg(a, 3), v = (uint)vm.NativeArg(a, 5);
            v |= v << 8;
            v |= v << 16;
            long bytes = count * 4L;
            if (count == 0 || bytes > 0x10000000 || dst != src && !Apart(dst, bytes, src, bytes))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)bytes);
            vm.MapDwords(dst, src, (int)bytes, x => x ^ v);
        });

        // EFCLIB 54A0 / 549E / 6D79 (Chiccha na Hanayome 7766, other padding): l[1] dwords at l[0]
        // inverted in place
        RegisterNative(["4158148EFBFBC3AA4CE88FF4D6D5D2B9C5707615", "8DC1060C677A722C3CACF4865B7E506824EB7942",
                        "863B772CB1F64D67213C7F27F74AEEC6471CBD3A", "98CFC5C0D6A29A93EC9E2A728E0017AEE4027AE1"], "invert32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0);
            uint count = (uint)vm.NativeArg(a, 1);
            if (count == 0 || count > 0x4000000)
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)count * 4);
            vm.MapDwords(dst, dst, (int)count * 4, x => ~x);
        });

        // Ero-On! START 5DCCA (MMX): case 0 of its zoom (0x14A3B), a rectangle of 32-bit pixels
        // moved by fx / 16, fy / 16 of a pixel: each byte (p00 (16 - fx)(16 - fy) + p10 fx (16 - fy)
        // + p01 (16 - fx) fy + p11 fx fy) >> 8 in 16 bits, the products whose weight is 0 left
        // out (fx = fy = 0 copies). l[0] dst, l[1] src, l[2] / l[3] their strides, l[4] width,
        // l[5] height, l[6] fx, l[7] fy
        RegisterNative("1AFA154B046DABF3E9E3ECE7BCD4562FCCFE18D5", "move16", (vm, c, a) => vm.Move16(c, a));

        // Re: Rem Plus START 85774: BGR to 32-bit pixels (bytes FF, B, G, R). l[0] src (rows one
        // after the other), l[1] dst, l[2] width, l[3] height, l[4] dst stride
        RegisterNative("A0298E9169EFB1ED73FB780ABAB186C3C879C91B", "bgr to 32", (vm, c, a) =>
        {
            int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1), width = vm.NativeArg(a, 2), height = vm.NativeArg(a, 3), sd = vm.NativeArg(a, 4);
            if (height == 0)
                return;
            if (width < 0 || height < 0 || width > 0x1000000 || height > 0x100000)
            {
                vm.RunX86(c, a);
                return;
            }
            var dstSpan = RowsSpan(dst, sd, height, width * 4L);
            if (width > 0 && !Apart(dstSpan, (src, (long)width * 3 * height)))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, (int)dstSpan.Start, (int)dstSpan.Length);
            var row = new byte[width * 3];
            var output = new byte[width * 4];
            for (int y = 0; y < height; y++, src += width * 3, dst += sd)
            {
                vm.ReadBytes(src, row);
                for (int x = 0; x < width; x++)
                {
                    output[4 * x] = 0xFF;
                    output[4 * x + 1] = row[3 * x];
                    output[4 * x + 2] = row[3 * x + 1];
                    output[4 * x + 3] = row[3 * x + 2];
                }
                vm.WriteBytes(dst, output);
            }
        });

        // Re: Rem Plus START 857DC: 32-bit pixels to BGR (bytes 1, 2, 3 of each). l[0] src, l[1]
        // dst (rows one after the other), l[2] width, l[3] height, l[4] src stride
        RegisterNative("BF29AD3A71C286A4A98B9FBCE1752CCFA4A5FB6B", "32 to bgr", (vm, c, a) =>
        {
            int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1), width = vm.NativeArg(a, 2), height = vm.NativeArg(a, 3), ss = vm.NativeArg(a, 4);
            if (height == 0)
                return;
            if (width < 0 || height < 0 || width > 0x1000000 || height > 0x100000)
            {
                vm.RunX86(c, a);
                return;
            }
            long dstBytes = (long)width * 3 * height;
            if (width > 0 && !Apart(RowsSpan(src, ss, height, width * 4L), (dst, dstBytes)))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)dstBytes);
            var row = new byte[width * 4];
            var output = new byte[width * 3];
            for (int y = 0; y < height; y++, src += ss, dst += width * 3)
            {
                vm.ReadBytes(src, row);
                for (int x = 0; x < width; x++)
                {
                    output[3 * x] = row[4 * x + 1];
                    output[3 * x + 1] = row[4 * x + 2];
                    output[3 * x + 2] = row[4 * x + 3];
                }
                vm.WriteBytes(dst, output);
            }
        });

        // Re: Rem Plus START 88FEC: the string at l[1] to l[0] with half-width characters (20-7E)
        // made full-width by the table of words inside the routine (+34h + 2 c); the lead bytes
        // 81-9F and E0-FC take their next byte with them
        RegisterNative("B32EF050EF5EBAAAD4944D5CA7C0946711407AFA", "fullwidth", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1), table = vm.m_nativeTarget + 0x34;
            using var verify = vm.VerifyRegion(c, a, dst, FullWidthLength(vm, src));
            for (int ch; (ch = vm.ReadByte(src)) != 0; )
            {
                if (ch is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC)
                {
                    vm.Write16(dst, vm.Read16(src));
                    src += 2;
                    dst += 2;
                }
                else if (ch is >= 0x20 and <= 0x7E)
                {
                    vm.Write16(dst, vm.Read16(table + 2 * ch));
                    src++;
                    dst += 2;
                }
                else
                    vm.WriteByte(dst++, vm.ReadByte(src++));
            }
            vm.WriteByte(dst, 0);

            static int FullWidthLength(ScnVm vm, int src)
            {
                int n = 0;
                for (int ch; (ch = vm.ReadByte(src)) != 0 && n < 0x100000; )
                {
                    bool wide = ch is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;
                    n += wide || ch is >= 0x20 and <= 0x7E ? 2 : 1;
                    src += wide ? 2 : 1;
                }
                return n + 1;
            }
        });

        // Re: Rem Plus START 8911E: l[2] BGR pixels from l[1] to l[0] with their saturation
        // changed by l[3] through the tables inside the routine (+A72h)
        RegisterNative("ED8F3CADF6C30D98C2133F56F7209213D4423519", "saturation", (vm, c, a) => vm.Saturation(c, a));

        // Re: Rem Plus START 8BB90: swaps l[2] bytes at l[0] and l[1] (dwords, then bytes)
        RegisterNative("341D72D8264C3FAF28CF53828124786719214DCB", "swap", (vm, c, a) =>
        {
            int p = vm.NativeArg(a, 0), q = vm.NativeArg(a, 1);
            uint n = (uint)vm.NativeArg(a, 2);
            if (n > 0x10000000)
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegions(c, a, (p, (int)n), (q, (int)n));
            if (Apart(p, n, q, n))
            {
                byte[] x = vm.ReadBytes(p, (int)n), y = vm.ReadBytes(q, (int)n);
                vm.WriteBytes(p, y);
                vm.WriteBytes(q, x);
                return;
            }
            // as the x86 code does it, a dword or byte at a time
            for (uint k = n >> 2; k > 0; k--, p += 4, q += 4)
            {
                int x = vm.Read32(p), y = vm.Read32(q);
                vm.Write32(p, y);
                vm.Write32(q, x);
            }
            for (uint k = n & 3; k > 0; k--, p++, q++)
            {
                byte x = vm.ReadByte(p), y = vm.ReadByte(q);
                vm.WriteByte(p, y);
                vm.WriteByte(q, x);
            }
        });

        // Re: Rem Plus START 8BC00: the montage table at b[202] (its size at +4, 24-byte entries
        // from +8) gets its names (16 bytes, up to a 0) in capitals
        RegisterNative("8248DF0D0FB0946C946217CF79FDE27AA4B3F55A", "montage upper", (vm, c, a) =>
        {
            int table = vm.Read32(a.B + 0x328);
            uint count = ((uint)vm.Read32(table + 4) - 8) / 24;
            if (count == 0)
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, table + 8, (int)Math.Min(count * 24L, int.MaxValue));
            var name = new byte[16];
            for (int entry = table + 8; count > 0; count--, entry += 24)
            {
                vm.ReadBytes(entry, name);
                int n = name.AsSpan().IndexOf((byte)0);
                UpperAscii(name.AsSpan(0, n < 0 ? 16 : n));
                vm.WriteBytes(entry, name);
            }
        });

        // Re: Rem Plus START 8BC4C: looks the name at l[0] (put in capitals, up to 16 bytes) up in
        // that table by bisection: l[0] = its entry or -1; l[1] the entries, l[2] the length
        // compared (with the 0), l[3] / l[4] the last bounds, l[5] the count
        RegisterNative("D2DBBF7D7496763A3E96AC2A084D0A60D32C885D", "montage find", (vm, c, a) => vm.MontageFind(c, a));

        // Re: Rem Plus START 8BD18 / 8BD38: l[2] subtracted from / added to l[1] dwords at l[0]
        RegisterNative("6ADF303C50268109E441608629A5D7E07752E4D0", "sub32", (vm, c, a) => vm.AddDwords(c, a, -vm.NativeArg(a, 2)));
        RegisterNative("462BA655CE37918B692B5286D22DB1C995B10E25", "add32", (vm, c, a) => vm.AddDwords(c, a, vm.NativeArg(a, 2)));

        // Re: Rem Plus START 8BD58: l[0] = the checksum of l[1] bytes at l[0]: each dword (the
        // last 1-3 bytes as one) added, then rotated right by 3
        RegisterNative("639ACC96A94246D7D1420B3161F2E864AC311BA5", "checksum", (vm, c, a) =>
        {
            int p = vm.NativeArg(a, 0);
            uint n = (uint)vm.NativeArg(a, 1), sum = 0;
            using var verify = vm.VerifyRegion(c, a, a.Stack, 4);
            const int Chunk = 1 << 16;
            var buffer = ArrayPool<byte>.Shared.Rent(Chunk);
            for (uint left = n >> 2; left > 0; )
            {
                int words = (int)Math.Min(left, Chunk / 4);
                var span = buffer.AsSpan(0, words * 4);
                vm.ReadBytes(p, span);
                for (int k = 0; k < span.Length; k += 4)
                    sum = BitOperations.RotateRight(sum + BinaryPrimitives.ReadUInt32LittleEndian(span[k..]), 3);
                p += words * 4;
                left -= (uint)words;
            }
            ArrayPool<byte>.Shared.Return(buffer);
            int rest = (int)(n & 3);
            if (rest != 0)
            {
                uint tail = 0;
                for (int k = rest - 1; k >= 0; k--)
                    tail = tail << 8 | vm.ReadByte(p + k);
                sum = BitOperations.RotateRight(sum + tail, 3);
            }
            vm.SetL(a, 0, (int)sum);
        });

        // Re: Rem Plus START 8BD9C: a 28 x 28 mask from the first byte of each pixel of a BGR
        // picture 1280 wide at l[0]: (v << 6) / 255 into l[1]
        RegisterNative("B0BB28FCE0DED80456A7AC465F671A3E703C7897", "mask28", (vm, c, a) =>
        {
            int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1);
            using var verify = vm.VerifyRegion(c, a, dst, 28 * 28);
            for (int y = 0; y < 28; y++, src += 3840)
                for (int x = 0; x < 28; x++)
                    vm.WriteByte(dst++, (byte)((vm.ReadByte(src + 3 * x) << 6) / 255));
        });

        // Re: Rem Plus START 904F4: matches the 48-byte records from l[1] (b[93] on to b[92])
        // against the b[94] left in the table at l[2] (40 bytes compared; a matched one is set
        // to -1); a record without its match, and every table record left, gets 1 at +5Ch of
        // the record its first dword points to
        RegisterNative("97B65886703D8A12CE27C061A3EEFBD607A1C556", "match records", (vm, c, a) => vm.MatchRecords(c, a));

        // Libra of the Vampire Princess START E1B0C: mask28 of any size, n x n with n the low byte
        // of l[2] (0: 256). Sorcery Jokers 2184D0: the same from a picture 1920 wide
        RegisterNative("0A844E6B45D4675A2CDE5BA1EA49CCAA0A3AB48B", "mask", (vm, c, a) => vm.Mask(c, a, 3840));
        RegisterNative("CDBE0CBE1850ACA2AC91138D33EEED4284359AE3", "mask 1920", (vm, c, a) => vm.Mask(c, a, 5760));

        // Sorcery Jokers START 2159B8: a blank S25 picture at l[0]: l[1] bytes cleared, then
        // "S25", l[5] frames of l[4] bytes each (their offsets after the header), each l[6] x
        // l[7] at (0, 0) with rows l[3] bytes apart, every row one run of l[6] ABGR pixels (the
        // code dword l[2] << 16: no length before the run, method 4 from l[2] 0x8000) left
        // clear; l[8] counts the frames down to 0
        RegisterNative("DE3A498EFE2D198ACE0A0AA10C6CD571E986461A", "blank s25", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), bytes = vm.NativeArg(a, 1), code = vm.NativeArg(a, 2), stride = vm.NativeArg(a, 3);
            int frameBytes = vm.NativeArg(a, 4), frames = vm.NativeArg(a, 5), w = vm.NativeArg(a, 6), h = vm.NativeArg(a, 7);
            // Counts the x86 code would take round 4 billion times, or that write past the bytes cleared
            if (bytes < 0 || frames <= 0 || h <= 0 || 8L + 4L * frames + (long)frameBytes * (frames - 1) + 0x14 + 4L * h + (long)stride * (h - 1) + 8 > bytes)
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegions(c, a, (dst, bytes), (a.Stack + 32, 4));
            vm.FillMemory(dst, bytes, 0);
            vm.Write32(dst, 0x353253);
            vm.Write32(dst + 4, frames);
            int frame = dst + 8 + 4 * frames;
            for (int f = 0; f < frames; f++)
                vm.Write32(dst + 8 + 4 * f, 4 * frames + 8 + f * frameBytes);
            for (int f = 0; f < frames; f++, frame += frameBytes)
            {
                vm.Write32(frame, w);
                vm.Write32(frame + 4, h);
                int rows = frame + 0x14, row = rows + 4 * h;
                for (int y = 0; y < h; y++)
                    vm.Write32(rows + 4 * y, row + y * stride - dst);
                for (int y = 0; y < h; y++, row += stride)
                {
                    vm.Write32(row, code << 16);
                    vm.Write32(row + 4, w);
                }
            }
            vm.SetL(a, 8, 0);
        });

        // Libra of the Vampire Princess START F777: the Shift-JIS string at l[0] copied to l[1],
        // each single byte among the first five of l[2] written twice (the sixth is looked at
        // too, but "repne scasb" leaves ecx 0 when it is found there, as when it is not); a lead
        // byte (81-9F, E0-FC) takes the byte after it whatever it is, its 0 too
        RegisterNative("49414E55EAE3AB94EC8890F0BBE7D67BB8039798", "escape copy", (vm, c, a) =>
        {
            int src = vm.NativeArg(a, 0), dst = vm.NativeArg(a, 1), set = vm.NativeArg(a, 2);
            Span<byte> doubled = stackalloc byte[5];
            vm.ReadBytes(set, doubled);
            var output = new List<byte>();
            for (int p = src; ; )
            {
                byte b = vm.ReadByte(p);
                if (b == 0)
                    break;
                if (b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC)
                {
                    output.Add(b);
                    output.Add(vm.ReadByte(p + 1));
                    p += 2;
                    continue;
                }
                if (doubled.Contains(b))
                    output.Add(b);
                output.Add(b);
                p++;
            }
            output.Add(0);
            using var verify = vm.VerifyRegion(c, a, dst, output.Count);
            vm.WriteBytes(dst, output.ToArray());
        });

        // Libra of the Vampire Princess START F7C8: the first "[" (l[0] = 1), "{" (2) or lead byte
        // of a double-byte character (81-9F, E0-FC: 3) in the text at l[0], l[1] = its address;
        // l[0] = 0 when there is none before the 0 (l[1] left as it was)
        RegisterNative("B9C872F614C7912A386590D67B7FFBA75C4AF240", "find mark", (vm, c, a) =>
        {
            int p = vm.NativeArg(a, 0), kind = 0;
            using var verify = vm.VerifyRegion(c, a, a.Stack, 8);
            for (byte b; (b = vm.ReadByte(p)) != 0; p++)
            {
                kind = b switch
                {
                    (byte)'[' => 1,
                    (byte)'{' => 2,
                    >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC => 3,
                    _ => 0,
                };
                if (kind != 0)
                    break;
            }
            // l[0] is cleared first, then its low byte set
            vm.SetL(a, 0, kind);
            if (kind != 0)
                vm.SetL(a, 1, p);
        });

        // Libra of the Vampire Princess START A27E4: the width of the text at l[0], its letters'
        // widths from the byte table in use (l[2] at first; "[" switches to l[1], "{" to l[3], "]"
        // and "}" back to l[2]), 36 for a double-byte character: l[0] = the width, l[1] = the
        // table in use at the end
        RegisterNative("5B6AFC75F46CBDFF8B9C53955F1445FBF6C4B8B1", "text width", (vm, c, a) => vm.TextWidth(c, a, 0x24));
        // Sorcery Jokers START 1DBB08: the same, 34 for a double-byte character
        RegisterNative("1F28134971F3C9966E2AA8BCC5D672B3034AAE0D", "text width 34", (vm, c, a) => vm.TextWidth(c, a, 0x22));

        // Sorcery Jokers START 215980: the picture at l[0] made relocatable again (what 04B2 did,
        // undone): each frame's pointer and its rows' pointers less the picture's address
        RegisterNative("FC46690390954614B7823E737C55531ED3C6017B", "unrelocate s25", (vm, c, a) =>
        {
            int picture = vm.NativeArg(a, 0), frames = vm.Read32(picture + 4);
            if (frames == 0)
                return;
            if (frames < 0 || frames > 0x100000)
            {
                vm.RunX86(c, a);
                return;
            }
            var regions = new List<(int Start, int Length)> { (picture + 8, 4 * frames) };
            for (int i = 0; i < frames; i++)
                if (vm.Read32(picture + 8 + 4 * i) is var frame and not 0 && vm.Read32(frame + 4) is > 0 and var height)
                    regions.Add((frame + 0x14, 4 * height));
            using var verify = vm.VerifyRegions(c, a, regions.ToArray());
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

        // Sorcery Jokers START 215A56: l[2] BGR pixels from l[1] to l[0], their colour scaled by
        // l[3] towards their brightest channel, which stays: of the two others the least moves by
        // (max - min) * l[3] / 256, the middle one by a dword table after the routine (+0xA7A,
        // four of 512 by which channel is largest; the index ((mid - x) << 8) / (max - min) may
        // be below 0 and reach into the table before) times that / 65536. Divisions as idiv,
        // shifts as sar after the x86 code's rounding towards 0
        RegisterNative("3897C1B201D06481C10CFBDD3B60B45DFCF84934", "saturation", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 1);
            uint count = (uint)vm.NativeArg(a, 2);
            int k = vm.NativeArg(a, 3), table = vm.m_nativeTarget + 0xA7A;
            if (count == 0)
                return;
            long bytes = 3L * count;
            if (bytes > 0x10000000 || dst != src && !Apart(dst, bytes, src, bytes))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, (int)bytes);
            static int Sar16(int x) => (x + ((x >> 31) & 0xFFFF)) >> 16;
            static int Sar8(int x) => (x + ((x >> 31) & 0xFF)) >> 8;
            for (int n = 0; n < (int)count; n++, src += 3, dst += 3)
            {
                int b = vm.ReadByte(src), g = vm.ReadByte(src + 1), r = vm.ReadByte(src + 2);
                int T(int at, int index) => vm.Read32(table + at + 4 * index);
                byte nb, ng, nr;
                if (b > g)
                {
                    if (r > b)
                    {
                        int e = unchecked((r - g) * k), q = ((b - g) << 8) / (r - g);
                        (nb, ng, nr) = ((byte)(r - Sar16(unchecked(T(0x1000, q) * e))), (byte)(r - Sar8(e)), (byte)r);
                    }
                    else if (g > r)
                    {
                        int e = unchecked((b - r) * k), q = ((g - r) << 8) / (b - r);
                        (nb, ng, nr) = ((byte)b, (byte)(b - Sar16(unchecked(T(0, q) * e))), (byte)(b - Sar8(e)));
                    }
                    else
                    {
                        int e = unchecked((b - g) * k), q = ((g - r) << 8) / (b - g);
                        (nb, ng, nr) = ((byte)b, (byte)(b - Sar8(e)), (byte)(b - Sar16(unchecked(T(0x1800, q) * e))));
                    }
                }
                else if (r > g)
                {
                    int e = unchecked((r - b) * k), q = ((b - g) << 8) / (r - b);
                    (nb, ng, nr) = ((byte)(r - Sar8(e)), (byte)(r - Sar16(unchecked(T(0x1000, q) * e))), (byte)r);
                }
                else if (b > r)
                {
                    int e = unchecked((g - r) * k), q = ((r - b) << 8) / (g - r);
                    (nb, ng, nr) = ((byte)(g - Sar16(unchecked(T(0x800, q) * e))), (byte)g, (byte)(g - Sar8(e)));
                }
                else if (g == b)
                    (nb, ng, nr) = ((byte)g, (byte)g, (byte)g);
                else
                {
                    int e = unchecked((g - b) * k), q = ((r - b) << 8) / (g - b);
                    (nb, ng, nr) = ((byte)(g - Sar8(e)), (byte)g, (byte)(g - Sar16(unchecked(T(0x800, q) * e))));
                }
                vm.WriteByte(dst, nb);
                vm.WriteByte(dst + 1, ng);
                vm.WriteByte(dst + 2, nr);
            }
        });

        // Sorcery Jokers START 1249E: l[1] = the first byte of the text at l[5] that is not a
        // double-byte character (lead bytes 81-9F, E0-FC take the byte after them, whatever it
        // is), 0 when the text ends first
        RegisterNative("76C5891E51AA8562C14754D009A15228C3B0964E", "skip double bytes", (vm, c, a) =>
        {
            int p = vm.NativeArg(a, 5);
            using var verify = vm.VerifyRegion(c, a, a.Stack + 4, 4);
            byte b;
            while ((b = vm.ReadByte(p)) is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC)
                p += 2;
            vm.SetL(a, 1, b == 0 ? 0 : p);
        });

        // Sorcery Jokers START 211A07: 482 rows of 856 BGR pixels from l[4] (a picture 1920
        // wide) to l[0], one after the other (a byte at a time: blocks that share bytes run as x86)
        RegisterNative("8E6C33413435C5ACB0FE26A1264354E76D077DA0", "copy 856x482", (vm, c, a) =>
        {
            const int Rows = 482, RowBytes = 856 * 3, Stride = 5760;
            int dst = vm.NativeArg(a, 0), src = vm.NativeArg(a, 4);
            if (!Apart((dst, (long)Rows * RowBytes), RowsSpan(src, Stride, Rows, RowBytes)))
            {
                vm.RunX86(c, a);
                return;
            }
            using var verify = vm.VerifyRegion(c, a, dst, Rows * RowBytes);
            for (int y = 0; y < Rows; y++)
                vm.WriteBytes(dst + y * RowBytes, vm.ReadBytes(src + y * Stride, RowBytes));
        });
    }

    /// <summary>
    /// Libra of the Vampire Princess START A27E4 (Sorcery Jokers 1DBB08): the width of the text
    /// at l[0], its letters' widths from the byte table in use (l[2] at first; "[" switches to
    /// l[1], "{" to l[3], "]" and "}" back to l[2]), <paramref name="wide"/> for a double-byte
    /// character: l[0] = the width, l[1] = the table in use at the end.
    /// </summary>
    private void TextWidth(ScnContext c, ScnNativeArgs a, int wide)
    {
        int p = NativeArg(a, 0), table = NativeArg(a, 2), width = 0;
        using var verify = VerifyRegion(c, a, a.Stack, 8);
        for (byte b; (b = ReadByte(p)) != 0; )
        {
            switch (b)
            {
                case (byte)'[': table = NativeArg(a, 1); p++; continue;
                case (byte)']' or (byte)'}': table = NativeArg(a, 2); p++; continue;
                case (byte)'{': table = NativeArg(a, 3); p++; continue;
            }
            if (b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC)
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

    /// <summary>
    /// An n x n mask (n the low byte of l[2], 0: 256) at l[1] from the first byte of each pixel of
    /// a BGR picture at l[0] with rows <paramref name="stride"/> bytes apart: (v &lt;&lt; 6) / 255.
    /// </summary>
    private void Mask(ScnContext c, ScnNativeArgs a, int stride)
    {
        int src = NativeArg(a, 0), dst = NativeArg(a, 1), n = (byte)NativeArg(a, 2);
        if (n == 0)
            n = 256;
        using var verify = VerifyRegion(c, a, dst, n * n);
        for (int y = 0; y < n; y++, src += stride)
            for (int x = 0; x < n; x++)
                WriteByte(dst++, (byte)((ReadByte(src + 3 * x) << 6) / 255));
    }

    /// <summary>a-z to A-Z (the x86 code compares signed bytes: 80-FF stay).</summary>
    private static void UpperAscii(Span<byte> bytes)
    {
        foreach (ref byte b in bytes)
            if (b is >= 0x61 and <= 0x7A)
                b -= 0x20;
    }

    /// <summary>dst = f(src) a dword at a time (dst is src or apart from it).</summary>
    private void MapDwords(int dst, int src, int bytes, Func<uint, uint> f)
    {
        const int Chunk = 1 << 16;
        var buffer = ArrayPool<byte>.Shared.Rent(Chunk);
        for (int done = 0; done < bytes; done += Chunk)
        {
            int n = Math.Min(Chunk, bytes - done);
            var span = buffer.AsSpan(0, n);
            ReadBytes(src + done, span);
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(span);
            for (int k = 0; k < words.Length; k++)
                words[k] = f(words[k]);
            WriteBytes(dst + done, span);
        }
        ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>
    /// A width x height 32-bit picture (l[2], rows l[3] bytes apart) each pixel repeated n times
    /// across and down into l[0] (rows l[1] bytes apart); pictures that share bytes run as x86 code.
    /// </summary>
    private void EnlargeWhole(ScnContext c, ScnNativeArgs a, int n, int width, int height)
    {
        int dst = NativeArg(a, 0), dstStride = NativeArg(a, 1), src = NativeArg(a, 2), srcStride = NativeArg(a, 3);
        int inBytes = width * 4, outBytes = inBytes * n;
        var target = RowsSpan(dst, dstStride, height * n, outBytes);
        // (rows written over each other: the order the x86 code writes them in tells)
        if (Math.Abs((long)dstStride) < outBytes || !Apart(target, RowsSpan(src, srcStride, height, inBytes)))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, (int)target.Start, (int)target.Length);
        var input = new byte[inBytes];
        var output = new byte[outBytes];
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(input.AsSpan());
        var repeated = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(output.AsSpan());
        for (int y = 0; y < height; y++, src += srcStride)
        {
            ReadBytes(src, input);
            for (int x = 0; x < width; x++)
                repeated.Slice(x * n, n).Fill(pixels[x]);
            for (int k = 0; k < n; k++, dst += dstStride)
                WriteBytes(dst, output);
        }
    }

    /// <summary>1280 x 720 32-bit pixels (l[2], rows l[3] bytes apart) into 1920 x 1080 (l[0], rows l[1] apart), 2 x 2 into 3 x 3.</summary>
    private void EnlargeThreeHalves(ScnContext c, ScnNativeArgs a)
    {
        const int Pairs = 640, PairRows = 360, InBytes = Pairs * 8, OutBytes = Pairs * 12;
        int dst = NativeArg(a, 0), dstStride = NativeArg(a, 1), src = NativeArg(a, 2), srcStride = NativeArg(a, 3);
        var target = RowsSpan(dst, dstStride, PairRows * 3, OutBytes);
        if (Math.Abs((long)dstStride) < OutBytes || !Apart(target, RowsSpan(src, srcStride, PairRows * 2, InBytes)))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, (int)target.Start, (int)target.Length);
        var top = new byte[InBytes];
        var bottom = new byte[InBytes];
        for (int y = 0; y < PairRows; y++)
        {
            ReadBytes(src + 2 * y * srcStride, top);
            ReadBytes(src + (2 * y + 1) * srcStride, bottom);
            var r0 = Bytes(dst + 3 * y * dstStride, OutBytes);
            var r1 = Bytes(dst + (3 * y + 1) * dstStride, OutBytes);
            var r2 = Bytes(dst + (3 * y + 2) * dstStride, OutBytes);
            for (int i = 0, o = 0; i < InBytes; i += 8, o += 12)
                for (int k = 0; k < 4; k++)
                {
                    int pa = top[i + k], pb = top[i + 4 + k], pc = bottom[i + k], pd = bottom[i + 4 + k];
                    r0[o + k] = (byte)pa;
                    r0[o + 4 + k] = (byte)((pa + pb) >> 1);
                    r0[o + 8 + k] = (byte)pb;
                    r1[o + k] = (byte)((pa + pc) >> 1);
                    r1[o + 4 + k] = (byte)((pa + pb + pc + pd) >> 2);
                    r1[o + 8 + k] = (byte)((pb + pd) >> 1);
                    r2[o + k] = (byte)pc;
                    r2[o + 4 + k] = (byte)((pc + pd) >> 1);
                    r2[o + 8 + k] = (byte)pd;
                }
        }
    }

    /// <summary>Rows of rowBytes from src (rows srcStride apart) to dst (dstStride apart); blocks that share bytes run as x86 code.</summary>
    private void CopyRows(ScnContext c, ScnNativeArgs a, int dst, int dstStride, int src, int srcStride, int rows, int rowBytes)
    {
        var target = RowsSpan(dst, dstStride, rows, rowBytes);
        if (Math.Abs((long)dstStride) < rowBytes || !Apart(target, RowsSpan(src, srcStride, rows, rowBytes)))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, (int)target.Start, (int)target.Length);
        var row = new byte[rowBytes];
        for (int y = 0; y < rows; y++)
        {
            ReadBytes(src + y * srcStride, row);
            WriteBytes(dst + y * dstStride, row);
        }
    }

    private void Thumbnail(ScnContext c, ScnNativeArgs a, int block, int columns, int rows, int stride = 2400) =>
        Thumbnail(c, a, block, columns, rows, NativeArg(a, 0), NativeArg(a, 4), stride);

    /// <summary>Each byte the average of a block of block x block pixels of a BGR picture (rows of <paramref name="stride"/> bytes; 800 x 600 unless said).</summary>
    private void Thumbnail(ScnContext c, ScnNativeArgs a, int block, int columns, int rows, int dst, int src, int stride = 2400)
    {
        int outBytes = columns * rows * 3, inRows = rows * block;
        if (!Apart(dst, outBytes, src, (long)stride * inRows))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, dst, outBytes);
        byte[] picture = ReadBytes(src, stride * (inRows - 1) + columns * block * 3);
        var output = new byte[outBytes];
        int o = 0;
        for (int by = 0; by < rows; by++)
            for (int bx = 0; bx < columns; bx++)
                for (int k = 0; k < 3; k++)
                {
                    int sum = 0;
                    for (int y = 0; y < block; y++)
                    {
                        int p = (by * block + y) * stride + bx * block * 3 + k;
                        for (int x = 0; x < block; x++, p += 3)
                            sum += picture[p];
                    }
                    output[o++] = (byte)(sum / (block * block));
                }
        WriteBytes(dst, output);
    }

    private void BlockCopy(ScnContext c, ScnNativeArgs a, int rows, int rowBytes, int stride)
    {
        int src = NativeArg(a, 0), dst = NativeArg(a, 1);
        var dstSpan = RowsSpan(dst, stride, rows, rowBytes);
        if (!Apart(dstSpan, (src, (long)rows * rowBytes)))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, (int)dstSpan.Start, (int)dstSpan.Length);
        for (int y = 0; y < rows; y++)
            MoveMemory(dst + y * stride, src + y * rowBytes, rowBytes);
    }

    /// <summary>
    /// n x n samples of the first byte of 24-bit pixels from l[0] (rows 3840 bytes apart) to
    /// l[1], each b * 64 / 255, in the x86 code's order.
    /// </summary>
    private void Samples(ScnContext c, ScnNativeArgs a, int n)
    {
        int src = NativeArg(a, 0), dst = NativeArg(a, 1);
        using var verify = VerifyRegion(c, a, dst, n * n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                WriteByte(dst++, (byte)((ReadByte(src + y * 3840 + x * 3) << 6) / 255));
    }

    private void Move16(ScnContext c, ScnNativeArgs a)
    {
        int dst = NativeArg(a, 0), src = NativeArg(a, 1), sd = NativeArg(a, 2), ss = NativeArg(a, 3);
        int width = NativeArg(a, 4), height = NativeArg(a, 5);
        uint fx = (uint)NativeArg(a, 6), fy = (uint)NativeArg(a, 7);
        // a width or height of 0 makes the x86 loops wrap round
        if (width <= 0 || height <= 0 || width > 0x100000 || height > 0x100000)
        {
            RunX86(c, a);
            return;
        }
        bool right = fx != 0, below = fy != 0;
        var dstSpan = RowsSpan(dst, sd, height, width * 4L);
        if (!Apart(dstSpan, RowsSpan(src, ss, height + (below ? 1 : 0), (width + (right ? 1 : 0)) * 4L)))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, (int)dstSpan.Start, (int)dstSpan.Length);
        if (!right && !below)
        {
            for (int y = 0; y < height; y++)
                MoveMemory(dst + y * sd, src + y * ss, width * 4);
            return;
        }
        // the weights as the MMX code has them: the low words of the products
        uint w00 = (ushort)((16 - fx) * (16 - fy)), w10 = (ushort)(fx * (16 - fy)), w01 = (ushort)((16 - fx) * fy), w11 = (ushort)(fx * fy);
        int inBytes = (width + (right ? 1 : 0)) * 4;
        var top = new byte[inBytes];
        var bottom = below ? new byte[inBytes] : top;
        var output = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            ReadBytes(src + y * ss, top);
            if (below)
                ReadBytes(src + y * ss + ss, bottom);
            for (int i = 0; i < output.Length; i++)
            {
                uint sum = top[i] * w00;
                if (right)
                    sum += top[i + 4] * w10;
                if (below)
                    sum += bottom[i] * w01;
                if (right && below)
                    sum += bottom[i + 4] * w11;
                output[i] = (byte)((sum & 0xFFFF) >> 8);
            }
            WriteBytes(dst + y * sd, output);
        }
    }

    private void Saturation(ScnContext c, ScnNativeArgs a)
    {
        int dst = NativeArg(a, 0), src = NativeArg(a, 1), k = NativeArg(a, 3);
        uint count = (uint)NativeArg(a, 2);
        int tables = m_nativeTarget + 0xA72;
        if (count == 0)
            return;
        long bytes = count * 3L;
        if (bytes > 0x10000000)
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, dst, (int)bytes);
        if (dst == src || Apart(dst, bytes, src, bytes))
        {
            byte[] pixels = ReadBytes(src, (int)bytes);
            for (int i = 0; i < pixels.Length; i += 3)
                (pixels[i], pixels[i + 1], pixels[i + 2]) = SaturationPixel(tables, k, pixels[i], pixels[i + 1], pixels[i + 2]);
            WriteBytes(dst, pixels);
            return;
        }
        // blocks that overlap: a pixel read, then written, as the x86 code goes
        for (uint n = 0; n < count; n++, src += 3, dst += 3)
        {
            var (d0, d1, d2) = SaturationPixel(tables, k, ReadByte(src), ReadByte(src + 1), ReadByte(src + 2));
            WriteByte(dst, d0);
            WriteByte(dst + 1, d1);
            WriteByte(dst + 2, d2);
        }
    }

    /// <summary>
    /// A pixel of 8911E: with max and min of its bytes and t a table entry (by which byte is
    /// the largest) at ((mid - min) &lt;&lt; 8) / (max - min), a byte becomes max - (t (max - min) k) / 65536
    /// or max - ((max - min) k) / 256 (divisions towards 0, in 32 bits).
    /// </summary>
    private (byte, byte, byte) SaturationPixel(int tables, int k, int b, int g, int r)
    {
        int Down(int table, int numerator, int range, out int coarse)
        {
            int e = unchecked(range * k);
            int q = (numerator << 8) / range;
            int product = unchecked(Read32(table + 4 * q) * e);
            coarse = (e < 0 ? e + 0xFF : e) >> 8;
            return (product < 0 ? product + 0xFFFF : product) >> 16;
        }
        int v1, v2;
        if (b > g)
        {
            if (r > b)
            {
                v1 = Down(tables + 0x1000, b - g, r - g, out v2);
                return ((byte)(r - v1), (byte)(r - v2), (byte)r);
            }
            if (g > r)
            {
                v1 = Down(tables, g - r, b - r, out v2);
                return ((byte)b, (byte)(b - v1), (byte)(b - v2));
            }
            v1 = Down(tables + 0x1800, g - r, b - g, out v2);
            return ((byte)b, (byte)(b - v2), (byte)(b - v1));
        }
        if (r > g)
        {
            v1 = Down(tables + 0x1000, b - g, r - b, out v2);
            return ((byte)(r - v2), (byte)(r - v1), (byte)r);
        }
        if (b > r)
        {
            v1 = Down(tables + 0x800, r - b, g - r, out v2);
            return ((byte)(g - v1), (byte)g, (byte)(g - v2));
        }
        if (g == b)
            return ((byte)g, (byte)g, (byte)g);
        v1 = Down(tables + 0x800, r - b, g - b, out v2);
        return ((byte)(g - v2), (byte)g, (byte)(g - v1));
    }

    private void MontageFind(ScnContext c, ScnNativeArgs a)
    {
        int key = NativeArg(a, 0);
        int table = Read32(a.B + 0x328);
        int entries = table + 8;
        int count = (int)(((uint)Read32(table + 4) - 8) / 24);
        using var verify = VerifyRegions(c, a, (a.Stack, 24), (key, 16));
        SetL(a, 1, entries);
        SetL(a, 5, count);
        SetL(a, 4, count - 1);
        SetL(a, 3, 0);
        var name = ReadBytes(key, 16);
        int end = name.AsSpan().IndexOf((byte)0);
        int length = end < 0 ? 16 : end + 1;
        UpperAscii(name.AsSpan(0, length));
        WriteBytes(key, name.AsSpan(0, length));
        SetL(a, 2, length);
        var probe = new byte[length];
        // repe cmpsb: 0 when the length is the same, else the signed order of the first byte
        // that differs (the x86 code's jge)
        int Compare(int index)
        {
            ReadBytes(entries + unchecked(index * 24), probe);
            int at = probe.AsSpan().CommonPrefixLength(name.AsSpan(0, length));
            return at == length ? 0 : (sbyte)probe[at] > (sbyte)name[at] ? 1 : -1;
        }
        int low = 0, high = count - 1, found;
        while (true)
        {
            if (low >= high)
            {
                found = low;
                if (Compare(found) != 0)
                {
                    SetL(a, 0, -1);
                    return;
                }
                break;
            }
            int middle = (int)((uint)(low + high) >> 1);
            int order = Compare(middle);
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
        SetL(a, 0, entries + unchecked(found * 24));
    }

    private void AddDwords(ScnContext c, ScnNativeArgs a, int value)
    {
        int p = NativeArg(a, 0);
        uint count = (uint)NativeArg(a, 1);
        if (count > 0x4000000)
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, p, (int)count * 4);
        MapDwords(p, p, (int)count * 4, x => unchecked(x + (uint)value));
    }

    private void MatchRecords(ScnContext c, ScnNativeArgs a)
    {
        int b = a.B, l = a.Stack;
        using var verify = VerifyRegions(c, a, (b + 0x170, 12), (l, 12));
        var record = new byte[40];
        var other = new byte[40];
        while (Read32(b + 0x174) != Read32(b + 0x170))
        {
            bool matched = false;
            int left = Read32(b + 0x178);
            if (left != 0)
            {
                ReadBytes(Read32(l + 4), record);
                for (int entry = Read32(l + 8); ; entry += 0x30)
                {
                    if (Read32(entry) == -1)
                        continue;
                    ReadBytes(entry, other);
                    if (record.AsSpan().SequenceEqual(other))
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
        for (int entry = Read32(l + 8); ; entry += 0x30)
        {
            if (Read32(entry) != -1)
                Write32(Read32(entry) + 0x5C, 1);
            int left = Read32(b + 0x178) - 1;
            Write32(b + 0x178, left);
            if (left == 0)
                break;
        }
    }
}
