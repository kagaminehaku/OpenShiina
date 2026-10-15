// VC-1 motion vectors and motion compensation (FFmpeg vc1_pred.c, vc1_mc.c and the bicubic,
// bilinear and chroma interpolation of vc1dsp.c / hpeldsp / h264chroma). A reference block is
// always copied with its coordinates clamped to the picture (FFmpeg's edge emulation, also what
// its padded pictures give) before it is interpolated.

namespace OpenShiina.Formats;

public sealed partial class Vc1Video
{
    /// <summary>s->mv[dir][n][c]: the motion vectors of the current macroblock's blocks.</summary>
    private readonly int[] m_mv = new int[2 * 4 * 2];

    private ref int Mv(int dir, int n, int c) => ref m_mv[(dir * 4 + n) * 2 + c];

    private const int EdgeStride = 32, ChromaEdgeStride = 16;
    private readonly byte[] m_edgeY = new byte[EdgeStride * EdgeStride];
    private readonly byte[] m_edgeU = new byte[ChromaEdgeStride * ChromaEdgeStride];
    private readonly byte[] m_edgeV = new byte[ChromaEdgeStride * ChromaEdgeStride];
    private readonly short[] m_mspelTemp = new short[19 * 16];

    private static int MidPred(int a, int b, int c)
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

    private static int Median4(int a, int b, int c, int d)
    {
        if (a < b)
            return c < d ? (Math.Min(b, d) + Math.Max(a, c)) / 2 : (Math.Min(b, c) + Math.Max(a, d)) / 2;
        return c < d ? (Math.Min(a, d) + Math.Max(b, c)) / 2 : (Math.Min(a, c) + Math.Max(b, d)) / 2;
    }

    #region Prediction

