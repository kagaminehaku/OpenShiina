// IScnShapes with Windows GDI: the part of the surface is copied into a DIB section of its size
// and the shape drawn there by GDI itself, so it comes out exactly as the game draws it.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenShiina.Platform;

[SupportedOSPlatform("windows")]
public sealed class GdiShapes : IScnShapes
{
    public void Ellipse(Span<byte> pixels, int width, int height, int stride, int bytesPerPixel, int l, int t, int r, int b, int? brush, ScnPen? pen)
    {
        if (width <= 0 || height <= 0 || bytesPerPixel is not (3 or 4))
            return;
        var info = new byte[40];
        BitConverter.TryWriteBytes(info.AsSpan(0), 40);
        BitConverter.TryWriteBytes(info.AsSpan(4), width);
        BitConverter.TryWriteBytes(info.AsSpan(8), -height);
        BitConverter.TryWriteBytes(info.AsSpan(12), (short)1);
        BitConverter.TryWriteBytes(info.AsSpan(14), (short)(bytesPerPixel * 8));
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr bitmap = CreateDIBSection(dc, info, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(dc);
            return;
        }
        IntPtr oldBitmap = SelectObject(dc, bitmap);
        int dibStride = (width * bytesPerPixel + 3) & ~3;
        var row = new byte[dibStride];
        for (int y = 0; y < height; y++)
        {
            pixels.Slice(y * stride, width * bytesPerPixel).CopyTo(row);
            Marshal.Copy(row, 0, bits + y * dibStride, dibStride);
        }
        IntPtr brushHandle = brush is { } colour ? CreateSolidBrush(colour) : GetStockObject(NullBrush);
        IntPtr penHandle = pen is { } p ? CreatePen(p.Style, p.Width, p.Color) : GetStockObject(NullPen);
        IntPtr oldBrush = SelectObject(dc, brushHandle), oldPen = SelectObject(dc, penHandle);
        Ellipse(dc, l, t, r, b);
        GdiFlush();
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(bits + y * dibStride, row, 0, dibStride);
            row.AsSpan(0, width * bytesPerPixel).CopyTo(pixels.Slice(y * stride));
        }
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBitmap);
        if (brush != null)
            DeleteObject(brushHandle);
        if (pen != null)
            DeleteObject(penHandle);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }

    private const int NullBrush = 5, NullPen = 8;

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, byte[] info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, int color);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr dc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
