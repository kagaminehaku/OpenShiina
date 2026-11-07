// C# Formats/AudioDecoder.cs: ShiinaRio's OGV and PAD sounds (ported there from GARbro
// ArcFormats/ShiinaRio/AudioOGV.cs and AudioPAD.cs). The Ogg Vorbis inside an OGV is decoded by
// the music streams (ScnMusic), with stb_vorbis here where the C# player has NVorbis.
#pragma once

#include <optional>

#include "io/Binary.h"

namespace openshiina::audio::AudioDecoder {

bool IsOgv(const Bytes& data);
bool IsPad(const Bytes& data);

/// The raw Ogg Vorbis stream of an OGV file (after its "fmt " and "data" headers).
std::optional<Bytes> DecodeOgv(const Bytes& data);

/// A PAD file (compressed PCM) as a RIFF WAVE file.
std::optional<Bytes> DecodePad(const Bytes& data);

} // namespace openshiina::audio::AudioDecoder
