// C# Formats/Vc1Video.Motion.cs: VC-1 motion vectors and motion compensation (FFmpeg vc1_pred.c,
// vc1_mc.c and the bicubic, bilinear and chroma interpolation of vc1dsp.c / hpeldsp / h264chroma). A
// reference block is always copied with its coordinates clamped to the picture (FFmpeg's edge
// emulation, also what its padded pictures give) before it is interpolated.
#include <algorithm>
#include <cstdlib>
#include <cstring>

#include "formats/Vc1Video.h"

namespace openshiina::formats {

int Vc1Video::MidPred(int a, int b, int c)
{
    if (a > b)
    {
        if (c > b)
            b = c > a ? a : c;
    }
    else if (b > c)
        b = c > a ? c : a;
    return b;
}

int Vc1Video::Median4(int a, int b, int c, int d)
{
    if (a < b)
        return c < d ? (std::min(b, d) + std::max(a, c)) / 2 : (std::min(b, c) + std::max(a, d)) / 2;
    return c < d ? (std::min(a, d) + std::max(b, c)) / 2 : (std::min(a, c) + std::max(b, d)) / 2;
}

// ---- Prediction ----

void Vc1Video::PredMv(int n, int dmvX, int dmvY, bool mv1, int rX, int rY, int dir)
{
    // ff_vc1_pred_mv for progressive pictures: predicts block n's vector and adds the differential
    Picture& cur = *m_cur;
    if (m_quarterSample == 0)
    {
        dmvX *= 2;
        dmvY *= 2;
    }
    int wrap = m_b8Stride, xy = m_blockIndex[size_t(n)];
    if (m_mbIntra != 0)
    {
        Mv(0, n, 0) = Motion(cur, 0, xy, 0) = 0;
        Mv(0, n, 1) = Motion(cur, 0, xy, 1) = 0;
        Motion(cur, 1, xy, 0) = Motion(cur, 1, xy, 1) = 0;
        if (mv1)
        {
            for (int at : {xy + 1, xy + wrap, xy + wrap + 1})
            {
                Motion(cur, 0, at, 0) = Motion(cur, 0, at, 1) = 0;
                Motion(cur, 1, at, 0) = Motion(cur, 1, at, 1) = 0;
            }
            m_lumaMv[size_t(MbPos()) * 2] = m_lumaMv[size_t(MbPos()) * 2 + 1] = 0;
        }
        return;
    }
    bool aValid = !m_firstSliceLine || n == 2 || n == 3;
    bool bValid = aValid;
    bool cValid = m_mbX != 0 || n == 1 || n == 3;
    int off;
    if (mv1)
    {
        off = m_mbX == m_mbWidth - 1 ? -1 : 2;
        bValid = bValid && m_mbWidth > 1;
    }
    else
    {
        switch (n)
        {
        case 0:
            off = m_resRtmFlag != 0 ? (m_mbX != 0 ? -1 : 1) : (m_mbX != 0 ? -1 : 2 * m_mbWidth - wrap - 1);
            break;
        case 1:
            off = m_mbX == m_mbWidth - 1 ? -1 : 1;
            break;
        case 2:
            off = 1;
            break;
        default:
            off = -1;
            break;
        }
    }
    int ax = 0, ay = 0, bx = 0, by = 0, cx = 0, cy = 0, valid = 0;
    if (aValid)
    {
        ax = Motion(cur, dir, xy - wrap, 0);
        ay = Motion(cur, dir, xy - wrap, 1);
        valid++;
    }
    if (bValid)
    {
        bx = Motion(cur, dir, xy - wrap + off, 0);
        by = Motion(cur, dir, xy - wrap + off, 1);
        valid++;
    }
    if (cValid)
    {
        cx = Motion(cur, dir, xy - 1, 0);
        cy = Motion(cur, dir, xy - 1, 1);
        valid++;
    }
    int px, py;
    if (aValid)
    {
        px = ax;
        py = ay;
    }
    else if (cValid)
    {
        px = cx;
        py = cy;
    }
    else if (bValid)
    {
        px = bx;
        py = by;
    }
    else
        px = py = 0;
    if (valid > 1)
    {
        px = MidPred(ax, bx, cx);
        py = MidPred(ay, by, cy);
    }
    // pullback (8.3.5.3.4)
    {
        int mvMin = mv1 ? -60 : -28;
        int qx = (m_mbX << 6) + (n == 1 || n == 3 ? 32 : 0);
        int qy = (m_mbY << 6) + (n == 2 || n == 3 ? 32 : 0);
        int x = (m_mbWidth << 6) - 4, y = (m_mbHeight << 6) - 4;
        if (qx + px < mvMin)
            px = mvMin - qx;
        if (qy + py < mvMin)
            py = mvMin - qy;
        if (qx + px > x)
            px = x - qx;
        if (qy + py > y)
            py = y - qy;
    }
    // hybrid prediction (8.3.5.3.5)
    if (aValid && cValid)
    {
        int sum = MbType(xy - wrap) != 0 ? std::abs(px) + std::abs(py) : std::abs(px - ax) + std::abs(py - ay);
        if (sum > 32)
        {
            if (GetBit() != 0)
            {
                px = ax;
                py = ay;
            }
            else
            {
                px = cx;
                py = cy;
            }
        }
        else
        {
            sum = MbType(xy - 1) != 0 ? std::abs(px) + std::abs(py) : std::abs(px - cx) + std::abs(py - cy);
            if (sum > 32)
            {
                if (GetBit() != 0)
                {
                    px = ax;
                    py = ay;
                }
                else
                {
                    px = cx;
                    py = cy;
                }
            }
        }
    }
    int mx = ((px + dmvX + rX) & ((rX << 1) - 1)) - rX;
    int my = ((py + dmvY + rY) & ((rY << 1) - 1)) - rY;
    Mv(dir, n, 0) = mx;
    Mv(dir, n, 1) = my;
    Motion(cur, dir, xy, 0) = int16_t(mx);
    Motion(cur, dir, xy, 1) = int16_t(my);
    if (mv1)
        for (int at : {xy + 1, xy + wrap, xy + wrap + 1})
        {
            Motion(cur, dir, at, 0) = int16_t(mx);
            Motion(cur, dir, at, 1) = int16_t(my);
        }
}

int Vc1Video::ScaleMv(int value, int bfrac, int inv, int qs)
{
    int n = bfrac;
    if (inv != 0)
        n -= 256;
    if (qs == 0)
        return 2 * ((value * n + 255) >> 9);
    return (value * n + 128) >> 8;
}

void Vc1Video::PredBMv(int* dmvX, int* dmvY, bool direct, int mvtype)
{
    // ff_vc1_pred_b_mv: the forward and backward vectors of a B macroblock
    Picture& cur = *m_cur;
    Picture& next = *m_nextPicture;
    int rX = m_rangeX, rY = m_rangeY;
    if (m_quarterSample == 0)
    {
        dmvX[0] *= 2;
        dmvY[0] *= 2;
        dmvX[1] *= 2;
        dmvY[1] *= 2;
    }
    int wrap = m_b8Stride, xy = m_blockIndex[0];
    if (m_mbIntra != 0)
    {
        Motion(cur, 0, xy, 0) = Motion(cur, 0, xy, 1) = Motion(cur, 1, xy, 0) = Motion(cur, 1, xy, 1) = 0;
        return;
    }
    int colX = Motion(next, 1, xy, 0), colY = Motion(next, 1, xy, 1);
    Mv(0, 0, 0) = ScaleMv(colX, m_bfraction, 0, m_quarterSample);
    Mv(0, 0, 1) = ScaleMv(colY, m_bfraction, 0, m_quarterSample);
    Mv(1, 0, 0) = ScaleMv(colX, m_bfraction, 1, m_quarterSample);
    Mv(1, 0, 1) = ScaleMv(colY, m_bfraction, 1, m_quarterSample);
    // pullback (8.4.5.4)
    int minX = -60 - (m_mbX << 6), maxX = (m_mbWidth << 6) - 4 - (m_mbX << 6);
    int minY = -60 - (m_mbY << 6), maxY = (m_mbHeight << 6) - 4 - (m_mbY << 6);
    Mv(0, 0, 0) = std::clamp(Mv(0, 0, 0), minX, maxX);
    Mv(0, 0, 1) = std::clamp(Mv(0, 0, 1), minY, maxY);
    Mv(1, 0, 0) = std::clamp(Mv(1, 0, 0), minX, maxX);
    Mv(1, 0, 1) = std::clamp(Mv(1, 0, 1), minY, maxY);
    if (direct)
    {
        Motion(cur, 0, xy, 0) = int16_t(Mv(0, 0, 0));
        Motion(cur, 0, xy, 1) = int16_t(Mv(0, 0, 1));
        Motion(cur, 1, xy, 0) = int16_t(Mv(1, 0, 0));
        Motion(cur, 1, xy, 1) = int16_t(Mv(1, 0, 1));
        return;
    }
    for (int dir = 0; dir < 2; dir++)
    {
        if (dir == 0 && mvtype != BmvForward && mvtype != BmvInterpolated)
            continue;
        if (dir == 1 && mvtype != BmvBackward && mvtype != BmvInterpolated)
            continue;
        int off = m_mbX == m_mbWidth - 1 ? -2 : 2;
        if (m_mbX == 0)
            Motion(cur, dir, xy - 2, 0) = Motion(cur, dir, xy - 2, 1) = 0;
        int cx = Motion(cur, dir, xy - 2, 0), cy = Motion(cur, dir, xy - 2, 1);
        int px, py;
        if (!m_firstSliceLine)
        {
            int ax = Motion(cur, dir, xy - wrap * 2, 0), ay = Motion(cur, dir, xy - wrap * 2, 1);
            if (m_mbWidth == 1)
            {
                px = ax;
                py = ay;
            }
            else
            {
                int bx = Motion(cur, dir, xy - wrap * 2 + off, 0), by = Motion(cur, dir, xy - wrap * 2 + off, 1);
                px = MidPred(ax, bx, cx);
                py = MidPred(ay, by, cy);
            }
        }
        else if (m_mbX != 0)
        {
            px = cx;
            py = cy;
        }
        else
            px = py = 0;
        // pullback (8.3.5.3.4)
        int sh = m_profile < ProfileAdvanced ? 5 : 6;
        int mvMin = 4 - (1 << sh);
        int qx = m_mbX << sh, qy = m_mbY << sh;
        int x = (m_mbWidth << sh) - 4, y = (m_mbHeight << sh) - 4;
        if (qx + px < mvMin)
            px = mvMin - qx;
        if (qy + py < mvMin)
            py = mvMin - qy;
        if (qx + px > x)
            px = x - qx;
        if (qy + py > y)
            py = y - qy;
        Mv(dir, 0, 0) = ((px + dmvX[dir] + rX) & ((rX << 1) - 1)) - rX;
        Mv(dir, 0, 1) = ((py + dmvY[dir] + rY) & ((rY << 1) - 1)) - rY;
    }
    Motion(cur, 0, xy, 0) = int16_t(Mv(0, 0, 0));
    Motion(cur, 0, xy, 1) = int16_t(Mv(0, 0, 1));
    Motion(cur, 1, xy, 0) = int16_t(Mv(1, 0, 0));
    Motion(cur, 1, xy, 1) = int16_t(Mv(1, 0, 1));
}

// ---- Compensation ----

void Vc1Video::BMc(bool direct, int mode)
{
    // vc1_b_mc
    if (direct || mode == BmvInterpolated)
    {
        Mc1Mv(0);
        InterpMc();
        return;
    }
    Mc1Mv(mode == BmvBackward ? 1 : 0);
}

void Vc1Video::Fetch(const uint8_t* plane, int stride, int width, int height, int x, int y, int w, int h, uint8_t* dst, int dstStride)
{
    // A block of a plane with its coordinates clamped to the picture
    if (x >= 0 && y >= 0 && x + w <= width && y + h <= height)
    {
        for (int j = 0; j < h; j++)
            std::memcpy(dst + j * dstStride, plane + (y + j) * stride + x, size_t(w));
        return;
    }
    for (int j = 0; j < h; j++)
    {
        int row = std::clamp(y + j, 0, height - 1) * stride;
        for (int i = 0; i < w; i++)
            dst[j * dstStride + i] = plane[row + std::clamp(x + i, 0, width - 1)];
    }
}

void Vc1Video::ApplyLut(uint8_t* block, int stride, int size, const uint8_t* lut)
{
    for (int j = 0; j < size; j++)
        for (int i = 0; i < size; i++)
            block[j * stride + i] = lut[block[j * stride + i]];
}

Vc1Video::Ref Vc1Video::Reference(int dir)
{
    // The reference of a direction: the last (0) or the next (1) anchor, and its intensity tables
    if (dir == 0)
        return {m_last, m_lastLuty.data(), m_lastLutuv.data(), m_lastUseIc != 0};
    return {m_nextPicture, m_nextLuty.data(), m_nextLutuv.data(), m_nextUseIc != 0};
}

void Vc1Video::FetchLuma(const MpegFrame& frame, int x, int y, int k, const uint8_t* luty, bool useIc)
{
    // The luma source of a block: copied into m_edgeY from (x - mspel, y - mspel), intensity compensated
    Fetch(frame.Y.data(), frame.Stride, m_codedWidth, m_codedHeight, x - m_mspel, y - m_mspel, k, k, m_edgeY.data(), EdgeStride);
    if (useIc)
        ApplyLut(m_edgeY.data(), EdgeStride, k, luty);
}

void Vc1Video::FetchChroma(const MpegFrame& frame, int x, int y, const uint8_t* lutuv, bool useIc)
{
    int w = m_codedWidth >> 1, h = m_codedHeight >> 1;
    Fetch(frame.Cb.data(), frame.ChromaStride, w, h, x, y, 9, 9, m_edgeU.data(), ChromaEdgeStride);
    Fetch(frame.Cr.data(), frame.ChromaStride, w, h, x, y, 9, 9, m_edgeV.data(), ChromaEdgeStride);
    if (useIc)
    {
        ApplyLut(m_edgeU.data(), ChromaEdgeStride, 9, lutuv);
        ApplyLut(m_edgeV.data(), ChromaEdgeStride, 9, lutuv);
    }
}

void Vc1Video::ClampLumaSource(int& x, int& y) const
{
    if (m_profile != ProfileAdvanced)
    {
        x = std::clamp(x, -16, m_mbWidth * 16);
        y = std::clamp(y, -16, m_mbHeight * 16);
    }
    else
    {
        x = std::clamp(x, -17, m_codedWidth);
        y = std::clamp(y, -18, m_codedHeight + 1);
    }
}

void Vc1Video::ClampChromaSource(int& x, int& y) const
{
    if (m_profile != ProfileAdvanced)
    {
        x = std::clamp(x, -8, m_mbWidth * 8);
        y = std::clamp(y, -8, m_mbHeight * 8);
    }
    else
    {
        x = std::clamp(x, -8, m_codedWidth >> 1);
        y = std::clamp(y, -8, m_codedHeight >> 1);
    }
}

void Vc1Video::Mc1Mv(int dir)
{
    // ff_vc1_mc_1mv: the whole macroblock from one vector of direction dir
    Ref reference = Reference(dir);
    Picture& cur = *m_cur;
    int mx = Mv(dir, 0, 0), my = Mv(dir, 0, 1);
    if (m_pictType == TypeP)
        for (int i = 0; i < 4; i++)
        {
            Motion(cur, 1, m_blockIndex[size_t(i)], 0) = int16_t(mx);
            Motion(cur, 1, m_blockIndex[size_t(i)], 1) = int16_t(my);
        }
    int uvmx = (mx + ((mx & 3) == 3 ? 1 : 0)) >> 1;
    int uvmy = (my + ((my & 3) == 3 ? 1 : 0)) >> 1;
    m_lumaMv[size_t(MbPos()) * 2] = int16_t(uvmx);
    m_lumaMv[size_t(MbPos()) * 2 + 1] = int16_t(uvmy);
    if (m_fastuvmc != 0)
    {
        uvmx += uvmx < 0 ? uvmx & 1 : -(uvmx & 1);
        uvmy += uvmy < 0 ? uvmy & 1 : -(uvmy & 1);
    }
    int srcX = m_mbX * 16 + (mx >> 2), srcY = m_mbY * 16 + (my >> 2);
    int uvsrcX = m_mbX * 8 + (uvmx >> 2), uvsrcY = m_mbY * 8 + (uvmy >> 2);
    ClampLumaSource(srcX, srcY);
    ClampChromaSource(uvsrcX, uvsrcY);
    MpegFrame& f = cur.Frame;
    FetchLuma(reference.Pic->Frame, srcX, srcY, 17 + m_mspel * 2, reference.Luty, reference.UseIc);
    int src = m_mspel * (1 + EdgeStride);
    if (m_mspel != 0)
        MspelMc(f.Y.data(), m_destY, f.Stride, m_edgeY.data(), src, EdgeStride, mx & 3, my & 3, m_rnd, 16, false);
    else
        HpelMc(f.Y.data(), m_destY, f.Stride, m_edgeY.data(), src, EdgeStride, (my & 2) | ((mx & 2) >> 1), m_rnd == 0, 16, false);
    FetchChroma(reference.Pic->Frame, uvsrcX, uvsrcY, reference.Lutuv, reference.UseIc);
    uvmx = (uvmx & 3) << 1;
    uvmy = (uvmy & 3) << 1;
    ChromaMc(f.Cb.data(), m_destC, f.ChromaStride, m_edgeU.data(), uvmx, uvmy, m_rnd == 0, false);
    ChromaMc(f.Cr.data(), m_destC, f.ChromaStride, m_edgeV.data(), uvmx, uvmy, m_rnd == 0, false);
}

void Vc1Video::Mc4MvLuma(int n, int dir, bool avg)
{
    // ff_vc1_mc_4mv_luma: luma block n from its own vector
    Ref reference = Reference(dir);
    MpegFrame& f = m_cur->Frame;
    int mx = Mv(dir, n, 0), my = Mv(dir, n, 1);
    int off = f.Stride * 4 * (n & 2) + (n & 1) * 8;
    int srcX = m_mbX * 16 + (n & 1) * 8 + (mx >> 2);
    int srcY = m_mbY * 16 + (n & 2) * 4 + (my >> 2);
    ClampLumaSource(srcX, srcY);
    FetchLuma(reference.Pic->Frame, srcX, srcY, 9 + m_mspel * 2, reference.Luty, reference.UseIc);
    int src = m_mspel * (1 + EdgeStride);
    if (m_mspel != 0)
        MspelMc(f.Y.data(), m_destY + off, f.Stride, m_edgeY.data(), src, EdgeStride, mx & 3, my & 3, m_rnd, 8, avg);
    else
        HpelMc(f.Y.data(), m_destY + off, f.Stride, m_edgeY.data(), src, EdgeStride, (my & 2) | ((mx & 2) >> 1), m_rnd == 0, 8, false);
}

namespace {

constexpr uint8_t kPopcount4[] = {0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4};
constexpr uint8_t kChromaIndex2[] = {0, 0, 0, 0x01, 0, 0x02, 0x12, 0, 0, 0x03, 0x13, 0, 0x23, 0, 0, 0};

} // namespace

void Vc1Video::Mc4MvChroma(int dir)
{
    // ff_vc1_mc_4mv_chroma: both chroma blocks from the vector made of the inter luma blocks' ones
    Picture& cur = *m_cur;
    int idx = (MbType(m_blockIndex[0]) == 0 ? 1 : 0) | (MbType(m_blockIndex[1]) == 0 ? 2 : 0)
        | (MbType(m_blockIndex[2]) == 0 ? 4 : 0) | (MbType(m_blockIndex[3]) == 0 ? 8 : 0);
    int tx, ty;
    switch (kPopcount4[idx])
    {
    case 4:
        tx = Median4(Mv(dir, 0, 0), Mv(dir, 1, 0), Mv(dir, 2, 0), Mv(dir, 3, 0));
        ty = Median4(Mv(dir, 0, 1), Mv(dir, 1, 1), Mv(dir, 2, 1), Mv(dir, 3, 1));
        break;
    case 3:
    {
        int a = idx > 0xD ? 1 : 0, b = 1 + (idx > 0xB ? 1 : 0), c = 2 + (idx > 0x7 ? 1 : 0);
        tx = MidPred(Mv(dir, a, 0), Mv(dir, b, 0), Mv(dir, c, 0));
        ty = MidPred(Mv(dir, a, 1), Mv(dir, b, 1), Mv(dir, c, 1));
        break;
    }
    case 2:
    {
        int a = kChromaIndex2[idx] >> 4, b = kChromaIndex2[idx] & 0xF;
        tx = (Mv(dir, a, 0) + Mv(dir, b, 0)) / 2;
        ty = (Mv(dir, a, 1) + Mv(dir, b, 1)) / 2;
        break;
    }
    default:
        Motion(cur, 1, m_blockIndex[0], 0) = Motion(cur, 1, m_blockIndex[0], 1) = 0;
        m_lumaMv[size_t(MbPos()) * 2] = m_lumaMv[size_t(MbPos()) * 2 + 1] = 0;
        return;
    }
    tx = int16_t(tx);
    ty = int16_t(ty);
    Motion(cur, 1, m_blockIndex[0], 0) = int16_t(tx);
    Motion(cur, 1, m_blockIndex[0], 1) = int16_t(ty);
    int uvmx = (tx + ((tx & 3) == 3 ? 1 : 0)) >> 1;
    int uvmy = (ty + ((ty & 3) == 3 ? 1 : 0)) >> 1;
    m_lumaMv[size_t(MbPos()) * 2] = int16_t(uvmx);
    m_lumaMv[size_t(MbPos()) * 2 + 1] = int16_t(uvmy);
    if (m_fastuvmc != 0)
    {
        uvmx += uvmx < 0 ? uvmx & 1 : -(uvmx & 1);
        uvmy += uvmy < 0 ? uvmy & 1 : -(uvmy & 1);
    }
    int uvsrcX = m_mbX * 8 + (uvmx >> 2), uvsrcY = m_mbY * 8 + (uvmy >> 2);
    ClampChromaSource(uvsrcX, uvsrcY);
    Ref reference = Reference(dir);
    FetchChroma(reference.Pic->Frame, uvsrcX, uvsrcY, reference.Lutuv, reference.UseIc);
    MpegFrame& f = cur.Frame;
    uvmx = (uvmx & 3) << 1;
    uvmy = (uvmy & 3) << 1;
    ChromaMc(f.Cb.data(), m_destC, f.ChromaStride, m_edgeU.data(), uvmx, uvmy, m_rnd == 0, false);
    ChromaMc(f.Cr.data(), m_destC, f.ChromaStride, m_edgeV.data(), uvmx, uvmy, m_rnd == 0, false);
}

void Vc1Video::InterpMc()
{
    // ff_vc1_interp_mc: the backward prediction averaged into the forward one
    Ref reference = Reference(1);
    MpegFrame& f = m_cur->Frame;
    int mx = Mv(1, 0, 0), my = Mv(1, 0, 1);
    int uvmx = (mx + ((mx & 3) == 3 ? 1 : 0)) >> 1;
    int uvmy = (my + ((my & 3) == 3 ? 1 : 0)) >> 1;
    if (m_fastuvmc != 0)
    {
        // (as FFmpeg rounds here: away from zero)
        uvmx += uvmx < 0 ? -(uvmx & 1) : uvmx & 1;
        uvmy += uvmy < 0 ? -(uvmy & 1) : uvmy & 1;
    }
    int srcX = m_mbX * 16 + (mx >> 2), srcY = m_mbY * 16 + (my >> 2);
    int uvsrcX = m_mbX * 8 + (uvmx >> 2), uvsrcY = m_mbY * 8 + (uvmy >> 2);
    ClampLumaSource(srcX, srcY);
    ClampChromaSource(uvsrcX, uvsrcY);
    FetchLuma(reference.Pic->Frame, srcX, srcY, 17 + m_mspel * 2, reference.Luty, reference.UseIc);
    int src = m_mspel * (1 + EdgeStride);
    if (m_mspel != 0)
        MspelMc(f.Y.data(), m_destY, f.Stride, m_edgeY.data(), src, EdgeStride, mx & 3, my & 3, m_rnd, 16, true);
    else
        HpelMc(f.Y.data(), m_destY, f.Stride, m_edgeY.data(), src, EdgeStride, (my & 2) | ((mx & 2) >> 1), m_rnd == 0, 16, true);
    FetchChroma(reference.Pic->Frame, uvsrcX, uvsrcY, reference.Lutuv, reference.UseIc);
    uvmx = (uvmx & 3) << 1;
    uvmy = (uvmy & 3) << 1;
    ChromaMc(f.Cb.data(), m_destC, f.ChromaStride, m_edgeU.data(), uvmx, uvmy, m_rnd == 0, true);
    ChromaMc(f.Cr.data(), m_destC, f.ChromaStride, m_edgeV.data(), uvmx, uvmy, m_rnd == 0, true);
}

// ---- Interpolation ----

namespace {

int VerFilter(const uint8_t* src, int at, int stride, int mode)
{
    switch (mode)
    {
    case 1:
        return -4 * src[at - stride] + 53 * src[at] + 18 * src[at + stride] - 3 * src[at + 2 * stride];
    case 2:
        return -1 * src[at - stride] + 9 * src[at] + 9 * src[at + stride] - 1 * src[at + 2 * stride];
    case 3:
        return -3 * src[at - stride] + 18 * src[at] + 53 * src[at + stride] - 4 * src[at + 2 * stride];
    default:
        return 0;
    }
}

int HorFilter(const int16_t* src, int at, int mode)
{
    switch (mode)
    {
    case 1:
        return -4 * src[at - 1] + 53 * src[at] + 18 * src[at + 1] - 3 * src[at + 2];
    case 2:
        return -1 * src[at - 1] + 9 * src[at] + 9 * src[at + 1] - 1 * src[at + 2];
    case 3:
        return -3 * src[at - 1] + 18 * src[at] + 53 * src[at + 1] - 4 * src[at + 2];
    default:
        return 0;
    }
}

int MspelFilter(const uint8_t* src, int at, int stride, int mode, int r)
{
    switch (mode)
    {
    case 0:
        return src[at];
    case 1:
        return (-4 * src[at - stride] + 53 * src[at] + 18 * src[at + stride] - 3 * src[at + 2 * stride] + 32 - r) >> 6;
    case 2:
        return (-1 * src[at - stride] + 9 * src[at] + 9 * src[at + stride] - 1 * src[at + 2 * stride] + 8 - r) >> 4;
    default:
        return (-3 * src[at - stride] + 18 * src[at] + 53 * src[at + stride] - 4 * src[at + 2 * stride] + 32 - r) >> 6;
    }
}

constexpr int kShiftValue[] = {0, 5, 1, 5};

void Op(uint8_t* dst, int at, int value, bool avg)
{
    uint8_t v = uint8_t(value < 0 ? 0 : value > 255 ? 255 : value);
    dst[at] = avg ? uint8_t((dst[at] + v + 1) >> 1) : v;
}

} // namespace

void Vc1Video::MspelMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int s, int srcStride, int hmode, int vmode, int rnd, int size, bool avg)
{
    // vc1_mspel_mc: bicubic interpolation of a size x size block (put or average)
    if (vmode != 0)
    {
        if (hmode != 0)
        {
            int shift = (kShiftValue[hmode] + kShiftValue[vmode]) >> 1;
            int r = (1 << (shift - 1)) + rnd - 1;
            int w = size + 3;
            int16_t* tmp = m_mspelTemp.data();
            for (int j = 0; j < size; j++)
                for (int i = 0; i < w; i++)
                    tmp[j * w + i] = int16_t((VerFilter(src, s - 1 + j * srcStride + i, srcStride, vmode) + r) >> shift);
            r = 64 - rnd;
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                    Op(dst, d + j * dstStride + i, (HorFilter(tmp, j * w + 1 + i, hmode) + r) >> 7, avg);
            return;
        }
        int rv = 1 - rnd;
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
                Op(dst, d + j * dstStride + i, MspelFilter(src, s + j * srcStride + i, srcStride, vmode, rv), avg);
        return;
    }
    for (int j = 0; j < size; j++)
        for (int i = 0; i < size; i++)
            Op(dst, d + j * dstStride + i, MspelFilter(src, s + j * srcStride + i, 1, hmode, rnd), avg);
}

