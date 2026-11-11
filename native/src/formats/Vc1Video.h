// C# Formats/Vc1Video*.cs: Windows Media Video 9 (WMV3: VC-1 Simple and Main profiles) and VC-1
// Advanced profile (WVC1), the video of the .wmv movies (AsfFile). A port of FFmpeg's VC-1 decoder
// (libavcodec vc1*.c, LGPL 2.1 or later) for progressive pictures: I, P, B, BI and skipped frames,
// 1-MV and 4-MV macroblocks, intensity compensation, variable-size transforms, overlap smoothing and
// the loop filter. Interlaced pictures, sprites (WMV3 image), range reduction and the X8 intra mode
// are not there (no game movie uses them; such a stream is refused).
//
// FFmpeg filters and puts the macroblocks a row and a column behind the decoding; here the overlap
// smoothing, the putting of intra blocks and the loop filter run over the whole picture once it is
// decoded, in the same order (vertical edges before horizontal ones for the overlap, horizontal
// before vertical for the loop filter, macroblocks in raster order).
//   Vc1Video.cpp         - the stream, sequence / entry point / picture headers, the pictures
//   Vc1Video.Blocks.cpp  - macroblocks and blocks (DC / AC prediction, coefficients, transforms)
//   Vc1Video.Motion.cpp  - motion vector prediction and motion compensation
//   Vc1Video.Filters.cpp - overlap smoothing and the loop filter
#pragma once

#include <array>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "formats/AsfFile.h"
#include "formats/MpegVideo.h"
#include "formats/VlcTable.h"

namespace openshiina::formats {


class Vc1Video final : public IMovieVideo
{
public:
    /// The video stream of an ASF file (the stream is kept: its objects are read as the frames go).
    explicit Vc1Video(const AsfFile::Stream& stream);

    int Width() const override { return m_width; }
    int Height() const override { return m_height; }
    double FrameRate() const override { return m_frameRate; }
    /// Pictures that did not decode cleanly so far.
    int Errors() const { return m_errors; }

    /// The next frame in display order, or null at the end.
    const MpegFrame* Next() override;
    /// Back to the first picture.
    void Rewind() override;

    /// The NEON motion compensation against the scalar one on random blocks: empty when they agree
    /// (the PC tests do not run it; empty there).
    static std::string CheckVectorLanes();

private:
    static constexpr int TypeI = 1, TypeP = 2, TypeB = 3, TypeBI = 4;
    static constexpr int ProfileAdvanced = 3;
    // MV modes
    static constexpr int Mv1MvHpelBilin = 0, Mv1Mv = 1, Mv1MvHpel = 2, MvMixedMv = 3, MvIntensityComp = 4;
    // Transform types
    static constexpr int Tt8x8 = 0, Tt8x4Bottom = 1, Tt8x4Top = 2, Tt8x4 = 3, Tt4x8Right = 4, Tt4x8Left = 5, Tt4x8 = 6, Tt4x4 = 7;
    // B macroblock types
    static constexpr int BmvBackward = 0, BmvForward = 1, BmvInterpolated = 2;
    // Conditional overlap
    static constexpr int CondoverNone = 0, CondoverAll = 1, CondoverSelect = 2;

    const AsfFile::Stream& m_stream;
    size_t m_object = 0;
    int m_width = 0, m_height = 0;
    double m_frameRate = 30;
    int m_errors = 0;

    // Sequence header and entry point
    int m_profile = 0, m_level = 0;
    int m_resX8 = 0, m_multires = 0, m_resFasttx = 0, m_resSprite = 0, m_rangered = 0, m_resRtmFlag = 0;
    int m_fastuvmc = 0, m_extendedMv = 0, m_dquant = 0, m_vstransform = 0, m_overlap = 0, m_quantizerMode = 0, m_finterpflag = 0;
    int m_maxBFrames = 0;
    bool m_loopFilter = false;
    int m_postprocflag = 0, m_broadcast = 0, m_interlace = 0, m_tfcntrflag = 0, m_panscanflag = 0, m_refdistFlag = 0, m_extendedDmv = 0;
    int m_hrdParamFlag = 0, m_hrdBuckets = 0;
    int m_codedWidth = 0, m_codedHeight = 0;

    // Scan tables (the 8x8 ones as FFmpeg keeps them: transposed for the VC-1 transform)
    std::array<std::array<uint8_t, 64>, 4> m_zz8x8{};
    const uint8_t* m_zz8x4 = nullptr;
    const uint8_t* m_zz4x8 = nullptr;
    static constexpr int LeftBlockShift = 0, TopBlockShift = 3;

