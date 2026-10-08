// SCNBOOT_GPUCHECK=cases: the GPU mode's kernels against the CPU code on random pictures and
// rectangles (each case drawn by both into the same destination, compared byte for byte), then
// the time of a large case on each. The interpreter only lends its memory; no frame is run.

using System.Diagnostics;
using OpenShiina.Scripting;

static class GpuCheck
{
    private delegate bool Draw(ScnVm vm, int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb, int table);

    private static readonly (string Name, bool Enlarges, Draw Draw)[] s_kernels =
    [
        ("scale32", false, (vm, d, dp, dl, dt, dr, db, s, sp, sl, st, sr, sb, _) => vm.Scale32(d, dp, dl, dt, dr, db, s, sp, sl, st, sr, sb)),
        ("scale16", false, (vm, d, dp, dl, dt, dr, db, s, sp, sl, st, sr, sb, _) => vm.Scale16(d, dp, dl, dt, dr, db, s, sp, sl, st, sr, sb)),
        ("enlarge16", true, (vm, d, dp, dl, dt, dr, db, s, sp, sl, st, sr, sb, t) => vm.Enlarge16(d, dp, dl, dt, dr, db, s, sp, sl, st, sr, sb, t)),
    ];

    public static int Run(ScnVm vm, IScnAccelerator gpu, int cases)
    {
        var random = new Random(1);
        vm.GpuAlways = true;
        int failed = 0;
        foreach (var (name, enlarges, draw) in s_kernels)
        {
            int run = 0, same = 0;
            for (int i = 0; i < cases; i++)
            {
                // A source rectangle in 1/16 pixels, and a destination smaller (scaling) or larger
                int sw = random.Next(16, (i % 4 == 0 ? 1700 : 300) * 16), sh = random.Next(16, (i % 4 == 0 ? 1000 : 200) * 16);
                double factor = enlarges ? 1 + random.NextDouble() * 2.5 : 0.25 + random.NextDouble() * 0.75;
                int dw = Math.Max(16, (int)(sw * factor)), dh = Math.Max(16, (int)(sh * factor));
                if (enlarges)
                    (dw, dh) = (Math.Max(dw, sw), Math.Max(dh, sh));
                int sl = random.Next(0, 64), st = random.Next(0, 64), dl = random.Next(0, 64), dt = random.Next(0, 64);
                if (Compare(vm, gpu, draw, dl, dt, dl + dw, dt + dh, sl, st, sl + sw, st + sh, random) is { } result)
                {
                    run++;
                    if (result)
                        same++;
                    else
                    {
                        failed++;
                        Console.WriteLine($"  {name} differs: dst {dl},{dt}-{dl + dw},{dt + dh} src {sl},{st}-{sl + sw},{st + sh}");
                    }
                }
            }
            Console.WriteLine($"{name}: {same} of {run} cases the same on the GPU ({cases - run} not drawn by the CPU code)");
            // A large case: Re:Rem's zoom, or Ero-On!'s enlarging of a quarter of the screen
            var (big, time) = enlarges
                ? (Size: (640 * 16, 360 * 16, 1280 * 16, 720 * 16), 0)
                : (Size: (1600 * 16, 900 * 16, 1280 * 16, 720 * 16), 0);
            _ = time;
            foreach (var on in new[] { false, true })
            {
                vm.Accelerator = on ? gpu : null;
                double ms = Time(vm, draw, big.Item1, big.Item2, big.Item3, big.Item4, random);
                Console.WriteLine($"  {big.Item1 / 16}x{big.Item2 / 16} -> {big.Item3 / 16}x{big.Item4 / 16} on the {(on ? "GPU" : "CPU")}: {ms:F2} ms");
            }
            vm.Accelerator = null;
        }
        failed += CheckSubpixel(vm, gpu, cases, random);
        failed += CheckRotateZoom(vm, gpu, cases, random);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 0574's pixels on random turns and zooms: the CPU's rows shared out (the source read once)
    /// against row by row as the x86 code draws them, and the GPU against both; then the time.
    /// </summary>
    private static int CheckRotateZoom(ScnVm vm, IScnAccelerator gpu, int cases, Random random)
    {
        int same = 0, failed = 0;
        for (int i = 0; i < cases; i++)
        {
            int w = random.Next(1, i % 4 == 0 ? 1280 : 300), h = random.Next(1, i % 4 == 0 ? 720 : 200);
            int sw = random.Next(1, i % 4 == 0 ? 1280 : 300), sh = random.Next(1, i % 4 == 0 ? 720 : 200);
            var (dst, dstPitch) = Picture(vm, w + 8, h + 8, random, fill: true);
            var (src, srcPitch) = Picture(vm, sw + 8, sh + 8, random, fill: true);
            int l = random.Next(0, 4), t = random.Next(0, 4);
            int sx = random.Next(-2, 4), sy = random.Next(-2, 4);
            double angle = random.NextDouble() * 2 * Math.PI, zoom = 0.4 + random.NextDouble() * 2;
            long cos = (long)(Math.Cos(angle) * 4294967296.0 / zoom), sin = (long)(Math.Sin(angle) * 4294967296.0 / zoom);
            long u0 = (long)((random.NextDouble() * sw - w / 2.0) * 4294967296.0), v0 = (long)((random.NextDouble() * sh - h / 2.0) * 4294967296.0);
            var area = new ScnVm.RotateZoomArea(dst, dstPitch, l, t, l + w, t + h, src, srcPitch, sx, sy, sx + sw, sy + sh,
                                                u0, v0, cos, sin, -sin, cos, random.Next(2) == 0, random.Next());
            int length = dstPitch * (h + 8);
            try
            {
                byte[] start = vm.ReadBytes(dst, length);
                vm.Accelerator = null;
                vm.RotateZoomPixels(area, inTurn: true);
                byte[] inTurn = vm.ReadBytes(dst, length);
                vm.WriteBytes(dst, start);
                vm.RotateZoomPixels(area);
                byte[] shared = vm.ReadBytes(dst, length);
                vm.WriteBytes(dst, start);
                vm.Accelerator = gpu;
                vm.RotateZoomPixels(area);
                byte[] onGpu = vm.ReadBytes(dst, length);
                if (inTurn.AsSpan().SequenceEqual(shared) && inTurn.AsSpan().SequenceEqual(onGpu))
                    same++;
                else
                {
                    failed++;
                    Console.WriteLine($"  0574 differs ({(inTurn.AsSpan().SequenceEqual(shared) ? "GPU" : "CPU rows shared out")}): {w}x{h} from {sw}x{sh}, angle {angle:F3}, zoom {zoom:F2}");
                }
            }
            finally
            {
                vm.Accelerator = null;
                vm.Free(src);
                vm.Free(dst);
            }
        }
        Console.WriteLine($"rotatezoom: {same} of {cases} cases the same on the GPU, with the rows shared out and row by row");
        var (bigDst, bigDstPitch) = Picture(vm, 1280, 720, random, fill: false);
        var (bigSrc, bigSrcPitch) = Picture(vm, 1280, 720, random, fill: true);
        long c30 = (long)(Math.Cos(0.5) * 4294967296.0 / 1.3), s30 = (long)(Math.Sin(0.5) * 4294967296.0 / 1.3);
        var big = new ScnVm.RotateZoomArea(bigDst, bigDstPitch, 0, 0, 1280, 720, bigSrc, bigSrcPitch, 0, 0, 1280, 720,
                                           (long)(-200.0 * 4294967296.0), (long)(100.0 * 4294967296.0), c30, s30, -s30, c30, false, 0);
        foreach (var (label, on, inTurn) in new[] { ("CPU row by row", false, true), ("CPU", false, false), ("GPU", true, false) })
        {
            vm.Accelerator = on ? gpu : null;
            vm.RotateZoomPixels(big, inTurn);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
                vm.RotateZoomPixels(big, inTurn);
            Console.WriteLine($"  1280x720 turned on the {label}: {clock.Elapsed.TotalMilliseconds / 20:F2} ms");
        }
        vm.Accelerator = null;
        vm.Free(bigSrc);
        vm.Free(bigDst);
        return failed;
    }

    /// <summary>subpixel32 (positions and sizes in 1/16 pixels, no scaling) on random rectangles, then its time.</summary>
    private static int CheckSubpixel(ScnVm vm, IScnAccelerator gpu, int cases, Random random)
    {
        int run = 0, same = 0, failed = 0;
        for (int i = 0; i < cases; i++)
        {
            int w = random.Next(16, (i % 4 == 0 ? 1280 : 200) * 16), h = random.Next(16, (i % 4 == 0 ? 720 : 150) * 16);
            int dtx = random.Next(0, 640), dty = random.Next(0, 640), stx = random.Next(0, 640), sty = random.Next(0, 640);
            if (i % 5 == 1)
                (stx, sty) = (dtx + 16 * random.Next(0, 3), dty + 16 * random.Next(0, 3));
            var (src, srcPitch) = Picture(vm, ((stx + w) >> 4) + 4, ((sty + h) >> 4) + 4, random, fill: true);
            var (dst, dstPitch) = Picture(vm, ((dtx + w) >> 4) + 4, ((dty + h) >> 4) + 4, random, fill: false);
            int length = dstPitch * (((dty + h) >> 4) + 4);
            try
            {
                byte[] start = vm.ReadBytes(dst, length);
                vm.Accelerator = null;
                if (!vm.Subpixel32(dst, dstPitch, dtx, dty, w, h, src, srcPitch, stx, sty))
                    continue;
                byte[] cpu = vm.ReadBytes(dst, length);
                vm.WriteBytes(dst, start);
                vm.Accelerator = gpu;
                vm.Subpixel32(dst, dstPitch, dtx, dty, w, h, src, srcPitch, stx, sty);
                run++;
                if (cpu.AsSpan().SequenceEqual(vm.ReadBytes(dst, length)))
                    same++;
                else
                {
                    failed++;
                    Console.WriteLine($"  subpixel32 differs: dst {dtx},{dty} size {w}x{h} src {stx},{sty}");
                }
            }
            finally
            {
                vm.Accelerator = null;
                vm.Free(dst);
                vm.Free(src);
            }
        }
        Console.WriteLine($"subpixel32: {same} of {run} cases the same on the GPU ({cases - run} not drawn by the CPU code)");
        var (bigSrc, bigSrcPitch) = Picture(vm, 1284, 724, random, fill: true);
        var (bigDst, bigDstPitch) = Picture(vm, 1284, 724, random, fill: false);
        foreach (var on in new[] { false, true })
        {
            vm.Accelerator = on ? gpu : null;
            vm.Subpixel32(bigDst, bigDstPitch, 23, 19, 1280 * 16 - 32, 720 * 16 - 32, bigSrc, bigSrcPitch, 16, 16);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
                vm.Subpixel32(bigDst, bigDstPitch, 23, 19, 1280 * 16 - 32, 720 * 16 - 32, bigSrc, bigSrcPitch, 16, 16);
            Console.WriteLine($"  1280x720 at 7/16, 3/16 of a pixel on the {(on ? "GPU" : "CPU")}: {clock.Elapsed.TotalMilliseconds / 20:F2} ms");
        }
        vm.Accelerator = null;
        vm.Free(bigDst);
        vm.Free(bigSrc);
        return failed;
    }

    private static (int Address, int Pitch) Picture(ScnVm vm, int width, int height, Random random, bool fill)
    {
        int pitch = width * 4;
        var bytes = new byte[pitch * height];
        if (fill)
        {
            random.NextBytes(bytes);
            // Mostly opaque, some clear or partly clear pixels
            for (int i = 3; i < bytes.Length; i += 4)
                bytes[i] = (i / 4 % 7) switch { 0 => 0, 1 => bytes[i], _ => 0xFF };
        }
        else
            Array.Fill(bytes, (byte)0xCD);
        return (vm.AllocateCopy(bytes), pitch);
    }

    /// <summary>One case drawn by the CPU and the GPU from the same start; null when the CPU code does not draw it.</summary>
    private static bool? Compare(ScnVm vm, IScnAccelerator gpu, Draw draw, int dl, int dt, int dr, int db, int sl, int st, int sr, int sb, Random random)
    {
        var (src, srcPitch) = Picture(vm, (sr >> 4) + 2, (sb >> 4) + 2, random, fill: true);
        var (dst, dstPitch) = Picture(vm, (dr >> 4) + 2, (db >> 4) + 2, random, fill: false);
        int table = vm.Allocate(((sr - sl) + 2) * 8);
        int length = dstPitch * ((db >> 4) + 2);
        try
        {
            byte[] start = vm.ReadBytes(dst, length);
            vm.Accelerator = null;
            if (!draw(vm, dst, dstPitch, dl, dt, dr, db, src, srcPitch, sl, st, sr, sb, table))
                return null;
            byte[] cpu = vm.ReadBytes(dst, length);
            vm.WriteBytes(dst, start);
            vm.Accelerator = gpu;
            draw(vm, dst, dstPitch, dl, dt, dr, db, src, srcPitch, sl, st, sr, sb, table);
            byte[] onGpu = vm.ReadBytes(dst, length);
            return cpu.AsSpan().SequenceEqual(onGpu);
        }
        finally
        {
            vm.Accelerator = null;
            vm.Free(table);
            vm.Free(dst);
            vm.Free(src);
        }
    }

    private static double Time(ScnVm vm, Draw draw, int sw, int sh, int dw, int dh, Random random)
    {
        var (src, srcPitch) = Picture(vm, (sw >> 4) + 2, (sh >> 4) + 2, random, fill: true);
        var (dst, dstPitch) = Picture(vm, (dw >> 4) + 2, (dh >> 4) + 2, random, fill: false);
        int table = vm.Allocate((sw + 2) * 8);
        try
        {
            draw(vm, dst, dstPitch, 0, 0, dw, dh, src, srcPitch, 0, 0, sw, sh, table);
            var clock = Stopwatch.StartNew();
            const int times = 20;
            for (int i = 0; i < times; i++)
                draw(vm, dst, dstPitch, 0, 0, dw, dh, src, srcPitch, 0, 0, sw, sh, table);
            return clock.Elapsed.TotalMilliseconds / times;
        }
        finally
        {
            vm.Free(table);
            vm.Free(dst);
            vm.Free(src);
        }
    }
}
