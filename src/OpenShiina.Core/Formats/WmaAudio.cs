// Windows Media Audio 1 and 2 (format tags 0x160 / 0x161, the sound of the WMV3 movies): a port of
// FFmpeg's wmadec.c and wma.c (LGPL 2.1 or later). Superframes of block_align bytes hold frames
// of 2^frame_len_bits samples, each made of MDCT blocks of variable length; the exponents are
// VLC or LSP coded, the coefficients run / level coded with noise substituted in high bands.

namespace OpenShiina.Formats;

public sealed class WmaAudio : IWmaDecoder
{
    private const int BlockMinBits = 7, BlockMaxBits = 11, BlockMaxSize = 1 << BlockMaxBits;
    private const int HighBandMaxSize = 16, NoiseTabSize = 8192, LspPowBits = 7;

    public int Channels { get; }
    public int SampleRate { get; }

    private readonly int m_version, m_blockAlign;
    private readonly bool m_useExpVlc, m_useBitReservoir, m_useVariableBlockLen, m_useNoiseCoding;
    private readonly int m_byteOffsetBits;
    private readonly int m_frameLen, m_frameLenBits, m_nbBlockSizes;
    private readonly int[] m_exponentSizes = new int[5];
    private readonly int[][] m_exponentBands = Enumerable.Range(0, 5).Select(_ => new int[25]).ToArray();
    private readonly int[] m_highBandStart = new int[5], m_coefsEnd = new int[5], m_exponentHighSizes = new int[5];
    private readonly int[][] m_exponentHighBands = Enumerable.Range(0, 5).Select(_ => new int[HighBandMaxSize]).ToArray();
    private readonly int m_coefsStart;
    private readonly VlcTable? m_expVlc, m_hgainVlc;
    private readonly VlcTable[] m_coefVlc = new VlcTable[2];
    private readonly int[][] m_runTable = new int[2][];
    private readonly float[][] m_levelTable = new float[2][];
    private readonly float[][] m_windows = new float[5][];
    private readonly Mdct[] m_mdct = new Mdct[5];
    private readonly float[] m_noiseTable = new float[NoiseTabSize];
    private readonly float m_noiseMult;
    private readonly float[] m_lspCosTable = new float[BlockMaxSize];
    private readonly float[] m_lspPowETable = new float[256];
    private readonly float[] m_lspPowMTable1 = new float[1 << LspPowBits], m_lspPowMTable2 = new float[1 << LspPowBits];

    // decoding state
    private BitReader m_gb = new([], 0);
    private bool m_resetBlockLengths = true;
    private int m_blockLenBits, m_nextBlockLenBits, m_prevBlockLenBits, m_blockLen, m_blockPos;
    private bool m_msStereo;
    private readonly bool[] m_channelCoded = new bool[2];
    private readonly int[] m_exponentsBsize = new int[2];
    private readonly bool[] m_exponentsInitialized = new bool[2];
    private readonly float[][] m_exponents = [new float[BlockMaxSize], new float[BlockMaxSize]];
    private readonly float[] m_maxExponent = [1, 1];
    private readonly float[][] m_coefs1 = [new float[BlockMaxSize], new float[BlockMaxSize]];
    private readonly float[][] m_coefs = [new float[BlockMaxSize], new float[BlockMaxSize]];
    private readonly float[] m_output = new float[BlockMaxSize * 2];
    private readonly float[][] m_frameOut = [new float[BlockMaxSize * 2], new float[BlockMaxSize * 2]];
    private readonly int[][] m_highBandCoded = [new int[HighBandMaxSize], new int[HighBandMaxSize]];
    private readonly int[][] m_highBandValues = [new int[HighBandMaxSize], new int[HighBandMaxSize]];
    private readonly byte[] m_lastSuperframe = new byte[32768 + 16];
    private readonly float[] m_expPower = new float[HighBandMaxSize];
    private int m_lastSuperframeLen, m_lastBitoffset, m_noiseIndex;

