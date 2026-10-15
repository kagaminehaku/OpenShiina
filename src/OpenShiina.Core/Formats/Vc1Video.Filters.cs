// VC-1 overlap smoothing and loop filter (FFmpeg vc1_loopfilter.c, vc1dsp.c), each over a whole
// picture: the overlap of intra blocks before they are put (vertical edges, then horizontal),
// the loop filter after (horizontal edges, then vertical), macroblocks in raster order and
// their blocks in FFmpeg's order within each.

namespace OpenShiina.Formats;

public sealed partial class Vc1Video
{
    #region Overlap smoothing

    /// <summary>Block n of macroblock mb's stored coefficients.</summary>
    private int BlockAt(int mb, int n) => (mb * 6 + n) * 64;

    /// <summary>vc1_h_s_overlap_c over the vertical edge between two blocks (8 rows).</summary>
    private void HOverlap(int left, int right)
    {
        var b = m_blocks;
        int rnd1 = 4, rnd2 = 3;
        for (int i = 0; i < 8; i++, left += 8, right += 8)
        {
            int a = b[left + 6], bb = b[left + 7], c = b[right], d = b[right + 1];
            int d1 = a - d, d2 = a - d + bb - c;
            b[left + 6] = (short)((a * 8 - d1 + rnd1) >> 3);
            b[left + 7] = (short)((bb * 8 - d2 + rnd2) >> 3);
            b[right] = (short)((c * 8 + d2 + rnd1) >> 3);
            b[right + 1] = (short)((d * 8 + d1 + rnd2) >> 3);
            rnd2 = 7 - rnd2;
            rnd1 = 7 - rnd1;
        }
    }

    /// <summary>vc1_v_s_overlap_c over the horizontal edge between two blocks (8 columns).</summary>
    private void VOverlap(int top, int bottom)
    {
        var b = m_blocks;
        int rnd1 = 4, rnd2 = 3;
        for (int i = 0; i < 8; i++, top++, bottom++)
        {
            int a = b[top + 48], bb = b[top + 56], c = b[bottom], d = b[bottom + 8];
            int d1 = a - d, d2 = a - d + bb - c;
            b[top + 48] = (short)((a * 8 - d1 + rnd1) >> 3);
            b[top + 56] = (short)((bb * 8 - d2 + rnd2) >> 3);
            b[bottom] = (short)((c * 8 + d2 + rnd1) >> 3);
            b[bottom + 8] = (short)((d * 8 + d1 + rnd2) >> 3);
            rnd2 = 7 - rnd2;
            rnd1 = 7 - rnd1;
        }
    }

    /// <summary>
    /// Edge i of vc1_h_overlap_filter (0: left macroblock's top right / top left, 1: top left /
    /// top right, 2: left's bottom right / bottom left, 3: bottom left / bottom right, 4, 5:
    /// chroma with the left macroblock's).
    /// </summary>
    private void HOverlapEdge(int mb, int i)
    {
        switch (i)
        {
            case 0: HOverlap(BlockAt(mb - 1, 1), BlockAt(mb, 0)); break;
            case 1: HOverlap(BlockAt(mb, 0), BlockAt(mb, 1)); break;
            case 2: HOverlap(BlockAt(mb - 1, 3), BlockAt(mb, 2)); break;
            case 3: HOverlap(BlockAt(mb, 2), BlockAt(mb, 3)); break;
            default: HOverlap(BlockAt(mb - 1, i), BlockAt(mb, i)); break;
        }
    }

    /// <summary>Edge i of vc1_v_overlap_filter (0, 1: with the macroblock above, 2, 3: inside, 4, 5: chroma with the one above).</summary>
    private void VOverlapEdge(int mb, int i)
    {
        int above = mb - m_mbStride;
        switch (i)
        {
            case 0: VOverlap(BlockAt(above, 2), BlockAt(mb, 0)); break;
            case 1: VOverlap(BlockAt(above, 3), BlockAt(mb, 1)); break;
            case 2: VOverlap(BlockAt(mb, 0), BlockAt(mb, 2)); break;
            case 3: VOverlap(BlockAt(mb, 1), BlockAt(mb, 3)); break;
            default: VOverlap(BlockAt(above, i), BlockAt(mb, i)); break;
        }
    }

