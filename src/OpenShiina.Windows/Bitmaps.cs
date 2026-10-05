// WPF bitmaps for the engine's PixelImage pictures, and back.

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenShiina.Windows;

public static class Bitmaps
{
    /// <summary>
    /// The picture as a frozen WPF bitmap. The bitmap is kept in <see cref="PixelImage.Native"/>,
    /// so each picture is converted once. Safe to call from any thread.
    /// </summary>
    public static BitmapSource ToBitmapSource(this PixelImage image)
    {
        if (image.Native is BitmapSource cached)
            return cached;
        var format = image.Layout == PixelLayout.Bgra32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, format, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        image.Native = bitmap;
        return bitmap;
    }

    /// <summary>A WPF bitmap as straight-alpha BGRA pixels.</summary>
    public static PixelImage ToPixelImage(this BitmapSource bitmap)
    {
        var bgra = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return new PixelImage(bgra.PixelWidth, bgra.PixelHeight, PixelLayout.Bgra32, pixels);
    }

    /// <summary>A PNG (or other image file) read into a frozen bitmap, or null when it cannot be read.</summary>
    public static BitmapSource? Decode(byte[] file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The picture as PNG file bytes, written by WPF's encoder.</summary>
    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
