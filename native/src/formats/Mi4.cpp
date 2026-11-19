// C# Formats/Mi4Format.cs
#include "formats/Mi4.h"

#include <algorithm>

namespace openshiina::formats {

namespace {

// The C# reader throws when it reads past the data or the picture (and then tries the first
// version); here such a read sets Failed
class Reader
{
public:
    Reader(const uint8_t* data, size_t length, int width, int height)
        : m_data(data), m_length(length), m_stride(width * 3), m_out(size_t(m_stride) * size_t(height)) {}

    bool Failed = false;
    std::vector<uint8_t>& Output() { return m_out; }

    void Unpack(bool second)
    {
        m_pos = 0x10;
        m_bitCount = 0;
        m_bits = 0;
        Failed = false;
        std::fill(m_out.begin(), m_out.end(), uint8_t(0));
        LoadBits();
        if (second)
            UnpackV2();
        else
            UnpackV1();
    }

private:
    const uint8_t* m_data;
    size_t m_length, m_pos = 0;
    int m_stride;
    std::vector<uint8_t> m_out;
    int m_bitCount = 0;
    uint32_t m_bits = 0;

    void LoadBits()
    {
        for (int i = 0; i < 4 && m_pos < m_length; ++i)
        {
            m_bits = (m_bits >> 8) | (uint32_t(m_data[m_pos++]) << 24);
            m_bitCount += 8;
        }
    }
    uint32_t Bit()
    {
        uint32_t bit = m_bits >> 31;
        m_bits <<= 1;
        if (--m_bitCount == 0)
            LoadBits();
        return bit;
    }
    uint32_t Bits(int count)
    {
        int avail = std::min(count, m_bitCount);
        uint32_t bits = avail == 0 ? 0 : m_bits >> (32 - avail);
        m_bits = avail >= 32 ? 0 : m_bits << avail;
        m_bitCount -= avail;
        count -= avail;
        if (m_bitCount == 0)
        {
            LoadBits();
            if (count > 0)
            {
                bits = bits << count | m_bits >> (32 - count);
                m_bits <<= count;
                m_bitCount -= count;
            }
        }
        return bits;
    }
    uint8_t Byte()
    {
        if (m_pos >= m_length)
        {
            Failed = true;
            return 0;
        }
        return m_data[m_pos++];
    }
    // The pixel at dst + offset of the row above
    uint8_t Above(int dst, int offset)
    {
        long at = long(dst) - m_stride + offset;
        if (at < 0 || size_t(at) >= m_out.size())
        {
            Failed = true;
            return 0;
        }
        return m_out[size_t(at)];
    }

    void UnpackV1()
    {
        int dst = 0;
        uint8_t b = 0, g = 0, r = 0;
        while (size_t(dst) < m_out.size() && !Failed)
        {
            if (Bit() == 0)
            {
                if (Bit() != 0)
                {
                    b = Byte();
                    g = Byte();
                    r = Byte();
                }
                else if (Bit() != 0)
                {
                    uint8_t v = uint8_t(Bits(2));
                    if (v == 3)
                    {
                        b = Above(dst, 0);
                        g = Above(dst, 1);
                        r = Above(dst, 2);
                    }
                    else
                    {
                        b += uint8_t(v - 1);
                        g += uint8_t(Bits(2) - 1);
                        r += uint8_t(Bits(2) - 1);
                    }
                }
                else if (Bit() != 0)
                {
                    uint8_t v = uint8_t(Bits(3));
                    if (v == 7)
                    {
                        b = Above(dst, 3);
                        g = Above(dst, 4);
                        r = Above(dst, 5);
                    }
                    else
                    {
                        b += uint8_t(v - 3);
                        g += uint8_t(Bits(3) - 3);
                        r += uint8_t(Bits(3) - 3);
                    }
                }
                else if (Bit() != 0)
                {
                    uint8_t v = uint8_t(Bits(4));
                    if (v == 0xF)
                    {
                        b = Above(dst, -3);
                        g = Above(dst, -2);
                        r = Above(dst, -1);
                    }
                    else
                    {
                        b += uint8_t(v - 7);
                        g += uint8_t(Bits(4) - 7);
                        r += uint8_t(Bits(4) - 7);
                    }
                }
                else
                {
                    b += uint8_t(Bits(5) - 15);
                    g += uint8_t(Bits(5) - 15);
                    r += uint8_t(Bits(5) - 15);
                }
            }
            m_out[size_t(dst++)] = b;
            m_out[size_t(dst++)] = g;
            m_out[size_t(dst++)] = r;
        }
    }