    /// <summary>ff_vc1_i_overlap_filter over an I picture.</summary>
    private void OverlapIntraPicture()
    {
        bool advanced = m_profile == ProfileAdvanced;
        bool Applies(int mb, bool inside, int other) =>
            m_pq >= 9 || advanced && (m_condover == CondoverAll || m_overFlagsPlane[mb] != 0 && (inside || m_overFlagsPlane[other] != 0));
        for (int y = 0; y < m_mbHeight; y++)
            for (int x = 0; x < m_mbWidth; x++)
            {
                int mb = x + y * m_mbStride;
                for (int i = 0; i < 6; i++)
                {
                    bool inside = (i & 5) == 1;
                    if (x == 0 && !inside)
                        continue;
                    if (Applies(mb, inside, mb - 1))
                        HOverlapEdge(mb, i);
                }
            }
        for (int y = 0; y < m_mbHeight; y++)
            for (int x = 0; x < m_mbWidth; x++)
            {
                int mb = x + y * m_mbStride;
                for (int i = 0; i < 6; i++)
                {
                    bool inside = (i & 2) != 0;
                    if (y == 0 && !inside)
                        continue;
                    if (Applies(mb, inside, mb - m_mbStride))
                        VOverlapEdge(mb, i);
                }
            }
    }

