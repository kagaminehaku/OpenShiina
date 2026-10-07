// IScnShapes without Windows: GDI's Ellipse drawn the way Wine's DIB driver draws it, which
// follows Windows' rules for the rectangle (GM_COMPATIBLE: right and bottom left out), for
// 1-pixel pens (a closed polyline through the outline's pixels, each line without its last
// pixel) and for the brush (the interior of CreateEllipticRgn of the same rectangle, under the
// outline). The outline's pixels and the region's rows come from Alois Zingl's ellipse algorithm,
// as in Wine (dlls/win32u/dibdrv/graphics.c, region.c). Two things are not Wine's: an ellipse
// 1.75 times as tall as wide or more, which Wine cuts short at its tips, gets them as Zingl's
// algorithm draws them (the middle column or two); and pens wider than a pixel, which GDI strokes
// as a polygon, are drawn as a disc on each outline pixel. tools/GdiEllipseCheck compares this
// with GDI itself on Windows.

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
        // The pen as the DIB driver keeps it: a null pen has no width; width 0 is a pixel
        int penWidth = pen is { } p0 && p0.Style != PS_NULL ? Math.Max(1, p0.Width) : 0;
        int? penColor = penWidth > 0 ? pen!.Value.Color : null;

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
        int w = r - l, h = b - t;
        if (w <= 2 || h <= 2)
        {
            Rectangle(canvas, l, t, r, b, brush, penColor, penWidth);
            return;
        }

        var outline = Outline(l, t, r, b);
        // A wide pen's outline is a region the interior leaves out; a thin pen draws over it
        var stroke = penWidth > 1 ? Stroke(outline, penWidth) : null;
        if (brush is { } fill)
        {
            var rows = Interior(l, t, r, b, outline);
            for (int y = 0; y < h; y++)
            {
                var (left, right) = rows[y];
                for (int x = left; x < right; x++)
                    if (stroke == null || !stroke.Contains((x, t + y)))
                        canvas.Set(x, t + y, fill);
            }
        }
        if (penColor is { } colour)
        {
            if (stroke != null)
                foreach (var (x, y) in stroke)
                    canvas.Set(x, y, colour);
            else
                Polyline(canvas, outline, colour);
        }
    }

    /// <summary>A wide pen's pixels: a disc on each of the outline's pixels.</summary>
    private static HashSet<(int X, int Y)> Stroke(List<(int X, int Y)> outline, int width)
    {
        var stroke = new HashSet<(int X, int Y)>();
        double r2 = width * width / 4.0;
        int reach = width / 2 + 1;
        foreach (var (cx, cy) in outline)
            for (int y = -reach; y <= reach; y++)
                for (int x = -reach; x <= reach; x++)
                    if (x * x + y * y <= r2)
                        stroke.Add((cx + x, cy + y));
        return stroke;
    }

    /// <summary>
    /// The outline's pixels in the order GDI joins them (counterclockwise from the right end of
    /// the middle row): Zingl's first quadrant, mirrored left and right, then up and down.
    /// </summary>
    internal static List<(int X, int Y)> Outline(int l, int t, int r, int b)
    {
        int w = r - l, h = b - t;
        var quadrant = FirstQuadrant(w, h);
        var points = new List<(int X, int Y)>(quadrant.Count * 4);
        // Counterclockwise: the quadrant goes up from the middle row on the right
        foreach (var (x, y) in quadrant)
            points.Add((r - w + x, t + h - 1 - y));
        int count = points.Count;
        // Left and right; an odd width has its middle column once
        int end = 2 * count - 1;
        if (w % 2 != 0)
            end--;
        Resize(points, end + 1);
        for (int i = 0; i < count; i++)
            points[end - i] = (l + r - 1 - points[i].X, points[i].Y);
        count = end + 1;
        // Up and down; an odd height has its middle row once
        end = 2 * count - 1;
        if (h % 2 != 0)
            end--;
        Resize(points, end + 1);
        for (int i = 0; i < count; i++)
            points[end - i] = (points[i].X, t + b - 1 - points[i].Y);
        return points;
    }

    private static void Resize(List<(int X, int Y)> points, int count)
    {
        while (points.Count < count)
            points.Add(default);
        if (points.Count > count)
            points.RemoveRange(count, points.Count - count);
    }

    /// <summary>
    /// Zingl's ellipse in a width x height box, the quarter from the middle row's right end to the
    /// bottom middle (y down), then on to the bottom row in the middle column or two.
    /// </summary>
    private static List<(int X, int Y)> FirstQuadrant(int width, int height)
    {
        long a = width - 1, b = height - 1;
        long asq = 8 * a * a, bsq = 8 * b * b;
        long dx = 4 * b * b * (1 - a), dy = 4 * a * a * (1 + b % 2);
        long err = dx + dy + a * a * (b % 2);
        int x = (int)a, y = height / 2;
        var points = new List<(int X, int Y)>(width + height);
        while (x >= width / 2)
        {
            long e2 = 2 * err;
            points.Add((x, y));
            if (e2 >= dx)
            {
                x--;
                err += dx += bsq;
            }
            if (e2 <= dy)
            {
                y++;
                err += dy += asq;
            }
        }
        // The tip of a tall ellipse
        var (lastX, lastY) = points[^1];
        for (int row = lastY + 1; row < height; row++)
            points.Add((lastX, row));
        return points;
    }

    /// <summary>
    /// CreateEllipticRgn's rows for the box (as the DIB driver asks for it), as [left, right) a
    /// row from the top; a row the region leaves empty or out (the tips of a tall ellipse) spans
    /// the outline's pixels in it.
    /// </summary>
    internal static (int Left, int Right)[] Interior(int l, int t, int r, int b, List<(int X, int Y)> outline)
    {
        int w = r - l, h = b - t;
        var rows = new (int Left, int Right)[h];
        var set = new bool[h];
        long a = w - 1, bb = h - 1;
        long asq = 8 * a * a, bsq = 8 * bb * bb;
        long dx = 4 * bb * bb * (1 - a), dy = 4 * a * a * (1 + bb % 2);
        long err = dx + dy + a * a * (bb % 2);
        int x = 0, y = h / 2;
        rows[y] = (l, r);
        set[y] = true;
        while (x <= w / 2)
        {
            long e2 = 2 * err;
            if (e2 >= dx)
            {
                x++;
                err += dx += bsq;
            }
            if (e2 <= dy)
            {
                y++;
                err += dy += asq;
                if (y >= h)
                    break;
                rows[y] = (l + x, r - x);
                set[y] = true;
            }
        }
        for (int i = 0; i < h / 2; i++)
        {
            rows[i] = rows[bb - i];
            set[i] = set[bb - i];
        }
        var missing = new bool[h];
        for (int i = 0; i < h; i++)
            if (!set[i] || rows[i].Left >= rows[i].Right)
            {
                missing[i] = true;
                rows[i] = (int.MaxValue, int.MinValue);
            }
        foreach (var (px, py) in outline)
            if (missing[py - t])
                rows[py - t] = (Math.Min(rows[py - t].Left, px), Math.Max(rows[py - t].Right, px + 1));
        for (int i = 0; i < h; i++)
            if (rows[i].Left == int.MaxValue)
                rows[i] = (0, 0);
        return rows;
    }

    /// <summary>A closed polyline of a 1-pixel pen through neighbouring pixels.</summary>
    private static void Polyline(Canvas canvas, List<(int X, int Y)> points, int colour)
    {
        for (int i = 0; i < points.Count; i++)
            Segment(canvas, points[i], points[(i + 1) % points.Count], colour);
    }

    /// <summary>
    /// A line of a 1-pixel pen without its last pixel. Only horizontal and vertical lines and
    /// steps to a neighbour come here; a slanted line would need GDI's Bresenham bias rules.
    /// </summary>
    private static void Segment(Canvas canvas, (int X, int Y) start, (int X, int Y) end, int colour)
    {
        int sx = Math.Sign(end.X - start.X), sy = Math.Sign(end.Y - start.Y);
        int length = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        for (int k = 0; k < length; k++)
            canvas.Set(start.X + sx * k, start.Y + sy * k, colour);
    }

    /// <summary>Rectangle, which GDI draws for an ellipse two pixels wide or high or less.</summary>
    private static void Rectangle(Canvas canvas, int l, int t, int r, int b, int? brush, int? penColor, int penWidth)
    {
        r--;
        b--;
        // Counterclockwise from the top right
        (int, int)[] corners = [(r, t), (l, t), (l, b), (r, b)];
        bool wide = penWidth > 1;
        if (brush is { } fill)
        {
            if (wide)
            {
                for (int y = t; y < b; y++)
                    for (int x = l; x < r; x++)
                        if (!OnFrame(x, y, l, t, r, b, penWidth))
                            canvas.Set(x, y, fill);
            }
            else
            {
                for (int y = t + (penWidth + 1) / 2; y < b - penWidth / 2; y++)
                    for (int x = l + (penWidth + 1) / 2; x < r - penWidth / 2; x++)
                        canvas.Set(x, y, fill);
            }
        }
        if (penColor is { } colour)
        {
            if (wide)
            {
                for (int y = t - penWidth; y <= b + penWidth; y++)
                    for (int x = l - penWidth; x <= r + penWidth; x++)
                        if (OnFrame(x, y, l, t, r, b, penWidth))
                            canvas.Set(x, y, colour);
            }
            else
            {
                for (int i = 0; i < 4; i++)
                    Segment(canvas, corners[i], corners[(i + 1) % 4], colour);
            }
        }
    }

    /// <summary>Within a wide pen's stroke along the rectangle's sides.</summary>
    private static bool OnFrame(int x, int y, int l, int t, int r, int b, int width)
    {
        double half = width / 2.0;
        bool inX = x >= l - half && x <= r + half, inY = y >= t - half && y <= b + half;
        return inX && inY && (Math.Abs(x - l) <= half || Math.Abs(x - r) <= half || Math.Abs(y - t) <= half || Math.Abs(y - b) <= half);
    }

    private readonly ref struct Canvas(Span<byte> pixels, int width, int height, int stride, int bytes)
    {
        private readonly Span<byte> m_pixels = pixels;

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
