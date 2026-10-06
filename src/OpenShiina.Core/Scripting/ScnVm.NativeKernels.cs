// C# versions of embedded x86 routines that run hot (the interpreter runs them too, slowly).
// Each is found by the SHA-1 of its first 64 bytes and must give the bytes the x86 code gives;
// VerifyNatives runs the interpreter as well and compares (for tests).

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>Run each replaced routine on the interpreter too and compare the results.</summary>
    public bool VerifyNatives { get; set; }

    private void RegisterNativeKernels()
    {
        // Oreimo START 79366 (MMX): dst = A' * w + B' * (257 - w) >> 8 byte by byte over 32-bit
        // pixels, w = rB * 257 / (rA + rB); A' takes B's colour where A's alpha is 0 (and B' A's).
        // l[0] dst, l[1] A, l[2] B, l[3..5] their row strides, l[6] width, l[7] height, l[8] rA, l[9] rB
        RegisterNative("E3C1DCBBB9F5C465B07D1D4447E5CF1518F50573", "blend32", (vm, c, a) =>
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
    }
}
