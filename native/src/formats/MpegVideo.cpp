// C# Formats/MpegVideo.cs (see MpegVideo.h)
#include "formats/MpegVideo.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <stdexcept>
#include <string>
#include <utility>

#include "scripting/ParallelRows.h"

namespace openshiina::formats {

namespace ParallelRows = scripting::ParallelRows;

namespace {

/// A slice that cannot be decoded (C#: InvalidDataException or an index out of range).
struct BadSlice : std::runtime_error
{
    using std::runtime_error::runtime_error;
};

const int kZigZag[64] = {
    0,  1,  8,  16, 9,  2,  3,  10, 17, 24, 32, 25, 18, 11, 4,  5,  12, 19, 26, 33, 40, 48,
    41, 34, 27, 20, 13, 6,  7,  14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23,
    30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
};

const int kDefaultIntra[64] = {
    8,  16, 19, 22, 26, 27, 29, 34, 16, 16, 22, 24, 27, 29, 34, 37, 19, 22, 26, 27, 29, 34,
    34, 38, 22, 22, 26, 27, 29, 34, 37, 40, 22, 26, 27, 29, 32, 35, 40, 48, 26, 27, 29, 32,
    35, 40, 48, 58, 26, 27, 29, 34, 38, 46, 56, 69, 27, 29, 35, 38, 46, 56, 69, 83,
};

const double kFrameRates[16] = {0, 24000.0 / 1001, 24, 25, 30000.0 / 1001, 30, 50, 60000.0 / 1001, 60, 0, 0, 0, 0, 0, 0, 0};

/// A lookup table of bits bits from (code, value) pairs: entry = value shl 5 | code length (0 = no code).
std::vector<int> BuildVlc(int bits, std::initializer_list<std::pair<const char*, int>> codes)
{
    std::vector<int> table(size_t(1) << bits);
    for (const auto& [text, value] : codes)
    {
        int length = 0, prefix = 0;
        for (const char* p = text; *p; p++)
            if (*p == '0' || *p == '1')
            {
                prefix = prefix << 1 | (*p - '0');
                length++;
            }
        int shift = bits - length;
        for (int i = 0; i < 1 << shift; i++)
        {
            int index = prefix << shift | i;
            if (table[size_t(index)] != 0)
                throw std::logic_error(std::string("VLC code ") + text + " overlaps another");
            table[size_t(index)] = value << 5 | length;
        }
    }
    return table;
}

const std::vector<int> kAddressIncrement = BuildVlc(11, {
    {"1", 1}, {"011", 2}, {"010", 3}, {"0011", 4}, {"0010", 5}, {"0001 1", 6}, {"0001 0", 7},
    {"0000 111", 8}, {"0000 110", 9}, {"0000 1011", 10}, {"0000 1010", 11}, {"0000 1001", 12},
    {"0000 1000", 13}, {"0000 0111", 14}, {"0000 0110", 15}, {"0000 0101 11", 16},
    {"0000 0101 10", 17}, {"0000 0101 01", 18}, {"0000 0101 00", 19}, {"0000 0100 11", 20},
    {"0000 0100 10", 21}, {"0000 0100 011", 22}, {"0000 0100 010", 23}, {"0000 0100 001", 24},
    {"0000 0100 000", 25}, {"0000 0011 111", 26}, {"0000 0011 110", 27}, {"0000 0011 101", 28},
    {"0000 0011 100", 29}, {"0000 0011 011", 30}, {"0000 0011 010", 31}, {"0000 0011 001", 32},
    {"0000 0011 000", 33}, {"0000 0001 111", 34}, {"0000 0001 000", 35},
});

// Macroblock types: 0x10 quant, 8 forward, 4 backward, 2 pattern, 1 intra
const std::vector<int> kTypeI = BuildVlc(2, {{"1", 0x01}, {"01", 0x11}});
const std::vector<int> kTypeP = BuildVlc(6, {
    {"1", 0x0A}, {"01", 0x02}, {"001", 0x08}, {"0001 1", 0x01}, {"0001 0", 0x1A}, {"0000 1", 0x12}, {"0000 01", 0x11},
});
const std::vector<int> kTypeB = BuildVlc(6, {
    {"10", 0x0C}, {"11", 0x0E}, {"010", 0x04}, {"011", 0x06}, {"0010", 0x08}, {"0011", 0x0A},
    {"0001 1", 0x01}, {"0001 0", 0x1E}, {"0000 11", 0x1A}, {"0000 10", 0x16}, {"0000 01", 0x11},
});

const std::vector<int> kCodedBlockPattern = BuildVlc(9, {
    {"111", 60}, {"1101", 4}, {"1100", 8}, {"1011", 16}, {"1010", 32}, {"1001 1", 12}, {"1001 0", 48},
    {"1000 1", 20}, {"1000 0", 40}, {"0111 1", 28}, {"0111 0", 44}, {"0110 1", 52}, {"0110 0", 56},
    {"0101 1", 1}, {"0101 0", 61}, {"0100 1", 2}, {"0100 0", 62}, {"0011 11", 24}, {"0011 10", 36},
    {"0011 01", 3}, {"0011 00", 63}, {"0010 111", 5}, {"0010 110", 9}, {"0010 101", 17},
    {"0010 100", 33}, {"0010 011", 6}, {"0010 010", 10}, {"0010 001", 18}, {"0010 000", 34},
    {"0001 1111", 7}, {"0001 1110", 11}, {"0001 1101", 19}, {"0001 1100", 35}, {"0001 1011", 13},
    {"0001 1010", 49}, {"0001 1001", 21}, {"0001 1000", 41}, {"0001 0111", 14}, {"0001 0110", 50},
    {"0001 0101", 22}, {"0001 0100", 42}, {"0001 0011", 15}, {"0001 0010", 51}, {"0001 0001", 23},
    {"0001 0000", 43}, {"0000 1111", 25}, {"0000 1110", 37}, {"0000 1101", 26}, {"0000 1100", 38},
    {"0000 1011", 29}, {"0000 1010", 45}, {"0000 1001", 53}, {"0000 1000", 57}, {"0000 0111", 30},
    {"0000 0110", 46}, {"0000 0101", 54}, {"0000 0100", 58}, {"0000 0011 1", 31}, {"0000 0011 0", 47},
    {"0000 0010 1", 55}, {"0000 0010 0", 59}, {"0000 0001 1", 27}, {"0000 0001 0", 39},
});

// Motion codes without their sign bit
const std::vector<int> kMotion = BuildVlc(11, {
    {"1", 0}, {"01", 1}, {"001", 2}, {"0001", 3}, {"0000 11", 4}, {"0000 101", 5}, {"0000 100", 6},
    {"0000 011", 7}, {"0000 0101 1", 8}, {"0000 0101 0", 9}, {"0000 0100 1", 10}, {"0000 0100 01", 11},
    {"0000 0100 00", 12}, {"0000 0011 11", 13}, {"0000 0011 10", 14}, {"0000 0011 01", 15},
    {"0000 0011 00", 16},
});

const std::vector<int> kDcSizeLuma = BuildVlc(7, {
    {"100", 0}, {"00", 1}, {"01", 2}, {"101", 3}, {"110", 4}, {"1110", 5}, {"1111 0", 6}, {"1111 10", 7}, {"1111 110", 8},
});
const std::vector<int> kDcSizeChroma = BuildVlc(8, {
    {"00", 0}, {"01", 1}, {"10", 2}, {"110", 3}, {"1110", 4}, {"1111 0", 5}, {"1111 10", 6}, {"1111 110", 7}, {"1111 1110", 8},
});

constexpr int EndOfBlock = 0x3FE, Escape = 0x3FF;

// dct_coeff_next without the sign bit: run << 8 | level, or end of block / escape
const std::vector<int> kDctCoefficients = BuildVlc(16, {
    {"10", EndOfBlock}, {"0000 01", Escape},
    {"11", 0x001}, {"011", 0x101}, {"0100", 0x002}, {"0101", 0x201}, {"0010 1", 0x003},
    {"0011 1", 0x301}, {"0011 0", 0x401}, {"0001 10", 0x102}, {"0001 11", 0x501},
    {"0001 01", 0x601}, {"0001 00", 0x701}, {"0000 110", 0x004}, {"0000 100", 0x202},
    {"0000 111", 0x801}, {"0000 101", 0x901}, {"0010 0110", 0x005}, {"0010 0001", 0x006},
    {"0010 0101", 0x103}, {"0010 0100", 0x302}, {"0010 0111", 0xA01}, {"0010 0011", 0xB01},
    {"0010 0010", 0xC01}, {"0010 0000", 0xD01}, {"0000 0010 10", 0x007}, {"0000 0011 00", 0x104},
    {"0000 0010 11", 0x203}, {"0000 0011 11", 0x402}, {"0000 0010 01", 0x502},
    {"0000 0011 10", 0xE01}, {"0000 0011 01", 0xF01}, {"0000 0010 00", 0x1001},
    {"0000 0001 1101", 0x008}, {"0000 0001 1000", 0x009}, {"0000 0001 0011", 0x00A},
    {"0000 0001 0000", 0x00B}, {"0000 0001 1011", 0x105}, {"0000 0001 0100", 0x204},
    {"0000 0001 1100", 0x303}, {"0000 0001 0010", 0x403}, {"0000 0001 1110", 0x602},
    {"0000 0001 0101", 0x702}, {"0000 0001 0001", 0x802}, {"0000 0001 1111", 0x1101},
    {"0000 0001 1010", 0x1201}, {"0000 0001 1001", 0x1301}, {"0000 0001 0111", 0x1401},
    {"0000 0001 0110", 0x1501}, {"0000 0000 1101 0", 0x00C}, {"0000 0000 1100 1", 0x00D},
    {"0000 0000 1100 0", 0x00E}, {"0000 0000 1011 1", 0x00F}, {"0000 0000 1011 0", 0x106},
    {"0000 0000 1010 1", 0x107}, {"0000 0000 1010 0", 0x205}, {"0000 0000 1001 1", 0x304},
    {"0000 0000 1001 0", 0x503}, {"0000 0000 1000 1", 0x902}, {"0000 0000 1000 0", 0xA02},
    {"0000 0000 1111 1", 0x1601}, {"0000 0000 1111 0", 0x1701}, {"0000 0000 1110 1", 0x1801},
    {"0000 0000 1110 0", 0x1901}, {"0000 0000 1101 1", 0x1A01}, {"0000 0000 0111 11", 0x010},
    {"0000 0000 0111 10", 0x011}, {"0000 0000 0111 01", 0x012}, {"0000 0000 0111 00", 0x013},
    {"0000 0000 0110 11", 0x014}, {"0000 0000 0110 10", 0x015}, {"0000 0000 0110 01", 0x016},
    {"0000 0000 0110 00", 0x017}, {"0000 0000 0101 11", 0x018}, {"0000 0000 0101 10", 0x019},
    {"0000 0000 0101 01", 0x01A}, {"0000 0000 0101 00", 0x01B}, {"0000 0000 0100 11", 0x01C},
    {"0000 0000 0100 10", 0x01D}, {"0000 0000 0100 01", 0x01E}, {"0000 0000 0100 00", 0x01F},
    {"0000 0000 0011 000", 0x020}, {"0000 0000 0010 111", 0x021}, {"0000 0000 0010 110", 0x022},
    {"0000 0000 0010 101", 0x023}, {"0000 0000 0010 100", 0x024}, {"0000 0000 0010 011", 0x025},
    {"0000 0000 0010 010", 0x026}, {"0000 0000 0010 001", 0x027}, {"0000 0000 0010 000", 0x028},
    {"0000 0000 0011 111", 0x108}, {"0000 0000 0011 110", 0x109}, {"0000 0000 0011 101", 0x10A},
    {"0000 0000 0011 100", 0x10B}, {"0000 0000 0011 011", 0x10C}, {"0000 0000 0011 010", 0x10D},
    {"0000 0000 0011 001", 0x10E}, {"0000 0000 0001 0011", 0x10F}, {"0000 0000 0001 0010", 0x110},
    {"0000 0000 0001 0001", 0x111}, {"0000 0000 0001 0000", 0x112}, {"0000 0000 0001 0100", 0x603},
    {"0000 0000 0001 1010", 0xB02}, {"0000 0000 0001 1001", 0xC02}, {"0000 0000 0001 1000", 0xD02},
    {"0000 0000 0001 0111", 0xE02}, {"0000 0000 0001 0110", 0xF02}, {"0000 0000 0001 0101", 0x1002},
    {"0000 0000 0001 1111", 0x1B01}, {"0000 0000 0001 1110", 0x1C01}, {"0000 0000 0001 1101", 0x1D01},
    {"0000 0000 0001 1100", 0x1E01}, {"0000 0000 0001 1011", 0x1F01},
});

// kIdct[x * 8 + u] = C(u) / 2 * cos((2x + 1) u pi / 16)
struct IdctTable
{
    double T[64];
    IdctTable()
    {
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                T[x * 8 + u] = (u == 0 ? std::sqrt(0.5) : 1) / 2 * std::cos((2 * x + 1) * u * 3.14159265358979323846 / 16);
    }
};
const IdctTable kIdct;

/// The inverse DCT of an 8 x 8 block in place, rounded to the nearest integer. Each output is the
/// sum of the same products in the same order as the C# player's (sum over u, then over v), with
/// no fused multiply-add, so the rounding is the same.
void Idct(int* block)
{
    bool dcOnly = true;
    for (int i = 1; i < 64 && dcOnly; i++)
        dcOnly = block[i] == 0;
    if (dcOnly)
    {
        int v = int(std::floor(block[0] / 8.0 + 0.5));
        std::fill(block, block + 64, v);
        return;
    }
    const double* c = kIdct.T;
    // Rows: tmp[v][x] = sum_u F[v][u] c(x, u)
    double tmp[64];
    for (int v = 0; v < 8; v++)
    {
        const int* row = block + v * 8;
        bool zero = true;
        for (int u = 0; u < 8 && zero; u++)
            zero = row[u] == 0;
        for (int x = 0; x < 8; x++)
        {
            double s = 0;
            if (!zero)
                for (int u = 0; u < 8; u++)
                    s += double(row[u]) * c[x * 8 + u];
            tmp[v * 8 + x] = s;
        }
    }
    // Columns: f[y][x] = sum_v tmp[v][x] c(y, v)
    for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            double s = 0;
            for (int v = 0; v < 8; v++)
                s += tmp[v * 8 + x] * c[y * 8 + v];
            block[y * 8 + x] = int(std::floor(s + 0.5));
        }
}

uint8_t Clamp255(int v) { return uint8_t(v < 0 ? 0 : v > 255 ? 255 : v); }

constexpr int PictureI = 1, PictureP = 2, PictureB = 3;

} // namespace

