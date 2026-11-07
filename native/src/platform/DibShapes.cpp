// C# Platform/DibShapes.cs: GDI's Ellipse as Windows draws it, worked out from what GDI draws
// (tools/GdiEllipseCheck dump). In GM_COMPATIBLE, Ellipse(l, t, r, b) is a path: four Béziers
// around the box (16l, 16t, 16(r - 1), 16(b - 1)) in 28.4 fixed point (the right and bottom left
// out), counterclockwise from the middle of the right side, the control points 4(√2 - 1)/3 of the
// half width (rounded up) and of the half height (rounded down) from the middle of each side. The
// path is flattened by GDI's hybrid forward differencing (Kirk Olynyk and Andrew Goossen's; WPF
// keeps it in bezier.cpp, with GDI's constants noted: an error of 2/3 of a pixel, a 32-bit cracker
// for curves within 1024 pixels, else the 64-bit one), filled (pixel centres inside, the left and
// top edges in) and drawn over by the 1-pixel pen as GDI draws lines (grid intersect quantization:
// a pixel is lit when the line meets its diamond, |x| + |y| < 1/2 with its right and bottom
// corners; the last pixel of each line left out). Against Windows 10: every circle to 2200 pixels
// across (EFCLIB's wipe) but 2048, and 25577 of the 25600 ellipses to 160 x 160, are the same pixel
// for pixel; the others differ by a pixel where a line at 45 degrees runs along diamonds' edges.
// Not worked out: with a null pen the brush fills CreateEllipticRgn's region, here the path half a
// pixel up and left; pens wider than a pixel are here a disc on each pixel of the 1-pixel outline.
#include "platform/DibShapes.h"

#include <algorithm>
#include <climits>
#include <cmath>
#include <cstdlib>
#include <unordered_set>
#include <vector>

namespace openshiina::platform {

namespace {

constexpr int PS_NULL = 5, PS_INSIDEFRAME = 6;

struct Point
{
    int X, Y;
    bool operator==(const Point&) const = default;
};

using Polygon = std::vector<Point>;

/// A pixel as one key for the sets of pixels.
uint64_t Key(int x, int y) { return uint64_t(uint32_t(x)) << 32 | uint32_t(y); }

int64_t FloorDiv(int64_t a, int64_t b) { return a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0); }
int64_t CeilDiv(int64_t a, int64_t b) { return -FloorDiv(-a, b); }

// Bezier32 and Bezier64 follow WPF's bezier.cpp (github.com/dotnet/wpf, src/Microsoft.DotNet.Wpf/
// src/WpfGfx/core/geometry; MIT, copyright the .NET Foundation) with the constants it gives as
// GDI's (BEZIER_FLATTEN_GDI_COMPATIBLE) and without WPF's later check on large errors.

/// GDI's 32-bit hybrid forward differencing, in its own 32-bit arithmetic (wrapping, as the C#
/// player's unchecked code).
namespace Bezier32 {

constexpr int InitialShift = 10, AdditionalShift = 3, Shift = InitialShift + AdditionalShift;
constexpr int Round = 1 << (Shift - 1);
constexpr int InitialTestMagnitude = 6 * 0x2aa0, TestMagnitude = InitialTestMagnitude << AdditionalShift;
// Points must lie within a 10-bit space (28.4) of the curve's bounds
constexpr int MaxSize = int(0xffffc000u);

int Abs(int v) { return v < 0 ? int(0u - uint32_t(v)) : v; }

struct Basis
{
    int E0, E1, E2, E3;

    Basis(int p1, int p2, int p3, int p4)
        : E0(p1 << InitialShift), E1((p4 - p1) << InitialShift), E2((6 * (p2 - p3 - p3 + p4)) << InitialShift),
          E3((6 * (p1 - p2 - p2 + p3)) << InitialShift)
    {
    }

    int ParentErrorDividedBy4() const { return std::max(Abs(E3), Abs(E2 + E2 - E3)); }
    int Error() const { return std::max(Abs(E2), Abs(E3)); }
    int Value() const { return (E0 + Round) >> Shift; }

