// The graphics levels of the settings screens: each one a scaling of the game's picture (and FSR's
// sharpening), from the cheapest to the heaviest. The details on the settings page set
// them one by one; settings that match no level show as Custom. Phones and tablets show two
// profiles first (PhoneProfiles): the battery or the picture.

using OpenShiina.Localization;

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
    /// <summary>The levels as the settings screens show them, in the screens' language: name, what it does.</summary>
    public static IReadOnlyList<(GraphicsQuality Quality, string Label, string Help)> Choices =>
    [
        (GraphicsQuality.Potato, Strings.LevelPotato, Strings.LevelPotatoHelp),
        (GraphicsQuality.Low, Strings.LevelLow, Strings.LevelLowHelp),
        (GraphicsQuality.Medium, Strings.LevelMedium, Strings.LevelMediumHelp),
        (GraphicsQuality.High, Strings.LevelHigh, Strings.LevelHighHelp),
        (GraphicsQuality.Max, Strings.LevelMax, Strings.LevelMaxHelp),
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

/// <summary>The profiles phones and tablets show above the details.</summary>
public enum PhoneProfile
{
    /// <summary>Sharp scaling (Low) at the game's pace.</summary>
    BatterySaver,
    /// <summary>FSR 1 sharpened (High), a frame every refresh of the screen.</summary>
    Pretty,
}

public static class PhoneProfiles
{
    /// <summary>The profiles as the settings page shows them, in the screens' language: name, what it does.</summary>
    public static IReadOnlyList<(PhoneProfile Profile, string Label, string Help)> Choices =>
    [
        (PhoneProfile.BatterySaver, Strings.ProfileBatterySaver, Strings.ProfileBatterySaverHelp),
        (PhoneProfile.Pretty, Strings.ProfilePretty, Strings.ProfilePrettyHelp),
    ];

    private static (GraphicsQuality Quality, FramePacing Pacing) Settings(PhoneProfile profile) => profile switch
    {
        PhoneProfile.BatterySaver => (GraphicsQuality.Low, FramePacing.Game),
        _ => (GraphicsQuality.High, FramePacing.Display),
    };

    /// <summary>Sets <paramref name="settings"/> as the profile says.</summary>
    public static void Apply(PlayerSettings settings, PhoneProfile profile)
    {
        var (quality, pacing) = Settings(profile);
        GraphicsPresets.Apply(settings, quality);
        settings.FramePacing = pacing;
    }

    /// <summary>The profile the settings are at, or null when the details match neither.</summary>
    public static PhoneProfile? Of(PlayerSettings settings)
    {
        foreach (var (profile, _, _) in Choices)
        {
            var (quality, pacing) = Settings(profile);
            if (GraphicsPresets.Of(settings) == quality && settings.FramePacing == pacing)
                return profile;
        }
        return null;
    }
}