// ---- MpegSystemStream ----

MpegSystemStream::MpegSystemStream(const Bytes& file)
{
    // The payloads first, then each stream copied into an array of its size
    struct Part
    {
        size_t Start, Length;
        bool Video;
    };
    std::vector<Part> parts;
    size_t videoLength = 0, audioLength = 0;
    int videoId = -1, audioId = -1;
    size_t pos = 0, n = file.size();
    while (pos + 4 <= n)
    {
        if (file[pos] != 0 || file[pos + 1] != 0 || file[pos + 2] != 1)
        {
            pos++;
            continue;
        }
        int code = file[pos + 3];
        if (code == 0xB9)
            break;
        if (code == 0xBA)
        {
            if (pos + 5 > n)
                break;
            // MPEG-1 pack header: 12 bytes; MPEG-2: 14 plus stuffing
            if ((file[pos + 4] & 0xC0) == 0x40)
                pos += 14 + (pos + 13 < n ? file[pos + 13] & 7 : 0);
            else
                pos += 12;
            continue;
        }
        if (code < 0xBB || pos + 6 > n)
        {
            pos += 4;
            continue;
        }
        size_t length = size_t(file[pos + 4]) << 8 | file[pos + 5];
        size_t start = pos + 6, end = std::min(start + length, n);
        pos = end;
        bool isVideo = code >= 0xE0 && code <= 0xEF, isAudio = code >= 0xC0 && code <= 0xDF;
        if (!isVideo && !isAudio)
            continue;
        if (isVideo && (videoId == -1 ? (videoId = code) : videoId) != code)
            continue;
        if (isAudio && (audioId == -1 ? (audioId = code) : audioId) != code)
            continue;
        size_t p = start;
        if (p < end && (file[p] & 0xC0) == 0x80)
        {
            // MPEG-2 PES header
            if (p + 3 > end)
                continue;
            p += 3 + file[p + 2];
        }
        else
        {
            while (p < end && file[p] == 0xFF)
                p++;
            if (p < end && (file[p] & 0xC0) == 0x40)
                p += 2;
            if (p < end)
            {
                int flags = file[p] & 0xF0;
                p += flags == 0x20 ? 5 : flags == 0x30 ? 10 : 1;
            }
        }
        if (p < end)
        {
            parts.push_back({p, end - p, isVideo});
            (isVideo ? videoLength : audioLength) += end - p;
        }
    }
    if (videoLength + Mpeg1Video::Padding > size_t(0x7FFFFFC7))
        throw InvalidDataError("Video stream too large");
    VideoPadded.assign(videoLength + Mpeg1Video::Padding, 0);
    VideoLength = int(videoLength);
    Audio.resize(audioLength);
    size_t v = 0, a = 0;
    for (const auto& part : parts)
    {
        if (part.Video)
        {
            std::memcpy(VideoPadded.data() + v, file.data() + part.Start, part.Length);
            v += part.Length;
        }
        else
        {
            std::memcpy(Audio.data() + a, file.data() + part.Start, part.Length);
            a += part.Length;
        }
    }
}

