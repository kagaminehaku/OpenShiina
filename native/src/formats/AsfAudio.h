// C# Formats/AsfAudio.cs: the sound of a Windows Media movie (AsfFile). The C# player makes it a
// 16-bit RIFF WAVE of the file's play length, its first seconds decoded at once and the rest on
// another thread; here the audio stream is packed apart from the movie (Pack) and decoded as it
// plays (Reader, through the music streams), giving the same samples as that WAVE: the decoder's
// delay left out, each sample rounded to 16 bits, as long as the file's play length (silence after
// the last packet).
#pragma once

#include <cstdint>
#include <memory>
#include <vector>

#include "formats/AsfFile.h"
#include "formats/WmaAudio.h"

namespace openshiina::formats::AsfAudio {

/// The decoder of a WMA 1 / 2 / Pro stream, or null for another format.
std::unique_ptr<IWmaDecoder> Decoder(const AsfFile::Stream& audio);

/// The audio stream of a file packed for Reader (its format, play length and packets); empty when
/// there is no sound or it cannot be decoded.
Bytes Pack(const AsfFile& file);

/// A packed stream (Pack).
bool IsPacked(const Bytes& data);

/// A packed stream that starts milliseconds into the sound (C#: MpegAudio.From of the WAVE).
Bytes From(const Bytes& packed, int64_t milliseconds);

/// A packed stream decoded as it is read: interleaved samples of the 16-bit WAVE as floats.
class Reader
{
public:
    explicit Reader(const Bytes& packed);

    int Channels() const { return m_channels; }
    int Rate() const { return m_rate; }

    /// Up to count samples (a whole number of frames); fewer at the end.
    int Read(float* buffer, int count);

private:
    const Bytes& m_packed;
    std::unique_ptr<IWmaDecoder> m_decoder;
    int m_channels = 0, m_rate = 0;
    int64_t m_frames = 0, m_written = 0, m_skip = 0;
    size_t m_next = 0;
    uint32_t m_packets = 0, m_packet = 0;
    std::vector<float> m_samples[2];
    std::vector<int16_t> m_pending;
    size_t m_pendingAt = 0;

    bool Decode();
};

} // namespace openshiina::formats::AsfAudio
