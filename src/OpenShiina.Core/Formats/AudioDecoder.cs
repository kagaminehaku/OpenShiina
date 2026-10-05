// ShiinaRio OGV and PAD audio decoders
// Ported from GARbro ArcFormats/ShiinaRio/AudioOGV.cs and AudioPAD.cs

using System.IO;

namespace OpenShiina.Formats;

public class AudioDecoder
{
    public static bool IsOgv(byte[] data)
    {
        return data.Length >= 24 && data[0] == 'O' && data[1] == 'G' && data[2] == 'V';
    }

    public static bool IsPad(byte[] data)
    {
        return data.Length >= 0x2c && data[0] == 'P' && data[1] == 'A' && data[2] == 'D';
    }

    /// <summary>
    /// Decodes an OGV container into standard raw Ogg Vorbis stream bytes.
    /// </summary>
    public static byte[]? DecodeOgv(byte[] data)
    {
        if (!IsOgv(data)) return null;

        int pos = 0xc;
        if (pos + 8 > data.Length) return null;

        if (!Binary.AsciiEqual(data, pos, "fmt "))
            return null;

        uint fmtSize = LittleEndian.ToUInt32(data, pos + 4);
        pos += 8 + (int)fmtSize;

        if (pos + 8 > data.Length) return null;
        if (!Binary.AsciiEqual(data, pos, "data"))
            return null;

        pos += 8;
        int oggLength = data.Length - pos;
        if (oggLength <= 0) return null;

        byte[] oggData = new byte[oggLength];
        Buffer.BlockCopy(data, pos, oggData, 0, oggLength);
        return oggData;
    }

    /// <summary>
    /// Decodes a PAD compressed audio stream into a standard WAV PCM stream.
    /// </summary>
    public static byte[]? DecodePad(byte[] data)
    {
        if (!IsPad(data) || data.Length < 0x2c) return null;

        var wav_header = new byte[0x2c];
        Buffer.BlockCopy(data, 0, wav_header, 0, 0x2c);

        int pcm_size = LittleEndian.ToInt32(wav_header, 0x28);
        int channels = LittleEndian.ToUInt16(wav_header, 0x16);

        wav_header[0] = (byte)'R';
        wav_header[1] = (byte)'I';
        wav_header[2] = (byte)'F';
        wav_header[3] = (byte)'F';
        LittleEndian.Pack(pcm_size + 0x24, wav_header, 4);

        using var stream = new MemoryStream(data, 0x2c, data.Length - 0x2c);
        var decoder = new PadDecoder(stream, pcm_size, channels);
        decoder.Unpack();

        var result = new byte[0x2c + pcm_size];
        Buffer.BlockCopy(wav_header, 0, result, 0, 0x2c);
        Buffer.BlockCopy(decoder.Data, 0, result, 0x2c, pcm_size);
        return result;
    }

    /// <summary>
    /// Converts Ogg Vorbis bytes to standard PCM WAV bytes using NVorbis.
    /// </summary>
    public static byte[] OggToWav(byte[] oggBytes)
    {
        using var ms = new MemoryStream(oggBytes);
        using var vorbis = new NVorbis.VorbisReader(ms);
        int channels = vorbis.Channels;
        int sampleRate = vorbis.SampleRate;

        // ReadSamples may return fewer samples than requested and TotalSamples is only an
        // estimate, so read in chunks until the stream is exhausted.
        var samples = new List<float>();
        var chunk = new float[4096 * channels];
        int n;
        while ((n = vorbis.ReadSamples(chunk, 0, chunk.Length)) > 0)
            samples.AddRange(new ArraySegment<float>(chunk, 0, n));
        var buffer = samples.ToArray();
        int readSamples = buffer.Length;

        using var outMs = new MemoryStream();
        using var bw = new BinaryWriter(outMs);
        int dataSize = readSamples * 2;

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(dataSize + 36);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        for (int i = 0; i < readSamples; i++)
        {
            float f = Math.Clamp(buffer[i], -1.0f, 1.0f);
            short sample = (short)(f * 32767.0f);
            bw.Write(sample);
        }

        return outMs.ToArray();
    }

