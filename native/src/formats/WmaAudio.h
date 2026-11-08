// C# Formats/WmaAudio.cs: Windows Media Audio 1 and 2 (format tags 0x160 / 0x161, the sound of the
// WMV3 movies): a port of FFmpeg's wmadec.c and wma.c (LGPL 2.1 or later). Superframes of
// block_align bytes hold frames of 2^frame_len_bits samples, each made of MDCT blocks of variable
// length; the exponents are VLC or LSP coded, the coefficients run / level coded with noise
// substituted in high bands.
#pragma once

#include <array>
#include <memory>
#include <vector>

#include "formats/Mdct.h"
#include "formats/VlcTable.h"

namespace openshiina::formats {

/// A WMA decoder: packets of block_align bytes into float samples per channel (C#: IWmaDecoder).
class IWmaDecoder
{
public:
    virtual ~IWmaDecoder() = default;
    virtual int Channels() const = 0;
    virtual int SampleRate() const = 0;
    /// Samples put out before the stream's first ones.
    virtual int Delay() const = 0;
    /// The packet's frames' samples added to output[channel].
    virtual void DecodePacket(const uint8_t* packet, size_t length, std::vector<float>* output) = 0;
};

class WmaAudio final : public IWmaDecoder
{
public:
    /// formatTag: 0x160 (WMA 1) or 0x161 (WMA 2).
    WmaAudio(int formatTag, int channels, int sampleRate, int bytesPerSecond, int blockAlign, const Bytes& extra);

    int Channels() const override { return m_channels; }
    int SampleRate() const override { return m_sampleRate; }
    /// Samples the decoder puts out before the stream's first ones (FFmpeg's skip_samples).
    int Delay() const override { return m_frameLen * 2; }
    /// wma_decode_superframe: a packet of block_align bytes.
    void DecodePacket(const uint8_t* packet, size_t length, std::vector<float>* output) override;

private:
    static constexpr int BlockMinBits = 7, BlockMaxBits = 11, BlockMaxSize = 1 << BlockMaxBits;
    static constexpr int HighBandMaxSize = 16, NoiseTabSize = 8192, LspPowBits = 7;

    int m_channels, m_sampleRate;
    int m_version, m_blockAlign;
    bool m_useExpVlc = false, m_useBitReservoir = false, m_useVariableBlockLen = false, m_useNoiseCoding = false;
    int m_byteOffsetBits = 0;
    int m_frameLen = 0, m_frameLenBits = 0, m_nbBlockSizes = 0;
    std::array<int, 5> m_exponentSizes{};
    std::array<std::array<int, 25>, 5> m_exponentBands{};
    std::array<int, 5> m_highBandStart{}, m_coefsEnd{}, m_exponentHighSizes{};
    std::array<std::array<int, HighBandMaxSize>, 5> m_exponentHighBands{};
    int m_coefsStart = 0;
    const VlcTable* m_expVlc = nullptr;
    const VlcTable* m_hgainVlc = nullptr;
    std::array<VlcTable, 2> m_coefVlc;
    std::array<std::vector<int>, 2> m_runTable;
    std::array<std::vector<float>, 2> m_levelTable;
    std::array<std::vector<float>, 5> m_windows;
    std::array<std::unique_ptr<Mdct>, 5> m_mdct;
    std::vector<float> m_noiseTable = std::vector<float>(NoiseTabSize);
    float m_noiseMult = 0;
    std::vector<float> m_lspCosTable = std::vector<float>(BlockMaxSize);
    std::array<float, 256> m_lspPowETable{};
    std::array<float, 1 << LspPowBits> m_lspPowMTable1{}, m_lspPowMTable2{};

    // decoding state
    BitReader m_gb;
    bool m_resetBlockLengths = true;
    int m_blockLenBits = 0, m_nextBlockLenBits = 0, m_prevBlockLenBits = 0, m_blockLen = 0, m_blockPos = 0;
    bool m_msStereo = false;
    std::array<bool, 2> m_channelCoded{};
    std::array<int, 2> m_exponentsBsize{};
    std::array<bool, 2> m_exponentsInitialized{};
    std::array<std::vector<float>, 2> m_exponents{std::vector<float>(BlockMaxSize), std::vector<float>(BlockMaxSize)};
    std::array<float, 2> m_maxExponent{1, 1};
    std::array<std::vector<float>, 2> m_coefs1{std::vector<float>(BlockMaxSize), std::vector<float>(BlockMaxSize)};
    std::array<std::vector<float>, 2> m_coefs{std::vector<float>(BlockMaxSize), std::vector<float>(BlockMaxSize)};
    std::vector<float> m_output = std::vector<float>(BlockMaxSize * 2);
    std::array<std::vector<float>, 2> m_frameOut{std::vector<float>(BlockMaxSize * 2), std::vector<float>(BlockMaxSize * 2)};
    std::array<std::array<int, HighBandMaxSize>, 2> m_highBandCoded{}, m_highBandValues{};
    Bytes m_lastSuperframe = Bytes(32768 + 16);
    std::array<float, HighBandMaxSize> m_expPower{};
    int m_lastSuperframeLen = 0, m_lastBitoffset = 0, m_noiseIndex = 0;
    Bytes m_buf;

    void InitCoefVlc(int slot, int index);
    void LspToCurveInit();
    float PowM14(float x) const;
    void DecodeExpLsp(int ch);
    void DecodeExpVlc(int ch);
    void RunLevelDecode(int t, float* ptr, int numCoefs, int blockLen, int coefNbBits);
    void Window(float* o, int at);
    bool DecodeBlock();
    void DecodeBlockCoefficients(int bsize);
    void DecodeFrame(std::vector<float>* output);
};

} // namespace openshiina::formats
