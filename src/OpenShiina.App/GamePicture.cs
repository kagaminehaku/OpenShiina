// The game's picture in the Avalonia player, drawn with SkiaSharp as the setting "Scaling" says
// (Core Game/PictureScaling.cs): nearest-neighbour (to the view, or at its whole scale), bicubic
// (Catmull-Rom), or sharp: the "sharp bilinear" runtime shader (SkSL), which snaps the sampling
// point within each game pixel so that only the screen pixels on its edge mix with the next one
// (Skia runs it on the GPU where it draws on one, on the CPU otherwise), or the upscalers FSR 1
// and Anime4K (Upscalers.cs; on the GPU only: Sharp without one). The picture is a texture of the
// render thread's (a GPU surface; a raster one without a GPU) that takes only the part of each
// frame that changed (GameSession.TakeFrame gives it): the UI thread writes that part into the
// whole picture's pixels and marks it, the render thread copies what is marked into a buffer of
// its own and draws it into the texture (the whole picture is 3.7 MB at 1280 x 720; in a scene
// of text most frames change the text or the cursor only). An upscaled picture is kept until the
// picture or the size changes.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using OpenShiina.Game;
using OpenShiina.Scripting;
using SkiaSharp;

namespace OpenShiina.App;

public sealed class GamePicture : Control
{
    private readonly FrameTexture m_texture = new();
    private bool m_shown;
    private int m_width, m_height;

    /// <summary>Writes the part of the game's picture that changed (GameSession.TakeFrame): false when there is no new picture.</summary>
    public delegate bool FrameSource(Span<byte> target, int stride, out ScreenArea area);

    public GamePicture()
    {
        // The whole view takes the pointer (black around the picture included)
        Background = Brushes.Transparent;
    }

    /// <summary>The view's background (transparent: the pointer is taken all over it).</summary>
    public IBrush? Background { get; set; }

    /// <summary>How the picture is scaled to the view.</summary>
    public Scaling Scaling { get; set; } = Scaling.Sharp;

    /// <summary>FSR's sharpening, 0 (none) to 10 (PlayerSettings.FsrSharpness).</summary>
    public int FsrSharpness { get; set; } = 8;

    /// <summary>A game's picture of that size, black until its first frame.</summary>
    public void Start(int width, int height)
    {
        m_texture.Start(width, height);
        m_width = width;
        m_height = height;
        m_shown = true;
        InvalidateVisual();
    }

    /// <summary>The game's new picture, if any: what changed of it is written and drawn again.</summary>
    public void TakeFrame(FrameSource source)
    {
        if (m_shown && m_texture.Take(source))
            InvalidateVisual();
    }

    /// <summary>No picture (the game ended).</summary>
    public void Clear()
    {
        m_shown = false;
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
        if (!m_shown || m_width == 0)
            return;
        var (scale, left, top) = Placement();
        if (scale <= 0)
            return;
        double device = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var target = new Rect(left, top, m_width * scale, m_height * scale);
        context.Custom(new DrawPicture(new Rect(Bounds.Size), target, m_texture, Scaling, scale * device, device, FsrSharpness));
    }

    /// <summary>
    /// The picture's pixels, which the UI thread writes, and its texture, the render thread's.
    /// The part written since the texture last took it is marked; the render thread copies it
    /// under the lock into a buffer of its own and draws that into the texture.
    /// </summary>
    private sealed class FrameTexture
    {
        private readonly object m_lock = new();
        // Under the lock: the whole picture (BGRA), its size, and what changed since the texture took it
        private byte[] m_pixels = [];
        private int m_width, m_height;
        private ScreenArea m_changed;
        // The render thread's: the texture, the GPU it is on (null: a raster one), its size, its picture now, and the rows taken
        private SKSurface? m_surface;
        private GRContext? m_gpu;
        private int m_surfaceWidth, m_surfaceHeight;
        private SKImage? m_image;
        private byte[] m_rows = [];

        private static readonly SKPaint s_copy = new() { BlendMode = SKBlendMode.Src };

        /// <summary>Counts the texture's changes (the render thread's): an upscaled picture is made again when it moves.</summary>
        public int Version { get; private set; }

        private static SKImageInfo Info(int width, int height) => new(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);

        /// <summary>A black picture of that size, all of it to be taken.</summary>
        public void Start(int width, int height)
        {
            lock (m_lock)
            {
                if (m_pixels.Length == width * height * 4)
                    Array.Clear(m_pixels);
                else
                    m_pixels = new byte[width * height * 4];
                m_width = width;
                m_height = height;
                m_changed = new ScreenArea(0, 0, width, height);
            }
        }

        /// <summary>The game's new picture written into the pixels; true when a part of them changed.</summary>
        public bool Take(FrameSource source)
        {
            lock (m_lock)
            {
                if (m_width == 0 || !source(m_pixels, m_width * 4, out var area))
                    return false;
                area = area.Clip(m_width, m_height);
                if (area.IsEmpty)
                    return false;
                m_changed = m_changed.Union(area);
                return true;
            }
        }

