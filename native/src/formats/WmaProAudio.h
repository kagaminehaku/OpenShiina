// C# Formats/WmaProAudio.cs: Windows Media Audio 9 Professional (format tag 0x162, the sound of the
// WVC1 ending movie): a port of FFmpeg's wmaprodec.c (LGPL 2.1 or later). Packets of block_align
// bytes carry frames across their bounds (a bit reservoir); a frame splits each channel into
// subframes of MDCT blocks, with channel transforms, vector / run-level coded coefficients and
// scale factors.
#pragma once

#include <array>
#include <memory>
#include <vector>

#include "formats/WmaAudio.h"

namespace openshiina::formats {

class WmaProAudio final : public IWmaDecoder
{
public:
    WmaProAudio(int channels, int sampleRate, int blockAlign, const Bytes& extra);

    int Channels() const override { return m_numChannels; }
    int SampleRate() const override { return m_sampleRate; }
    /// None: the first frame is held back by the decoder itself.
    int Delay() const override { return 0; }
    /// A packet of block_align bytes; the frames it completes are added to output.
    void DecodePacket(const uint8_t* packet, size_t length, std::vector<float>* output) override;

private:
    static constexpr int MaxSubframes = 32, MaxBands = 29, MaxFramesize = 32768;
    static constexpr int BlockMinBits = 6, BlockMaxBits = 13, BlockMaxSize = 1 << BlockMaxBits, BlockSizes = BlockMaxBits - BlockMinBits + 1;
    static constexpr int HuffVec1Size = 101;

    struct Channel
    {
        int PrevBlockLen = 0;
        bool TransmitCoefs = false;
        int NumSubframes = 0;
        std::array<int, MaxSubframes> SubframeLen{}, SubframeOffset{};
        int CurSubframe = 0;
        int DecodedSamples = 0;
        bool Grouped = false;
        int QuantStep = 0;
        bool ReuseSf = false;
        int ScaleFactorStep = 0;
        int MaxScaleFactor = 0;
        std::array<std::array<int, MaxBands>, 2> SavedScaleFactors{};
        int ScaleFactorIdx = 0;
        /// Which of SavedScaleFactors the current scale factors are.
        int ScaleFactors = 0;
        int TableIdx = 0;
        /// Where the subframe's coefficients are in Out.
        int Coeffs = 0;
        int NumVecCoeffs = 0;
        std::vector<float> Out = std::vector<float>(BlockMaxSize + BlockMaxSize / 2);
    };

    struct ChannelGroup
    {
        int NumChannels = 0;
        bool Transform = false;
        std::array<bool, MaxBands> TransformBand{};
        std::array<float, 64> Matrix{};
        std::array<int, 8> ChannelData{};
    };

    int m_numChannels, m_sampleRate;
    int m_blockAlign, m_bitsPerSample = 0, m_samplesPerFrame = 0, m_log2FrameSize = 0;
    bool m_lenPrefix = false, m_dynamicRangeCompression = false;
    int m_maxNumSubframes = 0, m_subframeLenBits = 0, m_minSamplesPerSubframe = 0;
    bool m_maxSubframeLenBit = false;
    std::array<int, BlockSizes> m_numSfb{};
    std::array<std::array<int, MaxBands>, BlockSizes> m_sfbOffsets{};
    std::vector<int> m_sfOffsets = std::vector<int>(BlockSizes * BlockSizes * MaxBands);
    std::array<std::unique_ptr<Mdct>, BlockSizes> m_mdct;
    std::array<std::vector<float>, BlockSizes> m_windows;
    std::vector<float> m_tmp = std::vector<float>(BlockMaxSize);
    std::vector<Channel> m_channels;
    std::vector<ChannelGroup> m_groups;

    // packet state
    Bytes m_frameData = Bytes(MaxFramesize + 16);
    int m_pbBits = 0;
    int m_nextPacketStart = 0, m_packetOffset = 0, m_packetSequenceNumber = 0, m_numSavedBits = 0, m_frameOffset = 0;
    bool m_packetLoss = true, m_packetDone = false;
    // frame state
    BitReader m_gb;
    int m_bufBitSize = 0;
    bool m_skipFrame = true, m_parsedAllSubframes = false;
    int m_trimStart = 0, m_trimEnd = 0;
    int m_subframeLen = 0, m_channelsForCurSubframe = 0, m_numBands = 0, m_tableIdx = 0, m_escLen = 0, m_numChgroups = 0;
    bool m_transmitNumVecCoeffs = false;
    std::array<int, 8> m_channelIndexes{};
    Bytes m_data;

    int& SfOffset(int i, int x, int b) { return m_sfOffsets[size_t((i * BlockSizes + x) * MaxBands + b)]; }
    void PutBits(int n, int value);
    void CopyBits(const uint8_t* src, int byteOffset, int length);
    void SaveBits(BitReader& gb, int len, bool append);
    int DecodeStep(int start, int size, std::vector<float>* output, bool& error);
    bool DecodeFrame(std::vector<float>* output);
    int DecodeSubframeLength(int offset);
    bool DecodeTileHeader();
    void DecodeDecorrelationMatrix(ChannelGroup& group);
    bool DecodeChannelTransform();
    bool DecodeCoeffs(int c);
    bool DecodeScaleFactors();
    void InverseChannelTransform(const std::array<int, MaxBands>& sfbOffsets);
    void Window();
    bool DecodeSubframe();
};

} // namespace openshiina::formats
