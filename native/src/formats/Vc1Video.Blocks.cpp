// C# Formats/Vc1Video.Blocks.cs: VC-1 macroblocks and blocks (FFmpeg vc1_block.c): the picture
// loops, macroblock layers of I, P and B pictures, DC / AC prediction, the coefficients and the
// inverse transforms (vc1dsp.c).
#include <algorithm>
#include <array>
#include <cstdlib>
#include <cstring>
#include <format>
#include <random>
#include <string>

#if defined(__aarch64__)
#include <arm_neon.h>
#endif

#include "formats/Vc1Tables.h"
#include "formats/Vc1Video.Vlcs.h"
#include "formats/Vc1Video.h"

namespace openshiina::formats {

void Vc1Video::SetBlockIndex()
{
    // init_block_index + update_block_index for the current macroblock
    int b8s = m_b8Stride, mbs = m_mbStride, x = m_mbX, y = m_mbY;
    m_blockIndex[0] = b8s * (y * 2) + x * 2;
    m_blockIndex[1] = m_blockIndex[0] + 1;
    m_blockIndex[2] = b8s * (y * 2 + 1) + x * 2;
    m_blockIndex[3] = m_blockIndex[2] + 1;
    m_blockIndex[4] = mbs * (y + 1) + b8s * m_mbHeight * 2 + x;
    m_blockIndex[5] = mbs * (y + m_mbHeight + 2) + b8s * m_mbHeight * 2 + x;
    m_destY = y * 16 * m_cur->Frame.Stride + x * 16;
    m_destC = y * 8 * m_cur->Frame.ChromaStride + x * 8;
}

void Vc1Video::DecodeBlocks()
{
    // ff_vc1_decode_blocks
    m_blockWrap = {m_b8Stride, m_b8Stride, m_b8Stride, m_b8Stride, m_mbStride, m_mbStride};
    m_esc3LevelLength = 0;
    switch (m_pictType)
    {
    case TypeI:
        DecodeIBlocks();
        break;
    case TypeP:
        if (m_pSkipped != 0)
            DecodeSkipBlocks();
        else
            DecodePBlocks();
        break;
    case TypeB:
        if (m_biType != 0)
            DecodeIBlocks();
        else
            DecodeBBlocks();
        break;
    }
}

void Vc1Video::SelectCodingSets(int yIndex)
{
    switch (yIndex)
    {
    case 0:
        m_codingset = m_pqindex <= 8 ? 6 : 2; // high rate / low motion intra
        break;
    case 1:
        m_codingset = 0; // high motion intra
        break;
    default:
        m_codingset = 4; // mid rate intra
        break;
    }
    switch (m_cAcTableIndex)
    {
    case 0:
        m_codingset2 = m_pqindex <= 8 ? 7 : 3;
        break;
    case 1:
        m_codingset2 = 1;
        break;
    default:
        m_codingset2 = 5;
        break;
    }
}

void Vc1Video::CheckBits() const
{
    if (BitsLeft() < 0)
        throw InvalidDataError("VC-1 picture overread");
}

// ---- I pictures ----

void Vc1Video::DecodeIBlocks()
{
    // vc1_decode_i_blocks / vc1_decode_i_blocks_adv, then the overlap, the putting and the loop filter
    bool advanced = m_profile == ProfileAdvanced;
    SelectCodingSets(m_yAcTableIndex);
    m_yDcScale = m_cDcScale = Vc1Tables::DcScale[m_pq];
    m_mbIntra = 1;
    m_firstSliceLine = true;
    if (advanced && BitsLeft() <= 1)
        throw InvalidDataError("Empty I picture");
    Picture& cur = *m_cur;
    for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
    {
        for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
        {
            SetBlockIndex();
            int mbPos = MbPos();
            std::fill_n(m_blocks.begin() + std::ptrdiff_t(mbPos) * 6 * 64, 6 * 64, int16_t(0));
            for (int i = 0; i < 4; i++)
            {
                Motion(cur, 1, m_blockIndex[size_t(i)], 0) = 0;
                Motion(cur, 1, m_blockIndex[size_t(i)], 1) = 0;
            }
            if (advanced && BitsLeft() <= 1)
                throw InvalidDataError("I picture cut short");
            int cbp = GetVlc(Vlcs().MbIntra);
            int mquant = m_pq;
            if (advanced)
            {
                m_acPred = m_acpredIsRaw ? GetBit() : m_acpredPlane[size_t(mbPos)];
                if (m_condover == CondoverSelect && m_overflgIsRaw)
                    m_overFlagsPlane[size_t(mbPos)] = uint8_t(GetBit());
                GetMquant(mquant);
                m_qscale[size_t(mbPos)] = mquant;
                m_yDcScale = m_cDcScale = Vc1Tables::DcScale[std::abs(mquant)];
            }
            else
            {
                m_acPred = GetBit();
                m_qscale[size_t(mbPos)] = m_pq;
            }
            for (int k = 0; k < 6; k++)
            {
                MbType(m_blockIndex[size_t(k)]) = 1;
                int val = (cbp >> (5 - k)) & 1;
                if (k < 4)
                {
                    int coded;
                    int pred = CodedBlockPred(k, coded);
                    val ^= pred;
                    m_codedBlock[size_t(coded)] = uint8_t(val);
                }
                cbp |= val << (5 - k);
                int16_t* block = Block(k);
                if (advanced)
                {
                    m_aAvail = !m_firstSliceLine || k == 2 || k == 3 ? 1 : 0;
                    m_cAvail = m_mbX != 0 || k == 1 || k == 3 ? 1 : 0;
                    DecodeIBlockAdvanced(block, k, val, k < 4 ? m_codingset : m_codingset2, mquant);
                }
                else
                    DecodeIBlock(block, k, val, k < 4 ? m_codingset : m_codingset2);
                InvTrans8x8(block);
            }
            CheckBits();
            m_cbp[size_t(mbPos)] = 0xFFFFFF;
            m_isIntra[size_t(mbPos)] = 0x3F;
            m_ttblk[size_t(mbPos)] = 0;
        }
        m_firstSliceLine = false;
    }
    bool overlap = m_overlap != 0 && (m_pq >= 9 || (advanced && m_condover != CondoverNone));
    if (overlap)
        OverlapIntraPicture();
    PutIntraBlocks(overlap || advanced);
    if (m_loopFilter)
        LoopFilterIntra();
}

int Vc1Video::CodedBlockPred(int n, int& at)
{
    // vc1_coded_block_pred: the coded-block predictor of luma block n, and where to store the flag
    int xy = m_blockBase + m_blockIndex[size_t(n)], wrap = m_b8Stride;
    int a = m_codedBlock[size_t(xy - 1)], b = m_codedBlock[size_t(xy - 1 - wrap)], c = m_codedBlock[size_t(xy - wrap)];
    at = xy;
    return b == c ? a : c;
}

namespace {

constexpr uint16_t kDcPred[] = {
    0xFFFF, 1024, 512, 341, 256, 205, 171, 146, 128, 114, 102, 93, 85, 79, 73, 68, 64,
    60, 57, 54, 51, 49, 47, 45, 43, 41, 39, 38, 37, 35, 34, 33,
};

constexpr uint8_t kSizeTable[] = {0, 2, 3, 4, 5, 8};
constexpr int16_t kOffsetTable[] = {0, 1, 3, 7, 15, 31, 63, 127, 255};

/// (uint)a * b * c + 0x20000 as an int, >> 18: the scaling of a predicted DC or AC by quantizers.
int Rescale(uint32_t a, uint32_t b, uint32_t c) { return int(a * b * c + 0x20000u) >> 18; }

} // namespace

int Vc1Video::IPredDc(int n, int& dir)
{
    // vc1_i_pred_dc (Simple / Main I pictures): the predicted DC and its direction (1 left, 0 top)
    int scale = n < 4 ? m_yDcScale : m_cDcScale;
    int wrap = m_blockWrap[size_t(n)], xy = m_blockBase + m_blockIndex[size_t(n)];
    int c = m_dcVal[size_t(xy - 1)], b = m_dcVal[size_t(xy - 1 - wrap)], a = m_dcVal[size_t(xy - wrap)];
    int outer = m_pq < 9 || m_overlap == 0 ? int16_t(kDcPred[scale]) : 0;
    if (m_firstSliceLine && n != 2 && n != 3)
        b = a = outer;
    if (m_mbX == 0 && n != 1 && n != 3)
        b = c = outer;
    if (std::abs(a - b) <= std::abs(b - c))
    {
        dir = 1;
        return c;
    }
    dir = 0;
    return a;
}

int Vc1Video::PredDc(int n, int aAvail, int cAvail, int& dir)
{
    // ff_vc1_pred_dc (scaled by the neighbours' quantizers)
    int mbPos = MbPos();
    int q1 = std::abs(m_qscale[size_t(mbPos)]);
    int dqscaleIndex = Vc1Tables::DcScale[q1] - 1;
    dir = 1;
    if (dqscaleIndex < 0)
        return 0;
    int wrap = m_blockWrap[size_t(n)], xy = m_blockBase + m_blockIndex[size_t(n)];
    int c = m_dcVal[size_t(xy - 1)], b = m_dcVal[size_t(xy - 1 - wrap)], a = m_dcVal[size_t(xy - wrap)];
    uint32_t dq = uint32_t(Vc1Tables::Dqscale[dqscaleIndex]);
    int q2;
    if (cAvail != 0 && n != 1 && n != 3)
    {
        q2 = std::abs(m_qscale[size_t(mbPos - 1)]);
        if (q2 != 0 && q2 != q1)
            c = Rescale(uint32_t(c), Vc1Tables::DcScale[q2], dq);
    }
    if (aAvail != 0 && n != 2 && n != 3)
    {
        q2 = std::abs(m_qscale[size_t(mbPos - m_mbStride)]);
        if (q2 != 0 && q2 != q1)
            a = Rescale(uint32_t(a), Vc1Tables::DcScale[q2], dq);
    }
    if (aAvail != 0 && cAvail != 0 && n != 3)
    {
        int off = mbPos;
        if (n != 1)
            off--;
        if (n != 2)
            off -= m_mbStride;
        q2 = std::abs(m_qscale[size_t(off)]);
        if (q2 != 0 && q2 != q1)
            b = Rescale(uint32_t(b), Vc1Tables::DcScale[q2], dq);
    }
    if (cAvail != 0 && (aAvail == 0 || std::abs(a - b) <= std::abs(b - c)))
    {
        dir = 1;
        return c;
    }
    if (aAvail != 0)
    {
        dir = 0;
        return a;
    }
    dir = 1;
    return 0;
}

int Vc1Video::ReadDcDiff(int n, int quant)
{
    // The DC differential of an intra block
    int dcdiff = GetVlc(Vlcs().Dc[size_t(m_dcTableIndex)][n >= 4 ? 1 : 0]);
    if (dcdiff < 0)
        throw InvalidDataError("Bad DC code");
    if (dcdiff != 0)
    {
        int m = quant == 1 || quant == 2 ? 3 - quant : 0;
        if (dcdiff == 119)
            dcdiff = GetBits(8 + m);
        else if (m != 0)
            dcdiff = (dcdiff << m) + GetBits(m) - ((1 << m) - 1);
        if (GetBit() != 0)
            dcdiff = -dcdiff;
    }
    return dcdiff;
}

void Vc1Video::DecodeAcCoeff(bool& last, int& skip, int& value, int codingset)
{
    // vc1_decode_ac_coeff: one run / level / last of codingset's table
    const VlcTable& vlc = Vlcs().Ac[size_t(codingset)];
    int index = GetVlc(vlc);
    if (index < 0)
        throw InvalidDataError("Bad AC code");
    int run, level, sign;
    bool lst;
    int size = Vc1Tables::AcSizes[codingset];
    if (index != size - 1)
    {
        run = Vc1Tables::IndexDecode[(codingset * AcEscapeIndexRows + index) * 2];
        level = Vc1Tables::IndexDecode[(codingset * AcEscapeIndexRows + index) * 2 + 1];
        lst = index >= Vc1Tables::LastDecode[codingset] || BitsLeft() < 0;
        sign = GetBit();
    }
    else
    {
        int escape = Decode210();
        if (escape != 2)
        {
            index = GetVlc(vlc);
            if (uint32_t(index) >= uint32_t(size - 1))
                throw InvalidDataError("Bad AC escape");
            run = Vc1Tables::IndexDecode[(codingset * AcEscapeIndexRows + index) * 2];
            level = Vc1Tables::IndexDecode[(codingset * AcEscapeIndexRows + index) * 2 + 1];
            lst = index >= Vc1Tables::LastDecode[codingset];
            if (escape == 0)
                level += lst ? Vc1Tables::LastDeltaLevel[codingset * 44 + run] : Vc1Tables::DeltaLevel[codingset * 31 + run];
            else
                run += (lst ? Vc1Tables::LastDeltaRun[codingset * 10 + level] : Vc1Tables::DeltaRun[codingset * 57 + level]) + 1;
            sign = GetBit();
        }
        else
        {
            lst = GetBit() != 0;
            if (m_esc3LevelLength == 0)
            {
                if (m_pq < 8 || m_dquantfrm != 0)
                {
                    m_esc3LevelLength = GetBits(3);
                    if (m_esc3LevelLength == 0)
                        m_esc3LevelLength = GetBits(2) + 8;
                }
                else
                    m_esc3LevelLength = GetUnary(1, 6) + 2;
                m_esc3RunLength = 3 + GetBits(2);
            }
            run = GetBits(m_esc3RunLength);
            sign = GetBit();
            level = GetBits(m_esc3LevelLength);
        }
    }
    last = lst;
    skip = run;
    value = (level ^ -sign) + sign;
}

void Vc1Video::DecodeIBlock(int16_t* block, int n, int coded, int codingset)
{
    // vc1_decode_i_block: an intra block of a Simple / Main I picture
    int dcdiff = ReadDcDiff(n, m_pq);
    int dcPredDir;
    dcdiff += IPredDc(n, dcPredDir);
    int xy = m_blockBase + m_blockIndex[size_t(n)];
    m_dcVal[size_t(xy)] = int16_t(dcdiff);
    block[0] = int16_t(dcdiff * (n < 4 ? m_yDcScale : m_cDcScale));

    int16_t* ac = m_acVal.data();
    int acVal = xy * 16, acVal2 = acVal;
    acVal -= dcPredDir != 0 ? 16 : 16 * m_blockWrap[size_t(n)];
    int scale = m_pq * 2 + m_halfpq;
    int i = coded != 0 ? 1 : 0;
    if (coded != 0)
    {
        const uint8_t* zz = m_acPred != 0 ? (dcPredDir == 0 ? m_zz8x8[2].data() : m_zz8x8[3].data()) : m_zz8x8[1].data();
        bool last = false;
        while (!last)
        {
            int skip, value;
            DecodeAcCoeff(last, skip, value, codingset);
            i += skip;
            if (i > 63)
                break;
            block[zz[i++]] = int16_t(value);
        }
        if (m_acPred != 0)
        {
            int sh;
            if (dcPredDir != 0)
                sh = LeftBlockShift;
            else
            {
                sh = TopBlockShift;
                acVal += 8;
            }
            for (int k = 1; k < 8; k++)
                block[k << sh] = int16_t(block[k << sh] + ac[acVal + k]);
        }
        for (int k = 1; k < 8; k++)
        {
            ac[acVal2 + k] = block[k << LeftBlockShift];
            ac[acVal2 + k + 8] = block[k << TopBlockShift];
        }
        for (int k = 1; k < 64; k++)
            if (block[k] != 0)
            {
                int v = block[k] * scale;
                if (m_pquantizer == 0)
                    v += v < 0 ? -m_pq : m_pq;
                block[k] = int16_t(v);
            }
    }
    else
    {
        std::fill_n(ac + acVal2, 16, int16_t(0));
        if (m_acPred != 0)
        {
            int sh;
            if (dcPredDir != 0)
                sh = LeftBlockShift;
            else
            {
                sh = TopBlockShift;
                acVal += 8;
                acVal2 += 8;
            }
            std::memmove(ac + acVal2, ac + acVal, 8 * sizeof(int16_t));
            for (int k = 1; k < 8; k++)
            {
                int v = ac[acVal + k] * scale;
                if (m_pquantizer == 0 && v != 0)
                    v += v < 0 ? -m_pq : m_pq;
                block[k << sh] = int16_t(v);
            }
        }
    }
}

void Vc1Video::DecodeIBlockAdvanced(int16_t* block, int n, int coded, int codingset, int mquant)
{
    // vc1_decode_i_block_adv: an intra block of an Advanced profile I picture
    int aAvail = m_aAvail, cAvail = m_cAvail;
    int usePred = m_acPred;
    int mbPos = MbPos();
    int quant = std::abs(mquant);
    int dcdiff = ReadDcDiff(n, quant);
    int dcPredDir;
    dcdiff += PredDc(n, aAvail, cAvail, dcPredDir);
    int xy = m_blockBase + m_blockIndex[size_t(n)];
    m_dcVal[size_t(xy)] = int16_t(dcdiff);
    block[0] = int16_t(dcdiff * (n < 4 ? m_yDcScale : m_cDcScale));
    if (aAvail == 0 && cAvail == 0)
        usePred = 0;
    int scale = quant * 2 + (mquant < 0 ? 0 : m_halfpq);
    int16_t* ac = m_acVal.data();
    int acVal = xy * 16, acVal2 = acVal;
    acVal -= dcPredDir != 0 ? 16 : 16 * m_blockWrap[size_t(n)];

    int q1 = m_qscale[size_t(mbPos)], q2 = 0;
    if (n == 3)
        q2 = q1;
    else if (dcPredDir != 0)
    {
        if (n == 1)
            q2 = q1;
        else if (cAvail != 0 && mbPos != 0)
            q2 = m_qscale[size_t(mbPos - 1)];
    }
    else
    {
        if (n == 2)
            q2 = q1;
        else if (aAvail != 0 && mbPos >= m_mbStride)
            q2 = m_qscale[size_t(mbPos - m_mbStride)];
    }

    int i = 1;
    if (coded != 0)
    {
        const uint8_t* zz = m_acPred != 0 ? (dcPredDir == 0 ? m_zz8x8[2].data() : m_zz8x8[3].data()) : m_zz8x8[1].data();
        bool last = false;
        while (!last)
        {
            int skip, value;
            DecodeAcCoeff(last, skip, value, codingset);
            i += skip;
            if (i > 63)
                break;
            block[zz[i++]] = int16_t(value);
        }
        if (usePred != 0)
        {
            int sh;
            if (dcPredDir != 0)
                sh = LeftBlockShift;
            else
            {
                sh = TopBlockShift;
                acVal += 8;
            }
            q1 = std::abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
            if (q1 < 1)
                throw InvalidDataError("Bad quantizer");
            if (q2 != 0)
                q2 = std::abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
            if (q2 != 0 && q1 != q2)
            {
                for (int k = 1; k < 8; k++)
                    block[k << sh] = int16_t(block[k << sh] + int16_t(Rescale(uint32_t(int(ac[acVal + k])), uint32_t(q2), uint32_t(Vc1Tables::Dqscale[q1 - 1]))));
            }
            else
                for (int k = 1; k < 8; k++)
                    block[k << sh] = int16_t(block[k << sh] + ac[acVal + k]);
        }
        for (int k = 1; k < 8; k++)
        {
            ac[acVal2 + k] = block[k << LeftBlockShift];
            ac[acVal2 + k + 8] = block[k << TopBlockShift];
        }
        for (int k = 1; k < 64; k++)
            if (block[k] != 0)
            {
                int v = block[k] * scale;
                if (m_pquantizer == 0)
                    v += v < 0 ? -quant : quant;
                block[k] = int16_t(v);
            }
    }
    else
    {
        std::fill_n(ac + acVal2, 16, int16_t(0));
        if (usePred != 0)
        {
            int sh;
            if (dcPredDir != 0)
                sh = LeftBlockShift;
            else
            {
                sh = TopBlockShift;
                acVal += 8;
                acVal2 += 8;
            }
            std::memmove(ac + acVal2, ac + acVal, 8 * sizeof(int16_t));
            q1 = std::abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
            if (q1 < 1)
                throw InvalidDataError("Bad quantizer");
            if (q2 != 0)
                q2 = std::abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
            if (q2 != 0 && q1 != q2)
                for (int k = 1; k < 8; k++)
                    ac[acVal2 + k] = int16_t(Rescale(uint32_t(ac[acVal2 + k] * q2), 1, uint32_t(Vc1Tables::Dqscale[q1 - 1])));
            for (int k = 1; k < 8; k++)
            {
                int v = ac[acVal2 + k] * scale;
                if (m_pquantizer == 0 && v != 0)
                    v += v < 0 ? -quant : quant;
                block[k << sh] = int16_t(v);
            }
        }
    }
}

void Vc1Video::DecodeIntraBlock(int16_t* block, int n, int coded, int mquant, int codingset)
{
    // vc1_decode_intra_block: an intra block in a P or B picture
    int mbPos = MbPos();
    int aAvail = m_aAvail, cAvail = m_cAvail;
    int usePred = m_acPred;
    std::fill_n(block, 64, int16_t(0));
    int quant = std::abs(mquant) & 31;
    m_yDcScale = m_cDcScale = Vc1Tables::DcScale[quant];
    int dcdiff = ReadDcDiff(n, quant);
    int dcPredDir;
    dcdiff += PredDc(n, aAvail, cAvail, dcPredDir);
    int xy = m_blockBase + m_blockIndex[size_t(n)];
    m_dcVal[size_t(xy)] = int16_t(dcdiff);
    block[0] = int16_t(dcdiff * (n < 4 ? m_yDcScale : m_cDcScale));

    int i = 1;
    if (aAvail == 0)
        dcPredDir = 1;
    if (cAvail == 0)
        dcPredDir = 0;
    if (aAvail == 0 && cAvail == 0)
        usePred = 0;
    int16_t* ac = m_acVal.data();
    int acVal = xy * 16, acVal2 = acVal;
    int scale = quant * 2 + (mquant < 0 ? 0 : m_halfpq);
    acVal -= dcPredDir != 0 ? 16 : 16 * m_blockWrap[size_t(n)];

    int q1 = m_qscale[size_t(mbPos)], q2 = 0;
    if (dcPredDir != 0 && cAvail != 0 && mbPos != 0)
        q2 = m_qscale[size_t(mbPos - 1)];
    if (dcPredDir == 0 && aAvail != 0 && mbPos >= m_mbStride)
        q2 = m_qscale[size_t(mbPos - m_mbStride)];
    if (dcPredDir != 0 && n == 1)
        q2 = q1;
    if (dcPredDir == 0 && n == 2)
        q2 = q1;
    if (n == 3)
        q2 = q1;

    if (coded != 0)
    {
        const uint8_t* zz = m_zz8x8[0].data();
        bool last = false;
        while (!last)
        {
            int skip, value;
            DecodeAcCoeff(last, skip, value, codingset);
            i += skip;
            if (i > 63)
                break;
            block[zz[i++]] = int16_t(value);
        }
        if (usePred != 0)
        {
            q1 = std::abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
            if (q1 < 1)
                throw InvalidDataError("Bad quantizer");
            if (q2 != 0)
                q2 = std::abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
            uint32_t dq = uint32_t(Vc1Tables::Dqscale[std::max(q1 - 1, 0)]);
            if (q2 != 0 && q1 != q2)
            {
                if (dcPredDir != 0)
                    for (int k = 1; k < 8; k++)
                        block[k << LeftBlockShift] = int16_t(block[k << LeftBlockShift] + int16_t(Rescale(uint32_t(ac[acVal + k] * q2), 1, dq)));
                else
                    for (int k = 1; k < 8; k++)
                        block[k << TopBlockShift] = int16_t(block[k << TopBlockShift] + int16_t(Rescale(uint32_t(ac[acVal + k + 8] * q2), 1, dq)));
            }
            else
            {
                if (dcPredDir != 0)
                    for (int k = 1; k < 8; k++)
                        block[k << LeftBlockShift] = int16_t(block[k << LeftBlockShift] + ac[acVal + k]);
                else
                    for (int k = 1; k < 8; k++)
                        block[k << TopBlockShift] = int16_t(block[k << TopBlockShift] + ac[acVal + k + 8]);
            }
        }
        for (int k = 1; k < 8; k++)
        {
            ac[acVal2 + k] = block[k << LeftBlockShift];
            ac[acVal2 + k + 8] = block[k << TopBlockShift];
        }
        for (int k = 1; k < 64; k++)
            if (block[k] != 0)
            {
                int v = block[k] * scale;
                if (m_pquantizer == 0)
                    v += v < 0 ? -quant : quant;
                block[k] = int16_t(v);
            }
    }
    else
    {
        std::fill_n(ac + acVal2, 16, int16_t(0));
        if (usePred != 0)
        {
            q1 = std::abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
            if (q1 < 1)
                throw InvalidDataError("Bad quantizer");
            if (q2 != 0)
                q2 = std::abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
            int part = dcPredDir != 0 ? 0 : 8;
            std::memmove(ac + acVal2 + part, ac + acVal + part, 8 * sizeof(int16_t));
            if (q2 != 0 && q1 != q2)
                for (int k = 1; k < 8; k++)
                    ac[acVal2 + part + k] = int16_t(Rescale(uint32_t(int(ac[acVal2 + part + k])), uint32_t(q2), uint32_t(Vc1Tables::Dqscale[q1 - 1])));
            int sh = dcPredDir != 0 ? LeftBlockShift : TopBlockShift;
            for (int k = 1; k < 8; k++)
            {
                int v = ac[acVal2 + part + k] * scale;
                if (m_pquantizer == 0 && v != 0)
                    v += v < 0 ? -quant : quant;
                block[k << sh] = int16_t(v);
            }
        }
    }
}

int Vc1Video::DecodePBlock(int16_t* block, int n, int mquant, int ttmb, bool firstBlock, uint8_t* plane, int dst, int linesize, int& ttmbOut)
{
    // vc1_decode_p_block: an inter block's coefficients added to the prediction at dst; returns the
    // pattern of coded 4x4 parts (bit 3 top left .. bit 0 bottom right) and adds the block's
    // transform type to ttmbOut
    int subblkpat = 0;
    int ttblk = ttmb & 7;
    int pat = 0;
    int quant = std::abs(mquant);
    std::fill_n(block, 64, int16_t(0));
    if (ttmb == -1)
        ttblk = Vc1Tables::TtblkToTt[m_ttIndex * 8 + GetVlc(Vlcs().Ttblk[size_t(m_ttIndex)])];
    if (ttblk == Tt4x4)
        subblkpat = ~(GetVlc(Vlcs().Subblkpat[size_t(m_ttIndex)]) + 1);
    if (ttblk != Tt8x8 && ttblk != Tt4x4
        && (m_ttmbf != 0 || (ttmb != -1 && (ttmb & 8) != 0 && !firstBlock) || (m_resRtmFlag == 0 && !firstBlock)))
    {
        subblkpat = Decode012();
        if (subblkpat != 0)
            subblkpat ^= 3;
        if (ttblk == Tt8x4Top || ttblk == Tt8x4Bottom)
            ttblk = Tt8x4;
        if (ttblk == Tt4x8Right || ttblk == Tt4x8Left)
            ttblk = Tt4x8;
    }
    int scale = quant * 2 + (mquant < 0 ? 0 : m_halfpq);
    if (ttblk == Tt8x4Top || ttblk == Tt8x4Bottom)
    {
        subblkpat = 2 - (ttblk == Tt8x4Top ? 1 : 0);
        ttblk = Tt8x4;
    }
    if (ttblk == Tt4x8Right || ttblk == Tt4x8Left)
    {
        subblkpat = 2 - (ttblk == Tt4x8Left ? 1 : 0);
        ttblk = Tt4x8;
    }
    switch (ttblk)
    {
    case Tt8x8:
    {
        pat = 0xF;
        int i = 0;
        bool last = false;
        while (!last)
        {
            int skip, value;
            DecodeAcCoeff(last, skip, value, m_codingset2);
            i += skip;
            if (i > 63)
                break;
            int idx = m_zz8x8[0][size_t(i++)];
            block[idx] = int16_t(Dequant(value, scale, quant));
        }
        if (i == 1)
            InvTrans8x8Dc(plane, dst, linesize, block);
        else
        {
            InvTrans8x8(block);
            AddPixelsClamped(block, plane, dst, linesize);
        }
        break;
    }
    case Tt4x4:
        pat = ~subblkpat & 0xF;
        for (int j = 0; j < 4; j++)
        {
            bool last = (subblkpat & (1 << (3 - j))) != 0;
            int i = 0, off = (j & 1) * 4 + (j & 2) * 16;
            while (!last)
            {
                int skip, value;
                DecodeAcCoeff(last, skip, value, m_codingset2);
                i += skip;
                if (i > 15)
                    break;
                int idx = Vc1Tables::SimpleProgressive4x4[i++];
                block[idx + off] = int16_t(Dequant(value, scale, quant));
            }
            if ((subblkpat & (1 << (3 - j))) == 0)
            {
                int at = dst + (j & 1) * 4 + (j & 2) * 2 * linesize;
                if (i == 1)
                    InvTrans4x4Dc(plane, at, linesize, block[off]);
                else
                    InvTrans4x4(plane, at, linesize, block + off);
            }
        }
        break;
    case Tt8x4:
        pat = ~((subblkpat & 2) * 6 + (subblkpat & 1) * 3) & 0xF;
        for (int j = 0; j < 2; j++)
        {
            bool last = (subblkpat & (1 << (1 - j))) != 0;
            int i = 0, off = j * 32;
            while (!last)
            {
                int skip, value;
                DecodeAcCoeff(last, skip, value, m_codingset2);
                i += skip;
                if (i > 31)
                    break;
                int idx = m_zz8x4[i++] + off;
                block[idx] = int16_t(Dequant(value, scale, quant));
            }
            if ((subblkpat & (1 << (1 - j))) == 0)
            {
                int at = dst + j * 4 * linesize;
                if (i == 1)
                    InvTrans8x4Dc(plane, at, linesize, block[off]);
                else
                    InvTrans8x4(plane, at, linesize, block + off);
            }
        }
        break;
    case Tt4x8:
        pat = ~(subblkpat * 5) & 0xF;
        for (int j = 0; j < 2; j++)
        {
            bool last = (subblkpat & (1 << (1 - j))) != 0;
            int i = 0, off = j * 4;
            while (!last)
            {
                int skip, value;
                DecodeAcCoeff(last, skip, value, m_codingset2);
                i += skip;
                if (i > 31)
                    break;
                int idx = m_zz4x8[i++] + off;
                block[idx] = int16_t(Dequant(value, scale, quant));
            }
            if ((subblkpat & (1 << (1 - j))) == 0)
            {
                int at = dst + j * 4;
                if (i == 1)
                    InvTrans4x8Dc(plane, at, linesize, block[off]);
                else
                    InvTrans4x8(plane, at, linesize, block + off);
            }
        }
        break;
    }
    ttmbOut |= ttblk << (n * 4);
    return pat;
}

int Vc1Video::Dequant(int value, int scale, int quant) const
{
    int v = value * scale;
    if (m_pquantizer == 0)
        v += v < 0 ? -quant : quant;
    return v;
}

void Vc1Video::GetMquant(int& mquant)
{
    // GET_MQUANT: the macroblock quantizer (negative: no half step)
    if (m_dquantfrm == 0)
        return;
    int edges = 0;
    if (m_dqprofile == 3)
    {
        if (m_dqbilevel != 0)
            mquant = GetBit() != 0 ? -m_altpq : m_pq;
        else
        {
            int mqdiff = GetBits(3);
            mquant = mqdiff != 7 ? -m_pq - mqdiff : -GetBits(5);
        }
    }
    if (m_dqprofile == 2)
        edges = 1 << m_dqsbedge;
    else if (m_dqprofile == 1)
        edges = (3 << m_dqsbedge) % 15;
    else if (m_dqprofile == 0)
        edges = 15;
    if ((edges & 1) != 0 && m_mbX == 0)
        mquant = -m_altpq;
    if ((edges & 2) != 0 && m_mbY == 0)
        mquant = -m_altpq;
    if ((edges & 4) != 0 && m_mbX == m_mbWidth - 1)
        mquant = -m_altpq;
    if ((edges & 8) != 0 && m_mbY == m_mbHeight - 1)
        mquant = -m_altpq;
    if (mquant == 0 || mquant > 31 || mquant < -31)
        mquant = 1;
}

void Vc1Video::GetMvData(int& dmvX, int& dmvY)
{
    // GET_MVDATA: a motion vector differential; sets m_mbHasCoeffs and m_mbIntra
    int index = 1 + GetVlc(Vlcs().MvDiff[size_t(m_mvTableIndex)]);
    if (index > 36)
    {
        m_mbHasCoeffs = 1;
        index -= 37;
    }
    else
        m_mbHasCoeffs = 0;
    m_mbIntra = 0;
    if (index == 0)
        dmvX = dmvY = 0;
    else if (index == 35)
    {
        dmvX = GetBits(m_kx - 1 + m_quarterSample);
        dmvY = GetBits(m_ky - 1 + m_quarterSample);
    }
    else if (index == 36)
    {
        dmvX = dmvY = 0;
        m_mbIntra = 1;
    }
    else
    {
        int index1 = index % 6;
        dmvX = kOffsetTable[index1];
        int val = kSizeTable[index1] - (m_quarterSample == 0 && index1 == 5 ? 1 : 0);
        if (val > 0)
        {
            val = GetBits(val);
            int sign = -(val & 1);
            dmvX = (sign ^ ((val >> 1) + dmvX)) - sign;
        }
        index1 = index / 6;
        dmvY = kOffsetTable[index1];
        val = kSizeTable[index1] - (m_quarterSample == 0 && index1 == 5 ? 1 : 0);
        if (val > 0)
        {
            val = GetBits(val);
            int sign = -(val & 1);
            dmvY = (sign ^ ((val >> 1) + dmvY)) - sign;
        }
    }
}

// ---- P pictures ----

void Vc1Video::DecodePBlocks()
{
    // vc1_decode_p_blocks, then the overlap of intra blocks, their putting and the loop filter
    SelectCodingSets(m_cAcTableIndex);
    m_firstSliceLine = true;
    for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
    {
        for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
        {
            SetBlockIndex();
            if ((m_mvTypeIsRaw || m_skipIsRaw) && BitsLeft() <= 1)
                throw InvalidDataError("P picture cut short");
            DecodePMb();
            CheckBits();
        }
        m_firstSliceLine = false;
    }
    bool overlap = m_overlap != 0 && m_pq >= 9;
    if (overlap)
        OverlapInterPicture();
    PutIntraBlocks(true);
    if (m_loopFilter)
        LoopFilterInter();
}

void Vc1Video::DecodePMb()
{
    // vc1_decode_p_mb
    int mbPos = MbPos();
    int ttmb = m_ttfrm;
    bool firstBlock = true;
    int blockCbp = 0, blockTt = 0, blockIntra = 0;
    int mquant = m_pq;
    Picture& cur = *m_cur;
    int fourmv = m_mvTypeIsRaw ? GetBit() : m_mvTypePlane[size_t(mbPos)];
    int skipped = m_skipIsRaw ? GetBit() : m_skipPlane[size_t(mbPos)];
    int stride = cur.Frame.Stride, cstride = cur.Frame.ChromaStride;

    if (fourmv == 0)
    {
        if (skipped == 0)
        {
            int dmvX, dmvY;
            GetMvData(dmvX, dmvY);
            if (m_mbIntra != 0)
            {
                Motion(cur, 1, m_blockIndex[0], 0) = 0;
                Motion(cur, 1, m_blockIndex[0], 1) = 0;
            }
            PredMv(0, dmvX, dmvY, true, m_rangeX, m_rangeY, 0);
            int cbp;
            if (m_mbIntra != 0 && m_mbHasCoeffs == 0)
            {
                GetMquant(mquant);
                m_acPred = GetBit();
                cbp = 0;
            }
            else if (m_mbHasCoeffs != 0)
            {
                if (m_mbIntra != 0)
                    m_acPred = GetBit();
                cbp = GetVlc(Vlcs().CbpcyP[size_t(m_cbptab)]);
                GetMquant(mquant);
            }
            else
            {
                mquant = m_pq;
                cbp = 0;
            }
            m_qscale[size_t(mbPos)] = mquant;
            if (m_ttmbf == 0 && m_mbIntra == 0 && m_mbHasCoeffs != 0)
                ttmb = GetVlc(Vlcs().Ttmb[size_t(m_ttIndex)]);
            if (m_mbIntra == 0)
                Mc1Mv(0);
            for (int i = 0; i < 6; i++)
            {
                DcVal(m_blockIndex[size_t(i)]) = 0;
                int val = (cbp >> (5 - i)) & 1;
                MbType(m_blockIndex[size_t(i)]) = uint8_t(m_mbIntra);
                if (m_mbIntra != 0)
                {
                    m_aAvail = m_cAvail = 0;
                    if (i == 2 || i == 3 || !m_firstSliceLine)
                        m_aAvail = MbType(m_blockIndex[size_t(i)] - m_blockWrap[size_t(i)]);
                    if (i == 1 || i == 3 || m_mbX != 0)
                        m_cAvail = MbType(m_blockIndex[size_t(i)] - 1);
                    int16_t* block = Block(i);
                    DecodeIntraBlock(block, i, val, mquant, i < 4 ? m_codingset : m_codingset2);
                    InvTrans8x8(block);
                    blockCbp |= 0xF << (i << 2);
                    blockIntra |= 1 << i;
                }
                else if (val != 0)
                {
                    Destination d = Dest(i, stride, cstride);
                    int pat = DecodePBlock(m_scratch.data(), i, mquant, ttmb, firstBlock, d.Plane, d.Offset, d.Stride, blockTt);
                    blockCbp |= pat << (i << 2);
                    if (m_ttmbf == 0 && ttmb < 8)
                        ttmb = -1;
                    firstBlock = false;
                }
            }
        }
        else
        {
            m_mbIntra = 0;
            for (int i = 0; i < 6; i++)
            {
                MbType(m_blockIndex[size_t(i)]) = 0;
                DcVal(m_blockIndex[size_t(i)]) = 0;
            }
            m_qscale[size_t(mbPos)] = 0;
            PredMv(0, 0, 0, true, m_rangeX, m_rangeY, 0);
            Mc1Mv(0);
        }
    }
    else
    {
        if (skipped == 0)
        {
            int intraCount = 0;
            bool codedInter = false;
            int isIntra[6] = {}, isCoded[6] = {};
            int cbp = GetVlc(Vlcs().CbpcyP[size_t(m_cbptab)]);
            for (int i = 0; i < 6; i++)
            {
                int val = (cbp >> (5 - i)) & 1;
                DcVal(m_blockIndex[size_t(i)]) = 0;
                m_mbIntra = 0;
                if (i < 4)
                {
                    int dmvX = 0, dmvY = 0;
                    m_mbHasCoeffs = 0;
                    if (val != 0)
                        GetMvData(dmvX, dmvY);
                    PredMv(i, dmvX, dmvY, false, m_rangeX, m_rangeY, 0);
                    if (m_mbIntra == 0)
                        Mc4MvLuma(i, 0, false);
                    intraCount += m_mbIntra;
                    isIntra[i] = m_mbIntra;
                    isCoded[i] = m_mbHasCoeffs;
                }
                if (i >= 4)
                {
                    isIntra[i] = intraCount >= 3 ? 1 : 0;
                    isCoded[i] = val;
                }
                if (i == 4)
                    Mc4MvChroma(0);
                MbType(m_blockIndex[size_t(i)]) = uint8_t(isIntra[i]);
                if (!codedInter)
                    codedInter = isIntra[i] == 0 && isCoded[i] != 0;
            }
            if (intraCount != 0 || codedInter)
            {
                GetMquant(mquant);
                m_qscale[size_t(mbPos)] = mquant;
                bool intrapred = false;
                for (int i = 0; i < 6 && !intrapred; i++)
                    if (isIntra[i] != 0)
                    {
                        if (((!m_firstSliceLine || i == 2 || i == 3) && MbType(m_blockIndex[size_t(i)] - m_blockWrap[size_t(i)]) != 0)
                            || ((m_mbX != 0 || i == 1 || i == 3) && MbType(m_blockIndex[size_t(i)] - 1) != 0))
                            intrapred = true;
                    }
                m_acPred = intrapred ? GetBit() : 0;
                if (m_ttmbf == 0 && codedInter)
                    ttmb = GetVlc(Vlcs().Ttmb[size_t(m_ttIndex)]);
                for (int i = 0; i < 6; i++)
                {
                    m_mbIntra = isIntra[i];
                    if (isIntra[i] != 0)
                    {
                        m_aAvail = m_cAvail = 0;
                        if (i == 2 || i == 3 || !m_firstSliceLine)
                            m_aAvail = MbType(m_blockIndex[size_t(i)] - m_blockWrap[size_t(i)]);
                        if (i == 1 || i == 3 || m_mbX != 0)
                            m_cAvail = MbType(m_blockIndex[size_t(i)] - 1);
                        int16_t* block = Block(i);
                        DecodeIntraBlock(block, i, isCoded[i], mquant, i < 4 ? m_codingset : m_codingset2);
                        InvTrans8x8(block);
                        blockCbp |= 0xF << (i << 2);
                        blockIntra |= 1 << i;
                    }
                    else if (isCoded[i] != 0)
                    {
                        Destination d = Dest(i, stride, cstride);
                        int pat = DecodePBlock(m_scratch.data(), i, mquant, ttmb, firstBlock, d.Plane, d.Offset, d.Stride, blockTt);
                        blockCbp |= pat << (i << 2);
                        if (m_ttmbf == 0 && ttmb < 8)
                            ttmb = -1;
                        firstBlock = false;
                    }
                }
            }
        }
        else
        {
            m_mbIntra = 0;
            m_qscale[size_t(mbPos)] = 0;
            for (int i = 0; i < 6; i++)
            {
                MbType(m_blockIndex[size_t(i)]) = 0;
                DcVal(m_blockIndex[size_t(i)]) = 0;
            }
            for (int i = 0; i < 4; i++)
            {
                PredMv(i, 0, 0, false, m_rangeX, m_rangeY, 0);
                Mc4MvLuma(i, 0, false);
            }
            Mc4MvChroma(0);
            m_qscale[size_t(mbPos)] = 0;
        }
    }
    m_cbp[size_t(mbPos)] = uint32_t(blockCbp);
    m_ttblk[size_t(mbPos)] = blockTt;
    m_isIntra[size_t(mbPos)] = uint8_t(blockIntra);
}

Vc1Video::Destination Vc1Video::Dest(int n, int stride, int cstride)
{
    // Where block n of the current macroblock is in the picture: plane, offset, stride
    MpegFrame& f = m_cur->Frame;
    if (n < 4)
        return {f.Y.data(), m_destY + (n & 1) * 8 + (n & 2) * 4 * stride, stride};
    if (n == 4)
        return {f.Cb.data(), m_destC, cstride};
    return {f.Cr.data(), m_destC, cstride};
}

// ---- B pictures and skipped pictures ----

void Vc1Video::DecodeBBlocks()
{
    // vc1_decode_b_blocks, then the loop filter
    SelectCodingSets(m_cAcTableIndex);
    m_firstSliceLine = true;
    for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
    {
        for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
        {
            SetBlockIndex();
            if ((m_skipIsRaw || m_dmbIsRaw) && BitsLeft() <= 1)
                throw InvalidDataError("B picture cut short");
            DecodeBMb();
            CheckBits();
        }
        m_firstSliceLine = false;
    }
    if (m_loopFilter)
        LoopFilterIntra();
}

void Vc1Video::DecodeBMb()
{
    // vc1_decode_b_mb
    int mbPos = MbPos();
    int ttmb = m_ttfrm;
    bool firstBlock = true;
    int mquant = m_pq;
    int cbp = 0;
    m_mbIntra = 0;
    m_mbHasCoeffs = 0;
    int direct = m_dmbIsRaw ? GetBit() : m_directPlane[size_t(mbPos)];
    int skipped = m_skipIsRaw ? GetBit() : m_skipPlane[size_t(mbPos)];
    int dmvX[2] = {}, dmvY[2] = {};
    int bmvtype = BmvBackward;
    for (int i = 0; i < 6; i++)
    {
        MbType(m_blockIndex[size_t(i)]) = 0;
        DcVal(m_blockIndex[size_t(i)]) = 0;
    }
    m_qscale[size_t(mbPos)] = 0;
    if (direct == 0)
    {
        if (skipped == 0)
        {
            GetMvData(dmvX[0], dmvY[0]);
            dmvX[1] = dmvX[0];
            dmvY[1] = dmvY[0];
        }
        if (skipped != 0 || m_mbIntra == 0)
        {
            bmvtype = Decode012();
            switch (bmvtype)
            {
            case 0:
                bmvtype = m_bfraction >= 128 ? BmvBackward : BmvForward;
                break;
            case 1:
                bmvtype = m_bfraction >= 128 ? BmvForward : BmvBackward;
                break;
            case 2:
                bmvtype = BmvInterpolated;
                dmvX[0] = dmvY[0] = 0;
                break;
            }
        }
    }
    for (int i = 0; i < 6; i++)
        MbType(m_blockIndex[size_t(i)]) = uint8_t(m_mbIntra);

    if (skipped != 0)
    {
        if (direct != 0)
            bmvtype = BmvInterpolated;
        PredBMv(dmvX, dmvY, direct != 0, bmvtype);
        BMc(direct != 0, bmvtype);
        return;
    }
    if (direct != 0)
    {
        cbp = GetVlc(Vlcs().CbpcyP[size_t(m_cbptab)]);
        GetMquant(mquant);
        m_mbIntra = 0;
        m_qscale[size_t(mbPos)] = mquant;
        if (m_ttmbf == 0)
            ttmb = GetVlc(Vlcs().Ttmb[size_t(m_ttIndex)]);
        dmvX[0] = dmvY[0] = dmvX[1] = dmvY[1] = 0;
        PredBMv(dmvX, dmvY, true, bmvtype);
        BMc(true, bmvtype);
    }
    else
    {
        if (m_mbHasCoeffs == 0 && m_mbIntra == 0)
        {
            PredBMv(dmvX, dmvY, false, bmvtype);
            BMc(false, bmvtype);
            return;
        }
        if (m_mbIntra != 0 && m_mbHasCoeffs == 0)
        {
            GetMquant(mquant);
            m_qscale[size_t(mbPos)] = mquant;
            m_acPred = GetBit();
            cbp = 0;
            PredBMv(dmvX, dmvY, false, bmvtype);
        }
        else
        {
            if (bmvtype == BmvInterpolated)
            {
                GetMvData(dmvX[0], dmvY[0]);
                if (m_mbHasCoeffs == 0)
                {
                    PredBMv(dmvX, dmvY, false, bmvtype);
                    BMc(false, bmvtype);
                    return;
                }
            }
            PredBMv(dmvX, dmvY, false, bmvtype);
            if (m_mbIntra == 0)
                BMc(false, bmvtype);
            if (m_mbIntra != 0)
                m_acPred = GetBit();
            cbp = GetVlc(Vlcs().CbpcyP[size_t(m_cbptab)]);
            GetMquant(mquant);
            m_qscale[size_t(mbPos)] = mquant;
            if (m_ttmbf == 0 && m_mbIntra == 0 && m_mbHasCoeffs != 0)
                ttmb = GetVlc(Vlcs().Ttmb[size_t(m_ttIndex)]);
        }
    }
    MpegFrame& f = m_cur->Frame;
    int stride = f.Stride, cstride = f.ChromaStride;
    int ignored = 0;
    for (int i = 0; i < 6; i++)
    {
        DcVal(m_blockIndex[size_t(i)]) = 0;
        int val = (cbp >> (5 - i)) & 1;
        MbType(m_blockIndex[size_t(i)]) = uint8_t(m_mbIntra);
        Destination d = Dest(i, stride, cstride);
        if (m_mbIntra != 0)
        {
            m_aAvail = m_cAvail = 0;
            if (i == 2 || i == 3 || !m_firstSliceLine)
                m_aAvail = MbType(m_blockIndex[size_t(i)] - m_blockWrap[size_t(i)]);
            if (i == 1 || i == 3 || m_mbX != 0)
                m_cAvail = MbType(m_blockIndex[size_t(i)] - 1);
            DecodeIntraBlock(m_scratch.data(), i, val, mquant, i < 4 ? m_codingset : m_codingset2);
            InvTrans8x8(m_scratch.data());
            PutPixelsClamped(m_scratch.data(), d.Plane, d.Offset, d.Stride, true);
        }
        else if (val != 0)
        {
            DecodePBlock(m_scratch.data(), i, mquant, ttmb, firstBlock, d.Plane, d.Offset, d.Stride, ignored);
            if (m_ttmbf == 0 && ttmb < 8)
                ttmb = -1;
            firstBlock = false;
        }
    }
}

void Vc1Video::DecodeSkipBlocks()
{
    // vc1_decode_skip_blocks: the last picture again
    const MpegFrame& from = m_last->Frame;
    MpegFrame& to = m_cur->Frame;
    to.Y = from.Y;
    to.Cb = from.Cb;
    to.Cr = from.Cr;
    std::fill(m_cur->Motion[0].begin(), m_cur->Motion[0].end(), int16_t(0));
    std::fill(m_cur->Motion[1].begin(), m_cur->Motion[1].end(), int16_t(0));
}

// ---- Putting the intra blocks ----

void Vc1Video::PutIntraBlocks(bool isSigned)
{
    // The intra blocks of the picture into it (put_pixels_clamped, or put_signed_pixels_clamped with
    // 128 added), after the overlap smoothing
    MpegFrame& f = m_cur->Frame;
    int stride = f.Stride, cstride = f.ChromaStride;
    for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
        for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
        {
            SetBlockIndex();
            for (int i = 0; i < 6; i++)
            {
                if (MbType(m_blockIndex[size_t(i)]) == 0)
                    continue;
                Destination d = Dest(i, stride, cstride);
                PutPixelsClamped(Block(i), d.Plane, d.Offset, d.Stride, isSigned);
            }
        }
}

void Vc1Video::PutPixelsClamped(const int16_t* block, uint8_t* plane, int dst, int linesize, bool isSigned)
{
    int add = isSigned ? 128 : 0;
    for (int y = 0; y < 8; y++, dst += linesize)
        for (int x = 0; x < 8; x++)
            plane[dst + x] = Clip(block[y * 8 + x] + add);
}

void Vc1Video::AddPixelsClampedScalar(const int16_t* block, uint8_t* plane, int dst, int linesize)
{
    for (int y = 0; y < 8; y++, dst += linesize)
        for (int x = 0; x < 8; x++)
            plane[dst + x] = Clip(plane[dst + x] + block[y * 8 + x]);
}

#if defined(__aarch64__)

namespace {

// The 8x8 inverse transform and the adding of a block in NEON, the same numbers as the scalar code:
// a pass works on the eight columns at once in 32 bits, its results cut to 16 bits as int16_t does,
// and the picture's bytes plus the block clamped (a saturated 16-bit sum clamps the same way).

/// The eight results of one pass of the transform (d the rounding, the +1 of the last four in pass 2).
template <int Shift, int Round, int Last>
inline void Pass(const int32x4_t (&s)[8], int32x4_t (&out)[8])
{
    int32x4_t t1 = vaddq_s32(vmulq_n_s32(vaddq_s32(s[0], s[4]), 12), vdupq_n_s32(Round));
    int32x4_t t2 = vaddq_s32(vmulq_n_s32(vsubq_s32(s[0], s[4]), 12), vdupq_n_s32(Round));
    int32x4_t t3 = vmlaq_n_s32(vmulq_n_s32(s[2], 16), s[6], 6);
    int32x4_t t4 = vmlsq_n_s32(vmulq_n_s32(s[2], 6), s[6], 16);
    int32x4_t t5 = vaddq_s32(t1, t3), t6 = vaddq_s32(t2, t4), t7 = vsubq_s32(t2, t4), t8 = vsubq_s32(t1, t3);
    int32x4_t u1 = vmlaq_n_s32(vmlaq_n_s32(vmlaq_n_s32(vmulq_n_s32(s[1], 16), s[3], 15), s[5], 9), s[7], 4);
    int32x4_t u2 = vmlsq_n_s32(vmlsq_n_s32(vmlsq_n_s32(vmulq_n_s32(s[1], 15), s[3], 4), s[5], 16), s[7], 9);
    int32x4_t u3 = vmlaq_n_s32(vmlaq_n_s32(vmlsq_n_s32(vmulq_n_s32(s[1], 9), s[3], 16), s[5], 4), s[7], 15);
    int32x4_t u4 = vmlsq_n_s32(vmlaq_n_s32(vmlsq_n_s32(vmulq_n_s32(s[1], 4), s[3], 9), s[5], 15), s[7], 16);
    int32x4_t last = vdupq_n_s32(Last);
    out[0] = vshrq_n_s32(vaddq_s32(t5, u1), Shift);
    out[1] = vshrq_n_s32(vaddq_s32(t6, u2), Shift);
    out[2] = vshrq_n_s32(vaddq_s32(t7, u3), Shift);
    out[3] = vshrq_n_s32(vaddq_s32(t8, u4), Shift);
    out[4] = vshrq_n_s32(vaddq_s32(vsubq_s32(t8, u4), last), Shift);
    out[5] = vshrq_n_s32(vaddq_s32(vsubq_s32(t7, u3), last), Shift);
    out[6] = vshrq_n_s32(vaddq_s32(vsubq_s32(t6, u2), last), Shift);
    out[7] = vshrq_n_s32(vaddq_s32(vsubq_s32(t5, u1), last), Shift);
}

/// One pass over the eight columns of rows (rows[k]: lanes the columns): out[m] lane i the m-th
/// result of column i, cut to 16 bits.
template <int Shift, int Round, int Last>
inline void Columns(const int16x8_t (&rows)[8], int16x8_t (&out)[8])
{
    int32x4_t lo[8], hi[8], loOut[8], hiOut[8];
    for (int k = 0; k < 8; k++)
    {
        lo[k] = vmovl_s16(vget_low_s16(rows[k]));
        hi[k] = vmovl_s16(vget_high_s16(rows[k]));
    }
    Pass<Shift, Round, Last>(lo, loOut);
    Pass<Shift, Round, Last>(hi, hiOut);
    for (int m = 0; m < 8; m++)
        out[m] = vcombine_s16(vmovn_s32(loOut[m]), vmovn_s32(hiOut[m]));
}

/// The 8 x 8 matrix of rows v transposed.
inline void Transpose(int16x8_t (&v)[8])
{
    int16x8x2_t a0 = vtrnq_s16(v[0], v[1]), a1 = vtrnq_s16(v[2], v[3]), a2 = vtrnq_s16(v[4], v[5]), a3 = vtrnq_s16(v[6], v[7]);
    int32x4x2_t b0 = vtrnq_s32(vreinterpretq_s32_s16(a0.val[0]), vreinterpretq_s32_s16(a1.val[0]));
    int32x4x2_t b1 = vtrnq_s32(vreinterpretq_s32_s16(a0.val[1]), vreinterpretq_s32_s16(a1.val[1]));
    int32x4x2_t b2 = vtrnq_s32(vreinterpretq_s32_s16(a2.val[0]), vreinterpretq_s32_s16(a3.val[0]));
    int32x4x2_t b3 = vtrnq_s32(vreinterpretq_s32_s16(a2.val[1]), vreinterpretq_s32_s16(a3.val[1]));
    auto low = [](int32x4_t x, int32x4_t y) { return vreinterpretq_s16_s32(vcombine_s32(vget_low_s32(x), vget_low_s32(y))); };
    auto high = [](int32x4_t x, int32x4_t y) { return vreinterpretq_s16_s32(vcombine_s32(vget_high_s32(x), vget_high_s32(y))); };
    v[0] = low(b0.val[0], b2.val[0]);
    v[1] = low(b1.val[0], b3.val[0]);
    v[2] = low(b0.val[1], b2.val[1]);
    v[3] = low(b1.val[1], b3.val[1]);
    v[4] = high(b0.val[0], b2.val[0]);
    v[5] = high(b1.val[0], b3.val[0]);
    v[6] = high(b0.val[1], b2.val[1]);
    v[7] = high(b1.val[1], b3.val[1]);
}

} // namespace

void Vc1Video::AddPixelsClamped(const int16_t* block, uint8_t* plane, int dst, int linesize)
{
    for (int y = 0; y < 8; y++, dst += linesize)
    {
        int16x8_t sum = vqaddq_s16(vreinterpretq_s16_u16(vmovl_u8(vld1_u8(plane + dst))), vld1q_s16(block + y * 8));
        vst1_u8(plane + dst, vqmovun_s16(sum));
    }
}

void Vc1Video::InvTrans8x8(int16_t* block)
{
    // pass 1: the columns of the block, its results the rows of temp (out[k] lane i: temp[i][k]);
    // pass 2: the columns of temp, its results the rows of the block
    int16x8_t rows[8], out[8];
    for (int k = 0; k < 8; k++)
        rows[k] = vld1q_s16(block + 8 * k);
    Columns<3, 4, 0>(rows, out);
    Transpose(out);
    Columns<7, 64, 1>(out, rows);
    for (int k = 0; k < 8; k++)
        vst1q_s16(block + 8 * k, rows[k]);
}

#else

void Vc1Video::AddPixelsClamped(const int16_t* block, uint8_t* plane, int dst, int linesize) { AddPixelsClampedScalar(block, plane, dst, linesize); }
void Vc1Video::InvTrans8x8(int16_t* block) { InvTrans8x8Scalar(block); }

#endif

std::string Vc1Video::CheckTransformLanes()
{
#if defined(__aarch64__)
    // Random blocks of each size of coefficients (small, the decoder's, and any 16 bits)
    std::mt19937 random(54321);
    int differed = 0;
    for (int round = 0; round < 6000; round++)
    {
        std::array<int16_t, 64> block{}, scalar{};
        int range = round % 3 == 0 ? 64 : round % 3 == 1 ? 2048 : 65536;
        for (auto& v : block)
            v = int16_t(int(random() % uint32_t(range)) - range / 2);
        scalar = block;
        InvTrans8x8(block.data());
        InvTrans8x8Scalar(scalar.data());
        std::array<uint8_t, 8 * 24> picture{}, expected{};
        for (auto& v : picture)
            v = uint8_t(random());
        expected = picture;
        AddPixelsClamped(block.data(), picture.data(), 3, 24);
        AddPixelsClampedScalar(scalar.data(), expected.data(), 3, 24);
        if (block != scalar || picture != expected)
            differed++;
    }
    return differed == 0 ? std::string() : std::format(" {} transforms differed;", differed);
#else
    return {};
#endif
}

// ---- Inverse transforms (vc1dsp.c) ----

void Vc1Video::InvTrans8x8Scalar(int16_t* block)
{
    int16_t temp[64];
    for (int i = 0; i < 8; i++)
    {
        int s0 = block[i], s8 = block[i + 8], s16 = block[i + 16], s24 = block[i + 24];
        int s32 = block[i + 32], s40 = block[i + 40], s48 = block[i + 48], s56 = block[i + 56];
        int t1 = 12 * (s0 + s32) + 4, t2 = 12 * (s0 - s32) + 4;
        int t3 = 16 * s16 + 6 * s48, t4 = 6 * s16 - 16 * s48;
        int t5 = t1 + t3, t6 = t2 + t4, t7 = t2 - t4, t8 = t1 - t3;
        t1 = 16 * s8 + 15 * s24 + 9 * s40 + 4 * s56;
        t2 = 15 * s8 - 4 * s24 - 16 * s40 - 9 * s56;
        t3 = 9 * s8 - 16 * s24 + 4 * s40 + 15 * s56;
        t4 = 4 * s8 - 9 * s24 + 15 * s40 - 16 * s56;
        int d = i * 8;
        temp[d] = int16_t((t5 + t1) >> 3);
        temp[d + 1] = int16_t((t6 + t2) >> 3);
        temp[d + 2] = int16_t((t7 + t3) >> 3);
        temp[d + 3] = int16_t((t8 + t4) >> 3);
        temp[d + 4] = int16_t((t8 - t4) >> 3);
        temp[d + 5] = int16_t((t7 - t3) >> 3);
        temp[d + 6] = int16_t((t6 - t2) >> 3);
        temp[d + 7] = int16_t((t5 - t1) >> 3);
    }
    for (int i = 0; i < 8; i++)
    {
        int s0 = temp[i], s8 = temp[i + 8], s16 = temp[i + 16], s24 = temp[i + 24];
        int s32 = temp[i + 32], s40 = temp[i + 40], s48 = temp[i + 48], s56 = temp[i + 56];
        int t1 = 12 * (s0 + s32) + 64, t2 = 12 * (s0 - s32) + 64;
        int t3 = 16 * s16 + 6 * s48, t4 = 6 * s16 - 16 * s48;
        int t5 = t1 + t3, t6 = t2 + t4, t7 = t2 - t4, t8 = t1 - t3;
        t1 = 16 * s8 + 15 * s24 + 9 * s40 + 4 * s56;
        t2 = 15 * s8 - 4 * s24 - 16 * s40 - 9 * s56;
        t3 = 9 * s8 - 16 * s24 + 4 * s40 + 15 * s56;
        t4 = 4 * s8 - 9 * s24 + 15 * s40 - 16 * s56;
        block[i] = int16_t((t5 + t1) >> 7);
        block[i + 8] = int16_t((t6 + t2) >> 7);
        block[i + 16] = int16_t((t7 + t3) >> 7);
        block[i + 24] = int16_t((t8 + t4) >> 7);
        block[i + 32] = int16_t((t8 - t4 + 1) >> 7);
        block[i + 40] = int16_t((t7 - t3 + 1) >> 7);
        block[i + 48] = int16_t((t6 - t2 + 1) >> 7);
        block[i + 56] = int16_t((t5 - t1 + 1) >> 7);
    }
}

void Vc1Video::AddDc(uint8_t* plane, int dst, int linesize, int dc, int width, int height)
{
    for (int y = 0; y < height; y++, dst += linesize)
        for (int x = 0; x < width; x++)
            plane[dst + x] = Clip(plane[dst + x] + dc);
}

void Vc1Video::InvTrans8x8Dc(uint8_t* plane, int dst, int linesize, const int16_t* block)
{
    int dc = block[0];
    dc = (3 * dc + 1) >> 1;
    dc = (3 * dc + 16) >> 5;
    AddDc(plane, dst, linesize, dc, 8, 8);
}

void Vc1Video::InvTrans8x4Dc(uint8_t* plane, int dst, int linesize, int dc)
{
    dc = (3 * dc + 1) >> 1;
    dc = (17 * dc + 64) >> 7;
    AddDc(plane, dst, linesize, dc, 8, 4);
}

void Vc1Video::InvTrans4x8Dc(uint8_t* plane, int dst, int linesize, int dc)
{
    dc = (17 * dc + 4) >> 3;
    dc = (12 * dc + 64) >> 7;
    AddDc(plane, dst, linesize, dc, 4, 8);
}

void Vc1Video::InvTrans4x4Dc(uint8_t* plane, int dst, int linesize, int dc)
{
    dc = (17 * dc + 4) >> 3;
    dc = (17 * dc + 64) >> 7;
    AddDc(plane, dst, linesize, dc, 4, 4);
}

void Vc1Video::InvTrans8x4(uint8_t* plane, int dest, int linesize, int16_t* block)
{
    // The 8x4 half of a block (4 rows of 8 coefficients) added to the picture
    for (int i = 0; i < 4; i++)
    {
        int s = i * 8;
        int t1 = 12 * (block[s] + block[s + 4]) + 4, t2 = 12 * (block[s] - block[s + 4]) + 4;
        int t3 = 16 * block[s + 2] + 6 * block[s + 6], t4 = 6 * block[s + 2] - 16 * block[s + 6];
        int t5 = t1 + t3, t6 = t2 + t4, t7 = t2 - t4, t8 = t1 - t3;
        t1 = 16 * block[s + 1] + 15 * block[s + 3] + 9 * block[s + 5] + 4 * block[s + 7];
        t2 = 15 * block[s + 1] - 4 * block[s + 3] - 16 * block[s + 5] - 9 * block[s + 7];
        t3 = 9 * block[s + 1] - 16 * block[s + 3] + 4 * block[s + 5] + 15 * block[s + 7];
        t4 = 4 * block[s + 1] - 9 * block[s + 3] + 15 * block[s + 5] - 16 * block[s + 7];
        block[s] = int16_t((t5 + t1) >> 3);
        block[s + 1] = int16_t((t6 + t2) >> 3);
        block[s + 2] = int16_t((t7 + t3) >> 3);
        block[s + 3] = int16_t((t8 + t4) >> 3);
        block[s + 4] = int16_t((t8 - t4) >> 3);
        block[s + 5] = int16_t((t7 - t3) >> 3);
        block[s + 6] = int16_t((t6 - t2) >> 3);
        block[s + 7] = int16_t((t5 - t1) >> 3);
    }
    for (int i = 0; i < 8; i++)
    {
        int t1 = 17 * (block[i] + block[i + 16]) + 64, t2 = 17 * (block[i] - block[i + 16]) + 64;
        int t3 = 22 * block[i + 8] + 10 * block[i + 24], t4 = 22 * block[i + 24] - 10 * block[i + 8];
        int d = dest + i;
        plane[d] = Clip(plane[d] + ((t1 + t3) >> 7));
        plane[d + linesize] = Clip(plane[d + linesize] + ((t2 - t4) >> 7));
        plane[d + 2 * linesize] = Clip(plane[d + 2 * linesize] + ((t2 + t4) >> 7));
        plane[d + 3 * linesize] = Clip(plane[d + 3 * linesize] + ((t1 - t3) >> 7));
    }
}

void Vc1Video::InvTrans4x8(uint8_t* plane, int dest, int linesize, int16_t* block)
{
    // The 4x8 half of a block (8 rows of 4 coefficients, stride 8) added to the picture
    for (int i = 0; i < 8; i++)
    {
        int s = i * 8;
        int t1 = 17 * (block[s] + block[s + 2]) + 4, t2 = 17 * (block[s] - block[s + 2]) + 4;
        int t3 = 22 * block[s + 1] + 10 * block[s + 3], t4 = 22 * block[s + 3] - 10 * block[s + 1];
        block[s] = int16_t((t1 + t3) >> 3);
        block[s + 1] = int16_t((t2 - t4) >> 3);
        block[s + 2] = int16_t((t2 + t4) >> 3);
        block[s + 3] = int16_t((t1 - t3) >> 3);
    }
    for (int i = 0; i < 4; i++)
    {
        int s0 = block[i], s8 = block[i + 8], s16 = block[i + 16], s24 = block[i + 24];
        int s32 = block[i + 32], s40 = block[i + 40], s48 = block[i + 48], s56 = block[i + 56];
        int t1 = 12 * (s0 + s32) + 64, t2 = 12 * (s0 - s32) + 64;
        int t3 = 16 * s16 + 6 * s48, t4 = 6 * s16 - 16 * s48;
        int t5 = t1 + t3, t6 = t2 + t4, t7 = t2 - t4, t8 = t1 - t3;
        t1 = 16 * s8 + 15 * s24 + 9 * s40 + 4 * s56;
        t2 = 15 * s8 - 4 * s24 - 16 * s40 - 9 * s56;
        t3 = 9 * s8 - 16 * s24 + 4 * s40 + 15 * s56;
        t4 = 4 * s8 - 9 * s24 + 15 * s40 - 16 * s56;
        int d = dest + i;
        plane[d] = Clip(plane[d] + ((t5 + t1) >> 7));
        plane[d + linesize] = Clip(plane[d + linesize] + ((t6 + t2) >> 7));
        plane[d + 2 * linesize] = Clip(plane[d + 2 * linesize] + ((t7 + t3) >> 7));
        plane[d + 3 * linesize] = Clip(plane[d + 3 * linesize] + ((t8 + t4) >> 7));
        plane[d + 4 * linesize] = Clip(plane[d + 4 * linesize] + ((t8 - t4 + 1) >> 7));
        plane[d + 5 * linesize] = Clip(plane[d + 5 * linesize] + ((t7 - t3 + 1) >> 7));
        plane[d + 6 * linesize] = Clip(plane[d + 6 * linesize] + ((t6 - t2 + 1) >> 7));
        plane[d + 7 * linesize] = Clip(plane[d + 7 * linesize] + ((t5 - t1 + 1) >> 7));
    }
}

void Vc1Video::InvTrans4x4(uint8_t* plane, int dest, int linesize, int16_t* block)
{
    // A 4x4 quarter of a block (stride 8) added to the picture
    for (int i = 0; i < 4; i++)
    {
        int s = i * 8;
        int t1 = 17 * (block[s] + block[s + 2]) + 4, t2 = 17 * (block[s] - block[s + 2]) + 4;
        int t3 = 22 * block[s + 1] + 10 * block[s + 3], t4 = 22 * block[s + 3] - 10 * block[s + 1];
        block[s] = int16_t((t1 + t3) >> 3);
        block[s + 1] = int16_t((t2 - t4) >> 3);
        block[s + 2] = int16_t((t2 + t4) >> 3);
        block[s + 3] = int16_t((t1 - t3) >> 3);
    }
    for (int i = 0; i < 4; i++)
    {
        int t1 = 17 * (block[i] + block[i + 16]) + 64, t2 = 17 * (block[i] - block[i + 16]) + 64;
        int t3 = 22 * block[i + 8] + 10 * block[i + 24], t4 = 22 * block[i + 24] - 10 * block[i + 8];
        int d = dest + i;
        plane[d] = Clip(plane[d] + ((t1 + t3) >> 7));
        plane[d + linesize] = Clip(plane[d + linesize] + ((t2 - t4) >> 7));
        plane[d + 2 * linesize] = Clip(plane[d + 2 * linesize] + ((t2 + t4) >> 7));
        plane[d + 3 * linesize] = Clip(plane[d + 3 * linesize] + ((t1 - t3) >> 7));
    }
}

} // namespace openshiina::formats
