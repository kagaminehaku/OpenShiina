// LogoGen [repository folder] [--font cinzel|playfair] [--preview folder]: OpenShiina's logo,
// drawn from its shapes. The icon is a forbidden book: wine-red cover, a leather strap and a gold
// lock whose keyhole is a shii nut (椎, the "Shii" of Shiina) - a locked book to open. OpenShiina
// Tools' icon has a goose quill beside it, its nib red. The wordmark spells "OpenShiina" with S,
// i, n in red: the sin hidden in the name; its letters are those of an SIL OFL font in Fonts/
// (Cinzel by default, Playfair Display), made outlines. Writes:
//   docs/logo/openshiina.svg, openshiina-small.svg   the icon (256 units), and its plainer
//                                                    version for 32 px and less
//   docs/logo/tools.svg                              OpenShiina Tools' icon
//   docs/logo/wordmark-light.svg, wordmark-dark.svg  icon and name, for light and dark pages
//   docs/logo/openshiina-512.png, tools-512.png, wordmark-*.png
//   src/OpenShiina.Desktop/Resources/icon.ico        16-256 px (the small version up to 32)
//   src/OpenShiina.Tools/Resources/icon.ico, icon.png  the tools' icon, and their windows'
//   src/OpenShiina.App/Resources/icon.png            256 px, the player's windows' icon
//   src/OpenShiina.Android/Resources/mipmap-*/icon.png
// --preview: only the wordmarks in both fonts, into that folder, to compare.

using System.Globalization;
using SkiaSharp;

string? Option(string name) => Array.IndexOf(args, name) is >= 0 and int at && at + 1 < args.Length ? args[at + 1] : null;
string root = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
if (!File.Exists(Path.Combine(root, "OpenShiina.slnx")))
    throw new DirectoryNotFoundException($"No OpenShiina repository at {root}");
string fonts = Path.Combine(root, "tools", "LogoGen", "Fonts");
var faces = new Dictionary<string, string> { ["cinzel"] = "Cinzel-Bold.ttf", ["playfair"] = "PlayfairDisplay-Bold.ttf" };
var full = Logo.Icon(small: false);
var small = Logo.Icon(small: true);
(string Name, string Ink, string Red)[] themes = [("light", "#2B1A20", "#C8102E"), ("dark", "#F3E6C8", "#E23B4E")];

if (Option("--preview") is { } preview)
{
    Directory.CreateDirectory(preview);
    foreach (var (key, file) in faces)
        foreach (var (name, ink, red) in themes)
        {
            var (shapes, width) = Logo.Wordmark(full, Path.Combine(fonts, file), ink, red);
            File.WriteAllBytes(Path.Combine(preview, $"wordmark-{key}-{name}.png"), Logo.Png(shapes, (int)Math.Ceiling(width), 256, 1));
        }
    File.WriteAllBytes(Path.Combine(preview, "tools-512.png"), Logo.Png(Logo.Quill(full, small: false), 512));
    File.WriteAllBytes(Path.Combine(preview, "tools-32.png"), Logo.Png(Logo.Quill(small, small: true), 32));
    Console.WriteLine($"Wordmarks in both fonts and the tools' icon written into {preview}.");
    return;
}

string font = Path.Combine(fonts, faces[Option("--font") ?? "cinzel"]);
string logo = Path.Combine(root, "docs", "logo");
Directory.CreateDirectory(logo);
var tools = Logo.Quill(full, small: false);
var toolsSmall = Logo.Quill(small, small: true);
File.WriteAllText(Path.Combine(logo, "openshiina.svg"), Logo.Svg(full, 256, 256));
File.WriteAllText(Path.Combine(logo, "openshiina-small.svg"), Logo.Svg(small, 256, 256));
File.WriteAllText(Path.Combine(logo, "tools.svg"), Logo.Svg(tools, 256, 256));
File.WriteAllBytes(Path.Combine(logo, "openshiina-512.png"), Logo.Png(full, 512));
File.WriteAllBytes(Path.Combine(logo, "tools-512.png"), Logo.Png(tools, 512));

