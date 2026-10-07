// The GPU mode of the interpreter (IScnAccelerator): the routines that hand their pixel loops to
// it gather the scripts' memory straight into the kernel's buffers and write its output back.

using System.Runtime.InteropServices;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>The GPU (null: everything on the CPU). Set before the game starts.</summary>
    public IScnAccelerator? Accelerator { get; set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Scale32Parameters
    {
        public int RowCount, ColumnCount, Width;
        public uint FullRow, FullColumn;
    }

    /// <summary>
    /// scale32's pixels on the GPU (Shaders/scale32.comp): the source rows it reads, the tables as
    /// five numbers an entry (first source row / column, flags, whole ones, the two partial
    /// weights), the destination written back row by row. False leaves it to the CPU.
    /// </summary>
    private bool Scale32OnGpu(IScnAccelerator gpu, List<ScaleEntry> rows, int[] rowY, List<ScaleEntry> columns, int[] columnFrom,
                              int width, int src, int srcPitch, int dst, int dstPitch, uint fullRow, uint fullColumn)
    {
        if (gpu.Kernel("scale32") is not { } kernel)
            return false;
        int nr = rows.Count, nc = columns.Count;
        int sourceRows = rowY[nr] + 1, rowBytes = width * 4;
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
            (entry[0], entry[1], entry[2], entry[3], entry[4]) = (rowY[r], e.Flags, e.Count, (int)e.First, (int)e.Last);
        }
        for (int c = 0; c < nc; c++)
        {
            var e = columns[c];
            var entry = columnTable.Slice(c * 5, 5);
            (entry[0], entry[1], entry[2], entry[3], entry[4]) = (columnFrom[c], e.Flags & 0xC0, e.Count, (int)e.First, (int)e.Last);
        }
        var parameters = new Scale32Parameters { RowCount = nr, ColumnCount = nc, Width = width, FullRow = fullRow, FullColumn = fullColumn };
        if (!kernel.Run(parameters, (nc + 15) / 16, (nr + 15) / 16))
            return false;
        for (int r = 0; r < nr; r++)
            WriteBytes(dst + r * dstPitch, output.Slice(r * nc * 4, nc * 4));
        return true;
    }
}