    // ---- Pictures ----

    /// A decoded picture and the motion vectors B pictures take from it.
    struct Picture
    {
        Picture(int width, int height, int mbWidth, int mbHeight, int motionLength)
            : Frame(width, height, mbWidth, mbHeight), Motion{std::vector<int16_t>(size_t(motionLength)), std::vector<int16_t>(size_t(motionLength))}
        {
        }
        MpegFrame Frame;
        /// motion_val[0] and [1]: x, y per 8x8 luma block (b8 stride), from MotionOffset.
        std::array<std::vector<int16_t>, 2> Motion;
    };

    /// Shorts before motion vector 0 (FFmpeg's motion_val is its buffer + 4 pairs).
    static constexpr int MotionOffset = 8;

    std::array<std::unique_ptr<Picture>, 3> m_anchors;
    std::unique_ptr<Picture> m_bPicture;
    Picture* m_last = nullptr;
    Picture* m_nextPicture = nullptr;
    Picture* m_cur = nullptr;
    bool m_lowDelay = false, m_flushed = false;

    int m_mbWidth = 0, m_mbHeight = 0, m_mbStride = 0, m_b8Stride = 0;

    // Per block (FFmpeg's layout: luma blocks on the b8 grid, then Cb and Cr on the macroblock grid)
    std::vector<int16_t> m_dcVal, m_acVal;
    std::vector<uint8_t> m_codedBlock, m_mbType;
    int m_blockBase = 0;
    // Per macroblock
    std::vector<int> m_qscale;
    std::vector<uint8_t> m_skipPlane, m_mvTypePlane, m_directPlane, m_acpredPlane, m_overFlagsPlane;
    std::vector<uint32_t> m_cbp;
    std::vector<int> m_ttblk;
    std::vector<uint8_t> m_isIntra;
    std::vector<int16_t> m_lumaMv;
    /// The intra blocks of the picture (6 x 64 coefficients a macroblock) until they are put.
    std::vector<int16_t> m_blocks;

    void Allocate();
    Picture* DecodeFrame(const uint8_t* data, size_t length);

    // ---- Bits ----

    Bytes m_buf = Bytes(1024);
    int m_pos = 0, m_size = 0;

    void SetBits(const uint8_t* data, size_t length);
    void SetBits(const Bytes& data) { SetBits(data.data(), data.size()); }
    int BitsLeft() const { return m_size - m_pos; }
    uint32_t Show(int n) const { return BitReader::Show(m_buf.data(), m_buf.size(), m_pos, n); }
    int GetBits(int n)
    {
        uint32_t v = Show(n);
        m_pos += n;
        return int(v);
    }
    int GetBit() { return GetBits(1); }
    void SkipBits(int n) { m_pos += n; }
    /// get_unary: bits up to len until one equals stop.
    int GetUnary(int stop, int len)
    {
        int i = 0;
        while (i < len && GetBit() != stop)
            i++;
        return i;
    }
    int Decode012() { return GetBit() == 0 ? 0 : GetBit() + 1; }
    int Decode210() { return GetBit() == 1 ? 0 : 2 - GetBit(); }
    /// A variable-length code: its index in the table it was made from, or -1.
    int GetVlc(const VlcTable& vlc) { return vlc.Read(m_buf.data(), m_buf.size(), m_pos); }

    // ---- Headers ----

    void ReadSimpleSequenceHeader(const Bytes& extra);
    void ReadAdvancedExtraData(const Bytes& extra);
    void ReadAdvancedSequenceHeader();
    void ReadEntryPoint();
    bool ReadAdvancedFrameData(const uint8_t* data, size_t length);

    // Picture header
    int m_pictType = 0, m_biType = 0, m_pSkipped = 0, m_rnd = 0;
    int m_pq = 0, m_pqindex = 0, m_halfpq = 0, m_pquantizer = 0, m_altpq = 0;
    int m_dquantfrm = 0, m_dqprofile = 0, m_dqsbedge = 0, m_dqbilevel = 0;
    int m_mvrange = 0, m_kx = 0, m_ky = 0, m_rangeX = 0, m_rangeY = 0;
    int m_mvMode = 0, m_mvMode2 = 0, m_quarterSample = 0, m_mspel = 0;
    int m_ttIndex = 0, m_ttmbf = 0, m_ttfrm = 0, m_mvTableIndex = 0, m_cbptab = 0;
    int m_cAcTableIndex = 0, m_yAcTableIndex = 0, m_dcTableIndex = 0;
    int m_bfraction = 0, m_condover = 0;
    bool m_mvTypeIsRaw = false, m_skipIsRaw = false, m_dmbIsRaw = false, m_acpredIsRaw = false, m_overflgIsRaw = false;

