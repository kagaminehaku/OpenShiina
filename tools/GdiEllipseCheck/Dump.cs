// GdiEllipseCheck dump: what GDI draws, in one gzipped text file (gdi-ellipse-dump.txt.gz), to
// work out its rules away from Windows. Masks are the pixels GDI changed on a white DIB section,
// relative to the shape's box (l, t). "rows" masks list, from their first row, the first and last
// pixel of each row as changes from the row before ("-" an empty row, "* runs..." a row of more
// than one run); "runs" masks list each row's runs in full.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using OpenShiina.Platform;
using OpenShiina.Scripting;

[SupportedOSPlatform("windows")]
static unsafe class Dump
{
    private const uint White = 0xFFFFFF, Grey = 0x808080, BrushColour = 0x0000C0, PenColour = 0x00C000;

    public static void Run(string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var o = new StreamWriter(gzip, new UTF8Encoding(false)) { NewLine = "\n" };
        o.WriteLine("# OpenShiina GDI ellipse dump 1");
        o.WriteLine($"# {Environment.OSVersion} {RuntimeInformation.OSDescription}");
        var started = DateTime.Now;
        void Progress(string what) => Console.WriteLine($"{(DateTime.Now - started).TotalSeconds,6:F0} s  {what}");

        // 1. EFCLIB's case, circles: brush and 1-pixel pen of one grey
        Progress("circles");
        for (int d = 1; d <= 2200; d += d <= 601 ? 1 : 2)
            using (var dib = new Dib(d + 4, d + 4))
            {
                if (d % 200 == 0)
                    Progress($"circles to {d}");
                dib.Ellipse(2, 2, 2 + d, 2 + d, Grey, Grey);
                o.WriteLine($"circle {d}");
                dib.WriteRows(o, 2, 2);
            }

        // 2. Ellipses to 160 x 160 where GDI and DibShapes differ (same grey)
        Progress("ellipses to 160 x 160");
        var ours = new DibShapes();
        int differ = 0;
        for (int w = 1; w <= 160; w++)
            for (int h = 1; h <= 160; h++)
                using (var dib = new Dib(w + 4, h + 4))
                {
                    dib.Ellipse(2, 2, 2 + w, 2 + h, Grey, Grey);
                    var mine = new byte[(w + 4) * (h + 4) * 4];
                    mine.AsSpan().Fill(0xFF);
                    ours.Ellipse(mine, w + 4, h + 4, (w + 4) * 4, 4, 2, 2, 2 + w, 2 + h, (int)Grey, new ScnPen(0, 1, (int)Grey));
                    var theirs = dib.Pixels.ToArray().Select(p => p & White);
                    if (!theirs.SequenceEqual(MemoryMarshal.Cast<byte, uint>(mine).ToArray().Select(p => p & White)))
                    {
                        differ++;
                        o.WriteLine($"ellipse {w} {h}");
                        dib.WriteRows(o, 2, 2);
                    }
                }
        o.WriteLine($"# ellipses to 160 x 160 differing from DibShapes: {differ}");

        // 3. The parts, for some sizes: brush alone, pen alone, both in two colours, the region,
        //    the path (as Béziers, flattened, and flattened in 1/16 pixels), and the path stroked
        //    and filled by StrokeAndFillPath
        Progress("parts");
        var sizes = new List<(int W, int H)>();
        for (int d = 90; d <= 140; d++)
            sizes.Add((d, d));
        foreach (int d in new[] { 160, 199, 200, 256, 301, 400, 401, 555, 800, 1001, 1200 })
            sizes.Add((d, d));
        var random = new Random(7);
        for (int k = 0; k < 120; k++)
            sizes.Add((random.Next(3, 700), random.Next(3, 700)));
        foreach (var (w, h) in sizes)
        {
            o.WriteLine($"parts {w} {h}");
            using var dib = new Dib(w + 8, h + 8);
            int l = 4, t = 4, r = 4 + w, b = 4 + h;
            dib.Ellipse(l, t, r, b, BrushColour, null);
            o.WriteLine("brush");
            dib.WriteRuns(o, l, t);
            dib.Clear();
            dib.Ellipse(l, t, r, b, null, PenColour);
            o.WriteLine("pen");
            dib.WriteRuns(o, l, t);
            dib.Clear();
            dib.Ellipse(l, t, r, b, BrushColour, PenColour);
            o.WriteLine("both pen");
            dib.WriteRuns(o, l, t, PenColour);
            o.WriteLine("both brush");
            dib.WriteRuns(o, l, t, BrushColour);
            dib.Clear();
            dib.Region(l, t, r, b);
            o.WriteLine("region");
            dib.WriteRuns(o, l, t);
            o.WriteLine("path " + dib.Path(l, t, r, b, flatten: false, sixteenths: false));
            o.WriteLine("flat " + dib.Path(l, t, r, b, flatten: true, sixteenths: false));
            o.WriteLine("flat16 " + dib.Path(l, t, r, b, flatten: true, sixteenths: true));
            o.WriteLine("path16 " + dib.Path(l, t, r, b, flatten: false, sixteenths: true));
            dib.Clear();
            dib.Ellipse(l, t, r, b, Grey, Grey);
            var drawn = dib.Pixels.ToArray();
            dib.Clear();
            dib.StrokeAndFillPath(l, t, r, b, Grey, Grey);
            o.WriteLine($"strokeandfillpath differs {drawn.Zip(dib.Pixels.ToArray()).Count(p => p.First != p.Second)}");
        }

        // 4. Polygons and polylines with corners in 1/16 pixels (GM_ADVANCED, world transform 1/16)
        Progress("polygons and lines");
        for (int k = 0; k < 400; k++)
        {
            int n = random.Next(2) == 0 ? 2 : random.Next(3, 7);
            var points = Enumerable.Range(0, n).Select(_ => (X: random.Next(16 * 2, 16 * 38), Y: random.Next(16 * 2, 16 * 38))).ToArray();
            using var dib = new Dib(40, 40);
            string corners = string.Join(" ", points.Select(p => $"{p.X},{p.Y}"));
            if (n > 2)
            {
                dib.Polygon(points, BrushColour, null);
                o.WriteLine($"polygon {corners}");
                dib.WriteRuns(o, 0, 0);
                dib.Clear();
            }
            dib.Polyline(points, PenColour);
            o.WriteLine($"polyline {corners}");
            dib.WriteRuns(o, 0, 0);
        }

        // 5. Does a circle come out the same moved, and cut by the picture's edges?
        Progress("moved and cut circles");
        for (int k = 0; k < 300; k++)
        {
            int d = random.Next(2) == 0 ? random.Next(1, 200) : random.Next(200, 1400);
            uint[] reference;
            using (var dib = new Dib(d + 4, d + 4))
            {
                dib.Ellipse(2, 2, 2 + d, 2 + d, Grey, Grey);
                reference = dib.Pixels.ToArray();
            }
            int width = random.Next(8, 900), height = random.Next(8, 700);
            int l = random.Next(-d, width), t = random.Next(-d, height);
            using var moved = new Dib(width, height);
            moved.Ellipse(l, t, l + d, t + d, Grey, Grey);
            int bad = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int rx = x - l + 2, ry = y - t + 2;
                    uint want = rx >= 0 && ry >= 0 && rx < d + 4 && ry < d + 4 ? reference[ry * (d + 4) + rx] : White;
                    if (moved.Pixel(x, y) != want)
                        bad++;
                }
            o.WriteLine($"moved {d} at {l} {t} on {width}x{height}: {bad} pixels differ");
        }
        Progress("done");
    }

    private sealed class Dib : IDisposable
    {
        public readonly int Width, Height;
        private readonly IntPtr m_dc, m_bitmap, m_old;
        private readonly uint* m_bits;

        public Dib(int width, int height)
        {
            Width = width;
            Height = height;
            var info = new byte[40];
            BitConverter.TryWriteBytes(info.AsSpan(0), 40);
            BitConverter.TryWriteBytes(info.AsSpan(4), width);
            BitConverter.TryWriteBytes(info.AsSpan(8), -height);
            BitConverter.TryWriteBytes(info.AsSpan(12), (short)1);
            BitConverter.TryWriteBytes(info.AsSpan(14), (short)32);
            m_dc = CreateCompatibleDC(IntPtr.Zero);
            m_bitmap = CreateDIBSection(m_dc, info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (m_bitmap == IntPtr.Zero)
                throw new OutOfMemoryException($"No {width}x{height} DIB section");
            m_bits = (uint*)bits;
            m_old = SelectObject(m_dc, m_bitmap);
            Clear();
        }

        public Span<uint> Pixels => new(m_bits, Width * Height);

        public uint Pixel(int x, int y) => m_bits[y * Width + x] & White;

        /// <summary>A COLORREF (0x00BBGGRR) as the DIB section's pixel (0x00RRGGBB).</summary>
        private static uint Rgb(uint colour) => (colour & 0xFF) << 16 | colour & 0xFF00 | colour >> 16 & 0xFF;

        public void Clear()
        {
            GdiFlush();
            Pixels.Fill(White);
        }

        private void With(uint? brush, uint? pen, Action draw, int penWidth = 1)
        {
            IntPtr b = brush is { } bc ? CreateSolidBrush(bc) : GetStockObject(NullBrush);
            IntPtr p = pen is { } pc ? CreatePen(0, penWidth, pc) : GetStockObject(NullPen);
            IntPtr ob = SelectObject(m_dc, b), op = SelectObject(m_dc, p);
            draw();
            GdiFlush();
            SelectObject(m_dc, ob);
            SelectObject(m_dc, op);
            if (brush != null)
                DeleteObject(b);
            if (pen != null)
                DeleteObject(p);
        }

        public void Ellipse(int l, int t, int r, int b, uint? brush, uint? pen) =>
            With(brush, pen, () => GdiEllipse(m_dc, l, t, r, b));

        public void StrokeAndFillPath(int l, int t, int r, int b, uint brush, uint pen) =>
            With(brush, pen, () =>
            {
                BeginPath(m_dc);
                GdiEllipse(m_dc, l, t, r, b);
                EndPath(m_dc);
                GdiStrokeAndFillPath(m_dc);
            });

        public void Region(int l, int t, int r, int b)
        {
            IntPtr region = CreateEllipticRgn(l, t, r, b);
            IntPtr brush = CreateSolidBrush(BrushColour);
            FillRgn(m_dc, region, brush);
            GdiFlush();
            DeleteObject(brush);
            DeleteObject(region);
        }

        /// <summary>The ellipse's path: points and types; in 1/16 pixels through a world transform.</summary>
        public string Path(int l, int t, int r, int b, bool flatten, bool sixteenths)
        {
            if (sixteenths)
            {
                SetGraphicsMode(m_dc, GM_ADVANCED);
                var form = new XForm { M11 = 1 / 16f, M22 = 1 / 16f };
                SetWorldTransform(m_dc, ref form);
                l *= 16;
                t *= 16;
                r *= 16;
                b *= 16;
            }
            BeginPath(m_dc);
            GdiEllipse(m_dc, l, t, r, b);
            EndPath(m_dc);
            if (flatten)
                FlattenPath(m_dc);
            int n = GetPath(m_dc, null, null, 0);
            var points = new int[Math.Max(n, 0) * 2];
            var types = new byte[Math.Max(n, 0)];
            if (n > 0)
                GetPath(m_dc, points, types, n);
            AbortPath(m_dc);
            if (sixteenths)
            {
                var form = new XForm { M11 = 1, M22 = 1 };
                SetWorldTransform(m_dc, ref form);
                SetGraphicsMode(m_dc, GM_COMPATIBLE);
            }
            var text = new StringBuilder($"{n}");
            for (int i = 0; i < n; i++)
                text.Append($" {points[2 * i]},{points[2 * i + 1]},{types[i]}");
            return text.ToString();
        }

        /// <summary>Polygon or polyline with corners in 1/16 pixels.</summary>
        public void Polygon((int X, int Y)[] points, uint? brush, uint? pen) => Sixteenths(points, brush, pen, close: true);

        public void Polyline((int X, int Y)[] points, uint pen) => Sixteenths(points, null, pen, close: false);

        private void Sixteenths((int X, int Y)[] points, uint? brush, uint? pen, bool close)
        {
            SetGraphicsMode(m_dc, GM_ADVANCED);
            var form = new XForm { M11 = 1 / 16f, M22 = 1 / 16f };
            SetWorldTransform(m_dc, ref form);
            var flat = points.SelectMany(p => new[] { p.X, p.Y }).ToArray();
            With(brush, pen, () =>
            {
                if (close)
                    GdiPolygon(m_dc, flat, points.Length);
                else
                    GdiPolyline(m_dc, flat, points.Length);
            }, penWidth: 0);
            form = new XForm { M11 = 1, M22 = 1 };
            SetWorldTransform(m_dc, ref form);
            SetGraphicsMode(m_dc, GM_COMPATIBLE);
        }

        private List<(int A, int B)> Runs(int y, Func<uint, bool> take)
        {
            var runs = new List<(int, int)>();
            for (int x = 0; x < Width; x++)
            {
                if (!take(Pixel(x, y)))
                    continue;
                int start = x;
                while (x + 1 < Width && take(Pixel(x + 1, y)))
                    x++;
                runs.Add((start, x));
            }
            return runs;
        }

        /// <summary>Each row's first and last changed pixel, as changes from the row before.</summary>
        public void WriteRows(TextWriter o, int l, int t)
        {
            var rows = Enumerable.Range(0, Height).Select(y => Runs(y, p => p != White)).ToArray();
            int first = Array.FindIndex(rows, r => r.Count > 0), last = Array.FindLastIndex(rows, r => r.Count > 0);
            if (first < 0)
            {
                o.WriteLine("empty");
                return;
            }
            o.WriteLine($"from {first - t} to {last - t}");
            int px = 0, pr = 0;
            for (int y = first; y <= last; y++)
            {
                var runs = rows[y];
                if (runs.Count == 0)
                    o.WriteLine("-");
                else if (runs.Count > 1)
                    o.WriteLine("* " + string.Join(" ", runs.Select(r => $"{r.A - l}-{r.B - l}")));
                else
                {
                    int a = runs[0].A - l, b = runs[0].B - l;
                    o.WriteLine(y == first ? $"{a} {b}" : $"{a - px} {b - pr}");
                    (px, pr) = (a, b);
                    continue;
                }
                if (runs.Count > 0)
                    (px, pr) = (runs[0].A - l, runs[^1].B - l);
            }
        }

        /// <summary>Each row's runs of changed pixels (or of one colour), "y: a-b c-d".</summary>
        public void WriteRuns(TextWriter o, int l, int t, uint? colour = null)
        {
            for (int y = 0; y < Height; y++)
            {
                var runs = Runs(y, p => colour is { } c ? p == Rgb(c) : p != White);
                if (runs.Count > 0)
                    o.WriteLine($"{y - t}: " + string.Join(" ", runs.Select(r => $"{r.A - l}-{r.B - l}")));
            }
            o.WriteLine(".");
        }

        public void Dispose()
        {
            SelectObject(m_dc, m_old);
            DeleteObject(m_bitmap);
            DeleteDC(m_dc);
        }
    }

    private struct XForm
    {
        public float M11, M12, M21, M22, Dx, Dy;
    }

    private const int NullBrush = 5, NullPen = 8, GM_COMPATIBLE = 1, GM_ADVANCED = 2;

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, byte[] info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll", EntryPoint = "Ellipse")] private static extern bool GdiEllipse(IntPtr dc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool FillRgn(IntPtr dc, IntPtr region, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern bool BeginPath(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool EndPath(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool AbortPath(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool FlattenPath(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "StrokeAndFillPath")] private static extern bool GdiStrokeAndFillPath(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetPath(IntPtr dc, int[]? points, byte[]? types, int count);
    [DllImport("gdi32.dll")] private static extern int SetGraphicsMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetWorldTransform(IntPtr dc, ref XForm form);
    [DllImport("gdi32.dll", EntryPoint = "Polygon")] private static extern bool GdiPolygon(IntPtr dc, int[] points, int count);
    [DllImport("gdi32.dll", EntryPoint = "Polyline")] private static extern bool GdiPolyline(IntPtr dc, int[] points, int count);
}
