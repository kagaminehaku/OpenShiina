// The Switch player's settings, of the C# player's (Game/PlayerSettings.cs) those it has:
// sdmc:/OpenShiina/settings.json with the C# player's names and values ("Scaling": "Sharp",
// "FsrSharpness": 8); the keys it does not know are kept as they are when it writes the file.
#pragma once

#include <string>

#include "ui/Renderer.h"

namespace openshiina::player {

struct Settings
{
    /// How the game's picture is scaled to the screen (C# PictureScaling).
    Scaling PictureScaling = Scaling::Sharp;
    /// FSR's sharpening: 1 (mild) to 10 (AMD's strongest).
    int FsrSharpness = 8;

    /// The file's settings (the defaults for what it lacks or when there is none).
    static Settings Load(const std::string& path);
    void Save(const std::string& path) const;
};

/// The choices of "Scaling" as the C# player names and shows them: the name in settings.json, the
/// label, the help line.
struct ScalingChoice
{
    Scaling Value;
    const char* Name;
    const char* Label;
    const char* Help;
};
extern const ScalingChoice kScalingChoices[5];

} // namespace openshiina::player
