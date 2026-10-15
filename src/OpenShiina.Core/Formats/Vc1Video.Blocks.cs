// VC-1 macroblocks and blocks (FFmpeg vc1_block.c): the picture loops, macroblock layers of I,
// P and B pictures, DC / AC prediction, the coefficients and the inverse transforms (vc1dsp.c).

namespace OpenShiina.Formats;

public sealed partial class Vc1Video
{
    // The macroblock being decoded
    private int m_mbX, m_mbY;
    private bool m_firstSliceLine;
    private readonly int[] m_blockIndex = new int[6];
    private int[] m_blockWrap = null!;
    private int m_mbIntra, m_acPred;
    private int m_codingset, m_codingset2;
    private int m_aAvail, m_cAvail;
    private int m_yDcScale, m_cDcScale;
    private int m_esc3LevelLength, m_esc3RunLength;
    private int m_mbHasCoeffs;
    /// <summary>Offsets of the macroblock in the current picture's luma and chroma planes.</summary>
    private int m_destY, m_destC;
    private readonly short[] m_scratch = new short[64];

    private const int AcEscapeIndexRows = 185;

    private int MbPos => m_mbX + m_mbY * m_mbStride;

    /// <summary>The 64 coefficients of block n of the current macroblock in <see cref="m_blocks"/>.</summary>
    private Span<short> Block(int n) => m_blocks.AsSpan((MbPos * 6 + n) * 64, 64);

    /// <summary>init_block_index + update_block_index for the current macroblock.</summary>
    private void SetBlockIndex()
    {
        int b8s = m_b8Stride, mbs = m_mbStride, x = m_mbX, y = m_mbY;
        m_blockIndex[0] = b8s * (y * 2) + x * 2;
        m_blockIndex[1] = m_blockIndex[0] + 1;
        m_blockIndex[2] = b8s * (y * 2 + 1) + x * 2;
        m_blockIndex[3] = m_blockIndex[2] + 1;
        m_blockIndex[4] = mbs * (y + 1) + b8s * m_mbHeight * 2 + x;
        m_blockIndex[5] = mbs * (y + m_mbHeight + 2) + b8s * m_mbHeight * 2 + x;
        m_destY = y * 16 * m_cur.Frame.Stride + x * 16;
        m_destC = y * 8 * m_cur.Frame.ChromaStride + x * 8;
    }

    private ref short DcVal(int index) => ref m_dcVal[m_blockBase + index];

    private ref byte MbType(int index) => ref m_mbType[m_blockBase + index];

    private ref short Motion(Picture p, int dir, int xy, int c) => ref p.Motion[dir][MotionOffset + 2 * xy + c];

    /// <summary>ff_vc1_decode_blocks.</summary>
    private void DecodeBlocks()
    {
        m_blockWrap ??= [m_b8Stride, m_b8Stride, m_b8Stride, m_b8Stride, m_mbStride, m_mbStride];
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

    private void SelectCodingSets(int yIndex)
    {
        m_codingset = yIndex switch
        {
            0 => m_pqindex <= 8 ? 6 : 2, // high rate / low motion intra
            1 => 0,                      // high motion intra
            _ => 4,                      // mid rate intra
        };
        m_codingset2 = m_cAcTableIndex switch
        {
            0 => m_pqindex <= 8 ? 7 : 3,
            1 => 1,
            _ => 5,
        };
    }

    private void CheckBits()
    {
        if (BitsLeft < 0)
            throw new InvalidDataException("VC-1 picture overread");
    }

    #region I pictures

    /// <summary>vc1_decode_i_blocks / vc1_decode_i_blocks_adv, then the overlap, the putting and the loop filter.</summary>
    private void DecodeIBlocks()
    {
        bool advanced = m_profile == ProfileAdvanced;
        SelectCodingSets(m_yAcTableIndex);
        m_yDcScale = m_cDcScale = Vc1Tables.DcScale[m_pq];
        m_mbIntra = 1;
        m_firstSliceLine = true;
        if (advanced && BitsLeft <= 1)
            throw new InvalidDataException("Empty I picture");
        var cur = m_cur;
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
        {
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                int mbPos = MbPos;
                m_blocks.AsSpan(mbPos * 6 * 64, 6 * 64).Clear();
                for (int i = 0; i < 4; i++)
                {
                    Motion(cur, 1, m_blockIndex[i], 0) = 0;
                    Motion(cur, 1, m_blockIndex[i], 1) = 0;
                }
                if (advanced && BitsLeft <= 1)
                    throw new InvalidDataException("I picture cut short");
                int cbp = GetVlc(s_mbIntraVlc);
                int mquant = m_pq;
                if (advanced)
                {
                    m_acPred = m_acpredIsRaw ? GetBit() : m_acpredPlane[mbPos];
                    if (m_condover == CondoverSelect && m_overflgIsRaw)
                        m_overFlagsPlane[mbPos] = (byte)GetBit();
                    GetMquant(ref mquant);
                    m_qscale[mbPos] = mquant;
                    m_yDcScale = m_cDcScale = Vc1Tables.DcScale[Math.Abs(mquant)];
                }
                else
                {
                    m_acPred = GetBit();
                    m_qscale[mbPos] = m_pq;
                }
                for (int k = 0; k < 6; k++)
                {
                    MbType(m_blockIndex[k]) = 1;
                    int val = (cbp >> (5 - k)) & 1;
                    if (k < 4)
                    {
                        int pred = CodedBlockPred(k, out int coded);
                        val ^= pred;
                        m_codedBlock[coded] = (byte)val;
                    }
                    cbp |= val << (5 - k);
                    var block = Block(k);
                    if (advanced)
                    {
                        m_aAvail = !m_firstSliceLine || k is 2 or 3 ? 1 : 0;
                        m_cAvail = m_mbX != 0 || k is 1 or 3 ? 1 : 0;
                        DecodeIBlockAdvanced(block, k, val, k < 4 ? m_codingset : m_codingset2, mquant);
                    }
                    else
                        DecodeIBlock(block, k, val, k < 4 ? m_codingset : m_codingset2);
                    InvTrans8x8(block);
                }
                CheckBits();
                m_cbp[mbPos] = 0xFFFFFF;
                m_isIntra[mbPos] = 0x3F;
                m_ttblk[mbPos] = 0;
            }
            m_firstSliceLine = false;
        }
        bool overlap = m_overlap != 0 && (m_pq >= 9 || advanced && m_condover != CondoverNone);
        if (overlap)
            OverlapIntraPicture();
        PutIntraBlocks(signed: overlap || advanced);
        if (m_loopFilter)
            LoopFilterIntra();
    }

