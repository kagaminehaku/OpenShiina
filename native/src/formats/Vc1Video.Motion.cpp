// C# Formats/Vc1Video.Motion.cs: VC-1 motion vectors and motion compensation (FFmpeg vc1_pred.c,
// vc1_mc.c and the bicubic, bilinear and chroma interpolation of vc1dsp.c / hpeldsp / h264chroma). A
// reference block is always copied with its coordinates clamped to the picture (FFmpeg's edge
// emulation, also what its padded pictures give) before it is interpolated.
#include <algorithm>
#include <array>
#include <cstdlib>
#include <cstring>
#include <format>
#include <random>
#include <utility>

#if defined(__aarch64__)
#include <arm_neon.h>
#endif

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

// MspelMc's kinds of block each written out (the filters' modes, the block's size, put or average)
// with the same sums: loops of constants the compiler makes vector code of (NEON on the Switch),
// where one loop for all of them chose the filter for every sample

/// The four taps of a mode (1: 1/4, 2: 1/2, 3: 3/4) over p[-step], p[0], p[step], p[2 step].
template <int Mode, typename T>
inline int Taps(const T* p, int step)
{
    if constexpr (Mode == 1)
        return -4 * p[-step] + 53 * p[0] + 18 * p[step] - 3 * p[2 * step];
    else if constexpr (Mode == 2)
        return -1 * p[-step] + 9 * p[0] + 9 * p[step] - 1 * p[2 * step];
    else
        return -3 * p[-step] + 18 * p[0] + 53 * p[step] - 4 * p[2 * step];
}

template <bool Avg>
inline void Put(uint8_t* d, int value)
{
    uint8_t v = uint8_t(value < 0 ? 0 : value > 255 ? 255 : value);
    *d = Avg ? uint8_t((*d + v + 1) >> 1) : v;
}

template <int H, int V, int Size, bool Avg>
void Mspel(uint8_t* dst, int dstStride, const uint8_t* src, int srcStride, int rnd, int16_t* tmp)
{
    if constexpr (V != 0 && H != 0)
    {
        constexpr int shift = (kShiftValue[H] + kShiftValue[V]) >> 1;
        constexpr int w = Size + 3;
        const int r = (1 << (shift - 1)) + rnd - 1;
        for (int j = 0; j < Size; j++)
            for (int i = 0; i < w; i++)
                tmp[j * w + i] = int16_t((Taps<V>(src - 1 + j * srcStride + i, srcStride) + r) >> shift);
        const int r2 = 64 - rnd;
        for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
                Put<Avg>(dst + j * dstStride + i, (Taps<H>(tmp + j * w + 1 + i, 1) + r2) >> 7);
    }
    else if constexpr (V != 0)
    {
        constexpr int shift = V == 2 ? 4 : 6;
        const int r = (V == 2 ? 8 : 32) - (1 - rnd);
        for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
                Put<Avg>(dst + j * dstStride + i, (Taps<V>(src + j * srcStride + i, srcStride) + r) >> shift);
    }
    else if constexpr (H != 0)
    {
        constexpr int shift = H == 2 ? 4 : 6;
        const int r = (H == 2 ? 8 : 32) - rnd;
        for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
                Put<Avg>(dst + j * dstStride + i, (Taps<H>(src + j * srcStride + i, 1) + r) >> shift);
    }
    else
    {
        for (int j = 0; j < Size; j++)
            for (int i = 0; i < Size; i++)
                Put<Avg>(dst + j * dstStride + i, src[j * srcStride + i]);
    }
}

using MspelKind = void (*)(uint8_t*, int, const uint8_t*, int, int, int16_t*);

/// The kinds of a size, put or average, by hmode + 4 vmode.
template <int Size, bool Avg, int... K>
constexpr std::array<MspelKind, 16> MspelKinds(std::integer_sequence<int, K...>)
{
    return {&Mspel<(K & 3), (K >> 2), Size, Avg>...};
}

constexpr auto kMspel8 = MspelKinds<8, false>(std::make_integer_sequence<int, 16>{});
constexpr auto kMspel8Avg = MspelKinds<8, true>(std::make_integer_sequence<int, 16>{});
constexpr auto kMspel16 = MspelKinds<16, false>(std::make_integer_sequence<int, 16>{});
constexpr auto kMspel16Avg = MspelKinds<16, true>(std::make_integer_sequence<int, 16>{});

#if defined(__aarch64__)
// The kinds GCC leaves as they are (a filter along one axis, or none: blocks of 8 bytes) in NEON. A
// filter's four taps of bytes lie in -1785..18105, so 16-bit lanes hold them, and the sum's
// rounding, arithmetic shift and saturation to 0..255 (vqmovun) are the scalar code's
// (Vc1Video::CheckVectorLanes compares them on the console).