// ---- MpegFrame ----

MpegFrame::MpegFrame(int width, int height, int mbWidth, int mbHeight)
    : Width(width), Height(height), Stride(mbWidth * 16), ChromaStride(mbWidth * 8), Y(size_t(Stride) * size_t(mbHeight) * 16),
      Cb(size_t(ChromaStride) * size_t(mbHeight) * 8), Cr(size_t(ChromaStride) * size_t(mbHeight) * 8)
{
}

void MpegFrame::ToBgr24(uint8_t* dst, int pitch, int width, int height) const
{
    width = std::min(width, Width);
    height = std::min(height, Height);
    if (width <= 0 || height <= 0)
        return;
    auto rows = [&](int from, int to) {
        for (int y = from; y < to; y++)
        {
            uint8_t* row = dst + int64_t(y) * pitch;
            int yi = y * Stride, ci = (y >> 1) * ChromaStride;
            for (int x = 0; x < width; x++)
            {
                int c = ci + (x >> 1);
                int l = (Y[size_t(yi + x)] - 16) * 76309;
                int cb = Cb[size_t(c)] - 128, cr = Cr[size_t(c)] - 128;
                row[x * 3] = Clamp255((l + 132201 * cb + 32768) >> 16);
                row[x * 3 + 1] = Clamp255((l - 25675 * cb - 53279 * cr + 32768) >> 16);
                row[x * 3 + 2] = Clamp255((l + 104597 * cr + 32768) >> 16);
            }
        }
    };
    // The rows on several cores (half of a 1280 x 720 movie's time on one)
    if (pitch >= width * 3)
        ParallelRows::For(height, int64_t(width) * height, rows);
    else
        rows(0, height);
}

