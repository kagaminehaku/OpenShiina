// A decoded picture as plain pixels, so that decoders and the story engine need no UI toolkit.
// Each front end turns it into its own bitmap (the WPF app: UI/Bitmaps.cs) and may keep that
// bitmap in Native, so the conversion runs once per picture.

namespace OpenShiina.Formats;

public enum PixelLayout
{
    /// <summary>4 bytes a pixel: blue, green, red, alpha (straight, not premultiplied).</summary>
    Bgra32,
    /// <summary>3 bytes a pixel: blue, green, red.</summary>
    Bgr24,
}

public sealed class PixelImage
{
    public int Width { get; }
    public int Height { get; }
    public PixelLayout Layout { get; }
    public byte[] Pixels { get; }

    /// <summary>The front end's bitmap made from these pixels, once it has made one.</summary>
    public object? Native { get; set; }

    public PixelImage(int width, int height, PixelLayout layout, byte[] pixels)
    {
        if (pixels.Length < width * height * BytesPerPixel(layout))
            throw new ArgumentException("Not enough pixel data for the size.", nameof(pixels));
        Width = width;
        Height = height;
        Layout = layout;
        Pixels = pixels;
    }

    /// <summary>A transparent BGRA picture.</summary>
    public PixelImage(int width, int height) : this(width, height, PixelLayout.Bgra32, new byte[width * height * 4])
    {
    }

    public static int BytesPerPixel(PixelLayout layout) => layout == PixelLayout.Bgra32 ? 4 : 3;

    public int Stride => Width * BytesPerPixel(Layout);

    /// <summary>
    /// Alpha-blends <paramref name="source"/> (BGRA) over this picture (BGRA) with its top-left
    /// corner at (x, y); parts outside are left out.
    /// </summary>
    public void DrawOver(PixelImage source, int x, int y)
    {
        if (Layout != PixelLayout.Bgra32 || source.Layout != PixelLayout.Bgra32)
            throw new NotSupportedException("Only BGRA pictures are blended.");
        int x0 = Math.Max(0, -x), x1 = Math.Min(source.Width, Width - x);
        int y0 = Math.Max(0, -y), y1 = Math.Min(source.Height, Height - y);
        byte[] src = source.Pixels, dst = Pixels;
        for (int sy = y0; sy < y1; sy++)
        {
            int s = (sy * source.Width + x0) * 4;
            int d = ((y + sy) * Width + x + x0) * 4;
            for (int sx = x0; sx < x1; sx++, s += 4, d += 4)
                BlendPixel(src, s, dst, d);
        }
    }

    /// <summary>"Over" for one straight-alpha BGRA pixel, colours weighted by their alpha.</summary>
    internal static void BlendPixel(byte[] src, int s, byte[] dst, int d)
    {
        int sa = src[s + 3];
        if (sa == 0)
            return;
        int da = dst[d + 3];
        if (sa == 255 || da == 0)
        {
            dst[d] = src[s];
            dst[d + 1] = src[s + 1];
            dst[d + 2] = src[s + 2];
            dst[d + 3] = (byte)sa;
            return;
        }
        // out = src + dst * (1 - srcA)
        int dw = da * (255 - sa) / 255;
        int oa = sa + dw;
        dst[d] = (byte)((src[s] * sa + dst[d] * dw) / oa);
        dst[d + 1] = (byte)((src[s + 1] * sa + dst[d + 1] * dw) / oa);
        dst[d + 2] = (byte)((src[s + 2] * sa + dst[d + 2] * dw) / oa);
        dst[d + 3] = (byte)oa;
    }
}