    /// <summary>ff_vc1_pred_mv for progressive pictures: predicts block n's vector and adds the differential.</summary>
    private void PredMv(int n, int dmvX, int dmvY, bool mv1, int rX, int rY, int dir)
    {
        var cur = m_cur;
        if (m_quarterSample == 0)
        {
            dmvX *= 2;
            dmvY *= 2;
        }
        int wrap = m_b8Stride, xy = m_blockIndex[n];
        if (m_mbIntra != 0)
        {
            Mv(0, n, 0) = Motion(cur, 0, xy, 0) = 0;
            Mv(0, n, 1) = Motion(cur, 0, xy, 1) = 0;
            Motion(cur, 1, xy, 0) = Motion(cur, 1, xy, 1) = 0;
            if (mv1)
            {
                foreach (int at in (ReadOnlySpan<int>)[xy + 1, xy + wrap, xy + wrap + 1])
                {
                    Motion(cur, 0, at, 0) = Motion(cur, 0, at, 1) = 0;
                    Motion(cur, 1, at, 0) = Motion(cur, 1, at, 1) = 0;
                }
                m_lumaMv[MbPos * 2] = m_lumaMv[MbPos * 2 + 1] = 0;
            }
            return;
        }
        bool aValid = !m_firstSliceLine || n is 2 or 3;
        bool bValid = aValid;
        bool cValid = m_mbX != 0 || n is 1 or 3;
        int off;
        if (mv1)
        {
            off = m_mbX == m_mbWidth - 1 ? -1 : 2;
            bValid = bValid && m_mbWidth > 1;
        }
        else
        {
            off = n switch
            {
                0 => m_resRtmFlag != 0 ? (m_mbX != 0 ? -1 : 1) : (m_mbX != 0 ? -1 : 2 * m_mbWidth - wrap - 1),
                1 => m_mbX == m_mbWidth - 1 ? -1 : 1,
                2 => 1,
                _ => -1,
            };
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
            int qx = (m_mbX << 6) + (n is 1 or 3 ? 32 : 0);
            int qy = (m_mbY << 6) + (n is 2 or 3 ? 32 : 0);
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
            int sum = MbType(xy - wrap) != 0 ? Math.Abs(px) + Math.Abs(py) : Math.Abs(px - ax) + Math.Abs(py - ay);
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
                sum = MbType(xy - 1) != 0 ? Math.Abs(px) + Math.Abs(py) : Math.Abs(px - cx) + Math.Abs(py - cy);
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
        Motion(cur, dir, xy, 0) = (short)mx;
        Motion(cur, dir, xy, 1) = (short)my;
        if (mv1)
            foreach (int at in (ReadOnlySpan<int>)[xy + 1, xy + wrap, xy + wrap + 1])
            {
                Motion(cur, dir, at, 0) = (short)mx;
                Motion(cur, dir, at, 1) = (short)my;
            }
    }

    private static int ScaleMv(int value, int bfrac, int inv, int qs)
    {
        int n = bfrac;
        if (inv != 0)
            n -= 256;
        if (qs == 0)
            return 2 * ((value * n + 255) >> 9);
        return (value * n + 128) >> 8;
    }

    /// <summary>ff_vc1_pred_b_mv: the forward and backward vectors of a B macroblock.</summary>
    private void PredBMv(Span<int> dmvX, Span<int> dmvY, bool direct, int mvtype)
    {
        var cur = m_cur;
        var next = m_nextPicture!;
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
        Mv(0, 0, 0) = Math.Clamp(Mv(0, 0, 0), minX, maxX);
        Mv(0, 0, 1) = Math.Clamp(Mv(0, 0, 1), minY, maxY);
        Mv(1, 0, 0) = Math.Clamp(Mv(1, 0, 0), minX, maxX);
        Mv(1, 0, 1) = Math.Clamp(Mv(1, 0, 1), minY, maxY);
        if (direct)
        {
            Motion(cur, 0, xy, 0) = (short)Mv(0, 0, 0);
            Motion(cur, 0, xy, 1) = (short)Mv(0, 0, 1);
            Motion(cur, 1, xy, 0) = (short)Mv(1, 0, 0);
            Motion(cur, 1, xy, 1) = (short)Mv(1, 0, 1);
            return;
        }
        for (int dir = 0; dir < 2; dir++)
        {
            if (dir == 0 && mvtype is not (BmvForward or BmvInterpolated))
                continue;
            if (dir == 1 && mvtype is not (BmvBackward or BmvInterpolated))
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
        Motion(cur, 0, xy, 0) = (short)Mv(0, 0, 0);
        Motion(cur, 0, xy, 1) = (short)Mv(0, 0, 1);
        Motion(cur, 1, xy, 0) = (short)Mv(1, 0, 0);
        Motion(cur, 1, xy, 1) = (short)Mv(1, 0, 1);
    }

    #endregion

    #region Compensation

    /// <summary>vc1_b_mc.</summary>
    private void BMc(bool direct, int mode)
    {
        if (direct || mode == BmvInterpolated)
        {
            Mc1Mv(0);
            InterpMc();
            return;
        }
        Mc1Mv(mode == BmvBackward ? 1 : 0);
    }

    /// <summary>A block of a plane with its coordinates clamped to the picture.</summary>
    private static void Fetch(byte[] plane, int stride, int width, int height, int x, int y, int w, int h, byte[] dst, int dstStride)
    {
        if (x >= 0 && y >= 0 && x + w <= width && y + h <= height)
        {
            for (int j = 0; j < h; j++)
                Buffer.BlockCopy(plane, (y + j) * stride + x, dst, j * dstStride, w);
            return;
        }
        for (int j = 0; j < h; j++)
        {
            int row = Math.Clamp(y + j, 0, height - 1) * stride;
            for (int i = 0; i < w; i++)
                dst[j * dstStride + i] = plane[row + Math.Clamp(x + i, 0, width - 1)];
        }
    }

    private static void ApplyLut(byte[] block, int stride, int size, byte[] lut)
    {
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
                block[j * stride + i] = lut[block[j * stride + i]];
    }

    /// <summary>The reference of a direction: the last (0) or the next (1) anchor, and its intensity tables.</summary>
    private (Picture Picture, byte[] Luty, byte[] Lutuv, bool UseIc) Reference(int dir) =>
        dir == 0 ? (m_last!, m_lastLuty, m_lastLutuv, m_lastUseIc != 0) : (m_nextPicture!, m_nextLuty, m_nextLutuv, m_nextUseIc != 0);

    /// <summary>The luma source of a block: copied into m_edgeY from (x - mspel, y - mspel), intensity compensated.</summary>
    private void FetchLuma(MpegFrame frame, int x, int y, int k, byte[] luty, bool useIc)
    {
        Fetch(frame.Y, frame.Stride, m_codedWidth, m_codedHeight, x - m_mspel, y - m_mspel, k, k, m_edgeY, EdgeStride);
        if (useIc)
            ApplyLut(m_edgeY, EdgeStride, k, luty);
    }

    private void FetchChroma(MpegFrame frame, int x, int y, byte[] lutuv, bool useIc)
    {
        int w = m_codedWidth >> 1, h = m_codedHeight >> 1;
        Fetch(frame.Cb, frame.ChromaStride, w, h, x, y, 9, 9, m_edgeU, ChromaEdgeStride);
        Fetch(frame.Cr, frame.ChromaStride, w, h, x, y, 9, 9, m_edgeV, ChromaEdgeStride);
        if (useIc)
        {
            ApplyLut(m_edgeU, ChromaEdgeStride, 9, lutuv);
            ApplyLut(m_edgeV, ChromaEdgeStride, 9, lutuv);
        }
    }

    private void ClampLumaSource(ref int x, ref int y)
    {
        if (m_profile != ProfileAdvanced)
        {
            x = Math.Clamp(x, -16, m_mbWidth * 16);
            y = Math.Clamp(y, -16, m_mbHeight * 16);
        }
        else
        {
            x = Math.Clamp(x, -17, m_codedWidth);
            y = Math.Clamp(y, -18, m_codedHeight + 1);
        }
    }

    private void ClampChromaSource(ref int x, ref int y)
    {
        if (m_profile != ProfileAdvanced)
        {
            x = Math.Clamp(x, -8, m_mbWidth * 8);
            y = Math.Clamp(y, -8, m_mbHeight * 8);
        }
        else
        {
            x = Math.Clamp(x, -8, m_codedWidth >> 1);
            y = Math.Clamp(y, -8, m_codedHeight >> 1);
        }
    }

    /// <summary>ff_vc1_mc_1mv: the whole macroblock from one vector of direction <paramref name="dir"/>.</summary>
    private void Mc1Mv(int dir)
    {
        var (reference, luty, lutuv, useIc) = Reference(dir);
        var cur = m_cur;
        int mx = Mv(dir, 0, 0), my = Mv(dir, 0, 1);
        if (m_pictType == TypeP)
            for (int i = 0; i < 4; i++)
            {
                Motion(cur, 1, m_blockIndex[i], 0) = (short)mx;
                Motion(cur, 1, m_blockIndex[i], 1) = (short)my;
            }
        int uvmx = (mx + ((mx & 3) == 3 ? 1 : 0)) >> 1;
        int uvmy = (my + ((my & 3) == 3 ? 1 : 0)) >> 1;
        m_lumaMv[MbPos * 2] = (short)uvmx;
        m_lumaMv[MbPos * 2 + 1] = (short)uvmy;
        if (m_fastuvmc != 0)
        {
            uvmx += uvmx < 0 ? uvmx & 1 : -(uvmx & 1);
            uvmy += uvmy < 0 ? uvmy & 1 : -(uvmy & 1);
        }
        int srcX = m_mbX * 16 + (mx >> 2), srcY = m_mbY * 16 + (my >> 2);
        int uvsrcX = m_mbX * 8 + (uvmx >> 2), uvsrcY = m_mbY * 8 + (uvmy >> 2);
        ClampLumaSource(ref srcX, ref srcY);
        ClampChromaSource(ref uvsrcX, ref uvsrcY);
        var f = cur.Frame;
        FetchLuma(reference.Frame, srcX, srcY, 17 + m_mspel * 2, luty, useIc);
        int src = m_mspel * (1 + EdgeStride);
        if (m_mspel != 0)
            MspelMc(f.Y, m_destY, f.Stride, m_edgeY, src, EdgeStride, mx & 3, my & 3, m_rnd, 16, false);
        else
            HpelMc(f.Y, m_destY, f.Stride, m_edgeY, src, EdgeStride, (my & 2) | ((mx & 2) >> 1), m_rnd == 0, 16, false);
        FetchChroma(reference.Frame, uvsrcX, uvsrcY, lutuv, useIc);
        uvmx = (uvmx & 3) << 1;
        uvmy = (uvmy & 3) << 1;
        ChromaMc(f.Cb, m_destC, f.ChromaStride, m_edgeU, uvmx, uvmy, m_rnd == 0, false);
        ChromaMc(f.Cr, m_destC, f.ChromaStride, m_edgeV, uvmx, uvmy, m_rnd == 0, false);
    }

    /// <summary>ff_vc1_mc_4mv_luma: luma block n from its own vector.</summary>
    private void Mc4MvLuma(int n, int dir, bool avg)
    {
        var (reference, luty, _, useIc) = Reference(dir);
        var f = m_cur.Frame;
        int mx = Mv(dir, n, 0), my = Mv(dir, n, 1);
        int off = f.Stride * 4 * (n & 2) + (n & 1) * 8;
        int srcX = m_mbX * 16 + (n & 1) * 8 + (mx >> 2);
        int srcY = m_mbY * 16 + (n & 2) * 4 + (my >> 2);
        ClampLumaSource(ref srcX, ref srcY);
        FetchLuma(reference.Frame, srcX, srcY, 9 + m_mspel * 2, luty, useIc);
        int src = m_mspel * (1 + EdgeStride);
        if (m_mspel != 0)
            MspelMc(f.Y, m_destY + off, f.Stride, m_edgeY, src, EdgeStride, mx & 3, my & 3, m_rnd, 8, avg);
        else
            HpelMc(f.Y, m_destY + off, f.Stride, m_edgeY, src, EdgeStride, (my & 2) | ((mx & 2) >> 1), m_rnd == 0, 8, false);
    }

    private static readonly byte[] s_popcount4 = [0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4];
    private static readonly byte[] s_chromaIndex2 = [0, 0, 0, 0x01, 0, 0x02, 0x12, 0, 0, 0x03, 0x13, 0, 0x23, 0, 0, 0];

    /// <summary>ff_vc1_mc_4mv_chroma: both chroma blocks from the vector made of the inter luma blocks' ones.</summary>
    private void Mc4MvChroma(int dir)
    {
        var cur = m_cur;
        int idx = (MbType(m_blockIndex[0]) == 0 ? 1 : 0) | (MbType(m_blockIndex[1]) == 0 ? 2 : 0)
            | (MbType(m_blockIndex[2]) == 0 ? 4 : 0) | (MbType(m_blockIndex[3]) == 0 ? 8 : 0);
        int tx, ty;
        switch (s_popcount4[idx])
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
                int a = s_chromaIndex2[idx] >> 4, b = s_chromaIndex2[idx] & 0xF;
                tx = (Mv(dir, a, 0) + Mv(dir, b, 0)) / 2;
                ty = (Mv(dir, a, 1) + Mv(dir, b, 1)) / 2;
                break;
            }
            default:
                Motion(cur, 1, m_blockIndex[0], 0) = Motion(cur, 1, m_blockIndex[0], 1) = 0;
                m_lumaMv[MbPos * 2] = m_lumaMv[MbPos * 2 + 1] = 0;
                return;
        }
        tx = (short)tx;
        ty = (short)ty;
        Motion(cur, 1, m_blockIndex[0], 0) = (short)tx;
        Motion(cur, 1, m_blockIndex[0], 1) = (short)ty;
        int uvmx = (tx + ((tx & 3) == 3 ? 1 : 0)) >> 1;
        int uvmy = (ty + ((ty & 3) == 3 ? 1 : 0)) >> 1;
        m_lumaMv[MbPos * 2] = (short)uvmx;
        m_lumaMv[MbPos * 2 + 1] = (short)uvmy;
        if (m_fastuvmc != 0)
        {
            uvmx += uvmx < 0 ? uvmx & 1 : -(uvmx & 1);
            uvmy += uvmy < 0 ? uvmy & 1 : -(uvmy & 1);
        }
        int uvsrcX = m_mbX * 8 + (uvmx >> 2), uvsrcY = m_mbY * 8 + (uvmy >> 2);
        ClampChromaSource(ref uvsrcX, ref uvsrcY);
        var (reference, _, lutuv, useIc) = Reference(dir);
        FetchChroma(reference.Frame, uvsrcX, uvsrcY, lutuv, useIc);
        var f = cur.Frame;
        uvmx = (uvmx & 3) << 1;
        uvmy = (uvmy & 3) << 1;
        ChromaMc(f.Cb, m_destC, f.ChromaStride, m_edgeU, uvmx, uvmy, m_rnd == 0, false);
        ChromaMc(f.Cr, m_destC, f.ChromaStride, m_edgeV, uvmx, uvmy, m_rnd == 0, false);
    }

