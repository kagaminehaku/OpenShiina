// The graphics levels of the settings screens: each one a scaling of the game's picture (and FSR's
// sharpening), from the cheapest to the heaviest. The Advanced graphics part of the screens sets
// them one by one; settings that match no level show as Custom.

namespace OpenShiina.Game;

public enum GraphicsQuality
{
    Potato,
    Low,
    Medium,
    High,
    Max,
    /// <summary>Advanced settings that match no level.</summary>
    Custom,
}

public static class GraphicsPresets
{
    /// <summary>The levels as the settings screens show them: name, what it does.</summary>
    public static IReadOnlyList<(GraphicsQuality Quality, string Label, string Help)> Choices { get; } =
    [
        (GraphicsQuality.Potato, "Potato", "Nearest pixels, no filtering: the least work, for the slowest phones."),
        (GraphicsQuality.Low, "Low", "Sharp: whole game pixels, smooth only where they meet. Light."),
        (GraphicsQuality.Medium, "Medium", "FSR 1 upscaling, no sharpening: clean edges on any GPU."),
        (GraphicsQuality.High, "High", "FSR 1 upscaling and sharpening."),
        (GraphicsQuality.Max, "Max", "Anime4K, made for drawn art: the heaviest, for a desktop GPU."),
    ];

    /// <summary>What a level sets.</summary>
    public static (Scaling Scaling, int FsrSharpness) Settings(GraphicsQuality quality) => quality switch
    {
        GraphicsQuality.Potato => (Scaling.Nearest, 0),
        GraphicsQuality.Low => (Scaling.Sharp, 0),
        GraphicsQuality.Medium => (Scaling.Fsr, 0),
        GraphicsQuality.High => (Scaling.Fsr, 8),
        _ => (Scaling.Anime4K, 0),
    };

    /// <summary>
    /// Sets <paramref name="settings"/> as the level says (FSR's sharpening only for an FSR level,
    /// Custom changes nothing).
    /// </summary>
    public static void Apply(PlayerSettings settings, GraphicsQuality quality)
    {
        if (quality == GraphicsQuality.Custom)
            return;
        var (scaling, sharpness) = Settings(quality);
        settings.Scaling = scaling;
        if (scaling == Scaling.Fsr)
            settings.FsrSharpness = sharpness;
    }

    /// <summary>The level the settings are at, or Custom.</summary>
    public static GraphicsQuality Of(PlayerSettings settings)
    {
        foreach (var (quality, _, _) in Choices)
        {
            var (scaling, sharpness) = Settings(quality);
            if (settings.Scaling == scaling && (scaling != Scaling.Fsr || settings.FsrSharpness == sharpness))
                return quality;
        }
        return GraphicsQuality.Custom;
    }
}
