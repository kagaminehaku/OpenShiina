// MPEG-1 movies (ISO/IEC 11172), the format of every movie of the GRAND†CROSS games (mv\*.mpg:
// MPEG-1 system streams, 800 x 600 or 1280 x 720 at 30 frames a second, some with MPEG-1
// Layer II sound). The engine plays them through DirectShow into a DirectDraw surface; this is
// a decoder of our own so that every platform shows the same picture:
//   MpegSystemStream - splits a system stream into its first video and first audio stream
//   Mpeg1Video       - decodes the video stream into YCbCr frames in display order

using System.Runtime.Intrinsics;

namespace OpenShiina.Formats;

/// <summary>The elementary streams of an MPEG-1 (or MPEG-2 program) system stream.</summary>
public sealed class MpegSystemStream
{
    /// <summary>The first video stream (0xE0-0xEF), or empty.</summary>
    public byte[] Video { get; }

    /// <summary>The first audio stream (0xC0-0xDF), or empty.</summary>
    public byte[] Audio { get; }

    public MpegSystemStream(byte[] file)
    {
        var video = new MemoryStream();
        var audio = new MemoryStream();
        int videoId = -1, audioId = -1;
        int pos = 0, n = file.Length;
        while (pos + 4 <= n)
        {
            if (file[pos] != 0 || file[pos + 1] != 0 || file[pos + 2] != 1)
            {
                pos++;
                continue;
            }
            int code = file[pos + 3];
            if (code == 0xB9)
                break;
            if (code == 0xBA)
            {
                if (pos + 5 > n)
                    break;
                // MPEG-1 pack header: 12 bytes; MPEG-2: 14 plus stuffing
                if ((file[pos + 4] & 0xC0) == 0x40)
                    pos += 14 + (pos + 13 < n ? file[pos + 13] & 7 : 0);
                else
                    pos += 12;
                continue;
            }
            if (code < 0xBB || pos + 6 > n)
            {
                pos += 4;
                continue;
            }
            int length = file[pos + 4] << 8 | file[pos + 5];
            int start = pos + 6, end = Math.Min(start + length, n);
            pos = end;
            bool isVideo = code is >= 0xE0 and <= 0xEF, isAudio = code is >= 0xC0 and <= 0xDF;
            if (!isVideo && !isAudio)
                continue;
            if (isVideo && (videoId == -1 ? (videoId = code) : videoId) != code)
                continue;
            if (isAudio && (audioId == -1 ? (audioId = code) : audioId) != code)
                continue;
            int p = start;
            if (p < end && (file[p] & 0xC0) == 0x80)
            {
                // MPEG-2 PES header
                if (p + 3 > end)
                    continue;
                p += 3 + file[p + 2];
            }
            else
            {
                while (p < end && file[p] == 0xFF)
                    p++;
                if (p < end && (file[p] & 0xC0) == 0x40)
                    p += 2;
                if (p < end)
                {
                    int flags = file[p] & 0xF0;
                    p += flags == 0x20 ? 5 : flags == 0x30 ? 10 : 1;
                }
            }
            if (p < end)
                (isVideo ? video : audio).Write(file, p, end - p);
        }
        Video = video.ToArray();
        Audio = audio.ToArray();
    }
}

/// <summary>A decoded picture: Y at full size, Cb and Cr at half size, macroblock-aligned planes.</summary>
public sealed class MpegFrame
{
    public readonly int Width, Height, Stride, ChromaStride;
    public readonly byte[] Y, Cb, Cr;

    public MpegFrame(int width, int height, int mbWidth, int mbHeight)
    {
        Width = width;
        Height = height;
        Stride = mbWidth * 16;
        ChromaStride = mbWidth * 8;
        Y = new byte[Stride * mbHeight * 16];
        Cb = new byte[ChromaStride * mbHeight * 8];
        Cr = new byte[ChromaStride * mbHeight * 8];
    }