    private static readonly VlcTable s_hgainVlc = VlcTable.FromLengths(9,
        Enumerable.Range(0, 37).Select(i => WmaTables.HgainHufftab[2 * i + 1]).ToArray(),
        Enumerable.Range(0, 37).Select(i => (int)WmaTables.HgainHufftab[2 * i]).ToArray(), -18);
    private static readonly VlcTable s_expVlc = VlcTable.FromCodes(8, WmaTables.ScalefactorCode, WmaTables.ScalefactorBits);

    /// <param name="formatTag">0x160 (WMA 1) or 0x161 (WMA 2).</param>
    public WmaAudio(int formatTag, int channels, int sampleRate, int bytesPerSecond, int blockAlign, byte[] extra)
    {
        if (formatTag is not (0x160 or 0x161) || channels is < 1 or > 2 || sampleRate is <= 0 or > 50000 || blockAlign <= 0 || bytesPerSecond <= 0)
            throw new InvalidDataException("Not a WMA 1 / 2 stream");
        Channels = channels;
        SampleRate = sampleRate;
        m_blockAlign = blockAlign;
        m_version = formatTag == 0x160 ? 1 : 2;
        int flags2 = 0;
        if (m_version == 1 && extra.Length >= 4)
            flags2 = extra[2] | extra[3] << 8;
        else if (m_version == 2 && extra.Length >= 6)
            flags2 = extra[4] | extra[5] << 8;
        m_useExpVlc = (flags2 & 1) != 0;
        m_useBitReservoir = (flags2 & 2) != 0;
        m_useVariableBlockLen = (flags2 & 4) != 0;
        if (m_version == 2 && extra.Length >= 8 && (extra[4] | extra[5] << 8) == 0xD && m_useVariableBlockLen)
            m_useVariableBlockLen = false;

        // ff_wma_init
        int bitRate = bytesPerSecond * 8;
        m_frameLenBits = sampleRate <= 16000 ? 9 : sampleRate <= 22050 || (sampleRate <= 32000 && m_version == 1) ? 10 : 11;
        m_nextBlockLenBits = m_prevBlockLenBits = m_blockLenBits = m_frameLenBits;
        m_frameLen = 1 << m_frameLenBits;
        if (m_useVariableBlockLen)
        {
            int nb = ((flags2 >> 3) & 3) + 1;
            if (bitRate / channels >= 32000)
                nb += 2;
            nb = Math.Min(nb, m_frameLenBits - BlockMinBits);
            m_nbBlockSizes = nb + 1;
        }
        else
            m_nbBlockSizes = 1;

        m_useNoiseCoding = true;
        float highFreq = sampleRate * 0.5f;
        int sampleRate1 = sampleRate;
        if (m_version == 2)
            sampleRate1 = sampleRate1 >= 44100 ? 44100 : sampleRate1 >= 22050 ? 22050 : sampleRate1 >= 16000 ? 16000
                : sampleRate1 >= 11025 ? 11025 : sampleRate1 >= 8000 ? 8000 : sampleRate1;
        float bps = (float)bitRate / (channels * sampleRate);
        m_byteOffsetBits = (int)Math.Log2((int)(bps * m_frameLen / 8.0 + 0.5)) + 2;
        float bps1 = channels == 2 ? bps * 1.6f : bps;
        if (sampleRate1 == 44100)
        {
            if (bps1 >= 0.61f)
                m_useNoiseCoding = false;
            else
                highFreq *= 0.4f;
        }
        else if (sampleRate1 == 22050)
        {
            if (bps1 >= 1.16f)
                m_useNoiseCoding = false;
            else
                highFreq *= bps1 >= 0.72f ? 0.7f : 0.6f;
        }
        else if (sampleRate1 == 16000)
            highFreq *= bps > 0.5f ? 0.5f : 0.3f;
        else if (sampleRate1 == 11025)
            highFreq *= 0.7f;
        else if (sampleRate1 == 8000)
        {
            if (bps <= 0.625f)
                highFreq *= 0.5f;
            else if (bps > 0.75f)
                m_useNoiseCoding = false;
            else
                highFreq *= 0.65f;
        }
        else
            highFreq *= bps >= 0.8f ? 0.75f : bps >= 0.6f ? 0.6f : 0.5f;

        m_coefsStart = m_version == 1 ? 3 : 0;
        for (int k = 0; k < m_nbBlockSizes; k++)
        {
            int blockLen = m_frameLen >> k;
            if (m_version == 1)
            {
                int lpos = 0, i;
                for (i = 0; i < 25; i++)
                {
                    int pos = (blockLen * 2 * WmaTables.CriticalFreqs[i] + (sampleRate >> 1)) / sampleRate;
                    pos = Math.Min(pos, blockLen);
                    m_exponentBands[0][i] = pos - lpos;
                    if (pos >= blockLen)
                    {
                        i++;
                        break;
                    }
                    lpos = pos;
                }
                m_exponentSizes[0] = i;
            }
            else
            {
                byte[]? table = null;
                int a = m_frameLenBits - BlockMinBits - k;
                if (a < 3)
                    table = sampleRate >= 44100 ? WmaTables.ExponentBand44100 : sampleRate >= 32000 ? WmaTables.ExponentBand32000
                        : sampleRate >= 22050 ? WmaTables.ExponentBand22050 : null;
                if (table != null)
                {
                    int n = table[a * 25];
                    for (int i = 0; i < n; i++)
                        m_exponentBands[k][i] = table[a * 25 + 1 + i];
                    m_exponentSizes[k] = n;
                }
                else
                {
                    int j = 0, lpos = 0;
                    for (int i = 0; i < 25; i++)
                    {
                        int pos = (blockLen * 2 * WmaTables.CriticalFreqs[i] + (sampleRate << 1)) / (4 * sampleRate);
                        pos <<= 2;
                        pos = Math.Min(pos, blockLen);
                        if (pos > lpos)
                            m_exponentBands[k][j++] = pos - lpos;
                        if (pos >= blockLen)
                            break;
                        lpos = pos;
                    }
                    m_exponentSizes[k] = j;
                }
            }
            m_coefsEnd[k] = (m_frameLen - m_frameLen * 9 / 100) >> k;
            m_highBandStart[k] = (int)(blockLen * 2 * highFreq / sampleRate + 0.5);
            int nb2 = m_exponentSizes[k], jj = 0, p = 0;
            for (int i = 0; i < nb2; i++)
            {
                int start = p;
                p += m_exponentBands[k][i];
                int end = p;
                start = Math.Max(start, m_highBandStart[k]);
                end = Math.Min(end, m_coefsEnd[k]);
                if (end > start)
                    m_exponentHighBands[k][jj++] = end - start;
            }
            m_exponentHighSizes[k] = jj;
        }

        for (int i = 0; i < m_nbBlockSizes; i++)
        {
            int len = 1 << (m_frameLenBits - i);
            var w = new float[len];
            for (int k = 0; k < len; k++)
                w[k] = (float)Math.Sin((k + 0.5) * (Math.PI / (2.0 * len)));
            m_windows[i] = w;
            m_mdct[i] = new Mdct(m_frameLenBits - i + 1, 1.0 / 32768.0);
        }

        if (m_useNoiseCoding)
        {
            m_noiseMult = m_useExpVlc ? 0.02f : 0.04f;
            uint seed = 1;
            float norm = (float)(1.0 / (1L << 31) * Math.Sqrt(3) * m_noiseMult);
            for (int i = 0; i < NoiseTabSize; i++)
            {
                seed = seed * 314159 + 1;
                m_noiseTable[i] = (int)seed * norm;
            }
            m_hgainVlc = s_hgainVlc;
        }

        int coefVlcTable = 2;
        if (sampleRate >= 32000)
        {
            if (bps1 < 0.72f)
                coefVlcTable = 0;
            else if (bps1 < 1.16f)
                coefVlcTable = 1;
        }
        for (int t = 0; t < 2; t++)
            InitCoefVlc(t, coefVlcTable * 2 + t);

        if (m_useExpVlc)
            m_expVlc = s_expVlc;
        else
            LspToCurveInit();
    }

