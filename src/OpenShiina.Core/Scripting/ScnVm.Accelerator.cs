// The GPU mode of the interpreter (IScnAccelerator): the routines that hand their pixel loops to
// it gather the scripts' memory straight into the kernel's buffers and write its output back.
//
// The GPU is not faster for every routine: on a desktop card the pictures go over PCIe both ways,
// which costs more than the work of a routine that only mixes a few pixels (enlarge16 and
// subpixel32 took 2-3 ms there against 1 ms on the CPU, scale32 2 ms against 6), while a phone's
// GPU shares the CPU's memory. So each routine chooses for itself: its first large calls
// alternate between the two (the very first on the GPU only warms it up), timed per pixel, and
// after five of each it keeps the faster (the GPU only when it is 10% faster). Both give the
// same bytes, so the choice cannot be seen. GpuChoices tells which way each went (perf.log).

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>The GPU (null: everything on the CPU). Set before the game starts.</summary>
    public IScnAccelerator? Accelerator { get; set; }

    /// <summary>Every call the GPU can take goes to it, faster or not (tests).</summary>
    public bool GpuAlways { get; set; }

    /// <summary>Each routine's choice once made ("scale32: GPU, 0.21 against 0.68 ms a 100,000 pixels"), for logs; the reader may clear it.</summary>
    public List<string> GpuChoices { get; } = [];

    private sealed class GpuChoice
    {
        public readonly List<double> Cpu = [], Gpu = [];
        public bool Warm, Decided, UseGpu;
    }

    private readonly Dictionary<string, GpuChoice> m_gpuChoices = [];
    private const int GpuSamples = 5, GpuSmallest = 16384;

    /// <summary>A call's way (GPU or CPU) and, while its routine is still timing both, when it started.</summary>
    private readonly record struct GpuTiming(bool Gpu, GpuChoice? Choice, long Pixels, long Started);

    /// <summary>Which way this call of <paramref name="routine"/> goes (<paramref name="pixels"/> the pixels it draws).</summary>
    private GpuTiming ChooseGpu(string routine, long pixels)
    {
        if (Accelerator == null)
            return default;
        if (GpuAlways)
            return new GpuTiming(true, null, pixels, 0);
        if (pixels < GpuSmallest)
            return default;
        if (!m_gpuChoices.TryGetValue(routine, out var choice))
            m_gpuChoices[routine] = choice = new GpuChoice();
        if (choice.Decided)
            return new GpuTiming(choice.UseGpu, null, pixels, 0);
        bool gpu = !choice.Warm || choice.Gpu.Count < choice.Cpu.Count;
        return new GpuTiming(gpu, choice, pixels, Stopwatch.GetTimestamp());
    }

    /// <summary>The call is done: while its routine is timing both ways, the time per pixel counts, and after enough of each it chooses.</summary>
    private void GpuDone(string routine, GpuTiming timing)
    {
        if (timing.Choice is not { } choice)
            return;
        double ms = (Stopwatch.GetTimestamp() - timing.Started) * 1000.0 / Stopwatch.Frequency;
        if (timing.Gpu && !choice.Warm)
        {
            choice.Warm = true;     // the first run makes the pipeline and the buffers
            return;
        }
        (timing.Gpu ? choice.Gpu : choice.Cpu).Add(ms * 100000 / timing.Pixels);
        if (choice.Gpu.Count < GpuSamples || choice.Cpu.Count < GpuSamples)
            return;
        static double Median(List<double> times) => times.Order().ElementAt(times.Count / 2);
        double gpu = Median(choice.Gpu), cpu = Median(choice.Cpu);
        choice.Decided = true;
        choice.UseGpu = gpu < cpu * 0.9;
        lock (GpuChoices)
            GpuChoices.Add($"{routine}: {(choice.UseGpu ? "GPU" : "CPU")}, {gpu:F2} ms on the GPU against {cpu:F2} on the CPU a 100,000 pixels");
    }

    /// <summary>The GPU failed this call: the routine stays on the CPU; the CPU's run is not timed.</summary>
    private GpuTiming GpuFailed(string routine)
    {
        if (!m_gpuChoices.TryGetValue(routine, out var choice))
            m_gpuChoices[routine] = choice = new GpuChoice();
        choice.Decided = true;
        choice.UseGpu = false;
        return default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScaleParameters
    {
        public int RowCount, ColumnCount, Width;
        public uint FullRow, FullColumn;
        public int Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Enlarge16Parameters
    {
        public int RowCount, ColumnCount, Width;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubpixelParameters
    {
        public int RowCount, OutPixels, InPixels, Middle;
        public uint AX, BX, TailX, SrcTailX, AY, BY, TailY, SrcTailY;
        public uint D0, D4, D8, DC;
        public int CopyAcross;
    }

    /// <summary>
    /// subpixel32's pixels on the GPU (Shaders/subpixel32.comp): the source rows from the first
    /// row's on, each destination row's kind and source row, the destination written back row by
    /// row. False leaves it to the CPU.
    /// </summary>
    private bool SubpixelOnGpu(IScnAccelerator gpu, List<(SubpixelRow Kind, int Source, int Target)> rows, int srcPitch,
                               int inPixels, int outPixels, SubpixelParameters parameters)
    {
        if (rows.Count == 0 || srcPitch <= 0 || gpu.Kernel("subpixel32") is not { } kernel)
            return false;
        int first = rows[0].Source, sourceRows = 0;
        foreach (var (kind, source, _) in rows)
        {
            if ((source - first) % srcPitch != 0)
                return false;
            sourceRows = Math.Max(sourceRows, (source - first) / srcPitch + ((int)kind % 2 == 1 ? 2 : 1));
        }
        int rowBytes = inPixels * 4;
        var sourceBytes = kernel.Input(0, (long)sourceRows * rowBytes);
        var rowTable = MemoryMarshal.Cast<byte, int>(kernel.Input(1, rows.Count * 8L));
        var output = kernel.Output(2, (long)rows.Count * outPixels * 4);
        if (sourceBytes.IsEmpty || rowTable.IsEmpty || output.IsEmpty)
            return false;
        for (int r = 0; r < sourceRows; r++)
            ReadBytes(first + r * srcPitch, sourceBytes.Slice(r * rowBytes, rowBytes));
        for (int r = 0; r < rows.Count; r++)
        {
            rowTable[r * 2] = (int)rows[r].Kind;
            rowTable[r * 2 + 1] = (rows[r].Source - first) / srcPitch;
        }
        parameters.RowCount = rows.Count;
        parameters.OutPixels = outPixels;
        parameters.InPixels = inPixels;
        if (!kernel.Run(parameters, (outPixels + 15) / 16, (rows.Count + 15) / 16))
            return false;
        for (int r = 0; r < rows.Count; r++)
            WriteBytes(rows[r].Target, output.Slice(r * outPixels * 4, outPixels * 4));
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RotateZoomParameters
    {
        public int Width, Height, SX, SY, SXEnd, SYEnd, SW, Keep;
        public uint Colour, DuxLow, DuxHigh, DvxLow, DvxHigh;
    }

    /// <summary>
    /// 0574's pixels on the GPU (Shaders/rotatezoom.comp): the source rectangle read before, u
    /// and v at the start of every destination row, the destination before when it keeps its
    /// colour outside the source. False leaves it to the CPU.
    /// </summary>
    private bool RotateZoomOnGpu(IScnAccelerator gpu, RotateZoomArea a, int[] source, int sw, int sh)
    {
        if (gpu.Kernel("rotatezoom") is not { } kernel)
            return false;
        int width = a.R - a.L, height = a.B - a.T, rowBytes = width * 4;
        var sourceBytes = kernel.Input(0, (long)sw * sh * 4);
        var rowTable = MemoryMarshal.Cast<byte, long>(kernel.Input(1, height * 16L));
        var before = kernel.Input(2, a.Keep ? (long)height * rowBytes : 4);
        var output = kernel.Output(3, (long)height * rowBytes);
        if (sourceBytes.IsEmpty || rowTable.IsEmpty || before.IsEmpty || output.IsEmpty)
            return false;
        MemoryMarshal.AsBytes(source.AsSpan()).CopyTo(sourceBytes);
        for (int r = 0; r < height; r++)
        {
            int yy = a.T + r;
            rowTable[r * 2] = unchecked(yy * a.DUY + a.U0);
            rowTable[r * 2 + 1] = unchecked(yy * a.DVY + a.V0);
            if (a.Keep)
                ReadBytes(a.DstBase + yy * a.DstStride + a.L * 4, before.Slice(r * rowBytes, rowBytes));
        }
        var parameters = new RotateZoomParameters
        {
            Width = width, Height = height, SX = a.SX, SY = a.SY, SXEnd = a.SXEnd, SYEnd = a.SYEnd, SW = sw, Keep = a.Keep ? 1 : 0,
            Colour = (uint)a.Colour, DuxLow = (uint)a.DUX, DuxHigh = (uint)(a.DUX >> 32), DvxLow = (uint)a.DVX, DvxHigh = (uint)(a.DVX >> 32),
        };
        if (!kernel.Run(parameters, (width + 15) / 16, (height + 15) / 16))
            return false;
        for (int r = 0; r < height; r++)
            WriteBytes(a.DstBase + (a.T + r) * a.DstStride + a.L * 4, output.Slice(r * rowBytes, rowBytes));
        return true;
    }

    /// <summary>
    /// The pixels of scale32 (mode 0) or scale16 (mode 1) on the GPU (Shaders/scale.comp): the
    /// source rows they read, the tables as five numbers an entry (first source row / column,
    /// flags, whole ones, the two partial weights), the destination written back row by row.
    /// False leaves it to the CPU.
    /// </summary>
    private bool ScaleOnGpu(IScnAccelerator gpu, int mode, IReadOnlyList<ScaleEntry> rows, int[] rowFrom, IReadOnlyList<ScaleEntry> columns,
                            int[] columnFrom, int width, int src, int srcPitch, int dst, int dstPitch, uint fullRow, uint fullColumn)
    {
        if (gpu.Kernel("scale") is not { } kernel)
            return false;
        int nr = rows.Count, nc = columns.Count;
        int sourceRows = 0;
        for (int r = 0; r < nr; r++)
        {
            var e = rows[r];
            sourceRows = Math.Max(sourceRows, rowFrom[r] + ((e.Flags & 0x80) != 0 ? 1 : 0) + e.Count + ((e.Flags & 0x40) != 0 ? 1 : 0));
        }
        int rowBytes = width * 4;
        var source = kernel.Input(0, (long)sourceRows * rowBytes);
        var rowTable = MemoryMarshal.Cast<byte, int>(kernel.Input(1, nr * 20L));
        var columnTable = MemoryMarshal.Cast<byte, int>(kernel.Input(2, nc * 20L));
        var output = kernel.Output(3, (long)nr * nc * 4);
        if (output.IsEmpty || source.IsEmpty || rowTable.IsEmpty || columnTable.IsEmpty)
            return false;
        for (int r = 0; r < sourceRows; r++)
            ReadBytes(src + r * srcPitch, source.Slice(r * rowBytes, rowBytes));
        for (int r = 0; r < nr; r++)
        {
            var e = rows[r];
            var entry = rowTable.Slice(r * 5, 5);
            (entry[0], entry[1], entry[2], entry[3], entry[4]) = (rowFrom[r], e.Flags, e.Count, (int)e.First, (int)e.Last);
        }
        for (int c = 0; c < nc; c++)
        {
            var e = columns[c];
            var entry = columnTable.Slice(c * 5, 5);
            (entry[0], entry[1], entry[2], entry[3], entry[4]) = (columnFrom[c], e.Flags & 0xC0, e.Count, (int)e.First, (int)e.Last);
        }
        var parameters = new ScaleParameters
        {
            RowCount = nr, ColumnCount = nc, Width = width, FullRow = fullRow, FullColumn = fullColumn, Mode = mode,
        };
        if (!kernel.Run(parameters, (nc + 15) / 16, (nr + 15) / 16))
            return false;
        for (int r = 0; r < nr; r++)
            WriteBytes(dst + r * dstPitch, output.Slice(r * nc * 4, nc * 4));
        return true;
    }

    /// <summary>
    /// enlarge16's pixels on the GPU (Shaders/enlarge16.comp): the source rows, per destination
    /// row its source row, whether it is mixed and the two row weights, per destination column its
    /// source pixel, whether it is mixed and the four words each side. False leaves it to the CPU.
    /// </summary>
    private bool Enlarge16OnGpu(IScnAccelerator gpu, Enlarge16Row[] rows, int[] from, int[] mixLeft, int[] left, int[] right,
                                int width, int src, int srcPitch, int dst, int dstPitch)
    {
        if (gpu.Kernel("enlarge16") is not { } kernel)
            return false;
        int nr = rows.Length, nc = from.Length;
        int sourceRows = 0;
        foreach (var row in rows)
            sourceRows = Math.Max(sourceRows, row.Source + (row.Mixed ? 2 : 1));
        int rowBytes = width * 4;
        var source = kernel.Input(0, (long)sourceRows * rowBytes);
        var rowTable = MemoryMarshal.Cast<byte, int>(kernel.Input(1, nr * 16L));
        var columnTable = MemoryMarshal.Cast<byte, int>(kernel.Input(2, nc * 40L));
        var output = kernel.Output(3, (long)nr * nc * 4);
        if (output.IsEmpty || source.IsEmpty || rowTable.IsEmpty || columnTable.IsEmpty)
            return false;
        for (int r = 0; r < sourceRows; r++)
            ReadBytes(src + r * srcPitch, source.Slice(r * rowBytes, rowBytes));
        for (int r = 0; r < nr; r++)
        {
            var row = rows[r];
            var entry = rowTable.Slice(r * 4, 4);
            (entry[0], entry[1], entry[2], entry[3]) = (row.Source, row.Mixed ? 1 : 0, row.Upper, row.Lower);
        }
        for (int c = 0; c < nc; c++)
        {
            var entry = columnTable.Slice(c * 10, 10);
            entry[0] = from[c];
            entry[1] = mixLeft[c] >= 0 ? 1 : 0;
            left.AsSpan(c * 4, 4).CopyTo(entry[2..]);
            right.AsSpan(c * 4, 4).CopyTo(entry[6..]);
        }
        if (!kernel.Run(new Enlarge16Parameters { RowCount = nr, ColumnCount = nc, Width = width }, (nc + 15) / 16, (nr + 15) / 16))
            return false;
        for (int r = 0; r < nr; r++)
            WriteBytes(dst + r * dstPitch, output.Slice(r * nc * 4, nc * 4));
        return true;
    }
}
