// The games' .exe icons for the home screen, read once per .exe on a pool thread (a phone reads
// each .exe, about 1 MB, from its shared storage), with each icon's own colour for the cards: of
// its opaque pixels, the hue that has most of the vivid ones (an average of them all would be
// mud: blue and yellow make grey), their average; none for a grey icon.

using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenShiina.Formats;
using SkiaSharp;

namespace OpenShiina.App;

public static class GameIcons
{
    private static readonly Dictionary<string, Task<(Bitmap? Icon, Color? Colour)>> s_icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon of <paramref name="exe"/> and its colour (nulls when it has none).</summary>
    public static Task<(Bitmap? Icon, Color? Colour)> Of(string? exe)
    {
        if (exe == null)
            return Task.FromResult<(Bitmap?, Color?)>((null, null));
        lock (s_icons)
        {
            if (!s_icons.TryGetValue(exe, out var task))
                s_icons[exe] = task = Task.Run(() => Read(exe));
            return task;
        }
    }

    private static (Bitmap?, Color?) Read(string exe)
    {
        try
        {
            if (!File.Exists(exe) || ExeIcon.Read(exe) is not { } ico)
                return (null, null);
            return (new Bitmap(new MemoryStream(ico)), Colour(ico));
        }
        catch (Exception)
        {
            // No icon: the card keeps the name's first letter
            return (null, null);
        }
    }

    private const int Hues = 12;

    private static Color? Colour(byte[] ico)
    {
        using var bitmap = SKBitmap.Decode(ico);
        if (bitmap == null)
            return null;
        var weights = new double[Hues];
        var sums = new (double R, double G, double B)[Hues];
        int step = Math.Max(1, Math.Max(bitmap.Width, bitmap.Height) / 48);
        for (int y = 0; y < bitmap.Height; y += step)
            for (int x = 0; x < bitmap.Width; x += step)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Alpha < 128)
                    continue;
                int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                if (max < 40)
                    continue;
                double saturation = (max - min) / (double)max;
                if (saturation < 0.2)
                    continue;
                c.ToHsv(out float hue, out _, out _);
                int bucket = (int)(hue / (360f / Hues)) % Hues;
                double weight = saturation * saturation * (max / 255.0);
                weights[bucket] += weight;
                sums[bucket].R += c.Red * weight;
                sums[bucket].G += c.Green * weight;
                sums[bucket].B += c.Blue * weight;
            }
        int best = Array.IndexOf(weights, weights.Max());
        // A grey icon (or one with a dot of colour): the theme's own surface
        if (weights[best] < 0.5)
            return null;
        var (r, g, b) = sums[best];
        double w = weights[best];
        return Color.FromRgb((byte)Math.Round(r / w), (byte)Math.Round(g / w), (byte)Math.Round(b / w));
    }
}
