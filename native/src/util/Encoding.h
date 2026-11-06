// Hex, base64 and SHA-256: how Formats.Json keeps its keys and checks its images.
#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace openshiina::Encoding {

/// Hex digits to bytes; throws InvalidDataError on an odd length or a non-hex digit.
std::vector<uint8_t> FromHex(std::string_view hex);
std::string ToHex(const uint8_t* data, size_t length);
/// Base64 (with or without padding) to bytes; throws InvalidDataError on other characters.
std::vector<uint8_t> FromBase64(std::string_view text);

class Sha256
{
public:
    Sha256();
    void Append(const uint8_t* data, size_t length);
    std::array<uint8_t, 32> Finish();

    static std::string HexOf(const uint8_t* data, size_t length);

private:
    void Block(const uint8_t* block);

    uint32_t m_state[8];
    uint8_t m_buffer[64];
    size_t m_buffered = 0;
    uint64_t m_total = 0;
};

} // namespace openshiina::Encoding
