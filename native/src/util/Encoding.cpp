#include "util/Encoding.h"

#include <bit>
#include <cstring>

#include "io/Binary.h"

namespace openshiina::Encoding {

namespace {

int HexDigit(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

int Base64Digit(char c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

constexpr uint32_t kRound[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
};

} // namespace

std::vector<uint8_t> FromHex(std::string_view hex)
{
    if (hex.size() % 2 != 0)
        throw InvalidDataError("Hex text of an odd length");
    std::vector<uint8_t> out(hex.size() / 2);
    for (size_t i = 0; i < out.size(); i++)
    {
        int hi = HexDigit(hex[2 * i]), lo = HexDigit(hex[2 * i + 1]);
        if (hi < 0 || lo < 0)
            throw InvalidDataError("Not a hex digit");
        out[i] = uint8_t(hi << 4 | lo);
    }
    return out;
}

std::string ToHex(const uint8_t* data, size_t length)
{
    static const char digits[] = "0123456789ABCDEF";
    std::string out(length * 2, '0');
    for (size_t i = 0; i < length; i++)
    {
        out[2 * i] = digits[data[i] >> 4];
        out[2 * i + 1] = digits[data[i] & 15];
    }
    return out;
}

std::vector<uint8_t> FromBase64(std::string_view text)
{
    std::vector<uint8_t> out;
    out.reserve(text.size() / 4 * 3);
    uint32_t bits = 0;
    int count = 0;
    for (char c : text)
    {
        if (c == '=')
            break;
        if (c == '\r' || c == '\n' || c == ' ')
            continue;
        int v = Base64Digit(c);
        if (v < 0)
            throw InvalidDataError("Not a base64 character");
        bits = bits << 6 | uint32_t(v);
        if (++count == 4)
        {
            out.push_back(uint8_t(bits >> 16));
            out.push_back(uint8_t(bits >> 8));
            out.push_back(uint8_t(bits));
            bits = 0;
            count = 0;
        }
    }
    if (count == 2)
        out.push_back(uint8_t(bits >> 4));
    else if (count == 3)
    {
        out.push_back(uint8_t(bits >> 10));
        out.push_back(uint8_t(bits >> 2));
    }
    else if (count == 1)
        throw InvalidDataError("Base64 text cut short");
    return out;
}

Sha256::Sha256()
    : m_state{0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19}
{
}

void Sha256::Block(const uint8_t* block)
{
    uint32_t w[64];
    for (int i = 0; i < 16; i++)
        w[i] = BigEndian::ToUInt32(block + 4 * i);
    for (int i = 16; i < 64; i++)
    {
        uint32_t s0 = Binary::RotR(w[i - 15], 7) ^ Binary::RotR(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = Binary::RotR(w[i - 2], 17) ^ Binary::RotR(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    uint32_t a = m_state[0], b = m_state[1], c = m_state[2], d = m_state[3];
    uint32_t e = m_state[4], f = m_state[5], g = m_state[6], h = m_state[7];
    for (int i = 0; i < 64; i++)
    {
        uint32_t s1 = Binary::RotR(e, 6) ^ Binary::RotR(e, 11) ^ Binary::RotR(e, 25);
        uint32_t ch = (e & f) ^ (~e & g);
        uint32_t t1 = h + s1 + ch + kRound[i] + w[i];
        uint32_t s0 = Binary::RotR(a, 2) ^ Binary::RotR(a, 13) ^ Binary::RotR(a, 22);
        uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
        uint32_t t2 = s0 + maj;
        h = g;
        g = f;
        f = e;
        e = d + t1;
        d = c;
        c = b;
        b = a;
        a = t1 + t2;
    }
    m_state[0] += a; m_state[1] += b; m_state[2] += c; m_state[3] += d;
    m_state[4] += e; m_state[5] += f; m_state[6] += g; m_state[7] += h;
}

void Sha256::Append(const uint8_t* data, size_t length)
{
    m_total += length;
    while (length > 0)
    {
        if (m_buffered == 0 && length >= 64)
        {
            Block(data);
            data += 64;
            length -= 64;
            continue;
        }
        size_t n = std::min(length, 64 - m_buffered);
        std::memcpy(m_buffer + m_buffered, data, n);
        m_buffered += n;
        data += n;
        length -= n;
        if (m_buffered == 64)
        {
            Block(m_buffer);
            m_buffered = 0;
        }
    }
}

std::array<uint8_t, 32> Sha256::Finish()
{
    uint64_t bits = m_total * 8;
    uint8_t pad[72] = {0x80};
    size_t padLength = (m_buffered < 56 ? 56 : 120) - m_buffered;
    for (int i = 0; i < 8; i++)
        pad[padLength + i] = uint8_t(bits >> (56 - 8 * i));
    Append(pad, padLength + 8);
    std::array<uint8_t, 32> digest{};
    for (int i = 0; i < 8; i++)
        for (int j = 0; j < 4; j++)
            digest[4 * i + j] = uint8_t(m_state[i] >> (24 - 8 * j));
    return digest;
}

std::string Sha256::HexOf(const uint8_t* data, size_t length)
{
    Sha256 hash;
    hash.Append(data, length);
    auto digest = hash.Finish();
    return ToHex(digest.data(), digest.size());
}

Sha1::Sha1() : m_state{0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0} {}

void Sha1::Block(const uint8_t* block)
{
    uint32_t w[80];
    for (int i = 0; i < 16; i++)
        w[i] = uint32_t(block[4 * i]) << 24 | uint32_t(block[4 * i + 1]) << 16 | uint32_t(block[4 * i + 2]) << 8 | block[4 * i + 3];
    for (int i = 16; i < 80; i++)
        w[i] = std::rotl(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
    uint32_t a = m_state[0], b = m_state[1], c = m_state[2], d = m_state[3], e = m_state[4];
    for (int i = 0; i < 80; i++)
    {
        uint32_t f, k;
        if (i < 20)
        {
            f = (b & c) | (~b & d);
            k = 0x5A827999;
        }
        else if (i < 40)
        {
            f = b ^ c ^ d;
            k = 0x6ED9EBA1;
        }
        else if (i < 60)
        {
            f = (b & c) | (b & d) | (c & d);
            k = 0x8F1BBCDC;
        }
        else
        {
            f = b ^ c ^ d;
            k = 0xCA62C1D6;
        }
        uint32_t t = std::rotl(a, 5) + f + e + k + w[i];
        e = d;
        d = c;
        c = std::rotl(b, 30);
        b = a;
        a = t;
    }
    m_state[0] += a;
    m_state[1] += b;
    m_state[2] += c;
    m_state[3] += d;
    m_state[4] += e;
}

void Sha1::Append(const uint8_t* data, size_t length)
{
    m_total += length;
    while (length > 0)
    {
        size_t n = std::min(length, 64 - m_buffered);
        std::memcpy(m_buffer + m_buffered, data, n);
        m_buffered += n;
        data += n;
        length -= n;
        if (m_buffered == 64)
        {
            Block(m_buffer);
            m_buffered = 0;
        }
    }
}

std::array<uint8_t, 20> Sha1::Finish()
{
    uint64_t bits = m_total * 8;
    uint8_t pad[72] = {0x80};
    size_t padLength = (m_buffered < 56 ? 56 : 120) - m_buffered;
    for (int i = 0; i < 8; i++)
        pad[padLength + i] = uint8_t(bits >> (56 - 8 * i));
    Append(pad, padLength + 8);
    std::array<uint8_t, 20> digest{};
    for (int i = 0; i < 5; i++)
        for (int j = 0; j < 4; j++)
            digest[4 * i + j] = uint8_t(m_state[i] >> (24 - 8 * j));
    return digest;
}

} // namespace openshiina::Encoding
