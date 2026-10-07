// IScnShapes without Windows: GDI's Ellipse as Windows draws it, worked out from what GDI draws
// (tools/GdiEllipseCheck dump). In GM_COMPATIBLE, Ellipse(l, t, r, b) is a path: four Béziers
// around the box (16l, 16t, 16(r - 1), 16(b - 1)) in 28.4 fixed point (the right and bottom left
// out), counterclockwise from the middle of the right side, the control points 4(√2 - 1)/3 of
// the half width (rounded up) and of the half height (rounded down) from the middle of each
// side. The path is flattened by GDI's hybrid forward differencing (Kirk Olynyk and Andrew
// Goossen's; WPF keeps it in bezier.cpp, with GDI's constants noted: an error of 2/3 of a pixel, a
// 32-bit cracker for curves within 1024 pixels, else the 64-bit one), filled (pixel centres
// inside, the left and top edges in) and drawn over by the 1-pixel pen as GDI draws lines (grid
// intersect quantization: a pixel is lit when the line meets its diamond, |x| + |y| < 1/2 with
// its right and bottom corners; the last pixel of each line left out). Against Windows 10: every
// circle to 2200 pixels across (EFCLIB's wipe) but 2048, and 25577 of the 25600 ellipses to
// 160 x 160, are the same pixel for pixel; the others differ by a pixel where a line at 45
// degrees runs along diamonds' edges. Not worked out: with a null pen the brush fills
// CreateEllipticRgn's region, here the path half a pixel up and left; pens wider than a pixel
// are here a disc on each pixel of the 1-pixel outline.

using OpenShiina.Scripting;

namespace OpenShiina.Platform;

public sealed class DibShapes : IScnShapes
{
    private const int PS_NULL = 5, PS_INSIDEFRAME = 6;