        /// <summary>
        /// The picture as the texture has it, with what changed since the last call drawn into it
        /// (the render thread's; the texture keeps the image): on <paramref name="gpu"/>, or in
        /// memory without one. A new GPU or size makes a new texture, which takes all of it.
        /// </summary>
        public unsafe SKImage? Image(GRContext? gpu)
        {
            ScreenArea area;
            lock (m_lock)
            {
                if (m_width == 0)
                    return null;
                if (m_surface == null || m_gpu != gpu || m_surfaceWidth != m_width || m_surfaceHeight != m_height)
                {
                    m_image?.Dispose();
                    m_image = null;
                    m_surface?.Dispose();
                    m_surface = (gpu != null ? SKSurface.Create(gpu, true, Info(m_width, m_height)) : null) ?? SKSurface.Create(Info(m_width, m_height));
                    if (m_surface == null)
                        return null;
                    m_gpu = gpu;
                    m_surfaceWidth = m_width;
                    m_surfaceHeight = m_height;
                    m_changed = new ScreenArea(0, 0, m_width, m_height);
                }
                area = m_changed;
                m_changed = default;
                int row = m_width * 4, from = area.L * 4, bytes = area.Width * 4;
                if (m_rows.Length < bytes * area.Height)
                    m_rows = GC.AllocateUninitializedArray<byte>(m_width * m_height * 4, pinned: true);
                for (int y = 0; y < area.Height; y++)
                    m_pixels.AsSpan((area.T + y) * row + from, bytes).CopyTo(m_rows.AsSpan(y * bytes));
            }
            if (!area.IsEmpty)
            {
                // The last image let go first: drawing into the surface while it is held copies the whole surface for it
                m_image?.Dispose();
                m_image = null;
                fixed (byte* p = m_rows)
                {
                    using (var part = SKImage.FromPixels(new SKPixmap(Info(area.Width, area.Height), (nint)p, area.Width * 4)))
                        m_surface.Canvas.DrawImage(part, area.L, area.T, s_copy);
                    // The rows are read now, before the buffer takes the next ones
                    m_surface.Flush();
                }
                Version++;
            }
            return m_image ??= m_surface.Snapshot();
        }
    }

    /// <summary>The picture drawn with SkiaSharp on the render thread.</summary>
    private sealed class DrawPicture : ICustomDrawOperation
    {
        private readonly Rect m_target;
        private readonly FrameTexture m_texture;
        private readonly Scaling m_scaling;
        private readonly double m_pixels, m_device;
        private readonly int m_sharpness;

        public DrawPicture(Rect bounds, Rect target, FrameTexture texture, Scaling scaling, double pixels, double device, int sharpness)
        {
            Bounds = bounds;
            m_target = target;
            m_texture = texture;
            m_scaling = scaling;
            m_pixels = pixels;
            m_device = device;
            m_sharpness = sharpness;
        }

        // The last upscaled picture (the render thread's): what it was made from, and how
        [ThreadStatic]
        private static (GRContext Gpu, FrameTexture Source, int Version, Scaling Scaling, int Width, int Height, int Sharpness, SKImage Result)? t_upscaled;

        /// <summary>
        /// The picture upscaled as the setting says (to the target's device pixels for FSR, twice
        /// its size for Anime4K), made again only for a new picture or size; null without a GPU.
        /// </summary>
        private SKImage? Upscaled(GRContext? gpu, SKImage image, int version)
        {
            if (gpu == null)
                return null;
            int width = (int)Math.Round(m_target.Width * m_device), height = (int)Math.Round(m_target.Height * m_device);
            if (t_upscaled is { } last && last.Gpu == gpu && last.Source == m_texture && last.Version == version && last.Scaling == m_scaling && last.Width == width && last.Height == height && last.Sharpness == m_sharpness)
                return last.Result;
            var result = m_scaling == Scaling.Fsr ? Upscalers.Fsr(gpu, image, width, height, m_sharpness) : Upscalers.Anime4K(gpu, image);
            t_upscaled?.Result.Dispose();
            t_upscaled = result == null ? null : (gpu, m_texture, version, m_scaling, width, height, m_sharpness, result);
            return result;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            if (m_texture.Image(lease.GrContext) is not { } image)
                return;
            var target = new SKRect((float)m_target.Left, (float)m_target.Top, (float)m_target.Right, (float)m_target.Bottom);
            if (m_scaling.NeedsShader() && m_pixels > 1 + 1e-6 && Upscaled(lease.GrContext, image, m_texture.Version) is { } upscaled)
            {
                // FSR: the target's own pixels; Anime4K: twice the picture, the rest bicubic
                canvas.DrawImage(upscaled, target, m_scaling == Scaling.Fsr
                    ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
                    : new SKSamplingOptions(SKCubicResampler.CatmullRom));
                return;
            }
            switch (m_scaling.NeedsShader() ? Scaling.Sharp : m_scaling)
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
