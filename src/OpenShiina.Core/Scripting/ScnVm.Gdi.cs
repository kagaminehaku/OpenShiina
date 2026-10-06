// Calls into Windows DLLs made by the scripts (op_01A4, FUN_0041DB50 -> FUN_0041DA80:
// LoadLibraryA + GetProcAddress, the arguments up to an FF byte pushed as dwords). The functions
// the eleven games call are done here: the GDI ones draw into the engine's surfaces through
// their device contexts (0x00030000 + surface, op_0529), with brushes and pens kept here and the
// shapes drawn by the platform (IScnShapes: GDI itself on Windows). Also the font list of op_006F.

namespace OpenShiina.Scripting;

/// <summary>A solid pen: style (PS_SOLID 0 ... PS_NULL 5), width and colour (0x00BBGGRR).</summary>
public readonly record struct ScnPen(int Style, int Width, int Color);

/// <summary>Shapes drawn the way GDI draws them.</summary>
public interface IScnShapes
{
    /// <summary>
    /// Ellipse(l, t, r, b) on a top-down picture of 3 or 4 bytes a pixel (32-bit pixels get 0 in
    /// their fourth byte), filled with a solid brush (0x00BBGGRR) unless null and outlined with
    /// the pen unless null.
    /// </summary>
    void Ellipse(Span<byte> pixels, int width, int height, int stride, int bytesPerPixel, int l, int t, int r, int b, int? brush, ScnPen? pen);
}

public sealed partial class ScnVm
{
    // GDI objects made by the scripts: brushes (colour) and pens, by handle
    private readonly record struct GdiObject(bool Pen, int Color, int Style, int Width);
    private readonly Dictionary<int, GdiObject> m_gdiObjects = new();
    // What each device context has selected (a new one: the white brush and the black pen)
    private readonly Dictionary<int, (int Brush, int Pen)> m_gdiSelected = new();
    private int m_nextGdiObject = 0x00060001;
    private const int WhiteBrush = 0x00050000, BlackPen = 0x00050001, NullBrush = 0x00050002, NullPen = 0x00050003;