    public void Ellipse(Span<byte> pixels, int width, int height, int stride, int bytesPerPixel,
        int l, int t, int r, int b, int? brush, ScnPen? pen)
    {
        if (bytesPerPixel is not (3 or 4))
            return;
        var canvas = new Canvas(pixels, width, height, stride, bytesPerPixel);
        int penWidth = pen is { } p0 && p0.Style != PS_NULL ? Math.Max(1, p0.Width) : 0;
        if (l > r)
            (l, r) = (r, l);
        if (t > b)
            (t, b) = (b, t);
        if (l == r || t == b)
            return;
        if (pen?.Style == PS_INSIDEFRAME && penWidth > 1)
        {
            l += penWidth / 2;
            t += penWidth / 2;
            r -= (penWidth - 1) / 2;
            b -= (penWidth - 1) / 2;
        }

        if (penWidth == 0)
        {
            if (brush is { } alone)
                Fill(canvas, Path(16 * l - 8, 16 * t - 8, 16 * (r - 1) - 8, 16 * (b - 1) - 8), alone, null);
            return;
        }
        var path = Path(16 * l, 16 * t, 16 * (r - 1), 16 * (b - 1));
        HashSet<(int X, int Y)>? wide = null;
        if (penWidth > 1)
        {
            // A disc on each pixel of the 1-pixel outline
            wide = new HashSet<(int X, int Y)>();
            var line = new List<(int X, int Y)>();
            Stroke(path, line, width, height);
            double r2 = penWidth * penWidth / 4.0;
            int reach = penWidth / 2 + 1;
            foreach (var (cx, cy) in line)
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dx = -reach; dx <= reach; dx++)
                        if (dx * dx + dy * dy <= r2)
                            wide.Add((cx + dx, cy + dy));
        }
        if (brush is { } fill)
            Fill(canvas, path, fill, wide);
        int colour = pen!.Value.Color;
        if (wide != null)
            foreach (var (x, y) in wide)
                canvas.Set(x, y, colour);
        else
        {
            var line = new List<(int X, int Y)>();
            Stroke(path, line, width, height);
            foreach (var (x, y) in line)
                canvas.Set(x, y, colour);
        }
    }

    // ---- The path ----

    private static readonly double s_kappa = 4 * (Math.Sqrt(2) - 1) / 3;

    /// <summary>The ellipse in the box (28.4), flattened: a closed polygon without its last point.</summary>
    internal static List<(int X, int Y)> Path(int left, int top, int right, int bottom)
    {
        int w = right - left, h = bottom - top;
        int cx = left + w / 2, cy = top + h / 2;
        int ox = (int)Math.Ceiling(s_kappa * w / 2), oy = (int)Math.Floor(s_kappa * h / 2);
        (int X, int Y)[] control =
        [
            (right, cy), (right, cy - oy), (cx + ox, top), (cx, top), (cx - ox, top), (left, cy - oy),
            (left, cy), (left, cy + oy), (cx - ox, bottom), (cx, bottom), (cx + ox, bottom), (right, cy + oy), (right, cy),
        ];
        var points = new List<(int X, int Y)> { control[0] };
        for (int i = 0; i < 4; i++)
            Flatten(control.AsSpan(3 * i, 4), points);
        if (points.Count > 1 && points[^1] == points[0])
            points.RemoveAt(points.Count - 1);
        return points;
    }

    /// <summary>The points of a Bézier after its first, as GDI flattens it.</summary>
    private static void Flatten(ReadOnlySpan<(int X, int Y)> bezier, List<(int X, int Y)> points)
    {
        if (!Bezier32.Flatten(bezier, points))
            Bezier64.Flatten(bezier, points);
    }

    // Bezier32 and Bezier64 follow WPF's bezier.cpp (github.com/dotnet/wpf, src/Microsoft.DotNet.Wpf/
    // src/WpfGfx/core/geometry; MIT, copyright the .NET Foundation) with the constants it gives as
    // GDI's (BEZIER_FLATTEN_GDI_COMPATIBLE) and without WPF's later check on large errors.

    /// <summary>GDI's 32-bit hybrid forward differencing, in its own 32-bit arithmetic.</summary>
    private static class Bezier32
    {
        private const int InitialShift = 10, AdditionalShift = 3, Shift = InitialShift + AdditionalShift;
        private const int Round = 1 << (Shift - 1);
        private const int InitialTestMagnitude = 6 * 0x2aa0, TestMagnitude = InitialTestMagnitude << AdditionalShift;
        // Points must lie within a 10-bit space (28.4) of the curve's bounds
        private const int MaxSize = unchecked((int)0xffffc000);

        private struct Basis
        {
            public int E0, E1, E2, E3;

            public Basis(int p1, int p2, int p3, int p4)
            {
                E0 = p1 << InitialShift;
                E1 = (p4 - p1) << InitialShift;
                E2 = 6 * (p2 - p3 - p3 + p4) << InitialShift;
                E3 = 6 * (p1 - p2 - p2 + p3) << InitialShift;
            }

            public readonly int ParentErrorDividedBy4 => Math.Max(Math.Abs(E3), Math.Abs(E2 + E2 - E3));
            public readonly int Error => Math.Max(Math.Abs(E2), Math.Abs(E3));
            public readonly int Value => (E0 + Round) >> Shift;

            public void LazyHalveStepSize(int shift)
            {
                E2 = (E2 + E3) >> 1;
                E1 = (E1 - (E2 >> shift)) >> 1;
            }

            public void SteadyState(int shift)
            {
                E0 <<= AdditionalShift;
                E1 <<= AdditionalShift;
                int s = shift - AdditionalShift;
                if (s < 0)
                {
                    E2 <<= -s;
                    E3 <<= -s;
                }
                else
                {
                    E2 >>= s;
                    E3 >>= s;
                }
            }

            public void HalveStepSize()
            {
                E2 = (E2 + E3) >> 3;
                E1 = (E1 - E2) >> 1;
                E3 >>= 2;
            }

            public void DoubleStepSize()
            {
                E1 += E1 + E2;
                E3 <<= 2;
                E2 = (E2 << 3) - E3;
            }

            public void TakeStep()
            {
                E0 += E1;
                int temp = E2;
                E1 += temp;
                E2 += temp - E3;
                E3 = temp;
            }
        }

        public static bool Flatten(ReadOnlySpan<(int X, int Y)> p, List<(int X, int Y)> points)
        {
            unchecked
            {
                int left = Math.Min(Math.Min(p[0].X, p[1].X), Math.Min(p[2].X, p[3].X)) - 16;
                int top = Math.Min(Math.Min(p[0].Y, p[1].Y), Math.Min(p[2].Y, p[3].Y)) - 16;
                int x0 = p[0].X - left, x1 = p[1].X - left, x2 = p[2].X - left, x3 = p[3].X - left;
                int y0 = p[0].Y - top, y1 = p[1].Y - top, y2 = p[2].Y - top, y3 = p[3].Y - top;
                if (((x0 | x1 | x2 | x3 | y0 | y1 | y2 | y3) & MaxSize) != 0)
                    return false;
                var x = new Basis(x0, x1, x2, x3);
                var y = new Basis(y0, y1, y2, y3);
                int shift = 0, steps = 1;
                while (x.Error > InitialTestMagnitude << shift || y.Error > InitialTestMagnitude << shift)
                {
                    shift += 2;
                    x.LazyHalveStepSize(shift);
                    y.LazyHalveStepSize(shift);
                    steps <<= 1;
                }
                x.SteadyState(shift);
                y.SteadyState(shift);
                x.TakeStep();
                y.TakeStep();
                steps--;
                while (true)
                {
                    points.Add((x.Value + left, y.Value + top));
                    if (steps == 0)
                        return true;
                    if (Math.Max(x.Error, y.Error) > TestMagnitude)
                    {
                        x.HalveStepSize();
                        y.HalveStepSize();
                        steps <<= 1;
                    }
                    while ((steps & 1) == 0 && x.ParentErrorDividedBy4 <= TestMagnitude >> 2 && y.ParentErrorDividedBy4 <= TestMagnitude >> 2)
                    {
                        x.DoubleStepSize();
                        y.DoubleStepSize();
                        steps >>= 1;
                    }
                    steps--;
                    x.TakeStep();
                    y.TakeStep();
                }
            }
        }
    }

    /// <summary>GDI's 64-bit cracker, in 36.28 fixed point, for bigger curves.</summary>
    private static class Bezier64
    {
        private const int Fraction = 28;
        private const long ErrorHigh = (long)(6 * (1 << 15) >> (32 - Fraction)) << 32, ErrorLow = 4L << 32;

        private struct Basis
        {
            public long E0, E1, E2, E3;

            public Basis(long p1, long p2, long p3, long p4)
            {
                E0 = p1 << Fraction;
                E1 = (p4 - p1) << Fraction;
                E2 = 3 * (p2 - 2 * p3 + p4) << (Fraction + 1);
                E3 = 3 * (p1 - 2 * p2 + p3) << (Fraction + 1);
            }

            public readonly long ParentError => Math.Max(Math.Abs(E3 << 2), Math.Abs((E2 << 3) - (E3 << 2)));
            public readonly long Error => Math.Max(Math.Abs(E2), Math.Abs(E3));
            public readonly int Value => (int)((E0 + (1L << (Fraction - 1))) >> Fraction);

            public readonly (int, int, int, int) Untransform()
            {
                long p2 = 3 * E1, p1 = p2 + p2 - E2;
                p2 = p1 + p1 - E3;
                p1 -= E3 + E3;
                p1 = p1 / 18 + E0;
                p2 = p2 / 18 + E0;
                static int R(long v) => (int)((v + (1L << (Fraction - 1))) >> Fraction);
                return (R(E0), R(p1), R(p2), R(E0 + E1));
            }

            public void HalveStepSize()
            {
                E2 = (E2 + E3) >> 3;
                E1 = (E1 - E2) >> 1;
                E3 >>= 2;
            }

            public void DoubleStepSize()
            {
                E1 = (E1 << 1) + E2;
                E3 <<= 2;
                E2 = (E2 << 3) - E3;
            }

            public void TakeStep()
            {
                E0 += E1;
                long temp = E2;
                E1 += E2;
                E2 += temp - E3;
                E3 = temp;
            }
        }

        public static void Flatten(ReadOnlySpan<(int X, int Y)> p, List<(int X, int Y)> points)
        {
            var xHigh = new Basis(p[0].X, p[1].X, p[2].X, p[3].X);
            var yHigh = new Basis(p[0].Y, p[1].Y, p[2].Y, p[3].Y);
            int stepsHigh = 1, stepsLow = 0;
            while (xHigh.Error > ErrorHigh || yHigh.Error > ErrorHigh)
            {
                stepsHigh <<= 1;
                xHigh.HalveStepSize();
                yHigh.HalveStepSize();
            }
            Basis xLow = default, yLow = default;
            while (true)
            {
                if (stepsLow == 0)
                {
                    var (a0, a1, a2, a3) = xHigh.Untransform();
                    var (b0, b1, b2, b3) = yHigh.Untransform();
                    xLow = new Basis(a0, a1, a2, a3);
                    yLow = new Basis(b0, b1, b2, b3);
                    stepsLow = 1;
                    while (xLow.Error > ErrorLow || yLow.Error > ErrorLow)
                    {
                        stepsLow <<= 1;
                        xLow.HalveStepSize();
                        yLow.HalveStepSize();
                    }
                    if (--stepsHigh != 0)
                    {
                        xHigh.TakeStep();
                        yHigh.TakeStep();
                        if (xHigh.Error > ErrorHigh || yHigh.Error > ErrorHigh)
                        {
                            stepsHigh <<= 1;
                            xHigh.HalveStepSize();
                            yHigh.HalveStepSize();
                        }
                        while ((stepsHigh & 1) == 0 && xHigh.ParentError <= ErrorHigh && yHigh.ParentError <= ErrorHigh)
                        {
                            xHigh.DoubleStepSize();
                            yHigh.DoubleStepSize();
                            stepsHigh >>= 1;
                        }
                    }
                }
                xLow.TakeStep();
                yLow.TakeStep();
                points.Add((xLow.Value, yLow.Value));
                stepsLow--;
                if (stepsLow == 0 && stepsHigh == 0)
                    return;
                if (xLow.Error > ErrorLow || yLow.Error > ErrorLow)
                {
                    stepsLow <<= 1;
                    xLow.HalveStepSize();
                    yLow.HalveStepSize();
                }
                while ((stepsLow & 1) == 0 && xLow.ParentError <= ErrorLow && yLow.ParentError <= ErrorLow)
                {
                    xLow.DoubleStepSize();
                    yLow.DoubleStepSize();
                    stepsLow >>= 1;
                }
            }
        }
    }

    // ---- Filling ----

    /// <summary>
    /// The polygon's inside, alternating: each row's pixel centres between pairs of crossings,
    /// an edge crossing the row when its top is on or above it and its bottom below; a pixel
    /// whose centre is on the left crossing is in, on the right one out.
    /// </summary>
    private static void Fill(Canvas canvas, List<(int X, int Y)> polygon, int colour, HashSet<(int X, int Y)>? except)
    {
        int n = polygon.Count;
        if (n < 3)
            return;
        // The edges by their top, those across the current row kept active
        var edges = new List<(int X0, int Y0, int X1, int Y1)>(n);
        for (int i = 0; i < n; i++)
        {
            var (x0, y0) = polygon[i];
            var (x1, y1) = polygon[i + 1 < n ? i + 1 : 0];
            if (y0 != y1)
                edges.Add(y0 < y1 ? (x0, y0, x1, y1) : (x1, y1, x0, y0));
        }
        if (edges.Count == 0)
            return;
        edges.Sort((a, b) => a.Y0.CompareTo(b.Y0));
        var active = new List<(int X0, int Y0, int X1, int Y1)>();
        // Each crossing as the first pixel at or right of it (rounding keeps their order)
        var crossings = new long[edges.Count];
        int next = 0, maxY = edges.Max(e => e.Y1);
        int row = Math.Max(CeilDiv(edges[0].Y0, 16), 0);
        for (; 16L * row < maxY && row < canvas.Height; row++)
        {
            long y = 16L * row;
            while (next < edges.Count && edges[next].Y0 <= y)
                active.Add(edges[next++]);
            active.RemoveAll(e => e.Y1 <= y);
            int count = 0;
            foreach (var (x0, y0, x1, y1) in active)
            {
                // x = x0 + (x1 - x0)(y - y0) / (y1 - y0), the edge's top in, its bottom out
                long den = y1 - y0, num = x0 * den + (long)(x1 - x0) * (y - y0);
                long first = CeilDiv(num, 16 * den);
                int k = count++;
                for (; k > 0 && crossings[k - 1] > first; k--)
                    crossings[k] = crossings[k - 1];
                crossings[k] = first;
            }
            for (int k = 0; k + 1 < count; k += 2)
            {
                int first = (int)Math.Max(crossings[k], 0), end = (int)Math.Min(crossings[k + 1], canvas.Width);
                if (except == null)
                    canvas.FillRow(row, first, end, colour);
                else
                    for (int x = first; x < end; x++)
                        if (!except.Contains((x, row)))
                            canvas.Set(x, row, colour);
            }
        }
    }

    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

    private static int CeilDiv(int a, int b) => (int)CeilDiv((long)a, b);

    private static long FloorDiv(long a, long b) => a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0);

    // ---- The 1-pixel pen ----

    /// <summary>The closed polygon's lines, each without its last pixel.</summary>
    internal static void Stroke(List<(int X, int Y)> polygon, List<(int X, int Y)> pixels, int width, int height)
    {
        int n = polygon.Count;
        if (n == 1)
            return;
        for (int i = 0; i < n; i++)
        {
            var (p, q) = (polygon[i], polygon[(i + 1) % n]);
            // Lines that cannot reach the picture
            if (Math.Max(p.X, q.X) < -16 || Math.Min(p.X, q.X) > 16 * width || Math.Max(p.Y, q.Y) < -16 || Math.Min(p.Y, q.Y) > 16 * height)
                continue;
            Line(p, q, pixels, width, height);
        }
    }

    /// <summary>
    /// A cosmetic line from p to q (28.4): the pixels whose diamond it meets, but the one whose
    /// diamond holds q. Each pixel near the line along its longer axis is tested exactly. A line
    /// at exactly 45 degrees can run along diamonds' edges: it counts as moved a little (s_nudge).
    /// </summary>
    private static void Line((int X, int Y) p, (int X, int Y) q, List<(int X, int Y)> pixels, int width, int height)
    {
        long dx = q.X - p.X, dy = q.Y - p.Y;
        if (dx == 0 && dy == 0)
            return;
        bool diagonal = Math.Abs(dx) == Math.Abs(dy);
        int dir = Dir(dx, dy);
        var (endX, endY) = EndPixel(q, diagonal, dir);
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            long from = Math.Max(FloorDiv(Math.Min(p.X, q.X) - 8, 16), -1), to = Math.Min(CeilDiv(Math.Max(p.X, q.X) + 8, 16), width);
            for (long x = from; x <= to; x++)
            {
                long y = FloorDiv(p.Y + FloorDiv(dy * (16 * x - p.X), dx), 16);
                for (long yy = y - 1; yy <= y + 2; yy++)
                    Try(x, yy);
            }
        }
        else
        {
            long from = Math.Max(FloorDiv(Math.Min(p.Y, q.Y) - 8, 16), -1), to = Math.Min(CeilDiv(Math.Max(p.Y, q.Y) + 8, 16), height);
            for (long y = from; y <= to; y++)
            {
                long x = FloorDiv(p.X + FloorDiv(dx * (16 * y - p.Y), dy), 16);
                for (long xx = x - 1; xx <= x + 2; xx++)
                    Try(xx, y);
            }
        }

        void Try(long x, long y)
        {
            if ((x != endX || y != endY) && Meets(p, dx, dy, 16 * x, 16 * y, diagonal, dir))
                pixels.Add(((int)x, (int)y));
        }
    }

    /// <summary>The pixel whose diamond holds the point, or none (a point on a diamond's edge).</summary>
    private static (long X, long Y) EndPixel((int X, int Y) q, bool diagonal, int dir)
    {
        long cx = FloorDiv(q.X + 8, 16), cy = FloorDiv(q.Y + 8, 16);
        for (long y = cy - 1; y <= cy + 1; y++)
            for (long x = cx - 1; x <= cx + 1; x++)
                if (InDiamond(q.X - 16 * x, q.Y - 16 * y, diagonal, dir))
                    return (x, y);
        return (long.MinValue, long.MinValue);
    }

    /// <summary>
    /// A pixel's diamond: |x| + |y| below half a pixel, with its right and bottom corners; for a
    /// line at 45 degrees, with the edge points the line's nudge takes inside.
    /// </summary>
    private static bool InDiamond(long x, long y, bool diagonal, int dir = 0)
    {
        long f = Math.Abs(x) + Math.Abs(y);
        return f < 8 || f == 8 && (diagonal ? Nudged(x, y, dir) : (x, y) is (8, 0) or (0, 8));
    }

    // Which way a line at 45 degrees counts as moved, by its direction (Dir): right and down when
    // going right and down or left and up, else left and down. Found against Windows' pixels.
    private static readonly (int A, int B)[] s_nudge = [(2, 1), (-2, 1), (-2, 1), (2, 1)];

    private static int Dir(long dx, long dy) => (dx > 0 ? 0 : 1) + (dy > 0 ? 0 : 2);

    /// <summary>A point on a diamond's edge, for a line at 45 degrees: inside once the line is moved?</summary>
    private static bool Nudged(long x, long y, int dir)
    {
        var (a, b) = s_nudge[dir];
        long d = (x != 0 ? Math.Sign(x) * a : Math.Abs(a)) + (y != 0 ? Math.Sign(y) * b : Math.Abs(b));
        return d < 0;
    }

    /// <summary>Whether the line from p by (dx, dy) meets the diamond of the pixel centred at c.</summary>
    private static bool Meets((int X, int Y) p, long dx, long dy, long cx, long cy, bool diagonal, int dir)
    {
        long x0 = p.X - cx, y0 = p.Y - cy;
        // |x| + |y| along the line is smallest at an end or where x or y is 0 (t = n / d); where
        // the line only touches the diamond's edge, its points there include one of these
        if (In(0, 1) || In(1, 1))
            return true;
        if (dx != 0 && Within(-x0, dx) && In(-x0, dx))
            return true;
        if (dy != 0 && Within(-y0, dy) && In(-y0, dy))
            return true;
        if (diagonal)
            return false;
        // The corners that belong to the diamond, on the line
        return OnLine(8, 0) || OnLine(0, 8);

        bool In(long n, long d)
        {
            if (d < 0)
                (n, d) = (-n, -d);
            long x = x0 * d + dx * n, y = y0 * d + dy * n, f = Math.Abs(x) + Math.Abs(y);
            return f < 8 * d || diagonal && f == 8 * d && Nudged(x, y, dir);
        }

        static bool Within(long n, long d) => d > 0 ? n > 0 && n < d : n < 0 && n > d;

        bool OnLine(long x, long y) =>
            dx * (y - y0) == dy * (x - x0) && Math.Min(x0, x0 + dx) <= x && x <= Math.Max(x0, x0 + dx)
            && Math.Min(y0, y0 + dy) <= y && y <= Math.Max(y0, y0 + dy);
    }

    private readonly ref struct Canvas(Span<byte> pixels, int width, int height, int stride, int bytes)
    {
        private readonly Span<byte> m_pixels = pixels;

        public int Width => width;
        public int Height => height;

        /// <summary>Pixels first to end - 1 of a row inside the picture.</summary>
        public void FillRow(int y, int first, int end, int colour)
        {
            if (end <= first)
                return;
            var row = m_pixels.Slice(y * stride + first * bytes, (end - first) * bytes);
            if (bytes == 4)
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row).Fill((uint)((colour & 0xFF) << 16 | colour & 0xFF00 | colour >> 16 & 0xFF));
            else
                for (int at = 0; at < row.Length; at += 3)
                {
                    row[at] = (byte)(colour >> 16);
                    row[at + 1] = (byte)(colour >> 8);
                    row[at + 2] = (byte)colour;
                }
        }

        /// <summary>A pixel in 0x00BBGGRR, clipped to the picture; 32-bit pixels get 0 in their fourth byte.</summary>
        public void Set(int x, int y, int colour)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                return;
            int at = y * stride + x * bytes;
            m_pixels[at] = (byte)(colour >> 16);
            m_pixels[at + 1] = (byte)(colour >> 8);
            m_pixels[at + 2] = (byte)colour;
            if (bytes == 4)
                m_pixels[at + 3] = 0;
        }
    }
}
