// C# Audio/ScnMusic.cs (see ScnMusic.h)
#include "audio/ScnMusic.h"

#include <cmath>
#include <cstring>
#include <optional>

#include "audio/AudioDecoder.h"
#include "formats/MpegAudio.h"
#include "minimp3/minimp3.h"

#define STB_VORBIS_HEADER_ONLY
#define STB_VORBIS_NO_STDIO
#include "stb_vorbis/stb_vorbis.c"

namespace openshiina::audio {

namespace {

/// Ogg Vorbis decoded as it is read (C#: NVorbis's VorbisReader).
class VorbisSource final : public SampleSource
{
public:
    VorbisSource(Bytes ogg, bool loop) : m_ogg(std::move(ogg)), m_loop(loop)
    {
        int error = 0;
        m_vorbis = stb_vorbis_open_memory(m_ogg.data(), int(m_ogg.size()), &error, nullptr);
        if (m_vorbis == nullptr)
            throw InvalidDataError("Not an Ogg Vorbis stream");
        auto info = stb_vorbis_get_info(m_vorbis);
        m_channels = info.channels;
        m_rate = int(info.sample_rate);
        // never past the last page's granule position (the C# player's ReadVorbis)
        m_length = int64_t(stb_vorbis_stream_length_in_samples(m_vorbis));
    }

    ~VorbisSource() override { stb_vorbis_close(m_vorbis); }

    int Channels() const override { return m_channels; }
    int Rate() const override { return m_rate; }

    int Read(float* buffer, int count) override
    {
        count -= count % m_channels;
        int total = 0;
        while (total < count)
        {
            int frames = (count - total) / m_channels;
            if (m_length > 0)
                frames = int(std::min<int64_t>(frames, std::max<int64_t>(0, m_length - m_position)));
            int n = frames > 0 ? stb_vorbis_get_samples_float_interleaved(m_vorbis, m_channels, buffer + total, frames * m_channels) : 0;
            if (n == 0)
            {
                if (!m_loop)
                    break;
                stb_vorbis_seek_start(m_vorbis);
                m_position = 0;
                // an empty file would loop for ever: stop instead
                if (total == 0 && ++m_emptyRewinds > 2)
                    break;
                continue;
            }
            m_emptyRewinds = 0;
            m_position += n;
            total += n * m_channels;
        }
        return total;
    }

private:
    Bytes m_ogg;
    stb_vorbis* m_vorbis = nullptr;
    bool m_loop;
    int m_channels = 1, m_rate = 44100, m_emptyRewinds = 0;
    int64_t m_length = 0, m_position = 0;
};

/// A RIFF WAVE file's PCM (8, 16, 24 or 32 bits, or 32-bit floats) as floats (C#: NAudio's
/// WaveFileReader and its sample provider).
class WaveSource final : public SampleSource
{
public:
    WaveSource(Bytes file, bool loop) : m_file(std::move(file)), m_loop(loop)
    {
        const Bytes& d = m_file;
        if (d.size() < 12 || std::memcmp(d.data(), "RIFF", 4) != 0 || std::memcmp(d.data() + 8, "WAVE", 4) != 0)
            throw InvalidDataError("Not a RIFF WAVE file");
        bool format = false;
        for (size_t at = 12; at + 8 <= d.size();)
        {
            uint32_t size = LittleEndian::ToUInt32(d.data() + at + 4);
            size_t body = at + 8;
            if (std::memcmp(d.data() + at, "fmt ", 4) == 0 && body + 16 <= d.size())
            {
                int tag = LittleEndian::ToUInt16(d.data() + body);
                m_channels = LittleEndian::ToUInt16(d.data() + body + 2);
                m_rate = int(LittleEndian::ToUInt32(d.data() + body + 4));
                m_bits = LittleEndian::ToUInt16(d.data() + body + 14);
                if (tag == 0xFFFE && size >= 26 && body + 26 <= d.size())
                    tag = LittleEndian::ToUInt16(d.data() + body + 24);
                m_float = tag == 3;
                if (!(tag == 1 || (tag == 3 && m_bits == 32)) || m_channels <= 0 || m_rate <= 0
                    || (m_bits != 8 && m_bits != 16 && m_bits != 24 && m_bits != 32))
                    throw InvalidDataError("A WAVE format that cannot be played");
                format = true;
            }
            else if (std::memcmp(d.data() + at, "data", 4) == 0)
            {
                m_data = body;
                m_end = std::min(d.size(), body + size_t(size));
                break;
            }
            at = body + size_t(size) + (size & 1);
        }
        if (!format || m_data == 0)
            throw InvalidDataError("A WAVE file without its format or data");
        m_frameBytes = m_channels * (m_bits / 8);
        m_end = m_data + (m_end - m_data) / size_t(m_frameBytes) * size_t(m_frameBytes);
        m_at = m_data;
    }