    private void RegisterGdi()
    {
        m_gdiObjects[WhiteBrush] = new(false, 0xFFFFFF, 0, 0);
        m_gdiObjects[BlackPen] = new(true, 0, 0, 1);
        m_gdiObjects[NullBrush] = new(false, 0, 1, 0);
        m_gdiObjects[NullPen] = new(true, 0, 5, 1);

        // 01A4 -, dll, function, arguments..., v: v = dll!function(arguments)
        Register(0x01A4, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            string dll = vm.ReadString(vm.Value(c, i.Args[1])), function = vm.ReadString(vm.Value(c, i.Args[2]));
            var a = new int[i.Args.Length - 4];
            for (int k = 0; k < a.Length; k++)
                a[k] = vm.Value(c, i.Args[3 + k]);
            int? result = vm.DllCall(dll.ToLowerInvariant().Replace(".dll", ""), function, a);
            if (result == null)
                throw vm.Error(c, $"The scripts call {dll}!{function}, which is not supported yet");
            vm.Store(c, i.Args[^1], result.Value);
            return 0;
        });
        // 006F -, v: v = a block of 32-byte face names, an empty one last (FUN_00415630)
        Register(0x006F, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            var faces = vm.m_host.Fonts?.FixedPitchJapaneseFaces() ?? [];
            int block = vm.Allocate((faces.Count + 1) * 32);
            for (int k = 0; k < faces.Count; k++)
                vm.WriteBytes(block + 32 * k, faces[k].AsSpan(0, Math.Min(faces[k].Length, 31)));
            vm.Store(c, i.Args[1], block);
            return 0;
        });
    }

    private int? DllCall(string dll, string function, int[] a)
    {
        int Arg(int k) => k < a.Length ? a[k] : 0;
        switch (dll, function)
        {
            case ("gdi32", "CreateSolidBrush"):
                return NewGdiObject(new(false, Arg(0) & 0xFFFFFF, 0, 0));
            case ("gdi32", "CreatePen"):
                return NewGdiObject(new(true, Arg(2) & 0xFFFFFF, Arg(0), Arg(1)));
            case ("gdi32", "SelectObject"):
            {
                int dc = Arg(0), handle = Arg(1);
                if (!m_gdiObjects.TryGetValue(handle, out var o))
                    return 0;
                var (brush, pen) = m_gdiSelected.GetValueOrDefault(dc, (WhiteBrush, BlackPen));
                m_gdiSelected[dc] = o.Pen ? (brush, handle) : (handle, pen);
                return o.Pen ? pen : brush;
            }
            case ("gdi32", "DeleteObject"):
                return Arg(0) >= 0x00060000 && m_gdiObjects.Remove(Arg(0)) ? 1 : 0;
            case ("gdi32", "Ellipse"):
                return GdiEllipse(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4)) ? 1 : 0;
            case ("user32", "GetForegroundWindow"):
                return m_host.Active ? WindowHandle : 0;
            case ("user32", "AdjustWindowRect"):
                // The host makes the window around the picture; the rectangle stays as it is
                return 1;
            case ("kernel32", "GlobalMemoryStatusEx"):
            {
                // MEMORYSTATUSEX of a PC with 8 GB, as a 32-bit program sees it
                int p = Arg(0);
                Write32(p + 4, 25);
                Write64(p + 8, 8L << 30);
                Write64(p + 16, 6L << 30);
                Write64(p + 24, 16L << 30);
                Write64(p + 32, 12L << 30);
                Write64(p + 40, 0x7FFE0000);
                Write64(p + 48, 0x60000000);
                Write64(p + 56, 0);
                return 1;
            }
            default:
                return null;
        }
    }

    private void Write64(int address, long value)
    {
        Write32(address, (int)value);
        Write32(address + 4, (int)(value >> 32));
    }

    private int NewGdiObject(GdiObject o)
    {
        int handle = m_nextGdiObject++;
        m_gdiObjects[handle] = o;
        return handle;
    }

    /// <summary>Ellipse on the surface behind a device context, with its brush and pen.</summary>
    private bool GdiEllipse(int dc, int l, int t, int r, int b)
    {
        int surface = dc - 0x00030000;
        if (surface is < 0 or >= SurfaceCount || SurfaceField(surface, 1) != dc || SurfaceField(surface, 2) == 0)
            return false;
        var (brushHandle, penHandle) = m_gdiSelected.GetValueOrDefault(dc, (WhiteBrush, BlackPen));
        var brush = m_gdiObjects.GetValueOrDefault(brushHandle, m_gdiObjects[WhiteBrush]);
        var pen = m_gdiObjects.GetValueOrDefault(penHandle, m_gdiObjects[BlackPen]);
        int? fill = brush.Style == 1 ? null : brush.Color;
        ScnPen? outline = pen.Style == 5 ? null : new ScnPen(pen.Style, pen.Width, pen.Color);

        // Only the part of the surface the shape can reach goes to the platform
        int w = SurfaceField(surface, 7), h = SurfaceField(surface, 8);
        int pitch = SurfaceField(surface, 10), bytes = SurfaceField(surface, 9) >> 3, pixels = SurfaceField(surface, 2);
        if (bytes is not (3 or 4))
            return false;
        int margin = (outline?.Width ?? 0) + 1;
        int x0 = Math.Max(0, Math.Min(l, r) - margin), y0 = Math.Max(0, Math.Min(t, b) - margin);
        int x1 = Math.Min(w, Math.Max(l, r) + margin), y1 = Math.Min(h, Math.Max(t, b) + margin);
        if (x1 <= x0 || y1 <= y0)
            return true;
        int stride = (x1 - x0) * bytes;
        var region = new byte[stride * (y1 - y0)];
        for (int y = y0; y < y1; y++)
            ReadBytes(pixels + y * pitch + x0 * bytes, region.AsSpan((y - y0) * stride, stride));
        if (m_host.Shapes is { } shapes)
            shapes.Ellipse(region, x1 - x0, y1 - y0, stride, bytes, l - x0, t - y0, r - x0, b - y0, fill, outline);
        else
            ApproximateEllipse(region, x1 - x0, y1 - y0, stride, bytes, l - x0, t - y0, r - x0, b - y0, fill, outline);
        for (int y = y0; y < y1; y++)
            WriteBytes(pixels + y * pitch + x0 * bytes, region.AsSpan((y - y0) * stride, stride));
        Shown(surface);
        return true;
    }

    /// <summary>
    /// Without the platform's GDI: the ellipse inside (l, t) - (r - 1, b - 1), the pen on its
    /// edge pixels. GDI's own shapes differ on some edge pixels.
    /// </summary>
    private static void ApproximateEllipse(Span<byte> pixels, int width, int height, int stride, int bytes,
        int l, int t, int r, int b, int? brush, ScnPen? pen)
    {
        if (r < l)
            (l, r) = (r, l);
        if (b < t)
            (t, b) = (b, t);
        double cx = (l + r - 1) / 2.0, cy = (t + b - 1) / 2.0, ax = (r - l) / 2.0, ay = (b - t) / 2.0;
        if (ax <= 0 || ay <= 0)
            return;
        bool Inside(int x, int y)
        {
            double dx = (x - cx) / ax, dy = (y - cy) / ay;
            return dx * dx + dy * dy <= 1;
        }
        for (int y = Math.Max(t, 0); y < Math.Min(b, height); y++)
            for (int x = Math.Max(l, 0); x < Math.Min(r, width); x++)
            {
                if (!Inside(x, y))
                    continue;
                bool edge = !Inside(x - 1, y) || !Inside(x + 1, y) || !Inside(x, y - 1) || !Inside(x, y + 1);
                int? colour = edge && pen != null ? pen.Value.Color : brush;
                if (colour is not { } c)
                    continue;
                int at = y * stride + x * bytes;
                pixels[at] = (byte)(c >> 16);
                pixels[at + 1] = (byte)(c >> 8);
                pixels[at + 2] = (byte)c;
                if (bytes == 4)
                    pixels[at + 3] = 0;
            }
    }
}
