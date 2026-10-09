// C# versions of the rest of the embedded x86 routines of the eleven games (2026-10-09): the
// small ones (copies, strings, tables, checksums) and the pixel loops that are not zooms or
// blends (sepia, thumbnails, Ero-On!'s same-size zoom). Each is found by the signature of the
// whole routine (X86Routine.Signature, one per build) like the kernels of ScnVm.NativeKernels.cs
// and gives the bytes the x86 code gives; cases it does not take (blocks that overlap in a way
// the result depends on, counts the x86 code would wrap round to 4 billion) run as x86 code
// (RunX86). Builds: v2.47 = Oreimo and Azu Plus ("ret 4"), v2.49 = Homu, Yuru, Nyaru, Rikka, Sena,
// Kuroneko Plus and Ero-On! ("ret"), v2.50 = Maki Fes! and Re: Rem Plus. Offsets are Oreimo's /
// Sena's / Re: Rem Plus's START unless said otherwise. Left out: the SSE2 build of the zoom
// (Oreimo 79A20, Sena 79E70, Re: Rem Plus 8BDE0), which never runs: the scripts take their MMX
// paths, as the CPU the interpreter reports has no SSE.

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
        // START 75A84 / 75EE4 / 85384: the CPU's features (CPUID: 1 Intel, 2 AMD, 10h MMX, 20h SSE,
        // 40h SSE2, 80h SSE3...) into l[0] and into the dword before the routine (gCPUID). The
        // CPU the interpreter reports (X86Cpu.Cpuid: an Intel with MMX, no SSE) gives 0x11
        RegisterNative(["B6FF80115E4E2727617C10E780653984637EDB0D", "9C94055F3764EB20CCEA440A6A01BE9363DBC93F"], "cpuid", (vm, c, a) =>
        {
            const int Features = 0x11;
            using var verify = vm.VerifyRegions(c, a, (a.Stack, 4), (vm.m_nativeTarget - 4, 4));
            vm.SetL(a, 0, Features);
            vm.Write32(vm.m_nativeTarget - 4, Features);
        });

        // START 75B51 / 75FAF: the save thumbnail, 100 x 75 from an 800 x 600 BGR picture: each
        // byte the average of an 8 x 8 block. Ero-On! 5D7A0: 16 x 12 from blocks of 50 x 50.
        // l[0] dst (bytes in a row), l[4] src (rows of 2400 bytes)
        RegisterNative(["DCEE38C4FB3BC259CFB217ABD2D2F712B23BB57A", "1444F8C617F489497F12A55553446AC99F217C14"], "thumbnail",
                       (vm, c, a) => vm.Thumbnail(c, a, 8, 100, 75));
        RegisterNative("1174F52A9A080EAEF9B086E491846FF3D5B35EB8", "thumbnail", (vm, c, a) => vm.Thumbnail(c, a, 50, 16, 12));

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

        // START 75FEE / 76434 / 858D0 (Ero-On! 5DC31): l[0] = NOT l[1], l[2] bytes, by 8 bytes
        // (l[3] = 0: the count rounded down to 8) or by 4
        RegisterNative(["8EE6D4D8C30D61F6379F0F7FA42BAB648AE0483E", "0CF868D5FF21E737808C7235BF0D164BFDE48B4F",
                        "516F6F4090493362A1590006FF9F3031DFFC9346", "039192F71200D43AF29A5F5AE0682CCAC4317F2C"], "not", (vm, c, a) =>
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

        // EFCLIB 1E9E (Re: Rem Plus 2792): the negative and other fills: l[3] dwords from l[4] to
        // l[0], each XORed with l[5]'s byte in every byte
        RegisterNative(["EB59CB1AF7084D666150A58C5866638D49AAC562", "BD87E97A68BDF28ECD10B4E3EBDBD6799D5A0F1D",
                        "997E3DD49B9B61739720F69D2E180ADAC4C9D884"], "xor32", (vm, c, a) =>
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

        // EFCLIB 54A0 / 549E / 6D79: l[1] dwords at l[0] inverted in place
        RegisterNative(["4158148EFBFBC3AA4CE88FF4D6D5D2B9C5707615", "8DC1060C677A722C3CACF4865B7E506824EB7942",
                        "863B772CB1F64D67213C7F27F74AEEC6471CBD3A"], "invert32", (vm, c, a) =>
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

    private void Thumbnail(ScnContext c, ScnNativeArgs a, int block, int columns, int rows)
    {
        const int Stride = 2400;
        int dst = NativeArg(a, 0), src = NativeArg(a, 4);
        int outBytes = columns * rows * 3, inRows = rows * block;
        if (!Apart(dst, outBytes, src, (long)Stride * inRows))
        {
            RunX86(c, a);
            return;
        }
        using var verify = VerifyRegion(c, a, dst, outBytes);
        byte[] picture = ReadBytes(src, Stride * (inRows - 1) + columns * block * 3);
        var output = new byte[outBytes];
        int o = 0;
        for (int by = 0; by < rows; by++)
            for (int bx = 0; bx < columns; bx++)
                for (int k = 0; k < 3; k++)
                {
                    int sum = 0;
                    for (int y = 0; y < block; y++)
                    {
                        int p = (by * block + y) * Stride + bx * block * 3 + k;
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
