// C# versions of embedded x86 routines that run hot, written by hand: faster still than their
// X86Jit translation (Vector128, all cores). Each is found by the signature of the whole routine
// (X86Routine.Signature) and must give the bytes the x86 code gives; VerifyNatives runs the
// interpreter as well and compares (for tests).
//
// Each comes in two builds with the same instructions: the v2.47 one (Oreimo and Azu Plus) ends
// with "ret 4", the v2.49 one (Homu, Yuru, Nyaru, Rikka, Sena and Kuroneko Plus, also blend32 of
// Maki Fes! and Re: Rem Plus) with "ret"; the padding between their blocks differs too, so their
// signatures do. Maki Fes! and Re: Rem Plus have a third scale32: v2.47's blocks with "ret".

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>Run each replaced routine on the interpreter too and compare the results.</summary>
    public bool VerifyNatives { get; set; }

    // START 75C3D of Oreimo (6A6ED Azu, 5D88B Ero-On!, 75ED7 Homu, 76167 Yuru, 76687 Nyaru, 76647
    // Rikka, 76097 Sena, 760B7 Kuroneko): xors count dwords at l[1] with a key stream from l[0]
    // (xor [ebx],eax / inc eax / rol eax,3 / bswap eax), count a constant of each game (Oreimo
    // 0xD94A4: 3.5 MB, 21 ms a call translated). Found by its bytes with the count left open,
    // ending with "ret 4" (v2.47) or "ret".
    private static readonly byte[] s_xorStreamHead = Convert.FromHexString("9C608B6C242883EC24FF7500FF7504FF7508FF750CFF75108B34248B068B5E04B9");
    private static readonly byte[] s_xorStreamTail = Convert.FromHexString("31034083C304C1C0030FC84975F283C438619D");

    /// <summary>A C# version for the routine at <paramref name="address"/> found by its bytes, or null.</summary>
    private (string Name, NativeRoutine Run)? NativeByPattern(int address)
    {
        int length = s_xorStreamHead.Length + 4 + s_xorStreamTail.Length;
        byte[] code = ReadBytes(address, length + 3);
        if (!code.AsSpan(0, s_xorStreamHead.Length).SequenceEqual(s_xorStreamHead)
            || !code.AsSpan(s_xorStreamHead.Length + 4, s_xorStreamTail.Length).SequenceEqual(s_xorStreamTail)
            || !(code[length] == 0xC3 || code[length] == 0xC2 && code[length + 1] == 4 && code[length + 2] == 0))
            return null;
        int count = BitConverter.ToInt32(code, s_xorStreamHead.Length);
        if (count <= 0 || count > 0x4000000)
            return null;
        return ("xorstream", new NativeRoutine((vm, c, a) =>
        {
            uint key = (uint)vm.NativeArg(a, 0);
            int buffer = vm.NativeArg(a, 1);
            using var verify = vm.VerifyRegion(c, a, buffer, count * 4);
            byte[] bytes = vm.ReadBytes(buffer, count * 4);
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
            for (int i = 0; i < words.Length; i++)
            {
                words[i] ^= key;
                key = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(System.Numerics.BitOperations.RotateLeft(key + 1, 3));
            }
            vm.WriteBytes(buffer, bytes);
        }));
    }

    private void RegisterNativeKernels()
    {
        // Re:Rem Plus / Maki Fes! START 8BBD0: f[0] = the 16 bytes at l[0] against those at l[1]
        // (repe cmpsb, signed bytes: -1, 0, 1); the compare 0294 sorts the montage table with
        // (81,301 entries in Re:Rem Plus, so about 1.5 million calls at start)
        RegisterNative("EE95B19696EABB2722809AF27EBEFAD9A6266667", "compare16", (vm, c, a) =>
        {
            int x = vm.NativeArg(a, 0), y = vm.NativeArg(a, 1), result = 0;
            for (int k = 0; k < 16; k++)
            {
                sbyte p = (sbyte)vm.ReadByte(x + k), q = (sbyte)vm.ReadByte(y + k);
                if (p != q)
                {
                    result = p > q ? 1 : -1;
                    break;
                }
            }
            vm.Write32(a.F, result);
        });

        // Oreimo START 79366 (Sena 797B4; MMX): dst = A' * w + B' * (257 - w) >> 8 byte by byte over 32-bit
        // pixels, w = rB * 257 / (rA + rB); A' takes B's colour where A's alpha is 0 (and B' A's).
        // l[0] dst, l[1] A, l[2] B, l[3..5] their row strides, l[6] width, l[7] height, l[8] rA, l[9] rB.
        // Ero-On!'s START 5E878 is other code that gives the same bytes (300 random cases on the
        // interpreter against 79366)
        RegisterNative(["A4344EBEFD9887DE875827C5FC8F01C6DC49DEEF", "4A177932B7CDFCE9D2FBC516D15DCB24243F0A00",
                        "9B44AD3EA3AFD3BEE7FA87EF120DEC2088989FA9"], "blend32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pa = vm.NativeArg(a, 1), pb = vm.NativeArg(a, 2);
            int sd = vm.NativeArg(a, 3), sa = vm.NativeArg(a, 4), sb = vm.NativeArg(a, 5);
            int width = vm.NativeArg(a, 6), height = vm.NativeArg(a, 7);
            uint ra = (uint)vm.NativeArg(a, 8), rb = (uint)vm.NativeArg(a, 9);
            if (width == 0 || height == 0 || ra + rb == 0)
                return;
            uint w = (uint)((ulong)rb * 0x101 / (ra + rb)), w2 = 0x101 - w;
            using var verify = vm.VerifyRegion(c, a, dst, sd * (height - 1) + width * 4);
            int bytes = width * 4;
            byte[]? spareA = null, spareB = null;
            for (int y = 0; y < height; y++, dst += sd, pa += sa, pb += sb)
            {
                // the rows in place (copied when they share bytes with the row written)
                var rowA = vm.ReadView(pa, bytes, ref spareA, dst, bytes);
                var rowB = vm.ReadView(pb, bytes, ref spareB, dst, bytes);
                var output = vm.Bytes(dst, bytes);
                for (int p = 0; p < bytes; p += 4)
                {
                    bool aClear = rowA[p] == 0, bClear = rowB[p] == 0;
                    for (int k = 0; k < 4; k++)
                    {
                        uint va = aClear && k > 0 ? rowB[p + k] : rowA[p + k];
                        uint vb = bClear && k > 0 ? rowA[p + k] : rowB[p + k];
                        uint sum = (va * w + vb * w2) & 0xFFFF;
                        output[p + k] = (byte)Math.Min(255u, sum >> 8);
                    }
                }
            }
        });

        // Oreimo START 76388 (Sena 767D4; MMX, 0x16157): copies a rectangle of a 32-bit picture to another
        // with positions in 1/16 pixels (bilinear weights at the fractions, partial coverage in
        // the alpha of the edge pixels). Written out in ScnVm.Subpixel32.cs.
        // l[0] dst, l[1] its row stride, l[2] / l[3] dst x / y, l[4] / l[5] width / height,
        // l[6] src, l[7] its stride, l[8] / l[9] src x / y (all 1/16 pixels)
        RegisterNative(["D39944F06B961BEDC6A0F25C3C4F5A87D184E244", "FDDD8FF834069B7A80B9424B868B9D71F6BDD4E8"], "subpixel32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
            int x = vm.NativeArg(a, 2), y = vm.NativeArg(a, 3), w = vm.NativeArg(a, 4), h = vm.NativeArg(a, 5);
            int length = x < 0 || y < 0 || w < 0 || h < 0 ? 0 : pitch * ((y + h) >> 4) + (((x + w) >> 4) + 1) * 4;
            using var verify = vm.VerifyRegion(c, a, dst, length);
            if (!vm.Subpixel32(dst, pitch, x, y, w, h, vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9)))
                throw vm.Error(c, "Embedded x86 routine (subpixel32): a rectangle under one pixel (the x86 code would not end)");
        });

        // Maki Fes! START 850F8, Re: Rem Plus 85C68: v2.50's build of the same copy - its loop over
        // the rows from two source rows draws one row fewer and the partly covered bottom row is
        // not drawn (ScnVm.Subpixel32, v250; 387 random cases the same as the x86 code; translated it
        // took 93 ms a call on a Galaxy S7). Arguments as above
        RegisterNative("A7DB830A1F217956E69E27B70C2AA00DC6544167", "subpixel32 v2.50", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
            int x = vm.NativeArg(a, 2), y = vm.NativeArg(a, 3), w = vm.NativeArg(a, 4), h = vm.NativeArg(a, 5);
            int length = x < 0 || y < 0 || w < 0 || h < 0 ? 0 : pitch * ((y + h) >> 4) + (((x + w) >> 4) + 1) * 4;
            using var verify = vm.VerifyRegion(c, a, dst, length);
            if (!vm.Subpixel32(dst, pitch, x, y, w, h, vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9), v250: true))
                throw vm.Error(c, "Embedded x86 routine (subpixel32): a rectangle under one pixel (the x86 code would not end)");
        });

        // Oreimo START 77108 (Azu 6BBB8; Sena 77556 and the other v2.49 games; Re: Rem Plus 866A8,
        // Maki Fes! 85B38; MMX): the enlarging case of the same copy, written out in
        // ScnVm.Enlarge32.cs (each build: 150 or more random cases the same as its x86 code; Re:
        // Rem Plus's 130 % zoom took 50 ms a frame translated, 4 ms as C#). Arguments as scale32's;
        // rectangles the C# version does not take run as x86 code (RunX86)
        RegisterNative(["67FD294324E188D2D3891314AB51537274147A92", "5F055480EAF613D53B5AAF6EE2475EA9FF247A76",
                        "E17C33CA39A48D249E621A1EB13D0FBE757B99D8"], "enlarge32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
            int right = vm.NativeArg(a, 4), bottom = vm.NativeArg(a, 5);
            int length = right < 0 || bottom < 0 ? 0 : pitch * ((bottom >> 4) + 1) + ((right >> 4) + 1) * 4;
            using var verify = vm.VerifyRegion(c, a, dst, length);
            if (!vm.Enlarge32(dst, pitch, vm.NativeArg(a, 2), vm.NativeArg(a, 3), right, bottom, vm.NativeArg(a, 6), vm.NativeArg(a, 7),
                              vm.NativeArg(a, 8), vm.NativeArg(a, 9), vm.NativeArg(a, 10), vm.NativeArg(a, 11)))
                vm.RunX86(c, a);
        });

        // Oreimo START 78380 (Sena 787CE, Re: Rem Plus 87920; MMX, 0x1632C): the scaling case of the same copy (source and
        // destination rectangles of different sizes, scaling down), with two work buffers.
        // Written out in ScnVm.Scale32.cs. l[0] dst, l[1] its stride, l[2..5]
        // dst left, top, right, bottom, l[6] src, l[7] its stride, l[8..11] src left, top, right,
        // bottom (1/16 pixels), l[12] / l[13] work buffers
        RegisterNative(["3481E54756B5B7C00408FD13CDD6C7A5F211DC97", "9122A668741F24CA756ABDBB886B20C41F54CB61",
                        "B4622533F3686687C2F5D363E992BE9B65E959EB"], "scale32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
            int right = vm.NativeArg(a, 4), bottom = vm.NativeArg(a, 5);
            int length = right < 0 || bottom < 0 ? 0 : pitch * ((bottom >> 4) + 1) + ((right >> 4) + 1) * 4;
            using var verify = vm.VerifyRegion(c, a, dst, length);
            if (!vm.Scale32(dst, pitch, vm.NativeArg(a, 2), vm.NativeArg(a, 3), right, bottom, vm.NativeArg(a, 6), vm.NativeArg(a, 7),
                            vm.NativeArg(a, 8), vm.NativeArg(a, 9), vm.NativeArg(a, 10), vm.NativeArg(a, 11)))
                throw vm.Error(c, "Embedded x86 routine (scale32): rectangles it cannot scale (the x86 code would not end)");
        });

        // Bitch Nee-chan START C7C70: the SSE2 build of the same scaling copy (Re: Rem Plus's
        // 8BDE0, which its START takes in place of 87920 when b[77] says SSE2; this START cannot
        // do without SSE2). Its arithmetic is in floats, so a pixel may come out a step off the MMX
        // build's that ScnVm.Scale32 gives; the interpreter has no SSE, so nothing to verify with
        RegisterNative("A3DCAB9DFB6CC480BF9F3995AEC7212737E5EB95", "scale32 (SSE2 build)", (vm, c, a) =>
        {
            if (!vm.Scale32(vm.NativeArg(a, 0), vm.NativeArg(a, 1), vm.NativeArg(a, 2), vm.NativeArg(a, 3), vm.NativeArg(a, 4), vm.NativeArg(a, 5),
                            vm.NativeArg(a, 6), vm.NativeArg(a, 7), vm.NativeArg(a, 8), vm.NativeArg(a, 9), vm.NativeArg(a, 10), vm.NativeArg(a, 11)))
                throw vm.Error(c, "Embedded x86 routine (scale32, SSE2 build): rectangles it cannot scale (the x86 code would not end)");
        });

        // Bitch Nee-chan START 9F5E6 (SSE2): the blur of ScnVm.Blur32.cs (15 s a call on the
        // interpreter). l[0] dst, l[1] its stride, l[2] width, l[3] height, l[4] src, l[5] its
        // stride, l[6] / l[7] the radii across and down
        RegisterNative("1276382986E6312B58F7F1B31302308A07B97B92", "blur32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), stride = vm.NativeArg(a, 1), w = vm.NativeArg(a, 2), h = vm.NativeArg(a, 3);
            using var verify = vm.VerifyRegion(c, a, dst, w <= 0 || h <= 0 ? 0 : stride * (h - 1) + w * 4);
            if (!vm.Blur32(dst, stride, w, h, vm.NativeArg(a, 4), vm.NativeArg(a, 5), vm.NativeArg(a, 6), vm.NativeArg(a, 7)))
                vm.RunX86(c, a);
        });

        // Oreimo START 796E5 (Sena 79B33; MMX, the rule fade of 0x16EB3): dst = S with its alpha byte made
        // from the rule mask M (the mask's top byte m, or 255 - m when dir is set): m <= imin
        // gives 0, m >= imax keeps S's alpha s, between them ((m - imin') * q in 16 bits, signed)
        // * s >> 15 with q = 0x8080 / (imax - imin + 1) and imin' = max(imin - 1, 0) (imin 0: 0).
        // Comparisons are signed 16-bit (pcmpgtw). l[0] dst, l[1] S, l[2] M, l[3..5] their row
        // strides, l[6] width, l[7] height, l[8] imax, l[9] imin, l[10] dir
        // Re: Rem Plus 88C84 and Maki Fes! 88114 are the same instructions (padding and ret apart)
        RegisterNative(["42BE6C919F9A6508C03AAA5B66020C3951CC7C05", "5D646499AC90624AAED39ACAC0A3AD67B53C2A81",
                        "60E811498D402206EC8562EE495A115C4A828B79"], "rulealpha", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), ps = vm.NativeArg(a, 1), pm = vm.NativeArg(a, 2);
            int sd = vm.NativeArg(a, 3), ss = vm.NativeArg(a, 4), sm = vm.NativeArg(a, 5);
            int width = vm.NativeArg(a, 6), height = vm.NativeArg(a, 7);
            int imax = vm.NativeArg(a, 8), imin = vm.NativeArg(a, 9);
            bool invert = vm.NativeArg(a, 10) != 0;
            if (width == 0 || height == 0)
                return;
            uint divisor = (uint)(imax - imin + 1);
            if (divisor == 0)
                throw vm.Error(c, "Embedded x86 routine (rule alpha): division by zero");
            ushort q = (ushort)(0x8080u / divisor);
            ushort bias = (ushort)(imin == 0 ? 0 : imin - 1);
            short max = (short)imax, min = (short)imin;
            using var verify = vm.VerifyRegion(c, a, dst, sd * (height - 1) + width * 4);
            int bytes = width * 4;
            byte[]? spareS = null, spareM = null;
            for (int y = 0; y < height; y++, dst += sd, ps += ss, pm += sm)
            {
                // S's row into the destination row, then its alpha bytes from the mask (the rows
                // in place; copied when they share bytes with the row written)
                var rowS = vm.ReadView(ps, bytes, ref spareS, dst, bytes);
                var rowM = vm.ReadView(pm, bytes, ref spareM, dst, bytes);
                var output = vm.Bytes(dst, bytes);
                rowS.CopyTo(output);
                for (int p = 0; p < bytes; p += 4)
                {
                    short m = (short)(invert ? 255 - rowM[p + 3] : rowM[p + 3]);
                    bool belowMax = max > m, inside = belowMax && m > min;
                    int s = rowS[p];
                    ushort w = inside ? (ushort)(m - bias) : (ushort)0;
                    int product = (short)(ushort)(w * q) * s;
                    uint v = (uint)product >> 15;
                    output[p] = (byte)(v | (belowMax ? 0u : (uint)s));
                }
            }
        });

        // Ero-On! START 5E2D6 (MMX, the scaling-down case of 0x14CC0): the first v2.49 build's
        // own way of Oreimo's scale32, written out in ScnVm.Scale16.cs. l[0] dst, l[1] its stride,
        // l[2..5] dst left, top, right, bottom, l[6] src, l[7] its stride, l[8..11] src left, top,
        // right, bottom (1/16 pixels), l[12] / l[13] the column / row weights, l[14] / l[15] the
        // column / row entries (24 bytes each). The x86 code makes the row entries before the
        // column tables: where those run into them, it runs as x86 code instead
        RegisterNative("31DAD459219DC78270C7147720CA1959DFEA5AFB", "scale16", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
            int dl = vm.NativeArg(a, 2), dt = vm.NativeArg(a, 3), dr = vm.NativeArg(a, 4), db = vm.NativeArg(a, 5);
            int sl = vm.NativeArg(a, 8), sr = vm.NativeArg(a, 10);
            long columns = (uint)(dr - dl) >> 4, rows = (uint)(db - dt) >> 4;
            long colWeights = vm.NativeArg(a, 12), colEntries = vm.NativeArg(a, 14), rowEntries = vm.NativeArg(a, 15);
            long rowEnd = rowEntries + rows * 24;
            bool Overlaps(long from, long length) => from < rowEnd && rowEntries < from + length;
            if (Overlaps(colWeights, ((long)(uint)(dr - dl) + 1) * 8) || Overlaps(colEntries, columns * 24))
            {
                vm.RunX86(c, a);
                return;
            }
            int length = rows == 0 || columns == 0 ? 0 : (int)(pitch * (((uint)dt >> 4) + rows - 1) + (((uint)dl >> 4) + columns) * 4);
            using var verify = vm.VerifyRegion(c, a, dst, length);
            if (!vm.Scale16(dst, pitch, dl, dt, dr, db, vm.NativeArg(a, 6), vm.NativeArg(a, 7), sl, vm.NativeArg(a, 9), sr, vm.NativeArg(a, 11)))
                throw vm.Error(c, "Embedded x86 routine (scale16): rectangles it cannot scale (the x86 code would not end)");
        });

        // Ero-On! START 5DFA5 (MMX, the enlarging case of 0x14BCE), written out in
        // ScnVm.Enlarge16.cs. l[0] dst, l[1] its stride, l[2..5] dst left, top, right, bottom,
        // l[6] src, l[7] its stride, l[8..11] src left, top, right, bottom (1/16 pixels), l[12] the
        // weight table. Rectangles it does not enlarge run as x86 code
        RegisterNative("27A535558D763B2B8F0008F2476F0FA14EB45AAB", "enlarge16", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
            int dl = vm.NativeArg(a, 2), dt = vm.NativeArg(a, 3), dr = vm.NativeArg(a, 4), db = vm.NativeArg(a, 5);
            long columns = (uint)(dr - dl) >> 4, rows = (uint)(db - dt) >> 4;
            int sl = vm.NativeArg(a, 8), st = vm.NativeArg(a, 9), sr = vm.NativeArg(a, 10), sb = vm.NativeArg(a, 11);
            if (sr - sl < 0 || sb - st < 0 || dr - dl < sr - sl || db - dt < sb - st)
            {
                vm.RunX86(c, a);
                return;
            }
            int length = rows == 0 || columns == 0 ? 0 : (int)(pitch * (((uint)dt >> 4) + rows - 1) + (((uint)dl >> 4) + columns) * 4);
            using var verify = vm.VerifyRegion(c, a, dst, length);
            if (!vm.Enlarge16(dst, pitch, dl, dt, dr, db, vm.NativeArg(a, 6), vm.NativeArg(a, 7), sl, st, sr, sb, vm.NativeArg(a, 12)))
                throw vm.Error(c, "Embedded x86 routine (enlarge16): rectangles too large");
        });
    }
}
