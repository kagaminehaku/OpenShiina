// The game's picture in the Avalonia player, drawn with SkiaSharp as the setting "Scaling" says
// (Core Game/PictureScaling.cs): nearest-neighbour (to the view, or at its whole scale), bicubic
// (Catmull-Rom), or sharp: the "sharp bilinear" runtime shader (SkSL), which snaps the sampling
// point within each game pixel so that only the screen pixels on its edge mix with the next one
// (Skia runs it on the GPU where it draws on one, on the CPU otherwise). The picture is kept as an
// SKImage made from each new frame, shared with the draw operations the render thread runs.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using OpenShiina.Game;
using SkiaSharp;

namespace OpenShiina.App;

public sealed class GamePicture : Control
{
    private SharedImage? m_image;
    private int m_width, m_height;

    public GamePicture()
    {
        // The whole view takes the pointer (black around the picture included)
        Background = Brushes.Transparent;
    }

    /// <summary>The view's background (transparent: the pointer is taken all over it).</summary>
    public IBrush? Background { get; set; }

    /// <summary>How the picture is scaled to the view.</summary>
    public Scaling Scaling { get; set; } = Scaling.Sharp;

    /// <summary>A new picture: BGRA pixels, rows stride bytes apart (copied).</summary>
    public unsafe void SetFrame(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        SKImage? image;
        fixed (byte* p = pixels)
            image = SKImage.FromPixelCopy(info, (nint)p, stride);
        if (image == null)
            return;
        var old = m_image;
        m_image = new SharedImage(image);
        old?.Release();
        m_width = width;
        m_height = height;
        InvalidateVisual();
    }

    /// <summary>No picture (the game ended).</summary>
    public void Clear()
    {
        m_image?.Release();
        m_image = null;
        InvalidateVisual();
    }

    /// <summary>
    /// The picture's scale and top left corner in the view's coordinates (device-independent
    /// pixels), as Scaling places it on the screen's pixels.
    /// </summary>
    public (double Scale, double Left, double Top) Placement()
    {
        double device = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var (scale, left, top) = PictureScaling.Placement(Scaling, m_width, m_height, Bounds.Width * device, Bounds.Height * device);
        return (scale / device, left / device, top / device);
    }

    public override void Render(DrawingContext context)
    {
        if (Background != null)
            context.FillRectangle(Background, new Rect(Bounds.Size));
        if (m_image is not { } image || m_width == 0)
            return;
        var (scale, left, top) = Placement();
        if (scale <= 0)
            return;
        double device = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var target = new Rect(left, top, m_width * scale, m_height * scale);
        context.Custom(new DrawPicture(new Rect(Bounds.Size), target, image, Scaling, scale * device));
    }

    /// <summary>An SKImage the control and the draw operations hold; disposed when none does.</summary>
    private sealed class SharedImage(SKImage image)
    {
        private int m_holders = 1;

        public SKImage Image { get; } = image;

        public bool TryHold()
        {
            int n;
            do
            {
                n = Volatile.Read(ref m_holders);
                if (n == 0)
                    return false;
            }
            while (Interlocked.CompareExchange(ref m_holders, n + 1, n) != n);
            return true;
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref m_holders) == 0)
                Image.Dispose();
        }
    }

    /// <summary>The picture drawn with SkiaSharp on the render thread.</summary>
    private sealed class DrawPicture : ICustomDrawOperation
    {
        private readonly Rect m_target;
        private readonly SharedImage? m_image;
        private readonly Scaling m_scaling;
        private readonly double m_pixels;

        public DrawPicture(Rect bounds, Rect target, SharedImage image, Scaling scaling, double pixels)
        {
            Bounds = bounds;
            m_target = target;
            m_image = image.TryHold() ? image : null;
            m_scaling = scaling;
            m_pixels = pixels;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() => m_image?.Release();

        public void Render(ImmediateDrawingContext context)
        {
            if (m_image == null || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var image = m_image.Image;
            var target = new SKRect((float)m_target.Left, (float)m_target.Top, (float)m_target.Right, (float)m_target.Bottom);
            switch (m_scaling)
            {
                case Scaling.Smooth:
                    canvas.DrawImage(image, target, new SKSamplingOptions(SKCubicResampler.CatmullRom));
                    return;
                case Scaling.Sharp when m_pixels > 1 + 1e-6 && SharpShader.Effect is { } effect:
                {
                    using var picture = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    var builder = new SKRuntimeShaderBuilder(effect);
                    builder.Uniforms["origin"] = new SKPoint(target.Left, target.Top);
                    builder.Uniforms["unit"] = target.Width / image.Width;
                    builder.Uniforms["pixels"] = (float)m_pixels;
                    builder.Children["picture"] = picture;
                    using var shader = builder.Build();
                    using var paint = new SKPaint { Shader = shader };
                    canvas.DrawRect(target, paint);
                    return;
                }
                case Scaling.Sharp:
                    // shown at most x1 (or no runtime shaders): filtered
                    canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    return;
                default:
                    canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                    return;
            }
        }
    }

    /// <summary>
    /// The sharp bilinear shader: in game pixels, a screen pixel inside a game pixel samples its
    /// middle; on its edges (the last pixels / pixel units of it) the sampling point slides to the
    /// next game pixel, which linear filtering mixes in.
    /// </summary>
    private static class SharpShader
    {
        private const string Source = """
            uniform shader picture;
            uniform float2 origin;   // the picture's top left corner (view units)
            uniform float unit;      // view units a game pixel
            uniform float pixels;    // screen pixels a game pixel
            half4 main(float2 p) {
                float2 texel = (p - origin) / unit;
                float2 cell = floor(texel);
                float2 d = fract(texel) - 0.5;
                float range = 0.5 - 0.5 / pixels;
                float2 f = (d - clamp(d, -range, range)) * pixels + 0.5;
                return picture.eval(cell + f);
            }
            """;

        public static SKRuntimeEffect? Effect { get; } = SKRuntimeEffect.CreateShader(Source, out _);
    }
}
