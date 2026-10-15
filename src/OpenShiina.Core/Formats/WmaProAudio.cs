// Windows Media Audio 9 Professional (format tag 0x162, the sound of the WVC1 ending movie): a
// port of FFmpeg's wmaprodec.c (LGPL 2.1 or later). Packets of block_align bytes carry frames
// across their bounds (a bit reservoir); a frame splits each channel into subframes of MDCT
// blocks, with channel transforms, vector / run-level coded coefficients and scale factors.

namespace OpenShiina.Formats;

public sealed class WmaProAudio : IWmaDecoder
{
    private const int MaxSubframes = 32, MaxBands = 29, MaxFramesize = 32768;
    private const int BlockMinBits = 6, BlockMaxBits = 13, BlockMaxSize = 1 << BlockMaxBits, BlockSizes = BlockMaxBits - BlockMinBits + 1;
    private const int HuffVec1Size = 101;

    public int Channels { get; }
    public int SampleRate { get; }
    /// <summary>None: the first frame is held back by the decoder itself.</summary>
    public int Delay => 0;

    private sealed class Channel
    {
        public int PrevBlockLen;
        public bool TransmitCoefs;
        public int NumSubframes;
        public readonly int[] SubframeLen = new int[MaxSubframes];
        public readonly int[] SubframeOffset = new int[MaxSubframes];
        public int CurSubframe;
        public int DecodedSamples;
        public bool Grouped;
        public int QuantStep;
        public bool ReuseSf;
        public int ScaleFactorStep;
        public int MaxScaleFactor;
        public readonly int[][] SavedScaleFactors = [new int[MaxBands], new int[MaxBands]];
        public int ScaleFactorIdx;
        /// <summary>Which of <see cref="SavedScaleFactors"/> the current scale factors are.</summary>
        public int ScaleFactors;
        public int TableIdx;
        /// <summary>Where the subframe's coefficients are in <see cref="Out"/>.</summary>
        public int Coeffs;
        public int NumVecCoeffs;
        public readonly float[] Out = new float[BlockMaxSize + BlockMaxSize / 2];
    }

    private sealed class ChannelGroup
    {
        public int NumChannels;
        public bool Transform;
        public readonly bool[] TransformBand = new bool[MaxBands];
        public readonly float[] Matrix = new float[64];
        public readonly int[] ChannelData = new int[8];
    }

    private readonly int m_blockAlign, m_bitsPerSample, m_samplesPerFrame, m_log2FrameSize;
    private readonly bool m_lenPrefix, m_dynamicRangeCompression;
    private readonly int m_maxNumSubframes, m_subframeLenBits, m_minSamplesPerSubframe;
    private readonly bool m_maxSubframeLenBit;
    private readonly int[] m_numSfb = new int[BlockSizes];
    private readonly int[][] m_sfbOffsets = Enumerable.Range(0, BlockSizes).Select(_ => new int[MaxBands]).ToArray();
    private readonly int[,,] m_sfOffsets = new int[BlockSizes, BlockSizes, MaxBands];
    private readonly Mdct[] m_mdct = new Mdct[BlockSizes];
    private readonly float[][] m_windows = new float[BlockSizes][];
    private readonly float[] m_tmp = new float[BlockMaxSize];
    private readonly Channel[] m_channels;
    private readonly ChannelGroup[] m_groups;

    // packet state
    private readonly byte[] m_frameData = new byte[MaxFramesize + 16];
    private int m_pbBits;
    private BitReader m_pgb = new([], 0);
    private int m_nextPacketStart, m_packetOffset, m_packetSequenceNumber, m_numSavedBits, m_frameOffset;
    private bool m_packetLoss = true, m_packetDone;
    // frame state
    private BitReader m_gb = new([], 0);
    private int m_bufBitSize;
    private bool m_skipFrame = true, m_parsedAllSubframes;
    private int m_trimStart, m_trimEnd;
    private int m_subframeLen, m_channelsForCurSubframe, m_numBands, m_tableIdx, m_escLen, m_numChgroups;
    private bool m_transmitNumVecCoeffs;
    private readonly int[] m_channelIndexes = new int[8];

    private static readonly VlcTable s_sfVlc = FromPairs(8, WmaTables.ProScaleTable, -60);
    private static readonly VlcTable s_sfRlVlc = FromPairs(9, WmaTables.ProScaleRlTable, 0);
    private static readonly VlcTable s_coef0Vlc = VlcTable.FromLengths(9, WmaTables.ProCoef0Lens, Array.ConvertAll(WmaTables.ProCoef0Syms, s => (int)s), 0);
    private static readonly VlcTable s_coef1Vlc = FromPairs(9, WmaTables.ProCoef1Table, 0);
    private static readonly VlcTable s_vec4Vlc = VlcTable.FromLengths(9, WmaTables.ProVec4Lens, Array.ConvertAll(WmaTables.ProVec4Syms, s => (int)s), -1);
    private static readonly VlcTable s_vec2Vlc = FromPairs(9, WmaTables.ProVec2Table, -1);
    private static readonly VlcTable s_vec1Vlc = FromPairs(9, WmaTables.ProVec1Table, 0);
    private static readonly float[] s_sin64 = Enumerable.Range(0, 33).Select(i => (float)Math.Sin(i * Math.PI / 64.0)).ToArray();
    private static readonly int[] s_defaultDecorrelation = [0, 0, 1, 5, 14, 30, 55];