    /// <summary>ff_vc1_interp_mc: the backward prediction averaged into the forward one.</summary>
    private void InterpMc()
    {
        var (reference, luty, lutuv, useIc) = Reference(1);
        var f = m_cur.Frame;
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
        ClampLumaSource(ref srcX, ref srcY);
        ClampChromaSource(ref uvsrcX, ref uvsrcY);
        FetchLuma(reference.Frame, srcX, srcY, 17 + m_mspel * 2, luty, useIc);
        int src = m_mspel * (1 + EdgeStride);
        if (m_mspel != 0)
            MspelMc(f.Y, m_destY, f.Stride, m_edgeY, src, EdgeStride, mx & 3, my & 3, m_rnd, 16, true);
        else
            HpelMc(f.Y, m_destY, f.Stride, m_edgeY, src, EdgeStride, (my & 2) | ((mx & 2) >> 1), m_rnd == 0, 16, true);
        FetchChroma(reference.Frame, uvsrcX, uvsrcY, lutuv, useIc);
        uvmx = (uvmx & 3) << 1;
        uvmy = (uvmy & 3) << 1;
        ChromaMc(f.Cb, m_destC, f.ChromaStride, m_edgeU, uvmx, uvmy, m_rnd == 0, true);
        ChromaMc(f.Cr, m_destC, f.ChromaStride, m_edgeV, uvmx, uvmy, m_rnd == 0, true);
    }