void Vc1Video::HpelMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int s, int srcStride, int dxy, bool round, int size, bool avg)
{
    // hpeldsp: half-sample bilinear interpolation (dxy: bit 0 x, bit 1 y), rounding or not, put or average
    int r2 = round ? 1 : 0, r4 = round ? 2 : 1;
    for (int j = 0; j < size; j++)
    {
        int a = s + j * srcStride, o = d + j * dstStride;
        for (int i = 0; i < size; i++)
        {
            int v;
            switch (dxy)
            {
            case 0:
                v = src[a + i];
                break;
            case 1:
                v = (src[a + i] + src[a + i + 1] + r2) >> 1;
                break;
            case 2:
                v = (src[a + i] + src[a + i + srcStride] + r2) >> 1;
                break;
            default:
                v = (src[a + i] + src[a + i + 1] + src[a + i + srcStride] + src[a + i + srcStride + 1] + r4) >> 2;
                break;
            }
            dst[o + i] = avg ? uint8_t((dst[o + i] + v + 1) >> 1) : uint8_t(v);
        }
    }
}

void Vc1Video::ChromaMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int x, int y, bool round, bool avg)
{
    // 8x8 chroma bilinear interpolation in eighths (h264chroma when rounding, vc1dsp's no_rnd one otherwise)
    int a = (8 - x) * (8 - y), b = x * (8 - y), c = (8 - x) * y, e = x * y;
    int bias = round ? 32 : 28;
    for (int j = 0; j < 8; j++)
    {
        int s = j * ChromaEdgeStride, o = d + j * dstStride;
        for (int i = 0; i < 8; i++)
        {
            int v = (a * src[s + i] + b * src[s + i + 1] + c * src[s + i + ChromaEdgeStride] + e * src[s + i + ChromaEdgeStride + 1] + bias) >> 6;
            dst[o + i] = avg ? uint8_t((dst[o + i] + v + 1) >> 1) : uint8_t(v);
        }
    }
}

} // namespace openshiina::formats