    int Channels() const override { return m_channels; }
    int Rate() const override { return m_rate; }

    int Read(float* buffer, int count) override
    {
        count -= count % m_channels;
        int total = 0;
        while (total < count)
        {
            if (m_at >= m_end)
            {
                if (!m_loop)
                    break;
                m_at = m_data;
                if (total == 0 && ++m_emptyRewinds > 2)
                    break;
                if (m_end == m_data)
                    break;
                continue;
            }
            m_emptyRewinds = 0;
            const uint8_t* p = m_file.data() + m_at;
            float v;
            switch (m_bits)
            {
            case 8:
                v = (int(p[0]) - 128) / 128.0f;
                break;
            case 16:
                v = int16_t(LittleEndian::ToUInt16(p)) / 32768.0f;
                break;
            case 24:
                v = float(int32_t(uint32_t(p[0]) << 8 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 24) >> 8) / 8388608.0f;
                break;
            default:
                if (m_float)
                    std::memcpy(&v, p, 4);
                else
                    v = float(int32_t(LittleEndian::ToUInt32(p))) / 2147483648.0f;
                break;
            }
            buffer[total++] = v;
            m_at += size_t(m_bits / 8);
        }
        return total;
    }

private:
    Bytes m_file;
    bool m_loop;
    bool m_float = false;
    int m_channels = 1, m_rate = 44100, m_bits = 16, m_frameBytes = 2, m_emptyRewinds = 0;
    size_t m_data = 0, m_end = 0, m_at = 0;
};

/// An MPEG audio stream (a movie's Layer II sound) decoded frame by frame as it is read (C#:
/// NLayer, into a WAVE first).
class MpegAudioSource final : public SampleSource
{
public:
    MpegAudioSource(Bytes stream, bool loop) : m_stream(std::move(stream)), m_loop(loop)
    {
        mp3dec_init(&m_decoder);
        // the format of the first frame
        mp3dec_frame_info_t info{};
        for (size_t at = 0; at < m_stream.size();)
        {
            int samples = mp3dec_decode_frame(&m_decoder, m_stream.data() + at, int(m_stream.size() - at), nullptr, &info);
            if (info.frame_bytes == 0)
                break;
            if (samples > 0 || info.channels > 0)
            {
                m_channels = info.channels;
                m_rate = info.hz;
                break;
            }
            at += size_t(info.frame_bytes);
        }
        if (m_channels < 1 || m_channels > 2 || m_rate <= 0)
            throw InvalidDataError("Not an MPEG audio stream");
        mp3dec_init(&m_decoder);
    }

    int Channels() const override { return m_channels; }
    int Rate() const override { return m_rate; }