foreach (var (name, ink, red) in themes)
{
    var (shapes, width) = Logo.Wordmark(full, font, ink, red);
    File.WriteAllText(Path.Combine(logo, $"wordmark-{name}.svg"), Logo.Svg(shapes, width, 256));
    File.WriteAllBytes(Path.Combine(logo, $"wordmark-{name}.png"), Logo.Png(shapes, (int)Math.Ceiling(width), 256, 1));
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
File.WriteAllBytes(Path.Combine(root, "src", "OpenShiina.Desktop", "Resources", "icon.ico"),
    Logo.Ico(sizes.Select(s => (s, Logo.Png(s <= 32 ? small : full, s))).ToList()));
string toolsResources = Path.Combine(root, "src", "OpenShiina.Tools", "Resources");
Directory.CreateDirectory(toolsResources);
File.WriteAllBytes(Path.Combine(toolsResources, "icon.ico"), Logo.Ico(sizes.Select(s => (s, Logo.Png(s <= 32 ? toolsSmall : tools, s))).ToList()));
File.WriteAllBytes(Path.Combine(toolsResources, "icon.png"), Logo.Png(tools, 256));

Directory.CreateDirectory(Path.Combine(root, "src", "OpenShiina.App", "Resources"));
File.WriteAllBytes(Path.Combine(root, "src", "OpenShiina.App", "Resources", "icon.png"), Logo.Png(full, 256));

foreach (var (folder, size) in new[] { ("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192) })
    File.WriteAllBytes(Path.Combine(root, "src", "OpenShiina.Android", "Resources", $"mipmap-{folder}", "icon.png"), Logo.Png(full, size));

Console.WriteLine($"Logo written into {logo}, the player's and the tools' icons and the Android icons.");

/// <summary>A filled (or stroked) SVG path.</summary>
internal sealed record Shape(string Path, string Fill, string? Stroke = null, float Width = 0);

internal static class Logo
{
    // The colours: the cover, the spine, the pages, the gold, the gold's shade, the leather
    private const string Night = "#1A0A10", Cover = "#6B1024", Spine = "#4A0A18", Pages = "#F3E6C8",
        PageLine = "#D6C49C", Gold = "#D4A649", GoldShade = "#A9792B", Leather = "#2A0A12";

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A rectangle with round corners as a path.</summary>
    public static string Rect(float x, float y, float w, float h, float r)
    {
        if (r <= 0)
            return $"M{F(x)} {F(y)}H{F(x + w)}V{F(y + h)}H{F(x)}Z";
        return $"M{F(x + r)} {F(y)}H{F(x + w - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w)} {F(y + r)}V{F(y + h - r)}" +
               $"A{F(r)} {F(r)} 0 0 1 {F(x + w - r)} {F(y + h)}H{F(x + r)}A{F(r)} {F(r)} 0 0 1 {F(x)} {F(y + h - r)}" +
               $"V{F(y + r)}A{F(r)} {F(r)} 0 0 1 {F(x + r)} {F(y)}Z";
    }

    /// <summary>A shii nut (pointed, its cup below) as a path, its tip at (cx, top), <paramref name="h"/> high.</summary>
    private static string Nut(float cx, float top, float h)
    {
        float w = h * 0.62f, body = top + h * 0.78f;
        return $"M{F(cx)} {F(top)}C{F(cx + w * 0.62f)} {F(top + h * 0.30f)} {F(cx + w * 0.62f)} {F(body)} {F(cx)} {F(body + h * 0.04f)}" +
               $"C{F(cx - w * 0.62f)} {F(body)} {F(cx - w * 0.62f)} {F(top + h * 0.30f)} {F(cx)} {F(top)}Z" +
               // The cup: a bowl under the body
               $"M{F(cx - w * 0.52f)} {F(top + h * 0.70f)}Q{F(cx)} {F(top + h * 1.12f)} {F(cx + w * 0.52f)} {F(top + h * 0.70f)}Z";
    }

    /// <summary>The icon on a 256 x 256 square; <paramref name="small"/>: fewer, larger parts for 32 px and less.</summary>
    public static List<Shape> Icon(bool small)
    {
        var shapes = new List<Shape>
        {
            new(Rect(0, 0, 256, 256, 56), Night),
            // The pages, out on the right of the cover
            new(Rect(174, 44, 26, 168, 5), Pages),
        };
        if (!small)
            shapes.Add(new Shape("M192 50V206M196 52V204", "none", PageLine, 1.5f));
        shapes.Add(new(Rect(60, 34, 130, 188, 10), Cover));
        shapes.Add(new(Rect(60, 34, 22, 188, 10), Spine));
        shapes.Add(new(Rect(72, 34, 10, 188, 0), Spine));
        if (!small)
        {
            // The gold frame and the corner guards
            shapes.Add(new(Rect(94, 50, 82, 156, 6), "none", Gold, 3));
            shapes.Add(new("M190 34H168L190 56Z", Gold));
            shapes.Add(new("M190 222H168L190 200Z", Gold));
        }
        // The strap, over the pages, and the lock with its keyhole
        float plateX = small ? 100 : 110, plateY = small ? 88 : 98, plateW = small ? 72 : 54, plateH = small ? 80 : 62;
        float strapH = small ? 26 : 20, strapY = plateY + plateH / 2 - strapH / 2;
        shapes.Add(new(Rect(plateX + plateW / 2, strapY, 206 - plateX - plateW / 2, strapH, 3), Leather));
        shapes.Add(new(Rect(plateX, plateY, plateW, plateH, small ? 10 : 8), Gold));
        if (!small)
            shapes.Add(new(Rect(plateX + 5, plateY + 5, plateW - 10, plateH - 10, 5), "none", GoldShade, 2));
        float nutH = small ? 54 : 38;
        shapes.Add(new(Nut(plateX + plateW / 2, plateY + (plateH - nutH * 1.04f) / 2 - 1, nutH), Leather));
        return shapes;
    }

    /// <summary>
    /// The tools' icon: <paramref name="icon"/> with a goose quill leaning on the book's right side,
    /// its nib red; <paramref name="small"/>: a plainer, wider feather.
    /// </summary>
    public static List<Shape> Quill(List<Shape> icon, bool small)
    {
        // Along the line from the feather's tip T to the nib N, n across it (to the right)
        (float X, float Y) T = (232, 20), N = (166, 242);
        float dx = N.X - T.X, dy = N.Y - T.Y, length = MathF.Sqrt(dx * dx + dy * dy);
        (float X, float Y) n = (-dy / length, dx / length);
        if (n.X < 0)
            n = (-n.X, -n.Y);
        (float X, float Y) At(float t, float across = 0) => (T.X + dx * t + n.X * across, T.Y + dy * t + n.Y * across);
        string P((float X, float Y) p) => $"{F(p.X)} {F(p.Y)}";
        float wide = small ? 1.45f : 1;
        var vaneEnd = At(0.70f);
        var shapes = new List<Shape>(icon)
        {
            // The vane: wider on the right
            new($"M{P(T)}Q{P(At(0.26f, 30 * wide))} {P(vaneEnd)}Q{P(At(0.30f, -22 * wide))} {P(T)}Z", "#FBF6EC"),
        };
        if (!small)
            shapes.Add(new($"M{P(At(0.04f))}L{P(vaneEnd)}", "none", "#C9B49A", 2.5f));
        // The bare shaft below the vane, then the nib, its point red
        shapes.Add(new($"M{P(vaneEnd)}L{P(At(0.86f))}", "none", "#EDE3CF", small ? 7 : 5));
        shapes.Add(new($"M{P(At(0.86f, 4.5f))}L{P(N)}L{P(At(0.86f, -4.5f))}Z", "#2A0A12"));
        shapes.Add(new($"M{P(At(0.95f, 2.4f))}L{P(N)}L{P(At(0.95f, -2.4f))}Z", "#C8102E"));
        return shapes;
    }

    /// <summary>
    /// The icon and "OpenShiina" beside it in the font <paramref name="fontFile"/> (S, i, n in
    /// <paramref name="red"/>), its capitals centred on the icon; and the whole width.
    /// </summary>
    public static (List<Shape> Shapes, float Width) Wordmark(List<Shape> icon, string fontFile, string ink, string red)
    {
        var shapes = new List<Shape>(icon);
        using var typeface = SKTypeface.FromFile(fontFile) ?? throw new FileNotFoundException("No font", fontFile);
        using var font = new SKFont(typeface, 100);
        // Capitals 116 units high, centred on the icon
        font.Size = 100 * 116 / font.Metrics.CapHeight;
        float cap = font.Metrics.CapHeight;
        const string text = "OpenShiina";
        // S, the first i, n: the letters of "sin"
        bool[] sin = [false, false, false, false, true, false, true, false, true, false];
        var glyphs = font.GetGlyphs(text);
        float x0 = 300, baseline = 128 + cap / 2;
        var positions = font.GetGlyphPositions(glyphs, new SKPoint(x0, baseline));
        float right = x0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            using var path = font.GetGlyphPath(glyphs[i]);
            path.Offset(positions[i]);
            shapes.Add(new Shape(path.ToSvgPathData(), sin[i] ? red : ink));
            right = Math.Max(right, path.Bounds.Right);
        }
        return (shapes, right + 24);
    }

    public static string Svg(List<Shape> shapes, float width, float height)
    {
        var svg = new System.Text.StringBuilder(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F(width)} {F(height)}\" width=\"{F(width)}\" height=\"{F(height)}\">\n");
        foreach (var shape in shapes)
        {
            svg.Append($"  <path d=\"{shape.Path}\" fill=\"{shape.Fill}\"");
            if (shape.Stroke != null)
                svg.Append($" stroke=\"{shape.Stroke}\" stroke-width=\"{F(shape.Width)}\" stroke-linecap=\"round\"");
            svg.Append("/>\n");
        }
        return svg.Append("</svg>\n").ToString();
    }

    /// <summary>A square PNG of the 256-unit icon, <paramref name="size"/> pixels a side.</summary>
    public static byte[] Png(List<Shape> shapes, int size) => Png(shapes, size, size, size / 256f);

    public static byte[] Png(List<Shape> shapes, int width, int height, float scale)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        foreach (var shape in shapes)
        {
            using var path = SKPath.ParseSvgPathData(shape.Path);
            if (shape.Fill != "none")
            {
                using var fill = new SKPaint { IsAntialias = true, Color = SKColor.Parse(shape.Fill), Style = SKPaintStyle.Fill };
                canvas.DrawPath(path, fill);
            }
            if (shape.Stroke != null)
            {
                using var stroke = new SKPaint
                {
                    IsAntialias = true, Color = SKColor.Parse(shape.Stroke), Style = SKPaintStyle.Stroke,
                    StrokeWidth = shape.Width, StrokeCap = SKStrokeCap.Round,
                };
                canvas.DrawPath(path, stroke);
            }
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A Windows icon of PNG images (Windows Vista and later read them).</summary>
    public static byte[] Ico(List<(int Size, byte[] Png)> images)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images)
            writer.Write(png);
        return stream.ToArray();
    }
}