    private void InitCoefVlc(int slot, int index)
    {
        var (codes, bits, levels) = index switch
        {
            0 => (WmaTables.Coef0Codes, WmaTables.Coef0Bits, WmaTables.Levels0),
            1 => (WmaTables.Coef1Codes, WmaTables.Coef1Bits, WmaTables.Levels1),
            2 => (WmaTables.Coef2Codes, WmaTables.Coef2Bits, WmaTables.Levels2),
            3 => (WmaTables.Coef3Codes, WmaTables.Coef3Bits, WmaTables.Levels3),
            4 => (WmaTables.Coef4Codes, WmaTables.Coef4Bits, WmaTables.Levels4),
            _ => (WmaTables.Coef5Codes, WmaTables.Coef5Bits, WmaTables.Levels5),
        };
        int n = bits.Length;
        m_coefVlc[slot] = VlcTable.FromCodes(9, codes, bits);
        var run = new int[n];
        var level = new float[n];
        int i = 2, lv = 1, k = 0;
        while (i < n)
        {
            int l = levels[k++];
            for (int j = 0; j < l && i < n; j++, i++)
            {
                run[i] = j;
                level[i] = lv;
            }
            lv++;
        }
        m_runTable[slot] = run;
        m_levelTable[slot] = level;
    }