    /// <summary>vc1_coded_block_pred: the coded-block predictor of luma block n, and where to store the flag.</summary>
    private int CodedBlockPred(int n, out int at)
    {
        int xy = m_blockBase + m_blockIndex[n], wrap = m_b8Stride;
        int a = m_codedBlock[xy - 1], b = m_codedBlock[xy - 1 - wrap], c = m_codedBlock[xy - wrap];
        at = xy;
        return b == c ? a : c;
    }

    private static readonly ushort[] s_dcPred =
    [
        0xFFFF, 1024, 512, 341, 256, 205, 171, 146, 128, 114, 102, 93, 85, 79, 73, 68, 64,
        60, 57, 54, 51, 49, 47, 45, 43, 41, 39, 38, 37, 35, 34, 33,
    ];

    /// <summary>vc1_i_pred_dc (Simple / Main I pictures): the predicted DC and its direction (1 left, 0 top).</summary>
    private int IPredDc(int n, out int dir)
    {
        int scale = n < 4 ? m_yDcScale : m_cDcScale;
        int wrap = m_blockWrap[n], xy = m_blockBase + m_blockIndex[n];
        int c = m_dcVal[xy - 1], b = m_dcVal[xy - 1 - wrap], a = m_dcVal[xy - wrap];
        int outer = m_pq < 9 || m_overlap == 0 ? (short)s_dcPred[scale] : 0;
        if (m_firstSliceLine && n != 2 && n != 3)
            b = a = outer;
        if (m_mbX == 0 && n != 1 && n != 3)
            b = c = outer;
        if (Math.Abs(a - b) <= Math.Abs(b - c))
        {
            dir = 1;
            return c;
        }
        dir = 0;
        return a;
    }