// ---- The slices ----

/// The decoding of slices: a picture's slices write macroblocks of their own, so each runs on a
/// decoder of its own (bit position, quantiser, motion predictors, DC predictors, block) and the
/// picture's slices are shared out between the cores. The picture (its type, motion sizes,
/// reference frames, matrices) is the video's, only read here.
class SliceDecoder
{
public:
    explicit SliceDecoder(const Mpeg1Video& video) : v(video) {}

    /// The slice whose start code (vertical position code) ends at bit bit.
    void Decode(int code, int bit)
    {
        m_bit = bit;
        DecodeSlice(code);
    }

private:
    int Peek(int count) const
    {
        size_t b = size_t(m_bit >> 3);
        if (m_bit < 0 || b + 4 >= v.m_data.size())
            throw BadSlice("read past the stream");
        const uint8_t* d = v.m_data.data();
        uint32_t x = uint32_t(d[b]) << 24 | uint32_t(d[b + 1]) << 16 | uint32_t(d[b + 2]) << 8 | d[b + 3];
        x = (x << (m_bit & 7)) | uint32_t(d[b + 4] >> (8 - (m_bit & 7)));
        return int(x >> (32 - count));
    }

    int Read(int count)
    {
        int x = Peek(count);
        m_bit += count;
        return x;
    }

