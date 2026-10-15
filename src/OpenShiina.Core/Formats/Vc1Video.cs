// Windows Media Video 9 (WMV3: VC-1 Simple and Main profiles) and VC-1 Advanced profile (WVC1),
// the video of the .wmv movies (AsfFile). A port of FFmpeg's VC-1 decoder (libavcodec vc1*.c,
// LGPL 2.1 or later) for progressive pictures: I, P, B, BI and skipped frames, 1-MV and 4-MV
// macroblocks, intensity compensation, variable-size transforms, overlap smoothing and the loop
// filter. Interlaced pictures, sprites (WMV3 image), range reduction and the X8 intra mode are
// not there (no game movie uses them; such a stream is refused).
//
// FFmpeg filters and puts the macroblocks a row and a column behind the decoding; here the
// overlap smoothing, the putting of intra blocks and the loop filter run over the whole picture
// once it is decoded, in the same order (vertical edges before horizontal ones for the overlap,
// horizontal before vertical for the loop filter, macroblocks in raster order).
//   Vc1Video.cs        - the stream, sequence / entry point / picture headers, the pictures
//   Vc1Video.Blocks.cs - macroblocks and blocks (DC / AC prediction, coefficients, transforms)
//   Vc1Video.Motion.cs - motion vector prediction and motion compensation
//   Vc1Video.Filters.cs - overlap smoothing and the loop filter

using System.Buffers.Binary;

namespace OpenShiina.Formats;

public sealed partial class Vc1Video : IMovieVideo
{
    private const int TypeI = 1, TypeP = 2, TypeB = 3, TypeBI = 4;
    private const int ProfileAdvanced = 3;

    // MV modes
    private const int Mv1MvHpelBilin = 0, Mv1Mv = 1, Mv1MvHpel = 2, MvMixedMv = 3, MvIntensityComp = 4;
    // Transform types
    private const int Tt8x8 = 0, Tt8x4Bottom = 1, Tt8x4Top = 2, Tt8x4 = 3, Tt4x8Right = 4, Tt4x8Left = 5, Tt4x8 = 6, Tt4x4 = 7;
    // B macroblock types
    private const int BmvBackward = 0, BmvForward = 1, BmvInterpolated = 2;
    // Conditional overlap
    private const int CondoverNone = 0, CondoverAll = 1, CondoverSelect = 2;

    private readonly AsfFile.Stream m_stream;
    private int m_object;

    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    /// <summary>Pictures that did not decode cleanly so far.</summary>
    public int Errors { get; private set; }

    // Sequence header and entry point
    private int m_profile, m_level;
    private int m_resX8, m_multires, m_resFasttx, m_resSprite, m_rangered, m_resRtmFlag;
    private int m_fastuvmc, m_extendedMv, m_dquant, m_vstransform, m_overlap, m_quantizerMode, m_finterpflag;
    private int m_maxBFrames;
    private bool m_loopFilter;
    private int m_postprocflag, m_broadcast, m_interlace, m_tfcntrflag, m_panscanflag, m_refdistFlag, m_extendedDmv;
    private int m_hrdParamFlag, m_hrdBuckets;
    private int m_codedWidth, m_codedHeight;

    // Scan tables (the 8x8 ones as FFmpeg keeps them: transposed for the VC-1 transform)
    private readonly byte[][] m_zz8x8 = [new byte[64], new byte[64], new byte[64], new byte[64]];
    private byte[] m_zz8x4 = Vc1Tables.Wmv2ScanA, m_zz4x8 = Vc1Tables.Wmv2ScanB;
    private const int LeftBlockShift = 0, TopBlockShift = 3;