    // Intensity compensation: tables for reading the last and the next reference
    std::array<uint8_t, 256> m_lastLuty{}, m_lastLutuv{}, m_nextLuty{}, m_nextLutuv{}, m_auxLuty{}, m_auxLutuv{};
    int m_lastUseIc = 0, m_nextUseIc = 0;

    static void InitLut(int lumscale, int lumshift, uint8_t* luty, uint8_t* lutuv, bool chain);
    void RotateLuts();
    int ReadBfraction();
    void ReadQuantizer();
    void SetMvRange();
    void ReadPMvMode();
    bool MixedMvPicture() const { return m_mvMode == MvMixedMv || (m_mvMode == MvIntensityComp && m_mvMode2 == MvMixedMv); }
    void ReadTransformType();
    void ReadVopDquant();
    void ParseFrameHeader();
    void ParseFrameHeaderAdvanced();
    bool BitplaneDecoding(std::vector<uint8_t>& plane);
    void RowSkip(uint8_t* plane, int at, int width, int height, int stride);
    void ColSkip(uint8_t* plane, int at, int width, int height, int stride);

    static uint8_t Clip(int v) { return uint8_t(v < 0 ? 0 : v > 255 ? 255 : v); }

    // ---- Blocks (Vc1Video.Blocks.cpp) ----

    // The macroblock being decoded
    int m_mbX = 0, m_mbY = 0;
    bool m_firstSliceLine = false;
    std::array<int, 6> m_blockIndex{};
    std::array<int, 6> m_blockWrap{};
    int m_mbIntra = 0, m_acPred = 0;
    int m_codingset = 0, m_codingset2 = 0;
    int m_aAvail = 0, m_cAvail = 0;
    int m_yDcScale = 0, m_cDcScale = 0;
    int m_esc3LevelLength = 0, m_esc3RunLength = 0;
    int m_mbHasCoeffs = 0;
    /// Offsets of the macroblock in the current picture's luma and chroma planes.
    int m_destY = 0, m_destC = 0;
    std::array<int16_t, 64> m_scratch{};

    static constexpr int AcEscapeIndexRows = 185;

    int MbPos() const { return m_mbX + m_mbY * m_mbStride; }
    /// The 64 coefficients of block n of the current macroblock in m_blocks.
    int16_t* Block(int n) { return m_blocks.data() + (size_t(MbPos()) * 6 + size_t(n)) * 64; }
    void SetBlockIndex();
    int16_t& DcVal(int index) { return m_dcVal[size_t(m_blockBase + index)]; }
    uint8_t& MbType(int index) { return m_mbType[size_t(m_blockBase + index)]; }
    static int16_t& Motion(Picture& p, int dir, int xy, int c) { return p.Motion[size_t(dir)][size_t(MotionOffset + 2 * xy + c)]; }

    void DecodeBlocks();
    void SelectCodingSets(int yIndex);
    void CheckBits() const;
    void DecodeIBlocks();
    int CodedBlockPred(int n, int& at);
    int IPredDc(int n, int& dir);
    int PredDc(int n, int aAvail, int cAvail, int& dir);
    int ReadDcDiff(int n, int quant);
    void DecodeAcCoeff(bool& last, int& skip, int& value, int codingset);
    void DecodeIBlock(int16_t* block, int n, int coded, int codingset);
    void DecodeIBlockAdvanced(int16_t* block, int n, int coded, int codingset, int mquant);
    void DecodeIntraBlock(int16_t* block, int n, int coded, int mquant, int codingset);
    int DecodePBlock(int16_t* block, int n, int mquant, int ttmb, bool firstBlock, uint8_t* plane, int dst, int linesize, int& ttmbOut);
    int Dequant(int value, int scale, int quant) const;
    void GetMquant(int& mquant);
    void GetMvData(int& dmvX, int& dmvY);
    void DecodePBlocks();
    void DecodePMb();
    struct Destination
    {
        uint8_t* Plane;
        int Offset, Stride;
    };
    Destination Dest(int n, int stride, int cstride);
    void DecodeBBlocks();
    void DecodeBMb();
    void DecodeSkipBlocks();
    void PutIntraBlocks(bool isSigned);
    static void PutPixelsClamped(const int16_t* block, uint8_t* plane, int dst, int linesize, bool isSigned);
    static void AddPixelsClamped(const int16_t* block, uint8_t* plane, int dst, int linesize);
    static void InvTrans8x8(int16_t* block);
    static void AddDc(uint8_t* plane, int dst, int linesize, int dc, int width, int height);
    static void InvTrans8x8Dc(uint8_t* plane, int dst, int linesize, const int16_t* block);
    static void InvTrans8x4Dc(uint8_t* plane, int dst, int linesize, int dc);
    static void InvTrans4x8Dc(uint8_t* plane, int dst, int linesize, int dc);
    static void InvTrans4x4Dc(uint8_t* plane, int dst, int linesize, int dc);
    static void InvTrans8x4(uint8_t* plane, int dest, int linesize, int16_t* block);
    static void InvTrans4x8(uint8_t* plane, int dest, int linesize, int16_t* block);
    static void InvTrans4x4(uint8_t* plane, int dest, int linesize, int16_t* block);

