// C# Formats/MpegAudio.cs: the sound of a movie, the MPEG-1 audio stream of an MPEG-1 system stream
// (Layer II in the games' movies). The C# player decodes it with NLayer into a 16-bit RIFF WAVE
// (its first seconds at once, the rest on another thread); here the stream itself goes to the
// music streams, which decode it as it plays (audio/ScnMusic: minimp3), so only the frames are
// walked here: to start the sound part of the way in (From).
#pragma once

#include <cstdint>
#include <optional>

#include "io/Binary.h"

namespace openshiina::formats::MpegAudio {

/// An MPEG audio frame header (Layers I-III, MPEG-1, 2 and 2.5): its length in bytes, its samples
/// and the sample rate; none when the four bytes are not one.
struct FrameHeader
{
    int Bytes = 0, Samples = 0, Rate = 0;
};
std::optional<FrameHeader> ReadHeader(const uint8_t* p);

/// The stream starts with an MPEG audio frame (looked for in its first bytes, as a decoder syncs).
bool IsMpegAudio(const Bytes& data);

/// The stream from the first frame at or after milliseconds into it (C#: From of the WAVE).
Bytes From(const Bytes& audio, int64_t milliseconds);

} // namespace openshiina::formats::MpegAudio