    public Vc1Video(AsfFile.Stream stream)
    {
        if (!stream.Video)
            throw new InvalidDataException("Not a video stream");
        m_stream = stream;
        string fourcc = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(stream.FourCC));
        m_codedWidth = stream.Width;
        m_codedHeight = stream.Height;
        if (fourcc is "WMV3")
            ReadSimpleSequenceHeader(stream.Extra);
        else if (fourcc is "WVC1")
            ReadAdvancedExtraData(stream.Extra);
        else
            throw new InvalidDataException($"Not a VC-1 stream ({fourcc})");
        if (m_profile == ProfileAdvanced)
        {
            m_zz8x4 = Vc1Tables.AdvProgressive8x4;
            m_zz4x8 = Vc1Tables.AdvProgressive4x8;
        }
        else if (m_resFasttx == 0)
            throw new InvalidDataException("WMV3 without the VC-1 transform (RES_FASTTX 0)");
        for (int k = 0; k < 4; k++)
            for (int i = 0; i < 64; i++)
            {
                int x = Vc1Tables.Wmv1Scan[k * 64 + i];
                m_zz8x8[k][i] = (byte)((x >> 3) | ((x & 7) << 3));
            }
        if (m_codedWidth < 16 || m_codedHeight < 16 || m_codedWidth > 4096 || m_codedHeight > 4096)
            throw new InvalidDataException("Bad VC-1 picture size");
        Width = m_codedWidth;
        Height = m_codedHeight;
        FrameRate = stream.TimePerFrame > 0 ? 1e7 / stream.TimePerFrame : 30;
        Allocate();
    }

    /// <summary>Back to the first picture.</summary>
    public void Rewind()
    {
        m_object = 0;
        m_last = m_nextPicture = null;
        m_flushed = false;
        m_lastUseIc = m_nextUseIc = 0;
    }

    /// <summary>The next frame in display order, or null at the end.</summary>
    public MpegFrame? Next()
    {
        while (m_object < m_stream.Objects.Count)
        {
            var (start, length, _, _) = m_stream.Objects[m_object++];
            Picture? shown;
            try
            {
                shown = DecodeFrame(m_stream.Data.AsSpan(start, length));
            }
            catch (InvalidDataException)
            {
                Errors++;
                continue;
            }
            if (shown != null)
                return shown.Frame;
        }
        // The last anchor, held back for the B pictures before it
        if (!m_flushed && !m_lowDelay && m_nextPicture != null)
        {
            m_flushed = true;
            return m_nextPicture.Frame;
        }
        return null;
    }

    #region Pictures

    /// <summary>A decoded picture and the motion vectors B pictures take from it.</summary>
    private sealed class Picture(int width, int height, int mbWidth, int mbHeight, int motionLength)
    {
        public readonly MpegFrame Frame = new(width, height, mbWidth, mbHeight);
        /// <summary>motion_val[0] and [1]: x, y per 8x8 luma block (b8 stride), from <see cref="MotionOffset"/>.</summary>
        public readonly short[][] Motion = [new short[motionLength], new short[motionLength]];
    }

    /// <summary>Shorts before motion vector 0 (FFmpeg's motion_val is its buffer + 4 pairs).</summary>
    private const int MotionOffset = 8;

    private readonly Picture[] m_anchors = new Picture[3];
    private Picture m_bPicture = null!;
    private Picture? m_last, m_nextPicture;
    private Picture m_cur = null!;
    private bool m_lowDelay, m_flushed;

    private int m_mbWidth, m_mbHeight, m_mbStride, m_b8Stride;

    // Per block (FFmpeg's layout: luma blocks on the b8 grid, then Cb and Cr on the macroblock grid)
    private short[] m_dcVal = null!, m_acVal = null!;
    private byte[] m_codedBlock = null!, m_mbType = null!;
    private int m_blockBase;
    // Per macroblock
    private int[] m_qscale = null!;
    private byte[] m_skipPlane = null!, m_mvTypePlane = null!, m_directPlane = null!, m_acpredPlane = null!, m_overFlagsPlane = null!;
    private uint[] m_cbp = null!;
    private int[] m_ttblk = null!;
    private byte[] m_isIntra = null!;
    private short[] m_lumaMv = null!;
    /// <summary>The intra blocks of the picture (6 x 64 coefficients a macroblock) until they are put.</summary>
    private short[] m_blocks = null!;

    private void Allocate()
    {
        m_mbWidth = (m_codedWidth + 15) >> 4;
        m_mbHeight = (m_codedHeight + 15) >> 4;
        m_mbStride = m_mbWidth + 1;
        m_b8Stride = m_mbWidth * 2 + 1;
        int ySize = m_b8Stride * (2 * m_mbHeight + 1), cSize = m_mbStride * (m_mbHeight + 1);
        int ycSize = ySize + 2 * cSize;
        m_dcVal = new short[ycSize];
        Array.Fill(m_dcVal, (short)1024);
        m_acVal = new short[ycSize * 16];
        m_codedBlock = new byte[ycSize];
        m_mbType = new byte[ycSize];
        m_blockBase = m_b8Stride + 1;
        int mbs = m_mbStride * (m_mbHeight + 1);
        m_qscale = new int[mbs];
        m_skipPlane = new byte[mbs];
        m_mvTypePlane = new byte[mbs];
        m_directPlane = new byte[mbs];
        m_acpredPlane = new byte[mbs];
        m_overFlagsPlane = new byte[mbs];
        m_cbp = new uint[mbs];
        m_ttblk = new int[mbs];
        m_isIntra = new byte[mbs];
        m_lumaMv = new short[mbs * 2];
        m_blocks = new short[mbs * 6 * 64];
        int motion = (m_b8Stride * (2 * m_mbHeight + 1) + 8) * 2 + MotionOffset;
        for (int i = 0; i < 3; i++)
            m_anchors[i] = new Picture(m_codedWidth, m_codedHeight, m_mbWidth, m_mbHeight, motion);
        m_bPicture = new Picture(m_codedWidth, m_codedHeight, m_mbWidth, m_mbHeight, motion);
        m_lowDelay = m_maxBFrames == 0;
    }

    /// <summary>
    /// Decodes a frame (vc1_decode_frame) and gives the picture to show now: a B picture at
    /// once, an anchor (I / P) when the next anchor comes (at once without B pictures).
    /// </summary>
    private Picture? DecodeFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || (data.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0x10A))
            return null;
        if (m_profile == ProfileAdvanced)
        {
            if (!ReadAdvancedFrameData(data))
                return null;
        }
        else
            SetBits(data);

        if (m_profile < ProfileAdvanced)
            ParseFrameHeader();
        else
            ParseFrameHeaderAdvanced();

        // B pictures need both references
        if (m_pictType == TypeB && m_last == null)
            return null;
        if (m_pictType == TypeB && m_nextPicture == null)
            return null;

        // The picture to decode into: B pictures to their own, anchors to one that no reference uses
        if (m_pictType == TypeB)
            m_cur = m_bPicture;
        else
        {
            m_cur = m_anchors.First(p => p != m_last && p != m_nextPicture);
            m_last = m_nextPicture;
            m_nextPicture = m_cur;
        }
        if (m_pictType == TypeP && m_last == null)
            throw new InvalidDataException("P picture without a reference");

        DecodeBlocks();

        if (m_pictType == TypeB || m_lowDelay)
            return m_cur;
        return m_last;
    }

    #endregion

    #region Bits

    private byte[] m_buf = new byte[1024];
    private int m_pos, m_size;

    private void SetBits(ReadOnlySpan<byte> data)
    {
        if (m_buf.Length < data.Length + 16)
            m_buf = new byte[data.Length + 1024];
        data.CopyTo(m_buf);
        m_buf.AsSpan(data.Length, 16).Clear();
        m_pos = 0;
        m_size = data.Length * 8;
    }

    private int BitsLeft => m_size - m_pos;

    private uint Show(int n)
    {
        int at = m_pos >> 3;
        if (n == 0 || at + 8 > m_buf.Length)
            return 0;
        ulong w = BinaryPrimitives.ReadUInt64BigEndian(m_buf.AsSpan(at));
        return (uint)((w << (m_pos & 7)) >> (64 - n));
    }

    private int GetBits(int n)
    {
        uint v = Show(n);
        m_pos += n;
        return (int)v;
    }

    private int GetBit() => GetBits(1);

    private void SkipBits(int n) => m_pos += n;

    /// <summary>get_unary: bits up to <paramref name="len"/> until one equals <paramref name="stop"/>.</summary>
    private int GetUnary(int stop, int len)
    {
        int i = 0;
        while (i < len && GetBit() != stop)
            i++;
        return i;
    }

    private int Decode012() => GetBit() == 0 ? 0 : GetBit() + 1;

    private int Decode210() => GetBit() == 1 ? 0 : 2 - GetBit();

    /// <summary>A variable-length code: its index in the table it was made from, or -1.</summary>
    private int GetVlc(VlcTable vlc) => vlc.Read(m_buf, ref m_pos);

    private static readonly VlcTable s_imodeVlc = VlcTable.FromCodes(4, Vc1Tables.ImodeCodes, Vc1Tables.ImodeBits);
    private static readonly VlcTable s_norm2Vlc = VlcTable.FromCodes(3, Vc1Tables.Norm2Codes, Vc1Tables.Norm2Bits);
    private static readonly VlcTable s_norm6Vlc = VlcTable.FromCodes(9, Vc1Tables.Norm6Codes, Vc1Tables.Norm6Bits);
    private static readonly VlcTable[] s_ttmbVlc = Range(3, i => VlcTable.FromCodes(9, Vc1Tables.TtmbCodes.AsSpan(i * 16, 16), Vc1Tables.TtmbBits.AsSpan(i * 16, 16)));
    private static readonly VlcTable[] s_ttblkVlc = Range(3, i => VlcTable.FromCodes(5, Vc1Tables.TtblkCodes.AsSpan(i * 8, 8), Vc1Tables.TtblkBits.AsSpan(i * 8, 8)));
    private static readonly VlcTable[] s_subblkpatVlc = Range(3, i => VlcTable.FromCodes(6, Vc1Tables.SubblkpatCodes.AsSpan(i * 15, 15), Vc1Tables.SubblkpatBits.AsSpan(i * 15, 15)));
    private static readonly VlcTable[] s_cbpcyPVlc = Range(4, i => VlcTable.FromCodes(9, Vc1Tables.CbpcyPCodes.AsSpan(i * 64, 64), Vc1Tables.CbpcyPBits.AsSpan(i * 64, 64)));
    private static readonly VlcTable[] s_mvDiffVlc = Range(4, i => VlcTable.FromCodes(9, Vc1Tables.MvDiffCodes.AsSpan(i * 73, 73), Vc1Tables.MvDiffBits.AsSpan(i * 73, 73)));
    private static readonly VlcTable[] s_acVlc = Range(8, i => VlcTable.FromPairs(9, Vc1Tables.AcTables.AsSpan(i * 186 * 2, Vc1Tables.AcSizes[i] * 2)));
    private static readonly VlcTable s_mbIntraVlc = VlcTable.FromPairs(9, Array.ConvertAll(Vc1Tables.MbIntra, c => (uint)c));
    private static readonly VlcTable[][] s_dcVlc = Range(2, t => Range(2, c => VlcTable.FromPairs(9, Vc1Tables.DcTables.AsSpan((t * 2 + c) * 240, 240))));

    private static T[] Range<T>(int count, Func<int, T> make) => Enumerable.Range(0, count).Select(make).ToArray();

    #endregion

    #region Headers

    /// <summary>The Simple / Main profile sequence header (ff_vc1_decode_sequence_header): the WMV3 extra data.</summary>
    private void ReadSimpleSequenceHeader(byte[] extra)
    {
        if (extra.Length < 4)
            throw new InvalidDataException("No WMV3 sequence header");
        SetBits(extra);
        m_profile = GetBits(2);
        if (m_profile == ProfileAdvanced)
        {
            ReadAdvancedSequenceHeader();
            return;
        }
        if (m_profile == 2)
            throw new InvalidDataException("WMV3 Complex profile");
        int y411 = GetBit();
        m_resSprite = GetBit();
        if (y411 != 0)
            throw new InvalidDataException("Old interlaced mode");
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
            throw new InvalidDataException("Reserved RES_TRANSTAB");
        m_overlap = GetBit();
        GetBit(); // resync marker
        m_rangered = GetBit();
        m_maxBFrames = GetBits(3);
        m_quantizerMode = GetBits(2);
        m_finterpflag = GetBit();
        if (m_resSprite != 0)
            throw new InvalidDataException("WMV3 sprites");
        m_resRtmFlag = GetBit();
        if (m_rangered != 0 || m_multires != 0)
            throw new InvalidDataException("WMV3 range reduction / multiresolution");
    }

    /// <summary>The WVC1 extra data: a sequence header and an entry point, each after a start code.</summary>
    private void ReadAdvancedExtraData(byte[] extra)
    {
        bool sequence = false, entry = false;
        foreach (var (code, payload) in Markers(extra))
        {
            SetBits(Unescape(payload));
            if (code == 0x10F)
            {
                m_profile = GetBits(2);
                if (m_profile != ProfileAdvanced)
                    throw new InvalidDataException("Not an Advanced profile sequence header");
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
            throw new InvalidDataException("Incomplete WVC1 extra data");
    }

    /// <summary>decode_sequence_header_adv.</summary>
    private void ReadAdvancedSequenceHeader()
    {
        m_resRtmFlag = 1;
        m_level = GetBits(3);
        if (GetBits(2) != 1)
            throw new InvalidDataException("Only 4:2:0 chroma");
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
            throw new InvalidDataException("Progressive segmented frames");
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
            throw new InvalidDataException("Interlaced VC-1");
    }

    /// <summary>ff_vc1_decode_entry_point.</summary>
    private void ReadEntryPoint()
    {
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
            throw new InvalidDataException("VC-1 range mapping");
    }

    /// <summary>The start codes (00 00 01 xx) of a buffer and the bytes after each up to the next.</summary>
    private static List<(int Code, byte[] Payload)> Markers(ReadOnlySpan<byte> data)
    {
        var starts = new List<int>();
        for (int i = 0; i + 3 < data.Length; i++)
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                starts.Add(i);
                i += 2;
            }
        var list = new List<(int, byte[])>();
        for (int k = 0; k < starts.Count; k++)
        {
            int s = starts[k], end = k + 1 < starts.Count ? starts[k + 1] : data.Length;
            if (end - s - 4 > 0)
                list.Add((0x100 | data[s + 3], data[(s + 4)..end].ToArray()));
        }
        return list;
    }

    /// <summary>vc1_unescape_buffer: 00 00 03 0x becomes 00 00 0x.</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> src)
    {
        if (src.Length < 4)
            return src.ToArray();
        var dst = new byte[src.Length];
        int n = 0;
        for (int i = 0; i < src.Length; i++)
        {
            if (src[i] == 3 && i >= 2 && src[i - 1] == 0 && src[i - 2] == 0 && i < src.Length - 1 && src[i + 1] < 4)
            {
                dst[n++] = src[i + 1];
                i++;
            }
            else
                dst[n++] = src[i];
        }
        return dst.AsSpan(0, n).ToArray();
    }

    /// <summary>An Advanced profile frame: start codes (an entry point, the frame) or the frame alone; false when there is no frame.</summary>
    private bool ReadAdvancedFrameData(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1)
        {
            byte[]? frame = null;
            foreach (var (code, payload) in Markers(data))
            {
                if (code == 0x10D)
                    frame = Unescape(payload);
                else if (code == 0x10E)
                {
                    SetBits(Unescape(payload));
                    ReadEntryPoint();
                }
                else if (code is 0x10B or 0x10C)
                    throw new InvalidDataException("VC-1 slices / fields");
            }
            if (frame == null)
                return false;
            SetBits(frame);
            return true;
        }
        SetBits(Unescape(data));
        return true;
    }

    // Picture header
    private int m_pictType, m_biType, m_pSkipped, m_rnd;
    private int m_pq, m_pqindex, m_halfpq, m_pquantizer, m_altpq;
    private int m_dquantfrm, m_dqprofile, m_dqsbedge, m_dqbilevel;
    private int m_mvrange, m_kx, m_ky, m_rangeX, m_rangeY;
    private int m_mvMode, m_mvMode2, m_quarterSample, m_mspel;
    private int m_ttIndex, m_ttmbf, m_ttfrm, m_mvTableIndex, m_cbptab;
    private int m_cAcTableIndex, m_yAcTableIndex, m_dcTableIndex;
    private int m_bfraction, m_condover;
    private bool m_mvTypeIsRaw, m_skipIsRaw, m_dmbIsRaw, m_acpredIsRaw, m_overflgIsRaw;

    // Intensity compensation: tables for reading the last and the next reference
    private readonly byte[] m_lastLuty = new byte[256], m_lastLutuv = new byte[256];
    private readonly byte[] m_nextLuty = new byte[256], m_nextLutuv = new byte[256];
    private byte[] m_auxLuty = new byte[256], m_auxLutuv = new byte[256];
    private int m_lastUseIc, m_nextUseIc;

    private static void InitLut(int lumscale, int lumshift, byte[] luty, byte[] lutuv, bool chain)
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

    /// <summary>rotate_luts: the tables move with the references; the current picture's start as identity.</summary>
    private void RotateLuts()
    {
        if (m_pictType is TypeB or TypeBI)
        {
            InitLut(32, 0, m_auxLuty, m_auxLutuv, false);
            return;
        }
        // last <-> next: the old next reference becomes the last; the current picture (next) starts clean
        Span<byte> tmp = stackalloc byte[256];
        m_lastLuty.CopyTo(tmp);
        m_nextLuty.CopyTo(m_lastLuty);
        tmp.CopyTo(m_nextLuty);
        m_lastLutuv.CopyTo(tmp);
        m_nextLutuv.CopyTo(m_lastLutuv);
        tmp.CopyTo(m_nextLutuv);
        m_lastUseIc = m_nextUseIc;
        InitLut(32, 0, m_nextLuty, m_nextLutuv, false);
        m_nextUseIc = 0;
    }

    private int ReadBfraction()
    {
        int index = GetBits(3);
        if (index == 7)
            index = 7 + GetBits(4);
        if (index == 21)
            throw new InvalidDataException("Bad BFRACTION");
        return Vc1Tables.BfractionLut[index];
    }

    /// <summary>The quantizer of the picture (PQINDEX, HALFQP, PQUANTIZER).</summary>
    private void ReadQuantizer()
    {
        if (BitsLeft < 5)
            throw new InvalidDataException("Picture header cut short");
        int pqindex = GetBits(5);
        if (pqindex == 0)
            throw new InvalidDataException("PQINDEX 0");
        m_pq = Vc1Tables.PquantTable[(m_quantizerMode == 0 ? 0 : 1) * 32 + pqindex];
        m_pqindex = pqindex;
        m_halfpq = pqindex < 9 ? GetBit() : 0;
        m_pquantizer = m_quantizerMode switch
        {
            0 => pqindex < 9 ? 1 : 0,
            2 => 0,
            1 => GetBit(),
            _ => 1,
        };
        m_dquantfrm = 0;
    }

    private void SetMvRange()
    {
        m_kx = m_mvrange + 9 + (m_mvrange >> 1);
        m_ky = m_mvrange + 8;
        m_rangeX = 1 << (m_kx - 1);
        m_rangeY = 1 << (m_ky - 1);
    }

    /// <summary>The P picture's MV mode, intensity compensation and quarter / half sample flags.</summary>
    private void ReadPMvMode()
    {
        int lowquant = m_pq > 12 ? 0 : 1;
        m_mvMode = Vc1Tables.MvPmode[lowquant * 5 + GetUnary(1, 4)];
        if (m_mvMode == MvIntensityComp)
        {
            m_mvMode2 = Vc1Tables.MvPmode2[lowquant * 4 + GetUnary(1, 3)];
            int lumscale = GetBits(6), lumshift = GetBits(6);
            m_lastUseIc = 1;
            InitLut(lumscale, lumshift, m_lastLuty, m_lastLutuv, true);
        }
        int mode = m_mvMode == MvIntensityComp ? m_mvMode2 : m_mvMode;
        m_quarterSample = mode is not (Mv1MvHpel or Mv1MvHpelBilin) ? 1 : 0;
        m_mspel = mode != Mv1MvHpelBilin ? 1 : 0;
    }

    private bool MixedMvPicture => m_mvMode == MvMixedMv || (m_mvMode == MvIntensityComp && m_mvMode2 == MvMixedMv);

    private void ReadTransformType()
    {
        if (m_vstransform != 0)
        {
            m_ttmbf = GetBit();
            m_ttfrm = m_ttmbf != 0 ? Vc1Tables.TtfrmToTt[GetBits(2)] : 0;
        }
        else
        {
            m_ttmbf = 1;
            m_ttfrm = Tt8x8;
        }
    }

    /// <summary>vop_dquant_decoding.</summary>
    private void ReadVopDquant()
    {
        if (m_dquant != 2)
        {
            m_dquantfrm = GetBit();
            if (m_dquantfrm == 0)
                return;
            m_dqprofile = GetBits(2);
            if (m_dqprofile is 1 or 2)
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

    /// <summary>ff_vc1_parse_frame_header (Simple / Main profiles).</summary>
    private void ParseFrameHeader()
    {
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
        if (m_pictType is TypeI or TypeBI)
            GetBits(7); // buffer fullness
        if (m_pictType is TypeI or TypeBI)
            m_rnd = 1;
        if (m_pictType == TypeP)
            m_rnd ^= 1;
        ReadQuantizer();
        if (m_extendedMv == 1)
            m_mvrange = GetUnary(0, 3);
        SetMvRange();
        if (m_resX8 != 0 && m_pictType is TypeI or TypeBI && GetBit() != 0)
            throw new InvalidDataException("X8 intra pictures");
        RotateLuts();
        if (m_pictType == TypeP)
        {
            m_ttIndex = (m_pq > 4 ? 1 : 0) + (m_pq > 12 ? 1 : 0);
            ReadPMvMode();
            if (MixedMvPicture)
                m_mvTypeIsRaw = BitplaneDecoding(m_mvTypePlane);
            else
            {
                m_mvTypeIsRaw = false;
                Array.Clear(m_mvTypePlane);
            }
            m_skipIsRaw = BitplaneDecoding(m_skipPlane);
            if (BitsLeft < 4)
                throw new InvalidDataException("Picture header cut short");
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
        if (m_pictType is TypeI or TypeBI)
            m_yAcTableIndex = Decode012();
        m_dcTableIndex = GetBit();
        if (m_pictType == TypeBI)
        {
            m_pictType = TypeB;
            m_biType = 1;
        }
    }

    /// <summary>ff_vc1_parse_frame_header_adv for progressive pictures.</summary>
    private void ParseFrameHeaderAdvanced()
    {
        int ptype = GetUnary(0, 4);
        m_pictType = ptype switch
        {
            0 => TypeP,
            1 => TypeB,
            2 => TypeI,
            3 => TypeBI,
            _ => TypeP,
        };
        // a skipped picture (1111): the last one again
        m_pSkipped = ptype == 4 ? 1 : 0;
        if (m_tfcntrflag != 0)
            GetBits(8);
        if (m_broadcast != 0)
            GetBits(2); // rptfrm
        if (m_panscanflag != 0)
            throw new InvalidDataException("Pan-scan windows");
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
                if (MixedMvPicture)
                    m_mvTypeIsRaw = BitplaneDecoding(m_mvTypePlane);
                else
                {
                    m_mvTypeIsRaw = false;
                    Array.Clear(m_mvTypePlane);
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
        if (m_pictType is TypeI or TypeBI)
            m_yAcTableIndex = Decode012();
        m_dcTableIndex = GetBit();
        if (m_pictType is TypeI or TypeBI && m_dquant != 0)
            ReadVopDquant();
        m_biType = m_pictType == TypeBI ? 1 : 0;
        if (m_biType != 0)
            m_pictType = TypeB;
    }

    /// <summary>bitplane_decoding into a macroblock plane; true when the bits are in the macroblocks instead (raw).</summary>
    private bool BitplaneDecoding(byte[] plane)
    {
        int width = m_mbWidth, height = m_mbHeight, stride = m_mbStride;
        int invert = GetBit();
        int imode = GetVlc(s_imodeVlc);
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
                    plane[p++] = (byte)GetBit();
                    y = offset = 1;
                    if (offset == width)
                    {
                        offset = 0;
                        p += stride - width;
                    }
                }
                for (; y < height * width; y += 2)
                {
                    int code = GetVlc(s_norm2Vlc);
                    plane[p++] = (byte)(code & 1);
                    if (++offset == width)
                    {
                        offset = 0;
                        p += stride - width;
                    }
                    plane[p++] = (byte)(code >> 1);
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
                            int code = GetVlc(s_norm6Vlc);
                            if (code < 0)
                                throw new InvalidDataException("Bad NORM-6 code");
                            plane[p + x] = (byte)(code & 1);
                            plane[p + x + 1] = (byte)((code >> 1) & 1);
                            plane[p + x + stride] = (byte)((code >> 2) & 1);
                            plane[p + x + 1 + stride] = (byte)((code >> 3) & 1);
                            plane[p + x + stride * 2] = (byte)((code >> 4) & 1);
                            plane[p + x + 1 + stride * 2] = (byte)((code >> 5) & 1);
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
                            int code = GetVlc(s_norm6Vlc);
                            if (code < 0)
                                throw new InvalidDataException("Bad NORM-6 code");
                            plane[p + x] = (byte)(code & 1);
                            plane[p + x + 1] = (byte)((code >> 1) & 1);
                            plane[p + x + 2] = (byte)((code >> 2) & 1);
                            plane[p + x + stride] = (byte)((code >> 3) & 1);
                            plane[p + x + 1 + stride] = (byte)((code >> 4) & 1);
                            plane[p + x + 2 + stride] = (byte)((code >> 5) & 1);
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
                throw new InvalidDataException("Bad bitplane mode");
        }
        if (imode is 2 or 4)
        {
            // the differential operator
            plane[0] ^= (byte)invert;
            for (int x = 1; x < width; x++)
                plane[x] ^= plane[x - 1];
            for (int y = 1; y < height; y++)
            {
                int p = y * stride;
                plane[p] ^= plane[p - stride];
                for (int x = 1; x < width; x++)
                {
                    if (plane[p + x - 1] != plane[p + x - stride])
                        plane[p + x] ^= (byte)invert;
                    else
                        plane[p + x] ^= plane[p + x - 1];
                }
            }
        }
        else if (invert != 0)
        {
            for (int i = 0; i < stride * height; i++)
                plane[i] = (byte)(plane[i] == 0 ? 1 : 0);
        }
        return false;
    }

    private void RowSkip(byte[] plane, int at, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++, at += stride)
        {
            if (GetBit() == 0)
                plane.AsSpan(at, width).Clear();
            else
                for (int x = 0; x < width; x++)
                    plane[at + x] = (byte)GetBit();
        }
    }

    private void ColSkip(byte[] plane, int at, int width, int height, int stride)
    {
        for (int x = 0; x < width; x++, at++)
        {
            if (GetBit() == 0)
                for (int y = 0; y < height; y++)
                    plane[at + y * stride] = 0;
            else
                for (int y = 0; y < height; y++)
                    plane[at + y * stride] = (byte)GetBit();
        }
    }

    #endregion

    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