    private void LspToCurveInit()
    {
        double wdel = Math.PI / m_frameLen;
        for (int i = 0; i < m_frameLen; i++)
            m_lspCosTable[i] = (float)(2.0f * Math.Cos(wdel * i));
        for (int i = 0; i < 256; i++)
            m_lspPowETable[i] = MathF.Pow(2, (i - 126) * -0.25f);
        float b = 1;
        for (int i = (1 << LspPowBits) - 1; i >= 0; i--)
        {
            int m = (1 << LspPowBits) + i;
            float a = (float)(m * (0.5 / (1 << LspPowBits)));
            a = (float)(1 / Math.Sqrt(Math.Sqrt(a)));
            m_lspPowMTable1[i] = 2 * a - b;
            m_lspPowMTable2[i] = b - a;
            b = a;
        }
    }

    private float PowM14(float x)
    {
        uint v = BitConverter.SingleToUInt32Bits(x);
        uint e = v >> 23, m = (v >> (23 - LspPowBits)) & ((1 << LspPowBits) - 1);
        float t = BitConverter.UInt32BitsToSingle(((v << LspPowBits) & ((1 << 23) - 1)) | (127u << 23));
        return m_lspPowETable[e] * (m_lspPowMTable1[m] + m_lspPowMTable2[m] * t);
    }

    private void DecodeExpLsp(int ch)
    {
        Span<float> lsp = stackalloc float[10];
        for (int i = 0; i < 10; i++)
        {
            int val = i == 0 || i >= 8 ? m_gb.Get(3) : m_gb.Get(4);
            lsp[i] = WmaTables.LspCodebook[i * 16 + val];
        }
        float valMax = 0;
        var o = m_exponents[ch];
        for (int i = 0; i < m_blockLen; i++)
        {
            float p = 0.5f, q = 0.5f, w = m_lspCosTable[i];
            for (int j = 1; j < 10; j += 2)
            {
                q *= w - lsp[j - 1];
                p *= w - lsp[j];
            }
            p *= p * (2.0f - w);
            q *= q * (2.0f + w);
            float v = PowM14(p + q);
            if (v > valMax)
                valMax = v;
            o[i] = v;
        }
        m_maxExponent[ch] = valMax;
    }

    /// <summary>pow(10, i / 16) for i in -60..95.</summary>
    private static readonly float[] s_powTab = Enumerable.Range(-60, 156).Select(i => (float)Math.Pow(10, i / 16.0)).ToArray();