    void LazyHalveStepSize(int shift)
    {
        E2 = (E2 + E3) >> 1;
        E1 = (E1 - (E2 >> shift)) >> 1;
    }

    void SteadyState(int shift)
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

    void HalveStepSize()
    {
        E2 = (E2 + E3) >> 3;
        E1 = (E1 - E2) >> 1;
        E3 >>= 2;
    }

    void DoubleStepSize()
    {
        E1 += E1 + E2;
        E3 <<= 2;
        E2 = (E2 << 3) - E3;
    }

    void TakeStep()
    {
        E0 += E1;
        int temp = E2;
        E1 += temp;
        E2 += temp - E3;
        E3 = temp;
    }
};

bool Flatten(const Point* p, Polygon& points)
{
    int left = std::min(std::min(p[0].X, p[1].X), std::min(p[2].X, p[3].X)) - 16;
    int top = std::min(std::min(p[0].Y, p[1].Y), std::min(p[2].Y, p[3].Y)) - 16;
    int x0 = p[0].X - left, x1 = p[1].X - left, x2 = p[2].X - left, x3 = p[3].X - left;
    int y0 = p[0].Y - top, y1 = p[1].Y - top, y2 = p[2].Y - top, y3 = p[3].Y - top;
    if (((x0 | x1 | x2 | x3 | y0 | y1 | y2 | y3) & MaxSize) != 0)
        return false;
    Basis x(x0, x1, x2, x3), y(y0, y1, y2, y3);
    int shift = 0, steps = 1;
    while (x.Error() > InitialTestMagnitude << shift || y.Error() > InitialTestMagnitude << shift)
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
        points.push_back({x.Value() + left, y.Value() + top});
        if (steps == 0)
            return true;
        if (std::max(x.Error(), y.Error()) > TestMagnitude)
        {
            x.HalveStepSize();
            y.HalveStepSize();
            steps <<= 1;
        }
        while ((steps & 1) == 0 && x.ParentErrorDividedBy4() <= TestMagnitude >> 2 && y.ParentErrorDividedBy4() <= TestMagnitude >> 2)
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

} // namespace Bezier32

/// GDI's 64-bit cracker, in 36.28 fixed point, for bigger curves.
namespace Bezier64 {

constexpr int Fraction = 28;
constexpr int64_t ErrorHigh = int64_t((6 * (1 << 15)) >> (32 - Fraction)) << 32, ErrorLow = int64_t(4) << 32;

struct Basis
{
    int64_t E0 = 0, E1 = 0, E2 = 0, E3 = 0;

    Basis() = default;
    Basis(int64_t p1, int64_t p2, int64_t p3, int64_t p4)
        : E0(p1 << Fraction), E1((p4 - p1) << Fraction), E2((3 * (p2 - 2 * p3 + p4)) << (Fraction + 1)),
          E3((3 * (p1 - 2 * p2 + p3)) << (Fraction + 1))
    {
    }

    int64_t ParentError() const { return std::max(std::llabs(E3 << 2), std::llabs((E2 << 3) - (E3 << 2))); }
    int64_t Error() const { return std::max(std::llabs(E2), std::llabs(E3)); }
    int Value() const { return int((E0 + (int64_t(1) << (Fraction - 1))) >> Fraction); }

    static int R(int64_t v) { return int((v + (int64_t(1) << (Fraction - 1))) >> Fraction); }

    void Untransform(int& a0, int& a1, int& a2, int& a3) const
    {
        int64_t p2 = 3 * E1, p1 = p2 + p2 - E2;
        p2 = p1 + p1 - E3;
        p1 -= E3 + E3;
        p1 = p1 / 18 + E0;
        p2 = p2 / 18 + E0;
        a0 = R(E0);
        a1 = R(p1);
        a2 = R(p2);
        a3 = R(E0 + E1);
    }

    void HalveStepSize()
    {
        E2 = (E2 + E3) >> 3;
        E1 = (E1 - E2) >> 1;
        E3 >>= 2;
    }

    void DoubleStepSize()
    {
        E1 = (E1 << 1) + E2;
        E3 <<= 2;
        E2 = (E2 << 3) - E3;
    }

