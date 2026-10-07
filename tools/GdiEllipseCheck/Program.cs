// GdiEllipseCheck [random cases] [seed]: on Windows, draws ellipses with GDI itself (GdiShapes,
// what the WPF player uses) and with DibShapes (what the players without Windows use) and counts
// where they differ: every size up to 96 x 96 and circles up to 1100 across with the brush and
// the 1-pixel pen of one colour (EFCLIB's circle wipe), then random ellipses (cut by the picture's
// edges, corners swapped, 3 and 4 bytes a pixel, null brushes and pens, other colours, wide and
// inside-frame pens). The first differences of each kind go to gdi-ellipse-check.txt as pictures.
// GdiEllipseCheck show w h [pen width]: one ellipse, from both where GDI is there.

using System.Text;
using OpenShiina.Platform;
using OpenShiina.Scripting;

const int Margin = 4, Background = 0x5A;
var dib = new DibShapes();

if (args is ["show", var ws, var hs, ..])
{
    int w = int.Parse(ws), h = int.Parse(hs), penWidth = args.Length > 3 ? int.Parse(args[3]) : 1;
    var c = new Case("show", w + 2 * Margin, h + 2 * Margin, 4, Margin, Margin, Margin + w, Margin + h, 1, new ScnPen(0, penWidth, 2));
    Console.WriteLine(OperatingSystem.IsWindows() ? Compare(c).Picture : Picture(c, Draw(dib, c), null));
    return;
}
if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("GDI is Windows' own: run this on Windows (or 'show w h' for DibShapes alone).");
    return;
}

int randomCases = args.Length > 0 ? int.Parse(args[0]) : 5000;
var random = new Random(args.Length > 1 ? int.Parse(args[1]) : 1);
var report = new StringBuilder();
var totals = new SortedDictionary<string, (int Cases, int Differ, long Pixels)>();
var shown = new Dictionary<string, int>();

void Run(Case c)
{
    var (differ, picture) = Compare(c);
    var (cases, bad, pixels) = totals.GetValueOrDefault(c.Kind);
    totals[c.Kind] = (cases + 1, bad + (differ > 0 ? 1 : 0), pixels + differ);
    if (differ > 0 && shown.GetValueOrDefault(c.Kind) < 12)
    {
        shown[c.Kind] = shown.GetValueOrDefault(c.Kind) + 1;
        report.AppendLine($"{c}: {differ} pixels differ").AppendLine(picture);
    }
}

// EFCLIB: brush and 1-pixel pen of one grey
const int Grey = 0x808080;
for (int w = 1; w <= 96; w++)
    for (int h = 1; h <= 96; h++)
        Run(new Case("one colour, every size to 96", w + 2 * Margin, h + 2 * Margin, 4, Margin, Margin, Margin + w, Margin + h, Grey, new ScnPen(0, 1, Grey)));
for (int d = 97; d <= 1100; d++)
    Run(new Case("one colour, circles 97 to 1100", d + 2 * Margin, d + 2 * Margin, 4, Margin, Margin, Margin + d, Margin + d, Grey, new ScnPen(0, 1, Grey)));
// A circle cut by the picture, as the wipe's last ones are
for (int d = 400; d <= 1100; d += 7)
    Run(new Case("one colour, circles cut by the picture", 800, 600, 4, 400 - d / 2, 300 - d / 2, 400 - d / 2 + d, 300 - d / 2 + d, Grey, new ScnPen(0, 1, Grey)));

for (int k = 0; k < randomCases; k++)
{
    int w = random.Next(1, random.Next(2) == 0 ? 40 : 400), h = random.Next(2) == 0 ? w : random.Next(1, 400);
    int width = Math.Max(8, w + random.Next(-w / 2, 16)), height = Math.Max(8, h + random.Next(-h / 2, 16));
    int l = random.Next(-w / 3, width - w / 2), t = random.Next(-h / 3, height - h / 2);
    int r = l + w, b = t + h;
    if (random.Next(4) == 0)
        (l, r) = (r, l);
    if (random.Next(4) == 0)
        (t, b) = (b, t);
    int bytes = random.Next(2) == 0 ? 3 : 4;
    int? brush = random.Next(5) == 0 ? null : random.Next(0x1000000);
    ScnPen? pen = random.Next(10) switch
    {
        0 => null,
        1 => new ScnPen(5, 1, 0),
        2 => new ScnPen(0, 0, random.Next(0x1000000)),
        3 or 4 => new ScnPen(random.Next(2) == 0 ? 0 : 6, random.Next(2, 7), random.Next(0x1000000)),
        _ => new ScnPen(0, 1, random.Next(0x1000000)),
    };
    string kind = pen switch
    {
        null or { Style: 5 } => brush == null ? "random, nothing" : "random, brush alone",
        { Width: > 1, Style: 6 } => "random, wide inside-frame pen",
        { Width: > 1 } => "random, wide pen",
        _ => brush == null ? "random, 1-pixel pen alone" : "random, brush and 1-pixel pen",
    };
    Run(new Case(kind, width, height, bytes, l, t, r, b, brush, pen));
}