    private void DecodeExpVlc(int ch)
    {
        var bands = m_exponentBands[m_frameLenBits - m_blockLenBits];
        var q = m_exponents[ch];
        int qi = 0, band = 0, lastExp;
        float maxScale = 0;
        if (m_version == 1)
        {
            lastExp = m_gb.Get(5) + 10;
            float v = s_powTab[lastExp + 60];
            maxScale = v;
            int n = bands[band++];
            for (int i = 0; i < n; i++)
                q[qi++] = v;
        }
        else
            lastExp = 36;
        while (qi < m_blockLen)
        {
            int code = m_expVlc!.Read(m_gb);
            lastExp += code - 60;
            if ((uint)(lastExp + 60) >= s_powTab.Length)
                throw new InvalidDataException("WMA exponent out of range");
            float v = s_powTab[lastExp + 60];
            if (v > maxScale)
                maxScale = v;
            int n = bands[band++];
            for (int i = 0; i < n && qi < q.Length; i++)
                q[qi++] = v;
        }
        m_maxExponent[ch] = maxScale;
    }

    /// <summary>ff_wma_run_level_decode (WMA 1 / 2 escapes).</summary>
    private void RunLevelDecode(int t, float[] ptr, int numCoefs, int blockLen, int coefNbBits)
    {
        var vlc = m_coefVlc[t];
        var run = m_runTable[t];
        var level = m_levelTable[t];
        int mask = blockLen - 1, offset = 0;
        for (; offset < numCoefs; offset++)
        {
            int code = vlc.Read(m_gb);
            if (code > 1)
            {
                offset += run[code];
                int sign = m_gb.Bit();
                ptr[offset & mask] = sign != 0 ? level[code] : -level[code];
            }
            else if (code == 1)
                break;
            else
            {
                int lv = m_gb.Get(coefNbBits);
                offset += m_gb.Get(m_frameLenBits);
                int sign = m_gb.Bit() - 1;
                ptr[offset & mask] = (lv ^ sign) - sign;
            }
        }
        if (offset > numCoefs)
            throw new InvalidDataException("WMA coefficient overflow");
    }

    private void Window(float[] o, int at)
    {
        var input = m_output;
        if (m_blockLenBits <= m_prevBlockLenBits)
        {
            var w = m_windows[m_frameLenBits - m_blockLenBits];
            for (int i = 0; i < m_blockLen; i++)
                o[at + i] += input[i] * w[i];
        }
        else
        {
            int blockLen = 1 << m_prevBlockLenBits, n = (m_blockLen - blockLen) / 2;
            var w = m_windows[m_frameLenBits - m_prevBlockLenBits];
            for (int i = 0; i < blockLen; i++)
                o[at + n + i] += input[n + i] * w[i];
            Array.Copy(input, n + blockLen, o, at + n + blockLen, n);
        }
        at += m_blockLen;
        int inAt = m_blockLen;
        if (m_blockLenBits <= m_nextBlockLenBits)
        {
            var w = m_windows[m_frameLenBits - m_blockLenBits];
            for (int i = 0; i < m_blockLen; i++)
                o[at + i] = input[inAt + i] * w[m_blockLen - 1 - i];
        }
        else
        {
            int blockLen = 1 << m_nextBlockLenBits, n = (m_blockLen - blockLen) / 2;
            var w = m_windows[m_frameLenBits - m_nextBlockLenBits];
            Array.Copy(input, inAt, o, at, n);
            for (int i = 0; i < blockLen; i++)
                o[at + n + i] = input[inAt + n + i] * w[blockLen - 1 - i];
            Array.Clear(o, at + n + blockLen, n);
        }
    }

    private static int TotalGainToBits(int g) => g < 15 ? 13 : g < 32 ? 12 : g < 40 ? 11 : g < 45 ? 10 : 9;

