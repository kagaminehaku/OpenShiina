// C# Formats/WmaProAudio.cs (its BitReaders are shared objects there; here the frame's reader is
// m_gb, used by reference)
#include "formats/WmaProAudio.h"

#include <algorithm>
#include <bit>
#include <cmath>
#include <cstring>
#include <numbers>
#include <span>

#include "formats/WmaTables.h"

namespace openshiina::formats {

namespace {

/// A table of symbol / length pairs (canonical codes in table order).
VlcTable FromPairs(int bits, std::span<const uint8_t> pairs, int offset)
{
    size_t n = pairs.size() / 2;
    std::vector<uint8_t> lengths(n);
    std::vector<int32_t> symbols(n);
    for (size_t i = 0; i < n; i++)
    {
        symbols[i] = pairs[2 * i];
        lengths[i] = pairs[2 * i + 1];
    }
    return VlcTable::FromLengths(bits, lengths, symbols, offset);
}

struct ProVlcs
{
    VlcTable Sf, SfRl, Coef0, Coef1, Vec4, Vec2, Vec1;
    std::array<float, 33> Sin64{};

    ProVlcs()
    {
        Sf = FromPairs(8, WmaTables::ProScaleTable, -60);
        SfRl = FromPairs(9, WmaTables::ProScaleRlTable, 0);
        Coef0 = VlcTable::FromLengths(9, std::span(WmaTables::ProCoef0Lens), std::span(WmaTables::ProCoef0Syms), 0);
        Coef1 = FromPairs(9, WmaTables::ProCoef1Table, 0);
        Vec4 = VlcTable::FromLengths(9, std::span(WmaTables::ProVec4Lens), std::span(WmaTables::ProVec4Syms), -1);
        Vec2 = FromPairs(9, WmaTables::ProVec2Table, -1);
        Vec1 = FromPairs(9, WmaTables::ProVec1Table, 0);
        for (int i = 0; i < 33; i++)
            Sin64[size_t(i)] = float(std::sin(i * std::numbers::pi / 64.0));
    }
};

const ProVlcs& Vlcs()
{
    static const ProVlcs vlcs;
    return vlcs;
}

constexpr int kDefaultDecorrelation[] = {0, 0, 1, 5, 14, 30, 55};

/// (int)Math.Log2(v) of a positive int.
int Log2(int v) { return int(std::bit_width(uint32_t(v))) - 1; }

uint32_t GetLargeVal(BitReader& gb)
{
    int nBits = 8;
    if (gb.Bit() != 0)
    {
        nBits += 8;
        if (gb.Bit() != 0)
        {
            nBits += 8;
            if (gb.Bit() != 0)
                nBits += 7;
        }
    }
    return gb.GetLong(nBits);
}

} // namespace

WmaProAudio::WmaProAudio(int channels, int sampleRate, int blockAlign, const Bytes& extra)
    : m_numChannels(channels), m_sampleRate(sampleRate), m_blockAlign(blockAlign)
{
    if (blockAlign <= 0 || extra.size() < 18 || sampleRate <= 0)
        throw InvalidDataError("Not a WMA Pro stream");
    int decodeFlags = extra[14] | extra[15] << 8;
    uint32_t channelMask = LittleEndian::ToUInt32(extra.data() + 2);
    m_bitsPerSample = extra[0] | extra[1] << 8;
    int nbChannels = channelMask != 0 ? std::popcount(channelMask) : channels;
    if (m_bitsPerSample > 32 || m_bitsPerSample < 1 || nbChannels < 1 || nbChannels > 8 || nbChannels > channels)
        throw InvalidDataError("Unsupported WMA Pro stream");
    if ((channelMask & 8) != 0 && nbChannels > 2)
        throw InvalidDataError("WMA Pro with an LFE channel");
    m_numChannels = nbChannels;
    m_log2FrameSize = Log2(blockAlign) + 4;
    if (m_log2FrameSize > 25)
        throw InvalidDataError("WMA Pro block align");
    m_lenPrefix = (decodeFlags & 0x40) != 0;
    int bits = sampleRate <= 16000 ? 9 : sampleRate <= 22050 ? 10 : sampleRate <= 48000 ? 11 : sampleRate <= 96000 ? 12 : 13;
    int tmp = decodeFlags & 6;
    if (tmp == 2)
        bits++;
    else if (tmp == 4)
        bits--;
    else if (tmp == 6)
        bits -= 2;
    if (bits > BlockMaxBits)
        throw InvalidDataError("WMA Pro 14-bit blocks");
    m_samplesPerFrame = 1 << bits;
    int log2MaxNumSubframes = (decodeFlags & 0x38) >> 3;
    m_maxNumSubframes = 1 << log2MaxNumSubframes;
    m_maxSubframeLenBit = m_maxNumSubframes == 16 || m_maxNumSubframes == 4;
    m_subframeLenBits = (log2MaxNumSubframes == 0 ? 0 : Log2(log2MaxNumSubframes)) + 1;
    int numPossibleBlockSizes = log2MaxNumSubframes + 1;
    m_minSamplesPerSubframe = m_samplesPerFrame / m_maxNumSubframes;
    m_dynamicRangeCompression = (decodeFlags & 0x80) != 0;
    if (m_maxNumSubframes > MaxSubframes || m_minSamplesPerSubframe < 1 << BlockMinBits)
        throw InvalidDataError("WMA Pro subframes");

    m_channels.resize(size_t(nbChannels));
    for (Channel& c : m_channels)
        c.PrevBlockLen = m_samplesPerFrame;
    m_groups.resize(size_t(nbChannels));

    for (int i = 0; i < numPossibleBlockSizes; i++)
    {
        int subframeLen = m_samplesPerFrame >> i, band = 1;
        auto& offsets = m_sfbOffsets[size_t(i)];
        offsets[0] = 0;
        for (int x = 0; x < MaxBands - 1 && offsets[size_t(band - 1)] < subframeLen; x++)
        {
            int offset = (subframeLen * 2 * WmaTables::ProCriticalFreq[x]) / sampleRate + 2;
            offset &= ~3;
            if (offset > offsets[size_t(band - 1)])
                offsets[size_t(band++)] = offset;
            if (offset >= subframeLen)
                break;
        }
        offsets[size_t(band - 1)] = subframeLen;
        m_numSfb[size_t(i)] = band - 1;
        if (m_numSfb[size_t(i)] <= 0)
            throw InvalidDataError("WMA Pro bands");
    }
    for (int i = 0; i < numPossibleBlockSizes; i++)
        for (int b = 0; b < m_numSfb[size_t(i)]; b++)
        {
            int offset = ((m_sfbOffsets[size_t(i)][size_t(b)] + m_sfbOffsets[size_t(i)][size_t(b + 1)] - 1) << i) >> 1;
            for (int x = 0; x < numPossibleBlockSizes; x++)
            {
                int v = 0;
                while (m_sfbOffsets[size_t(x)][size_t(v + 1)] << x < offset)
                    v++;
                SfOffset(i, x, b) = v;
            }
        }
    for (int i = 0; i < BlockSizes; i++)
    {
        double scale = 1.0 / double(1 << (BlockMinBits + i - 1)) / double(int64_t(1) << (m_bitsPerSample - 1));
        m_mdct[size_t(i)] = std::make_unique<Mdct>(BlockMinBits + i + 1, scale);
        int len = 1 << (BlockMinBits + i);
        std::vector<float> w(static_cast<size_t>(len));
        for (int k = 0; k < len; k++)
            w[size_t(k)] = float(std::sin((k + 0.5) * (std::numbers::pi / (2.0 * len))));
        m_windows[size_t(i)] = std::move(w);
    }
}

// ---- Bit reservoir ----

void WmaProAudio::PutBits(int n, int value)
{
    for (int i = n - 1; i >= 0; i--)
    {
        int bit = (value >> i) & 1;
        size_t at = size_t(m_pbBits >> 3);
        if ((m_pbBits & 7) == 0)
            m_frameData[at] = 0;
        m_frameData[at] = uint8_t(m_frameData[at] | (bit << (7 - (m_pbBits & 7))));
        m_pbBits++;
    }
}

void WmaProAudio::CopyBits(const uint8_t* src, int byteOffset, int length)
{
    // ff_copy_bits: length bits from a byte position of a buffer
    int whole = length >> 3;
    if ((m_pbBits & 7) == 0)
    {
        std::memmove(m_frameData.data() + (m_pbBits >> 3), src + byteOffset, size_t(whole));
        m_pbBits += whole * 8;
    }
    else
        for (int i = 0; i < whole; i++)
            PutBits(8, src[byteOffset + i]);
    int rest = length & 7;
    if (rest != 0)
        PutBits(rest, src[byteOffset + whole] >> (8 - rest));
}

void WmaProAudio::SaveBits(BitReader& gb, int len, bool append)
{
    // save_bits: a (partial) frame into the reservoir
    int buflen;
    if (!append)
    {
        m_frameOffset = gb.Position & 7;
        m_numSavedBits = m_frameOffset;
        m_pbBits = 0;
        buflen = (m_numSavedBits + len + 7) >> 3;
    }
    else
        buflen = (m_pbBits + len + 7) >> 3;
    if (len <= 0 || buflen > MaxFramesize)
    {
        m_packetLoss = true;
        return;
    }
    m_numSavedBits += len;
    if (!append)
        CopyBits(gb.Data, gb.Position >> 3, m_numSavedBits);
    else
    {
        int align = std::min(8 - (gb.Position & 7), len);
        PutBits(align, gb.Get(align));
        len -= align;
        CopyBits(gb.Data, gb.Position >> 3, len);
    }
    gb.Skip(len);
    size_t from = size_t((m_pbBits + 7) >> 3);
    std::fill_n(m_frameData.begin() + std::ptrdiff_t(from), std::min<size_t>(16, m_frameData.size() - from), uint8_t(0));
    m_gb = BitReader(m_frameData, m_numSavedBits, m_frameOffset);
}

void WmaProAudio::DecodePacket(const uint8_t* packet, size_t length, std::vector<float>* output)
{
    m_data.assign(length + 16, 0);
    std::memcpy(m_data.data(), packet, length);
    // FFmpeg's decode loop: the packet again from where the last call stopped, until it is done
    int offset = 0;
    int size = int(length);
    for (bool first = true; first || (offset < size && !m_packetDone); first = false)
    {
        bool error;
        int used = DecodeStep(offset, size - offset, output, error);
        if (error)
            throw InvalidDataError("WMA Pro packet");
        offset += used >> 3;
        if (used == 0)
            break;
    }
}

int WmaProAudio::DecodeStep(int start, int size, std::vector<float>* output, bool& error)
{
    // decode_packet: one call of FFmpeg's, on the packet's bytes from start; the bits it used
    error = false;
    BitReader gb;
    if (m_packetDone || m_packetLoss)
    {
        m_packetDone = false;
        if (size < m_blockAlign)
        {
            m_packetLoss = true;
            error = true;
            return size * 8;
        }
        m_nextPacketStart = size - m_blockAlign;
        int bufSize = m_blockAlign;
        m_bufBitSize = bufSize << 3;
        gb = BitReader(m_data, start * 8 + m_bufBitSize, start * 8);
        int sequence = gb.Get(4);
        gb.Skip(2);
        int numBitsPrevFrame = gb.Get(m_log2FrameSize);
        if (!m_packetLoss && ((m_packetSequenceNumber + 1) & 0xF) != sequence)
            m_packetLoss = true;
        m_packetSequenceNumber = sequence;
        if (numBitsPrevFrame > 0)
        {
            int remaining = gb.Size - gb.Position;
            if (numBitsPrevFrame >= remaining)
            {
                numBitsPrevFrame = remaining;
                m_packetDone = true;
            }
            SaveBits(gb, numBitsPrevFrame, true);
            if (!m_packetLoss)
                DecodeFrame(output);
        }
        if (m_packetLoss)
        {
            m_numSavedBits = 0;
            m_packetLoss = false;
        }
    }
    else
    {
        m_bufBitSize = (size - m_nextPacketStart) << 3;
        gb = BitReader(m_data, start * 8 + m_bufBitSize, start * 8 + m_packetOffset);
        int remaining = gb.Size - gb.Position;
        int frameSize = 0;
        if (m_lenPrefix && remaining > m_log2FrameSize && (frameSize = int(gb.Show(m_log2FrameSize))) != 0 && frameSize <= remaining)
        {
            SaveBits(gb, frameSize, false);
            if (!m_packetLoss)
                m_packetDone = !DecodeFrame(output);
        }
        else if (!m_lenPrefix && m_numSavedBits > m_gb.Position)
            m_packetDone = !DecodeFrame(output);
        else
            m_packetDone = true;
    }
    int left = gb.Size - gb.Position;
    if (left < 0)
        m_packetLoss = true;
    if (m_packetDone && !m_packetLoss && left > 0)
        SaveBits(gb, left, false);
    int consumed = gb.Position - start * 8;
    m_packetOffset = consumed & 7;
    error = m_packetLoss;
    return consumed;
}

// ---- Frames ----

bool WmaProAudio::DecodeFrame(std::vector<float>* output)
{
    // decode_frame: true when more frames follow in the packet
    BitReader& gb = m_gb;
    int len = m_lenPrefix ? gb.Get(m_log2FrameSize) : 0;
    if (!DecodeTileHeader())
    {
        m_packetLoss = true;
        return false;
    }
    int nb = m_numChannels;
    if (nb > 1 && gb.Bit() != 0 && gb.Bit() != 0)
        gb.Skip(4 * nb * nb);
    if (m_dynamicRangeCompression)
        gb.Get(8); // drc gain
    if (gb.Bit() != 0)
    {
        int trimBits = Log2(m_samplesPerFrame * 2);
        if (gb.Bit() != 0)
            m_trimStart = gb.Get(trimBits);
        if (gb.Bit() != 0)
            m_trimEnd = gb.Get(trimBits);
    }
    else
        m_trimStart = m_trimEnd = 0;

    m_parsedAllSubframes = false;
    for (Channel& c : m_channels)
    {
        c.DecodedSamples = 0;
        c.CurSubframe = 0;
        c.ReuseSf = false;
    }
    while (!m_parsedAllSubframes)
        if (!DecodeSubframe())
        {
            m_packetLoss = true;
            return false;
        }

    if (m_skipFrame)
        m_skipFrame = false;
    else
    {
        int from = std::min(m_trimStart, m_samplesPerFrame), to = m_samplesPerFrame - std::min(m_trimEnd, m_samplesPerFrame);
        for (int ch = 0; ch < nb; ch++)
            for (int i = from; i < to; i++)
                output[ch].push_back(m_channels[size_t(ch)].Out[size_t(i)]);
    }
    m_trimStart = m_trimEnd = 0;
    for (Channel& c : m_channels)
        std::memmove(c.Out.data(), c.Out.data() + m_samplesPerFrame, size_t(m_samplesPerFrame >> 1) * sizeof(float));

    if (m_lenPrefix)
    {
        int read = gb.Position - m_frameOffset;
        if (len != read + 2)
        {
            m_packetLoss = true;
            return false;
        }
        gb.Skip(len - read - 1);
    }
    else
        while (gb.Position < m_numSavedBits && gb.Bit() == 0)
        {
        }
    return gb.Bit() != 0;
}

int WmaProAudio::DecodeSubframeLength(int offset)
{
    if (offset == m_samplesPerFrame - m_minSamplesPerSubframe)
        return m_minSamplesPerSubframe;
    if (m_gb.Left() < 1)
        return -1;
    int shift = 0;
    if (m_maxSubframeLenBit)
    {
        if (m_gb.Bit() != 0)
            shift = 1 + m_gb.Get(m_subframeLenBits - 1);
    }
    else
        shift = m_gb.Get(m_subframeLenBits);
    int len = m_samplesPerFrame >> shift;
    return len < m_minSamplesPerSubframe || len > m_samplesPerFrame ? -1 : len;
}

bool WmaProAudio::DecodeTileHeader()
{
    // decode_tilehdr: how the frame splits into subframes per channel
    int nb = m_numChannels;
    int numSamples[8] = {};
    bool contains[8] = {};
    int channelsForCur = nb, minChannelLen = 0;
    for (Channel& c : m_channels)
        c.NumSubframes = 0;
    bool fixedLayout = m_maxNumSubframes == 1 || m_gb.Bit() != 0;
    do
    {
        for (int c = 0; c < nb; c++)
        {
            if (numSamples[c] == minChannelLen)
                contains[c] = fixedLayout || channelsForCur == 1 || minChannelLen == m_samplesPerFrame - m_minSamplesPerSubframe || m_gb.Bit() != 0;
            else
                contains[c] = false;
        }
        int subframeLen = DecodeSubframeLength(minChannelLen);
        if (subframeLen <= 0)
            return false;
        minChannelLen += subframeLen;
        for (int c = 0; c < nb; c++)
        {
            Channel& chan = m_channels[size_t(c)];
            if (contains[c])
            {
                if (chan.NumSubframes >= MaxSubframes)
                    return false;
                chan.SubframeLen[size_t(chan.NumSubframes++)] = subframeLen;
                numSamples[c] += subframeLen;
                if (numSamples[c] > m_samplesPerFrame)
                    return false;
            }
            else if (numSamples[c] <= minChannelLen)
            {
                if (numSamples[c] < minChannelLen)
                {
                    channelsForCur = 0;
                    minChannelLen = numSamples[c];
                }
                channelsForCur++;
            }
        }
    } while (minChannelLen < m_samplesPerFrame);
    for (Channel& chan : m_channels)
    {
        int offset = 0;
        for (int i = 0; i < chan.NumSubframes; i++)
        {
            chan.SubframeOffset[size_t(i)] = offset;
            offset += chan.SubframeLen[size_t(i)];
        }
    }
    return true;
}

void WmaProAudio::DecodeDecorrelationMatrix(ChannelGroup& group)
{
    const auto& sin64 = Vlcs().Sin64;
    int n = group.NumChannels;
    group.Matrix.fill(0);
    int rotation[64] = {};
    for (int i = 0; i < n * (n - 1) >> 1; i++)
        rotation[i] = m_gb.Get(6);
    for (int i = 0; i < n; i++)
        group.Matrix[size_t(n * i + i)] = m_gb.Bit() != 0 ? 1.0f : -1.0f;
    int offset = 0;
    for (int i = 1; i < n; i++)
    {
        for (int x = 0; x < i; x++)
            for (int y = 0; y < i + 1; y++)
            {
                float v1 = group.Matrix[size_t(x * n + y)], v2 = group.Matrix[size_t(i * n + y)];
                int r = rotation[offset + x];
                float sinv, cosv;
                if (r < 32)
                {
                    sinv = sin64[size_t(r)];
                    cosv = sin64[size_t(32 - r)];
                }
                else
                {
                    sinv = sin64[size_t(64 - r)];
                    cosv = -sin64[size_t(r - 32)];
                }
                group.Matrix[size_t(y + x * n)] = v1 * sinv - v2 * cosv;
                group.Matrix[size_t(y + i * n)] = v1 * cosv + v2 * sinv;
            }
        offset += i;
    }
}

bool WmaProAudio::DecodeChannelTransform()
{
    // decode_channel_transform
    m_numChgroups = 0;
    if (m_numChannels <= 1)
        return true;
    int remaining = m_channelsForCurSubframe;
    if (m_gb.Bit() != 0)
        return false;
    for (m_numChgroups = 0; remaining != 0 && m_numChgroups < m_channelsForCurSubframe; m_numChgroups++)
    {
        ChannelGroup& group = m_groups[size_t(m_numChgroups)];
        int data = 0;
        group.NumChannels = 0;
        group.Transform = false;
        if (remaining > 2)
        {
            for (int i = 0; i < m_channelsForCurSubframe; i++)
            {
                int idx = m_channelIndexes[size_t(i)];
                if (!m_channels[size_t(idx)].Grouped && m_gb.Bit() != 0)
                {
                    group.NumChannels++;
                    m_channels[size_t(idx)].Grouped = true;
                    group.ChannelData[size_t(data++)] = idx;
                }
            }
        }
        else
        {
            group.NumChannels = remaining;
            for (int i = 0; i < m_channelsForCurSubframe; i++)
            {
                int idx = m_channelIndexes[size_t(i)];
                if (!m_channels[size_t(idx)].Grouped)
                    group.ChannelData[size_t(data++)] = idx;
                m_channels[size_t(idx)].Grouped = true;
            }
        }
        if (group.NumChannels == 2)
        {
            if (m_gb.Bit() != 0)
            {
                if (m_gb.Bit() != 0)
                    return false;
            }
            else
            {
                group.Transform = true;
                if (m_numChannels == 2)
                {
                    group.Matrix[0] = 1;
                    group.Matrix[1] = -1;
                    group.Matrix[2] = 1;
                    group.Matrix[3] = 1;
                }
                else
                {
                    group.Matrix[0] = 0.70703125f;
                    group.Matrix[1] = -0.70703125f;
                    group.Matrix[2] = 0.70703125f;
                    group.Matrix[3] = 0.70703125f;
                }
            }
        }
        else if (group.NumChannels > 2 && m_gb.Bit() != 0)
        {
            group.Transform = true;
            if (m_gb.Bit() != 0)
                DecodeDecorrelationMatrix(group);
            else if (group.NumChannels <= 6)
                std::copy_n(WmaTables::ProDecorrelation + kDefaultDecorrelation[group.NumChannels], group.NumChannels * group.NumChannels,
                            group.Matrix.begin());
        }
        if (group.Transform)
        {
            if (m_gb.Bit() == 0)
                for (int i = 0; i < m_numBands; i++)
                    group.TransformBand[size_t(i)] = m_gb.Bit() != 0;
            else
                std::fill_n(group.TransformBand.begin(), m_numBands, true);
        }
        remaining -= group.NumChannels;
    }
    return true;
}

bool WmaProAudio::DecodeCoeffs(int c)
{
    // decode_coeffs: vector coded, then run / level coded coefficients of a channel
    const ProVlcs& vlcs = Vlcs();
    BitReader& gb = m_gb;
    Channel& ci = m_channels[size_t(c)];
    float* coeffs = ci.Out.data();
    int at = ci.Coeffs;
    bool table1 = gb.Bit() != 0;
    const VlcTable& vlc = table1 ? vlcs.Coef1 : vlcs.Coef0;
    const uint16_t* run = table1 ? WmaTables::ProCoef1Run : WmaTables::ProCoef0Run;
    const float* level = table1 ? WmaTables::ProCoef1Level : WmaTables::ProCoef0Level;
    bool rlMode = false;
    int cur = 0, numZeros = 0;
    float vals[4];
    while ((m_transmitNumVecCoeffs || !rlMode) && cur + 3 < ci.NumVecCoeffs)
    {
        int idx = vlcs.Vec4.Read(gb);
        if (idx < 0)
        {
            for (int i = 0; i < 4; i += 2)
            {
                idx = vlcs.Vec2.Read(gb);
                if (idx < 0)
                {
                    uint32_t v0 = uint32_t(vlcs.Vec1.Read(gb));
                    if (v0 == HuffVec1Size - 1)
                        v0 += GetLargeVal(gb);
                    uint32_t v1 = uint32_t(vlcs.Vec1.Read(gb));
                    if (v1 == HuffVec1Size - 1)
                        v1 += GetLargeVal(gb);
                    vals[i] = float(v0);
                    vals[i + 1] = float(v1);
                }
                else
                {
                    vals[i] = float(idx >> 4);
                    vals[i + 1] = float(idx & 0xF);
                }
            }
        }
        else
        {
            vals[0] = float(idx >> 12);
            vals[1] = float((idx >> 8) & 0xF);
            vals[2] = float((idx >> 4) & 0xF);
            vals[3] = float(idx & 0xF);
        }
        for (int i = 0; i < 4; i++)
        {
            if (vals[i] != 0)
            {
                coeffs[at + cur] = gb.Bit() != 0 ? vals[i] : -vals[i];
                numZeros = 0;
            }
            else
            {
                coeffs[at + cur] = 0;
                rlMode |= ++numZeros > m_subframeLen >> 8;
            }
            cur++;
        }
    }
    if (cur < m_subframeLen)
    {
        std::fill_n(coeffs + at + cur, m_subframeLen - cur, 0.0f);
        int mask = m_subframeLen - 1, offset = cur;
        for (; offset < m_subframeLen; offset++)
        {
            int code = vlc.Read(gb);
            if (code > 1)
            {
                offset += run[code];
                coeffs[at + (offset & mask)] = gb.Bit() != 0 ? level[code] : -level[code];
            }
            else if (code == 1)
                break;
            else
            {
                int lv = int(GetLargeVal(gb));
                if (gb.Bit() != 0)
                {
                    if (gb.Bit() != 0)
                    {
                        if (gb.Bit() != 0)
                            return false;
                        offset += gb.Get(m_escLen) + 4;
                    }
                    else
                        offset += gb.Get(2) + 1;
                }
                int sign = gb.Bit() - 1;
                coeffs[at + (offset & mask)] = float((lv ^ sign) - sign);
            }
        }
        if (offset > m_subframeLen)
            return false;
    }
    return true;
}

bool WmaProAudio::DecodeScaleFactors()
{
    // decode_scale_factors
    const ProVlcs& vlcs = Vlcs();
    BitReader& gb = m_gb;
    for (int i = 0; i < m_channelsForCurSubframe; i++)
    {
        Channel& ch = m_channels[size_t(m_channelIndexes[size_t(i)])];
        ch.ScaleFactors = ch.ScaleFactorIdx == 0 ? 1 : 0;
        auto& sf = ch.SavedScaleFactors[size_t(ch.ScaleFactors)];
        if (ch.ReuseSf)
        {
            const auto& saved = ch.SavedScaleFactors[size_t(ch.ScaleFactorIdx)];
            for (int b = 0; b < m_numBands; b++)
                sf[size_t(b)] = saved[size_t(SfOffset(m_tableIdx, ch.TableIdx, b))];
        }
        if (ch.CurSubframe == 0 || gb.Bit() != 0)
        {
            if (!ch.ReuseSf)
            {
                ch.ScaleFactorStep = gb.Get(2) + 1;
                int val = 45 / ch.ScaleFactorStep;
                for (int b = 0; b < m_numBands; b++)
                {
                    val += vlcs.Sf.Read(gb);
                    sf[size_t(b)] = val;
                }
            }
            else
            {
                for (int b = 0; b < m_numBands; b++)
                {
                    int idx = vlcs.SfRl.Read(gb), skip, val, sign;
                    if (idx == 0)
                    {
                        int code = gb.Get(14);
                        val = code >> 6;
                        sign = (code & 1) - 1;
                        skip = (code & 0x3F) >> 1;
                    }
                    else if (idx == 1)
                        break;
                    else
                    {
                        if (idx < 0)
                            return false;
                        skip = WmaTables::ProScaleRlRun[idx];
                        val = WmaTables::ProScaleRlLevel[idx];
                        sign = gb.Bit() - 1;
                    }
                    b += skip;
                    if (b >= m_numBands)
                        return false;
                    sf[size_t(b)] += (val ^ sign) - sign;
                }
            }
            ch.ScaleFactorIdx = ch.ScaleFactorIdx == 0 ? 1 : 0;
            ch.TableIdx = m_tableIdx;
            ch.ReuseSf = true;
        }
        ch.MaxScaleFactor = sf[0];
        for (int b = 1; b < m_numBands; b++)
            ch.MaxScaleFactor = std::max(ch.MaxScaleFactor, sf[size_t(b)]);
    }
    return true;
}

void WmaProAudio::InverseChannelTransform(const std::array<int, MaxBands>& sfbOffsets)
{
    float data[8];
    for (int g = 0; g < m_numChgroups; g++)
    {
        ChannelGroup& group = m_groups[size_t(g)];
        if (!group.Transform)
            continue;
        int n = group.NumChannels;
        for (int b = 0; b < m_numBands; b++)
        {
            int start = sfbOffsets[size_t(b)], end = std::min(sfbOffsets[size_t(b + 1)], m_subframeLen);
            if (group.TransformBand[size_t(b)])
            {
                for (int y = start; y < end; y++)
                {
                    for (int k = 0; k < n; k++)
                    {
                        Channel& ch = m_channels[size_t(group.ChannelData[size_t(k)])];
                        data[k] = ch.Out[size_t(ch.Coeffs + y)];
                    }
                    size_t m = 0;
                    for (int k = 0; k < n; k++)
                    {
                        float sum = 0;
                        for (int j = 0; j < n; j++)
                            sum += data[j] * group.Matrix[m++];
                        Channel& ch = m_channels[size_t(group.ChannelData[size_t(k)])];
                        ch.Out[size_t(ch.Coeffs + y)] = sum;
                    }
                }
            }
            else if (m_numChannels == 2)
            {
                for (int k = 0; k < 2; k++)
                {
                    Channel& ch = m_channels[size_t(group.ChannelData[size_t(k)])];
                    for (int y = start; y < end; y++)
                        ch.Out[size_t(ch.Coeffs + y)] *= 181.0f / 128;
                }
            }
        }
    }
}

void WmaProAudio::Window()
{
    for (int i = 0; i < m_channelsForCurSubframe; i++)
    {
        Channel& ch = m_channels[size_t(m_channelIndexes[size_t(i)])];
        int winlen = ch.PrevBlockLen;
        int start = ch.Coeffs - (winlen >> 1);
        if (m_subframeLen < winlen)
        {
            start += (winlen - m_subframeLen) >> 1;
            winlen = m_subframeLen;
        }
        const float* window = m_windows[size_t(Log2(winlen) - BlockMinBits)].data();
        int len = winlen >> 1;
        // vector_fmul_window(start, start, start + len, window, len)
        float* o = ch.Out.data();
        int dst = start + len, src0 = start + len, src1 = start + len, win = len;
        for (int a = -len, j = len - 1; a < 0; a++, j--)
        {
            float s0 = o[src0 + a], s1 = o[src1 + j], wi = window[win + a], wj = window[win + j];
            o[dst + a] = s0 * wj - s1 * wi;
            o[dst + j] = s0 * wi + s1 * wj;
        }
        ch.PrevBlockLen = m_subframeLen;
    }
}

bool WmaProAudio::DecodeSubframe()
{
    // decode_subframe
    BitReader& gb = m_gb;
    int nb = m_numChannels;
    int offset = m_samplesPerFrame, subframeLen = m_samplesPerFrame;
    int totalSamples = m_samplesPerFrame * nb;
    bool transmitCoeffs = false;
    for (Channel& ch : m_channels)
    {
        ch.Grouped = false;
        if (offset > ch.DecodedSamples)
        {
            offset = ch.DecodedSamples;
            subframeLen = ch.SubframeLen[size_t(ch.CurSubframe)];
        }
    }
    m_channelsForCurSubframe = 0;
    for (int i = 0; i < nb; i++)
    {
        Channel& ch = m_channels[size_t(i)];
        totalSamples -= ch.DecodedSamples;
        if (offset == ch.DecodedSamples && subframeLen == ch.SubframeLen[size_t(ch.CurSubframe)])
        {
            totalSamples -= ch.SubframeLen[size_t(ch.CurSubframe)];
            ch.DecodedSamples += ch.SubframeLen[size_t(ch.CurSubframe)];
            m_channelIndexes[size_t(m_channelsForCurSubframe++)] = i;
        }
    }
    if (totalSamples == 0)
        m_parsedAllSubframes = true;

    m_tableIdx = Log2(m_samplesPerFrame / subframeLen);
    m_numBands = m_numSfb[size_t(m_tableIdx)];
    const auto& sfbOffsets = m_sfbOffsets[size_t(m_tableIdx)];
    offset += m_samplesPerFrame >> 1;
    for (int i = 0; i < m_channelsForCurSubframe; i++)
        m_channels[size_t(m_channelIndexes[size_t(i)])].Coeffs = offset;
    m_subframeLen = subframeLen;
    m_escLen = Log2(subframeLen - 1) + 1;

    if (gb.Bit() != 0)
    {
        int numFillBits = gb.Get(2);
        if (numFillBits == 0)
        {
            int len = gb.Get(4);
            numFillBits = (len == 0 ? 0 : int(gb.GetLong(len))) + 1;
        }
        if (gb.Position + numFillBits > m_numSavedBits)
            return false;
        gb.Skip(numFillBits);
    }
    if (gb.Bit() != 0)
        return false;
    if (!DecodeChannelTransform())
        return false;
    for (int i = 0; i < m_channelsForCurSubframe; i++)
    {
        Channel& ch = m_channels[size_t(m_channelIndexes[size_t(i)])];
        ch.TransmitCoefs = gb.Bit() != 0;
        transmitCoeffs |= ch.TransmitCoefs;
    }
    if (transmitCoeffs)
    {
        int quantStep = 90 * m_bitsPerSample >> 4;
        m_transmitNumVecCoeffs = gb.Bit() != 0;
        if (m_transmitNumVecCoeffs)
        {
            int numBits = Log2((m_subframeLen + 3) / 4) + 1;
            for (int i = 0; i < m_channelsForCurSubframe; i++)
            {
                int numVec = gb.Get(numBits) << 2;
                if (numVec > m_subframeLen)
                    return false;
                m_channels[size_t(m_channelIndexes[size_t(i)])].NumVecCoeffs = numVec;
            }
        }
        else
            for (int i = 0; i < m_channelsForCurSubframe; i++)
                m_channels[size_t(m_channelIndexes[size_t(i)])].NumVecCoeffs = m_subframeLen;
        int step = gb.Get(6);
        step = step >= 32 ? step - 64 : step;
        quantStep += step;
        if (step == -32 || step == 31)
        {
            int sign = (step == 31 ? 1 : 0) - 1, quant = 0;
            while (gb.Position + 5 < m_numSavedBits && (step = gb.Get(5)) == 31)
                quant += 31;
            quantStep += ((quant + step) ^ sign) - sign;
        }
        if (m_channelsForCurSubframe == 1)
            m_channels[size_t(m_channelIndexes[0])].QuantStep = quantStep;
        else
        {
            int modifierLen = gb.Get(3);
            for (int i = 0; i < m_channelsForCurSubframe; i++)
            {
                Channel& ch = m_channels[size_t(m_channelIndexes[size_t(i)])];
                ch.QuantStep = quantStep;
                if (gb.Bit() != 0)
                    ch.QuantStep += modifierLen != 0 ? gb.Get(modifierLen) + 1 : 1;
            }
        }
        if (!DecodeScaleFactors())
            return false;
    }
    for (int i = 0; i < m_channelsForCurSubframe; i++)
    {
        int c = m_channelIndexes[size_t(i)];
        Channel& ch = m_channels[size_t(c)];
        // (a broken coefficient run is not an error to FFmpeg either)
        if (ch.TransmitCoefs && gb.Position < m_numSavedBits)
            DecodeCoeffs(c);
        else
            std::fill_n(ch.Out.begin() + ch.Coeffs, subframeLen, 0.0f);
    }
    if (transmitCoeffs)
    {
        Mdct& mdct = *m_mdct[size_t(Log2(subframeLen) - BlockMinBits)];
        InverseChannelTransform(sfbOffsets);
        for (int i = 0; i < m_channelsForCurSubframe; i++)
        {
            Channel& ch = m_channels[size_t(m_channelIndexes[size_t(i)])];
            const auto& sf = ch.SavedScaleFactors[size_t(ch.ScaleFactors)];
            for (int b = 0; b < m_numBands; b++)
            {
                int end = std::min(sfbOffsets[size_t(b + 1)], m_subframeLen), start = sfbOffsets[size_t(b)];
                int exp = ch.QuantStep - (ch.MaxScaleFactor - sf[size_t(b)]) * ch.ScaleFactorStep;
                float quant = float(std::pow(10.0, exp / 20.0));
                for (int k = start; k < end; k++)
                    m_tmp[size_t(k)] = ch.Out[size_t(ch.Coeffs + k)] * quant;
            }
            mdct.InverseHalf(m_tmp.data(), ch.Out.data() + ch.Coeffs);
        }
    }
    Window();
    for (int i = 0; i < m_channelsForCurSubframe; i++)
    {
        Channel& ch = m_channels[size_t(m_channelIndexes[size_t(i)])];
        if (ch.CurSubframe >= ch.NumSubframes)
            return false;
        ch.CurSubframe++;
    }
    return true;
}

} // namespace openshiina::formats