    /// <summary>ff_vc1_pred_dc (scaled by the neighbours' quantizers).</summary>
    private int PredDc(int n, int aAvail, int cAvail, out int dir)
    {
        int mbPos = MbPos;
        int q1 = Math.Abs(m_qscale[mbPos]);
        int dqscaleIndex = Vc1Tables.DcScale[q1] - 1;
        dir = 1;
        if (dqscaleIndex < 0)
            return 0;
        int wrap = m_blockWrap[n], xy = m_blockBase + m_blockIndex[n];
        int c = m_dcVal[xy - 1], b = m_dcVal[xy - 1 - wrap], a = m_dcVal[xy - wrap];
        int q2;
        if (cAvail != 0 && n != 1 && n != 3)
        {
            q2 = Math.Abs(m_qscale[mbPos - 1]);
            if (q2 != 0 && q2 != q1)
                c = (int)((uint)c * Vc1Tables.DcScale[q2] * (uint)Vc1Tables.Dqscale[dqscaleIndex] + 0x20000) >> 18;
        }
        if (aAvail != 0 && n != 2 && n != 3)
        {
            q2 = Math.Abs(m_qscale[mbPos - m_mbStride]);
            if (q2 != 0 && q2 != q1)
                a = (int)((uint)a * Vc1Tables.DcScale[q2] * (uint)Vc1Tables.Dqscale[dqscaleIndex] + 0x20000) >> 18;
        }
        if (aAvail != 0 && cAvail != 0 && n != 3)
        {
            int off = mbPos;
            if (n != 1)
                off--;
            if (n != 2)
                off -= m_mbStride;
            q2 = Math.Abs(m_qscale[off]);
            if (q2 != 0 && q2 != q1)
                b = (int)((uint)b * Vc1Tables.DcScale[q2] * (uint)Vc1Tables.Dqscale[dqscaleIndex] + 0x20000) >> 18;
        }
        if (cAvail != 0 && (aAvail == 0 || Math.Abs(a - b) <= Math.Abs(b - c)))
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

    /// <summary>The DC differential of an intra block.</summary>
    private int ReadDcDiff(int n, int quant)
    {
        int dcdiff = GetVlc(s_dcVlc[m_dcTableIndex][n >= 4 ? 1 : 0]);
        if (dcdiff < 0)
            throw new InvalidDataException("Bad DC code");
        if (dcdiff != 0)
        {
            int m = quant is 1 or 2 ? 3 - quant : 0;
            if (dcdiff == 119)
                dcdiff = GetBits(8 + m);
            else if (m != 0)
                dcdiff = (dcdiff << m) + GetBits(m) - ((1 << m) - 1);
            if (GetBit() != 0)
                dcdiff = -dcdiff;
        }
        return dcdiff;
    }

    /// <summary>vc1_decode_ac_coeff: one run / level / last of codingset's table.</summary>
    private void DecodeAcCoeff(out bool last, out int skip, out int value, int codingset)
    {
        int index = GetVlc(s_acVlc[codingset]);
        if (index < 0)
            throw new InvalidDataException("Bad AC code");
        int run, level, sign;
        bool lst;
        int size = Vc1Tables.AcSizes[codingset];
        if (index != size - 1)
        {
            run = Vc1Tables.IndexDecode[(codingset * AcEscapeIndexRows + index) * 2];
            level = Vc1Tables.IndexDecode[(codingset * AcEscapeIndexRows + index) * 2 + 1];
            lst = index >= Vc1Tables.LastDecode[codingset] || BitsLeft < 0;
            sign = GetBit();
        }
        else
        {
            int escape = Decode210();
            if (escape != 2)
            {
                index = GetVlc(s_acVlc[codingset]);
                if ((uint)index >= (uint)(size - 1))
                    throw new InvalidDataException("Bad AC escape");
                run = Vc1Tables.IndexDecode[(codingset * AcEscapeIndexRows + index) * 2];
                level = Vc1Tables.IndexDecode[(codingset * AcEscapeIndexRows + index) * 2 + 1];
                lst = index >= Vc1Tables.LastDecode[codingset];
                if (escape == 0)
                    level += lst ? Vc1Tables.LastDeltaLevel[codingset * 44 + run] : Vc1Tables.DeltaLevel[codingset * 31 + run];
                else
                    run += (lst ? Vc1Tables.LastDeltaRun[codingset * 10 + level] : Vc1Tables.DeltaRun[codingset * 57 + level]) + 1;
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

    /// <summary>vc1_decode_i_block: an intra block of a Simple / Main I picture.</summary>
    private void DecodeIBlock(Span<short> block, int n, int coded, int codingset)
    {
        int dcdiff = ReadDcDiff(n, m_pq);
        dcdiff += IPredDc(n, out int dcPredDir);
        int xy = m_blockBase + m_blockIndex[n];
        m_dcVal[xy] = (short)dcdiff;
        block[0] = (short)(dcdiff * (n < 4 ? m_yDcScale : m_cDcScale));

        int acVal = xy * 16, acVal2 = acVal;
        acVal -= dcPredDir != 0 ? 16 : 16 * m_blockWrap[n];
        int scale = m_pq * 2 + m_halfpq;
        int i = coded != 0 ? 1 : 0;
        if (coded != 0)
        {
            var zz = m_acPred != 0 ? (dcPredDir == 0 ? m_zz8x8[2] : m_zz8x8[3]) : m_zz8x8[1];
            bool last = false;
            while (!last)
            {
                DecodeAcCoeff(out last, out int skip, out int value, codingset);
                i += skip;
                if (i > 63)
                    break;
                block[zz[i++]] = (short)value;
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
                    block[k << sh] += m_acVal[acVal + k];
            }
            for (int k = 1; k < 8; k++)
            {
                m_acVal[acVal2 + k] = block[k << LeftBlockShift];
                m_acVal[acVal2 + k + 8] = block[k << TopBlockShift];
            }
            for (int k = 1; k < 64; k++)
                if (block[k] != 0)
                {
                    int v = block[k] * scale;
                    if (m_pquantizer == 0)
                        v += v < 0 ? -m_pq : m_pq;
                    block[k] = (short)v;
                }
        }
        else
        {
            m_acVal.AsSpan(acVal2, 16).Clear();
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
                m_acVal.AsSpan(acVal, 8).CopyTo(m_acVal.AsSpan(acVal2, 8));
                for (int k = 1; k < 8; k++)
                {
                    int v = m_acVal[acVal + k] * scale;
                    if (m_pquantizer == 0 && v != 0)
                        v += v < 0 ? -m_pq : m_pq;
                    block[k << sh] = (short)v;
                }
            }
        }
    }

    /// <summary>vc1_decode_i_block_adv: an intra block of an Advanced profile I picture.</summary>
    private void DecodeIBlockAdvanced(Span<short> block, int n, int coded, int codingset, int mquant)
    {
        int aAvail = m_aAvail, cAvail = m_cAvail;
        int usePred = m_acPred;
        int mbPos = MbPos;
        int quant = Math.Abs(mquant);
        int dcdiff = ReadDcDiff(n, quant);
        dcdiff += PredDc(n, aAvail, cAvail, out int dcPredDir);
        int xy = m_blockBase + m_blockIndex[n];
        m_dcVal[xy] = (short)dcdiff;
        block[0] = (short)(dcdiff * (n < 4 ? m_yDcScale : m_cDcScale));
        if (aAvail == 0 && cAvail == 0)
            usePred = 0;
        int scale = quant * 2 + (mquant < 0 ? 0 : m_halfpq);
        int acVal = xy * 16, acVal2 = acVal;
        acVal -= dcPredDir != 0 ? 16 : 16 * m_blockWrap[n];

        int q1 = m_qscale[mbPos], q2 = 0;
        if (n == 3)
            q2 = q1;
        else if (dcPredDir != 0)
        {
            if (n == 1)
                q2 = q1;
            else if (cAvail != 0 && mbPos != 0)
                q2 = m_qscale[mbPos - 1];
        }
        else
        {
            if (n == 2)
                q2 = q1;
            else if (aAvail != 0 && mbPos >= m_mbStride)
                q2 = m_qscale[mbPos - m_mbStride];
        }

        int i = 1;
        if (coded != 0)
        {
            var zz = m_acPred != 0 ? (dcPredDir == 0 ? m_zz8x8[2] : m_zz8x8[3]) : m_zz8x8[1];
            bool last = false;
            while (!last)
            {
                DecodeAcCoeff(out last, out int skip, out int value, codingset);
                i += skip;
                if (i > 63)
                    break;
                block[zz[i++]] = (short)value;
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
                q1 = Math.Abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
                if (q1 < 1)
                    throw new InvalidDataException("Bad quantizer");
                if (q2 != 0)
                    q2 = Math.Abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
                if (q2 != 0 && q1 != q2)
                {
                    for (int k = 1; k < 8; k++)
                        block[k << sh] += (short)((int)((uint)m_acVal[acVal + k] * (uint)q2 * (uint)Vc1Tables.Dqscale[q1 - 1] + 0x20000) >> 18);
                }
                else
                    for (int k = 1; k < 8; k++)
                        block[k << sh] += m_acVal[acVal + k];
            }
            for (int k = 1; k < 8; k++)
            {
                m_acVal[acVal2 + k] = block[k << LeftBlockShift];
                m_acVal[acVal2 + k + 8] = block[k << TopBlockShift];
            }
            for (int k = 1; k < 64; k++)
                if (block[k] != 0)
                {
                    int v = block[k] * scale;
                    if (m_pquantizer == 0)
                        v += v < 0 ? -quant : quant;
                    block[k] = (short)v;
                }
        }
        else
        {
            m_acVal.AsSpan(acVal2, 16).Clear();
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
                m_acVal.AsSpan(acVal, 8).CopyTo(m_acVal.AsSpan(acVal2, 8));
                q1 = Math.Abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
                if (q1 < 1)
                    throw new InvalidDataException("Bad quantizer");
                if (q2 != 0)
                    q2 = Math.Abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
                if (q2 != 0 && q1 != q2)
                    for (int k = 1; k < 8; k++)
                        m_acVal[acVal2 + k] = (short)((int)((uint)(m_acVal[acVal2 + k] * q2) * (uint)Vc1Tables.Dqscale[q1 - 1] + 0x20000) >> 18);
                for (int k = 1; k < 8; k++)
                {
                    int v = m_acVal[acVal2 + k] * scale;
                    if (m_pquantizer == 0 && v != 0)
                        v += v < 0 ? -quant : quant;
                    block[k << sh] = (short)v;
                }
            }
        }
    }

    /// <summary>vc1_decode_intra_block: an intra block in a P or B picture.</summary>
    private void DecodeIntraBlock(Span<short> block, int n, int coded, int mquant, int codingset)
    {
        int mbPos = MbPos;
        int aAvail = m_aAvail, cAvail = m_cAvail;
        int usePred = m_acPred;
        block.Clear();
        int quant = Math.Abs(mquant) & 31;
        m_yDcScale = m_cDcScale = Vc1Tables.DcScale[quant];
        int dcdiff = ReadDcDiff(n, quant);
        dcdiff += PredDc(n, aAvail, cAvail, out int dcPredDir);
        int xy = m_blockBase + m_blockIndex[n];
        m_dcVal[xy] = (short)dcdiff;
        block[0] = (short)(dcdiff * (n < 4 ? m_yDcScale : m_cDcScale));

        int i = 1;
        if (aAvail == 0)
            dcPredDir = 1;
        if (cAvail == 0)
            dcPredDir = 0;
        if (aAvail == 0 && cAvail == 0)
            usePred = 0;
        int acVal = xy * 16, acVal2 = acVal;
        int scale = quant * 2 + (mquant < 0 ? 0 : m_halfpq);
        acVal -= dcPredDir != 0 ? 16 : 16 * m_blockWrap[n];

        int q1 = m_qscale[mbPos], q2 = 0;
        if (dcPredDir != 0 && cAvail != 0 && mbPos != 0)
            q2 = m_qscale[mbPos - 1];
        if (dcPredDir == 0 && aAvail != 0 && mbPos >= m_mbStride)
            q2 = m_qscale[mbPos - m_mbStride];
        if (dcPredDir != 0 && n == 1)
            q2 = q1;
        if (dcPredDir == 0 && n == 2)
            q2 = q1;
        if (n == 3)
            q2 = q1;

        if (coded != 0)
        {
            var zz = m_zz8x8[0];
            bool last = false;
            while (!last)
            {
                DecodeAcCoeff(out last, out int skip, out int value, codingset);
                i += skip;
                if (i > 63)
                    break;
                block[zz[i++]] = (short)value;
            }
            if (usePred != 0)
            {
                q1 = Math.Abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
                if (q1 < 1)
                    throw new InvalidDataException("Bad quantizer");
                if (q2 != 0)
                    q2 = Math.Abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
                if (q2 != 0 && q1 != q2)
                {
                    if (dcPredDir != 0)
                        for (int k = 1; k < 8; k++)
                            block[k << LeftBlockShift] += (short)((int)((uint)(m_acVal[acVal + k] * q2) * (uint)Vc1Tables.Dqscale[q1 - 1] + 0x20000) >> 18);
                    else
                        for (int k = 1; k < 8; k++)
                            block[k << TopBlockShift] += (short)((int)((uint)(m_acVal[acVal + k + 8] * q2) * (uint)Vc1Tables.Dqscale[q1 - 1] + 0x20000) >> 18);
                }
                else
                {
                    if (dcPredDir != 0)
                        for (int k = 1; k < 8; k++)
                            block[k << LeftBlockShift] += m_acVal[acVal + k];
                    else
                        for (int k = 1; k < 8; k++)
                            block[k << TopBlockShift] += m_acVal[acVal + k + 8];
                }
            }
            for (int k = 1; k < 8; k++)
            {
                m_acVal[acVal2 + k] = block[k << LeftBlockShift];
                m_acVal[acVal2 + k + 8] = block[k << TopBlockShift];
            }
            for (int k = 1; k < 64; k++)
                if (block[k] != 0)
                {
                    int v = block[k] * scale;
                    if (m_pquantizer == 0)
                        v += v < 0 ? -quant : quant;
                    block[k] = (short)v;
                }
        }
        else
        {
            m_acVal.AsSpan(acVal2, 16).Clear();
            if (usePred != 0)
            {
                q1 = Math.Abs(q1) * 2 + (q1 < 0 ? 0 : m_halfpq) - 1;
                if (q1 < 1)
                    throw new InvalidDataException("Bad quantizer");
                if (q2 != 0)
                    q2 = Math.Abs(q2) * 2 + (q2 < 0 ? 0 : m_halfpq) - 1;
                int part = dcPredDir != 0 ? 0 : 8;
                m_acVal.AsSpan(acVal + part, 8).CopyTo(m_acVal.AsSpan(acVal2 + part, 8));
                if (q2 != 0 && q1 != q2)
                    for (int k = 1; k < 8; k++)
                        m_acVal[acVal2 + part + k] = (short)((int)((uint)m_acVal[acVal2 + part + k] * (uint)q2 * (uint)Vc1Tables.Dqscale[q1 - 1] + 0x20000) >> 18);
                int sh = dcPredDir != 0 ? LeftBlockShift : TopBlockShift;
                for (int k = 1; k < 8; k++)
                {
                    int v = m_acVal[acVal2 + part + k] * scale;
                    if (m_pquantizer == 0 && v != 0)
                        v += v < 0 ? -quant : quant;
                    block[k << sh] = (short)v;
                }
            }
        }
    }

    /// <summary>
    /// vc1_decode_p_block: an inter block's coefficients added to the prediction at
    /// <paramref name="dst"/>; returns the pattern of coded 4x4 parts (bit 3 top left .. bit 0
    /// bottom right) and adds the block's transform type to <paramref name="ttmbOut"/>.
    /// </summary>
    private int DecodePBlock(Span<short> block, int n, int mquant, int ttmb, bool firstBlock,
        byte[] plane, int dst, int linesize, ref int ttmbOut)
    {
        int subblkpat = 0;
        int ttblk = ttmb & 7;
        int pat = 0;
        int quant = Math.Abs(mquant);
        block.Clear();
        if (ttmb == -1)
            ttblk = Vc1Tables.TtblkToTt[m_ttIndex * 8 + GetVlc(s_ttblkVlc[m_ttIndex])];
        if (ttblk == Tt4x4)
            subblkpat = ~(GetVlc(s_subblkpatVlc[m_ttIndex]) + 1);
        if (ttblk != Tt8x8 && ttblk != Tt4x4
            && (m_ttmbf != 0 || (ttmb != -1 && (ttmb & 8) != 0 && !firstBlock) || (m_resRtmFlag == 0 && !firstBlock)))
        {
            subblkpat = Decode012();
            if (subblkpat != 0)
                subblkpat ^= 3;
            if (ttblk is Tt8x4Top or Tt8x4Bottom)
                ttblk = Tt8x4;
            if (ttblk is Tt4x8Right or Tt4x8Left)
                ttblk = Tt4x8;
        }
        int scale = quant * 2 + (mquant < 0 ? 0 : m_halfpq);
        if (ttblk is Tt8x4Top or Tt8x4Bottom)
        {
            subblkpat = 2 - (ttblk == Tt8x4Top ? 1 : 0);
            ttblk = Tt8x4;
        }
        if (ttblk is Tt4x8Right or Tt4x8Left)
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
                    DecodeAcCoeff(out last, out int skip, out int value, m_codingset2);
                    i += skip;
                    if (i > 63)
                        break;
                    int idx = m_zz8x8[0][i++];
                    block[idx] = (short)Dequant(value, scale, quant);
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
                        DecodeAcCoeff(out last, out int skip, out int value, m_codingset2);
                        i += skip;
                        if (i > 15)
                            break;
                        int idx = Vc1Tables.SimpleProgressive4x4[i++];
                        block[idx + off] = (short)Dequant(value, scale, quant);
                    }
                    if ((subblkpat & (1 << (3 - j))) == 0)
                    {
                        int at = dst + (j & 1) * 4 + (j & 2) * 2 * linesize;
                        if (i == 1)
                            InvTrans4x4Dc(plane, at, linesize, block[off]);
                        else
                            InvTrans4x4(plane, at, linesize, block[off..]);
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
                        DecodeAcCoeff(out last, out int skip, out int value, m_codingset2);
                        i += skip;
                        if (i > 31)
                            break;
                        int idx = m_zz8x4[i++] + off;
                        block[idx] = (short)Dequant(value, scale, quant);
                    }
                    if ((subblkpat & (1 << (1 - j))) == 0)
                    {
                        int at = dst + j * 4 * linesize;
                        if (i == 1)
                            InvTrans8x4Dc(plane, at, linesize, block[off]);
                        else
                            InvTrans8x4(plane, at, linesize, block[off..]);
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
                        DecodeAcCoeff(out last, out int skip, out int value, m_codingset2);
                        i += skip;
                        if (i > 31)
                            break;
                        int idx = m_zz4x8[i++] + off;
                        block[idx] = (short)Dequant(value, scale, quant);
                    }
                    if ((subblkpat & (1 << (1 - j))) == 0)
                    {
                        int at = dst + j * 4;
                        if (i == 1)
                            InvTrans4x8Dc(plane, at, linesize, block[off]);
                        else
                            InvTrans4x8(plane, at, linesize, block[off..]);
                    }
                }
                break;
        }
        ttmbOut |= ttblk << (n * 4);
        return pat;
    }

    private int Dequant(int value, int scale, int quant)
    {
        int v = value * scale;
        if (m_pquantizer == 0)
            v += v < 0 ? -quant : quant;
        return v;
    }

    /// <summary>GET_MQUANT: the macroblock quantizer (negative: no half step).</summary>
    private void GetMquant(ref int mquant)
    {
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

    private static readonly byte[] s_sizeTable = [0, 2, 3, 4, 5, 8];
    private static readonly short[] s_offsetTable = [0, 1, 3, 7, 15, 31, 63, 127, 255];

    /// <summary>GET_MVDATA: a motion vector differential; sets m_mbHasCoeffs and m_mbIntra.</summary>
    private void GetMvData(out int dmvX, out int dmvY)
    {
        int index = 1 + GetVlc(s_mvDiffVlc[m_mvTableIndex]);
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
            dmvX = s_offsetTable[index1];
            int val = s_sizeTable[index1] - (m_quarterSample == 0 && index1 == 5 ? 1 : 0);
            if (val > 0)
            {
                val = GetBits(val);
                int sign = -(val & 1);
                dmvX = (sign ^ ((val >> 1) + dmvX)) - sign;
            }
            index1 = index / 6;
            dmvY = s_offsetTable[index1];
            val = s_sizeTable[index1] - (m_quarterSample == 0 && index1 == 5 ? 1 : 0);
            if (val > 0)
            {
                val = GetBits(val);
                int sign = -(val & 1);
                dmvY = (sign ^ ((val >> 1) + dmvY)) - sign;
            }
        }
    }

    #endregion

    #region P pictures

    /// <summary>vc1_decode_p_blocks, then the overlap of intra blocks, their putting and the loop filter.</summary>
    private void DecodePBlocks()
    {
        SelectCodingSets(m_cAcTableIndex);
        m_firstSliceLine = true;
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
        {
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                if ((m_mvTypeIsRaw || m_skipIsRaw) && BitsLeft <= 1)
                    throw new InvalidDataException("P picture cut short");
                DecodePMb();
                CheckBits();
            }
            m_firstSliceLine = false;
        }
        bool overlap = m_overlap != 0 && m_pq >= 9;
        if (overlap)
            OverlapInterPicture();
        PutIntraBlocks(signed: true);
        if (m_loopFilter)
            LoopFilterInter();
    }

    /// <summary>vc1_decode_p_mb.</summary>
    private void DecodePMb()
    {
        int mbPos = MbPos;
        int ttmb = m_ttfrm;
        bool firstBlock = true;
        int blockCbp = 0, blockTt = 0, blockIntra = 0;
        int mquant = m_pq;
        var cur = m_cur;
        int fourmv = m_mvTypeIsRaw ? GetBit() : m_mvTypePlane[mbPos];
        int skipped = m_skipIsRaw ? GetBit() : m_skipPlane[mbPos];
        int stride = cur.Frame.Stride, cstride = cur.Frame.ChromaStride;

        if (fourmv == 0)
        {
            if (skipped == 0)
            {
                GetMvData(out int dmvX, out int dmvY);
                if (m_mbIntra != 0)
                {
                    Motion(cur, 1, m_blockIndex[0], 0) = 0;
                    Motion(cur, 1, m_blockIndex[0], 1) = 0;
                }
                PredMv(0, dmvX, dmvY, true, m_rangeX, m_rangeY, 0);
                int cbp;
                if (m_mbIntra != 0 && m_mbHasCoeffs == 0)
                {
                    GetMquant(ref mquant);
                    m_acPred = GetBit();
                    cbp = 0;
                }
                else if (m_mbHasCoeffs != 0)
                {
                    if (m_mbIntra != 0)
                        m_acPred = GetBit();
                    cbp = GetVlc(s_cbpcyPVlc[m_cbptab]);
                    GetMquant(ref mquant);
                }
                else
                {
                    mquant = m_pq;
                    cbp = 0;
                }
                m_qscale[mbPos] = mquant;
                if (m_ttmbf == 0 && m_mbIntra == 0 && m_mbHasCoeffs != 0)
                    ttmb = GetVlc(s_ttmbVlc[m_ttIndex]);
                if (m_mbIntra == 0)
                    Mc1Mv(0);
                for (int i = 0; i < 6; i++)
                {
                    DcVal(m_blockIndex[i]) = 0;
                    int val = (cbp >> (5 - i)) & 1;
                    MbType(m_blockIndex[i]) = (byte)m_mbIntra;
                    if (m_mbIntra != 0)
                    {
                        m_aAvail = m_cAvail = 0;
                        if (i is 2 or 3 || !m_firstSliceLine)
                            m_aAvail = MbType(m_blockIndex[i] - m_blockWrap[i]);
                        if (i is 1 or 3 || m_mbX != 0)
                            m_cAvail = MbType(m_blockIndex[i] - 1);
                        var block = Block(i);
                        DecodeIntraBlock(block, i, val, mquant, i < 4 ? m_codingset : m_codingset2);
                        InvTrans8x8(block);
                        blockCbp |= 0xF << (i << 2);
                        blockIntra |= 1 << i;
                    }
                    else if (val != 0)
                    {
                        var (plane, dst, linesize) = Dest(i, stride, cstride);
                        int pat = DecodePBlock(m_scratch, i, mquant, ttmb, firstBlock, plane, dst, linesize, ref blockTt);
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
                    MbType(m_blockIndex[i]) = 0;
                    DcVal(m_blockIndex[i]) = 0;
                }
                m_qscale[mbPos] = 0;
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
                Span<int> isIntra = stackalloc int[6];
                Span<int> isCoded = stackalloc int[6];
                int cbp = GetVlc(s_cbpcyPVlc[m_cbptab]);
                for (int i = 0; i < 6; i++)
                {
                    int val = (cbp >> (5 - i)) & 1;
                    DcVal(m_blockIndex[i]) = 0;
                    m_mbIntra = 0;
                    if (i < 4)
                    {
                        int dmvX = 0, dmvY = 0;
                        m_mbHasCoeffs = 0;
                        if (val != 0)
                            GetMvData(out dmvX, out dmvY);
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
                    MbType(m_blockIndex[i]) = (byte)isIntra[i];
                    if (!codedInter)
                        codedInter = isIntra[i] == 0 && isCoded[i] != 0;
                }
                if (intraCount != 0 || codedInter)
                {
                    GetMquant(ref mquant);
                    m_qscale[mbPos] = mquant;
                    bool intrapred = false;
                    for (int i = 0; i < 6 && !intrapred; i++)
                        if (isIntra[i] != 0)
                        {
                            if (((!m_firstSliceLine || i is 2 or 3) && MbType(m_blockIndex[i] - m_blockWrap[i]) != 0)
                                || ((m_mbX != 0 || i is 1 or 3) && MbType(m_blockIndex[i] - 1) != 0))
                                intrapred = true;
                        }
                    m_acPred = intrapred ? GetBit() : 0;
                    if (m_ttmbf == 0 && codedInter)
                        ttmb = GetVlc(s_ttmbVlc[m_ttIndex]);
                    for (int i = 0; i < 6; i++)
                    {
                        m_mbIntra = isIntra[i];
                        if (isIntra[i] != 0)
                        {
                            m_aAvail = m_cAvail = 0;
                            if (i is 2 or 3 || !m_firstSliceLine)
                                m_aAvail = MbType(m_blockIndex[i] - m_blockWrap[i]);
                            if (i is 1 or 3 || m_mbX != 0)
                                m_cAvail = MbType(m_blockIndex[i] - 1);
                            var block = Block(i);
                            DecodeIntraBlock(block, i, isCoded[i], mquant, i < 4 ? m_codingset : m_codingset2);
                            InvTrans8x8(block);
                            blockCbp |= 0xF << (i << 2);
                            blockIntra |= 1 << i;
                        }
                        else if (isCoded[i] != 0)
                        {
                            var (plane, dst, linesize) = Dest(i, stride, cstride);
                            int pat = DecodePBlock(m_scratch, i, mquant, ttmb, firstBlock, plane, dst, linesize, ref blockTt);
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
                m_qscale[mbPos] = 0;
                for (int i = 0; i < 6; i++)
                {
                    MbType(m_blockIndex[i]) = 0;
                    DcVal(m_blockIndex[i]) = 0;
                }
                for (int i = 0; i < 4; i++)
                {
                    PredMv(i, 0, 0, false, m_rangeX, m_rangeY, 0);
                    Mc4MvLuma(i, 0, false);
                }
                Mc4MvChroma(0);
                m_qscale[mbPos] = 0;
            }
        }
        m_cbp[mbPos] = (uint)blockCbp;
        m_ttblk[mbPos] = blockTt;
        m_isIntra[mbPos] = (byte)blockIntra;
    }

    /// <summary>Where block n of the current macroblock is in the picture: plane, offset, stride.</summary>
    private (byte[] Plane, int Offset, int Stride) Dest(int n, int stride, int cstride)
    {
        var f = m_cur.Frame;
        return n switch
        {
            < 4 => (f.Y, m_destY + (n & 1) * 8 + (n & 2) * 4 * stride, stride),
            4 => (f.Cb, m_destC, cstride),
            _ => (f.Cr, m_destC, cstride),
        };
    }

    #endregion

    #region B pictures and skipped pictures

    /// <summary>vc1_decode_b_blocks, then the loop filter.</summary>
    private void DecodeBBlocks()
    {
        SelectCodingSets(m_cAcTableIndex);
        m_firstSliceLine = true;
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
        {
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                if ((m_skipIsRaw || m_dmbIsRaw) && BitsLeft <= 1)
                    throw new InvalidDataException("B picture cut short");
                DecodeBMb();
                CheckBits();
            }
            m_firstSliceLine = false;
        }
        if (m_loopFilter)
            LoopFilterIntra();
    }

    /// <summary>vc1_decode_b_mb.</summary>
    private void DecodeBMb()
    {
        int mbPos = MbPos;
        int ttmb = m_ttfrm;
        bool firstBlock = true;
        int mquant = m_pq;
        int cbp = 0;
        m_mbIntra = 0;
        m_mbHasCoeffs = 0;
        int direct = m_dmbIsRaw ? GetBit() : m_directPlane[mbPos];
        int skipped = m_skipIsRaw ? GetBit() : m_skipPlane[mbPos];
        Span<int> dmvX = stackalloc int[2];
        Span<int> dmvY = stackalloc int[2];
        int bmvtype = BmvBackward;
        for (int i = 0; i < 6; i++)
        {
            MbType(m_blockIndex[i]) = 0;
            DcVal(m_blockIndex[i]) = 0;
        }
        m_qscale[mbPos] = 0;
        if (direct == 0)
        {
            if (skipped == 0)
            {
                GetMvData(out dmvX[0], out dmvY[0]);
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
            MbType(m_blockIndex[i]) = (byte)m_mbIntra;

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
            cbp = GetVlc(s_cbpcyPVlc[m_cbptab]);
            GetMquant(ref mquant);
            m_mbIntra = 0;
            m_qscale[mbPos] = mquant;
            if (m_ttmbf == 0)
                ttmb = GetVlc(s_ttmbVlc[m_ttIndex]);
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
                GetMquant(ref mquant);
                m_qscale[mbPos] = mquant;
                m_acPred = GetBit();
                cbp = 0;
                PredBMv(dmvX, dmvY, false, bmvtype);
            }
            else
            {
                if (bmvtype == BmvInterpolated)
                {
                    GetMvData(out dmvX[0], out dmvY[0]);
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
                cbp = GetVlc(s_cbpcyPVlc[m_cbptab]);
                GetMquant(ref mquant);
                m_qscale[mbPos] = mquant;
                if (m_ttmbf == 0 && m_mbIntra == 0 && m_mbHasCoeffs != 0)
                    ttmb = GetVlc(s_ttmbVlc[m_ttIndex]);
            }
        }
        var f = m_cur.Frame;
        int stride = f.Stride, cstride = f.ChromaStride;
        int ignored = 0;
        for (int i = 0; i < 6; i++)
        {
            DcVal(m_blockIndex[i]) = 0;
            int val = (cbp >> (5 - i)) & 1;
            MbType(m_blockIndex[i]) = (byte)m_mbIntra;
            var (plane, dst, linesize) = Dest(i, stride, cstride);
            if (m_mbIntra != 0)
            {
                m_aAvail = m_cAvail = 0;
                if (i is 2 or 3 || !m_firstSliceLine)
                    m_aAvail = MbType(m_blockIndex[i] - m_blockWrap[i]);
                if (i is 1 or 3 || m_mbX != 0)
                    m_cAvail = MbType(m_blockIndex[i] - 1);
                DecodeIntraBlock(m_scratch, i, val, mquant, i < 4 ? m_codingset : m_codingset2);
                InvTrans8x8(m_scratch);
                PutPixelsClamped(m_scratch, plane, dst, linesize, signed: true);
            }
            else if (val != 0)
            {
                DecodePBlock(m_scratch, i, mquant, ttmb, firstBlock, plane, dst, linesize, ref ignored);
                if (m_ttmbf == 0 && ttmb < 8)
                    ttmb = -1;
                firstBlock = false;
            }
        }
    }

    /// <summary>vc1_decode_skip_blocks: the last picture again.</summary>
    private void DecodeSkipBlocks()
    {
        var from = m_last!.Frame;
        var to = m_cur.Frame;
        from.Y.CopyTo(to.Y, 0);
        from.Cb.CopyTo(to.Cb, 0);
        from.Cr.CopyTo(to.Cr, 0);
        Array.Clear(m_cur.Motion[0]);
        Array.Clear(m_cur.Motion[1]);
    }

    #endregion

    #region Putting the intra blocks

    /// <summary>
    /// The intra blocks of the picture into it (put_pixels_clamped, or put_signed_pixels_clamped
    /// with 128 added), after the overlap smoothing.
    /// </summary>
    private void PutIntraBlocks(bool signed)
    {
        var f = m_cur.Frame;
        int stride = f.Stride, cstride = f.ChromaStride;
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                for (int i = 0; i < 6; i++)
                {
                    if (MbType(m_blockIndex[i]) == 0)
                        continue;
                    var (plane, dst, linesize) = Dest(i, stride, cstride);
                    PutPixelsClamped(Block(i), plane, dst, linesize, signed);
                }
            }
    }

    private static void PutPixelsClamped(ReadOnlySpan<short> block, byte[] plane, int dst, int linesize, bool signed)
    {
        int add = signed ? 128 : 0;
        for (int y = 0; y < 8; y++, dst += linesize)
            for (int x = 0; x < 8; x++)
                plane[dst + x] = Clip(block[y * 8 + x] + add);
    }

    private static void AddPixelsClamped(ReadOnlySpan<short> block, byte[] plane, int dst, int linesize)
    {
        for (int y = 0; y < 8; y++, dst += linesize)
            for (int x = 0; x < 8; x++)
                plane[dst + x] = Clip(plane[dst + x] + block[y * 8 + x]);
    }

    #endregion

    #region Inverse transforms (vc1dsp.c)

    private static void InvTrans8x8(Span<short> block)
    {
        Span<short> temp = stackalloc short[64];
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
            temp[d] = (short)((t5 + t1) >> 3);
            temp[d + 1] = (short)((t6 + t2) >> 3);
            temp[d + 2] = (short)((t7 + t3) >> 3);
            temp[d + 3] = (short)((t8 + t4) >> 3);
            temp[d + 4] = (short)((t8 - t4) >> 3);
            temp[d + 5] = (short)((t7 - t3) >> 3);
            temp[d + 6] = (short)((t6 - t2) >> 3);
            temp[d + 7] = (short)((t5 - t1) >> 3);
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
            block[i] = (short)((t5 + t1) >> 7);
            block[i + 8] = (short)((t6 + t2) >> 7);
            block[i + 16] = (short)((t7 + t3) >> 7);
            block[i + 24] = (short)((t8 + t4) >> 7);
            block[i + 32] = (short)((t8 - t4 + 1) >> 7);
            block[i + 40] = (short)((t7 - t3 + 1) >> 7);
            block[i + 48] = (short)((t6 - t2 + 1) >> 7);
            block[i + 56] = (short)((t5 - t1 + 1) >> 7);
        }
    }

    private static void AddDc(byte[] plane, int dst, int linesize, int dc, int width, int height)
    {
        for (int y = 0; y < height; y++, dst += linesize)
            for (int x = 0; x < width; x++)
                plane[dst + x] = Clip(plane[dst + x] + dc);
    }

    private static void InvTrans8x8Dc(byte[] plane, int dst, int linesize, ReadOnlySpan<short> block)
    {
        int dc = block[0];
        dc = (3 * dc + 1) >> 1;
        dc = (3 * dc + 16) >> 5;
        AddDc(plane, dst, linesize, dc, 8, 8);
    }

    private static void InvTrans8x4Dc(byte[] plane, int dst, int linesize, int dc)
    {
        dc = (3 * dc + 1) >> 1;
        dc = (17 * dc + 64) >> 7;
        AddDc(plane, dst, linesize, dc, 8, 4);
    }

    private static void InvTrans4x8Dc(byte[] plane, int dst, int linesize, int dc)
    {
        dc = (17 * dc + 4) >> 3;
        dc = (12 * dc + 64) >> 7;
        AddDc(plane, dst, linesize, dc, 4, 8);
    }

    private static void InvTrans4x4Dc(byte[] plane, int dst, int linesize, int dc)
    {
        dc = (17 * dc + 4) >> 3;
        dc = (17 * dc + 64) >> 7;
        AddDc(plane, dst, linesize, dc, 4, 4);
    }

    /// <summary>The 8x4 half of a block (4 rows of 8 coefficients) added to the picture.</summary>
    private static void InvTrans8x4(byte[] plane, int dest, int linesize, Span<short> block)
    {
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
            block[s] = (short)((t5 + t1) >> 3);
            block[s + 1] = (short)((t6 + t2) >> 3);
            block[s + 2] = (short)((t7 + t3) >> 3);
            block[s + 3] = (short)((t8 + t4) >> 3);
            block[s + 4] = (short)((t8 - t4) >> 3);
            block[s + 5] = (short)((t7 - t3) >> 3);
            block[s + 6] = (short)((t6 - t2) >> 3);
            block[s + 7] = (short)((t5 - t1) >> 3);
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

    /// <summary>The 4x8 half of a block (8 rows of 4 coefficients, stride 8) added to the picture.</summary>
    private static void InvTrans4x8(byte[] plane, int dest, int linesize, Span<short> block)
    {
        for (int i = 0; i < 8; i++)
        {
            int s = i * 8;
            int t1 = 17 * (block[s] + block[s + 2]) + 4, t2 = 17 * (block[s] - block[s + 2]) + 4;
            int t3 = 22 * block[s + 1] + 10 * block[s + 3], t4 = 22 * block[s + 3] - 10 * block[s + 1];
            block[s] = (short)((t1 + t3) >> 3);
            block[s + 1] = (short)((t2 - t4) >> 3);
            block[s + 2] = (short)((t2 + t4) >> 3);
            block[s + 3] = (short)((t1 - t3) >> 3);
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

    /// <summary>A 4x4 quarter of a block (stride 8) added to the picture.</summary>
    private static void InvTrans4x4(byte[] plane, int dest, int linesize, Span<short> block)
    {
        for (int i = 0; i < 4; i++)
        {
            int s = i * 8;
            int t1 = 17 * (block[s] + block[s + 2]) + 4, t2 = 17 * (block[s] - block[s + 2]) + 4;
            int t3 = 22 * block[s + 1] + 10 * block[s + 3], t4 = 22 * block[s + 3] - 10 * block[s + 1];
            block[s] = (short)((t1 + t3) >> 3);
            block[s + 1] = (short)((t2 - t4) >> 3);
            block[s + 2] = (short)((t2 + t4) >> 3);
            block[s + 3] = (short)((t1 - t3) >> 3);
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

    #endregion
}
