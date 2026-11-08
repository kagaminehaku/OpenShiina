// C# Formats/AsfAudio.cs (see AsfAudio.h)
//
// The packed stream: "OSWM", the format tag, channels, sample rate, bytes a second, block align
// (u32 each), the play length in ms and the frames to leave out at the start (i64 each), the
// extra format bytes (u32 length, bytes), then the packets (u32 count, each a u32 length and bytes).
#include "formats/AsfAudio.h"

#include <algorithm>
#include <cmath>
#include <cstring>

#include "formats/WmaProAudio.h"

namespace openshiina::formats::AsfAudio {

namespace {

constexpr uint32_t kMagic = 0x4D57534F; // "OSWM"
constexpr size_t kSkipAt = 4 + 5 * 4 + 8;

void Put32(Bytes& b, uint32_t v)
{
    for (int i = 0; i < 4; i++)
        b.push_back(uint8_t(v >> (8 * i)));
}

void Put64(Bytes& b, int64_t v)
{
    Put32(b, uint32_t(uint64_t(v)));
    Put32(b, uint32_t(uint64_t(v) >> 32));
}

int64_t Get64(const uint8_t* p) { return int64_t(LittleEndian::ToUInt64(p)); }

struct Header
{
    int FormatTag, Channels, SampleRate, BytesPerSecond, BlockAlign;
    int64_t Duration, Skip;
    Bytes Extra;
    size_t Packets;
};

Header ReadHeader(const Bytes& packed)
{
    if (!IsPacked(packed) || packed.size() < kSkipAt + 8 + 4)
        throw InvalidDataError("Not a packed WMA stream");
    const uint8_t* p = packed.data();
    Header h{};
    h.FormatTag = int(LittleEndian::ToUInt32(p + 4));
    h.Channels = int(LittleEndian::ToUInt32(p + 8));
    h.SampleRate = int(LittleEndian::ToUInt32(p + 12));
    h.BytesPerSecond = int(LittleEndian::ToUInt32(p + 16));
    h.BlockAlign = int(LittleEndian::ToUInt32(p + 20));
    h.Duration = Get64(p + 24);
    h.Skip = Get64(p + kSkipAt);
    size_t at = kSkipAt + 8;
    uint32_t extra = LittleEndian::ToUInt32(p + at);
    at += 4;
    if (extra > packed.size() - at)
        throw InvalidDataError("Packed WMA stream cut short");
    h.Extra.assign(p + at, p + at + extra);
    h.Packets = at + extra;
    return h;
}

std::unique_ptr<IWmaDecoder> MakeDecoder(int formatTag, int channels, int sampleRate, int bytesPerSecond, int blockAlign, const Bytes& extra)
{
    switch (formatTag)
    {
    case 0x160:
    case 0x161:
        return std::make_unique<WmaAudio>(formatTag, channels, sampleRate, bytesPerSecond, blockAlign, extra);
    case 0x162:
        return std::make_unique<WmaProAudio>(channels, sampleRate, blockAlign, extra);
    default:
        return nullptr;
    }
}

} // namespace

std::unique_ptr<IWmaDecoder> Decoder(const AsfFile::Stream& audio)
{
    return MakeDecoder(audio.FormatTag, audio.Channels, audio.SampleRate, audio.BytesPerSecond, audio.BlockAlign, audio.Extra);
}

Bytes Pack(const AsfFile& file)
{
    // C# StartWave's checks: no sound, a format or a channel count it cannot decode, no length
    if (!file.Audio || file.Audio->Objects.empty())
        return {};
    const AsfFile::Stream& audio = *file.Audio;
    std::unique_ptr<IWmaDecoder> decoder;
    try
    {
        decoder = Decoder(audio);
    }
    catch (const InvalidDataError&)
    {
        return {};
    }
    if (decoder == nullptr || decoder->Channels() < 1 || decoder->Channels() > 2)
        return {};
    int64_t frames = file.Duration * decoder->SampleRate() / 1000;
    if (frames <= 0 || frames > (INT32_MAX - 44) / (2 * decoder->Channels()))
        return {};
    Bytes b;
    Put32(b, kMagic);
    Put32(b, uint32_t(audio.FormatTag));
    Put32(b, uint32_t(audio.Channels));
    Put32(b, uint32_t(audio.SampleRate));
    Put32(b, uint32_t(audio.BytesPerSecond));
    Put32(b, uint32_t(audio.BlockAlign));
    Put64(b, file.Duration);
    Put64(b, 0);
    Put32(b, uint32_t(audio.Extra.size()));
    b.insert(b.end(), audio.Extra.begin(), audio.Extra.end());
    Put32(b, uint32_t(audio.Objects.size()));
    for (const auto& object : audio.Objects)
    {
        Put32(b, uint32_t(object.Length));
        b.insert(b.end(), audio.Data.begin() + object.Start, audio.Data.begin() + object.Start + object.Length);
    }
    return b;
}

bool IsPacked(const Bytes& data)
{
    return data.size() >= 4 && LittleEndian::ToUInt32(data.data()) == kMagic;
}

Bytes From(const Bytes& packed, int64_t milliseconds)
{
    Header h = ReadHeader(packed);
    int rate = h.SampleRate;
    if (auto decoder = MakeDecoder(h.FormatTag, h.Channels, h.SampleRate, h.BytesPerSecond, h.BlockAlign, h.Extra))
        rate = decoder->SampleRate();
    Bytes copy = packed;
    int64_t skip = std::max<int64_t>(0, milliseconds) * rate / 1000;
    for (int i = 0; i < 8; i++)
        copy[kSkipAt + size_t(i)] = uint8_t(uint64_t(skip) >> (8 * i));
    return copy;
}

Reader::Reader(const Bytes& packed) : m_packed(packed)
{
    Header h = ReadHeader(packed);
    m_decoder = MakeDecoder(h.FormatTag, h.Channels, h.SampleRate, h.BytesPerSecond, h.BlockAlign, h.Extra);
    if (m_decoder == nullptr)
        throw InvalidDataError("Not a WMA stream");
    m_channels = m_decoder->Channels();
    m_rate = m_decoder->SampleRate();
    m_frames = h.Duration * m_rate / 1000;
    // the WAVE's frames from the seek on (C#: MpegAudio.From)
    m_written = std::min(h.Skip, m_frames);
    m_skip = int64_t(m_decoder->Delay()) + m_written;
    m_packets = LittleEndian::ToUInt32(packed.data() + h.Packets);
    m_next = h.Packets + 4;
}

bool Reader::Decode()
{
    // A packet's samples into m_pending (C#: Writer.Run); false after the last packet
    if (m_packet >= m_packets || m_next + 4 > m_packed.size())
        return false;
    uint32_t length = LittleEndian::ToUInt32(m_packed.data() + m_next);
    m_next += 4;
    if (length > m_packed.size() - m_next)
    {
        m_packet = m_packets;
        return false;
    }
    const uint8_t* packet = m_packed.data() + m_next;
    m_next += length;
    m_packet++;
    for (auto& list : m_samples)
        list.clear();
    try
    {
        m_decoder->DecodePacket(packet, length, m_samples);
    }
    catch (const InvalidDataError&)
    {
        // a broken packet: its frames stay out (FFmpeg goes on in the same way)
    }
    int64_t count = int64_t(m_samples[0].size());
    int64_t from = std::min(m_skip, count);
    m_skip -= from;
    m_pending.clear();
    m_pendingAt = 0;
    for (int64_t i = from; i < count; i++)
        for (int c = 0; c < m_channels; c++)
        {
            float r = std::nearbyint(m_samples[c][size_t(i)] * 32767.0f);
            m_pending.push_back(int16_t(std::clamp(r, -32768.0f, 32767.0f)));
        }
    return true;
}

int Reader::Read(float* buffer, int count)
{
    count -= count % m_channels;
    int total = 0;
    while (total < count && m_written < m_frames)
    {
        if (m_pendingAt < m_pending.size())
        {
            int n = int(std::min<size_t>(m_pending.size() - m_pendingAt, size_t(count - total)));
            n = int(std::min<int64_t>(n / m_channels, m_frames - m_written)) * m_channels;
            for (int i = 0; i < n; i++)
                buffer[total + i] = m_pending[m_pendingAt + size_t(i)] / 32768.0f;
            m_pendingAt += size_t(n);
            total += n;
            m_written += n / m_channels;
            continue;
        }
        if (!Decode())
        {
            // after the last packet the WAVE is silent to its end
            int n = int(std::min<int64_t>((count - total) / m_channels, m_frames - m_written)) * m_channels;
            std::fill_n(buffer + total, n, 0.0f);
            total += n;
            m_written += n / m_channels;
        }
    }
    return total;
}

} // namespace openshiina::formats::AsfAudio
