// ShiinaRio's MI4 pictures (C# Formats/Mi4Format.cs, from GARbro's ImageMI4.cs): "MAI4", the width
// and height at 8 and 12, then the pixels from 0x10 as bits (a dword at a time, the high bit first)
// and literal BGR bytes. Two versions of the coding: the second is tried first, the first when the
// second runs past the data or the picture.
#pragma once

#include <cstdint>
#include <optional>
#include <vector>

namespace openshiina::formats {

struct Mi4Picture
{
    int Width = 0, Height = 0;
    /// Top-down rows of BGR, 3 bytes a pixel.
    std::vector<uint8_t> Bgr;
};

class Mi4
{
public:
    static bool Is(const uint8_t* data, size_t length);
    /// The picture, or none when the data is not MI4 (or neither coding reads it).
    static std::optional<Mi4Picture> Decode(const uint8_t* data, size_t length);
};

} // namespace openshiina::formats
