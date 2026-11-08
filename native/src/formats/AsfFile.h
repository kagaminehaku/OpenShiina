// C# Formats/AsfFile.cs: Windows Media files (ASF: the .wmv movies of v2.50 games). The engine plays
// them through DirectShow like its MPEG-1 movies; Sorcery Jokers ships them alone (mv\*.wmv, WMV3 /
// VC-1 video with WMA sound). AsfFile splits a file into the media objects (frames) of its first
// video and first audio stream, each stream copied once into an array of its own.
#pragma once

#include <cstdint>
#include <optional>
#include <vector>

#include "io/Binary.h"

namespace openshiina::formats {

class AsfFile
{
public:
    /// A stream: its format and its objects (start and length in Data, time in ms, key frame).
    struct Stream
    {
        int Number = 0;
        bool Video = false;
        /// Video: the BITMAPINFOHEADER's compression (WMV3, WVC1), size, and the bytes after the header.
        uint32_t FourCC = 0;
        int Width = 0, Height = 0;
        /// Audio: the WAVEFORMATEX fields, and the bytes after it (cbSize).
        int FormatTag = 0, Channels = 0, SampleRate = 0, BytesPerSecond = 0, BlockAlign = 0, BitsPerSample = 0;
        Bytes Extra;
        /// 100 ns units from the extended stream properties (0 when not given).
        int64_t TimePerFrame = 0;
        /// The objects one after another, followed by Padding zeros.
        Bytes Data;
        struct Object
        {
            int Start, Length, Time;
            bool Key;
        };
        std::vector<Object> Objects;

        // Audio spread (descrambling): span, virtual packet and chunk lengths
        int Span = 0, SpanPacket = 0, SpanChunk = 0;
    };

    /// Zeros after each stream's data, so that a bit reader may look past its end.
    static constexpr int Padding = 16;

    /// The file starts with the ASF header object.
    static bool IsAsf(const Bytes& file);

    explicit AsfFile(const Bytes& file);

    std::optional<Stream> Video, Audio;
    /// The play duration in ms (without the preroll).
    int64_t Duration = 0;
};

} // namespace openshiina::formats