    bool ReadBit() { return Read(1) != 0; }
    bool AtStartCode() const { return Peek(23) == 0; }

    int ReadVlc(const std::vector<int>& table, int bits)
    {
        int entry = table[size_t(Peek(bits))];
        if (entry == 0)
            throw BadSlice("bad VLC");
        m_bit += entry & 0x1F;
        return entry >> 5;
    }

    void DecodeSlice(int verticalPosition)
    {
        m_quantScale = Read(5);
        while (ReadBit())
            Read(8);
        m_mbAddress = (verticalPosition - 1) * v.m_mbWidth - 1;
        ResetMotion();
        ResetDc();
        bool first = true;
        do
        {
            DecodeMacroblock(first);
            first = false;
        }
        while (m_bit < v.m_end && !AtStartCode());
    }

    void ResetMotion() { m_forwardH = m_forwardV = m_backwardH = m_backwardV = 0; }
    void ResetDc() { m_dcY = m_dcCb = m_dcCr = 1024; }

    void SetPosition()
    {
        m_mbRow = m_mbAddress / v.m_mbWidth;
        m_mbCol = m_mbAddress % v.m_mbWidth;
        if (m_mbAddress < 0 || m_mbRow >= v.m_mbHeight)
            throw BadSlice("macroblock outside the picture");
    }

    void DecodeMacroblock(bool first)
    {
        int increment = 0;
        for (;;)
        {
            int x = ReadVlc(kAddressIncrement, 11);
            if (x == 34)
                continue;                   // stuffing
            if (x == 35)
            {
                increment += 33;            // escape
                continue;
            }
            increment += x;
            break;
        }
        if (first)
            m_mbAddress += increment;
        else
        {
            // Skipped macroblocks
            for (int k = 1; k < increment; k++)
            {
                m_mbAddress++;
                SetPosition();
                ResetDc();
                if (v.m_pictureType == PictureP)
                {
                    m_forwardH = m_forwardV = 0;
                    m_mvForwardH = m_mvForwardV = 0;
                    m_mbForward = true;
                    m_mbBackward = false;
                    Predict();
                }
                else if (v.m_pictureType == PictureB)
                    Predict();
                else
                    throw BadSlice("skipped macroblock in an I picture");
            }
            m_mbAddress++;
        }
        SetPosition();

        int type = v.m_pictureType == PictureI ? ReadVlc(kTypeI, 2) : v.m_pictureType == PictureP ? ReadVlc(kTypeP, 6) : ReadVlc(kTypeB, 6);
        bool intra = (type & 1) != 0;
        if ((type & 0x10) != 0)
            m_quantScale = Read(5);
        if (intra)
        {
            ResetMotion();
            m_mbForward = m_mbBackward = false;
        }
        else
        {
            ResetDc();
            m_mbForward = (type & 8) != 0;
            m_mbBackward = (type & 4) != 0;
            if (m_mbForward)
            {
                m_forwardH = DecodeMotion(v.m_forwardRSize, m_forwardH);
                m_forwardV = DecodeMotion(v.m_forwardRSize, m_forwardV);
                m_mvForwardH = v.m_fullPelForward ? m_forwardH << 1 : m_forwardH;
                m_mvForwardV = v.m_fullPelForward ? m_forwardV << 1 : m_forwardV;
            }
            else if (v.m_pictureType == PictureP)
            {
                // P macroblock without motion: zero vector, the predictor starts again
                m_forwardH = m_forwardV = 0;
                m_mvForwardH = m_mvForwardV = 0;
                m_mbForward = true;
            }
            if (m_mbBackward)
            {
                m_backwardH = DecodeMotion(v.m_backwardRSize, m_backwardH);
                m_backwardV = DecodeMotion(v.m_backwardRSize, m_backwardV);
                m_mvBackwardH = v.m_fullPelBackward ? m_backwardH << 1 : m_backwardH;
                m_mvBackwardV = v.m_fullPelBackward ? m_backwardV << 1 : m_backwardV;
            }
            Predict();
        }

        int pattern = intra ? 0x3F : (type & 2) != 0 ? ReadVlc(kCodedBlockPattern, 9) : 0;
        for (int b = 0; b < 6; b++)
            if ((pattern & (0x20 >> b)) != 0)
                DecodeBlock(b, intra);
    }