    /// <summary>A table of symbol / length pairs (canonical codes in table order).</summary>
    private static VlcTable FromPairs(int bits, byte[] pairs, int offset)
    {
        int n = pairs.Length / 2;
        var lengths = new byte[n];
        var symbols = new int[n];
        for (int i = 0; i < n; i++)
        {
            symbols[i] = pairs[2 * i];
            lengths[i] = pairs[2 * i + 1];
        }
        return VlcTable.FromLengths(bits, lengths, symbols, offset);
    }

    public WmaProAudio(int channels, int sampleRate, int blockAlign, byte[] extra)
    {
        if (blockAlign <= 0 || extra.Length < 18 || sampleRate <= 0)
            throw new InvalidDataException("Not a WMA Pro stream");
        m_blockAlign = blockAlign;
        int decodeFlags = extra[14] | extra[15] << 8;
        uint channelMask = BitConverter.ToUInt32(extra, 2);
        m_bitsPerSample = extra[0] | extra[1] << 8;
        int nbChannels = channelMask != 0 ? System.Numerics.BitOperations.PopCount(channelMask) : channels;
        if (m_bitsPerSample is > 32 or < 1 || nbChannels is < 1 or > 8 || nbChannels > channels)
            throw new InvalidDataException("Unsupported WMA Pro stream");
        if ((channelMask & 8) != 0 && nbChannels > 2)
            throw new InvalidDataException("WMA Pro with an LFE channel");
        Channels = nbChannels;
        SampleRate = sampleRate;
        m_log2FrameSize = (int)Math.Log2(blockAlign) + 4;
        if (m_log2FrameSize > 25)
            throw new InvalidDataException("WMA Pro block align");
        m_lenPrefix = (decodeFlags & 0x40) != 0;
        int bits = sampleRate <= 16000 ? 9 : sampleRate <= 22050 ? 10 : sampleRate <= 48000 ? 11 : sampleRate <= 96000 ? 12 : 13;
        int tmp = decodeFlags & 6;
        if (tmp == 2)
            bits++;
        else if (tmp == 4)
            bits--;
        else if (tmp == 6)
            bits -= 2;
        if (bits > BlockMaxBits)
            throw new InvalidDataException("WMA Pro 14-bit blocks");
        m_samplesPerFrame = 1 << bits;
        int log2MaxNumSubframes = (decodeFlags & 0x38) >> 3;
        m_maxNumSubframes = 1 << log2MaxNumSubframes;
        m_maxSubframeLenBit = m_maxNumSubframes is 16 or 4;
        m_subframeLenBits = (log2MaxNumSubframes == 0 ? 0 : (int)Math.Log2(log2MaxNumSubframes)) + 1;
        int numPossibleBlockSizes = log2MaxNumSubframes + 1;
        m_minSamplesPerSubframe = m_samplesPerFrame / m_maxNumSubframes;
        m_dynamicRangeCompression = (decodeFlags & 0x80) != 0;
        if (m_maxNumSubframes > MaxSubframes || m_minSamplesPerSubframe < 1 << BlockMinBits)
            throw new InvalidDataException("WMA Pro subframes");

        m_channels = Enumerable.Range(0, nbChannels).Select(_ => new Channel { PrevBlockLen = m_samplesPerFrame }).ToArray();
        m_groups = Enumerable.Range(0, nbChannels).Select(_ => new ChannelGroup()).ToArray();

        for (int i = 0; i < numPossibleBlockSizes; i++)
        {
            int subframeLen = m_samplesPerFrame >> i, band = 1;
            var offsets = m_sfbOffsets[i];
            offsets[0] = 0;
            for (int x = 0; x < MaxBands - 1 && offsets[band - 1] < subframeLen; x++)
            {
                int offset = (subframeLen * 2 * WmaTables.ProCriticalFreq[x]) / sampleRate + 2;
                offset &= ~3;
                if (offset > offsets[band - 1])
                    offsets[band++] = offset;
                if (offset >= subframeLen)
                    break;
            }
            offsets[band - 1] = subframeLen;
            m_numSfb[i] = band - 1;
            if (m_numSfb[i] <= 0)
                throw new InvalidDataException("WMA Pro bands");
        }
        for (int i = 0; i < numPossibleBlockSizes; i++)
            for (int b = 0; b < m_numSfb[i]; b++)
            {
                int offset = ((m_sfbOffsets[i][b] + m_sfbOffsets[i][b + 1] - 1) << i) >> 1;
                for (int x = 0; x < numPossibleBlockSizes; x++)
                {
                    int v = 0;
                    while (m_sfbOffsets[x][v + 1] << x < offset)
                        v++;
                    m_sfOffsets[i, x, b] = v;
                }
            }
        for (int i = 0; i < BlockSizes; i++)
        {
            double scale = 1.0 / (1 << (BlockMinBits + i - 1)) / (1L << (m_bitsPerSample - 1));
            m_mdct[i] = new Mdct(BlockMinBits + i + 1, scale);
            int len = 1 << (BlockMinBits + i);
            var w = new float[len];
            for (int k = 0; k < len; k++)
                w[k] = (float)Math.Sin((k + 0.5) * (Math.PI / (2.0 * len)));
            m_windows[i] = w;
        }
    }

