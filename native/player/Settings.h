// The Switch player's settings, of the C# player's (Game/PlayerSettings.cs) those it has:
// sdmc:/OpenShiina/settings.json with the C# player's names and values ("Scaling": "Sharp",
// "FsrSharpness": 8, "FileCache": "High"); the keys it does not know are kept as they are when it
// writes the file. The file cache is High (256 MB) when the file does not say: the C# player's Auto
// would give the Switch 96 MB, too little for the large pictures of the 1600 x 900 games. The
// Switch's own: "ConsoleStorageWarned", the warning before the first game moved onto the console's
// storage shown.
#pragma once

#include <cstdint>
#include <string>

#include "ui/Renderer.h"

namespace openshiina::player {

struct Settings
{
    /// How the game's picture is scaled to the screen (C# PictureScaling).
    Scaling PictureScaling = Scaling::Sharp;
    /// FSR's sharpening: 1 (mild) to 10 (AMD's strongest).
    int FsrSharpness = 8;
    /// The memory the archives' decoded files are kept in (C# FileCacheSize): an index of kFileCacheChoices.
    int FileCache = 4;
    /// The warning before the first game moved onto the console's storage was shown and accepted.
    bool ConsoleWarned = false;

    /// The file cache's bytes (Auto: as the C# player, a thirty-second of the memory, 64 to 512 MB).
    int64_t FileCacheBytes() const;

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

/// The choices of "File cache" (C# FileCachePresets): the name in settings.json, the label, the help
/// line, the megabytes (0: Auto).
struct FileCacheChoice
{
    const char* Name;
    const char* Label;
    const char* Help;
    int Megabytes;
};
extern const FileCacheChoice kFileCacheChoices[6];

} // namespace openshiina::player
