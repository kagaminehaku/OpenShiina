// C# Formats/MpegAudio.cs (see MpegAudio.h)
#include "formats/MpegAudio.h"

namespace openshiina::formats::MpegAudio {

namespace {

// Bit rates in kbit/s by version (0: MPEG-1, 1: MPEG-2 and 2.5), layer (0: I, 1: II, 2: III) and index
const int kBitRates[2][3][16] = {
    {{0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0},
     {0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0},
     {0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0}},
    {{0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0},
     {0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0},
     {0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0}},
};
const int kRates[3] = {44100, 48000, 32000};

/// The first frame from at, with the next frame's header after it (a sync that holds), or none.
std::optional<size_t> FindFrame(const Bytes& data, size_t at, size_t limit)
{
    for (size_t p = at; p + 4 <= data.size() && p < limit; p++)
        if (auto h = ReadHeader(data.data() + p))
        {
            size_t next = p + size_t(h->Bytes);
            if (next + 4 > data.size() || ReadHeader(data.data() + next))
                return p;
        }
    return std::nullopt;
}

} // namespace

std::optional<FrameHeader> ReadHeader(const uint8_t* p)
{
    if (p[0] != 0xFF || (p[1] & 0xE0) != 0xE0)
        return std::nullopt;
    int version = (p[1] >> 3) & 3;      // 0 MPEG-2.5, 2 MPEG-2, 3 MPEG-1
    int layer = 4 - ((p[1] >> 1) & 3);  // 1-3
    int bitIndex = p[2] >> 4, rateIndex = (p[2] >> 2) & 3, padding = (p[2] >> 1) & 1;
    if (version == 1 || layer == 4 || bitIndex == 0 || bitIndex == 15 || rateIndex == 3)
        return std::nullopt;
    int bitRate = kBitRates[version == 3 ? 0 : 1][layer - 1][bitIndex] * 1000;
    int rate = kRates[rateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
    FrameHeader h;
    h.Rate = rate;
    if (layer == 1)
    {
        h.Samples = 384;
        h.Bytes = (12 * bitRate / rate + padding) * 4;
    }
    else
    {
        bool half = layer == 3 && version != 3;
        h.Samples = half ? 576 : 1152;
        h.Bytes = (half ? 72 : 144) * bitRate / rate + padding;
    }
    if (h.Bytes < 4)
        return std::nullopt;
    return h;
}

bool IsMpegAudio(const Bytes& data)
{
    return FindFrame(data, 0, 4096).has_value();
}

Bytes From(const Bytes& audio, int64_t milliseconds)
{
    auto at = FindFrame(audio, 0, audio.size());
    if (!at || milliseconds <= 0)
        return audio;
    // The frames' samples counted up to the time
    int64_t samples = 0;
    size_t p = *at;
    while (p + 4 <= audio.size())
    {
        auto h = ReadHeader(audio.data() + p);
        if (!h)
        {
            auto next = FindFrame(audio, p + 1, audio.size());
            if (!next)
                break;
            p = *next;
            continue;
        }
        if (samples * 1000 >= milliseconds * h->Rate)
            return Bytes(audio.begin() + std::ptrdiff_t(p), audio.end());
        samples += h->Samples;
        p += size_t(h->Bytes);
    }
    return {};
}

} // namespace openshiina::formats::MpegAudio