    int DecodeMotion(int rSize, int predictor)
    {
        int code = ReadVlc(kMotion, 11);
        if (code != 0 && ReadBit())
            code = -code;
        int f = 1 << rSize;
        int delta;
        if (f != 1 && code != 0)
        {
            int r = Read(rSize);
            delta = ((std::abs(code) - 1) << rSize) + r + 1;
            if (code < 0)
                delta = -delta;
        }
        else
            delta = code;
        int x = predictor + delta;
        if (x > (f << 4) - 1)
            x -= f << 5;
        else if (x < -(f << 4))
            x += f << 5;
        return x;
    }

    /// The prediction of the macroblock from the reference pictures (no residual yet).
    void Predict()
    {
        if (m_mbForward)
        {
            PredictFrom(*v.m_forwardRef, m_mvForwardH, m_mvForwardV, false);
            if (m_mbBackward)
                PredictFrom(*v.m_backwardRef, m_mvBackwardH, m_mvBackwardV, true);
        }
        else if (m_mbBackward)
            PredictFrom(*v.m_backwardRef, m_mvBackwardH, m_mvBackwardV, false);
    }

    void PredictFrom(const MpegFrame& reference, int h, int vv, bool average)
    {
        MpegFrame& cur = *v.m_current;
        PredictBlock(reference.Y.data(), cur.Y.data(), cur.Stride, v.m_mbHeight * 16, m_mbCol * 16, m_mbRow * 16, 16, h, vv, average);
        h /= 2;
        vv /= 2;
        PredictBlock(reference.Cb.data(), cur.Cb.data(), cur.ChromaStride, v.m_mbHeight * 8, m_mbCol * 8, m_mbRow * 8, 8, h, vv, average);
        PredictBlock(reference.Cr.data(), cur.Cr.data(), cur.ChromaStride, v.m_mbHeight * 8, m_mbCol * 8, m_mbRow * 8, 8, h, vv, average);
    }

    static void PredictBlock(const uint8_t* src, uint8_t* dst, int stride, int rows, int x0, int y0, int size, int h, int vv, bool average)
    {
        int sx = x0 + (h >> 1), sy = y0 + (vv >> 1);
        bool hx = (h & 1) != 0, hy = (vv & 1) != 0;
        int maxX = stride - 1, maxY = rows - 1;
        bool inside = sx >= 0 && sy >= 0 && sx + size + (hx ? 1 : 0) <= stride && sy + size + (hy ? 1 : 0) <= rows;
        if (inside)
        {
            for (int y = 0; y < size; y++)
            {
                uint8_t* to = dst + (y0 + y) * stride + x0;
                const uint8_t* a = src + (sy + y) * stride + sx;
                if (!hx && !hy)
                {
                    if (!average)
                    {
                        std::memcpy(to, a, size_t(size));
                        continue;
                    }
                    for (int x = 0; x < size; x++)
                        to[x] = uint8_t((to[x] + a[x] + 1) >> 1);
                    continue;
                }
                if (!hy)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int p = (a[x] + a[x + 1] + 1) >> 1;
                        to[x] = average ? uint8_t((to[x] + p + 1) >> 1) : uint8_t(p);
                    }
                    continue;
                }
                const uint8_t* b = a + stride;
                if (!hx)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int p = (a[x] + b[x] + 1) >> 1;
                        to[x] = average ? uint8_t((to[x] + p + 1) >> 1) : uint8_t(p);
                    }
                    continue;
                }
                for (int x = 0; x < size; x++)
                {
                    int p = (a[x] + a[x + 1] + b[x] + b[x + 1] + 2) >> 2;
                    to[x] = average ? uint8_t((to[x] + p + 1) >> 1) : uint8_t(p);
                }
            }
            return;
        }
        // Partly outside the reference: its edge pixels repeated
        for (int y = 0; y < size; y++)
        {
            int d = (y0 + y) * stride + x0;
            for (int x = 0; x < size; x++)
            {
                int ax = std::clamp(sx + x, 0, maxX), bx = std::clamp(sx + x + 1, 0, maxX);
                int ay = std::clamp(sy + y, 0, maxY), by = std::clamp(sy + y + 1, 0, maxY);
                int a = src[ay * stride + ax];
                int p;
                if (!hx && !hy)
                    p = a;
                else if (hx && !hy)
                    p = (a + src[ay * stride + bx] + 1) >> 1;
                else if (!hx)
                    p = (a + src[by * stride + ax] + 1) >> 1;
                else
                    p = (a + src[ay * stride + bx] + src[by * stride + ax] + src[by * stride + bx] + 2) >> 2;
                dst[d + x] = average ? uint8_t((dst[d + x] + p + 1) >> 1) : uint8_t(p);
            }
        }
    }

    void DecodeBlock(int b, bool intra)
    {
        int* block = m_block;
        std::fill(block, block + 64, 0);
        int n;
        const int* matrix;
        if (intra)
        {
            int size = b < 4 ? ReadVlc(kDcSizeLuma, 7) : ReadVlc(kDcSizeChroma, 8);
            int diff = 0;
            if (size > 0)
            {
                diff = Read(size);
                if ((diff & (1 << (size - 1))) == 0)
                    diff -= (1 << size) - 1;
            }
            int& predictor = b < 4 ? m_dcY : b == 4 ? m_dcCb : m_dcCr;
            predictor += diff * 8;
            block[0] = predictor;
            n = 1;
            matrix = v.m_intraMatrix;
        }
        else
        {
            n = 0;
            matrix = v.m_nonIntraMatrix;
        }

        for (;;)
        {
            int run, level;
            if (n == 0 && !intra && Peek(1) == 1)
            {
                // dct_coeff_first: "1s" is run 0, level 1
                m_bit++;
                run = 0;
                level = ReadBit() ? -1 : 1;
            }
            else
            {
                int entry = ReadVlc(kDctCoefficients, 16);
                if (entry == EndOfBlock)
                    break;
                if (entry == Escape)
                {
                    run = Read(6);
                    level = Read(8);
                    if (level == 0)
                        level = Read(8);
                    else if (level == 128)
                        level = Read(8) - 256;
                    else if (level > 128)
                        level -= 256;
                }
                else
                {
                    run = entry >> 8;
                    level = entry & 0xFF;
                    if (ReadBit())
                        level = -level;
                }
            }
            n += run;
            if (n > 63)
                throw BadSlice("too many coefficients");
            int z = kZigZag[n];
            int sign = level < 0 ? -1 : 1;
            int c = intra ? 2 * level * m_quantScale * matrix[z] / 16 : (2 * level + sign) * m_quantScale * matrix[z] / 16;
            if ((c & 1) == 0 && c != 0)
                c -= sign;
            block[z] = std::clamp(c, -2048, 2047);
            n++;
        }

        MpegFrame& cur = *v.m_current;
        uint8_t* plane;
        int stride, offset;
        if (b < 4)
        {
            plane = cur.Y.data();
            stride = cur.Stride;
            offset = (m_mbRow * 16 + (b >> 1) * 8) * stride + m_mbCol * 16 + (b & 1) * 8;
        }
        else
        {
            plane = b == 4 ? cur.Cb.data() : cur.Cr.data();
            stride = cur.ChromaStride;
            offset = m_mbRow * 8 * stride + m_mbCol * 8;
        }
        Idct(block);
        for (int y = 0; y < 8; y++, offset += stride)
        {
            uint8_t* row = plane + offset;
            if (intra)
                for (int x = 0; x < 8; x++)
                    row[x] = Clamp255(block[y * 8 + x]);
            else
                for (int x = 0; x < 8; x++)
                    row[x] = Clamp255(block[y * 8 + x] + row[x]);
        }
    }

    const Mpeg1Video& v;
    int m_bit = 0;
    int m_quantScale = 0;
    int m_mbAddress = 0, m_mbRow = 0, m_mbCol = 0;
    int m_forwardH = 0, m_forwardV = 0, m_backwardH = 0, m_backwardV = 0;          // predictors, full or half pel
    int m_mvForwardH = 0, m_mvForwardV = 0, m_mvBackwardH = 0, m_mvBackwardV = 0;  // half pel
    bool m_mbForward = false, m_mbBackward = false;
    int m_dcY = 1024, m_dcCb = 1024, m_dcCr = 1024;
    int m_block[64] = {};
};