    /// <summary>wma_decode_block: true after the frame's last block.</summary>
    private bool DecodeBlock()
    {
        if (m_useVariableBlockLen)
        {
            int n = (int)Math.Log2(m_nbBlockSizes - 1) + 1;
            if (m_resetBlockLengths)
            {
                m_resetBlockLengths = false;
                int v1 = m_gb.Get(n);
                if (v1 >= m_nbBlockSizes)
                    throw new InvalidDataException("WMA block length");
                m_prevBlockLenBits = m_frameLenBits - v1;
                v1 = m_gb.Get(n);
                if (v1 >= m_nbBlockSizes)
                    throw new InvalidDataException("WMA block length");
                m_blockLenBits = m_frameLenBits - v1;
            }
            else
            {
                m_prevBlockLenBits = m_blockLenBits;
                m_blockLenBits = m_nextBlockLenBits;
            }
            int v = m_gb.Get(n);
            if (v >= m_nbBlockSizes)
                throw new InvalidDataException("WMA block length");
            m_nextBlockLenBits = m_frameLenBits - v;
        }
        else
            m_nextBlockLenBits = m_prevBlockLenBits = m_blockLenBits = m_frameLenBits;
        if (m_frameLenBits - m_blockLenBits >= m_nbBlockSizes)
            throw new InvalidDataException("WMA block length");
        m_blockLen = 1 << m_blockLenBits;
        if (m_blockPos + m_blockLen > m_frameLen)
            throw new InvalidDataException("WMA frame length");

        int channels = Channels;
        if (channels == 2)
            m_msStereo = m_gb.Bit() != 0;
        bool anyCoded = false;
        for (int ch = 0; ch < channels; ch++)
        {
            m_channelCoded[ch] = m_gb.Bit() != 0;
            anyCoded |= m_channelCoded[ch];
        }
        int bsize = m_frameLenBits - m_blockLenBits;
        if (anyCoded)
            DecodeBlockCoefficients(bsize);

        var mdct = m_mdct[bsize];
        for (int ch = 0; ch < channels; ch++)
        {
            int n4 = m_blockLen / 2;
            if (m_channelCoded[ch])
                mdct.Inverse(m_coefs[ch].AsSpan(0, m_blockLen), m_output.AsSpan(0, 2 * m_blockLen));
            else if (!(m_msStereo && ch == 1))
                Array.Clear(m_output);
            Window(m_frameOut[ch], m_frameLen / 2 + m_blockPos - n4);
        }
        m_blockPos += m_blockLen;
        return m_blockPos >= m_frameLen;
    }

