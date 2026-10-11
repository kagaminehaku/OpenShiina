// How the players scale the game's picture (800 x 600, 1280 x 720 for v2.50) to their window: the
// setting "Scaling" (PlayerSettings.Scaling). At a scale that is not a whole number some game
// pixels would become 3 screen pixels and some 4 with nearest-neighbour (uneven text and lines),
// and bilinear alone blurs. Sharp: the picture multiplied by a whole number with nearest-neighbour,
// the rest of the way bilinear (on the GPU where the player can: the "sharp bilinear" shader;
// else the whole-number copy on the CPU, Multiply, drawn with linear filtering). FSR and Anime4K
// are upscalers that run as shaders (the Avalonia player: GamePicture); a player without them
// (WPF) draws them as Sharp (ForCopy). Placement gives where the picture goes in the window, for
// drawing and for the pointer. GraphicsPresets (GraphicsQuality.cs) picks them by level.

namespace OpenShiina.Game;

public enum Scaling
{
    /// <summary>Whole game pixels as far as they fit, the rest bilinear: even and sharp (the default).</summary>
    Sharp,
    /// <summary>x2, x3... only, black around: exact.</summary>
    WholeNumbers,
    /// <summary>Filtered (bicubic): soft.</summary>
    Smooth,
    /// <summary>Nearest-neighbour to the window's size: uneven at scales that are not whole numbers.</summary>
    Nearest,
    /// <summary>AMD FidelityFX Super Resolution 1: edge-adaptive upscaling (EASU), then sharpening (RCAS) as PlayerSettings.FsrSharpness says.</summary>
    Fsr,
    /// <summary>Anime4K's upscaling network (CNN x2), made for drawn art; then to the window's size.</summary>
    Anime4K,
}

public static class PictureScaling
{
    /// <summary>The choices as the settings screens show them.</summary>
    public static IReadOnlyList<(Scaling Scaling, string Label)> Choices { get; } =
    [
        (Scaling.Sharp, "Sharp: whole game pixels as far as they fit, the rest smooth (even text and lines)"),
        (Scaling.WholeNumbers, "Whole numbers only: x2, x3... with black around (exact)"),
        (Scaling.Smooth, "Smooth: filtered, soft"),
        (Scaling.Nearest, "Nearest: the window's size, pixels as they fall (uneven when not a whole number)"),
        (Scaling.Fsr, "FSR 1: AMD's upscaler, clean edges, sharpened as set below (the GPU's work)"),
        (Scaling.Anime4K, "Anime4K: an upscaler made for drawn art (heaviest: a desktop GPU)"),
    ];

    /// <summary>The choices a player that draws without shaders (WPF) offers.</summary>
    public static IReadOnlyList<(Scaling Scaling, string Label)> CopyChoices { get; } = Choices.Where(c => !c.Scaling.NeedsShader()).ToList();

    /// <summary>The upscalers that need a shader (FSR, Anime4K).</summary>
    public static bool NeedsShader(this Scaling scaling) => scaling is Scaling.Fsr or Scaling.Anime4K;

    /// <summary>The scaling a player without shaders uses for <paramref name="scaling"/>: Sharp for the upscalers.</summary>
    public static Scaling ForCopy(Scaling scaling) => scaling.NeedsShader() ? Scaling.Sharp : scaling;

    /// <summary>
    /// The picture's scale and its top left corner in a view (any unit, the same for both: device
    /// pixels to be exact). Whole numbers: the largest whole scale that fits (the fitting scale when
    /// even x1 does not fit); the others fill the view, proportions kept, centred.
    /// </summary>
    public static (double Scale, double Left, double Top) Placement(Scaling scaling, int width, int height, double viewWidth, double viewHeight)
    {
        if (width <= 0 || height <= 0 || viewWidth <= 0 || viewHeight <= 0)
            return (0, 0, 0);
        double fit = Math.Min(viewWidth / width, viewHeight / height);
        double scale = scaling == Scaling.WholeNumbers && fit >= 1 ? Math.Floor(fit + 1e-9) : fit;
        return (scale, (viewWidth - width * scale) / 2, (viewHeight - height * scale) / 2);
    }

    /// <summary>
    /// The whole number the sharp scaling multiplies the picture by before the bilinear rest, for a
    /// picture shown at <paramref name="scale"/> device pixels a game pixel: the whole part (at
    /// least 2 when the scale is over 1, so that between x1 and x2 the bilinear step makes the
    /// picture smaller, which keeps its edges sharp); 1 when the picture is shown at most x1.
    /// </summary>
    public static int SharpFactor(double scale) => scale <= 1 + 1e-9 ? 1 : Math.Clamp((int)Math.Floor(scale + 1e-9), 2, 8);

    /// <summary>
    /// Nearest-neighbour by a whole number: each 4-byte pixel of <paramref name="source"/>
    /// (width x height, rows sourceStride bytes apart) becomes factor x factor pixels of
    /// <paramref name="target"/> (rows targetStride apart). Rows on several cores (ParallelRows).
    /// </summary>
    public static unsafe void Multiply(byte* source, int width, int height, int sourceStride, byte* target, int targetStride, int factor)
    {
        nint from = (nint)source, to = (nint)target;
        void Row(int y)
        {
            var src = new ReadOnlySpan<uint>((byte*)from + (long)y * sourceStride, width);
            var first = new Span<uint>((byte*)to + (long)y * factor * targetStride, width * factor);
            for (int x = 0; x < width; x++)
                first.Slice(x * factor, factor).Fill(src[x]);
            for (int k = 1; k < factor; k++)
                first.CopyTo(new Span<uint>((byte*)to + ((long)y * factor + k) * targetStride, width * factor));
        }
        ParallelRows.For(height, (long)width * height * factor * factor, (first, last) =>
        {
            for (int y = first; y < last; y++)
                Row(y);
        });
    }
}
