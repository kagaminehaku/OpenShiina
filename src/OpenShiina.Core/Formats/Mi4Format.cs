// ShiinaRio MI4 image decoder
// Ported from GARbro ArcFormats/ShiinaRio/ImageMI4.cs

using System.IO;

namespace OpenShiina.Formats;

public class Mi4Decoder
{
    public static bool IsMi4(byte[] data)
    {
        return data.Length >= 16 && data[0] == 'M' && data[1] == 'A' && data[2] == 'I' && data[3] == '4';
    }

    /// <summary>The picture as BGR pixels, or null when the data is not MI4.</summary>
    public static PixelImage? Decode(byte[] data)
    {
        if (!IsMi4(data)) return null;

        uint width = LittleEndian.ToUInt32(data, 8);
        uint height = LittleEndian.ToUInt32(data, 12);
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
            return null;

        using var stream = new BinMemoryStream(data);
        var reader = new Mi4Reader(stream, (int)width, (int)height);

        try
        {
            reader.Unpack(MaiVersion.Second);
        }
        catch
        {
            reader.Unpack(MaiVersion.First);
        }

        return new PixelImage((int)width, (int)height, PixelLayout.Bgr24, reader.Data);
    }

    internal enum MaiVersion
    {
        First, Second
    }

    internal class Mi4Reader
    {
        private readonly IBinaryStream m_input;
        private readonly byte[] m_output;
        private readonly int m_stride;
        private int m_bit_count;
        private uint m_bits;

        public byte[] Data => m_output;
        public int Stride => m_stride;

        public Mi4Reader(IBinaryStream file, int width, int height)
        {
            m_input = file;
            m_stride = width * 3;
            m_output = new byte[m_stride * height];
        }

        public void Unpack(MaiVersion version = MaiVersion.First)
        {
            m_input.Position = 0x10;
            m_bit_count = 0;
            LoadBits();
            if (MaiVersion.First == version)
                UnpackV1();
            else
                UnpackV2();
        }

        private void LoadBits()
        {
            for (int i = 0; i < 4; ++i)
            {
                int b = m_input.ReadByte();
                if (-1 == b)
                    break;
                m_bits = (m_bits >> 8) | (uint)(b << 24);
                m_bit_count += 8;
            }
        }

        private uint GetBit()
        {
            uint bit = m_bits >> 31;
            m_bits <<= 1;
            if (0 == --m_bit_count)
            {
                LoadBits();
            }
            return bit;
        }

        private uint GetBits(int count)
        {
            int avail_bits = Math.Min(count, m_bit_count);
            uint bits = m_bits >> (32 - avail_bits);
            m_bits <<= avail_bits;
            m_bit_count -= avail_bits;
            count -= avail_bits;
            if (0 == m_bit_count)
            {
                LoadBits();
                if (count > 0)
                {
                    bits = bits << count | m_bits >> (32 - count);
                    m_bits <<= count;
                    m_bit_count -= count;
                }
            }
            return bits;
        }

