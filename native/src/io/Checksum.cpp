#include "io/Checksum.h"

#include <array>

namespace openshiina {

namespace {

std::array<uint32_t, 256> MakeCrc32Table()
{
    constexpr uint32_t polynomial = 0x04C11DB7;
    std::array<uint32_t, 256> table{};
    for (uint32_t n = 0; n < 256; n++)
    {
        uint32_t c = n << 24;
        for (int k = 0; k < 8; k++)
            c = (c & 0x80000000u) ? polynomial ^ (c << 1) : c << 1;
        table[n] = c;
    }
    return table;
}

std::array<uint16_t, 256> MakeCrc16Table()
{
    std::array<uint16_t, 256> table{};
    for (int i = 0; i < 256; i++)
    {
        uint16_t crc = uint16_t(i);
        for (int j = 0; j < 8; j++)
            crc = (crc & 1) ? uint16_t((crc >> 1) ^ 0x8408) : uint16_t(crc >> 1);
        table[i] = crc;
    }
    return table;
}

const std::array<uint32_t, 256> kCrc32Table = MakeCrc32Table();
const std::array<uint16_t, 256> kCrc16Table = MakeCrc16Table();

} // namespace

uint32_t Crc32Normal::UpdateCrc(uint32_t crc, const uint8_t* data, size_t length)
{
    for (size_t i = 0; i < length; i++)
        crc = kCrc32Table[(crc >> 24) ^ data[i]] ^ (crc << 8);
    return crc;
}

uint32_t Crc32Normal::Compute(const uint8_t* data, size_t length)
{
    return UpdateCrc(0xFFFFFFFF, data, length) ^ 0xFFFFFFFF;
}

uint32_t Adler32::Compute(const uint8_t* data, size_t length)
{
    uint32_t a = 1, b = 0;
    for (size_t i = 0; i < length; i++)
    {
        a = (a + data[i]) % 65521;
        b = (b + a) % 65521;
    }
    return (b << 16) | a;
}

void Crc16::Update(const uint8_t* data, size_t length)
{
    for (size_t i = 0; i < length; i++)
        m_crc = uint16_t((m_crc >> 8) ^ kCrc16Table[(m_crc ^ data[i]) & 0xFF]);
}

} // namespace openshiina