    void TakeStep()
    {
        E0 += E1;
        int64_t temp = E2;
        E1 += E2;
        E2 += temp - E3;
        E3 = temp;
    }
};

void Flatten(const Point* p, Polygon& points)
{
    Basis xHigh(p[0].X, p[1].X, p[2].X, p[3].X), yHigh(p[0].Y, p[1].Y, p[2].Y, p[3].Y);
    int stepsHigh = 1, stepsLow = 0;
    while (xHigh.Error() > ErrorHigh || yHigh.Error() > ErrorHigh)
    {
        stepsHigh <<= 1;
        xHigh.HalveStepSize();
        yHigh.HalveStepSize();
    }
    Basis xLow, yLow;
    while (true)
    {
        if (stepsLow == 0)
        {
            int a0, a1, a2, a3, b0, b1, b2, b3;
            xHigh.Untransform(a0, a1, a2, a3);
            yHigh.Untransform(b0, b1, b2, b3);
            xLow = Basis(a0, a1, a2, a3);
            yLow = Basis(b0, b1, b2, b3);
            stepsLow = 1;
            while (xLow.Error() > ErrorLow || yLow.Error() > ErrorLow)
            {
                stepsLow <<= 1;
                xLow.HalveStepSize();
                yLow.HalveStepSize();
            }
            if (--stepsHigh != 0)
            {
                xHigh.TakeStep();
                yHigh.TakeStep();
                if (xHigh.Error() > ErrorHigh || yHigh.Error() > ErrorHigh)
                {
                    stepsHigh <<= 1;
                    xHigh.HalveStepSize();
                    yHigh.HalveStepSize();
                }
                while ((stepsHigh & 1) == 0 && xHigh.ParentError() <= ErrorHigh && yHigh.ParentError() <= ErrorHigh)
                {
                    xHigh.DoubleStepSize();
                    yHigh.DoubleStepSize();
                    stepsHigh >>= 1;
                }
            }
        }
        xLow.TakeStep();
        yLow.TakeStep();
        points.push_back({xLow.Value(), yLow.Value()});
        stepsLow--;
        if (stepsLow == 0 && stepsHigh == 0)
            return;
        if (xLow.Error() > ErrorLow || yLow.Error() > ErrorLow)
        {
            stepsLow <<= 1;
            xLow.HalveStepSize();
            yLow.HalveStepSize();
        }
        while ((stepsLow & 1) == 0 && xLow.ParentError() <= ErrorLow && yLow.ParentError() <= ErrorLow)
        {
            xLow.DoubleStepSize();
            yLow.DoubleStepSize();
            stepsLow >>= 1;
        }
    }
}

} // namespace Bezier64

/// The ellipse in the box (28.4), flattened: a closed polygon without its last point.
Polygon Path(int left, int top, int right, int bottom)
{
    static const double kappa = 4 * (std::sqrt(2.0) - 1) / 3;
    int w = right - left, h = bottom - top;
    int cx = left + w / 2, cy = top + h / 2;
    int ox = int(std::ceil(kappa * w / 2)), oy = int(std::floor(kappa * h / 2));
    const Point control[] = {
        {right, cy},         {right, cy - oy}, {cx + ox, top},    {cx, top},     {cx - ox, top},
        {left, cy - oy},     {left, cy},       {left, cy + oy},   {cx - ox, bottom}, {cx, bottom},
        {cx + ox, bottom},   {right, cy + oy}, {right, cy},
    };
    Polygon points{control[0]};
    for (int i = 0; i < 4; i++)
        if (!Bezier32::Flatten(control + 3 * i, points))
            Bezier64::Flatten(control + 3 * i, points);
    if (points.size() > 1 && points.back() == points.front())
        points.pop_back();
    return points;
}

class Canvas
{
public:
    Canvas(uint8_t* pixels, int width, int height, int stride, int bytes)
        : m_pixels(pixels), m_width(width), m_height(height), m_stride(stride), m_bytes(bytes)
    {
    }

    int Width() const { return m_width; }
    int Height() const { return m_height; }

    /// Pixels first to end - 1 of a row inside the picture.
    void FillRow(int y, int first, int end, int colour)
    {
        for (int x = first; x < end; x++)
            Set(x, y, colour);
    }