template <int Mode>
inline int16x8_t TapsNeon(uint8x8_t before, uint8x8_t at, uint8x8_t next, uint8x8_t after)
{
    constexpr uint8_t c0 = Mode == 1 ? 4 : Mode == 2 ? 1 : 3, c1 = Mode == 1 ? 53 : Mode == 2 ? 9 : 18;
    constexpr uint8_t c2 = Mode == 1 ? 18 : Mode == 2 ? 9 : 53, c3 = Mode == 1 ? 3 : Mode == 2 ? 1 : 4;
    uint16x8_t sum = vmull_u8(at, vdup_n_u8(c1));
    sum = vmlal_u8(sum, next, vdup_n_u8(c2));
    sum = vmlsl_u8(sum, before, vdup_n_u8(c0));
    sum = vmlsl_u8(sum, after, vdup_n_u8(c3));
    return vreinterpretq_s16_u16(sum);
}

/// Eight samples of a one-axis filter at p (taps step apart), rounded by r.
template <int Mode>
inline uint8x8_t Filter8(const uint8_t* p, int step, int16x8_t r)
{
    constexpr int Shift = Mode == 2 ? 4 : 6;
    int16x8_t sum = vaddq_s16(TapsNeon<Mode>(vld1_u8(p - step), vld1_u8(p), vld1_u8(p + step), vld1_u8(p + 2 * step)), r);
    return vqmovun_s16(vshrq_n_s16(sum, Shift));
}

template <bool Avg>
inline void Store8(uint8_t* d, uint8x8_t v)
{
    vst1_u8(d, Avg ? vrhadd_u8(vld1_u8(d), v) : v);
}

/// A block filtered along one axis (Vertical: the taps a row apart), or copied (Mode 0).
template <int Mode, bool Vertical, int Size, bool Avg>
void MspelAxisNeon(uint8_t* dst, int dstStride, const uint8_t* src, int srcStride, int rnd)
{
    const int step = Vertical ? srcStride : 1;
    const int16x8_t r = vdupq_n_s16(int16_t((Mode == 2 ? 8 : 32) - (Vertical ? 1 - rnd : rnd)));
    for (int j = 0; j < Size; j++, dst += dstStride, src += srcStride)
        for (int i = 0; i < Size; i += 8)
        {
            if constexpr (Mode == 0)
                Store8<Avg>(dst + i, vld1_u8(src + i));
            else
                Store8<Avg>(dst + i, Filter8<Mode>(src + i, step, r));
        }
}

using MspelNeonKind = void (*)(uint8_t*, int, const uint8_t*, int, int);

template <int Size, bool Avg>
constexpr std::array<MspelNeonKind, 7> MspelNeonKinds()
{
    // 0: a copy; 1-3: horizontal modes; 4-6: vertical modes
    return {&MspelAxisNeon<0, false, Size, Avg>, &MspelAxisNeon<1, false, Size, Avg>, &MspelAxisNeon<2, false, Size, Avg>,
            &MspelAxisNeon<3, false, Size, Avg>, &MspelAxisNeon<1, true, Size, Avg>,  &MspelAxisNeon<2, true, Size, Avg>,
            &MspelAxisNeon<3, true, Size, Avg>};
}

constexpr auto kNeon8 = MspelNeonKinds<8, false>(), kNeon8Avg = MspelNeonKinds<8, true>();
constexpr auto kNeon16 = MspelNeonKinds<16, false>(), kNeon16Avg = MspelNeonKinds<16, true>();

/// The NEON kind of a block, or null for one of both axes (GCC's vector code then).
MspelNeonKind MspelNeon(int hmode, int vmode, int size, bool avg)
{
    if ((hmode != 0 && vmode != 0) || (size != 8 && size != 16))
        return nullptr;
    const auto& kinds = size == 8 ? (avg ? kNeon8Avg : kNeon8) : (avg ? kNeon16Avg : kNeon16);
    return kinds[size_t(vmode != 0 ? 3 + vmode : hmode)];
}
#endif

} // namespace