    private void DecodeBlockCoefficients(int bsize)
    {
        int channels = Channels;
        int totalGain = 1;
        while (true)
        {
            if (m_gb.Left < 7)
                throw new InvalidDataException("WMA total gain");
            int a = m_gb.Get(7);
            totalGain += a;
            if (a != 127)
                break;
        }
        int coefNbBits = TotalGainToBits(totalGain);
        Span<int> nbCoefs = stackalloc int[2];
        int n = m_coefsEnd[bsize] - m_coefsStart;
        for (int ch = 0; ch < channels; ch++)
            nbCoefs[ch] = n;
        if (m_useNoiseCoding)
        {
            for (int ch = 0; ch < channels; ch++)
                if (m_channelCoded[ch])
                    for (int i = 0; i < m_exponentHighSizes[bsize]; i++)
                    {
                        int a = m_gb.Bit();
                        m_highBandCoded[ch][i] = a;
                        if (a != 0)
                            nbCoefs[ch] -= m_exponentHighBands[bsize][i];
                    }
            for (int ch = 0; ch < channels; ch++)
                if (m_channelCoded[ch])
                {
                    int val = int.MinValue;
                    for (int i = 0; i < m_exponentHighSizes[bsize]; i++)
                        if (m_highBandCoded[ch][i] != 0)
                        {
                            val = val == int.MinValue ? m_gb.Get(7) - 19 : val + m_hgainVlc!.Read(m_gb);
                            m_highBandValues[ch][i] = val;
                        }
                }
        }
        if (m_blockLenBits == m_frameLenBits || m_gb.Bit() != 0)
            for (int ch = 0; ch < channels; ch++)
                if (m_channelCoded[ch])
                {
                    if (m_useExpVlc)
                        DecodeExpVlc(ch);
                    else
                        DecodeExpLsp(ch);
                    m_exponentsBsize[ch] = bsize;
                    m_exponentsInitialized[ch] = true;
                }
        for (int ch = 0; ch < channels; ch++)
            if (m_channelCoded[ch] && !m_exponentsInitialized[ch])
                throw new InvalidDataException("WMA exponents missing");
        for (int ch = 0; ch < channels; ch++)
        {
            if (m_channelCoded[ch])
            {
                int t = ch == 1 && m_msStereo ? 1 : 0;
                Array.Clear(m_coefs1[ch], 0, m_blockLen);
                RunLevelDecode(t, m_coefs1[ch], nbCoefs[ch], m_blockLen, coefNbBits);
            }
            if (m_version == 1 && channels >= 2)
                m_gb.Align();
        }

        float mdctNorm = 1.0f / (m_blockLen / 2);
        if (m_version == 1)
            mdctNorm *= MathF.Sqrt(m_blockLen / 2);
        for (int ch = 0; ch < channels; ch++)
        {
            if (!m_channelCoded[ch])
                continue;
            var coefs1 = m_coefs1[ch];
            var exponents = m_exponents[ch];
            int esize = m_exponentsBsize[ch];
            float mult = (float)(Math.Pow(10, totalGain * 0.05) / m_maxExponent[ch]) * mdctNorm;
            var coefs = m_coefs[ch];
            int c = 0, c1 = 0;
            if (m_useNoiseCoding)
            {
                float mult1 = mult;
                for (int i = 0; i < m_coefsStart; i++)
                {
                    coefs[c++] = m_noiseTable[m_noiseIndex] * exponents[(i << bsize) >> esize] * mult1;
                    m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
                }
                int n1 = m_exponentHighSizes[bsize];
                var expPower = m_expPower;
                int e = (m_highBandStart[bsize] << bsize) >> esize;
                int lastHighBand = 0;
                for (int j = 0; j < n1; j++)
                {
                    int nb = m_exponentHighBands[bsize][j];
                    if (m_highBandCoded[ch][j] != 0)
                    {
                        float e2 = 0;
                        for (int i = 0; i < nb; i++)
                        {
                            float v = exponents[e + ((i << bsize) >> esize)];
                            e2 += v * v;
                        }
                        expPower[j] = e2 / nb;
                        lastHighBand = j;
                    }
                    e += (nb << bsize) >> esize;
                }
                e = (m_coefsStart << bsize) >> esize;
                for (int j = -1; j < n1; j++)
                {
                    int nb = j < 0 ? m_highBandStart[bsize] - m_coefsStart : m_exponentHighBands[bsize][j];
                    if (j >= 0 && m_highBandCoded[ch][j] != 0)
                    {
                        float m1 = MathF.Sqrt(expPower[j] / expPower[lastHighBand]);
                        m1 *= (float)Math.Pow(10, m_highBandValues[ch][j] * 0.05);
                        m1 /= m_maxExponent[ch] * m_noiseMult;
                        m1 *= mdctNorm;
                        for (int i = 0; i < nb; i++)
                        {
                            float noise = m_noiseTable[m_noiseIndex];
                            m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
                            coefs[c++] = noise * exponents[e + ((i << bsize) >> esize)] * m1;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < nb; i++)
                        {
                            float noise = m_noiseTable[m_noiseIndex];
                            m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
                            coefs[c++] = (coefs1[c1++] + noise) * exponents[e + ((i << bsize) >> esize)] * mult;
                        }
                    }
                    e += (nb << bsize) >> esize;
                }
                int rest = m_blockLen - m_coefsEnd[bsize];
                float mult2 = mult * exponents[e + ((-(1 << bsize)) >> esize)];
                for (int i = 0; i < rest; i++)
                {
                    coefs[c++] = m_noiseTable[m_noiseIndex] * mult2;
                    m_noiseIndex = (m_noiseIndex + 1) & (NoiseTabSize - 1);
                }
            }
            else
            {
                for (int i = 0; i < m_coefsStart; i++)
                    coefs[c++] = 0;
                for (int i = 0; i < nbCoefs[ch]; i++)
                    coefs[c++] = coefs1[i] * exponents[(i << bsize) >> esize] * mult;
                int rest = m_blockLen - m_coefsEnd[bsize];
                for (int i = 0; i < rest; i++)
                    coefs[c++] = 0;
            }
        }
        if (m_msStereo && m_channelCoded[1])
        {
            if (!m_channelCoded[0])
            {
                Array.Clear(m_coefs[0], 0, m_blockLen);
                m_channelCoded[0] = true;
            }
            var a0 = m_coefs[0];
            var a1 = m_coefs[1];
            for (int i = 0; i < m_blockLen; i++)
            {
                float t = a0[i] - a1[i];
                a0[i] += a1[i];
                a1[i] = t;
            }
        }
    }