    /// A pixel in 0x00BBGGRR, clipped to the picture; 32-bit pixels get 0 in their fourth byte.
    void Set(int x, int y, int colour)
    {
        if (uint32_t(x) >= uint32_t(m_width) || uint32_t(y) >= uint32_t(m_height))
            return;
        uint8_t* at = m_pixels + size_t(y) * size_t(m_stride) + size_t(x) * size_t(m_bytes);
        at[0] = uint8_t(colour >> 16);
        at[1] = uint8_t(colour >> 8);
        at[2] = uint8_t(colour);
        if (m_bytes == 4)
            at[3] = 0;
    }

private:
    uint8_t* m_pixels;
    int m_width, m_height, m_stride, m_bytes;
};

struct Edge
{
    int X0, Y0, X1, Y1;
};

/// The polygon's inside, alternating: each row's pixel centres between pairs of crossings, an edge
/// crossing the row when its top is on or above it and its bottom below; a pixel whose centre is on
/// the left crossing is in, on the right one out.
void Fill(Canvas& canvas, const Polygon& polygon, int colour, const std::unordered_set<uint64_t>* except)
{
    size_t n = polygon.size();
    if (n < 3)
        return;
    // The edges by their top, those across the current row kept active
    std::vector<Edge> edges;
    edges.reserve(n);
    for (size_t i = 0; i < n; i++)
    {
        Point a = polygon[i], b = polygon[i + 1 < n ? i + 1 : 0];
        if (a.Y != b.Y)
            edges.push_back(a.Y < b.Y ? Edge{a.X, a.Y, b.X, b.Y} : Edge{b.X, b.Y, a.X, a.Y});
    }
    if (edges.empty())
        return;
    std::sort(edges.begin(), edges.end(), [](const Edge& a, const Edge& b) { return a.Y0 < b.Y0; });
    std::vector<Edge> active;
    // Each crossing as the first pixel at or right of it (rounding keeps their order)
    std::vector<int64_t> crossings(edges.size());
    size_t next = 0;
    int maxY = INT_MIN;
    for (const auto& e : edges)
        maxY = std::max(maxY, e.Y1);
    int row = int(std::max<int64_t>(CeilDiv(edges[0].Y0, 16), 0));
    for (; 16LL * row < maxY && row < canvas.Height(); row++)
    {
        int64_t y = 16LL * row;
        while (next < edges.size() && edges[next].Y0 <= y)
            active.push_back(edges[next++]);
        std::erase_if(active, [y](const Edge& e) { return e.Y1 <= y; });
        int count = 0;
        for (const auto& [x0, y0, x1, y1] : active)
        {
            // x = x0 + (x1 - x0)(y - y0) / (y1 - y0), the edge's top in, its bottom out
            int64_t den = y1 - y0, num = x0 * den + int64_t(x1 - x0) * (y - y0);
            int64_t first = CeilDiv(num, 16 * den);
            int k = count++;
            for (; k > 0 && crossings[size_t(k) - 1] > first; k--)
                crossings[size_t(k)] = crossings[size_t(k) - 1];
            crossings[size_t(k)] = first;
        }
        for (int k = 0; k + 1 < count; k += 2)
        {
            int first = int(std::max<int64_t>(crossings[size_t(k)], 0)), end = int(std::min<int64_t>(crossings[size_t(k) + 1], canvas.Width()));
            if (!except)
                canvas.FillRow(row, first, end, colour);
            else
                for (int x = first; x < end; x++)
                    if (!except->contains(Key(x, row)))
                        canvas.Set(x, row, colour);
        }
    }
}

// ---- The 1-pixel pen ----

// Which way a line at 45 degrees counts as moved, by its direction (Dir): right and down when going
// right and down or left and up, else left and down. Found against Windows' pixels.
constexpr int kNudge[4][2] = {{2, 1}, {-2, 1}, {-2, 1}, {2, 1}};

int Dir(int64_t dx, int64_t dy) { return (dx > 0 ? 0 : 1) + (dy > 0 ? 0 : 2); }

int64_t Sign(int64_t v) { return v > 0 ? 1 : v < 0 ? -1 : 0; }

/// A point on a diamond's edge, for a line at 45 degrees: inside once the line is moved?
bool Nudged(int64_t x, int64_t y, int dir)
{
    int a = kNudge[dir][0], b = kNudge[dir][1];
    int64_t d = (x != 0 ? Sign(x) * a : std::abs(a)) + (y != 0 ? Sign(y) * b : std::abs(b));
    return d < 0;
}

/// A pixel's diamond: |x| + |y| below half a pixel, with its right and bottom corners; for a line at
/// 45 degrees, with the edge points the line's nudge takes inside.
bool InDiamond(int64_t x, int64_t y, bool diagonal, int dir)
{
    int64_t f = std::llabs(x) + std::llabs(y);
    return f < 8 || (f == 8 && (diagonal ? Nudged(x, y, dir) : (x == 8 && y == 0) || (x == 0 && y == 8)));
}

/// The pixel whose diamond holds the point, or none (a point on a diamond's edge).
std::pair<int64_t, int64_t> EndPixel(Point q, bool diagonal, int dir)
{
    int64_t cx = FloorDiv(q.X + 8, 16), cy = FloorDiv(q.Y + 8, 16);
    for (int64_t y = cy - 1; y <= cy + 1; y++)
        for (int64_t x = cx - 1; x <= cx + 1; x++)
            if (InDiamond(q.X - 16 * x, q.Y - 16 * y, diagonal, dir))
                return {x, y};
    return {INT64_MIN, INT64_MIN};
}

/// Whether the line from p by (dx, dy) meets the diamond of the pixel centred at c.
bool Meets(Point p, int64_t dx, int64_t dy, int64_t cx, int64_t cy, bool diagonal, int dir)
{
    int64_t x0 = p.X - cx, y0 = p.Y - cy;
    auto in = [&](int64_t n, int64_t d) {
        if (d < 0)
        {
            n = -n;
            d = -d;
        }
        int64_t x = x0 * d + dx * n, y = y0 * d + dy * n, f = std::llabs(x) + std::llabs(y);
        return f < 8 * d || (diagonal && f == 8 * d && Nudged(x, y, dir));
    };
    auto within = [](int64_t n, int64_t d) { return d > 0 ? n > 0 && n < d : n < 0 && n > d; };
    auto onLine = [&](int64_t x, int64_t y) {
        return dx * (y - y0) == dy * (x - x0) && std::min(x0, x0 + dx) <= x && x <= std::max(x0, x0 + dx) && std::min(y0, y0 + dy) <= y
               && y <= std::max(y0, y0 + dy);
    };
    // |x| + |y| along the line is smallest at an end or where x or y is 0 (t = n / d); where the
    // line only touches the diamond's edge, its points there include one of these
    if (in(0, 1) || in(1, 1))
        return true;
    if (dx != 0 && within(-x0, dx) && in(-x0, dx))
        return true;
    if (dy != 0 && within(-y0, dy) && in(-y0, dy))
        return true;
    if (diagonal)
        return false;
    // The corners that belong to the diamond, on the line
    return onLine(8, 0) || onLine(0, 8);
}

/// A cosmetic line from p to q (28.4): the pixels whose diamond it meets, but the one whose diamond
/// holds q. Each pixel near the line along its longer axis is tested exactly. A line at exactly 45
/// degrees can run along diamonds' edges: it counts as moved a little (kNudge).
void Line(Point p, Point q, std::vector<Point>& pixels, int width, int height)
{
    int64_t dx = q.X - p.X, dy = q.Y - p.Y;
    if (dx == 0 && dy == 0)
        return;
    bool diagonal = std::llabs(dx) == std::llabs(dy);
    int dir = Dir(dx, dy);
    auto [endX, endY] = EndPixel(q, diagonal, dir);
    auto tryPixel = [&](int64_t x, int64_t y) {
        if ((x != endX || y != endY) && Meets(p, dx, dy, 16 * x, 16 * y, diagonal, dir))
            pixels.push_back({int(x), int(y)});
    };
    if (std::llabs(dx) >= std::llabs(dy))
    {
        int64_t from = std::max<int64_t>(FloorDiv(std::min(p.X, q.X) - 8, 16), -1), to = std::min<int64_t>(CeilDiv(std::max(p.X, q.X) + 8, 16), width);
        for (int64_t x = from; x <= to; x++)
        {
            int64_t y = FloorDiv(p.Y + FloorDiv(dy * (16 * x - p.X), dx), 16);
            for (int64_t yy = y - 1; yy <= y + 2; yy++)
                tryPixel(x, yy);
        }
    }
    else
    {
        int64_t from = std::max<int64_t>(FloorDiv(std::min(p.Y, q.Y) - 8, 16), -1), to = std::min<int64_t>(CeilDiv(std::max(p.Y, q.Y) + 8, 16), height);
        for (int64_t y = from; y <= to; y++)
        {
            int64_t x = FloorDiv(p.X + FloorDiv(dx * (16 * y - p.Y), dy), 16);
            for (int64_t xx = x - 1; xx <= x + 2; xx++)
                tryPixel(xx, y);
        }
    }
}

/// The closed polygon's lines, each without its last pixel.
void Stroke(const Polygon& polygon, std::vector<Point>& pixels, int width, int height)
{
    size_t n = polygon.size();
    if (n == 1)
        return;
    for (size_t i = 0; i < n; i++)
    {
        Point p = polygon[i], q = polygon[(i + 1) % n];
        // Lines that cannot reach the picture
        if (std::max(p.X, q.X) < -16 || std::min(p.X, q.X) > 16 * width || std::max(p.Y, q.Y) < -16 || std::min(p.Y, q.Y) > 16 * height)
            continue;
        Line(p, q, pixels, width, height);
    }
}

} // namespace

void DibShapes::Ellipse(uint8_t* pixels, int width, int height, int stride, int bytesPerPixel, int l, int t, int r, int b, std::optional<int> brush,
                        std::optional<scripting::ScnPen> pen)
{
    if (bytesPerPixel != 3 && bytesPerPixel != 4)
        return;
    Canvas canvas(pixels, width, height, stride, bytesPerPixel);
    int penWidth = pen && pen->Style != PS_NULL ? std::max(1, pen->Width) : 0;
    if (l > r)
        std::swap(l, r);
    if (t > b)
        std::swap(t, b);
    if (l == r || t == b)
        return;
    if (pen && pen->Style == PS_INSIDEFRAME && penWidth > 1)
    {
        l += penWidth / 2;
        t += penWidth / 2;
        r -= (penWidth - 1) / 2;
        b -= (penWidth - 1) / 2;
    }

    if (penWidth == 0)
    {
        if (brush)
            Fill(canvas, Path(16 * l - 8, 16 * t - 8, 16 * (r - 1) - 8, 16 * (b - 1) - 8), *brush, nullptr);
        return;
    }
    Polygon path = Path(16 * l, 16 * t, 16 * (r - 1), 16 * (b - 1));
    std::unordered_set<uint64_t> wide;
    std::vector<Point> widePixels;
    if (penWidth > 1)
    {
        // A disc on each pixel of the 1-pixel outline
        std::vector<Point> line;
        Stroke(path, line, width, height);
        double r2 = penWidth * penWidth / 4.0;
        int reach = penWidth / 2 + 1;
        for (auto [cx, cy] : line)
            for (int dy = -reach; dy <= reach; dy++)
                for (int dx = -reach; dx <= reach; dx++)
                    if (dx * dx + dy * dy <= r2 && wide.insert(Key(cx + dx, cy + dy)).second)
                        widePixels.push_back({cx + dx, cy + dy});
    }
    if (brush)
        Fill(canvas, path, *brush, penWidth > 1 ? &wide : nullptr);
    int colour = pen->Color;
    if (penWidth > 1)
        for (auto [x, y] : widePixels)
            canvas.Set(x, y, colour);
    else
    {
        std::vector<Point> line;
        Stroke(path, line, width, height);
        for (auto [x, y] : line)
            canvas.Set(x, y, colour);
    }
}

} // namespace openshiina::platform