    // ---- Motion (Vc1Video.Motion.cpp) ----

    /// s->mv[dir][n][c]: the motion vectors of the current macroblock's blocks.
    std::array<int, 2 * 4 * 2> m_mv{};
    int& Mv(int dir, int n, int c) { return m_mv[size_t((dir * 4 + n) * 2 + c)]; }

    static constexpr int EdgeStride = 32, ChromaEdgeStride = 16;
    std::array<uint8_t, EdgeStride * EdgeStride> m_edgeY{};
    std::array<uint8_t, ChromaEdgeStride * ChromaEdgeStride> m_edgeU{}, m_edgeV{};
    std::array<int16_t, 19 * 16> m_mspelTemp{};

    static int MidPred(int a, int b, int c);
    static int Median4(int a, int b, int c, int d);
    void PredMv(int n, int dmvX, int dmvY, bool mv1, int rX, int rY, int dir);
    static int ScaleMv(int value, int bfrac, int inv, int qs);
    void PredBMv(int* dmvX, int* dmvY, bool direct, int mvtype);
    void BMc(bool direct, int mode);
    static void Fetch(const uint8_t* plane, int stride, int width, int height, int x, int y, int w, int h, uint8_t* dst, int dstStride);
    static void ApplyLut(uint8_t* block, int stride, int size, const uint8_t* lut);
    struct Ref
    {
        Picture* Pic;
        const uint8_t* Luty;
        const uint8_t* Lutuv;
        bool UseIc;
    };
    Ref Reference(int dir);
    void FetchLuma(const MpegFrame& frame, int x, int y, int k, const uint8_t* luty, bool useIc);
    void FetchChroma(const MpegFrame& frame, int x, int y, const uint8_t* lutuv, bool useIc);
    void ClampLumaSource(int& x, int& y) const;
    void ClampChromaSource(int& x, int& y) const;
    void Mc1Mv(int dir);
    void Mc4MvLuma(int n, int dir, bool avg);
    void Mc4MvChroma(int dir);
    void InterpMc();
    void MspelMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int s, int srcStride, int hmode, int vmode, int rnd, int size, bool avg);
    static void HpelMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int s, int srcStride, int dxy, bool round, int size, bool avg);
    static void ChromaMc(uint8_t* dst, int d, int dstStride, const uint8_t* src, int x, int y, bool round, bool avg);

    // ---- Filters (Vc1Video.Filters.cpp) ----

    static int BlockAt(int mb, int n) { return (mb * 6 + n) * 64; }
    void HOverlap(int left, int right);
    void VOverlap(int top, int bottom);
    void HOverlapEdge(int mb, int i);
    void VOverlapEdge(int mb, int i);
    void OverlapIntraPicture();
    void OverlapInterPicture();
    static bool FilterLine(uint8_t* p, int src, int stride, int pq);
    static void LoopFilter(uint8_t* p, int src, int step, int stride, int length, int pq);
    void VLoop(uint8_t* p, int src, int linesize, int length) const { LoopFilter(p, src, 1, linesize, length, m_pq); }
    void HLoop(uint8_t* p, int src, int linesize, int length) const { LoopFilter(p, src, linesize, 1, length, m_pq); }
    void LoopFilterIntra();
    void LoopFilterInter();
    void PVLoopFilter(int n, bool bottomEdge);
    void PHLoopFilter(int n, bool rightEdge);
};

} // namespace openshiina::formats
