// The icon of a Windows program, read from its resources without Windows (C# Formats/ExeIcon.cs):
// the first icon group (RT_GROUP_ICON) and one of its images (RT_ICON), as RGBA. The C# player
// takes the largest image and leaves the decoding to its image library; the Switch player draws
// the icon small beside the game's name, so here the image is the smallest one at least as large
// as asked for (else the largest), with the most colours, and of the formats inside .ico files
// only the Windows bitmap one is read (1, 4, 8, 16, 24 and 32 bits, the AND mask for
// transparency): an image stored as PNG is passed over for the next one.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "io/Binary.h"

namespace openshiina::formats {

struct IconImage
{
    int Width = 0, Height = 0;
    /// Top-down rows of RGBA.
    std::vector<uint8_t> Rgba;
};

class ExeIcon
{
public:
    /// The icon of the program in the file, or none (no icon, no bitmap image in it, not a PE file).
    static std::optional<IconImage> Read(const std::string& exePath, int wanted);
    /// The same from the program's bytes.
    static std::optional<IconImage> Extract(const Bytes& pe, int wanted);
    /// A bitmap image of an .ico (BITMAPINFOHEADER, the colours, the AND mask) as RGBA, or none.
    static std::optional<IconImage> DecodeBitmap(const Bytes& image);
};

} // namespace openshiina::formats
