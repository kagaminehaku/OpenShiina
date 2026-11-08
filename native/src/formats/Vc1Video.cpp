// C# Formats/Vc1Video.cs: the stream, sequence / entry point / picture headers, the pictures
#include "formats/Vc1Video.h"

#include <algorithm>
#include <cstring>
#include <span>
#include <string>
#include <utility>

#include "formats/Vc1Tables.h"
#include "formats/Vc1Video.Vlcs.h"

namespace openshiina::formats {

Vc1Vlcs::Vc1Vlcs()
{
    using namespace Vc1Tables;
    Imode = VlcTable::FromCodes(4, std::span(ImodeCodes), std::span(ImodeBits));
    Norm2 = VlcTable::FromCodes(3, std::span(Norm2Codes), std::span(Norm2Bits));
    Norm6 = VlcTable::FromCodes(9, std::span(Norm6Codes), std::span(Norm6Bits));
    for (size_t i = 0; i < 3; i++)
    {
        Ttmb[i] = VlcTable::FromCodes(9, std::span(TtmbCodes).subspan(i * 16, 16), std::span(TtmbBits).subspan(i * 16, 16));
        Ttblk[i] = VlcTable::FromCodes(5, std::span(TtblkCodes).subspan(i * 8, 8), std::span(TtblkBits).subspan(i * 8, 8));
        Subblkpat[i] = VlcTable::FromCodes(6, std::span(SubblkpatCodes).subspan(i * 15, 15), std::span(SubblkpatBits).subspan(i * 15, 15));
    }
    for (size_t i = 0; i < 4; i++)
    {
        CbpcyP[i] = VlcTable::FromCodes(9, std::span(CbpcyPCodes).subspan(i * 64, 64), std::span(CbpcyPBits).subspan(i * 64, 64));
        MvDiff[i] = VlcTable::FromCodes(9, std::span(MvDiffCodes).subspan(i * 73, 73), std::span(MvDiffBits).subspan(i * 73, 73));
    }
    for (size_t i = 0; i < 8; i++)
        Ac[i] = VlcTable::FromPairs(9, std::span(AcTables).subspan(i * 186 * 2, size_t(AcSizes[i]) * 2));
    std::vector<uint32_t> mbIntra(std::begin(Vc1Tables::MbIntra), std::end(Vc1Tables::MbIntra));
    MbIntra = VlcTable::FromPairs(9, mbIntra);
    for (size_t t = 0; t < 2; t++)
        for (size_t c = 0; c < 2; c++)
            Dc[t][c] = VlcTable::FromPairs(9, std::span(DcTables).subspan((t * 2 + c) * 240, 240));
}

const Vc1Vlcs& Vlcs()
{
    static const Vc1Vlcs vlcs;
    return vlcs;
}

Vc1Video::Vc1Video(const AsfFile::Stream& stream) : m_stream(stream)
{
    if (!stream.Video)
        throw InvalidDataError("Not a video stream");
    char text[5] = {char(stream.FourCC & 0xFF), char((stream.FourCC >> 8) & 0xFF), char((stream.FourCC >> 16) & 0xFF), char(stream.FourCC >> 24), 0};
    std::string fourcc = text;
    m_codedWidth = stream.Width;
    m_codedHeight = stream.Height;
    if (fourcc == "WMV3")
        ReadSimpleSequenceHeader(stream.Extra);
    else if (fourcc == "WVC1")
        ReadAdvancedExtraData(stream.Extra);
    else
        throw InvalidDataError("Not a VC-1 stream (" + fourcc + ")");
    m_zz8x4 = Vc1Tables::Wmv2ScanA;
    m_zz4x8 = Vc1Tables::Wmv2ScanB;
    if (m_profile == ProfileAdvanced)
    {
        m_zz8x4 = Vc1Tables::AdvProgressive8x4;
        m_zz4x8 = Vc1Tables::AdvProgressive4x8;
    }
    else if (m_resFasttx == 0)
        throw InvalidDataError("WMV3 without the VC-1 transform (RES_FASTTX 0)");
    for (size_t k = 0; k < 4; k++)
        for (size_t i = 0; i < 64; i++)
        {
            int x = Vc1Tables::Wmv1Scan[k * 64 + i];
            m_zz8x8[k][i] = uint8_t((x >> 3) | ((x & 7) << 3));
        }
    if (m_codedWidth < 16 || m_codedHeight < 16 || m_codedWidth > 4096 || m_codedHeight > 4096)
        throw InvalidDataError("Bad VC-1 picture size");
    m_width = m_codedWidth;
    m_height = m_codedHeight;
    m_frameRate = stream.TimePerFrame > 0 ? 1e7 / double(stream.TimePerFrame) : 30;
    Allocate();
}

void Vc1Video::Rewind()
{
    m_object = 0;
    m_last = m_nextPicture = nullptr;
    m_flushed = false;
    m_lastUseIc = m_nextUseIc = 0;
}

const MpegFrame* Vc1Video::Next()
{
    while (m_object < m_stream.Objects.size())
    {
        const auto& object = m_stream.Objects[m_object++];
        Picture* shown;
        try
        {
            shown = DecodeFrame(m_stream.Data.data() + object.Start, size_t(object.Length));
        }
        catch (const InvalidDataError&)
        {
            m_errors++;
            continue;
        }
        if (shown != nullptr)
            return &shown->Frame;
    }
    // The last anchor, held back for the B pictures before it
    if (!m_flushed && !m_lowDelay && m_nextPicture != nullptr)
    {
        m_flushed = true;
        return &m_nextPicture->Frame;
    }
    return nullptr;
}

// ---- Pictures ----

void Vc1Video::Allocate()
{
    m_mbWidth = (m_codedWidth + 15) >> 4;
    m_mbHeight = (m_codedHeight + 15) >> 4;
    m_mbStride = m_mbWidth + 1;
    m_b8Stride = m_mbWidth * 2 + 1;
    size_t ySize = size_t(m_b8Stride) * size_t(2 * m_mbHeight + 1), cSize = size_t(m_mbStride) * size_t(m_mbHeight + 1);
    size_t ycSize = ySize + 2 * cSize;
    m_dcVal.assign(ycSize, 1024);
    m_acVal.assign(ycSize * 16, 0);
    m_codedBlock.assign(ycSize, 0);
    m_mbType.assign(ycSize, 0);
    m_blockBase = m_b8Stride + 1;
    size_t mbs = size_t(m_mbStride) * size_t(m_mbHeight + 1);
    m_qscale.assign(mbs, 0);
    m_skipPlane.assign(mbs, 0);
    m_mvTypePlane.assign(mbs, 0);
    m_directPlane.assign(mbs, 0);
    m_acpredPlane.assign(mbs, 0);
    m_overFlagsPlane.assign(mbs, 0);
    m_cbp.assign(mbs, 0);
    m_ttblk.assign(mbs, 0);
    m_isIntra.assign(mbs, 0);
    m_lumaMv.assign(mbs * 2, 0);
    m_blocks.assign(mbs * 6 * 64, 0);
    int motion = (m_b8Stride * (2 * m_mbHeight + 1) + 8) * 2 + MotionOffset;
    for (auto& anchor : m_anchors)
        anchor = std::make_unique<Picture>(m_codedWidth, m_codedHeight, m_mbWidth, m_mbHeight, motion);
    m_bPicture = std::make_unique<Picture>(m_codedWidth, m_codedHeight, m_mbWidth, m_mbHeight, motion);
    m_lowDelay = m_maxBFrames == 0;
}

Vc1Video::Picture* Vc1Video::DecodeFrame(const uint8_t* data, size_t length)
{
    // vc1_decode_frame: gives the picture to show now, a B picture at once, an anchor (I / P) when
    // the next anchor comes (at once without B pictures)
    if (length == 0 || (length == 4 && BigEndian::ToUInt32(data) == 0x10A))
        return nullptr;
    if (m_profile == ProfileAdvanced)
    {
        if (!ReadAdvancedFrameData(data, length))
            return nullptr;
    }
    else
        SetBits(data, length);

    if (m_profile < ProfileAdvanced)
        ParseFrameHeader();
    else
        ParseFrameHeaderAdvanced();

    // B pictures need both references
    if (m_pictType == TypeB && m_last == nullptr)
        return nullptr;
    if (m_pictType == TypeB && m_nextPicture == nullptr)
        return nullptr;

    // The picture to decode into: B pictures to their own, anchors to one that no reference uses
    if (m_pictType == TypeB)
        m_cur = m_bPicture.get();
    else
    {
        for (auto& anchor : m_anchors)
            if (anchor.get() != m_last && anchor.get() != m_nextPicture)
            {
                m_cur = anchor.get();
                break;
            }
        m_last = m_nextPicture;
        m_nextPicture = m_cur;
    }
    if (m_pictType == TypeP && m_last == nullptr)
        throw InvalidDataError("P picture without a reference");

    DecodeBlocks();

    if (m_pictType == TypeB || m_lowDelay)
        return m_cur;
    return m_last;
}

// ---- Bits ----

void Vc1Video::SetBits(const uint8_t* data, size_t length)
{
    if (m_buf.size() < length + 16)
        m_buf.assign(length + 1024, 0);
    if (length != 0)
        std::memmove(m_buf.data(), data, length);
    std::fill_n(m_buf.begin() + std::ptrdiff_t(length), 16, uint8_t(0));
    m_pos = 0;
    m_size = int(length * 8);
}

// ---- Headers ----

void Vc1Video::ReadSimpleSequenceHeader(const Bytes& extra)
{
    // The Simple / Main profile sequence header (ff_vc1_decode_sequence_header): the WMV3 extra data
    if (extra.size() < 4)
        throw InvalidDataError("No WMV3 sequence header");
    SetBits(extra);
    m_profile = GetBits(2);
    if (m_profile == ProfileAdvanced)
    {
        ReadAdvancedSequenceHeader();
        return;
    }
    if (m_profile == 2)
        throw InvalidDataError("WMV3 Complex profile");
    int y411 = GetBit();
    m_resSprite = GetBit();
    if (y411 != 0)
        throw InvalidDataError("Old interlaced mode");
    GetBits(3); // frmrtq_postproc
    GetBits(5); // bitrtq_postproc
    m_loopFilter = GetBit() != 0;
    m_resX8 = GetBit();
    m_multires = GetBit();
    m_resFasttx = GetBit();
    m_fastuvmc = GetBit();
    m_extendedMv = GetBit();
    m_dquant = GetBits(2);
    m_vstransform = GetBit();
    if (GetBit() != 0) // res_transtab
        throw InvalidDataError("Reserved RES_TRANSTAB");
    m_overlap = GetBit();
    GetBit(); // resync marker
    m_rangered = GetBit();
    m_maxBFrames = GetBits(3);
    m_quantizerMode = GetBits(2);
    m_finterpflag = GetBit();
    if (m_resSprite != 0)
        throw InvalidDataError("WMV3 sprites");
    m_resRtmFlag = GetBit();
    if (m_rangered != 0 || m_multires != 0)
        throw InvalidDataError("WMV3 range reduction / multiresolution");
}

namespace {

/// The start codes (00 00 01 xx) of a buffer and the bytes after each up to the next.
std::vector<std::pair<int, Bytes>> Markers(const uint8_t* data, size_t length)
{
    std::vector<size_t> starts;
    for (size_t i = 0; i + 3 < length; i++)
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
        {
            starts.push_back(i);
            i += 2;
        }
    std::vector<std::pair<int, Bytes>> list;
    for (size_t k = 0; k < starts.size(); k++)
    {
        size_t s = starts[k], end = k + 1 < starts.size() ? starts[k + 1] : length;
        if (end > s + 4)
            list.emplace_back(0x100 | data[s + 3], Bytes(data + s + 4, data + end));
    }
    return list;
}

/// vc1_unescape_buffer: 00 00 03 0x becomes 00 00 0x.
Bytes Unescape(const uint8_t* src, size_t length)
{
    if (length < 4)
        return Bytes(src, src + length);
    Bytes dst(length);
    size_t n = 0;
    for (size_t i = 0; i < length; i++)
    {
        if (src[i] == 3 && i >= 2 && src[i - 1] == 0 && src[i - 2] == 0 && i < length - 1 && src[i + 1] < 4)
        {
            dst[n++] = src[i + 1];
            i++;
        }
        else
            dst[n++] = src[i];
    }
    dst.resize(n);
    return dst;
}

Bytes Unescape(const Bytes& src) { return Unescape(src.data(), src.size()); }

} // namespace

void Vc1Video::ReadAdvancedExtraData(const Bytes& extra)
{
    // The WVC1 extra data: a sequence header and an entry point, each after a start code
    bool sequence = false, entry = false;
    for (const auto& [code, payload] : Markers(extra.data(), extra.size()))
    {
        SetBits(Unescape(payload));
        if (code == 0x10F)
        {
            m_profile = GetBits(2);
            if (m_profile != ProfileAdvanced)
                throw InvalidDataError("Not an Advanced profile sequence header");
            ReadAdvancedSequenceHeader();
            sequence = true;
        }
        else if (code == 0x10E && sequence)
        {
            ReadEntryPoint();
            entry = true;
        }
    }
    if (!sequence || !entry)
        throw InvalidDataError("Incomplete WVC1 extra data");
}

void Vc1Video::ReadAdvancedSequenceHeader()
{
    // decode_sequence_header_adv
    m_resRtmFlag = 1;
    m_level = GetBits(3);
    if (GetBits(2) != 1)
        throw InvalidDataError("Only 4:2:0 chroma");
    GetBits(3); // frmrtq_postproc
    GetBits(5); // bitrtq_postproc
    m_postprocflag = GetBit();
    m_codedWidth = (GetBits(12) + 1) << 1;
    m_codedHeight = (GetBits(12) + 1) << 1;
    m_broadcast = GetBit();
    m_interlace = GetBit();
    m_tfcntrflag = GetBit();
    m_finterpflag = GetBit();
    GetBit(); // reserved
    if (GetBit() != 0) // psf
        throw InvalidDataError("Progressive segmented frames");
    m_maxBFrames = 7;
    if (GetBit() != 0)
    {
        // display info
        GetBits(14);
        GetBits(14);
        if (GetBit() != 0 && GetBits(4) == 15)
        {
            GetBits(8);
            GetBits(8);
        }
        if (GetBit() != 0)
        {
            if (GetBit() != 0)
                GetBits(16);
            else
            {
                GetBits(8);
                GetBits(4);
            }
        }
        if (GetBit() != 0)
            GetBits(24);
    }
    m_hrdParamFlag = GetBit();
    if (m_hrdParamFlag != 0)
    {
        m_hrdBuckets = GetBits(5);
        GetBits(8);
        for (int i = 0; i < m_hrdBuckets; i++)
            GetBits(32);
    }
    if (m_interlace != 0)
        throw InvalidDataError("Interlaced VC-1");
}

void Vc1Video::ReadEntryPoint()
{
    // ff_vc1_decode_entry_point
    GetBit(); // broken_link
    GetBit(); // closed_entry
    m_panscanflag = GetBit();
    m_refdistFlag = GetBit();
    m_loopFilter = GetBit() != 0;
    m_fastuvmc = GetBit();
    m_extendedMv = GetBit();
    m_dquant = GetBits(2);
    m_vstransform = GetBit();
    m_overlap = GetBit();
    m_quantizerMode = GetBits(2);
    if (m_hrdParamFlag != 0)
        for (int i = 0; i < m_hrdBuckets; i++)
            GetBits(8);
    if (GetBit() != 0)
    {
        m_codedWidth = (GetBits(12) + 1) << 1;
        m_codedHeight = (GetBits(12) + 1) << 1;
    }
    if (m_extendedMv != 0)
        m_extendedDmv = GetBit();
    if (GetBit() != 0 || GetBit() != 0)
        throw InvalidDataError("VC-1 range mapping");
}

bool Vc1Video::ReadAdvancedFrameData(const uint8_t* data, size_t length)
{
    // An Advanced profile frame: start codes (an entry point, the frame) or the frame alone; false
    // when there is no frame
    if (length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1)
    {
        Bytes frame;
        bool found = false;
        for (const auto& [code, payload] : Markers(data, length))
        {
            if (code == 0x10D)
            {
                frame = Unescape(payload);
                found = true;
            }
            else if (code == 0x10E)
            {
                SetBits(Unescape(payload));
                ReadEntryPoint();
            }
            else if (code == 0x10B || code == 0x10C)
                throw InvalidDataError("VC-1 slices / fields");
        }
        if (!found)
            return false;
        SetBits(frame);
        return true;
    }
    SetBits(Unescape(data, length));
    return true;
}

void Vc1Video::InitLut(int lumscale, int lumshift, uint8_t* luty, uint8_t* lutuv, bool chain)
{
    int scale, shift;
    if (lumscale == 0)
    {
        scale = -64;
        shift = (255 - lumshift * 2) * 64;
        if (lumshift > 31)
            shift += 128 << 6;
    }
    else
    {
        scale = lumscale + 32;
        shift = lumshift > 31 ? (lumshift - 64) * 64 : lumshift << 6;
    }
    for (int i = 0; i < 256; i++)
    {
        int iy = chain ? luty[i] : i, iu = chain ? lutuv[i] : i;
        luty[i] = Clip((scale * iy + shift + 32) >> 6);
        lutuv[i] = Clip((scale * (iu - 128) + 128 * 64 + 32) >> 6);
    }
}

void Vc1Video::RotateLuts()
{
    // rotate_luts: the tables move with the references; the current picture's start as identity
    if (m_pictType == TypeB || m_pictType == TypeBI)
    {
        InitLut(32, 0, m_auxLuty.data(), m_auxLutuv.data(), false);
        return;
    }
    // last <-> next: the old next reference becomes the last; the current picture (next) starts clean
    std::swap(m_lastLuty, m_nextLuty);
    std::swap(m_lastLutuv, m_nextLutuv);
    m_lastUseIc = m_nextUseIc;
    InitLut(32, 0, m_nextLuty.data(), m_nextLutuv.data(), false);
    m_nextUseIc = 0;
}

int Vc1Video::ReadBfraction()
{
    int index = GetBits(3);
    if (index == 7)
        index = 7 + GetBits(4);
    if (index == 21)
        throw InvalidDataError("Bad BFRACTION");
    return Vc1Tables::BfractionLut[index];
}

void Vc1Video::ReadQuantizer()
{
    // The quantizer of the picture (PQINDEX, HALFQP, PQUANTIZER)
    if (BitsLeft() < 5)
        throw InvalidDataError("Picture header cut short");
    int pqindex = GetBits(5);
    if (pqindex == 0)
        throw InvalidDataError("PQINDEX 0");
    m_pq = Vc1Tables::PquantTable[(m_quantizerMode == 0 ? 0 : 1) * 32 + pqindex];
    m_pqindex = pqindex;
    m_halfpq = pqindex < 9 ? GetBit() : 0;
    switch (m_quantizerMode)
    {
    case 0:
        m_pquantizer = pqindex < 9 ? 1 : 0;
        break;
    case 2:
        m_pquantizer = 0;
        break;
    case 1:
        m_pquantizer = GetBit();
        break;
    default:
        m_pquantizer = 1;
        break;
    }
    m_dquantfrm = 0;
}

void Vc1Video::SetMvRange()
{
    m_kx = m_mvrange + 9 + (m_mvrange >> 1);
    m_ky = m_mvrange + 8;
    m_rangeX = 1 << (m_kx - 1);
    m_rangeY = 1 << (m_ky - 1);
}

void Vc1Video::ReadPMvMode()
{
    // The P picture's MV mode, intensity compensation and quarter / half sample flags
    int lowquant = m_pq > 12 ? 0 : 1;
    m_mvMode = Vc1Tables::MvPmode[lowquant * 5 + GetUnary(1, 4)];
    if (m_mvMode == MvIntensityComp)
    {
        m_mvMode2 = Vc1Tables::MvPmode2[lowquant * 4 + GetUnary(1, 3)];
        int lumscale = GetBits(6), lumshift = GetBits(6);
        m_lastUseIc = 1;
        InitLut(lumscale, lumshift, m_lastLuty.data(), m_lastLutuv.data(), true);
    }
    int mode = m_mvMode == MvIntensityComp ? m_mvMode2 : m_mvMode;
    m_quarterSample = mode != Mv1MvHpel && mode != Mv1MvHpelBilin ? 1 : 0;
    m_mspel = mode != Mv1MvHpelBilin ? 1 : 0;
}

void Vc1Video::ReadTransformType()
{
    if (m_vstransform != 0)
    {
        m_ttmbf = GetBit();
        m_ttfrm = m_ttmbf != 0 ? Vc1Tables::TtfrmToTt[GetBits(2)] : 0;
    }
    else
    {
        m_ttmbf = 1;
        m_ttfrm = Tt8x8;
    }
}

void Vc1Video::ReadVopDquant()
{
    // vop_dquant_decoding
    if (m_dquant != 2)
    {
        m_dquantfrm = GetBit();
        if (m_dquantfrm == 0)
            return;
        m_dqprofile = GetBits(2);
        if (m_dqprofile == 1 || m_dqprofile == 2)
            m_dqsbedge = GetBits(2);
        else if (m_dqprofile == 3)
        {
            m_dqbilevel = GetBit();
            if (m_dqbilevel == 0)
            {
                m_halfpq = 0;
                return;
            }
        }
    }
    int pqdiff = GetBits(3);
    m_altpq = pqdiff == 7 ? GetBits(5) : m_pq + pqdiff + 1;
}

void Vc1Video::ParseFrameHeader()
{
    // ff_vc1_parse_frame_header (Simple / Main profiles)
    m_pSkipped = 0;
    if (m_finterpflag != 0)
        GetBit(); // interpfrm
    GetBits(2); // frame count
    if (GetBit() != 0)
        m_pictType = TypeP;
    else
        m_pictType = m_maxBFrames != 0 && GetBit() == 0 ? TypeB : TypeI;
    m_biType = 0;
    if (m_pictType == TypeB)
    {
        m_bfraction = ReadBfraction();
        if (m_bfraction == 0)
            m_pictType = TypeBI;
    }
    if (m_pictType == TypeI || m_pictType == TypeBI)
        GetBits(7); // buffer fullness
    if (m_pictType == TypeI || m_pictType == TypeBI)
        m_rnd = 1;
    if (m_pictType == TypeP)
        m_rnd ^= 1;
    ReadQuantizer();
    if (m_extendedMv == 1)
        m_mvrange = GetUnary(0, 3);
    SetMvRange();
    if (m_resX8 != 0 && (m_pictType == TypeI || m_pictType == TypeBI) && GetBit() != 0)
        throw InvalidDataError("X8 intra pictures");
    RotateLuts();
    if (m_pictType == TypeP)
    {
        m_ttIndex = (m_pq > 4 ? 1 : 0) + (m_pq > 12 ? 1 : 0);
        ReadPMvMode();
        if (MixedMvPicture())
            m_mvTypeIsRaw = BitplaneDecoding(m_mvTypePlane);
        else
        {
            m_mvTypeIsRaw = false;
            std::fill(m_mvTypePlane.begin(), m_mvTypePlane.end(), uint8_t(0));
        }
        m_skipIsRaw = BitplaneDecoding(m_skipPlane);
        if (BitsLeft() < 4)
            throw InvalidDataError("Picture header cut short");
        m_mvTableIndex = GetBits(2);
        m_cbptab = GetBits(2);
        if (m_dquant != 0)
            ReadVopDquant();
        ReadTransformType();
    }
    else if (m_pictType == TypeB)
    {
        m_ttIndex = (m_pq > 4 ? 1 : 0) + (m_pq > 12 ? 1 : 0);
        m_mvMode = GetBit() != 0 ? Mv1Mv : Mv1MvHpelBilin;
        m_quarterSample = m_mvMode == Mv1Mv ? 1 : 0;
        m_mspel = m_quarterSample;
        m_dmbIsRaw = BitplaneDecoding(m_directPlane);
        m_skipIsRaw = BitplaneDecoding(m_skipPlane);
        m_mvTableIndex = GetBits(2);
        m_cbptab = GetBits(2);
        if (m_dquant != 0)
            ReadVopDquant();
        ReadTransformType();
    }
    m_cAcTableIndex = Decode012();
    if (m_pictType == TypeI || m_pictType == TypeBI)
        m_yAcTableIndex = Decode012();
    m_dcTableIndex = GetBit();
    if (m_pictType == TypeBI)
    {
        m_pictType = TypeB;
        m_biType = 1;
    }
}

void Vc1Video::ParseFrameHeaderAdvanced()
{
    // ff_vc1_parse_frame_header_adv for progressive pictures
    int ptype = GetUnary(0, 4);
    switch (ptype)
    {
    case 0:
        m_pictType = TypeP;
        break;
    case 1:
        m_pictType = TypeB;
        break;
    case 2:
        m_pictType = TypeI;
        break;
    case 3:
        m_pictType = TypeBI;
        break;
    default:
        m_pictType = TypeP;
        break;
    }
    // a skipped picture (1111): the last one again
    m_pSkipped = ptype == 4 ? 1 : 0;
    if (m_tfcntrflag != 0)
        GetBits(8);
    if (m_broadcast != 0)
        GetBits(2); // rptfrm
    if (m_panscanflag != 0)
        throw InvalidDataError("Pan-scan windows");
    if (m_pSkipped != 0)
        return;
    m_rnd = GetBit();
    if (m_finterpflag != 0)
        GetBit(); // interpfrm
    if (m_pictType == TypeB)
    {
        m_bfraction = ReadBfraction();
        if (m_bfraction == 0)
            m_pictType = TypeBI;
    }
    ReadQuantizer();
    if (m_postprocflag != 0)
        GetBits(2);
    RotateLuts();
    switch (m_pictType)
    {
    case TypeI:
    case TypeBI:
        m_acpredIsRaw = BitplaneDecoding(m_acpredPlane);
        m_condover = CondoverNone;
        if (m_overlap != 0 && m_pq <= 8)
        {
            m_condover = Decode012();
            if (m_condover == CondoverSelect)
                m_overflgIsRaw = BitplaneDecoding(m_overFlagsPlane);
        }
        break;
    case TypeP:
        m_mvrange = m_extendedMv != 0 ? GetUnary(0, 3) : 0;
        SetMvRange();
        m_ttIndex = (m_pq > 4 ? 1 : 0) + (m_pq > 12 ? 1 : 0);
        ReadPMvMode();
        if (MixedMvPicture())
            m_mvTypeIsRaw = BitplaneDecoding(m_mvTypePlane);
        else
        {
            m_mvTypeIsRaw = false;
            std::fill(m_mvTypePlane.begin(), m_mvTypePlane.end(), uint8_t(0));
        }
        m_skipIsRaw = BitplaneDecoding(m_skipPlane);
        m_mvTableIndex = GetBits(2);
        m_cbptab = GetBits(2);
        if (m_dquant != 0)
            ReadVopDquant();
        ReadTransformType();
        break;
    case TypeB:
        m_mvrange = m_extendedMv != 0 ? GetUnary(0, 3) : 0;
        SetMvRange();
        m_ttIndex = (m_pq > 4 ? 1 : 0) + (m_pq > 12 ? 1 : 0);
        m_mvMode = GetBit() != 0 ? Mv1Mv : Mv1MvHpelBilin;
        m_quarterSample = m_mvMode == Mv1Mv ? 1 : 0;
        m_mspel = m_quarterSample;
        m_dmbIsRaw = BitplaneDecoding(m_directPlane);
        m_skipIsRaw = BitplaneDecoding(m_skipPlane);
        m_mvTableIndex = GetBits(2);
        m_cbptab = GetBits(2);
        if (m_dquant != 0)
            ReadVopDquant();
        ReadTransformType();
        break;
    }
    m_cAcTableIndex = Decode012();
    if (m_pictType == TypeI || m_pictType == TypeBI)
        m_yAcTableIndex = Decode012();
    m_dcTableIndex = GetBit();
    if ((m_pictType == TypeI || m_pictType == TypeBI) && m_dquant != 0)
        ReadVopDquant();
    m_biType = m_pictType == TypeBI ? 1 : 0;
    if (m_biType != 0)
        m_pictType = TypeB;
}

bool Vc1Video::BitplaneDecoding(std::vector<uint8_t>& planeVector)
{
    // bitplane_decoding into a macroblock plane; true when the bits are in the macroblocks instead (raw)
    uint8_t* plane = planeVector.data();
    int width = m_mbWidth, height = m_mbHeight, stride = m_mbStride;
    int invert = GetBit();
    int imode = GetVlc(Vlcs().Imode);
    switch (imode)
    {
    case 0: // raw
        return true;
    case 1: // norm-2
    case 2: // diff-2
    {
        int p = 0, offset = 0, y = 0;
        if (((height * width) & 1) != 0)
        {
            plane[p++] = uint8_t(GetBit());
            y = offset = 1;
            if (offset == width)
            {
                offset = 0;
                p += stride - width;
            }
        }
        for (; y < height * width; y += 2)
        {
            int code = GetVlc(Vlcs().Norm2);
            plane[p++] = uint8_t(code & 1);
            if (++offset == width)
            {
                offset = 0;
                p += stride - width;
            }
            plane[p++] = uint8_t(code >> 1);
            if (++offset == width)
            {
                offset = 0;
                p += stride - width;
            }
        }
        break;
    }
    case 3: // norm-6
    case 4: // diff-6
        if (height % 3 == 0 && width % 3 != 0)
        {
            // 2x3 tiles
            int p = 0;
            for (int y = 0; y < height; y += 3)
            {
                for (int x = width & 1; x < width; x += 2)
                {
                    int code = GetVlc(Vlcs().Norm6);
                    if (code < 0)
                        throw InvalidDataError("Bad NORM-6 code");
                    plane[p + x] = uint8_t(code & 1);
                    plane[p + x + 1] = uint8_t((code >> 1) & 1);
                    plane[p + x + stride] = uint8_t((code >> 2) & 1);
                    plane[p + x + 1 + stride] = uint8_t((code >> 3) & 1);
                    plane[p + x + stride * 2] = uint8_t((code >> 4) & 1);
                    plane[p + x + 1 + stride * 2] = uint8_t((code >> 5) & 1);
                }
                p += stride * 3;
            }
            if ((width & 1) != 0)
                ColSkip(plane, 0, 1, height, stride);
        }
        else
        {
            // 3x2 tiles
            int p = (height & 1) * stride;
            for (int y = height & 1; y < height; y += 2)
            {
                for (int x = width % 3; x < width; x += 3)
                {
                    int code = GetVlc(Vlcs().Norm6);
                    if (code < 0)
                        throw InvalidDataError("Bad NORM-6 code");
                    plane[p + x] = uint8_t(code & 1);
                    plane[p + x + 1] = uint8_t((code >> 1) & 1);
                    plane[p + x + 2] = uint8_t((code >> 2) & 1);
                    plane[p + x + stride] = uint8_t((code >> 3) & 1);
                    plane[p + x + 1 + stride] = uint8_t((code >> 4) & 1);
                    plane[p + x + 2 + stride] = uint8_t((code >> 5) & 1);
                }
                p += stride * 2;
            }
            int left = width % 3;
            if (left != 0)
                ColSkip(plane, 0, left, height, stride);
            if ((height & 1) != 0)
                RowSkip(plane, left, width - left, 1, stride);
        }
        break;
    case 5:
        RowSkip(plane, 0, width, height, stride);
        break;
    case 6:
        ColSkip(plane, 0, width, height, stride);
        break;
    default:
        throw InvalidDataError("Bad bitplane mode");
    }
    if (imode == 2 || imode == 4)
    {
        // the differential operator
        plane[0] = uint8_t(plane[0] ^ invert);
        for (int x = 1; x < width; x++)
            plane[x] = uint8_t(plane[x] ^ plane[x - 1]);
        for (int y = 1; y < height; y++)
        {
            int p = y * stride;
            plane[p] = uint8_t(plane[p] ^ plane[p - stride]);
            for (int x = 1; x < width; x++)
            {
                if (plane[p + x - 1] != plane[p + x - stride])
                    plane[p + x] = uint8_t(plane[p + x] ^ invert);
                else
                    plane[p + x] = uint8_t(plane[p + x] ^ plane[p + x - 1]);
            }
        }
    }
    else if (invert != 0)
    {
        for (int i = 0; i < stride * height; i++)
            plane[i] = plane[i] == 0 ? 1 : 0;
    }
    return false;
}

void Vc1Video::RowSkip(uint8_t* plane, int at, int width, int height, int stride)
{
    for (int y = 0; y < height; y++, at += stride)
    {
        if (GetBit() == 0)
            std::fill_n(plane + at, width, uint8_t(0));
        else
            for (int x = 0; x < width; x++)
                plane[at + x] = uint8_t(GetBit());
    }
}

void Vc1Video::ColSkip(uint8_t* plane, int at, int width, int height, int stride)
{
    for (int x = 0; x < width; x++, at++)
    {
        if (GetBit() == 0)
            for (int y = 0; y < height; y++)
                plane[at + y * stride] = 0;
        else
            for (int y = 0; y < height; y++)
                plane[at + y * stride] = uint8_t(GetBit());
    }
}

} // namespace openshiina::formats
