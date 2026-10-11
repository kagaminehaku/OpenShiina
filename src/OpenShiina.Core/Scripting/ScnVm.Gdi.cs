// Calls into Windows DLLs made by the scripts (op_01A4, FUN_0041DB50 -> FUN_0041DA80:
// LoadLibraryA + GetProcAddress, the arguments up to an FF byte pushed as dwords). The functions
// the eleven games call are done here: the GDI ones draw into the engine's surfaces through
// their device contexts (0x00030000 + surface, op_0529), with brushes and pens kept here and the
// shapes drawn by the platform (IScnShapes: GDI itself on Windows, else Platform/DibShapes.cs). Also the font list of op_006F.

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
    // Without the platform's GDI: GDI's shapes as Wine's DIB driver draws them
    private static readonly Platform.DibShapes s_dibShapes = new();
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
            case ("kernel32", "CreateDirectoryA"):
                // Ao no Juuai's save folder ("save" in the game's folder): the host keeps its
                // saves in a folder of its own and never writes into the game's
                Trace?.Add($"f{m_frameNumber} CreateDirectoryA \"{ReadString(Arg(0))}\"");
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
            case ("user32", "SetMenuItemInfoW"):
                // The window has no menu of the system's here (Bitch Gakuen's START renames
                // the items of its window menu): done
                return 1;
            case ("user32", "GetMessageExtraInfo"):
                // The message came from a mouse, not from a pen or a finger (Windows marks those
                // 0xFF5157xx; Bitch Nee-chan's hook reads it for RIO.INI's TabletShortcut)
                return 0;
            case ("kernel32", "LoadLibraryA"):
            {
                // No library loads; one in the game's folder seems to (a handle), so that its
                // functions below answer: Bitch Nee-chan's HID_ONAHOLE.dll (a USB device sold
                // with the game; START says the DLL is missing when it does not load)
                string name = ReadString(Arg(0));
                Trace?.Add($"f{m_frameNumber} LoadLibraryA \"{name}\"");
                return m_host.LooseFileSize(name) != null ? 0x10000000 : 0;
            }
            // HID_ONAHOLE.dll as it is without its device: version 1.0, onahoInit finds none
            case ("hid_onahole", "onahoDLLVer"):
                return 0x10000;
            case ("hid_onahole", "onahoInit" or "onahoEnd" or "onahoSetLevel" or "onahoSetPattern"):
                return 0;
            case ("kernel32", "GetVersionExA"):
            {
                // OSVERSIONINFOA as Windows 8 and later give a program without a manifest: 6.2,
                // build 9200, NT (Bitch Nee-chan's START reads the major and minor version)
                int p = Arg(0);
                int size = Read32(p);
                for (int k = 4; k < Math.Min(size, 156); k++)
                    WriteByte(p + k, 0);
                Write32(p + 4, 6);
                Write32(p + 8, 2);
                Write32(p + 12, 9200);
                Write32(p + 16, 2);
                return 1;
            }
            case ("kernel32", "MultiByteToWideChar"):
            {
                // Aneiro's START turns its UTF-8 .wav.sli files into Shift-JIS through UTF-16:
                // code page, flags, string, bytes (-1: up to its 0, which counts), out, out size
                int length = Arg(3);
                if (length < 0)
                    for (length = 0; ReadByte(Arg(2) + length) != 0; length++) { }
                string text = CodePage(Arg(0)).GetString(ReadBytes(Arg(2), length)) + (Arg(3) < 0 ? "\0" : "");
                if (Arg(5) == 0)
                    return text.Length;
                if (Arg(5) < text.Length)
                    return 0;
                for (int k = 0; k < text.Length; k++)
                    Write16(Arg(4) + 2 * k, text[k]);
                return text.Length;
            }
            case ("kernel32", "WideCharToMultiByte"):
            {
                // code page, flags, string, characters (-1: up to its 0), out, out size, default
                // character, whether it was used
                int length = Arg(3);
                if (length < 0)
                    for (length = 0; Read16(Arg(2) + 2 * length) != 0; length++) { }
                var chars = new char[length];
                for (int k = 0; k < length; k++)
                    chars[k] = (char)Read16(Arg(2) + 2 * k);
                byte[] bytes = CodePage(Arg(0)).GetBytes(chars);
                if (Arg(3) < 0)
                    bytes = [.. bytes, 0];
                if (Arg(5) == 0)
                    return bytes.Length;
                if (Arg(5) < bytes.Length)
                    return 0;
                WriteBytes(Arg(4), bytes);
                if (Arg(7) != 0)
                    Write32(Arg(7), 0);
                return bytes.Length;
            }
            default:
                return null;
        }
    }

    /// <summary>A Windows code page: the system's ones (0 ANSI, 1 OEM, 3 the thread's) are Japanese, 932.</summary>
    private static System.Text.Encoding CodePage(int codePage) => codePage switch
    {
        0 or 1 or 3 or 932 => Encodings.cp932,
        65001 => System.Text.Encoding.UTF8,
        _ => System.Text.Encoding.GetEncoding(codePage),
    };

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
        (m_host.Shapes ?? s_dibShapes).Ellipse(region, x1 - x0, y1 - y0, stride, bytes, l - x0, t - y0, r - x0, b - y0, fill, outline);
        for (int y = y0; y < y1; y++)
            WriteBytes(pixels + y * pitch + x0 * bytes, region.AsSpan((y - y0) * stride, stride));
        Shown(surface);
        return true;
    }
}