    /// <summary>wma_decode_frame: a frame's blocks, the frame's samples out.</summary>
    private void DecodeFrame(List<float>[] output)
    {
        m_blockPos = 0;
        while (!DecodeBlock())
        {
        }
        for (int ch = 0; ch < Channels; ch++)
        {
            var fo = m_frameOut[ch];
            for (int i = 0; i < m_frameLen; i++)
                output[ch].Add(fo[i]);
            Array.Copy(fo, m_frameLen, fo, 0, m_frameLen);
            Array.Clear(fo, m_frameLen, m_frameLen);
        }
    }

    /// <summary>wma_decode_superframe: a packet of block_align bytes; its frames' samples are added to <paramref name="output"/>.</summary>
    public void DecodePacket(ReadOnlySpan<byte> packet, List<float>[] output)
    {
        if (packet.Length < m_blockAlign)
            throw new InvalidDataException("WMA packet too small");
        int bufSize = m_blockAlign;
        var buf = new byte[bufSize + 16];
        packet[..bufSize].CopyTo(buf);
        m_gb = new BitReader(buf, bufSize * 8);
        try
        {
            if (!m_useBitReservoir)
            {
                DecodeFrame(output);
                return;
            }
            m_gb.Skip(4);
            int nbFrames = m_gb.Get(4) - (m_lastSuperframeLen <= 0 ? 1 : 0);
            if (nbFrames <= 0)
            {
                if (nbFrames < 0 || m_gb.Left <= 8 || m_lastSuperframeLen + bufSize - 1 > 32768)
                    throw new InvalidDataException("WMA superframe");
                for (int i = 0; i < bufSize - 1; i++)
                    m_lastSuperframe[m_lastSuperframeLen + i] = (byte)m_gb.Get(8);
                m_lastSuperframeLen += 8 * bufSize - 8;
                return;
            }
            int bitOffset = m_gb.Get(m_byteOffsetBits + 3);
            if (bitOffset > m_gb.Left)
                throw new InvalidDataException("WMA bit offset");
            if (m_lastSuperframeLen > 0)
            {
                if (m_lastSuperframeLen + ((bitOffset + 7) >> 3) > 32768)
                    throw new InvalidDataException("WMA superframe");
                int q = m_lastSuperframeLen, len = bitOffset;
                while (len > 7)
                {
                    m_lastSuperframe[q++] = (byte)m_gb.Get(8);
                    len -= 8;
                }
                if (len > 0)
                    m_lastSuperframe[q++] = (byte)(m_gb.Get(len) << (8 - len));
                Array.Clear(m_lastSuperframe, q, Math.Min(16, m_lastSuperframe.Length - q));
                var save = m_gb;
                m_gb = new BitReader(m_lastSuperframe, m_lastSuperframeLen * 8 + bitOffset, m_lastBitoffset);
                DecodeFrame(output);
                m_gb = save;
                nbFrames--;
            }
            int pos = bitOffset + 4 + 4 + m_byteOffsetBits + 3;
            if (pos >= 32768 * 8 || pos > bufSize * 8)
                throw new InvalidDataException("WMA superframe");
            m_gb = new BitReader(buf, bufSize * 8, pos);
            m_resetBlockLengths = true;
            for (int i = 0; i < nbFrames; i++)
                DecodeFrame(output);
            pos = m_gb.Position;
            m_lastBitoffset = pos & 7;
            pos >>= 3;
            int rest = bufSize - pos;
            if (rest is > 32768 or < 0)
                throw new InvalidDataException("WMA superframe");
            m_lastSuperframeLen = rest;
            Array.Copy(buf, pos, m_lastSuperframe, 0, rest);
        }
        catch (InvalidDataException)
        {
            m_lastSuperframeLen = 0;
            throw;
        }
    }

    /// <summary>Samples the decoder puts out before the stream's first ones (FFmpeg's skip_samples).</summary>
    public int Delay => m_frameLen * 2;
}