        private void UnpackV1()
        {
            int dst = 0;
            byte b = 0, g = 0, r = 0;
            while (dst < m_output.Length)
            {
                if (GetBit() == 0)
                {
                    if (GetBit() != 0)
                    {
                        b = m_input.ReadUInt8();
                        g = m_input.ReadUInt8();
                        r = m_input.ReadUInt8();
                    }
                    else if (GetBit() != 0)
                    {
                        byte v = (byte)GetBits(2);
                        if (3 == v)
                        {
                            b = m_output[dst - m_stride];
                            g = m_output[dst - m_stride + 1];
                            r = m_output[dst - m_stride + 2];
                        }
                        else
                        {
                            b += (byte)(v - 1);
                            g += (byte)(GetBits(2) - 1);
                            r += (byte)(GetBits(2) - 1);
                        }
                    }
                    else if (GetBit() != 0)
                    {
                        byte v = (byte)GetBits(3);
                        if (7 == v)
                        {
                            b = m_output[dst - m_stride + 3];
                            g = m_output[dst - m_stride + 4];
                            r = m_output[dst - m_stride + 5];
                        }
                        else
                        {
                            b += (byte)(v - 3);
                            g += (byte)(GetBits(3) - 3);
                            r += (byte)(GetBits(3) - 3);
                        }
                    }
                    else if (GetBit() != 0)
                    {
                        byte v = (byte)GetBits(4);
                        if (0xF == v)
                        {
                            b = m_output[dst - m_stride - 3];
                            g = m_output[dst - m_stride - 2];
                            r = m_output[dst - m_stride - 1];
                        }
                        else
                        {
                            b += (byte)(v - 7);
                            g += (byte)(GetBits(4) - 7);
                            r += (byte)(GetBits(4) - 7);
                        }
                    }
                    else
                    {
                        b += (byte)(GetBits(5) - 15);
                        g += (byte)(GetBits(5) - 15);
                        r += (byte)(GetBits(5) - 15);
                    }
                }
                m_output[dst++] = b;
                m_output[dst++] = g;
                m_output[dst++] = r;
            }
        }

        private void UnpackV2()
        {
            int dst = 0;
            byte b = 0, g = 0, r = 0;
            while (dst < m_output.Length)
            {
                if (GetBit() == 0)
                {
                    if (GetBit() != 0)
                    {
                        // GARbro: if (m_input.PeekByte() != -1). Without this guard, an image whose
                        // data ends on a literal pixel throws and Decode() wrongly retries as V1.
                        if (m_input.Position < m_input.Length)
                        {
                            b = m_input.ReadUInt8();
                            g = m_input.ReadUInt8();
                            r = m_input.ReadUInt8();
                        }
                    }
                    else if (GetBit() != 0)
                    {
                        byte v = (byte)GetBits(2);
                        if (3 == v)
                        {
                            b = m_output[dst - m_stride];
                            g = m_output[dst - m_stride + 1];
                            r = m_output[dst - m_stride + 2];
                        }
                        else
                        {
                            b += (byte)(v - 1);
                            v = (byte)GetBits(2);
                            if (3 == v)
                            {
                                if (GetBit() != 0)
                                {
                                    b = m_output[dst - m_stride - 3];
                                    g = m_output[dst - m_stride - 2];
                                    r = m_output[dst - m_stride - 1];
                                }
                                else
                                {
                                    b = m_output[dst - m_stride + 3];
                                    g = m_output[dst - m_stride + 4];
                                    r = m_output[dst - m_stride + 5];
                                }
                            }
                            else
                            {
                                g += (byte)(v - 1);
                                r += (byte)(GetBits(2) - 1);
                            }
                        }
                    }
                    else if (GetBit() != 0)
                    {
                        byte v = (byte)GetBits(3);
                        if (7 == v)
                        {
                            b = m_output[dst - m_stride];
                            g = m_output[dst - m_stride + 1];
                            r = m_output[dst - m_stride + 2];
                            b += (byte)(GetBits(3) - 3);
                            g += (byte)(GetBits(3) - 3);
                            r += (byte)(GetBits(3) - 3);
                        }
                        else
                        {
                            b += (byte)(v - 3);
                            g += (byte)(GetBits(3) - 3);
                            r += (byte)(GetBits(3) - 3);
                        }
                    }
                    else if (GetBit() != 0)
                    {
                        b += (byte)(GetBits(4) - 7);
                        g += (byte)(GetBits(4) - 7);
                        r += (byte)(GetBits(4) - 7);
                    }
                    else
                    {
                        b += (byte)(GetBits(5) - 15);
                        g += (byte)(GetBits(5) - 15);
                        r += (byte)(GetBits(5) - 15);
                    }
                }
                m_output[dst++] = b;
                m_output[dst++] = g;
                m_output[dst++] = r;
            }
        }
    }
}
