// C# Formats/AudioDecoder.cs (see AudioDecoder.h)
#include "audio/AudioDecoder.h"

#include <cstring>

namespace openshiina::audio::AudioDecoder {

namespace {

bool AsciiEqual(const Bytes& data, size_t at, const char* text)
{
    size_t n = std::strlen(text);
    return at + n <= data.size() && std::memcmp(data.data() + at, text, n) == 0;
}

void Pack16(int16_t v, uint8_t* p)
{
    p[0] = uint8_t(v);
    p[1] = uint8_t(uint16_t(v) >> 8);
}

// The doubles' bits as the C# decoder moves its stereo shift through table[12]'s high dword
int64_t Bits(double d)
{
    int64_t b;
    std::memcpy(&b, &d, 8);
    return b;
}

double FromBits(int64_t b)
{
    double d;
    std::memcpy(&d, &b, 8);
    return d;
}

/// The PCM of a PAD stream: 0x2C bytes of a WAVE header, then blocks of 4-bit samples.
Bytes UnpackPad(const uint8_t* input, size_t inputSize, int pcmSize, int channels)
{
    Bytes output(size_t(pcmSize) + 0x9C);
    int v10;
    double v3 = 0, v27 = 0, v28 = 0;
    int v30 = 0;
    double table[69] = {};
    table[4] = 0.9375;
    table[6] = 1.796875;
    table[7] = -0.8125;
    table[8] = 1.53125;
    table[9] = -0.859375;
    table[10] = 1.90625;
    table[11] = -0.9375;

    size_t dst = 0, src = 0;
    if (src >= inputSize)
        return output;
    int v5 = input[src++];
    while (v5 != 0xff && src < inputSize && dst < size_t(pcmSize))
    {
        int v7 = input[src++];
        int v29 = v7 >> 4;
        int v9 = v7 & 0xF;
        if (channels == 2)
        {
            if (src + 1 >= inputSize)
                break;
            int next = input[src + 1];
            v10 = next & 0xF;
            v30 = next >> 4;
            int64_t v = Bits(table[12]) & 0xffffffffLL;
            table[12] = FromBits(v | int64_t(v10) << 32);
            src += 2;
        }
        else
            v10 = int(Bits(table[12]) >> 32);
        int v12 = 14;
        for (int i = 0; i < 14; ++i)
        {
            if (src >= inputSize)
                break;
            int v13 = input[src++];
            int v14 = (v13 & 0xF) << 12;
            if ((v14 & 0x8000) != 0)
                v14 |= ~0xFFFF;
            int v15 = (v13 & 0xF0) << 8;
            table[v12 - 1] = double(v14 >> v9);
            if ((v15 & 0x8000) != 0)
                v15 |= ~0xFFFF;
            table[v12] = double(v15 >> v9);
            v12 += 2;
        }
        if (channels == 2)
        {
            v12 = 42;
            for (int i = 0; i < 14; ++i)
            {
                if (src >= inputSize)
                    break;
                int v18 = input[src++];
                int v19 = (v18 & 0xF) << 12;
                if ((v19 & 0x8000) != 0)
                    v19 |= ~0xFFFF;
                int v20 = uint8_t(v18 & 0xF0) << 8;
                table[v12 - 1] = double(v19 >> v10);
                if ((v20 & 0x8000) != 0)
                    v20 |= ~0xFFFF;
                table[v12] = double(v20 >> v10);
                v12 += 2;
            }
        }
        v12 = 41;
        for (int i = 0; i < 28 && dst + 1 < output.size(); ++i)
        {
            double v22 = v27 * table[2 * v29 + 3];
            v27 = table[0];
            table[v12 - 28] += v22 + v27 * table[2 * v29 + 2];
            table[0] = table[v12 - 28];
            Pack16(int16_t(table[v12 - 28] + 0.5), output.data() + dst);
            dst += 2;
            if (channels == 2)
            {
                if (dst + 1 >= output.size())
                    break;
                table[v12] += v28 * table[2 * v30 + 3] + v3 * table[2 * v30 + 2];
                v28 = v3;
                v3 = table[v12];
                Pack16(int16_t(table[v12] + 0.5), output.data() + dst);
                dst += 2;
            }
            ++v12;
        }
        if (src >= inputSize)
            break;
        v5 = input[src++];
    }
    return output;
}

} // namespace

bool IsOgv(const Bytes& data)
{
    return data.size() >= 24 && data[0] == 'O' && data[1] == 'G' && data[2] == 'V';
}

bool IsPad(const Bytes& data)
{
    return data.size() >= 0x2c && data[0] == 'P' && data[1] == 'A' && data[2] == 'D';
}

std::optional<Bytes> DecodeOgv(const Bytes& data)
{
    if (!IsOgv(data))
        return std::nullopt;
    size_t pos = 0xc;
    if (pos + 8 > data.size() || !AsciiEqual(data, pos, "fmt "))
        return std::nullopt;
    uint32_t fmtSize = LittleEndian::ToUInt32(data.data() + pos + 4);
    pos += 8 + size_t(fmtSize);
    if (pos + 8 > data.size() || !AsciiEqual(data, pos, "data"))
        return std::nullopt;
    pos += 8;
    if (pos >= data.size())
        return std::nullopt;
    return Bytes(data.begin() + std::ptrdiff_t(pos), data.end());
}

std::optional<Bytes> DecodePad(const Bytes& data)
{
    if (!IsPad(data))
        return std::nullopt;
    Bytes header(data.begin(), data.begin() + 0x2c);
    int pcmSize = int(LittleEndian::ToUInt32(header.data() + 0x28));
    int channels = LittleEndian::ToUInt16(header.data() + 0x16);
    if (pcmSize < 0)
        return std::nullopt;
    std::memcpy(header.data(), "RIFF", 4);
    LittleEndian::Pack(uint32_t(pcmSize + 0x24), header.data() + 4);
    Bytes pcm = UnpackPad(data.data() + 0x2c, data.size() - 0x2c, pcmSize, channels);
    Bytes result(size_t(0x2c) + size_t(pcmSize));
    std::memcpy(result.data(), header.data(), 0x2c);
    std::memcpy(result.data() + 0x2c, pcm.data(), size_t(pcmSize));
    return result;
}

} // namespace openshiina::audio::AudioDecoder