    void UnpackV2()
    {
        int dst = 0;
        uint8_t b = 0, g = 0, r = 0;
        while (size_t(dst) < m_out.size() && !Failed)
        {
            if (Bit() == 0)
            {
                if (Bit() != 0)
                {
                    // as GARbro: a literal pixel past the data's end leaves the one before
                    if (m_pos < m_length)
                    {
                        b = Byte();
                        g = Byte();
                        r = Byte();
                    }
                }
                else if (Bit() != 0)
                {
                    uint8_t v = uint8_t(Bits(2));
                    if (v == 3)
                    {
                        b = Above(dst, 0);
                        g = Above(dst, 1);
                        r = Above(dst, 2);
                    }
                    else
                    {
                        b += uint8_t(v - 1);
                        v = uint8_t(Bits(2));
                        if (v == 3)
                        {
                            if (Bit() != 0)
                            {
                                b = Above(dst, -3);
                                g = Above(dst, -2);
                                r = Above(dst, -1);
                            }
                            else
                            {
                                b = Above(dst, 3);
                                g = Above(dst, 4);
                                r = Above(dst, 5);
                            }
                        }
                        else
                        {
                            g += uint8_t(v - 1);
                            r += uint8_t(Bits(2) - 1);
                        }
                    }
                }
                else if (Bit() != 0)
                {
                    uint8_t v = uint8_t(Bits(3));
                    if (v == 7)
                    {
                        b = Above(dst, 0);
                        g = Above(dst, 1);
                        r = Above(dst, 2);
                        b += uint8_t(Bits(3) - 3);
                        g += uint8_t(Bits(3) - 3);
                        r += uint8_t(Bits(3) - 3);
                    }
                    else
                    {
                        b += uint8_t(v - 3);
                        g += uint8_t(Bits(3) - 3);
                        r += uint8_t(Bits(3) - 3);
                    }
                }
                else if (Bit() != 0)
                {
                    b += uint8_t(Bits(4) - 7);
                    g += uint8_t(Bits(4) - 7);
                    r += uint8_t(Bits(4) - 7);
                }
                else
                {
                    b += uint8_t(Bits(5) - 15);
                    g += uint8_t(Bits(5) - 15);
                    r += uint8_t(Bits(5) - 15);
                }
            }
            m_out[size_t(dst++)] = b;
            m_out[size_t(dst++)] = g;
            m_out[size_t(dst++)] = r;
        }
    }
};

uint32_t Le32(const uint8_t* p) { return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24; }

} // namespace

bool Mi4::Is(const uint8_t* data, size_t length)
{
    return length >= 16 && data[0] == 'M' && data[1] == 'A' && data[2] == 'I' && data[3] == '4';
}

std::optional<Mi4Picture> Mi4::Decode(const uint8_t* data, size_t length)
{
    if (!Is(data, length))
        return std::nullopt;
    uint32_t width = Le32(data + 8), height = Le32(data + 12);
    if (width == 0 || height == 0 || width > 16384 || height > 16384)
        return std::nullopt;
    Reader reader(data, length, int(width), int(height));
    reader.Unpack(true);
    if (reader.Failed)
    {
        reader.Unpack(false);
        if (reader.Failed)
            return std::nullopt;
    }
    Mi4Picture picture;
    picture.Width = int(width);
    picture.Height = int(height);
    picture.Bgr = std::move(reader.Output());
    return picture;
}

} // namespace openshiina::formats