void Vc1Video::MspelMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int s, int srcStride, int hmode, int vmode, int rnd, int size, bool avg)
{
    // vc1_mspel_mc: bicubic interpolation of a size x size block (put or average)
#if defined(__aarch64__)
    if (MspelNeonKind kind = MspelNeon(hmode, vmode, size, avg))
    {
        kind(dst + d, dstStride, src + s, srcStride, rnd);
        return;
    }
#endif
    if (size == 8 || size == 16)
    {
        const auto& kinds = size == 8 ? (avg ? kMspel8Avg : kMspel8) : (avg ? kMspel16Avg : kMspel16);
        kinds[size_t(hmode + 4 * vmode)](dst + d, dstStride, src + s, srcStride, rnd, m_mspelTemp.data());
        return;
    }
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

namespace {

/// ChromaMc put or averaged (a loop of eight the compiler makes vector code of).
template <bool Avg>
void Chroma8(uint8_t* dst, int dstStride, const uint8_t* src, int stride, int a, int b, int c, int e, int bias)
{
    for (int j = 0; j < 8; j++, dst += dstStride, src += stride)
        for (int i = 0; i < 8; i++)
        {
            int v = (a * src[i] + b * src[i + 1] + c * src[i + stride] + e * src[i + stride + 1] + bias) >> 6;
            dst[i] = Avg ? uint8_t((dst[i] + v + 1) >> 1) : uint8_t(v);
        }
}

#if defined(__aarch64__)
/// Chroma8 in NEON: the weights are at most 64 and their sum 64, so a sum of bytes times them holds
/// in 16 bits and its >> 6 in a byte, as the scalar code's.
template <bool Avg>
void Chroma8Neon(uint8_t* dst, int dstStride, const uint8_t* src, int stride, int a, int b, int c, int e, int bias)
{
    uint8x8_t wa = vdup_n_u8(uint8_t(a)), wb = vdup_n_u8(uint8_t(b)), wc = vdup_n_u8(uint8_t(c)), we = vdup_n_u8(uint8_t(e));
    uint16x8_t round = vdupq_n_u16(uint16_t(bias));
    for (int j = 0; j < 8; j++, dst += dstStride, src += stride)
    {
        uint16x8_t sum = vmlal_u8(round, vld1_u8(src), wa);
        sum = vmlal_u8(sum, vld1_u8(src + 1), wb);
        sum = vmlal_u8(sum, vld1_u8(src + stride), wc);
        sum = vmlal_u8(sum, vld1_u8(src + stride + 1), we);
        Store8<Avg>(dst, vshrn_n_u16(sum, 6));
    }
}
#endif

} // namespace

void Vc1Video::ChromaMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int x, int y, bool round, bool avg)
{
    // 8x8 chroma bilinear interpolation in eighths (h264chroma when rounding, vc1dsp's no_rnd one otherwise)
    int a = (8 - x) * (8 - y), b = x * (8 - y), c = (8 - x) * y, e = x * y;
    int bias = round ? 32 : 28;
#if defined(__aarch64__)
    if (avg)
        Chroma8Neon<true>(dst + d, dstStride, src, ChromaEdgeStride, a, b, c, e, bias);
    else
        Chroma8Neon<false>(dst + d, dstStride, src, ChromaEdgeStride, a, b, c, e, bias);
#else
    if (avg)
        Chroma8<true>(dst + d, dstStride, src, ChromaEdgeStride, a, b, c, e, bias);
    else
        Chroma8<false>(dst + d, dstStride, src, ChromaEdgeStride, a, b, c, e, bias);
#endif
}

std::string Vc1Video::CheckVectorLanes()
{
#if defined(__aarch64__)
    // The NEON kinds against the scalar ones on random blocks, every mode, size, rounding and average
    std::mt19937 random(12345);
    constexpr int Stride = 48;
    std::array<uint8_t, Stride * Stride> source{}, before{}, neon{}, scalar{};
    std::array<int16_t, 19 * 16> tmp{};
    int differed = 0;
    std::string report;
    for (int round = 0; round < 2000; round++)
    {
        for (auto& v : source)
            v = uint8_t(random());
        for (auto& v : before)
            v = uint8_t(random());
        const uint8_t* src = source.data() + 4 * Stride + 4;
        for (int size : {8, 16})
            for (bool avg : {false, true})
                for (int rnd : {0, 1})
                    for (int mode = 0; mode < 7; mode++)
                    {
                        int hmode = mode < 4 ? mode : 0, vmode = mode < 4 ? 0 : mode - 3;
                        neon = before;
                        scalar = before;
                        MspelNeon(hmode, vmode, size, avg)(neon.data(), Stride, src, Stride, rnd);
                        const auto& kinds = size == 8 ? (avg ? kMspel8Avg : kMspel8) : (avg ? kMspel16Avg : kMspel16);
                        kinds[size_t(hmode + 4 * vmode)](scalar.data(), Stride, src, Stride, rnd, tmp.data());
                        if (neon != scalar && differed++ < 3)
                            report += std::format(" mspel h{} v{} size {} avg {} rnd {};", hmode, vmode, size, avg, rnd);
                    }
        int x = int(random() % 8), y = int(random() % 8), bias = random() % 2 != 0 ? 32 : 28;
        int a = (8 - x) * (8 - y), b = x * (8 - y), c = (8 - x) * y, e = x * y;
        for (bool avg : {false, true})
        {
            neon = before;
            scalar = before;
            if (avg)
            {
                Chroma8Neon<true>(neon.data(), Stride, src, Stride, a, b, c, e, bias);
                Chroma8<true>(scalar.data(), Stride, src, Stride, a, b, c, e, bias);
            }
            else
            {
                Chroma8Neon<false>(neon.data(), Stride, src, Stride, a, b, c, e, bias);
                Chroma8<false>(scalar.data(), Stride, src, Stride, a, b, c, e, bias);
            }
            if (neon != scalar && differed++ < 3)
                report += std::format(" chroma x{} y{} avg {};", x, y, avg);
        }
    }
    std::string transforms = CheckTransformLanes();
    if (differed == 0 && transforms.empty())
        return {};
    return std::format("{} blocks differed:", differed) + report + transforms;
#else
    return {};
#endif
}

} // namespace openshiina::formats