    private class PadDecoder
    {
        private readonly byte[] m_input;
        private readonly byte[] m_output;
        private readonly int m_pcm_size;
        private readonly int m_packed_size;
        private readonly int m_channels;

        public byte[] Data => m_output;

        public PadDecoder(Stream input, int pcm_size, int channels)
        {
            m_packed_size = (int)(input.Length - input.Position);
            m_pcm_size = pcm_size;
            m_channels = channels;
            m_output = new byte[pcm_size + 0x9c];
            m_input = new byte[m_packed_size];
            if (m_packed_size != input.Read(m_input, 0, m_packed_size))
                throw new InvalidDataException("Unexpected end of file");
        }

        public byte[] Unpack()
        {
            int v10;
            double v3 = 0;
            double v27 = 0;
            double v28 = 0;
            int v30 = 0;

            var table = new double[69];
            table[4] = 0.9375;
            table[6] = 1.796875;
            table[7] = -0.8125;
            table[8] = 1.53125;
            table[9] = -0.859375;
            table[10] = 1.90625;
            table[11] = -0.9375;

            int dst = 0;
            int src = 0;
            if (src >= m_input.Length) return m_output;
            int v5 = m_input[src++];
            while (v5 != 0xff && src < m_input.Length && dst < m_pcm_size)
            {
                int v7 = m_input[src++];
                int v29 = v7 >> 4;
                int v9 = v7 & 0xF;
                if (2 == m_channels)
                {
                    if (src + 1 >= m_input.Length) break;
                    int next = m_input[src + 1];
                    v10 = next & 0xF;
                    v30 = next >> 4;
                    long v = BitConverter.DoubleToInt64Bits(table[12]) & 0xffffffffL;
                    table[12] = BitConverter.Int64BitsToDouble(v | (long)v10 << 32);
                    src += 2;
                }
                else
                {
                    v10 = (int)(BitConverter.DoubleToInt64Bits(table[12]) >> 32);
                }
                int v12 = 14;
                for (int i = 0; i < 14; ++i)
                {
                    if (src >= m_input.Length) break;
                    int v13 = m_input[src++];
                    int v14 = (v13 & 0xF) << 12;
                    if (0 != (v14 & 0x8000))
                        v14 |= ~0xFFFF;
                    int v15 = (v13 & 0xF0) << 8;
                    table[v12 - 1] = (double)(v14 >> v9);
                    if (0 != (v15 & 0x8000))
                        v15 |= ~0xFFFF;
                    table[v12] = (double)(v15 >> v9);
                    v12 += 2;
                }
                if (2 == m_channels)
                {
                    v12 = 42;
                    for (int i = 0; i < 14; ++i)
                    {
                        if (src >= m_input.Length) break;
                        int v18 = m_input[src++];
                        int v19 = (v18 & 0xF) << 12;
                        if (0 != (v19 & 0x8000))
                            v19 |= ~0xFFFF;
                        int v20 = (byte)(v18 & 0xF0) << 8;
                        table[v12 - 1] = (double)(v19 >> v10);
                        if (0 != (v20 & 0x8000))
                            v20 |= ~0xFFFF;
                        table[v12] = (double)(v20 >> v10);
                        v12 += 2;
                    }
                }
                v12 = 41;
                for (int i = 0; i < 28 && dst + 1 < m_output.Length; ++i)
                {
                    double v22 = v27 * table[2 * v29 + 3];
                    v27 = table[0];
                    table[v12 - 28] += v22 + v27 * table[2 * v29 + 2];
                    table[0] = table[v12 - 28];
                    LittleEndian.Pack((short)(table[v12 - 28] + 0.5), m_output, dst);
                    dst += 2;
                    if (2 == m_channels)
                    {
                        if (dst + 1 >= m_output.Length) break;
                        table[v12] += v28 * table[2 * v30 + 3] + v3 * table[2 * v30 + 2];
                        v28 = v3;
                        v3 = table[v12];
                        LittleEndian.Pack((short)(table[v12] + 0.5), m_output, dst);
                        dst += 2;
                    }
                    ++v12;
                }
                if (src >= m_input.Length) break;
                v5 = m_input[src++];
            }
            return m_output;
        }
    }
}