    int Read(float* buffer, int count) override
    {
        count -= count % m_channels;
        int total = 0;
        while (total < count)
        {
            if (m_at < m_decoded)
            {
                int n = std::min(m_decoded - m_at, count - total);
                for (int i = 0; i < n; i++)
                    buffer[total + i] = m_pcm[size_t(m_at + i)] / 32768.0f;
                m_at += n;
                total += n;
                continue;
            }
            mp3dec_frame_info_t info{};
            int samples = m_position < m_stream.size()
                ? mp3dec_decode_frame(&m_decoder, m_stream.data() + m_position, int(m_stream.size() - m_position), m_pcm, &info)
                : 0;
            if (info.frame_bytes == 0)
            {
                if (!m_loop)
                    break;
                m_position = 0;
                mp3dec_init(&m_decoder);
                if (total == 0 && ++m_emptyRewinds > 2)
                    break;
                continue;
            }
            m_emptyRewinds = 0;
            m_position += size_t(info.frame_bytes);
            // a frame of another channel count than the first is left out
            m_decoded = info.channels == m_channels ? samples * m_channels : 0;
            m_at = 0;
        }
        return total;
    }

private:
    Bytes m_stream;
    bool m_loop;
    mp3dec_t m_decoder{};
    mp3d_sample_t m_pcm[MINIMP3_MAX_SAMPLES_PER_FRAME] = {};
    size_t m_position = 0;
    int m_decoded = 0, m_at = 0, m_emptyRewinds = 0;
    int m_channels = 0, m_rate = 0;
};

} // namespace

std::unique_ptr<SampleSource> OpenSource(const Bytes& file, bool loop)
{
    if (file.size() < 4)
        throw InvalidDataError("A sound file too short to play");
    uint32_t magic = LittleEndian::ToUInt32(file.data());
    std::optional<Bytes> ogg;
    if ((magic & 0xFFFFFF) == 0x56474F)
        ogg = AudioDecoder::DecodeOgv(file);
    else if (magic == 0x5367674F)
        ogg = file;
    if (ogg)
        return std::make_unique<VorbisSource>(std::move(*ogg), loop);
    if ((magic & 0xFFFFFF) == 0x444150)
        if (auto wave = AudioDecoder::DecodePad(file))
            return std::make_unique<WaveSource>(std::move(*wave), loop);
    if (magic != 0x46464952 && formats::MpegAudio::IsMpegAudio(file))
        return std::make_unique<MpegAudioSource>(file, loop);
    return std::make_unique<WaveSource>(file, loop);
}

float VolumeGain(int volume)
{
    return volume <= -10000 ? 0.0f : float(std::pow(10.0, std::min(volume, 0) / 2000.0));
}

ScnMusic::~ScnMusic()
{
    for (auto& [id, s] : m_streams)
        Halt(s);
}

int ScnMusic::Open(const Bytes& file)
{
    int id = m_next++;
    m_streams[id] = Stream{file, nullptr, 1};
    return id;
}

void ScnMusic::Close(int stream)
{
    if (auto found = m_streams.find(stream); found != m_streams.end())
    {
        Halt(found->second);
        m_streams.erase(found);
    }
}

void ScnMusic::Play(int stream, bool loop)
{
    auto found = m_streams.find(stream);
    if (found == m_streams.end())
        return;
    Stream& s = found->second;
    Halt(s);
    try
    {
        s.Voice = m_mixer.Start(OpenSource(s.File, loop), s.Gain);
    }
    catch (const std::exception&)
    {
        // Music that cannot be decoded stays silent
        Halt(s);
    }
}

void ScnMusic::Stop(int stream)
{
    if (auto found = m_streams.find(stream); found != m_streams.end())
        Halt(found->second);
}

void ScnMusic::Pause(int stream)
{
    if (auto found = m_streams.find(stream); found != m_streams.end() && found->second.Voice)
        found->second.Voice->Paused = true;
}

void ScnMusic::Resume(int stream)
{
    if (auto found = m_streams.find(stream); found != m_streams.end() && found->second.Voice)
        found->second.Voice->Paused = false;
}

void ScnMusic::SetVolume(int stream, int volume)
{
    auto found = m_streams.find(stream);
    if (found == m_streams.end())
        return;
    found->second.Gain = VolumeGain(volume);
    if (found->second.Voice)
        found->second.Voice->Gain = found->second.Gain;
}

bool ScnMusic::IsPlaying(int stream)
{
    auto found = m_streams.find(stream);
    return found != m_streams.end() && found->second.Voice && !found->second.Voice->Ended();
}

void ScnMusic::Halt(Stream& s)
{
    if (s.Voice)
        s.Voice->Stop();
    s.Voice = nullptr;
}

} // namespace openshiina::audio