    #endregion

    #region Interpolation

    private static int VerFilter(byte[] src, int at, int stride, int mode) => mode switch
    {
        1 => -4 * src[at - stride] + 53 * src[at] + 18 * src[at + stride] - 3 * src[at + 2 * stride],
        2 => -1 * src[at - stride] + 9 * src[at] + 9 * src[at + stride] - 1 * src[at + 2 * stride],
        3 => -3 * src[at - stride] + 18 * src[at] + 53 * src[at + stride] - 4 * src[at + 2 * stride],
        _ => 0,
    };

    private static int HorFilter(short[] src, int at, int mode) => mode switch
    {
        1 => -4 * src[at - 1] + 53 * src[at] + 18 * src[at + 1] - 3 * src[at + 2],
        2 => -1 * src[at - 1] + 9 * src[at] + 9 * src[at + 1] - 1 * src[at + 2],
        3 => -3 * src[at - 1] + 18 * src[at] + 53 * src[at + 1] - 4 * src[at + 2],
        _ => 0,
    };

    private static int MspelFilter(byte[] src, int at, int stride, int mode, int r) => mode switch
    {
        0 => src[at],
        1 => (-4 * src[at - stride] + 53 * src[at] + 18 * src[at + stride] - 3 * src[at + 2 * stride] + 32 - r) >> 6,
        2 => (-1 * src[at - stride] + 9 * src[at] + 9 * src[at + stride] - 1 * src[at + 2 * stride] + 8 - r) >> 4,
        _ => (-3 * src[at - stride] + 18 * src[at] + 53 * src[at + stride] - 4 * src[at + 2 * stride] + 32 - r) >> 6,
    };

