// Picture kernels of the executable, done the way it does them on a CPU with MMX (the path the
// game takes on any current PC), so the pictures match it pixel for pixel.

using System.Numerics;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // op_0500: the brightness the display surface was made with last (0x482D38)
    private int m_screenLevel = -1;

    /// <summary>
    /// FUN_004396F0 with MMX (the path of every current PC): dst = A * a / (a + b) + B * b / (a + b)
    /// byte by byte, for <paramref name="width"/> pixels (3 bytes each) on <paramref name="height"/>
    /// rows that all have <paramref name="pitch"/>. <paramref name="source2"/> below 0x100 is a grey
    /// level instead of a second picture. In each row the bytes up to an 8-byte aligned destination
    /// and the last 1-3 come from tables, the 8- and 4-byte blocks from 16-bit MMX arithmetic:
    ///   level: table (level * b + v * a) / (a + b); blocks (v * m >> 8) + k, saturated,
    ///          m = a * 256 / (a + b), k = level * b / (a + b)
    ///   two:   table tA[A] + tB[B] (FUN_004396B4); blocks (A * m + B * (256 - m)) >> 8
    /// </summary>
    public void BlendRows(int dst, int source, int source2, int a, int b, int width, int height, int pitch)
    {
        int sum = a + b;
        if (sum == 0)
            return;
        bool level = (uint)source2 < 0x100;
        byte[] tableA = new byte[256], tableB = new byte[256];
        int m, k = 0;
        if (level)
        {
            for (int v = 0; v < 256; v++)
                tableA[v] = (byte)(((ulong)(uint)source2 * (uint)b + (ulong)(uint)a * (uint)v) / (uint)sum);
            m = (int)((ulong)(uint)a * 256 / (uint)sum);
            k = (int)((ulong)(uint)b * (uint)source2 / (uint)sum);
        }
        else
        {
            StepTable(tableA, a, sum);
            StepTable(tableB, b, sum);
            m = (int)((ulong)(uint)a * 256 / (uint)sum);
        }
        int rowBytes = width * 3;
        // A source row that starts inside the destination row (not on it) is copied first: the
        // row is read whole before it is written, as the x86 code reads ahead
        bool Shifted(int from) => from != dst && Math.Abs((long)from - dst) < rowBytes;
        bool copyA = Shifted(source), copyB = !level && Shifted(source2);
        void Rows(int from, int to)
        {
            var bufferA = copyA ? new byte[rowBytes] : null;
            var bufferB = copyB ? new byte[rowBytes] : null;
            for (int y = from; y < to; y++)
            {
                int at = dst + y * pitch;
                ReadOnlySpan<byte> rowA = bufferA ?? Bytes(source + y * pitch, rowBytes);
                if (bufferA != null)
                    ReadBytes(source + y * pitch, bufferA);
                ReadOnlySpan<byte> rowB = level ? default : bufferB ?? Bytes(source2 + y * pitch, rowBytes);
                if (bufferB != null)
                    ReadBytes(source2 + y * pitch, bufferB);
                BlendRow(rowA, rowB, Bytes(at, rowBytes), at, tableA, tableB, m, k, level);
            }
        }
        // The rows go on all cores where no row reads what another writes: a source that is the
        // destination itself or apart from it (a source a few rows off the destination is read
        // after the rows above were written, so those are drawn in turn)
        long Region(int start) => start + (long)(height - 1) * pitch + rowBytes;
        bool Apart(int from) => from == dst || Region(from) <= dst || Region(dst) <= from;
        if (height >= 2 * BlendBand && pitch >= rowBytes && (long)width * height >= 20000 && Apart(source) && (level || Apart(source2)))
        {
            for (int y = 0; y < height; y++)
            {
                foreach (int start in level ? [dst, source] : new[] { dst, source, source2 })
                {
                    ReadByte(start + y * pitch);
                    ReadByte(start + y * pitch + rowBytes - 1);
                }
            }
            Parallel.For(0, (height + BlendBand - 1) / BlendBand, band => Rows(band * BlendBand, Math.Min(height, (band + 1) * BlendBand)));
        }
        else
            Rows(0, height);
    }

    private const int BlendBand = 16;

    /// <summary>
    /// One row of BlendRows into <paramref name="output"/> (its destination at <paramref name="dst"/>):
    /// the tables for the bytes up to an 8-byte aligned address and the last 1-3, the MMX blocks
    /// between. <paramref name="level"/>: a grey level (tableA has it, m and k the blocks), no rowB.
    /// The rows may be the output itself: each byte is read before it is written.
    /// </summary>
    private static void BlendRow(ReadOnlySpan<byte> rowA, ReadOnlySpan<byte> rowB, Span<byte> output, int dst, byte[] tableA, byte[] tableB, int m, int k, bool level)
    {
        int n = output.Length, head = -dst & 7;
        // The x86 code's order: 1-3 bytes from the tables to a 4-byte boundary, a 4-byte block to
        // an 8-byte one, 8-byte blocks, a 4-byte block, the last 1-3 bytes from the tables
        int start = Math.Min(head & 3, n), rest = n - start;
        int afterFour = rest - (rest >= 4 && (head & 4) != 0 ? 4 : 0);
        int end = n - afterFour % 4;
        int vectors = start + (end - start) / Vector<byte>.Count * Vector<byte>.Count;
        if (level)
        {
            for (int i = 0; i < start; i++)
                output[i] = tableA[rowA[i]];
            // the blocks in 16-bit lanes, as the MMX code: (v * m >> 8) + k, saturated
            var vm = new Vector<ushort>((ushort)m);
            var vk = new Vector<ushort>((ushort)k);
            for (int i = start; i < vectors; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(rowA[i..]), out var low, out var high);
                low = Vector.Min(Vector.ShiftRightLogical(low * vm, 8) + vk, new Vector<ushort>(255));
                high = Vector.Min(Vector.ShiftRightLogical(high * vm, 8) + vk, new Vector<ushort>(255));
                Vector.Narrow(low, high).CopyTo(output[i..]);
            }
            for (int i = vectors; i < end; i++)
                output[i] = (byte)Math.Min(255, ((rowA[i] * m) >> 8) + k);
            for (int i = end; i < n; i++)
                output[i] = tableA[rowA[i]];
            return;
        }
        int w = 256 - m;
        for (int i = 0; i < start; i++)
            output[i] = (byte)(tableA[rowA[i]] + tableB[rowB[i]]);
        // (A * m + B * (256 - m)) >> 8 in 16-bit lanes (the products and their sum wrap)
        var wa = new Vector<ushort>((ushort)m);
        var wb = new Vector<ushort>((ushort)w);
        for (int i = start; i < vectors; i += Vector<byte>.Count)
        {
            Vector.Widen(new Vector<byte>(rowA[i..]), out var aLow, out var aHigh);
            Vector.Widen(new Vector<byte>(rowB[i..]), out var bLow, out var bHigh);
            var low = Vector.ShiftRightLogical(aLow * wa + bLow * wb, 8);
            var high = Vector.ShiftRightLogical(aHigh * wa + bHigh * wb, 8);
            Vector.Narrow(low, high).CopyTo(output[i..]);
        }
        for (int i = vectors; i < end; i++)
            output[i] = (byte)Math.Min(255, (rowA[i] * m + rowB[i] * w & 0xFFFF) >> 8);
        for (int i = end; i < n; i++)
            output[i] = (byte)(tableA[rowA[i]] + tableB[rowB[i]]);
    }

    /// <summary>FUN_004396B4: table[v] ~ v * weight / total, stepping a counter that starts at total / 2.</summary>
    private static void StepTable(byte[] table, int weight, int total)
    {
        if (weight == 0)
        {
            Array.Clear(table);
            return;
        }
        uint counter = (uint)total >> 1, value = 0;
        for (int i = 0; ; )
        {
            table[i] = (byte)value;
            if (++i == 256)
                return;
            if (counter >= (uint)weight)
                counter -= (uint)weight;
            else
            {
                counter = counter - (uint)weight + (uint)total;
                value++;
            }
        }
    }

    /// <summary>Surface 0 = surface <paramref name="source"/> at a brightness 0-255 (0 black, 255 a copy).</summary>
    private void ShowAtLevel(int source, int level)
    {
        int screen = SurfaceField(0, 2), from = SurfaceField(source, 2);
        int width = SurfaceField(0, 7), height = SurfaceField(0, 8), pitch = SurfaceField(0, 10);
        int size = pitch * height;
        if (level == 0)
            FillMemory(screen, size, 0);
        else if (level == 255)
            CopyMemory(screen, from, size);
        else
            BlendRows(screen, from, 0, level, 255 - level, width, height, pitch);
    }

    /// <summary>
    /// FUN_00437B40 with MMX: a rule (mask picture) transition, byte by byte over pixels * 3 / 4
    /// dwords. r = the rule's byte (inverted with bit 31 of t). Soft: k = clamp(r + t - 255, 0,
    /// 255), out = A k >> 8 without B, else min(65535, A k + B (256 - k)) >> 8. Hard (bit 30): r >
    /// 256 - t picks A, else B (or 0).
    /// </summary>
    public void RuleBlend(int dst, int a, int b, int rule, int pixels, int t)
    {
        int bytes = (int)((uint)(pixels * 3) >> 2) * 4;
        if (bytes <= 0)
            return;
        bool invert = t < 0, hard = (t & 0x40000000) != 0;
        int value = t & 0x3FFFFFFF;
        short threshold = (short)(0x100 - value);
        // Byte i of the output takes byte i of each input only: in parts of 64 KB on all cores,
        // where every input is the output itself or apart from it (else all is read first, as
        // the engine reads it)
        bool Apart(int from) => from == dst || (long)from + bytes <= dst || (long)dst + bytes <= from;
        void Part(int from, int count, byte[] ra, byte[] sa, byte[] sb, byte[] o)
        {
            ReadBytes(rule + from, ra.AsSpan(0, count));
            ReadBytes(a + from, sa.AsSpan(0, count));
            if (b != 0)
                ReadBytes(b + from, sb.AsSpan(0, count));
            for (int i = 0; i < count; i++)
            {
                int r = invert ? ra[i] ^ 0xFF : ra[i];
                if (hard)
                {
                    bool take = r > threshold;
                    o[i] = take ? sa[i] : b != 0 ? sb[i] : (byte)0;
                    continue;
                }
                int sum = r + value;
                int k = Math.Min(255, Math.Max(0, (sum & 0xFFFF) - 255));
                if (((uint)sum >> 16) != 0)
                    k = 255;
                o[i] = b == 0
                    ? (byte)Math.Min(255, sa[i] * k >> 8)
                    : (byte)Math.Min(255, Math.Min(0xFFFF, sa[i] * k + sb[i] * (256 - k)) >> 8);
            }
        }
        if (Apart(rule) && Apart(a) && (b == 0 || Apart(b)))
        {
            const int Chunk = 1 << 16;
            int parts = (bytes + Chunk - 1) / Chunk;
            // the pages exist before the parts are shared out
            for (int at = 0; at < bytes; at += Chunk / 2)
            {
                ReadByte(dst + at);
                ReadByte(rule + at);
                ReadByte(a + at);
                if (b != 0)
                    ReadByte(b + at);
            }
            ReadByte(dst + bytes - 1);
            Parallel.For(0, parts, () => (new byte[Chunk], new byte[Chunk], new byte[b != 0 ? Chunk : 0], new byte[Chunk]), (part, _, buffers) =>
            {
                int from = part * Chunk, count = Math.Min(Chunk, bytes - from);
                Part(from, count, buffers.Item1, buffers.Item2, buffers.Item3, buffers.Item4);
                WriteBytes(dst + from, buffers.Item4.AsSpan(0, count));
                return buffers;
            }, _ => { });
            return;
        }
        var all = new byte[bytes];
        var whole = (new byte[bytes], new byte[bytes], new byte[b != 0 ? bytes : 0]);
        Part(0, bytes, whole.Item1, whole.Item2, whole.Item3, all);
        WriteBytes(dst, all);
    }

    private void RegisterDraw()
    {
        // 0500 level: surface 0 (the one on screen) = surface 1 at a brightness 0-255
        // (FUN_00411F10); 256 or the same level again changes nothing
        Register(0x0500, (vm, c, i) =>
        {
            int level = vm.Value(c, i.Args[0]);
            vm.EngineGlobals[0x4880D8] = level;
            if (level == 0x100 || level == vm.m_screenLevel)
                return 0;
            vm.m_screenLevel = level;
            vm.ShowAtLevel(1, level);
            return 0;
        });
        // 04C6 n, show: surface 0 = surface n at the brightness of the last 0500 (FUN_004120F0,
        // always redone); show: InvalidateRect
        Register(0x04C6, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            if (vm.Trace != null)
                vm.TraceLine(c, $"04C6 surface {n}, show {vm.Value(c, i.Args[1])}");
            if (n != 0)
            {
                int level = vm.EngineGlobals.GetValueOrDefault(0x4880D8, 0x100);
                vm.m_screenLevel = -1;
                if (level is not (0x100 or -1))
                {
                    vm.m_screenLevel = level;
                    vm.ShowAtLevel(n, level);
                }
            }
            if (vm.Value(c, i.Args[1]) != 0)
            {
                vm.InvalidateWindow();
                vm.FrameShown = true;
            }
            return 0;
        });

        // 04F6 dst, x, y, A, ax, ay, B, bx, by, w, h, a, b (FUN_0041B4F0): dst = A * a / (a + b) +
        // B * b / (a + b) over a rectangle, clipped to dst. B with bit 31 is a grey level (its low
        // byte). All three use dst's pitch.
        Register(0x04F6, (vm, c, i) =>
        {
            var v = new int[13];
            for (int k = 0; k < 13; k++)
                v[k] = vm.Value(c, i.Args[k]);
            if (vm.Trace != null)
                vm.TraceLine(c, $"04F6 into surface {v[0]} at {v[1]},{v[2]} {v[9]}x{v[10]}: surface {v[3]} and {v[6]:X}, {v[11]} / {v[12]}");
            int dstSurface = v[0], srcA = v[3], srcB = v[6];
            int l = v[1], t = v[2], r = v[1] + v[9], btm = v[2] + v[10];
            int ax = v[4], ay = v[5], bx = v[7], by = v[8];
            int dw = vm.SurfaceField(dstSurface, 7), dh = vm.SurfaceField(dstSurface, 8);
            // FUN_00417790 with the two source rectangles following the destination
            if (dw < l || dh < t)
                return 0;
            if (l < 0)
            {
                if (r < 1)
                    return 0;
                ax -= l;
                bx -= l;
                l = 0;
            }
            if (t < 0)
            {
                if (btm < 1)
                    return 0;
                ay -= t;
                by -= t;
                t = 0;
            }
            r = Math.Min(r, dw);
            btm = Math.Min(btm, dh);
            int width = r - l, rows = btm - t;
            if (width == 0 || rows == 0)
                return 0;
            int pitch = vm.SurfaceField(dstSurface, 10);
            int bytesA = vm.SurfaceField(srcA, 9) >> 3;
            int dst = vm.SurfaceField(dstSurface, 2) + (vm.SurfaceField(dstSurface, 9) >> 3) * l + pitch * t;
            int a = vm.SurfaceField(srcA, 2) + vm.SurfaceField(srcA, 10) * ay + bytesA * ax;
            int offsetB = bytesA * bx + (srcB < 0 ? 0 : vm.SurfaceField(srcB & 0xFF, 10) * by);
            int b = (srcB < 0 ? srcB & 0xFF : vm.SurfaceField(srcB & 0xFF, 2)) + offsetB;
            vm.BlendRows(dst, a, b, v[11], v[12], width, rows, pitch);
            if (dstSurface == vm.DisplaySurface)
                vm.FrameShown = true;
            return 0;
        });
        // 0568 dst, A, B (-1: none), rule, t: a rule transition between surfaces (FUN_004182E0)
        Register(0x0568, (vm, c, i) =>
        {
            int dst = vm.Value(c, i.Args[0]), a = vm.Value(c, i.Args[1]), b = vm.Value(c, i.Args[2]);
            int rule = vm.Value(c, i.Args[3]);
            uint t = (uint)vm.Value(c, i.Args[4]);
            if ((t & 0x3FFFFFFF) > 0x200)
                t = (t & 0x3FFFF000) + 0x200;
            int pixels = vm.SurfaceField(dst, 8) * vm.SurfaceField(dst, 7);
            vm.RuleBlend(vm.SurfaceField(dst, 2), vm.SurfaceField(a, 2), b == -1 ? 0 : vm.SurfaceField(b, 2),
                vm.SurfaceField(rule, 2), pixels, (int)t);
            if (dst == vm.DisplaySurface)
            {
                vm.InvalidateWindow();
                vm.FrameShown = true;
            }
            return 0;
        });
        // 07D0 l, t, r, b: InvalidateRect - the next WM_PAINT shows that part of the display surface
        Register(0x07D0, (vm, c, i) =>
        {
            int l = vm.Value(c, i.Args[0]), t = vm.Value(c, i.Args[1]), r = vm.Value(c, i.Args[2]), b = vm.Value(c, i.Args[3]);
            if (vm.Trace != null)
                vm.TraceLine(c, $"07D0 {l},{t}-{r},{b}");
            vm.InvalidateWindow(l, t, r, b);
            vm.FrameShown = true;
            return 0;
        });
        // 002A ms: Sleep - the scripts wait for time to pass; the host frame ends (the host
        // paces frames itself)
        Register(0x002A, (vm, c, i) =>
        {
            vm.SleepRequested = Math.Max(vm.SleepRequested, vm.Value(c, i.Args[0]));
            vm.FrameShown = true;
            return 0;
        });
    }
}