    /// <summary>ff_vc1_p_overlap_filter over a P picture: the edges between two intra blocks.</summary>
    private void OverlapInterPicture()
    {
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                int mb = MbPos;
                for (int i = 0; i < 6; i++)
                {
                    if (m_mbX == 0 && (i & 5) != 1)
                        continue;
                    if (MbType(m_blockIndex[i]) != 0 && MbType(m_blockIndex[i] - 1) != 0)
                        HOverlapEdge(mb, i);
                }
            }
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                int mb = MbPos;
                for (int i = 0; i < 6; i++)
                {
                    if (m_mbY == 0 && (i & 2) == 0)
                        continue;
                    if (MbType(m_blockIndex[i]) != 0 && MbType(m_blockIndex[i] - m_blockWrap[i]) != 0)
                        VOverlapEdge(mb, i);
                }
            }
    }

    #endregion

    #region Loop filter

    /// <summary>vc1_filter_line: one line across an edge; true when the other three of its four should be filtered.</summary>
    private static bool FilterLine(byte[] p, int src, int stride, int pq)
    {
        int a0 = (2 * (p[src - 2 * stride] - p[src + stride]) - 5 * (p[src - stride] - p[src]) + 4) >> 3;
        int a0Sign = a0 >> 31;
        a0 = (a0 ^ a0Sign) - a0Sign;
        if (a0 < pq)
        {
            int a1 = Math.Abs((2 * (p[src - 4 * stride] - p[src - stride]) - 5 * (p[src - 3 * stride] - p[src - 2 * stride]) + 4) >> 3);
            int a2 = Math.Abs((2 * (p[src] - p[src + 3 * stride]) - 5 * (p[src + stride] - p[src + 2 * stride]) + 4) >> 3);
            if (a1 < a0 || a2 < a0)
            {
                int clip = p[src - stride] - p[src];
                int clipSign = clip >> 31;
                clip = ((clip ^ clipSign) - clipSign) >> 1;
                if (clip != 0)
                {
                    int a3 = Math.Min(a1, a2);
                    int d = 5 * (a3 - a0);
                    int dSign = d >> 31;
                    d = ((d ^ dSign) - dSign) >> 3;
                    dSign ^= a0Sign;
                    if ((dSign ^ clipSign) == 0)
                    {
                        d = Math.Min(d, clip);
                        d = (d ^ dSign) - dSign;
                        p[src - stride] = Clip(p[src - stride] - d);
                        p[src] = Clip(p[src] + d);
                    }
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>vc1_loop_filter: an edge of <paramref name="length"/> pixels, four at a time.</summary>
    private static void LoopFilter(byte[] p, int src, int step, int stride, int length, int pq)
    {
        for (int i = 0; i < length; i += 4, src += 4 * step)
        {
            if (FilterLine(p, src + 2 * step, stride, pq))
            {
                FilterLine(p, src, stride, pq);
                FilterLine(p, src + step, stride, pq);
                FilterLine(p, src + 3 * step, stride, pq);
            }
        }
    }

    /// <summary>The horizontal edge at <paramref name="src"/> (v_loop_filter: along x).</summary>
    private void VLoop(byte[] p, int src, int linesize, int length) => LoopFilter(p, src, 1, linesize, length, m_pq);

    /// <summary>The vertical edge at <paramref name="src"/> (h_loop_filter: along y).</summary>
    private void HLoop(byte[] p, int src, int linesize, int length) => LoopFilter(p, src, linesize, 1, length, m_pq);

    /// <summary>ff_vc1_i_loop_filter (I and B pictures): every 8x8 edge inside the picture.</summary>
    private void LoopFilterIntra()
    {
        var f = m_cur.Frame;
        int stride = f.Stride, cstride = f.ChromaStride;
        for (int y = 0; y < m_mbHeight; y++)
            for (int x = 0; x < m_mbWidth; x++)
            {
                int dy = y * 16 * stride + x * 16, dc = y * 8 * cstride + x * 8;
                if (y != 0)
                {
                    VLoop(f.Y, dy, stride, 16);
                }
                VLoop(f.Y, dy + 8 * stride, stride, 16);
                if (y != 0)
                {
                    VLoop(f.Cb, dc, cstride, 8);
                    VLoop(f.Cr, dc, cstride, 8);
                }
            }
        for (int y = 0; y < m_mbHeight; y++)
            for (int x = 0; x < m_mbWidth; x++)
            {
                int dy = y * 16 * stride + x * 16, dc = y * 8 * cstride + x * 8;
                if (x != 0)
                    HLoop(f.Y, dy, stride, 16);
                HLoop(f.Y, dy + 8, stride, 16);
                if (x != 0)
                {
                    HLoop(f.Cb, dc, cstride, 8);
                    HLoop(f.Cr, dc, cstride, 8);
                }
            }
    }

    /// <summary>ff_vc1_p_loop_filter over a P picture: the edges where the blocks differ (intra, vectors, coded parts).</summary>
    private void LoopFilterInter()
    {
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                for (int i = 0; i < 6; i++)
                    PVLoopFilter(i, m_mbY == m_mbHeight - 1);
            }
        for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
            for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
            {
                SetBlockIndex();
                for (int i = 0; i < 6; i++)
                    PHLoopFilter(i, m_mbX == m_mbWidth - 1);
            }
    }

    /// <summary>vc1_p_v_loop_filter: the bottom edge of block n, and its middle one for 8x4 / 4x4 transforms.</summary>
    private void PVLoopFilter(int n, bool bottomEdge)
    {
        var f = m_cur.Frame;
        int mb = MbPos;
        var (plane, dst, linesize) = Dest(n, f.Stride, f.ChromaStride);
        uint topCbp = m_cbp[mb] >> (n * 4);
        if (!bottomEdge || n < 2)
        {
            bool topIntra = (m_isIntra[mb] & (1 << n)) != 0, bottomIntra;
            uint bottomCbp;
            bool mvDiffer;
            if (n > 3)
            {
                int below = mb + m_mbStride;
                bottomIntra = (m_isIntra[below] & (1 << n)) != 0;
                bottomCbp = m_cbp[below] >> (n * 4);
                mvDiffer = m_lumaMv[mb * 2] != m_lumaMv[below * 2] || m_lumaMv[mb * 2 + 1] != m_lumaMv[below * 2 + 1];
            }
            else
            {
                if (n < 2)
                {
                    bottomIntra = (m_isIntra[mb] & (1 << (n + 2))) != 0;
                    bottomCbp = m_cbp[mb] >> ((n + 2) * 4);
                }
                else
                {
                    bottomIntra = (m_isIntra[mb + m_mbStride] & (1 << (n - 2))) != 0;
                    bottomCbp = m_cbp[mb + m_mbStride] >> ((n - 2) * 4);
                }
                int xy = m_blockIndex[n];
                mvDiffer = Motion(m_cur, 0, xy, 0) != Motion(m_cur, 0, xy + m_b8Stride, 0)
                    || Motion(m_cur, 0, xy, 1) != Motion(m_cur, 0, xy + m_b8Stride, 1);
            }
            if (topIntra || bottomIntra || mvDiffer)
                VLoop(plane, dst + 8 * linesize, linesize, 8);
            else
            {
                uint idx = (topCbp | (bottomCbp >> 2)) & 3;
                if ((idx & 1) != 0)
                    VLoop(plane, dst + 8 * linesize + 4, linesize, 4);
                if ((idx & 2) != 0)
                    VLoop(plane, dst + 8 * linesize, linesize, 4);
            }
        }
        int tt = (m_ttblk[mb] >> (n * 4)) & 0xF;
        if (tt is Tt4x4 or Tt8x4)
        {
            if ((topCbp & 5) != 0)
                VLoop(plane, dst + 4 * linesize + 4, linesize, 4);
            if ((topCbp & 10) != 0)
                VLoop(plane, dst + 4 * linesize, linesize, 4);
        }
    }

    /// <summary>vc1_p_h_loop_filter: the right edge of block n, and its middle one for 4x8 / 4x4 transforms.</summary>
    private void PHLoopFilter(int n, bool rightEdge)
    {
        var f = m_cur.Frame;
        int mb = MbPos;
        var (plane, dst, linesize) = Dest(n, f.Stride, f.ChromaStride);
        uint leftCbp = m_cbp[mb] >> (n * 4);
        // Microsoft's WMV9 decoder takes the coded parts of the top right block for the left side
        // of the bottom half's middle edge (the reference Simple / Main profile pictures show it)
        if (!rightEdge || (n & 5) == 0)
        {
            bool leftIntra = (m_isIntra[mb] & (1 << n)) != 0, rightIntra;
            uint rightCbp;
            bool mvDiffer;
            if (n > 3)
            {
                rightIntra = (m_isIntra[mb + 1] & (1 << n)) != 0;
                rightCbp = m_cbp[mb + 1] >> (n * 4);
                mvDiffer = m_lumaMv[mb * 2] != m_lumaMv[(mb + 1) * 2] || m_lumaMv[mb * 2 + 1] != m_lumaMv[(mb + 1) * 2 + 1];
            }
            else
            {
                if ((n & 1) != 0)
                {
                    rightIntra = (m_isIntra[mb + 1] & (1 << (n - 1))) != 0;
                    rightCbp = m_cbp[mb + 1] >> ((n - 1) * 4);
                }
                else
                {
                    rightIntra = (m_isIntra[mb] & (1 << (n + 1))) != 0;
                    rightCbp = m_cbp[mb] >> ((n + 1) * 4);
                }
                int xy = m_blockIndex[n];
                mvDiffer = Motion(m_cur, 0, xy, 0) != Motion(m_cur, 0, xy + 1, 0)
                    || Motion(m_cur, 0, xy, 1) != Motion(m_cur, 0, xy + 1, 1);
            }
            if (leftIntra || rightIntra || mvDiffer)
                HLoop(plane, dst + 8, linesize, 8);
            else
            {
                uint idx = (leftCbp | (rightCbp >> 1)) & 5;
                if ((idx & 1) != 0)
                    HLoop(plane, dst + 4 * linesize + 8, linesize, 4);
                if ((idx & 4) != 0)
                    HLoop(plane, dst + 8, linesize, 4);
            }
        }
        int tt = (m_ttblk[mb] >> (n * 4)) & 0xF;
        if (tt is Tt4x4 or Tt4x8)
        {
            if ((leftCbp & 3) != 0)
                HLoop(plane, dst + 4 * linesize + 4, linesize, 4);
            if ((leftCbp & 12) != 0)
                HLoop(plane, dst + 4, linesize, 4);
        }
    }

    #endregion
}
