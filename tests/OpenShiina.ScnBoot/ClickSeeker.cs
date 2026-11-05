// SCNBOOT_SWEEP_CLICKS=auto: where to click when the grid gets the game nowhere (a map of places,
// a calendar, a menu whose items are not where the grid clicks). The games tell their buttons
// from the mouse two ways, and both are watched:
//   - a list of sprites, 04C7 giving the value of the one under the mouse: its entries with a
//     value are buttons, their frames' rectangles where they are (ScnVm.HitTargets);
//   - the scripts' own comparisons of the mouse's x and y (0456) with a button's edges, in `if`s
//     (ScnVm.CompareSeen): a value compared with the mouse's x or y while the mouse is at a place
//     of odd numbers (seldom another variable's value) is an edge, lower when the mouse has to be
//     at or past it, upper when before it. Only the comparisons the scripts reach are seen (a
//     button's y is often compared only when its x matched), so each click shows more edges.
// The places to click: the buttons' middles (the largest first, not one of more than 15% of the
// screen), then the middles of the boxes the edges make (each
// lower edge with the nearest upper one past it, x by y), each once; between them, places of a
// grid of odd numbers, where the scripts' comparisons come to light. When the grid's places have
// all been tried once, the places are tried again.

sealed class ClickSeeker(int width, int height)
{
    private readonly HashSet<(int Axis, int Value, bool Lower)> m_edges = new();
    private readonly HashSet<(int, int)> m_tried = new();
    private readonly List<(int X, int Y)> m_probes = Probes(width, height);
    private int m_probe, m_since = -1;

    /// <summary>The comparison of an `if` (a, b, cmp as ScnVm.CompareSeen) while the mouse is at <paramref name="mouse"/>.</summary>
    public void Compared((int X, int Y) mouse, int a, int b, int cmp)
    {
        if (m_since < 0 || cmp is < 2 or > 5 || mouse.X == mouse.Y)
            return;
        for (int axis = 0; axis < 2; axis++)
        {
            int m = axis == 0 ? mouse.X : mouse.Y, limit = axis == 0 ? width : height;
            // mouse >= b, mouse > b: b is a lower edge; mouse <= b, mouse < b: an upper one
            if (a == m && b != m && b >= 0 && b <= limit)
                m_edges.Add((axis, b, cmp is 2 or 3));
            else if (b == m && a != m && a >= 0 && a <= limit)
                m_edges.Add((axis, a, cmp is 4 or 5));
        }
    }

    /// <summary>The next place to click, nothing new having come for a while.</summary>
    public (int X, int Y) Next(List<(int Value, int Left, int Top, int Width, int Height)> buttons, int frame)
    {
        if (m_since < 0)
        {
            m_since = frame;
            Console.WriteLine($"  [auto] frame {frame}: nothing new, looking for buttons");
        }
        // The largest first, but none of more than 15% of the screen (a background, the message
        // window): a menu's items are larger than the small buttons of the message window (Q-LOAD,
        // which takes the game back to its quick save: Tekoire)
        foreach (var (value, l, t, w, h) in buttons.Where(b => (long)b.Width * b.Height * 100 <= (long)width * height * 15)
                     .OrderByDescending(b => (long)b.Width * b.Height))
            if (Try((l + w / 2, t + h / 2)) is { } place)
            {
                Console.WriteLine($"  [auto] frame {frame}: button {value} at {place.X},{place.Y} ({l},{t} {w}x{h})");
                return place;
            }
        var xs = Middles(0, width);
        var ys = Middles(1, height);
        foreach (int y in ys)
            foreach (int x in xs)
                if (Try((x, y)) is { } place)
                {
                    Console.WriteLine($"  [auto] frame {frame}: box at {place.X},{place.Y} (edges x {Edges(0)}; y {Edges(1)})");
                    return place;
                }
        // A place of the grid, where comparisons come to light; once all have been, everything again
        if (m_probe > 0 && m_probe % m_probes.Count == 0)
            m_tried.Clear();
        var probe = m_probes[m_probe++ % m_probes.Count];
        return probe;
    }

    /// <summary>Something new came: the edges and places tried are of the screen left.</summary>
    public void Stop(int frame)
    {
        if (m_since < 0)
            return;
        Console.WriteLine($"  [auto] frame {frame}: something new after {frame - m_since} frames");
        m_since = -1;
        m_edges.Clear();
        m_tried.Clear();
        m_probe = 0;
    }

    private (int X, int Y)? Try((int X, int Y) place)
    {
        if (place.X < 0 || place.Y < 0 || place.X >= width || place.Y >= height)
            return null;
        // the mouse's x and y must differ, to tell which a comparison is of
        if (place.X == place.Y)
            place.X++;
        return m_tried.Add((place.X / 6, place.Y / 6)) ? place : null;
    }

    // The middle of each box an axis's edges make: a lower edge with the nearest upper one past it
    // (a button is not larger than half the screen), or a little past a lower edge without one
    private List<int> Middles(int axis, int limit)
    {
        var lower = m_edges.Where(e => e.Axis == axis && e.Lower).Select(e => e.Value).Distinct().Order().ToList();
        var upper = m_edges.Where(e => e.Axis == axis && !e.Lower).Select(e => e.Value).Distinct().Order().ToList();
        var middles = new List<int>();
        foreach (int l in lower)
            middles.Add(upper.FirstOrDefault(u => u > l && u - l <= limit / 2, -1) is int u and >= 0 ? (l + u) / 2 : l + 6);
        // the other axis's boxes with this one unknown: across the screen
        if (middles.Count == 0 && m_edges.Any(e => e.Axis != axis))
            middles.AddRange(m_probes.Select(p => axis == 0 ? p.X : p.Y).Distinct());
        return middles;
    }

    private string Edges(int axis) => string.Join(" ", m_edges.Where(e => e.Axis == axis).OrderBy(e => e.Value).Select(e => $"{(e.Lower ? ">=" : "<")}{e.Value}"));

    // A grid of 7 x 5 places of odd numbers, from the middle out
    private static List<(int X, int Y)> Probes(int width, int height)
    {
        var places = from y in new[] { 50, 30, 70, 15, 85 }
                     from x in new[] { 50, 35, 65, 20, 80, 8, 92 }
                     select (X: width * x / 100 | 1, Y: height * y / 100 | 1);
        return places.Select(p => p.X == p.Y ? (p.X + 2, p.Y) : p).ToList();
    }
}