    #region Bit reservoir

    private void PutBits(int n, int value)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            int bit = (value >> i) & 1;
            int at = m_pbBits >> 3;
            if ((m_pbBits & 7) == 0)
                m_frameData[at] = 0;
            m_frameData[at] |= (byte)(bit << (7 - (m_pbBits & 7)));
            m_pbBits++;
        }
    }

    /// <summary>ff_copy_bits: <paramref name="length"/> bits from a byte position of a buffer.</summary>
    private void CopyBits(byte[] src, int byteOffset, int length)
    {
        int whole = length >> 3;
        if ((m_pbBits & 7) == 0)
        {
            Buffer.BlockCopy(src, byteOffset, m_frameData, m_pbBits >> 3, whole);
            m_pbBits += whole * 8;
        }
        else
            for (int i = 0; i < whole; i++)
                PutBits(8, src[byteOffset + i]);
        int rest = length & 7;
        if (rest != 0)
            PutBits(rest, src[byteOffset + whole] >> (8 - rest));
    }

    /// <summary>save_bits: a (partial) frame into the reservoir.</summary>
    private void SaveBits(BitReader gb, int len, bool append)
    {
        int buflen;
        if (!append)
        {
            m_frameOffset = gb.Position & 7;
            m_numSavedBits = m_frameOffset;
            m_pbBits = 0;
            buflen = (m_numSavedBits + len + 7) >> 3;
        }
        else
            buflen = (m_pbBits + len + 7) >> 3;
        if (len <= 0 || buflen > MaxFramesize)
        {
            m_packetLoss = true;
            return;
        }
        m_numSavedBits += len;
        if (!append)
            CopyBits(gb.Data, gb.Position >> 3, m_numSavedBits);
        else
        {
            int align = Math.Min(8 - (gb.Position & 7), len);
            PutBits(align, gb.Get(align));
            len -= align;
            CopyBits(gb.Data, gb.Position >> 3, len);
        }
        gb.Skip(len);
        m_frameData.AsSpan((m_pbBits + 7) >> 3, Math.Min(16, m_frameData.Length - ((m_pbBits + 7) >> 3))).Clear();
        m_gb = new BitReader(m_frameData, m_numSavedBits, m_frameOffset);
    }

    #endregion

    /// <summary>A packet of block_align bytes; the frames it completes are added to <paramref name="output"/>.</summary>
    public void DecodePacket(ReadOnlySpan<byte> packet, List<float>[] output)
    {
        var data = new byte[packet.Length + 16];
        packet.CopyTo(data);
        // FFmpeg's decode loop: the packet again from where the last call stopped, until it is done
        int offset = 0;
        for (bool first = true; first || (offset < packet.Length && !m_packetDone); first = false)
        {
            int used = DecodeStep(data, offset, packet.Length - offset, output, out bool error);
            if (error)
                throw new InvalidDataException("WMA Pro packet");
            offset += used >> 3;
            if (used == 0)
                break;
        }
    }

    /// <summary>decode_packet: one call of FFmpeg's, on the packet's bytes from <paramref name="start"/>; the bits it used.</summary>
    private int DecodeStep(byte[] data, int start, int size, List<float>[] output, out bool error)
    {
        error = false;
        BitReader gb;
        if (m_packetDone || m_packetLoss)
        {
            m_packetDone = false;
            if (size < m_blockAlign)
            {
                m_packetLoss = true;
                error = true;
                return size * 8;
            }
            m_nextPacketStart = size - m_blockAlign;
            int bufSize = m_blockAlign;
            m_bufBitSize = bufSize << 3;
            gb = new BitReader(data, start * 8 + m_bufBitSize, start * 8);
            m_pgb = gb;
            int sequence = gb.Get(4);
            gb.Skip(2);
            int numBitsPrevFrame = gb.Get(m_log2FrameSize);
            if (!m_packetLoss && ((m_packetSequenceNumber + 1) & 0xF) != sequence)
                m_packetLoss = true;
            m_packetSequenceNumber = sequence;
            if (numBitsPrevFrame > 0)
            {
                int remaining = gb.Size - gb.Position;
                if (numBitsPrevFrame >= remaining)
                {
                    numBitsPrevFrame = remaining;
                    m_packetDone = true;
                }
                SaveBits(gb, numBitsPrevFrame, true);
                if (!m_packetLoss)
                    DecodeFrame(output);
            }
            if (m_packetLoss)
            {
                m_numSavedBits = 0;
                m_packetLoss = false;
            }
        }
        else
        {
            m_bufBitSize = (size - m_nextPacketStart) << 3;
            gb = new BitReader(data, start * 8 + m_bufBitSize, start * 8 + m_packetOffset);
            m_pgb = gb;
            int remaining = gb.Size - gb.Position;
            int frameSize;
            if (m_lenPrefix && remaining > m_log2FrameSize && (frameSize = (int)gb.Show(m_log2FrameSize)) != 0 && frameSize <= remaining)
            {
                SaveBits(gb, frameSize, false);
                if (!m_packetLoss)
                    m_packetDone = !DecodeFrame(output);
            }
            else if (!m_lenPrefix && m_numSavedBits > m_gb.Position)
                m_packetDone = !DecodeFrame(output);
            else
                m_packetDone = true;
        }
        int left = gb.Size - gb.Position;
        if (left < 0)
            m_packetLoss = true;
        if (m_packetDone && !m_packetLoss && left > 0)
            SaveBits(gb, left, false);
        int consumed = gb.Position - start * 8;
        m_packetOffset = consumed & 7;
        error = m_packetLoss;
        return consumed;
    }

    #region Frames

    /// <summary>decode_frame: true when more frames follow in the packet.</summary>
    private bool DecodeFrame(List<float>[] output)
    {
        var gb = m_gb;
        int len = m_lenPrefix ? gb.Get(m_log2FrameSize) : 0;
        if (!DecodeTileHeader())
        {
            m_packetLoss = true;
            return false;
        }
        int nb = Channels;
        if (nb > 1 && gb.Bit() != 0 && gb.Bit() != 0)
            gb.Skip(4 * nb * nb);
        if (m_dynamicRangeCompression)
            gb.Get(8); // drc gain
        if (gb.Bit() != 0)
        {
            int trimBits = (int)Math.Log2(m_samplesPerFrame * 2);
            if (gb.Bit() != 0)
                m_trimStart = gb.Get(trimBits);
            if (gb.Bit() != 0)
                m_trimEnd = gb.Get(trimBits);
        }
        else
            m_trimStart = m_trimEnd = 0;

        m_parsedAllSubframes = false;
        foreach (var c in m_channels)
        {
            c.DecodedSamples = 0;
            c.CurSubframe = 0;
            c.ReuseSf = false;
        }
        while (!m_parsedAllSubframes)
            if (!DecodeSubframe())
            {
                m_packetLoss = true;
                return false;
            }

        if (m_skipFrame)
            m_skipFrame = false;
        else
        {
            int from = Math.Min(m_trimStart, m_samplesPerFrame), to = m_samplesPerFrame - Math.Min(m_trimEnd, m_samplesPerFrame);
            for (int ch = 0; ch < nb; ch++)
                for (int i = from; i < to; i++)
                    output[ch].Add(m_channels[ch].Out[i]);
        }
        m_trimStart = m_trimEnd = 0;
        foreach (var c in m_channels)
            Array.Copy(c.Out, m_samplesPerFrame, c.Out, 0, m_samplesPerFrame >> 1);

        if (m_lenPrefix)
        {
            int read = gb.Position - m_frameOffset;
            if (len != read + 2)
            {
                m_packetLoss = true;
                return false;
            }
            gb.Skip(len - read - 1);
        }
        else
            while (gb.Position < m_numSavedBits && gb.Bit() == 0)
            {
            }
        return gb.Bit() != 0;
    }

    private int DecodeSubframeLength(int offset)
    {
        if (offset == m_samplesPerFrame - m_minSamplesPerSubframe)
            return m_minSamplesPerSubframe;
        if (m_gb.Left < 1)
            return -1;
        int shift = 0;
        if (m_maxSubframeLenBit)
        {
            if (m_gb.Bit() != 0)
                shift = 1 + m_gb.Get(m_subframeLenBits - 1);
        }
        else
            shift = m_gb.Get(m_subframeLenBits);
        int len = m_samplesPerFrame >> shift;
        return len < m_minSamplesPerSubframe || len > m_samplesPerFrame ? -1 : len;
    }

    /// <summary>decode_tilehdr: how the frame splits into subframes per channel.</summary>
    private bool DecodeTileHeader()
    {
        int nb = Channels;
        Span<int> numSamples = stackalloc int[8];
        Span<bool> contains = stackalloc bool[8];
        int channelsForCur = nb, minChannelLen = 0;
        foreach (var c in m_channels)
            c.NumSubframes = 0;
        bool fixedLayout = m_maxNumSubframes == 1 || m_gb.Bit() != 0;
        do
        {
            for (int c = 0; c < nb; c++)
            {
                if (numSamples[c] == minChannelLen)
                    contains[c] = fixedLayout || channelsForCur == 1 || minChannelLen == m_samplesPerFrame - m_minSamplesPerSubframe || m_gb.Bit() != 0;
                else
                    contains[c] = false;
            }
            int subframeLen = DecodeSubframeLength(minChannelLen);
            if (subframeLen <= 0)
                return false;
            minChannelLen += subframeLen;
            for (int c = 0; c < nb; c++)
            {
                var chan = m_channels[c];
                if (contains[c])
                {
                    if (chan.NumSubframes >= MaxSubframes)
                        return false;
                    chan.SubframeLen[chan.NumSubframes++] = subframeLen;
                    numSamples[c] += subframeLen;
                    if (numSamples[c] > m_samplesPerFrame)
                        return false;
                }
                else if (numSamples[c] <= minChannelLen)
                {
                    if (numSamples[c] < minChannelLen)
                    {
                        channelsForCur = 0;
                        minChannelLen = numSamples[c];
                    }
                    channelsForCur++;
                }
            }
        }
        while (minChannelLen < m_samplesPerFrame);
        foreach (var chan in m_channels)
        {
            int offset = 0;
            for (int i = 0; i < chan.NumSubframes; i++)
            {
                chan.SubframeOffset[i] = offset;
                offset += chan.SubframeLen[i];
            }
        }
        return true;
    }

    private void DecodeDecorrelationMatrix(ChannelGroup group)
    {
        int n = group.NumChannels;
        Array.Clear(group.Matrix);
        Span<int> rotation = stackalloc int[64];
        for (int i = 0; i < n * (n - 1) >> 1; i++)
            rotation[i] = m_gb.Get(6);
        for (int i = 0; i < n; i++)
            group.Matrix[n * i + i] = m_gb.Bit() != 0 ? 1.0f : -1.0f;
        int offset = 0;
        for (int i = 1; i < n; i++)
        {
            for (int x = 0; x < i; x++)
                for (int y = 0; y < i + 1; y++)
                {
                    float v1 = group.Matrix[x * n + y], v2 = group.Matrix[i * n + y];
                    int r = rotation[offset + x];
                    float sinv, cosv;
                    if (r < 32)
                    {
                        sinv = s_sin64[r];
                        cosv = s_sin64[32 - r];
                    }
                    else
                    {
                        sinv = s_sin64[64 - r];
                        cosv = -s_sin64[r - 32];
                    }
                    group.Matrix[y + x * n] = v1 * sinv - v2 * cosv;
                    group.Matrix[y + i * n] = v1 * cosv + v2 * sinv;
                }
            offset += i;
        }
    }

    /// <summary>decode_channel_transform.</summary>
    private bool DecodeChannelTransform()
    {
        m_numChgroups = 0;
        if (Channels <= 1)
            return true;
        int remaining = m_channelsForCurSubframe;
        if (m_gb.Bit() != 0)
            return false;
        for (m_numChgroups = 0; remaining != 0 && m_numChgroups < m_channelsForCurSubframe; m_numChgroups++)
        {
            var group = m_groups[m_numChgroups];
            int data = 0;
            group.NumChannels = 0;
            group.Transform = false;
            if (remaining > 2)
            {
                for (int i = 0; i < m_channelsForCurSubframe; i++)
                {
                    int idx = m_channelIndexes[i];
                    if (!m_channels[idx].Grouped && m_gb.Bit() != 0)
                    {
                        group.NumChannels++;
                        m_channels[idx].Grouped = true;
                        group.ChannelData[data++] = idx;
                    }
                }
            }
            else
            {
                group.NumChannels = remaining;
                for (int i = 0; i < m_channelsForCurSubframe; i++)
                {
                    int idx = m_channelIndexes[i];
                    if (!m_channels[idx].Grouped)
                        group.ChannelData[data++] = idx;
                    m_channels[idx].Grouped = true;
                }
            }
            if (group.NumChannels == 2)
            {
                if (m_gb.Bit() != 0)
                {
                    if (m_gb.Bit() != 0)
                        return false;
                }
                else
                {
                    group.Transform = true;
                    if (Channels == 2)
                    {
                        group.Matrix[0] = 1;
                        group.Matrix[1] = -1;
                        group.Matrix[2] = 1;
                        group.Matrix[3] = 1;
                    }
                    else
                    {
                        group.Matrix[0] = 0.70703125f;
                        group.Matrix[1] = -0.70703125f;
                        group.Matrix[2] = 0.70703125f;
                        group.Matrix[3] = 0.70703125f;
                    }
                }
            }
            else if (group.NumChannels > 2 && m_gb.Bit() != 0)
            {
                group.Transform = true;
                if (m_gb.Bit() != 0)
                    DecodeDecorrelationMatrix(group);
                else if (group.NumChannels <= 6)
                    Array.Copy(WmaTables.ProDecorrelation, s_defaultDecorrelation[group.NumChannels], group.Matrix, 0, group.NumChannels * group.NumChannels);
            }
            if (group.Transform)
            {
                if (m_gb.Bit() == 0)
                    for (int i = 0; i < m_numBands; i++)
                        group.TransformBand[i] = m_gb.Bit() != 0;
                else
                    Array.Fill(group.TransformBand, true, 0, m_numBands);
            }
            remaining -= group.NumChannels;
        }
        return true;
    }

    private static uint GetLargeVal(BitReader gb)
    {
        int nBits = 8;
        if (gb.Bit() != 0)
        {
            nBits += 8;
            if (gb.Bit() != 0)
            {
                nBits += 8;
                if (gb.Bit() != 0)
                    nBits += 7;
            }
        }
        return gb.GetLong(nBits);
    }

    /// <summary>decode_coeffs: vector coded, then run / level coded coefficients of a channel.</summary>
    private bool DecodeCoeffs(int c)
    {
        var gb = m_gb;
        var ci = m_channels[c];
        var coeffs = ci.Out;
        int at = ci.Coeffs;
        bool table1 = gb.Bit() != 0;
        var vlc = table1 ? s_coef1Vlc : s_coef0Vlc;
        var run = table1 ? WmaTables.ProCoef1Run : WmaTables.ProCoef0Run;
        var level = table1 ? WmaTables.ProCoef1Level : WmaTables.ProCoef0Level;
        bool rlMode = false;
        int cur = 0, numZeros = 0;
        Span<float> vals = stackalloc float[4];
        while ((m_transmitNumVecCoeffs || !rlMode) && cur + 3 < ci.NumVecCoeffs)
        {
            int idx = s_vec4Vlc.Read(gb);
            if (idx < 0)
            {
                for (int i = 0; i < 4; i += 2)
                {
                    idx = s_vec2Vlc.Read(gb);
                    if (idx < 0)
                    {
                        uint v0 = (uint)s_vec1Vlc.Read(gb);
                        if (v0 == HuffVec1Size - 1)
                            v0 += GetLargeVal(gb);
                        uint v1 = (uint)s_vec1Vlc.Read(gb);
                        if (v1 == HuffVec1Size - 1)
                            v1 += GetLargeVal(gb);
                        vals[i] = v0;
                        vals[i + 1] = v1;
                    }
                    else
                    {
                        vals[i] = idx >> 4;
                        vals[i + 1] = idx & 0xF;
                    }
                }
            }
            else
            {
                vals[0] = idx >> 12;
                vals[1] = (idx >> 8) & 0xF;
                vals[2] = (idx >> 4) & 0xF;
                vals[3] = idx & 0xF;
            }
            for (int i = 0; i < 4; i++)
            {
                if (vals[i] != 0)
                {
                    coeffs[at + cur] = gb.Bit() != 0 ? vals[i] : -vals[i];
                    numZeros = 0;
                }
                else
                {
                    coeffs[at + cur] = 0;
                    rlMode |= ++numZeros > m_subframeLen >> 8;
                }
                cur++;
            }
        }
        if (cur < m_subframeLen)
        {
            Array.Clear(coeffs, at + cur, m_subframeLen - cur);
            int mask = m_subframeLen - 1, offset = cur;
            for (; offset < m_subframeLen; offset++)
            {
                int code = vlc.Read(gb);
                if (code > 1)
                {
                    offset += run[code];
                    coeffs[at + (offset & mask)] = gb.Bit() != 0 ? level[code] : -level[code];
                }
                else if (code == 1)
                    break;
                else
                {
                    int lv = (int)GetLargeVal(gb);
                    if (gb.Bit() != 0)
                    {
                        if (gb.Bit() != 0)
                        {
                            if (gb.Bit() != 0)
                                return false;
                            offset += gb.Get(m_escLen) + 4;
                        }
                        else
                            offset += gb.Get(2) + 1;
                    }
                    int sign = gb.Bit() - 1;
                    coeffs[at + (offset & mask)] = (lv ^ sign) - sign;
                }
            }
            if (offset > m_subframeLen)
                return false;
        }
        return true;
    }

    /// <summary>decode_scale_factors.</summary>
    private bool DecodeScaleFactors()
    {
        var gb = m_gb;
        for (int i = 0; i < m_channelsForCurSubframe; i++)
        {
            var ch = m_channels[m_channelIndexes[i]];
            ch.ScaleFactors = ch.ScaleFactorIdx == 0 ? 1 : 0;
            var sf = ch.SavedScaleFactors[ch.ScaleFactors];
            if (ch.ReuseSf)
            {
                var saved = ch.SavedScaleFactors[ch.ScaleFactorIdx];
                for (int b = 0; b < m_numBands; b++)
                    sf[b] = saved[m_sfOffsets[m_tableIdx, ch.TableIdx, b]];
            }
            if (ch.CurSubframe == 0 || gb.Bit() != 0)
            {
                if (!ch.ReuseSf)
                {
                    ch.ScaleFactorStep = gb.Get(2) + 1;
                    int val = 45 / ch.ScaleFactorStep;
                    for (int b = 0; b < m_numBands; b++)
                    {
                        val += s_sfVlc.Read(gb);
                        sf[b] = val;
                    }
                }
                else
                {
                    for (int b = 0; b < m_numBands; b++)
                    {
                        int idx = s_sfRlVlc.Read(gb), skip, val, sign;
                        if (idx == 0)
                        {
                            int code = gb.Get(14);
                            val = code >> 6;
                            sign = (code & 1) - 1;
                            skip = (code & 0x3F) >> 1;
                        }
                        else if (idx == 1)
                            break;
                        else
                        {
                            if (idx < 0)
                                return false;
                            skip = WmaTables.ProScaleRlRun[idx];
                            val = WmaTables.ProScaleRlLevel[idx];
                            sign = gb.Bit() - 1;
                        }
                        b += skip;
                        if (b >= m_numBands)
                            return false;
                        sf[b] += (val ^ sign) - sign;
                    }
                }
                ch.ScaleFactorIdx = ch.ScaleFactorIdx == 0 ? 1 : 0;
                ch.TableIdx = m_tableIdx;
                ch.ReuseSf = true;
            }
            ch.MaxScaleFactor = sf[0];
            for (int b = 1; b < m_numBands; b++)
                ch.MaxScaleFactor = Math.Max(ch.MaxScaleFactor, sf[b]);
        }
        return true;
    }

    private void InverseChannelTransform(int[] sfbOffsets)
    {
        Span<float> data = stackalloc float[8];
        for (int g = 0; g < m_numChgroups; g++)
        {
            var group = m_groups[g];
            if (!group.Transform)
                continue;
            int n = group.NumChannels;
            for (int b = 0; b < m_numBands; b++)
            {
                int start = sfbOffsets[b], end = Math.Min(sfbOffsets[b + 1], m_subframeLen);
                if (group.TransformBand[b])
                {
                    for (int y = start; y < end; y++)
                    {
                        for (int k = 0; k < n; k++)
                        {
                            var ch = m_channels[group.ChannelData[k]];
                            data[k] = ch.Out[ch.Coeffs + y];
                        }
                        int m = 0;
                        for (int k = 0; k < n; k++)
                        {
                            float sum = 0;
                            for (int j = 0; j < n; j++)
                                sum += data[j] * group.Matrix[m++];
                            var ch = m_channels[group.ChannelData[k]];
                            ch.Out[ch.Coeffs + y] = sum;
                        }
                    }
                }
                else if (Channels == 2)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        var ch = m_channels[group.ChannelData[k]];
                        for (int y = start; y < end; y++)
                            ch.Out[ch.Coeffs + y] *= 181.0f / 128;
                    }
                }
            }
        }
    }

    private void Window()
    {
        for (int i = 0; i < m_channelsForCurSubframe; i++)
        {
            var ch = m_channels[m_channelIndexes[i]];
            int winlen = ch.PrevBlockLen;
            int start = ch.Coeffs - (winlen >> 1);
            if (m_subframeLen < winlen)
            {
                start += (winlen - m_subframeLen) >> 1;
                winlen = m_subframeLen;
            }
            var window = m_windows[(int)Math.Log2(winlen) - BlockMinBits];
            int len = winlen >> 1;
            // vector_fmul_window(start, start, start + len, window, len)
            var o = ch.Out;
            int dst = start + len, src0 = start + len, src1 = start + len, win = len;
            for (int a = -len, j = len - 1; a < 0; a++, j--)
            {
                float s0 = o[src0 + a], s1 = o[src1 + j], wi = window[win + a], wj = window[win + j];
                o[dst + a] = s0 * wj - s1 * wi;
                o[dst + j] = s0 * wi + s1 * wj;
            }
            ch.PrevBlockLen = m_subframeLen;
        }
    }

    /// <summary>decode_subframe.</summary>
    private bool DecodeSubframe()
    {
        var gb = m_gb;
        int nb = Channels;
        int offset = m_samplesPerFrame, subframeLen = m_samplesPerFrame;
        int totalSamples = m_samplesPerFrame * nb;
        bool transmitCoeffs = false;
        foreach (var ch in m_channels)
        {
            ch.Grouped = false;
            if (offset > ch.DecodedSamples)
            {
                offset = ch.DecodedSamples;
                subframeLen = ch.SubframeLen[ch.CurSubframe];
            }
        }
        m_channelsForCurSubframe = 0;
        for (int i = 0; i < nb; i++)
        {
            var ch = m_channels[i];
            totalSamples -= ch.DecodedSamples;
            if (offset == ch.DecodedSamples && subframeLen == ch.SubframeLen[ch.CurSubframe])
            {
                totalSamples -= ch.SubframeLen[ch.CurSubframe];
                ch.DecodedSamples += ch.SubframeLen[ch.CurSubframe];
                m_channelIndexes[m_channelsForCurSubframe++] = i;
            }
        }
        if (totalSamples == 0)
            m_parsedAllSubframes = true;

        m_tableIdx = (int)Math.Log2(m_samplesPerFrame / subframeLen);
        m_numBands = m_numSfb[m_tableIdx];
        var sfbOffsets = m_sfbOffsets[m_tableIdx];
        offset += m_samplesPerFrame >> 1;
        for (int i = 0; i < m_channelsForCurSubframe; i++)
            m_channels[m_channelIndexes[i]].Coeffs = offset;
        m_subframeLen = subframeLen;
        m_escLen = (int)Math.Log2(subframeLen - 1) + 1;

        if (gb.Bit() != 0)
        {
            int numFillBits = gb.Get(2);
            if (numFillBits == 0)
            {
                int len = gb.Get(4);
                numFillBits = (len == 0 ? 0 : (int)gb.GetLong(len)) + 1;
            }
            if (gb.Position + numFillBits > m_numSavedBits)
                return false;
            gb.Skip(numFillBits);
        }
        if (gb.Bit() != 0)
            return false;
        if (!DecodeChannelTransform())
            return false;
        for (int i = 0; i < m_channelsForCurSubframe; i++)
        {
            var ch = m_channels[m_channelIndexes[i]];
            ch.TransmitCoefs = gb.Bit() != 0;
            transmitCoeffs |= ch.TransmitCoefs;
        }
        if (transmitCoeffs)
        {
            int quantStep = 90 * m_bitsPerSample >> 4;
            m_transmitNumVecCoeffs = gb.Bit() != 0;
            if (m_transmitNumVecCoeffs)
            {
                int numBits = (int)Math.Log2((m_subframeLen + 3) / 4) + 1;
                for (int i = 0; i < m_channelsForCurSubframe; i++)
                {
                    int numVec = gb.Get(numBits) << 2;
                    if (numVec > m_subframeLen)
                        return false;
                    m_channels[m_channelIndexes[i]].NumVecCoeffs = numVec;
                }
            }
            else
                for (int i = 0; i < m_channelsForCurSubframe; i++)
                    m_channels[m_channelIndexes[i]].NumVecCoeffs = m_subframeLen;
            int step = gb.Get(6);
            step = step >= 32 ? step - 64 : step;
            quantStep += step;
            if (step is -32 or 31)
            {
                int sign = (step == 31 ? 1 : 0) - 1, quant = 0;
                while (gb.Position + 5 < m_numSavedBits && (step = gb.Get(5)) == 31)
                    quant += 31;
                quantStep += ((quant + step) ^ sign) - sign;
            }
            if (m_channelsForCurSubframe == 1)
                m_channels[m_channelIndexes[0]].QuantStep = quantStep;
            else
            {
                int modifierLen = gb.Get(3);
                for (int i = 0; i < m_channelsForCurSubframe; i++)
                {
                    var ch = m_channels[m_channelIndexes[i]];
                    ch.QuantStep = quantStep;
                    if (gb.Bit() != 0)
                        ch.QuantStep += modifierLen != 0 ? gb.Get(modifierLen) + 1 : 1;
                }
            }
            if (!DecodeScaleFactors())
                return false;
        }
        for (int i = 0; i < m_channelsForCurSubframe; i++)
        {
            int c = m_channelIndexes[i];
            var ch = m_channels[c];
            // (a broken coefficient run is not an error to FFmpeg either)
            if (ch.TransmitCoefs && gb.Position < m_numSavedBits)
                DecodeCoeffs(c);
            else
                Array.Clear(ch.Out, ch.Coeffs, subframeLen);
        }
        if (transmitCoeffs)
        {
            var mdct = m_mdct[(int)Math.Log2(subframeLen) - BlockMinBits];
            InverseChannelTransform(sfbOffsets);
            for (int i = 0; i < m_channelsForCurSubframe; i++)
            {
                var ch = m_channels[m_channelIndexes[i]];
                var sf = ch.SavedScaleFactors[ch.ScaleFactors];
                for (int b = 0; b < m_numBands; b++)
                {
                    int end = Math.Min(sfbOffsets[b + 1], m_subframeLen), start = sfbOffsets[b];
                    int exp = ch.QuantStep - (ch.MaxScaleFactor - sf[b]) * ch.ScaleFactorStep;
                    float quant = (float)Math.Pow(10, exp / 20.0);
                    for (int k = start; k < end; k++)
                        m_tmp[k] = ch.Out[ch.Coeffs + k] * quant;
                }
                mdct.InverseHalf(m_tmp.AsSpan(0, subframeLen), ch.Out.AsSpan(ch.Coeffs, subframeLen));
            }
        }
        Window();
        for (int i = 0; i < m_channelsForCurSubframe; i++)
        {
            var ch = m_channels[m_channelIndexes[i]];
            if (ch.CurSubframe >= ch.NumSubframes)
                return false;
            ch.CurSubframe++;
        }
        return true;
    }

    #endregion
}