    /// <summary>
    /// The picture as 24-bit BGR (ITU-R BT.601, video range, each chroma sample covering 2 x 2
    /// pixels), <paramref name="width"/> x <paramref name="height"/> pixels from the top left.
    /// </summary>
    public unsafe void ToBgr24(Span<byte> dst, int pitch, int width, int height)
    {
        width = Math.Min(width, Width);
        height = Math.Min(height, Height);
        if (width <= 0 || height <= 0)
            return;
        // Every row is checked to fit before any is written (the rows are written through a pointer)
        _ = dst.Slice((height - 1) * pitch, width * 3);
        fixed (byte* start = dst)
        {
            nint at = (nint)start;
            void Rows(int from, int to)
            {
                for (int y = from; y < to; y++)
                {
                    var row = new Span<byte>((byte*)at + (nint)y * pitch, width * 3);
                    int yi = y * Stride, ci = (y >> 1) * ChromaStride;
                    int x = Vector128.IsHardwareAccelerated ? RowOnVectors(row, yi, ci, width) : 0;
                    for (; x < width; x++)
                    {
                        int c = ci + (x >> 1);
                        int l = (Y[yi + x] - 16) * 76309;
                        int cb = Cb[c] - 128, cr = Cr[c] - 128;
                        row[x * 3] = Clamp((l + 132201 * cb + 32768) >> 16);
                        row[x * 3 + 1] = Clamp((l - 25675 * cb - 53279 * cr + 32768) >> 16);
                        row[x * 3 + 2] = Clamp((l + 104597 * cr + 32768) >> 16);
                    }
                }
            }
            // The rows on several cores (half of a 1280 x 720 movie's time on one)
            if (pitch >= width * 3)
                ParallelRows.For(height, (long)width * height, Rows);
            else
                Rows(0, height);
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // B, G and R of four pixels (lanes 0-3, 4-7, 8-11 after the two narrowings) in pixel order
    private static readonly Vector128<byte> s_bgrOrder = Vector128.Create((byte)0, 4, 8, 1, 5, 9, 2, 6, 10, 3, 7, 11, 12, 13, 14, 15);

    /// <summary>
    /// The pixels of a row four at a time, as the formula of ToBgr24 on 32-bit lanes; returns
    /// how many it did (it leaves at least the last four bytes of the row to the formula, as it
    /// stores twelve bytes of sixteen).
    /// </summary>
    private int RowOnVectors(Span<byte> row, int yi, int ci, int width)
    {
        var sixteen = Vector128.Create(16);
        var round = Vector128.Create(32768);
        var zero = Vector128<int>.Zero;
        var max = Vector128.Create(255);
        int x = 0;
        for (; x + 4 <= width && x * 3 + 16 <= row.Length; x += 4)
        {
            int c = ci + (x >> 1);
            var luma = Vector128.Create((int)Y[yi + x], Y[yi + x + 1], Y[yi + x + 2], Y[yi + x + 3]);
            var cb = Vector128.Create(Cb[c] - 128, Cb[c] - 128, Cb[c + 1] - 128, Cb[c + 1] - 128);
            var cr = Vector128.Create(Cr[c] - 128, Cr[c] - 128, Cr[c + 1] - 128, Cr[c + 1] - 128);
            var l = (luma - sixteen) * Vector128.Create(76309);
            var b = Vector128.ShiftRightArithmetic(l + cb * Vector128.Create(132201) + round, 16);
            var g = Vector128.ShiftRightArithmetic(l - cb * Vector128.Create(25675) - cr * Vector128.Create(53279) + round, 16);
            var r = Vector128.ShiftRightArithmetic(l + cr * Vector128.Create(104597) + round, 16);
            b = Vector128.Min(Vector128.Max(b, zero), max);
            g = Vector128.Min(Vector128.Max(g, zero), max);
            r = Vector128.Min(Vector128.Max(r, zero), max);
            var bytes = Vector128.Shuffle(Vector128.Narrow(Vector128.Narrow(b, g).AsUInt16(), Vector128.Narrow(r, zero).AsUInt16()), s_bgrOrder);
            System.Runtime.InteropServices.MemoryMarshal.Write(row[(x * 3)..], bytes.AsUInt64().ToScalar());
            System.Runtime.InteropServices.MemoryMarshal.Write(row[(x * 3 + 8)..], bytes.AsUInt32().GetElement(2));
        }
        return x;
    }
}

/// <summary>An MPEG-1 video stream decoder (ISO/IEC 11172-2); frames come out in display order.</summary>
public sealed class Mpeg1Video
{
    private readonly byte[] m_data;
    private int m_bit;              // read position in bits
    private readonly int m_end;     // end of the data in bits

    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Frames a second (picture_rate of the sequence header).</summary>
    public double FrameRate { get; private set; } = 30;
    /// <summary>Slices that did not decode cleanly so far.</summary>
    public int Errors => m_errors;
    private int m_errors;

    // The slices of the picture being decoded (vertical position, bit after the start code)
    private readonly List<(int Code, int Bit)> m_slices = [];
    private SliceDecoder? m_slice;

    private int m_mbWidth, m_mbHeight;
    private readonly int[] m_intraMatrix = new int[64];
    private readonly int[] m_nonIntraMatrix = new int[64];

    // Anchors (I / P pictures): the older one and the newer one; B pictures go to a third frame
    private MpegFrame? m_older, m_newer, m_anchor0, m_anchor1, m_bFrame;
    private bool m_flushed;

    // State of the picture being decoded
    private int m_pictureType;
    private bool m_fullPelForward, m_fullPelBackward;
    private int m_forwardRSize, m_backwardRSize;
    private MpegFrame m_current = null!, m_forwardRef = null!, m_backwardRef = null!;

    private static readonly double[] s_frameRates = [0, 24000.0 / 1001, 24, 25, 30000.0 / 1001, 30, 50, 60000.0 / 1001, 60, 0, 0, 0, 0, 0, 0, 0];

    public Mpeg1Video(byte[] stream)
    {
        // Padding so that peeking past the end reads zeros
        m_data = new byte[stream.Length + 8];
        stream.CopyTo(m_data, 0);
        m_end = stream.Length * 8;
        if (!NextStartCode(0xB3))
            throw new InvalidDataException("No MPEG-1 sequence header");
        ReadSequenceHeader();
        m_bit = 0;
    }

    /// <summary>Back to the first picture.</summary>
    public void Rewind()
    {
        m_bit = 0;
        m_older = m_newer = null;
        m_flushed = false;
    }

    /// <summary>
    /// The next frame in display order, or null at the end. The frame stays valid until the
    /// next call.
    /// </summary>
    public MpegFrame? Next()
    {
        while (true)
        {
            int code = NextStartCode(-1) ? m_data[(m_bit >> 3) - 1] : -1;
            if (code == -1)
            {
                if (!m_flushed && m_newer != null)
                {
                    m_flushed = true;
                    return m_newer;
                }
                return null;
            }
            if (code == 0xB3)
                ReadSequenceHeader();
            else if (code == 0x00)
            {
                var frame = DecodePicture();
                if (frame != null)
                    return frame;
            }
        }
    }

    #region Bits

    private int Peek(int count)
    {
        int b = m_bit >> 3;
        uint v = (uint)(m_data[b] << 24 | m_data[b + 1] << 16 | m_data[b + 2] << 8 | m_data[b + 3]);
        v = (v << (m_bit & 7)) | (uint)(m_data[b + 4] >> (8 - (m_bit & 7)));
        return (int)(v >> (32 - count));
    }

    private int Read(int count)
    {
        int v = Peek(count);
        m_bit += count;
        return v;
    }

    private bool ReadBit() => Read(1) != 0;

    /// <summary>
    /// Moves just past the next start code (00 00 01 xx) from the next byte boundary; with
    /// <paramref name="code"/> >= 0, past the next start code of that value. False at the end.
    /// </summary>
    private bool NextStartCode(int code)
    {
        int p = (m_bit + 7) >> 3, end = m_end >> 3;
        for (; p + 3 < end; p++)
        {
            if (m_data[p] == 0 && m_data[p + 1] == 0 && m_data[p + 2] == 1 && (code < 0 || m_data[p + 3] == code))
            {
                m_bit = (p + 4) * 8;
                return true;
            }
        }
        m_bit = m_end;
        return false;
    }

    // The rest of a slice is zero bits up to the next start code
    private bool AtStartCode => Peek(23) == 0;

    private int ReadVlc(int[] table, int bits)
    {
        int entry = table[Peek(bits)];
        if (entry == 0)
            throw new InvalidDataException("bad VLC");
        m_bit += entry & 0x1F;
        return entry >> 5;
    }

    #endregion

    #region Headers

    private void ReadSequenceHeader()
    {
        int width = Read(12), height = Read(12);
        Read(4);                            // aspect ratio
        double rate = s_frameRates[Read(4)];
        Read(18);                           // bit rate
        Read(1);
        Read(10);                           // VBV buffer size
        Read(1);                            // constrained parameters
        if (ReadBit())
            for (int i = 0; i < 64; i++)
                m_intraMatrix[s_zigZag[i]] = Read(8);
        else
            s_defaultIntra.CopyTo(m_intraMatrix, 0);
        if (ReadBit())
            for (int i = 0; i < 64; i++)
                m_nonIntraMatrix[s_zigZag[i]] = Read(8);
        else
            Array.Fill(m_nonIntraMatrix, 16);
        if (rate > 0)
            FrameRate = rate;
        if (width != Width || height != Height || m_bFrame == null)
        {
            Width = width;
            Height = height;
            m_mbWidth = (width + 15) >> 4;
            m_mbHeight = (height + 15) >> 4;
            m_older = m_newer = null;
            m_anchor0 = NewFrame();
            m_anchor1 = NewFrame();
            m_bFrame = NewFrame();
        }
    }

    private MpegFrame NewFrame() => new(Width, Height, m_mbWidth, m_mbHeight);

    private const int PictureI = 1, PictureP = 2, PictureB = 3;

    private MpegFrame? DecodePicture()
    {
        Read(10);                           // temporal reference
        m_pictureType = Read(3);
        Read(16);                           // VBV delay
        if (m_pictureType is not (PictureI or PictureP or PictureB))
            return null;
        if (m_pictureType != PictureI)
        {
            m_fullPelForward = ReadBit();
            m_forwardRSize = Read(3) - 1;
            if (m_forwardRSize < 0)
                return null;
        }
        if (m_pictureType == PictureB)
        {
            m_fullPelBackward = ReadBit();
            m_backwardRSize = Read(3) - 1;
            if (m_backwardRSize < 0)
                return null;
        }
        MpegFrame? shown = null;
        if (m_pictureType == PictureB)
        {
            // Without two anchors (an open GOP at the start) the picture cannot be made
            if (m_older == null || m_newer == null)
                return null;
            m_current = m_bFrame!;
            m_forwardRef = m_older;
            m_backwardRef = m_newer;
        }
        else
        {
            if (m_pictureType == PictureP && m_newer == null)
                return null;
            // The older anchor is not needed any more (the B pictures before this one are done)
            m_current = m_newer == m_anchor0 ? m_anchor1! : m_anchor0!;
            m_forwardRef = m_newer!;
            m_backwardRef = m_newer!;
        }

        // Slices up to the next picture, GOP or sequence start code: found first, then decoded
        // on all cores (each writes macroblocks of its own)
        m_slices.Clear();
        while (true)
        {
            if (!NextStartCode(-1))
                break;
            int code = m_data[(m_bit >> 3) - 1];
            if (code is >= 0x01 and <= 0xAF)
                m_slices.Add((code, m_bit));
            else if (code is 0xB2 or 0xB5)
                continue;                   // user data, extension
            else
            {
                m_bit = (m_bit >> 3) * 8 - 32;
                break;
            }
        }
        void DecodeOne(SliceDecoder decoder, int index)
        {
            try
            {
                decoder.Decode(m_slices[index].Code, m_slices[index].Bit);
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException)
            {
                Interlocked.Increment(ref m_errors);
            }
        }
        if (m_slices.Count >= 2)
            Parallel.For(0, m_slices.Count, () => new SliceDecoder(this), (index, _, decoder) =>
            {
                DecodeOne(decoder, index);
                return decoder;
            }, _ => { });
        else
        {
            m_slice ??= new SliceDecoder(this);
            for (int index = 0; index < m_slices.Count; index++)
                DecodeOne(m_slice, index);
        }

        if (m_pictureType == PictureB)
            return m_current;
        shown = m_newer;
        m_older = m_newer;
        m_newer = m_current;
        return shown;
    }

    #endregion

    #region Slices and macroblocks

    /// <summary>
    /// The decoding of slices: a picture's slices write macroblocks of their own, so each runs
    /// on a decoder of its own (bit position, quantiser, motion predictors, DC predictors, block)
    /// and the picture's slices are shared out between the cores. The picture (its type, motion
    /// sizes, reference frames, matrices) is the video's, only read here.
    /// </summary>
    private sealed class SliceDecoder(Mpeg1Video video)
    {
        private readonly byte[] m_data = video.m_data;
        private readonly int m_end = video.m_end;
        private int m_bit;

        private int m_pictureType => video.m_pictureType;
        private bool m_fullPelForward => video.m_fullPelForward;
        private bool m_fullPelBackward => video.m_fullPelBackward;
        private int m_forwardRSize => video.m_forwardRSize;
        private int m_backwardRSize => video.m_backwardRSize;
        private MpegFrame m_current => video.m_current;
        private MpegFrame m_forwardRef => video.m_forwardRef;
        private MpegFrame m_backwardRef => video.m_backwardRef;
        private int m_mbWidth => video.m_mbWidth;
        private int m_mbHeight => video.m_mbHeight;
        private int[] m_intraMatrix => video.m_intraMatrix;
        private int[] m_nonIntraMatrix => video.m_nonIntraMatrix;

        private int m_quantScale;
        private int m_mbAddress, m_mbRow, m_mbCol;
        private int m_forwardH, m_forwardV, m_backwardH, m_backwardV;  // predictors, full or half pel
        private int m_mvForwardH, m_mvForwardV, m_mvBackwardH, m_mvBackwardV;  // half pel
        private bool m_mbForward, m_mbBackward;
        private int m_dcY, m_dcCb, m_dcCr;
        private readonly int[] m_block = new int[64];

        /// <summary>The slice whose start code (vertical position <paramref name="code"/>) ends at bit <paramref name="bit"/>.</summary>
        public void Decode(int code, int bit)
        {
            m_bit = bit;
            DecodeSlice(code);
        }

        private int Peek(int count)
        {
            int b = m_bit >> 3;
            uint v = (uint)(m_data[b] << 24 | m_data[b + 1] << 16 | m_data[b + 2] << 8 | m_data[b + 3]);
            v = (v << (m_bit & 7)) | (uint)(m_data[b + 4] >> (8 - (m_bit & 7)));
            return (int)(v >> (32 - count));
        }

        private int Read(int count)
        {
            int v = Peek(count);
            m_bit += count;
            return v;
        }

        private bool ReadBit() => Read(1) != 0;

        private bool AtStartCode => Peek(23) == 0;

        private int ReadVlc(int[] table, int bits)
        {
            int entry = table[Peek(bits)];
            if (entry == 0)
                throw new InvalidDataException("bad VLC");
            m_bit += entry & 0x1F;
            return entry >> 5;
        }

        private void DecodeSlice(int verticalPosition)
        {
            m_quantScale = Read(5);
            while (ReadBit())
                Read(8);
            m_mbAddress = (verticalPosition - 1) * m_mbWidth - 1;
            ResetMotion();
            ResetDc();
            bool first = true;
            do
            {
                DecodeMacroblock(first);
                first = false;
            }
            while (m_bit < m_end && !AtStartCode);
        }

        private void ResetMotion() => m_forwardH = m_forwardV = m_backwardH = m_backwardV = 0;

        private void ResetDc() => m_dcY = m_dcCb = m_dcCr = 1024;

        private void DecodeMacroblock(bool first)
        {
            int increment = 0;
            while (true)
            {
                int v = ReadVlc(s_addressIncrement, 11);
                if (v == 34)
                    continue;                   // stuffing
                if (v == 35)
                {
                    increment += 33;            // escape
                    continue;
                }
                increment += v;
                break;
            }
            if (first)
                m_mbAddress += increment;
            else
            {
                // Skipped macroblocks
                for (int k = 1; k < increment; k++)
                {
                    m_mbAddress++;
                    SetPosition();
                    ResetDc();
                    if (m_pictureType == PictureP)
                    {
                        m_forwardH = m_forwardV = 0;
                        m_mvForwardH = m_mvForwardV = 0;
                        m_mbForward = true;
                        m_mbBackward = false;
                        Predict();
                    }
                    else if (m_pictureType == PictureB)
                        Predict();
                    else
                        throw new InvalidDataException("skipped macroblock in an I picture");
                }
                m_mbAddress++;
            }
            SetPosition();
            if (m_mbRow >= m_mbHeight)
                throw new InvalidDataException("macroblock outside the picture");

            int type = m_pictureType switch
            {
                PictureI => ReadVlc(s_typeI, 2),
                PictureP => ReadVlc(s_typeP, 6),
                _ => ReadVlc(s_typeB, 6),
            };
            bool intra = (type & 1) != 0;
            if ((type & 0x10) != 0)
                m_quantScale = Read(5);
            if (intra)
            {
                ResetMotion();
                m_mbForward = m_mbBackward = false;
            }
            else
            {
                ResetDc();
                m_mbForward = (type & 8) != 0;
                m_mbBackward = (type & 4) != 0;
                if (m_mbForward)
                {
                    m_forwardH = DecodeMotion(m_forwardRSize, m_forwardH);
                    m_forwardV = DecodeMotion(m_forwardRSize, m_forwardV);
                    m_mvForwardH = m_fullPelForward ? m_forwardH << 1 : m_forwardH;
                    m_mvForwardV = m_fullPelForward ? m_forwardV << 1 : m_forwardV;
                }
                else if (m_pictureType == PictureP)
                {
                    // P macroblock without motion: zero vector, the predictor starts again
                    m_forwardH = m_forwardV = 0;
                    m_mvForwardH = m_mvForwardV = 0;
                    m_mbForward = true;
                }
                if (m_mbBackward)
                {
                    m_backwardH = DecodeMotion(m_backwardRSize, m_backwardH);
                    m_backwardV = DecodeMotion(m_backwardRSize, m_backwardV);
                    m_mvBackwardH = m_fullPelBackward ? m_backwardH << 1 : m_backwardH;
                    m_mvBackwardV = m_fullPelBackward ? m_backwardV << 1 : m_backwardV;
                }
                Predict();
            }

            int pattern = intra ? 0x3F : (type & 2) != 0 ? ReadVlc(s_codedBlockPattern, 9) : 0;
            for (int b = 0; b < 6; b++)
            {
                if ((pattern & (0x20 >> b)) == 0)
                    continue;
                DecodeBlock(b, intra);
            }
        }

        private void SetPosition()
        {
            m_mbRow = m_mbAddress / m_mbWidth;
            m_mbCol = m_mbAddress % m_mbWidth;
        }

        private int DecodeMotion(int rSize, int predictor)
        {
            int code = ReadVlc(s_motion, 11);
            if (code != 0 && ReadBit())
                code = -code;
            int f = 1 << rSize;
            int delta;
            if (f != 1 && code != 0)
            {
                int r = Read(rSize);
                delta = ((Math.Abs(code) - 1) << rSize) + r + 1;
                if (code < 0)
                    delta = -delta;
            }
            else
                delta = code;
            int v = predictor + delta;
            if (v > (f << 4) - 1)
                v -= f << 5;
            else if (v < -(f << 4))
                v += f << 5;
            return v;
        }

        /// <summary>The prediction of the macroblock from the reference pictures (no residual yet).</summary>
        private void Predict()
        {
            if (m_mbForward)
            {
                PredictFrom(m_forwardRef, m_mvForwardH, m_mvForwardV, false);
                if (m_mbBackward)
                    PredictFrom(m_backwardRef, m_mvBackwardH, m_mvBackwardV, true);
            }
            else if (m_mbBackward)
                PredictFrom(m_backwardRef, m_mvBackwardH, m_mvBackwardV, false);
        }

        private void PredictFrom(MpegFrame reference, int h, int v, bool average)
        {
            var cur = m_current;
            PredictBlock(reference.Y, cur.Y, cur.Stride, m_mbHeight * 16, m_mbCol * 16, m_mbRow * 16, 16, h, v, average);
            h /= 2;
            v /= 2;
            PredictBlock(reference.Cb, cur.Cb, cur.ChromaStride, m_mbHeight * 8, m_mbCol * 8, m_mbRow * 8, 8, h, v, average);
            PredictBlock(reference.Cr, cur.Cr, cur.ChromaStride, m_mbHeight * 8, m_mbCol * 8, m_mbRow * 8, 8, h, v, average);
        }

        private static void PredictBlock(byte[] src, byte[] dst, int stride, int rows, int x0, int y0, int size, int h, int v, bool average)
        {
            int sx = x0 + (h >> 1), sy = y0 + (v >> 1);
            bool hx = (h & 1) != 0, hy = (v & 1) != 0;
            int maxX = stride - 1, maxY = rows - 1;
            bool inside = sx >= 0 && sy >= 0 && sx + size + (hx ? 1 : 0) <= stride && sy + size + (hy ? 1 : 0) <= rows;
            if (inside)
            {
                // The common case, a formula a loop
                for (int y = 0; y < size; y++)
                {
                    int d = (y0 + y) * stride + x0, s = (sy + y) * stride + sx;
                    var to = dst.AsSpan(d, size);
                    var a = src.AsSpan(s, hx ? size + 1 : size);
                    if (!hx && !hy)
                    {
                        if (!average)
                        {
                            a[..size].CopyTo(to);
                            continue;
                        }
                        for (int x = 0; x < size; x++)
                            to[x] = (byte)((to[x] + a[x] + 1) >> 1);
                        continue;
                    }
                    if (!hy)
                    {
                        for (int x = 0; x < size; x++)
                        {
                            int p = (a[x] + a[x + 1] + 1) >> 1;
                            to[x] = average ? (byte)((to[x] + p + 1) >> 1) : (byte)p;
                        }
                        continue;
                    }
                    var b = src.AsSpan(s + stride, hx ? size + 1 : size);
                    if (!hx)
                    {
                        for (int x = 0; x < size; x++)
                        {
                            int p = (a[x] + b[x] + 1) >> 1;
                            to[x] = average ? (byte)((to[x] + p + 1) >> 1) : (byte)p;
                        }
                        continue;
                    }
                    for (int x = 0; x < size; x++)
                    {
                        int p = (a[x] + a[x + 1] + b[x] + b[x + 1] + 2) >> 2;
                        to[x] = average ? (byte)((to[x] + p + 1) >> 1) : (byte)p;
                    }
                }
                return;
            }
            for (int y = 0; y < size; y++)
            {
                int d = (y0 + y) * stride + x0;
                for (int x = 0; x < size; x++)
                {
                    int p;
                    if (inside)
                    {
                        int s = (sy + y) * stride + sx + x;
                        if (!hx && !hy)
                            p = src[s];
                        else if (hx && !hy)
                            p = (src[s] + src[s + 1] + 1) >> 1;
                        else if (!hx)
                            p = (src[s] + src[s + stride] + 1) >> 1;
                        else
                            p = (src[s] + src[s + 1] + src[s + stride] + src[s + stride + 1] + 2) >> 2;
                    }
                    else
                    {
                        int ax = Math.Clamp(sx + x, 0, maxX), bx = Math.Clamp(sx + x + 1, 0, maxX);
                        int ay = Math.Clamp(sy + y, 0, maxY), by = Math.Clamp(sy + y + 1, 0, maxY);
                        int a = src[ay * stride + ax];
                        if (!hx && !hy)
                            p = a;
                        else if (hx && !hy)
                            p = (a + src[ay * stride + bx] + 1) >> 1;
                        else if (!hx)
                            p = (a + src[by * stride + ax] + 1) >> 1;
                        else
                            p = (a + src[ay * stride + bx] + src[by * stride + ax] + src[by * stride + bx] + 2) >> 2;
                    }
                    dst[d + x] = average ? (byte)((dst[d + x] + p + 1) >> 1) : (byte)p;
                }
            }
        }

        private void DecodeBlock(int b, bool intra)
        {
            var block = m_block;
            Array.Clear(block);
            int n;
            int[] matrix;
            if (intra)
            {
                int size = b < 4 ? ReadVlc(s_dcSizeLuma, 7) : ReadVlc(s_dcSizeChroma, 8);
                int diff = 0;
                if (size > 0)
                {
                    diff = Read(size);
                    if ((diff & (1 << (size - 1))) == 0)
                        diff -= (1 << size) - 1;
                }
                ref int predictor = ref b < 4 ? ref m_dcY : ref b == 4 ? ref m_dcCb : ref m_dcCr;
                predictor += diff * 8;
                block[0] = predictor;
                n = 1;
                matrix = m_intraMatrix;
            }
            else
            {
                n = 0;
                matrix = m_nonIntraMatrix;
            }

            while (true)
            {
                int run, level;
                if (n == 0 && !intra && Peek(1) == 1)
                {
                    // dct_coeff_first: "1s" is run 0, level 1
                    m_bit++;
                    run = 0;
                    level = ReadBit() ? -1 : 1;
                }
                else
                {
                    int entry = ReadVlc(s_dctCoefficients, 16);
                    if (entry == EndOfBlock)
                        break;
                    if (entry == Escape)
                    {
                        run = Read(6);
                        level = Read(8);
                        if (level == 0)
                            level = Read(8);
                        else if (level == 128)
                            level = Read(8) - 256;
                        else if (level > 128)
                            level -= 256;
                    }
                    else
                    {
                        run = entry >> 8;
                        level = entry & 0xFF;
                        if (ReadBit())
                            level = -level;
                    }
                }
                n += run;
                if (n > 63)
                    throw new InvalidDataException("too many coefficients");
                int z = s_zigZag[n];
                int sign = level < 0 ? -1 : 1;
                int c = intra
                    ? 2 * level * m_quantScale * matrix[z] / 16
                    : (2 * level + sign) * m_quantScale * matrix[z] / 16;
                if ((c & 1) == 0 && c != 0)
                    c -= sign;
                block[z] = Math.Clamp(c, -2048, 2047);
                n++;
            }

            var cur = m_current;
            byte[] plane;
            int stride, offset;
            if (b < 4)
            {
                plane = cur.Y;
                stride = cur.Stride;
                offset = (m_mbRow * 16 + (b >> 1) * 8) * stride + m_mbCol * 16 + (b & 1) * 8;
            }
            else
            {
                plane = b == 4 ? cur.Cb : cur.Cr;
                stride = cur.ChromaStride;
                offset = m_mbRow * 8 * stride + m_mbCol * 8;
            }
            Idct(block);
            for (int y = 0; y < 8; y++, offset += stride)
            {
                var row = plane.AsSpan(offset, 8);
                if (intra)
                    for (int x = 0; x < 8; x++)
                    {
                        int v = block[y * 8 + x];
                        row[x] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    }
                else
                    for (int x = 0; x < 8; x++)
                    {
                        int v = block[y * 8 + x] + row[x];
                        row[x] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    }
            }
        }
    }

    private static readonly double[] s_idct = MakeIdct();

    private static double[] MakeIdct()
    {
        // s_idct[x * 8 + u] = C(u) / 2 * cos((2x + 1) u pi / 16)
        var t = new double[64];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                t[x * 8 + u] = (u == 0 ? Math.Sqrt(0.5) : 1) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return t;
    }

    // The same coefficients for the rows on vectors: [u * 4 + k] = (c(2k, u), c(2k + 1, u))
    private static readonly Vector128<double>[] s_idctAcross = MakeIdctAcross();

    private static Vector128<double>[] MakeIdctAcross()
    {
        var t = new Vector128<double>[32];
        for (int u = 0; u < 8; u++)
            for (int k = 0; k < 4; k++)
                t[u * 4 + k] = Vector128.Create(s_idct[2 * k * 8 + u], s_idct[(2 * k + 1) * 8 + u]);
        return t;
    }

    // A block after the rows: [v * 4 + k] = (tmp[v][2k], tmp[v][2k + 1])
    [ThreadStatic] private static Vector128<double>[]? t_rows;

    /// <summary>
    /// The inverse DCT of an 8 x 8 block in place, rounded to the nearest integer. Each output is
    /// the sum of the same products in the same order as the definition (sum over u, then over
    /// v), two outputs a vector and no fused multiply-add, so the rounding is the definition's.
    /// </summary>
    private static void Idct(int[] block)
    {
        bool dcOnly = true;
        for (int i = 1; i < 64 && dcOnly; i++)
            dcOnly = block[i] == 0;
        if (dcOnly)
        {
            int v = (int)Math.Floor(block[0] / 8.0 + 0.5);
            Array.Fill(block, v);
            return;
        }
        var tmp = t_rows ??= new Vector128<double>[32];
        var across = s_idctAcross;
        // Rows: tmp[v][x] = sum_u F[v][u] c(x, u)
        for (int v = 0; v < 8; v++)
        {
            int r = v * 8;
            bool zero = true;
            for (int u = 0; u < 8 && zero; u++)
                zero = block[r + u] == 0;
            Vector128<double> s0 = Vector128<double>.Zero, s1 = s0, s2 = s0, s3 = s0;
            if (!zero)
                for (int u = 0; u < 8; u++)
                {
                    var f = Vector128.Create((double)block[r + u]);
                    s0 += f * across[u * 4];
                    s1 += f * across[u * 4 + 1];
                    s2 += f * across[u * 4 + 2];
                    s3 += f * across[u * 4 + 3];
                }
            tmp[v * 4] = s0;
            tmp[v * 4 + 1] = s1;
            tmp[v * 4 + 2] = s2;
            tmp[v * 4 + 3] = s3;
        }
        // Columns: f[y][x] = sum_v tmp[v][x] c(y, v), eight across at once
        var half = Vector128.Create(0.5);
        for (int y = 0; y < 8; y++)
        {
            Vector128<double> s0 = Vector128<double>.Zero, s1 = s0, s2 = s0, s3 = s0;
            for (int v = 0; v < 8; v++)
            {
                var c = Vector128.Create(s_idct[y * 8 + v]);
                s0 += tmp[v * 4] * c;
                s1 += tmp[v * 4 + 1] * c;
                s2 += tmp[v * 4 + 2] * c;
                s3 += tmp[v * 4 + 3] * c;
            }
            int o = y * 8;
            s0 = Vector128.Floor(s0 + half);
            s1 = Vector128.Floor(s1 + half);
            s2 = Vector128.Floor(s2 + half);
            s3 = Vector128.Floor(s3 + half);
            block[o] = (int)s0.GetElement(0);
            block[o + 1] = (int)s0.GetElement(1);
            block[o + 2] = (int)s1.GetElement(0);
            block[o + 3] = (int)s1.GetElement(1);
            block[o + 4] = (int)s2.GetElement(0);
            block[o + 5] = (int)s2.GetElement(1);
            block[o + 6] = (int)s3.GetElement(0);
            block[o + 7] = (int)s3.GetElement(1);
        }
    }

    #endregion

    #region Tables (ISO/IEC 11172-2 annex B)

    private static readonly int[] s_zigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly int[] s_defaultIntra =
    [
        8, 16, 19, 22, 26, 27, 29, 34,
        16, 16, 22, 24, 27, 29, 34, 37,
        19, 22, 26, 27, 29, 34, 34, 38,
        22, 22, 26, 27, 29, 34, 37, 40,
        22, 26, 27, 29, 32, 35, 40, 48,
        26, 27, 29, 32, 35, 40, 48, 58,
        26, 27, 29, 34, 38, 46, 56, 69,
        27, 29, 35, 38, 46, 56, 69, 83,
    ];

    /// <summary>
    /// A lookup table of <paramref name="bits"/> bits from (code, value) pairs: entry = value
    /// shl 5 | code length (0 = no code).
    /// </summary>
    internal static int[] BuildVlc(int bits, params (string Code, int Value)[] codes)
    {
        var table = new int[1 << bits];
        foreach (var (text, value) in codes)
        {
            string code = text.Replace(" ", "");
            int length = code.Length, prefix = Convert.ToInt32(code, 2);
            int shift = bits - length;
            for (int i = 0; i < 1 << shift; i++)
            {
                int index = prefix << shift | i;
                if (table[index] != 0)
                    throw new InvalidOperationException($"VLC code {text} overlaps another");
                table[index] = (value << 5 | length);
            }
        }
        return table;
    }

    private static readonly int[] s_addressIncrement = BuildVlc(11,
        ("1", 1), ("011", 2), ("010", 3), ("0011", 4), ("0010", 5), ("0001 1", 6), ("0001 0", 7),
        ("0000 111", 8), ("0000 110", 9), ("0000 1011", 10), ("0000 1010", 11), ("0000 1001", 12),
        ("0000 1000", 13), ("0000 0111", 14), ("0000 0110", 15), ("0000 0101 11", 16),
        ("0000 0101 10", 17), ("0000 0101 01", 18), ("0000 0101 00", 19), ("0000 0100 11", 20),
        ("0000 0100 10", 21), ("0000 0100 011", 22), ("0000 0100 010", 23), ("0000 0100 001", 24),
        ("0000 0100 000", 25), ("0000 0011 111", 26), ("0000 0011 110", 27), ("0000 0011 101", 28),
        ("0000 0011 100", 29), ("0000 0011 011", 30), ("0000 0011 010", 31), ("0000 0011 001", 32),
        ("0000 0011 000", 33), ("0000 0001 111", 34), ("0000 0001 000", 35));

    // Macroblock types: 0x10 quant, 8 forward, 4 backward, 2 pattern, 1 intra
    private static readonly int[] s_typeI = BuildVlc(2, ("1", 0x01), ("01", 0x11));

    private static readonly int[] s_typeP = BuildVlc(6,
        ("1", 0x0A), ("01", 0x02), ("001", 0x08), ("0001 1", 0x01), ("0001 0", 0x1A),
        ("0000 1", 0x12), ("0000 01", 0x11));

    private static readonly int[] s_typeB = BuildVlc(6,
        ("10", 0x0C), ("11", 0x0E), ("010", 0x04), ("011", 0x06), ("0010", 0x08), ("0011", 0x0A),
        ("0001 1", 0x01), ("0001 0", 0x1E), ("0000 11", 0x1A), ("0000 10", 0x16), ("0000 01", 0x11));

    private static readonly int[] s_codedBlockPattern = BuildVlc(9,
        ("111", 60), ("1101", 4), ("1100", 8), ("1011", 16), ("1010", 32), ("1001 1", 12), ("1001 0", 48),
        ("1000 1", 20), ("1000 0", 40), ("0111 1", 28), ("0111 0", 44), ("0110 1", 52), ("0110 0", 56),
        ("0101 1", 1), ("0101 0", 61), ("0100 1", 2), ("0100 0", 62), ("0011 11", 24), ("0011 10", 36),
        ("0011 01", 3), ("0011 00", 63), ("0010 111", 5), ("0010 110", 9), ("0010 101", 17),
        ("0010 100", 33), ("0010 011", 6), ("0010 010", 10), ("0010 001", 18), ("0010 000", 34),
        ("0001 1111", 7), ("0001 1110", 11), ("0001 1101", 19), ("0001 1100", 35), ("0001 1011", 13),
        ("0001 1010", 49), ("0001 1001", 21), ("0001 1000", 41), ("0001 0111", 14), ("0001 0110", 50),
        ("0001 0101", 22), ("0001 0100", 42), ("0001 0011", 15), ("0001 0010", 51), ("0001 0001", 23),
        ("0001 0000", 43), ("0000 1111", 25), ("0000 1110", 37), ("0000 1101", 26), ("0000 1100", 38),
        ("0000 1011", 29), ("0000 1010", 45), ("0000 1001", 53), ("0000 1000", 57), ("0000 0111", 30),
        ("0000 0110", 46), ("0000 0101", 54), ("0000 0100", 58), ("0000 0011 1", 31), ("0000 0011 0", 47),
        ("0000 0010 1", 55), ("0000 0010 0", 59), ("0000 0001 1", 27), ("0000 0001 0", 39));

    // Motion codes without their sign bit
    private static readonly int[] s_motion = BuildVlc(11,
        ("1", 0), ("01", 1), ("001", 2), ("0001", 3), ("0000 11", 4), ("0000 101", 5), ("0000 100", 6),
        ("0000 011", 7), ("0000 0101 1", 8), ("0000 0101 0", 9), ("0000 0100 1", 10), ("0000 0100 01", 11),
        ("0000 0100 00", 12), ("0000 0011 11", 13), ("0000 0011 10", 14), ("0000 0011 01", 15),
        ("0000 0011 00", 16));

    private static readonly int[] s_dcSizeLuma = BuildVlc(7,
        ("100", 0), ("00", 1), ("01", 2), ("101", 3), ("110", 4), ("1110", 5), ("1111 0", 6),
        ("1111 10", 7), ("1111 110", 8));

    private static readonly int[] s_dcSizeChroma = BuildVlc(8,
        ("00", 0), ("01", 1), ("10", 2), ("110", 3), ("1110", 4), ("1111 0", 5), ("1111 10", 6),
        ("1111 110", 7), ("1111 1110", 8));

    private const int EndOfBlock = 0x3FE, Escape = 0x3FF;

    // dct_coeff_next without the sign bit: run << 8 | level, or end of block / escape
    private static readonly int[] s_dctCoefficients = BuildDct();

    private static int[] BuildDct()
    {
        (string, int)[] codes =
        [
            ("10", EndOfBlock), ("0000 01", Escape),
            ("11", 0x001), ("011", 0x101), ("0100", 0x002), ("0101", 0x201), ("0010 1", 0x003),
            ("0011 1", 0x301), ("0011 0", 0x401), ("0001 10", 0x102), ("0001 11", 0x501),
            ("0001 01", 0x601), ("0001 00", 0x701), ("0000 110", 0x004), ("0000 100", 0x202),
            ("0000 111", 0x801), ("0000 101", 0x901), ("0010 0110", 0x005), ("0010 0001", 0x006),
            ("0010 0101", 0x103), ("0010 0100", 0x302), ("0010 0111", 0xA01), ("0010 0011", 0xB01),
            ("0010 0010", 0xC01), ("0010 0000", 0xD01), ("0000 0010 10", 0x007), ("0000 0011 00", 0x104),
            ("0000 0010 11", 0x203), ("0000 0011 11", 0x402), ("0000 0010 01", 0x502),
            ("0000 0011 10", 0xE01), ("0000 0011 01", 0xF01), ("0000 0010 00", 0x1001),
            ("0000 0001 1101", 0x008), ("0000 0001 1000", 0x009), ("0000 0001 0011", 0x00A),
            ("0000 0001 0000", 0x00B), ("0000 0001 1011", 0x105), ("0000 0001 0100", 0x204),
            ("0000 0001 1100", 0x303), ("0000 0001 0010", 0x403), ("0000 0001 1110", 0x602),
            ("0000 0001 0101", 0x702), ("0000 0001 0001", 0x802), ("0000 0001 1111", 0x1101),
            ("0000 0001 1010", 0x1201), ("0000 0001 1001", 0x1301), ("0000 0001 0111", 0x1401),
            ("0000 0001 0110", 0x1501), ("0000 0000 1101 0", 0x00C), ("0000 0000 1100 1", 0x00D),
            ("0000 0000 1100 0", 0x00E), ("0000 0000 1011 1", 0x00F), ("0000 0000 1011 0", 0x106),
            ("0000 0000 1010 1", 0x107), ("0000 0000 1010 0", 0x205), ("0000 0000 1001 1", 0x304),
            ("0000 0000 1001 0", 0x503), ("0000 0000 1000 1", 0x902), ("0000 0000 1000 0", 0xA02),
            ("0000 0000 1111 1", 0x1601), ("0000 0000 1111 0", 0x1701), ("0000 0000 1110 1", 0x1801),
            ("0000 0000 1110 0", 0x1901), ("0000 0000 1101 1", 0x1A01), ("0000 0000 0111 11", 0x010),
            ("0000 0000 0111 10", 0x011), ("0000 0000 0111 01", 0x012), ("0000 0000 0111 00", 0x013),
            ("0000 0000 0110 11", 0x014), ("0000 0000 0110 10", 0x015), ("0000 0000 0110 01", 0x016),
            ("0000 0000 0110 00", 0x017), ("0000 0000 0101 11", 0x018), ("0000 0000 0101 10", 0x019),
            ("0000 0000 0101 01", 0x01A), ("0000 0000 0101 00", 0x01B), ("0000 0000 0100 11", 0x01C),
            ("0000 0000 0100 10", 0x01D), ("0000 0000 0100 01", 0x01E), ("0000 0000 0100 00", 0x01F),
            ("0000 0000 0011 000", 0x020), ("0000 0000 0010 111", 0x021), ("0000 0000 0010 110", 0x022),
            ("0000 0000 0010 101", 0x023), ("0000 0000 0010 100", 0x024), ("0000 0000 0010 011", 0x025),
            ("0000 0000 0010 010", 0x026), ("0000 0000 0010 001", 0x027), ("0000 0000 0010 000", 0x028),
            ("0000 0000 0011 111", 0x108), ("0000 0000 0011 110", 0x109), ("0000 0000 0011 101", 0x10A),
            ("0000 0000 0011 100", 0x10B), ("0000 0000 0011 011", 0x10C), ("0000 0000 0011 010", 0x10D),
            ("0000 0000 0011 001", 0x10E), ("0000 0000 0001 0011", 0x10F), ("0000 0000 0001 0010", 0x110),
            ("0000 0000 0001 0001", 0x111), ("0000 0000 0001 0000", 0x112), ("0000 0000 0001 0100", 0x603),
            ("0000 0000 0001 1010", 0xB02), ("0000 0000 0001 1001", 0xC02), ("0000 0000 0001 1000", 0xD02),
            ("0000 0000 0001 0111", 0xE02), ("0000 0000 0001 0110", 0xF02), ("0000 0000 0001 0101", 0x1002),
            ("0000 0000 0001 1111", 0x1B01), ("0000 0000 0001 1110", 0x1C01), ("0000 0000 0001 1101", 0x1D01),
            ("0000 0000 0001 1100", 0x1E01), ("0000 0000 0001 1011", 0x1F01),
        ];
        return BuildVlc(16, codes);
    }

    /// <summary>Sum of 2^-length over a table's codes (1 for a complete code), for checks.</summary>
    internal static double Coverage(int[] table, int bits) =>
        table.Count(e => e != 0) / (double)(1 << bits);

    public static (double Address, double TypeP, double TypeB, double Pattern, double Motion, double DcLuma, double DcChroma, double Dct) TableCoverage() =>
        (Coverage(s_addressIncrement, 11), Coverage(s_typeP, 6), Coverage(s_typeB, 6), Coverage(s_codedBlockPattern, 9),
         Coverage(s_motion, 11), Coverage(s_dcSizeLuma, 7), Coverage(s_dcSizeChroma, 8), Coverage(s_dctCoefficients, 16));

    #endregion
}
