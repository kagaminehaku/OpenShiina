// C# versions of embedded x86 routines that run hot, written by hand: faster still than their
// X86Jit translation (Vector128, all cores). Each is found by the signature of the whole routine
// (X86Routine.Signature) and must give the bytes the x86 code gives; VerifyNatives runs the
// interpreter as well and compares (for tests).
//
// Each comes in two builds with the same instructions: the v2.47 one (Oreimo and Azu Plus, also
// scale32 of Maki Fes! and Re: Rem Plus) ends with "ret 4", the v2.49 one (Homu, Yuru, Nyaru,
// Rikka, Sena and Kuroneko Plus, also blend32 of Maki Fes! and Re: Rem Plus) with "ret"; the
// padding between their blocks differs too, so their signatures do.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>Run each replaced routine on the interpreter too and compare the results.</summary>
    public bool VerifyNatives { get; set; }

    private void RegisterNativeKernels()
    {
        // Oreimo START 79366 (Sena 797B4; MMX): dst = A' * w + B' * (257 - w) >> 8 byte by byte over 32-bit
        // pixels, w = rB * 257 / (rA + rB); A' takes B's colour where A's alpha is 0 (and B' A's).
        // l[0] dst, l[1] A, l[2] B, l[3..5] their row strides, l[6] width, l[7] height, l[8] rA, l[9] rB
        RegisterNative(["A4344EBEFD9887DE875827C5FC8F01C6DC49DEEF", "4A177932B7CDFCE9D2FBC516D15DCB24243F0A00"], "blend32", (vm, c, a) =>
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
            var rowA = new byte[bytes];
            var rowB = new byte[bytes];
            var output = new byte[bytes];
            for (int y = 0; y < height; y++, dst += sd, pa += sa, pb += sb)
            {
                vm.ReadBytes(pa, rowA);
                vm.ReadBytes(pb, rowB);
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
                vm.WriteBytes(dst, output);
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

        // Oreimo START 78380 (Sena 787CE; MMX, 0x1632C): the scaling case of the same copy (source and
        // destination rectangles of different sizes, scaling down), with two work buffers.
        // Written out in ScnVm.Scale32.cs. l[0] dst, l[1] its stride, l[2..5]
        // dst left, top, right, bottom, l[6] src, l[7] its stride, l[8..11] src left, top, right,
        // bottom (1/16 pixels), l[12] / l[13] work buffers
        RegisterNative(["3481E54756B5B7C00408FD13CDD6C7A5F211DC97", "9122A668741F24CA756ABDBB886B20C41F54CB61"], "scale32", (vm, c, a) =>
        {
            int dst = vm.NativeArg(a, 0), pitch = vm.NativeArg(a, 1);
            int right = vm.NativeArg(a, 4), bottom = vm.NativeArg(a, 5);
            int length = right < 0 || bottom < 0 ? 0 : pitch * ((bottom >> 4) + 1) + ((right >> 4) + 1) * 4;
            using var verify = vm.VerifyRegion(c, a, dst, length);
            if (!vm.Scale32(dst, pitch, vm.NativeArg(a, 2), vm.NativeArg(a, 3), right, bottom, vm.NativeArg(a, 6), vm.NativeArg(a, 7),
                            vm.NativeArg(a, 8), vm.NativeArg(a, 9), vm.NativeArg(a, 10), vm.NativeArg(a, 11)))
                throw vm.Error(c, "Embedded x86 routine (scale32): rectangles it cannot scale (the x86 code would not end)");
        });

        // Oreimo START 796E5 (Sena 79B33; MMX, the rule fade of 0x16EB3): dst = S with its alpha byte made
        // from the rule mask M (the mask's top byte m, or 255 - m when dir is set): m <= imin
        // gives 0, m >= imax keeps S's alpha s, between them ((m - imin') * q in 16 bits, signed)
        // * s >> 15 with q = 0x8080 / (imax - imin + 1) and imin' = max(imin - 1, 0) (imin 0: 0).
        // Comparisons are signed 16-bit (pcmpgtw). l[0] dst, l[1] S, l[2] M, l[3..5] their row
        // strides, l[6] width, l[7] height, l[8] imax, l[9] imin, l[10] dir
        RegisterNative(["42BE6C919F9A6508C03AAA5B66020C3951CC7C05", "5D646499AC90624AAED39ACAC0A3AD67B53C2A81"], "rulealpha", (vm, c, a) =>
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
            var rowS = new byte[bytes];
            var rowM = new byte[bytes];
            for (int y = 0; y < height; y++, dst += sd, ps += ss, pm += sm)
            {
                vm.ReadBytes(ps, rowS);
                vm.ReadBytes(pm, rowM);
                for (int p = 0; p < bytes; p += 4)
                {
                    short m = (short)(invert ? 255 - rowM[p + 3] : rowM[p + 3]);
                    bool belowMax = max > m, inside = belowMax && m > min;
                    int s = rowS[p];
                    ushort w = inside ? (ushort)(m - bias) : (ushort)0;
                    int product = (short)(ushort)(w * q) * s;
                    uint v = (uint)product >> 15;
                    rowS[p] = (byte)(v | (belowMax ? 0u : (uint)s));
                }
                vm.WriteBytes(dst, rowS);
            }
        });
    }
}
