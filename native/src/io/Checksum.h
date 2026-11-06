// CRC-32 (MSB first), Adler-32 and CRC-16 as the archive keys use them (C# IO/Checksum.cs).
#pragma once

#include <cstddef>
#include <cstdint>

namespace openshiina {

/// Non-reflected (MSB-first) CRC-32 with polynomial 0x04C11DB7, as in GARbro ArcFormats/Crc32.cs.
/// This is NOT the common zip/PNG CRC-32; Decoder::Decrypt2 depends on this exact variant.
namespace Crc32Normal {
uint32_t UpdateCrc(uint32_t crc, const uint8_t* data, size_t length);
uint32_t Compute(const uint8_t* data, size_t length);
}

namespace Adler32 {
uint32_t Compute(const uint8_t* data, size_t length);
}

/// Reflected CCITT CRC-16 (GARbro's Crc16), starting at 0xFFFF.
class Crc16
{
public:
    uint16_t Value() const { return m_crc; }
    void Update(const uint8_t* data, size_t length);

private:
    uint16_t m_crc = 0xFFFF;
};

} // namespace openshiina
