// C# Formats/WmaAudio.cs
#include "formats/WmaAudio.h"

#include <algorithm>
#include <bit>
#include <climits>
#include <cmath>
#include <cstring>
#include <numbers>
#include <span>

#include "formats/WmaTables.h"

namespace openshiina::formats {

namespace {

struct WmaVlcs
{
    VlcTable Hgain, Exp;

    WmaVlcs()
    {
        uint8_t lengths[37];
        int32_t symbols[37];
        for (int i = 0; i < 37; i++)
        {
            lengths[i] = WmaTables::HgainHufftab[2 * i + 1];
            symbols[i] = WmaTables::HgainHufftab[2 * i];
        }
        Hgain = VlcTable::FromLengths(9, lengths, std::span<const int32_t>(symbols), -18);
        Exp = VlcTable::FromCodes(8, std::span(WmaTables::ScalefactorCode), std::span(WmaTables::ScalefactorBits));
    }
};

const WmaVlcs& Vlcs()
{
    static const WmaVlcs vlcs;
    return vlcs;
}

/// pow(10, i / 16) for i in -60..95.
const std::array<float, 156>& PowTab()
{
    static const std::array<float, 156> table = [] {
        std::array<float, 156> t{};
        for (int i = 0; i < 156; i++)
            t[size_t(i)] = float(std::pow(10.0, (i - 60) / 16.0));
        return t;
    }();
    return table;
}

int TotalGainToBits(int g) { return g < 15 ? 13 : g < 32 ? 12 : g < 40 ? 11 : g < 45 ? 10 : 9; }

} // namespace

WmaAudio::WmaAudio(int formatTag, int channels, int sampleRate, int bytesPerSecond, int blockAlign, const Bytes& extra)
    : m_channels(channels), m_sampleRate(sampleRate), m_version(formatTag == 0x160 ? 1 : 2), m_blockAlign(blockAlign)
{
    if ((formatTag != 0x160 && formatTag != 0x161) || channels < 1 || channels > 2 || sampleRate <= 0 || sampleRate > 50000 || blockAlign <= 0
        || bytesPerSecond <= 0)
        throw InvalidDataError("Not a WMA 1 / 2 stream");
    int flags2 = 0;
    if (m_version == 1 && extra.size() >= 4)
        flags2 = extra[2] | extra[3] << 8;
    else if (m_version == 2 && extra.size() >= 6)
        flags2 = extra[4] | extra[5] << 8;
    m_useExpVlc = (flags2 & 1) != 0;
    m_useBitReservoir = (flags2 & 2) != 0;
    m_useVariableBlockLen = (flags2 & 4) != 0;
    if (m_version == 2 && extra.size() >= 8 && (extra[4] | extra[5] << 8) == 0xD && m_useVariableBlockLen)
        m_useVariableBlockLen = false;

    // ff_wma_init
    int bitRate = bytesPerSecond * 8;
    m_frameLenBits = sampleRate <= 16000 ? 9 : sampleRate <= 22050 || (sampleRate <= 32000 && m_version == 1) ? 10 : 11;
    m_nextBlockLenBits = m_prevBlockLenBits = m_blockLenBits = m_frameLenBits;
    m_frameLen = 1 << m_frameLenBits;
    if (m_useVariableBlockLen)
    {
        int nb = ((flags2 >> 3) & 3) + 1;
        if (bitRate / channels >= 32000)
            nb += 2;
        nb = std::min(nb, m_frameLenBits - BlockMinBits);
        m_nbBlockSizes = nb + 1;
    }
    else
        m_nbBlockSizes = 1;

    m_useNoiseCoding = true;
    float highFreq = float(sampleRate) * 0.5f;
    int sampleRate1 = sampleRate;
    if (m_version == 2)
        sampleRate1 = sampleRate1 >= 44100 ? 44100 : sampleRate1 >= 22050 ? 22050 : sampleRate1 >= 16000 ? 16000 : sampleRate1 >= 11025 ? 11025
            : sampleRate1 >= 8000 ? 8000 : sampleRate1;
    float bps = float(bitRate) / float(channels * sampleRate);
    m_byteOffsetBits = int(std::log2(double(int(double(bps * float(m_frameLen)) / 8.0 + 0.5)))) + 2;
    float bps1 = channels == 2 ? bps * 1.6f : bps;
    if (sampleRate1 == 44100)
    {
        if (bps1 >= 0.61f)
            m_useNoiseCoding = false;
        else
            highFreq *= 0.4f;
    }
    else if (sampleRate1 == 22050)
    {
        if (bps1 >= 1.16f)
            m_useNoiseCoding = false;
        else
            highFreq *= bps1 >= 0.72f ? 0.7f : 0.6f;
    }
    else if (sampleRate1 == 16000)
        highFreq *= bps > 0.5f ? 0.5f : 0.3f;
    else if (sampleRate1 == 11025)
        highFreq *= 0.7f;
    else if (sampleRate1 == 8000)
    {
        if (bps <= 0.625f)
            highFreq *= 0.5f;
        else if (bps > 0.75f)
            m_useNoiseCoding = false;
        else
            highFreq *= 0.65f;
    }
    else
        highFreq *= bps >= 0.8f ? 0.75f : bps >= 0.6f ? 0.6f : 0.5f;

    m_coefsStart = m_version == 1 ? 3 : 0;
    for (int k = 0; k < m_nbBlockSizes; k++)
    {
        int blockLen = m_frameLen >> k;
        if (m_version == 1)
        {
            int lpos = 0, i;
            for (i = 0; i < 25; i++)
            {
                int pos = (blockLen * 2 * WmaTables::CriticalFreqs[i] + (sampleRate >> 1)) / sampleRate;
                pos = std::min(pos, blockLen);
                m_exponentBands[0][size_t(i)] = pos - lpos;
                if (pos >= blockLen)
                {
                    i++;
                    break;
                }
                lpos = pos;
            }
            m_exponentSizes[0] = i;
        }
        else
        {
            const uint8_t* table = nullptr;
            int a = m_frameLenBits - BlockMinBits - k;
            if (a < 3)
                table = sampleRate >= 44100 ? WmaTables::ExponentBand44100
                    : sampleRate >= 32000   ? WmaTables::ExponentBand32000
                    : sampleRate >= 22050   ? WmaTables::ExponentBand22050
                                            : nullptr;
            if (table != nullptr)
            {
                int n = table[a * 25];
                for (int i = 0; i < n; i++)
                    m_exponentBands[size_t(k)][size_t(i)] = table[a * 25 + 1 + i];
                m_exponentSizes[size_t(k)] = n;
            }
            else
            {
                int j = 0, lpos = 0;
                for (int i = 0; i < 25; i++)
                {
                    int pos = (blockLen * 2 * WmaTables::CriticalFreqs[i] + (sampleRate << 1)) / (4 * sampleRate);
                    pos <<= 2;
                    pos = std::min(pos, blockLen);
                    if (pos > lpos)
                        m_exponentBands[size_t(k)][size_t(j++)] = pos - lpos;
                    if (pos >= blockLen)
                        break;
                    lpos = pos;
                }
                m_exponentSizes[size_t(k)] = j;
            }
        }
        m_coefsEnd[size_t(k)] = (m_frameLen - m_frameLen * 9 / 100) >> k;
        m_highBandStart[size_t(k)] = int(double(float(blockLen * 2) * highFreq / float(sampleRate)) + 0.5);
        int nb2 = m_exponentSizes[size_t(k)], jj = 0, p = 0;
        for (int i = 0; i < nb2; i++)
        {
            int start = p;
            p += m_exponentBands[size_t(k)][size_t(i)];
            int end = p;
            start = std::max(start, m_highBandStart[size_t(k)]);
            end = std::min(end, m_coefsEnd[size_t(k)]);
            if (end > start)
                m_exponentHighBands[size_t(k)][size_t(jj++)] = end - start;
        }
        m_exponentHighSizes[size_t(k)] = jj;
    }

    for (int i = 0; i < m_nbBlockSizes; i++)
    {
        int len = 1 << (m_frameLenBits - i);
        std::vector<float> w(static_cast<size_t>(len));
        for (int k = 0; k < len; k++)
            w[size_t(k)] = float(std::sin((k + 0.5) * (std::numbers::pi / (2.0 * len))));
        m_windows[size_t(i)] = std::move(w);
        m_mdct[size_t(i)] = std::make_unique<Mdct>(m_frameLenBits - i + 1, 1.0 / 32768.0);
    }

    if (m_useNoiseCoding)
    {
        m_noiseMult = m_useExpVlc ? 0.02f : 0.04f;
        uint32_t seed = 1;
        float norm = float(1.0 / double(int64_t(1) << 31) * std::sqrt(3.0) * double(m_noiseMult));
        for (int i = 0; i < NoiseTabSize; i++)
        {
            seed = seed * 314159 + 1;
            m_noiseTable[size_t(i)] = float(int32_t(seed)) * norm;
        }
        m_hgainVlc = &Vlcs().Hgain;
    }

    int coefVlcTable = 2;
    if (sampleRate >= 32000)
    {
        if (bps1 < 0.72f)
            coefVlcTable = 0;
        else if (bps1 < 1.16f)
            coefVlcTable = 1;
    }
    for (int t = 0; t < 2; t++)
        InitCoefVlc(t, coefVlcTable * 2 + t);

    if (m_useExpVlc)
        m_expVlc = &Vlcs().Exp;
    else
        LspToCurveInit();
}

void WmaAudio::InitCoefVlc(int slot, int index)
{
    std::span<const uint32_t> codes;
    std::span<const uint8_t> bits;
    std::span<const uint16_t> levels;
    switch (index)
    {
    case 0:
        codes = WmaTables::Coef0Codes, bits = WmaTables::Coef0Bits, levels = WmaTables::Levels0;
        break;
    case 1:
        codes = WmaTables::Coef1Codes, bits = WmaTables::Coef1Bits, levels = WmaTables::Levels1;
        break;
    case 2:
        codes = WmaTables::Coef2Codes, bits = WmaTables::Coef2Bits, levels = WmaTables::Levels2;
        break;
    case 3:
        codes = WmaTables::Coef3Codes, bits = WmaTables::Coef3Bits, levels = WmaTables::Levels3;
        break;
    case 4:
        codes = WmaTables::Coef4Codes, bits = WmaTables::Coef4Bits, levels = WmaTables::Levels4;
        break;
    default:
        codes = WmaTables::Coef5Codes, bits = WmaTables::Coef5Bits, levels = WmaTables::Levels5;
        break;
    }
    int n = int(bits.size());
    m_coefVlc[size_t(slot)] = VlcTable::FromCodes(9, codes, bits);
    std::vector<int> run(static_cast<size_t>(n));
    std::vector<float> level(static_cast<size_t>(n));
    int i = 2, lv = 1;
    size_t k = 0;
    while (i < n)
    {
        int l = levels[k++];
        for (int j = 0; j < l && i < n; j++, i++)
        {
            run[size_t(i)] = j;
            level[size_t(i)] = float(lv);
        }
        lv++;
    }
    m_runTable[size_t(slot)] = std::move(run);
    m_levelTable[size_t(slot)] = std::move(level);
}

void WmaAudio::LspToCurveInit()
{
    double wdel = std::numbers::pi / m_frameLen;
    for (int i = 0; i < m_frameLen; i++)
        m_lspCosTable[size_t(i)] = float(2.0f * std::cos(wdel * i));
    for (int i = 0; i < 256; i++)
        m_lspPowETable[size_t(i)] = std::pow(2.0f, float(i - 126) * -0.25f);
    float b = 1;
    for (int i = (1 << LspPowBits) - 1; i >= 0; i--)
    {
        int m = (1 << LspPowBits) + i;
        float a = float(m * (0.5 / (1 << LspPowBits)));
        a = float(1 / std::sqrt(std::sqrt(double(a))));
        m_lspPowMTable1[size_t(i)] = 2 * a - b;
        m_lspPowMTable2[size_t(i)] = b - a;
        b = a;
    }
}

float WmaAudio::PowM14(float x) const
{
    uint32_t v = std::bit_cast<uint32_t>(x);
    uint32_t e = v >> 23, m = (v >> (23 - LspPowBits)) & ((1 << LspPowBits) - 1);
    float t = std::bit_cast<float>(((v << LspPowBits) & ((1u << 23) - 1)) | (127u << 23));
    return m_lspPowETable[e] * (m_lspPowMTable1[m] + m_lspPowMTable2[m] * t);
}

void WmaAudio::DecodeExpLsp(int ch)
{
    float lsp[10];
    for (int i = 0; i < 10; i++)
    {
        int val = i == 0 || i >= 8 ? m_gb.Get(3) : m_gb.Get(4);
        lsp[i] = WmaTables::LspCodebook[i * 16 + val];
    }
    float valMax = 0;
    float* o = m_exponents[size_t(ch)].data();
    for (int i = 0; i < m_blockLen; i++)
    {
        float p = 0.5f, q = 0.5f, w = m_lspCosTable[size_t(i)];
        for (int j = 1; j < 10; j += 2)
        {
            q *= w - lsp[j - 1];
            p *= w - lsp[j];
        }
        p *= p * (2.0f - w);
        q *= q * (2.0f + w);
        float v = PowM14(p + q);
        if (v > valMax)
            valMax = v;
        o[i] = v;
    }
    m_maxExponent[size_t(ch)] = valMax;
}

void WmaAudio::DecodeExpVlc(int ch)
{
    const auto& powTab = PowTab();
    const auto& bands = m_exponentBands[size_t(m_frameLenBits - m_blockLenBits)];
    std::vector<float>& q = m_exponents[size_t(ch)];
    int qi = 0, band = 0, lastExp;
    float maxScale = 0;
    if (m_version == 1)
    {
        lastExp = m_gb.Get(5) + 10;
        float v = powTab[size_t(lastExp + 60)];
        maxScale = v;
        int n = bands[size_t(band++)];
        for (int i = 0; i < n; i++)
            q[size_t(qi++)] = v;
    }
    else
        lastExp = 36;
    while (qi < m_blockLen)
    {
        int code = m_expVlc->Read(m_gb);
        lastExp += code - 60;
        if (uint32_t(lastExp + 60) >= powTab.size())
            throw InvalidDataError("WMA exponent out of range");
        float v = powTab[size_t(lastExp + 60)];
        if (v > maxScale)
            maxScale = v;
        if (band >= 25)
            throw InvalidDataError("WMA exponent bands");
        int n = bands[size_t(band++)];
        for (int i = 0; i < n && size_t(qi) < q.size(); i++)
            q[size_t(qi++)] = v;
    }
    m_maxExponent[size_t(ch)] = maxScale;
}

void WmaAudio::RunLevelDecode(int t, float* ptr, int numCoefs, int blockLen, int coefNbBits)
{
    // ff_wma_run_level_decode (WMA 1 / 2 escapes)
    const VlcTable& vlc = m_coefVlc[size_t(t)];
    const std::vector<int>& run = m_runTable[size_t(t)];
    const std::vector<float>& level = m_levelTable[size_t(t)];
    int mask = blockLen - 1, offset = 0;
    for (; offset < numCoefs; offset++)
    {
        int code = vlc.Read(m_gb);
        if (code > 1)
        {
            offset += run[size_t(code)];
            int sign = m_gb.Bit();
            ptr[offset & mask] = sign != 0 ? level[size_t(code)] : -level[size_t(code)];
        }
        else if (code == 1)
            break;
        else
        {
            int lv = m_gb.Get(coefNbBits);
            offset += m_gb.Get(m_frameLenBits);
            int sign = m_gb.Bit() - 1;
            ptr[offset & mask] = float((lv ^ sign) - sign);
        }
    }
    if (offset > numCoefs)
        throw InvalidDataError("WMA coefficient overflow");
}

void WmaAudio::Window(float* o, int at)
{
    const float* input = m_output.data();
    if (m_blockLenBits <= m_prevBlockLenBits)
    {
        const float* w = m_windows[size_t(m_frameLenBits - m_blockLenBits)].data();
        for (int i = 0; i < m_blockLen; i++)
            o[at + i] += input[i] * w[i];
    }
    else
    {
        int blockLen = 1 << m_prevBlockLenBits, n = (m_blockLen - blockLen) / 2;
        const float* w = m_windows[size_t(m_frameLenBits - m_prevBlockLenBits)].data();
        for (int i = 0; i < blockLen; i++)
            o[at + n + i] += input[n + i] * w[i];
        std::memmove(o + at + n + blockLen, input + n + blockLen, size_t(n) * sizeof(float));
    }
    at += m_blockLen;
    int inAt = m_blockLen;
    if (m_blockLenBits <= m_nextBlockLenBits)
    {
        const float* w = m_windows[size_t(m_frameLenBits - m_blockLenBits)].data();
        for (int i = 0; i < m_blockLen; i++)
            o[at + i] = input[inAt + i] * w[m_blockLen - 1 - i];
    }
    else
    {
        int blockLen = 1 << m_nextBlockLenBits, n = (m_blockLen - blockLen) / 2;
        const float* w = m_windows[size_t(m_frameLenBits - m_nextBlockLenBits)].data();
        std::memmove(o + at, input + inAt, size_t(n) * sizeof(float));
        for (int i = 0; i < blockLen; i++)
            o[at + n + i] = input[inAt + n + i] * w[blockLen - 1 - i];
        std::fill_n(o + at + n + blockLen, n, 0.0f);
    }
}

bool WmaAudio::DecodeBlock()
{
    // wma_decode_block: true after the frame's last block
    if (m_useVariableBlockLen)
    {
        int n = int(std::log2(double(m_nbBlockSizes - 1))) + 1;
        if (m_resetBlockLengths)
        {
            m_resetBlockLengths = false;
            int v1 = m_gb.Get(n);
            if (v1 >= m_nbBlockSizes)
                throw InvalidDataError("WMA block length");
            m_prevBlockLenBits = m_frameLenBits - v1;
            v1 = m_gb.Get(n);
            if (v1 >= m_nbBlockSizes)
                throw InvalidDataError("WMA block length");
            m_blockLenBits = m_frameLenBits - v1;
        }
        else
        {
            m_prevBlockLenBits = m_blockLenBits;
            m_blockLenBits = m_nextBlockLenBits;
        }
        int v = m_gb.Get(n);
        if (v >= m_nbBlockSizes)
            throw InvalidDataError("WMA block length");
        m_nextBlockLenBits = m_frameLenBits - v;
    }
    else
        m_nextBlockLenBits = m_prevBlockLenBits = m_blockLenBits = m_frameLenBits;
    if (m_frameLenBits - m_blockLenBits >= m_nbBlockSizes)
        throw InvalidDataError("WMA block length");
    m_blockLen = 1 << m_blockLenBits;
    if (m_blockPos + m_blockLen > m_frameLen)
        throw InvalidDataError("WMA frame length");

    int channels = m_channels;
    if (channels == 2)
        m_msStereo = m_gb.Bit() != 0;
    bool anyCoded = false;
    for (int ch = 0; ch < channels; ch++)
    {
        m_channelCoded[size_t(ch)] = m_gb.Bit() != 0;
        anyCoded |= m_channelCoded[size_t(ch)];
    }
    int bsize = m_frameLenBits - m_blockLenBits;
    if (anyCoded)
        DecodeBlockCoefficients(bsize);

    Mdct& mdct = *m_mdct[size_t(bsize)];
    for (int ch = 0; ch < channels; ch++)
    {
        int n4 = m_blockLen / 2;
        if (m_channelCoded[size_t(ch)])
            mdct.Inverse(m_coefs[size_t(ch)].data(), m_output.data());
        else if (!(m_msStereo && ch == 1))
            std::fill(m_output.begin(), m_output.end(), 0.0f);
        Window(m_frameOut[size_t(ch)].data(), m_frameLen / 2 + m_blockPos - n4);
    }
    m_blockPos += m_blockLen;
    return m_blockPos >= m_frameLen;
}

void WmaAudio::DecodeBlockCoefficients(int bsize)
{
    int channels = m_channels;
    int totalGain = 1;
    while (true)
    {
        if (m_gb.Left() < 7)
            throw InvalidDataError("WMA total gain");
        int a = m_gb.Get(7);
        totalGain += a;
        if (a != 127)
            break;
    }
    int coefNbBits = TotalGainToBits(totalGain);
    int nbCoefs[2] = {};
    int n = m_coefsEnd[size_t(bsize)] - m_coefsStart;
    for (int ch = 0; ch < channels; ch++)
        nbCoefs[ch] = n;
    const auto& highBands = m_exponentHighBands[size_t(bsize)];
    int highSizes = m_exponentHighSizes[size_t(bsize)];
    if (m_useNoiseCoding)
    {
        for (int ch = 0; ch < channels; ch++)
            if (m_channelCoded[size_t(ch)])
                for (int i = 0; i < highSizes; i++)
                {
                    int a = m_gb.Bit();
                    m_highBandCoded[size_t(ch)][size_t(i)] = a;
                    if (a != 0)
                        nbCoefs[ch] -= highBands[size_t(i)];
                }
        for (int ch = 0; ch < channels; ch++)
            if (m_channelCoded[size_t(ch)])
            {
                int val = INT_MIN;
                for (int i = 0; i < highSizes; i++)
                    if (m_highBandCoded[size_t(ch)][size_t(i)] != 0)
                    {
                        val = val == INT_MIN ? m_gb.Get(7) - 19 : val + m_hgainVlc->Read(m_gb);
                        m_highBandValues[size_t(ch)][size_t(i)] = val;
                    }
            }
    }
    if (m_blockLenBits == m_frameLenBits || m_gb.Bit() != 0)
        for (int ch = 0; ch < channels; ch++)
            if (m_channelCoded[size_t(ch)])
            {
                if (m_useExpVlc)
                    DecodeExpVlc(ch);
                else
                    DecodeExpLsp(ch);
                m_exponentsBsize[size_t(ch)] = bsize;
                m_exponentsInitialized[size_t(ch)] = true;
            }
    for (int ch = 0; ch < channels; ch++)
        if (m_channelCoded[size_t(ch)] && !m_exponentsInitialized[size_t(ch)])
            throw InvalidDataError("WMA exponents missing");
    for (int ch = 0; ch < channels; ch++)
    {
        if (m_channelCoded[size_t(ch)])
        {
            int t = ch == 1 && m_msStereo ? 1 : 0;
            std::fill_n(m_coefs1[size_t(ch)].begin(), m_blockLen, 0.0f);
            RunLevelDecode(t, m_coefs1[size_t(ch)].data(), nbCoefs[ch], m_blockLen, coefNbBits);
        }
        if (m_version == 1 && channels >= 2)
            m_gb.Align();
    }

    float mdctNorm = 1.0f / float(m_blockLen / 2);
    if (m_version == 1)
        mdctNorm *= std::sqrt(float(m_blockLen / 2));
    for (int ch = 0; ch < channels; ch++)
    {
        if (!m_channelCoded[size_t(ch)])
            continue;
        const float* coefs1 = m_coefs1[size_t(ch)].data();
        const float* exponents = m_exponents[size_t(ch)].data();
        int esize = m_exponentsBsize[size_t(ch)];
        float mult = float(std::pow(10.0, totalGain * 0.05) / double(m_maxExponent[size_t(ch)])) * mdctNorm;
        float* coefs = m_coefs[size_t(ch)].data();
        int c = 0, c1 = 0;
        if (m_useNoiseCoding)
        {
            float mult1 = mult;
            for (int i = 0; i < m_coefsStart; i++)
            {
                coefs[c++] = m_noiseTable[size_t(m_noiseIndex)] * exponents[(i << bsize) >> esize] * mult1;
                m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
            }
            int n1 = highSizes;
            float* expPower = m_expPower.data();
            int e = (m_highBandStart[size_t(bsize)] << bsize) >> esize;
            int lastHighBand = 0;
            for (int j = 0; j < n1; j++)
            {
                int nb = highBands[size_t(j)];
                if (m_highBandCoded[size_t(ch)][size_t(j)] != 0)
                {
                    float e2 = 0;
                    for (int i = 0; i < nb; i++)
                    {
                        float v = exponents[e + ((i << bsize) >> esize)];
                        e2 += v * v;
                    }
                    expPower[j] = e2 / float(nb);
                    lastHighBand = j;
                }
                e += (nb << bsize) >> esize;
            }
            e = (m_coefsStart << bsize) >> esize;
            for (int j = -1; j < n1; j++)
            {
                int nb = j < 0 ? m_highBandStart[size_t(bsize)] - m_coefsStart : highBands[size_t(j)];
                if (j >= 0 && m_highBandCoded[size_t(ch)][size_t(j)] != 0)
                {
                    float m1 = std::sqrt(expPower[j] / expPower[lastHighBand]);
                    m1 *= float(std::pow(10.0, m_highBandValues[size_t(ch)][size_t(j)] * 0.05));
                    m1 /= m_maxExponent[size_t(ch)] * m_noiseMult;
                    m1 *= mdctNorm;
                    for (int i = 0; i < nb; i++)
                    {
                        float noise = m_noiseTable[size_t(m_noiseIndex)];
                        m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
                        coefs[c++] = noise * exponents[e + ((i << bsize) >> esize)] * m1;
                    }
                }
                else
                {
                    for (int i = 0; i < nb; i++)
                    {
                        float noise = m_noiseTable[size_t(m_noiseIndex)];
                        m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
                        coefs[c++] = (coefs1[c1++] + noise) * exponents[e + ((i << bsize) >> esize)] * mult;
                    }
                }
                e += (nb << bsize) >> esize;
            }
            int rest = m_blockLen - m_coefsEnd[size_t(bsize)];
            float mult2 = mult * exponents[e + ((-(1 << bsize)) >> esize)];
            for (int i = 0; i < rest; i++)
            {
                coefs[c++] = m_noiseTable[size_t(m_noiseIndex)] * mult2;
                m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
            }
        }
        else
        {
            for (int i = 0; i < m_coefsStart; i++)
                coefs[c++] = 0;
            for (int i = 0; i < nbCoefs[ch]; i++)
                coefs[c++] = coefs1[i] * exponents[(i << bsize) >> esize] * mult;
            int rest = m_blockLen - m_coefsEnd[size_t(bsize)];
            for (int i = 0; i < rest; i++)
                coefs[c++] = 0;
        }
    }
    if (m_msStereo && m_channelCoded[1])
    {
        if (!m_channelCoded[0])
        {
            std::fill_n(m_coefs[0].begin(), m_blockLen, 0.0f);
            m_channelCoded[0] = true;
        }
        float* a0 = m_coefs[0].data();
        float* a1 = m_coefs[1].data();
        for (int i = 0; i < m_blockLen; i++)
        {
            float t = a0[i] - a1[i];
            a0[i] += a1[i];
            a1[i] = t;
        }
    }
}

void WmaAudio::DecodeFrame(std::vector<float>* output)
{
    // wma_decode_frame: a frame's blocks, the frame's samples out
    m_blockPos = 0;
    while (!DecodeBlock())
    {
    }
    for (int ch = 0; ch < m_channels; ch++)
    {
        std::vector<float>& fo = m_frameOut[size_t(ch)];
        output[ch].insert(output[ch].end(), fo.begin(), fo.begin() + m_frameLen);
        std::memmove(fo.data(), fo.data() + m_frameLen, size_t(m_frameLen) * sizeof(float));
        std::fill_n(fo.begin() + m_frameLen, m_frameLen, 0.0f);
    }
}

void WmaAudio::DecodePacket(const uint8_t* packet, size_t length, std::vector<float>* output)
{
    if (length < size_t(m_blockAlign))
        throw InvalidDataError("WMA packet too small");
    int bufSize = m_blockAlign;
    m_buf.assign(size_t(bufSize) + 16, 0);
    std::memcpy(m_buf.data(), packet, size_t(bufSize));
    m_gb = BitReader(m_buf, bufSize * 8);
    try
    {
        if (!m_useBitReservoir)
        {
            DecodeFrame(output);
            return;
        }
        m_gb.Skip(4);
        int nbFrames = m_gb.Get(4) - (m_lastSuperframeLen <= 0 ? 1 : 0);
        if (nbFrames <= 0)
        {
            if (nbFrames < 0 || m_gb.Left() <= 8 || m_lastSuperframeLen + bufSize - 1 > 32768)
                throw InvalidDataError("WMA superframe");
            for (int i = 0; i < bufSize - 1; i++)
                m_lastSuperframe[size_t(m_lastSuperframeLen + i)] = uint8_t(m_gb.Get(8));
            m_lastSuperframeLen += 8 * bufSize - 8;
            return;
        }
        int bitOffset = m_gb.Get(m_byteOffsetBits + 3);
        if (bitOffset > m_gb.Left())
            throw InvalidDataError("WMA bit offset");
        if (m_lastSuperframeLen > 0)
        {
            if (m_lastSuperframeLen + ((bitOffset + 7) >> 3) > 32768)
                throw InvalidDataError("WMA superframe");
            int q = m_lastSuperframeLen, len = bitOffset;
            while (len > 7)
            {
                m_lastSuperframe[size_t(q++)] = uint8_t(m_gb.Get(8));
                len -= 8;
            }
            if (len > 0)
                m_lastSuperframe[size_t(q++)] = uint8_t(m_gb.Get(len) << (8 - len));
            std::fill_n(m_lastSuperframe.begin() + q, std::min<size_t>(16, m_lastSuperframe.size() - size_t(q)), uint8_t(0));
            BitReader save = m_gb;
            m_gb = BitReader(m_lastSuperframe, m_lastSuperframeLen * 8 + bitOffset, m_lastBitoffset);
            DecodeFrame(output);
            m_gb = save;
            nbFrames--;
        }
        int pos = bitOffset + 4 + 4 + m_byteOffsetBits + 3;
        if (pos >= 32768 * 8 || pos > bufSize * 8)
            throw InvalidDataError("WMA superframe");
        m_gb = BitReader(m_buf, bufSize * 8, pos);
        m_resetBlockLengths = true;
        for (int i = 0; i < nbFrames; i++)
            DecodeFrame(output);
        pos = m_gb.Position;
        m_lastBitoffset = pos & 7;
        pos >>= 3;
        int rest = bufSize - pos;
        if (rest > 32768 || rest < 0)
            throw InvalidDataError("WMA superframe");
        m_lastSuperframeLen = rest;
        std::memmove(m_lastSuperframe.data(), m_buf.data() + pos, size_t(rest));
    }
    catch (const InvalidDataError&)
    {
        m_lastSuperframeLen = 0;
        throw;
    }
}

} // namespace openshiina::formats