    private static readonly int[] s_shiftValue = [0, 5, 1, 5];

    private static void Op(byte[] dst, int at, int value, bool avg)
    {
        byte v = Clip(value);
        dst[at] = avg ? (byte)((dst[at] + v + 1) >> 1) : v;
    }

    /// <summary>vc1_mspel_mc: bicubic interpolation of a size x size block (put or average).</summary>
    private void MspelMc(byte[] dst, int d, int dstStride, byte[] src, int s, int srcStride, int hmode, int vmode, int rnd, int size, bool avg)
    {
        if (vmode != 0)
        {
            if (hmode != 0)
            {
                int shift = (s_shiftValue[hmode] + s_shiftValue[vmode]) >> 1;
                int r = (1 << (shift - 1)) + rnd - 1;
                int w = size + 3;
                var tmp = m_mspelTemp;
                for (int j = 0; j < size; j++)
                    for (int i = 0; i < w; i++)
                        tmp[j * w + i] = (short)((VerFilter(src, s - 1 + j * srcStride + i, srcStride, vmode) + r) >> shift);
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

    /// <summary>hpeldsp: half-sample bilinear interpolation (dxy: bit 0 x, bit 1 y), rounding or not, put or average.</summary>
    private static void HpelMc(byte[] dst, int d, int dstStride, byte[] src, int s, int srcStride, int dxy, bool round, int size, bool avg)
    {
        int r2 = round ? 1 : 0, r4 = round ? 2 : 1;
        for (int j = 0; j < size; j++)
        {
            int a = s + j * srcStride, o = d + j * dstStride;
            for (int i = 0; i < size; i++)
            {
                int v = dxy switch
                {
                    0 => src[a + i],
                    1 => (src[a + i] + src[a + i + 1] + r2) >> 1,
                    2 => (src[a + i] + src[a + i + srcStride] + r2) >> 1,
                    _ => (src[a + i] + src[a + i + 1] + src[a + i + srcStride] + src[a + i + srcStride + 1] + r4) >> 2,
                };
                dst[o + i] = avg ? (byte)((dst[o + i] + v + 1) >> 1) : (byte)v;
            }
        }
    }

    /// <summary>8x8 chroma bilinear interpolation in eighths (h264chroma when rounding, vc1dsp's no_rnd one otherwise).</summary>
    private static void ChromaMc(byte[] dst, int d, int dstStride, byte[] src, int x, int y, bool round, bool avg)
    {
        int a = (8 - x) * (8 - y), b = x * (8 - y), c = (8 - x) * y, e = x * y;
        int bias = round ? 32 : 28;
        for (int j = 0; j < 8; j++)
        {
            int s = j * ChromaEdgeStride, o = d + j * dstStride;
            for (int i = 0; i < 8; i++)
            {
                int v = (a * src[s + i] + b * src[s + i + 1] + c * src[s + i + ChromaEdgeStride] + e * src[s + i + ChromaEdgeStride + 1] + bias) >> 6;
                dst[o + i] = avg ? (byte)((dst[o + i] + v + 1) >> 1) : (byte)v;
            }
        }
    }

    #endregion
}