Console.WriteLine("Cases that differ from GDI, and their pixels:");
foreach (var (kind, (cases, bad, pixels)) in totals)
    Console.WriteLine($"  {kind}: {bad} of {cases} ({pixels} pixels)");
File.WriteAllText("gdi-ellipse-check.txt", report.Length > 0 ? report.ToString() : "No differences.\n");
Console.WriteLine($"Pictures of the first differences: {Path.GetFullPath("gdi-ellipse-check.txt")}");

(int Differ, string Picture) Compare(Case c)
{
    var gdi = Draw(new GdiShapes(), c);
    var ours = Draw(dib, c);
    int differ = 0;
    for (int p = 0; p < c.Width * c.Height; p++)
        if (!gdi.AsSpan(p * c.Bytes, c.Bytes).SequenceEqual(ours.AsSpan(p * c.Bytes, c.Bytes)))
            differ++;
    return (differ, Picture(c, ours, gdi));
}

static byte[] Draw(IScnShapes shapes, Case c)
{
    var pixels = new byte[c.Width * c.Height * c.Bytes];
    pixels.AsSpan().Fill(Background);
    shapes.Ellipse(pixels, c.Width, c.Height, c.Width * c.Bytes, c.Bytes, c.L, c.T, c.R, c.B, c.Brush, c.Pen);
    return pixels;
}

// Each pixel of the shape's box and a little around it: '.' brush, '#' pen, ' ' untouched,
// '?' another colour; where GDI differs, 'g' GDI alone drew there, 'o' only ours, 'x' both in other colours
static string Picture(Case c, byte[] ours, byte[]? gdi)
{
    char Of(byte[] px, int x, int y)
    {
        var p = px.AsSpan((y * c.Width + x) * c.Bytes, 3);
        int colour = p[2] | p[1] << 8 | p[0] << 16;
        bool untouched = p[0] == Background && p[1] == Background && p[2] == Background;
        return untouched ? ' ' : c.Pen is { } pen && colour == pen.Color ? '#' : colour == c.Brush ? '.' : '?';
    }
    int x0 = Math.Max(0, Math.Min(c.L, c.R) - 2), x1 = Math.Min(c.Width, Math.Max(c.L, c.R) + 2);
    int y0 = Math.Max(0, Math.Min(c.T, c.B) - 2), y1 = Math.Min(c.Height, Math.Max(c.T, c.B) + 2);
    if (x1 - x0 > 160 || y1 - y0 > 120)
        return "  (too big to show)";
    var text = new StringBuilder();
    for (int y = y0; y < y1; y++)
    {
        text.Append("  ");
        for (int x = x0; x < x1; x++)
        {
            char mine = Of(ours, x, y);
            if (gdi == null)
            {
                text.Append(mine);
                continue;
            }
            int at = (y * c.Width + x) * c.Bytes;
            char theirs = Of(gdi, x, y);
            text.Append(gdi.AsSpan(at, c.Bytes).SequenceEqual(ours.AsSpan(at, c.Bytes)) ? mine
                : mine == ' ' ? 'g' : theirs == ' ' ? 'o' : 'x');
        }
        text.AppendLine();
    }
    return text.ToString();
}

record Case(string Kind, int Width, int Height, int Bytes, int L, int T, int R, int B, int? Brush, ScnPen? Pen)
{
    public override string ToString() =>
        $"Ellipse({L}, {T}, {R}, {B}) on {Width}x{Height}x{Bytes * 8}, brush {(Brush is { } b ? b.ToString("X6") : "null")}, " +
        $"pen {(Pen is { } p ? $"style {p.Style} width {p.Width} {p.Color:X6}" : "null")}";
}