// ---- Mpeg1Video ----

namespace {

Bytes Padded(const Bytes& stream)
{
    Bytes data(stream.size() + size_t(Mpeg1Video::Padding), 0);
    std::memcpy(data.data(), stream.data(), stream.size());
    return data;
}

} // namespace

Mpeg1Video::Mpeg1Video(const Bytes& stream) : Mpeg1Video(Padded(stream), int(stream.size())) {}

Mpeg1Video::Mpeg1Video(MpegSystemStream&& system) : Mpeg1Video(std::move(system.VideoPadded), system.VideoLength) {}

Mpeg1Video::Mpeg1Video(Bytes data, int length) : m_data(std::move(data)), m_end(length * 8)
{
    if (!NextStartCode(0xB3))
        throw InvalidDataError("No MPEG-1 sequence header");
    ReadSequenceHeader();
    m_bit = 0;
}

void Mpeg1Video::Rewind()
{
    m_bit = 0;
    m_older = m_newer = nullptr;
    m_flushed = false;
}

const MpegFrame* Mpeg1Video::Next()
{
    for (;;)
    {
        int code = NextStartCode(-1) ? m_data[size_t(m_bit >> 3) - 1] : -1;
        if (code == -1)
        {
            if (!m_flushed && m_newer != nullptr)
            {
                m_flushed = true;
                return m_newer;
            }
            return nullptr;
        }
        if (code == 0xB3)
            ReadSequenceHeader();
        else if (code == 0x00)
            if (const MpegFrame* frame = DecodePicture())
                return frame;
    }
}

int Mpeg1Video::Peek(int count) const
{
    size_t b = size_t(m_bit >> 3);
    if (b + 4 >= m_data.size())
        throw InvalidDataError("Read past the MPEG-1 stream");
    const uint8_t* d = m_data.data();
    uint32_t x = uint32_t(d[b]) << 24 | uint32_t(d[b + 1]) << 16 | uint32_t(d[b + 2]) << 8 | d[b + 3];
    x = (x << (m_bit & 7)) | uint32_t(d[b + 4] >> (8 - (m_bit & 7)));
    return int(x >> (32 - count));
}

