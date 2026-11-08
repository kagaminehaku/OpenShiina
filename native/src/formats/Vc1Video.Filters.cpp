// C# Formats/Vc1Video.Filters.cs: VC-1 overlap smoothing and loop filter (FFmpeg vc1_loopfilter.c,
// vc1dsp.c), each over a whole picture: the overlap of intra blocks before they are put (vertical
// edges, then horizontal), the loop filter after (horizontal edges, then vertical), macroblocks in
// raster order and their blocks in FFmpeg's order within each.
#include <algorithm>
#include <cstdlib>

#include "formats/Vc1Video.h"

namespace openshiina::formats {

// ---- Overlap smoothing ----

void Vc1Video::HOverlap(int left, int right)
{
    // vc1_h_s_overlap_c over the vertical edge between two blocks (8 rows)
    int16_t* b = m_blocks.data();
    int rnd1 = 4, rnd2 = 3;
    for (int i = 0; i < 8; i++, left += 8, right += 8)
    {
        int a = b[left + 6], bb = b[left + 7], c = b[right], d = b[right + 1];
        int d1 = a - d, d2 = a - d + bb - c;
        b[left + 6] = int16_t((a * 8 - d1 + rnd1) >> 3);
        b[left + 7] = int16_t((bb * 8 - d2 + rnd2) >> 3);
        b[right] = int16_t((c * 8 + d2 + rnd1) >> 3);
        b[right + 1] = int16_t((d * 8 + d1 + rnd2) >> 3);
        rnd2 = 7 - rnd2;
        rnd1 = 7 - rnd1;
    }
}

void Vc1Video::VOverlap(int top, int bottom)
{
    // vc1_v_s_overlap_c over the horizontal edge between two blocks (8 columns)
    int16_t* b = m_blocks.data();
    int rnd1 = 4, rnd2 = 3;
    for (int i = 0; i < 8; i++, top++, bottom++)
    {
        int a = b[top + 48], bb = b[top + 56], c = b[bottom], d = b[bottom + 8];
        int d1 = a - d, d2 = a - d + bb - c;
        b[top + 48] = int16_t((a * 8 - d1 + rnd1) >> 3);
        b[top + 56] = int16_t((bb * 8 - d2 + rnd2) >> 3);
        b[bottom] = int16_t((c * 8 + d2 + rnd1) >> 3);
        b[bottom + 8] = int16_t((d * 8 + d1 + rnd2) >> 3);
        rnd2 = 7 - rnd2;
        rnd1 = 7 - rnd1;
    }
}

void Vc1Video::HOverlapEdge(int mb, int i)
{
    // Edge i of vc1_h_overlap_filter (0: left macroblock's top right / top left, 1: top left / top
    // right, 2: left's bottom right / bottom left, 3: bottom left / bottom right, 4, 5: chroma with
    // the left macroblock's)
    switch (i)
    {
    case 0:
        HOverlap(BlockAt(mb - 1, 1), BlockAt(mb, 0));
        break;
    case 1:
        HOverlap(BlockAt(mb, 0), BlockAt(mb, 1));
        break;
    case 2:
        HOverlap(BlockAt(mb - 1, 3), BlockAt(mb, 2));
        break;
    case 3:
        HOverlap(BlockAt(mb, 2), BlockAt(mb, 3));
        break;
    default:
        HOverlap(BlockAt(mb - 1, i), BlockAt(mb, i));
        break;
    }
}

void Vc1Video::VOverlapEdge(int mb, int i)
{
    // Edge i of vc1_v_overlap_filter (0, 1: with the macroblock above, 2, 3: inside, 4, 5: chroma
    // with the one above)
    int above = mb - m_mbStride;
    switch (i)
    {
    case 0:
        VOverlap(BlockAt(above, 2), BlockAt(mb, 0));
        break;
    case 1:
        VOverlap(BlockAt(above, 3), BlockAt(mb, 1));
        break;
    case 2:
        VOverlap(BlockAt(mb, 0), BlockAt(mb, 2));
        break;
    case 3:
        VOverlap(BlockAt(mb, 1), BlockAt(mb, 3));
        break;
    default:
        VOverlap(BlockAt(above, i), BlockAt(mb, i));
        break;
    }
}

void Vc1Video::OverlapIntraPicture()
{
    // ff_vc1_i_overlap_filter over an I picture
    bool advanced = m_profile == ProfileAdvanced;
    auto applies = [&](int mb, bool inside, int other) {
        return m_pq >= 9
            || (advanced && (m_condover == CondoverAll || (m_overFlagsPlane[size_t(mb)] != 0 && (inside || m_overFlagsPlane[size_t(other)] != 0))));
    };
    for (int y = 0; y < m_mbHeight; y++)
        for (int x = 0; x < m_mbWidth; x++)
        {
            int mb = x + y * m_mbStride;
            for (int i = 0; i < 6; i++)
            {
                bool inside = (i & 5) == 1;
                if (x == 0 && !inside)
                    continue;
                if (applies(mb, inside, mb - 1))
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
                if (applies(mb, inside, mb - m_mbStride))
                    VOverlapEdge(mb, i);
            }
        }
}

void Vc1Video::OverlapInterPicture()
{
    // ff_vc1_p_overlap_filter over a P picture: the edges between two intra blocks
    for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
        for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
        {
            SetBlockIndex();
            int mb = MbPos();
            for (int i = 0; i < 6; i++)
            {
                if (m_mbX == 0 && (i & 5) != 1)
                    continue;
                if (MbType(m_blockIndex[size_t(i)]) != 0 && MbType(m_blockIndex[size_t(i)] - 1) != 0)
                    HOverlapEdge(mb, i);
            }
        }
    for (m_mbY = 0; m_mbY < m_mbHeight; m_mbY++)
        for (m_mbX = 0; m_mbX < m_mbWidth; m_mbX++)
        {
            SetBlockIndex();
            int mb = MbPos();
            for (int i = 0; i < 6; i++)
            {
                if (m_mbY == 0 && (i & 2) == 0)
                    continue;
                if (MbType(m_blockIndex[size_t(i)]) != 0 && MbType(m_blockIndex[size_t(i)] - m_blockWrap[size_t(i)]) != 0)
                    VOverlapEdge(mb, i);
            }
        }
}

// ---- Loop filter ----

bool Vc1Video::FilterLine(uint8_t* p, int src, int stride, int pq)
{
    // vc1_filter_line: one line across an edge; true when the other three of its four should be filtered
    int a0 = (2 * (p[src - 2 * stride] - p[src + stride]) - 5 * (p[src - stride] - p[src]) + 4) >> 3;
    int a0Sign = a0 >> 31;
    a0 = (a0 ^ a0Sign) - a0Sign;
    if (a0 < pq)
    {
        int a1 = std::abs((2 * (p[src - 4 * stride] - p[src - stride]) - 5 * (p[src - 3 * stride] - p[src - 2 * stride]) + 4) >> 3);
        int a2 = std::abs((2 * (p[src] - p[src + 3 * stride]) - 5 * (p[src + stride] - p[src + 2 * stride]) + 4) >> 3);
        if (a1 < a0 || a2 < a0)
        {
            int clip = p[src - stride] - p[src];
            int clipSign = clip >> 31;
            clip = ((clip ^ clipSign) - clipSign) >> 1;
            if (clip != 0)
            {
                int a3 = std::min(a1, a2);
                int d = 5 * (a3 - a0);
                int dSign = d >> 31;
                d = ((d ^ dSign) - dSign) >> 3;
                dSign ^= a0Sign;
                if ((dSign ^ clipSign) == 0)
                {
                    d = std::min(d, clip);
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

void Vc1Video::LoopFilter(uint8_t* p, int src, int step, int stride, int length, int pq)
{
    // vc1_loop_filter: an edge of length pixels, four at a time
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

void Vc1Video::LoopFilterIntra()
{
    // ff_vc1_i_loop_filter (I and B pictures): every 8x8 edge inside the picture
    MpegFrame& f = m_cur->Frame;
    int stride = f.Stride, cstride = f.ChromaStride;
    for (int y = 0; y < m_mbHeight; y++)
        for (int x = 0; x < m_mbWidth; x++)
        {
            int dy = y * 16 * stride + x * 16, dc = y * 8 * cstride + x * 8;
            if (y != 0)
                VLoop(f.Y.data(), dy, stride, 16);
            VLoop(f.Y.data(), dy + 8 * stride, stride, 16);
            if (y != 0)
            {
                VLoop(f.Cb.data(), dc, cstride, 8);
                VLoop(f.Cr.data(), dc, cstride, 8);
            }
        }
    for (int y = 0; y < m_mbHeight; y++)
        for (int x = 0; x < m_mbWidth; x++)
        {
            int dy = y * 16 * stride + x * 16, dc = y * 8 * cstride + x * 8;
            if (x != 0)
                HLoop(f.Y.data(), dy, stride, 16);
            HLoop(f.Y.data(), dy + 8, stride, 16);
            if (x != 0)
            {
                HLoop(f.Cb.data(), dc, cstride, 8);
                HLoop(f.Cr.data(), dc, cstride, 8);
            }
        }
}

void Vc1Video::LoopFilterInter()
{
    // ff_vc1_p_loop_filter over a P picture: the edges where the blocks differ (intra, vectors, coded parts)
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

void Vc1Video::PVLoopFilter(int n, bool bottomEdge)
{
    // vc1_p_v_loop_filter: the bottom edge of block n, and its middle one for 8x4 / 4x4 transforms
    MpegFrame& f = m_cur->Frame;
    size_t mb = size_t(MbPos());
    Destination d = Dest(n, f.Stride, f.ChromaStride);
    uint8_t* plane = d.Plane;
    int dst = d.Offset, linesize = d.Stride;
    uint32_t topCbp = m_cbp[mb] >> (n * 4);
    if (!bottomEdge || n < 2)
    {
        bool topIntra = (m_isIntra[mb] & (1 << n)) != 0, bottomIntra;
        uint32_t bottomCbp;
        bool mvDiffer;
        if (n > 3)
        {
            size_t below = mb + size_t(m_mbStride);
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
                bottomIntra = (m_isIntra[mb + size_t(m_mbStride)] & (1 << (n - 2))) != 0;
                bottomCbp = m_cbp[mb + size_t(m_mbStride)] >> ((n - 2) * 4);
            }
            int xy = m_blockIndex[size_t(n)];
            mvDiffer = Motion(*m_cur, 0, xy, 0) != Motion(*m_cur, 0, xy + m_b8Stride, 0)
                || Motion(*m_cur, 0, xy, 1) != Motion(*m_cur, 0, xy + m_b8Stride, 1);
        }
        if (topIntra || bottomIntra || mvDiffer)
            VLoop(plane, dst + 8 * linesize, linesize, 8);
        else
        {
            uint32_t idx = (topCbp | (bottomCbp >> 2)) & 3;
            if ((idx & 1) != 0)
                VLoop(plane, dst + 8 * linesize + 4, linesize, 4);
            if ((idx & 2) != 0)
                VLoop(plane, dst + 8 * linesize, linesize, 4);
        }
    }
    int tt = (m_ttblk[mb] >> (n * 4)) & 0xF;
    if (tt == Tt4x4 || tt == Tt8x4)
    {
        if ((topCbp & 5) != 0)
            VLoop(plane, dst + 4 * linesize + 4, linesize, 4);
        if ((topCbp & 10) != 0)
            VLoop(plane, dst + 4 * linesize, linesize, 4);
    }
}

void Vc1Video::PHLoopFilter(int n, bool rightEdge)
{
    // vc1_p_h_loop_filter: the right edge of block n, and its middle one for 4x8 / 4x4 transforms
    MpegFrame& f = m_cur->Frame;
    size_t mb = size_t(MbPos());
    Destination d = Dest(n, f.Stride, f.ChromaStride);
    uint8_t* plane = d.Plane;
    int dst = d.Offset, linesize = d.Stride;
    uint32_t leftCbp = m_cbp[mb] >> (n * 4);
    // Microsoft's WMV9 decoder takes the coded parts of the top right block for the left side of the
    // bottom half's middle edge (the reference Simple / Main profile pictures show it)
    if (!rightEdge || (n & 5) == 0)
    {
        bool leftIntra = (m_isIntra[mb] & (1 << n)) != 0, rightIntra;
        uint32_t rightCbp;
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
            int xy = m_blockIndex[size_t(n)];
            mvDiffer = Motion(*m_cur, 0, xy, 0) != Motion(*m_cur, 0, xy + 1, 0) || Motion(*m_cur, 0, xy, 1) != Motion(*m_cur, 0, xy + 1, 1);
        }
        if (leftIntra || rightIntra || mvDiffer)
            HLoop(plane, dst + 8, linesize, 8);
        else
        {
            uint32_t idx = (leftCbp | (rightCbp >> 1)) & 5;
            if ((idx & 1) != 0)
                HLoop(plane, dst + 4 * linesize + 8, linesize, 4);
            if ((idx & 4) != 0)
                HLoop(plane, dst + 8, linesize, 4);
        }
    }
    int tt = (m_ttblk[mb] >> (n * 4)) & 0xF;
    if (tt == Tt4x4 || tt == Tt4x8)
    {
        if ((leftCbp & 3) != 0)
            HLoop(plane, dst + 4 * linesize + 4, linesize, 4);
        if ((leftCbp & 12) != 0)
            HLoop(plane, dst + 4, linesize, 4);
    }
}

} // namespace openshiina::formats