int Mpeg1Video::Read(int count)
{
    int x = Peek(count);
    m_bit += count;
    return x;
}

bool Mpeg1Video::NextStartCode(int code)
{
    // Moves just past the next start code (00 00 01 xx) from the next byte boundary; with code >= 0,
    // past the next start code of that value. False at the end
    int p = (m_bit + 7) >> 3, end = m_end >> 3;
    for (; p + 3 < end; p++)
        if (m_data[size_t(p)] == 0 && m_data[size_t(p) + 1] == 0 && m_data[size_t(p) + 2] == 1 && (code < 0 || m_data[size_t(p) + 3] == code))
        {
            m_bit = (p + 4) * 8;
            return true;
        }
    m_bit = m_end;
    return false;
}

void Mpeg1Video::ReadSequenceHeader()
{
    int width = Read(12), height = Read(12);
    Read(4);                            // aspect ratio
    double rate = kFrameRates[Read(4)];
    Read(18);                           // bit rate
    Read(1);
    Read(10);                           // VBV buffer size
    Read(1);                            // constrained parameters
    if (ReadBit())
        for (int i = 0; i < 64; i++)
            m_intraMatrix[kZigZag[i]] = Read(8);
    else
        std::memcpy(m_intraMatrix, kDefaultIntra, sizeof m_intraMatrix);
    if (ReadBit())
        for (int i = 0; i < 64; i++)
            m_nonIntraMatrix[kZigZag[i]] = Read(8);
    else
        std::fill(m_nonIntraMatrix, m_nonIntraMatrix + 64, 16);
    if (rate > 0)
        m_frameRate = rate;
    if (width != m_width || height != m_height || !m_bFrame)
    {
        if (width <= 0 || height <= 0)
            throw InvalidDataError("An MPEG-1 picture of no size");
        m_width = width;
        m_height = height;
        m_mbWidth = (width + 15) >> 4;
        m_mbHeight = (height + 15) >> 4;
        m_older = m_newer = nullptr;
        m_anchor0 = NewFrame();
        m_anchor1 = NewFrame();
        m_bFrame = NewFrame();
    }
}

std::unique_ptr<MpegFrame> Mpeg1Video::NewFrame() const { return std::make_unique<MpegFrame>(m_width, m_height, m_mbWidth, m_mbHeight); }

const MpegFrame* Mpeg1Video::DecodePicture()
{
    Read(10);                           // temporal reference
    m_pictureType = Read(3);
    Read(16);                           // VBV delay
    if (m_pictureType != PictureI && m_pictureType != PictureP && m_pictureType != PictureB)
        return nullptr;
    if (m_pictureType != PictureI)
    {
        m_fullPelForward = ReadBit();
        m_forwardRSize = Read(3) - 1;
        if (m_forwardRSize < 0)
            return nullptr;
    }
    if (m_pictureType == PictureB)
    {
        m_fullPelBackward = ReadBit();
        m_backwardRSize = Read(3) - 1;
        if (m_backwardRSize < 0)
            return nullptr;
    }
    if (m_pictureType == PictureB)
    {
        // Without two anchors (an open GOP at the start) the picture cannot be made
        if (m_older == nullptr || m_newer == nullptr)
            return nullptr;
        m_current = m_bFrame.get();
        m_forwardRef = m_older;
        m_backwardRef = m_newer;
    }
    else
    {
        if (m_pictureType == PictureP && m_newer == nullptr)
            return nullptr;
        // The older anchor is not needed any more (the B pictures before this one are done)
        m_current = m_newer == m_anchor0.get() ? m_anchor1.get() : m_anchor0.get();
        m_forwardRef = m_newer != nullptr ? m_newer : m_current;
        m_backwardRef = m_forwardRef;
    }

    // Slices up to the next picture, GOP or sequence start code: found first, then decoded on as
    // many cores as the pixel routines take (each writes macroblocks of its own)
    m_slices.clear();
    while (NextStartCode(-1))
    {
        int code = m_data[size_t(m_bit >> 3) - 1];
        if (code >= 0x01 && code <= 0xAF)
            m_slices.emplace_back(code, m_bit);
        else if (code == 0xB2 || code == 0xB5)
            continue;                   // user data, extension
        else
        {
            m_bit = (m_bit >> 3) * 8 - 32;
            break;
        }
    }
    std::atomic<int> errors{0};
    auto decode = [&](int from, int to) {
        SliceDecoder decoder(*this);
        for (int index = from; index < to; index++)
            try
            {
                decoder.Decode(m_slices[size_t(index)].first, m_slices[size_t(index)].second);
            }
            catch (const BadSlice&)
            {
                errors++;
            }
    };
    int count = int(m_slices.size());
    if (count >= 2 && ParallelRows::MaxParts() > 1)
        ParallelRows::For(count, int64_t(count) * ParallelRows::PieceWork, decode);
    else
        decode(0, count);
    m_errors += errors;

    if (m_pictureType == PictureB)
        return m_current;
    MpegFrame* shown = m_newer;
    m_older = m_newer;
    m_newer = m_current;
    return shown;
}

} // namespace openshiina::formats
