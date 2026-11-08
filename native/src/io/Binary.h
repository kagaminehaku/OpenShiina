// Byte-level helpers (C# IO/Utility.cs): little- and big-endian reads at any address, rotations,
// the overlapped copy of LZ back references, and the conversions of doubles to integers as .NET 9+
// does them (saturating), which C++ leaves undefined out of range.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

namespace openshiina {

using Bytes = std::vector<uint8_t>;

/// The data is not what a format says it is (C#'s InvalidDataException and the index errors it
/// would throw on a short buffer).
class InvalidDataError : public std::runtime_error
{
public:
    using std::runtime_error::runtime_error;
};

namespace LittleEndian {

inline uint16_t ToUInt16(const uint8_t* p) { return uint16_t(p[0] | p[1] << 8); }
inline uint32_t ToUInt32(const uint8_t* p) { return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24; }
inline uint64_t ToUInt64(const uint8_t* p) { return uint64_t(ToUInt32(p)) | uint64_t(ToUInt32(p + 4)) << 32; }

inline void Pack(uint32_t v, uint8_t* p)
{
    p[0] = uint8_t(v);
    p[1] = uint8_t(v >> 8);
    p[2] = uint8_t(v >> 16);
    p[3] = uint8_t(v >> 24);
}

/// The dword at p XORed with v (C#'s uint* writes on a little-endian CPU).
inline void Xor(uint8_t* p, uint32_t v) { Pack(ToUInt32(p) ^ v, p); }

/// Checked reads: past the end of the data is an InvalidDataError, as the index error is in C#.
inline uint16_t ToUInt16(const Bytes& d, size_t i)
{
    if (i + 2 > d.size()) throw InvalidDataError("Unexpected end of data");
    return ToUInt16(d.data() + i);
}
inline uint32_t ToUInt32(const Bytes& d, size_t i)
{
    if (i + 4 > d.size()) throw InvalidDataError("Unexpected end of data");
    return ToUInt32(d.data() + i);
}

} // namespace LittleEndian

namespace BigEndian {

inline uint32_t ToUInt32(const uint8_t* p) { return uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | uint32_t(p[3]); }
inline uint64_t ToUInt64(const uint8_t* p) { return uint64_t(ToUInt32(p)) << 32 | ToUInt32(p + 4); }

} // namespace BigEndian

namespace Binary {

inline uint32_t BigEndian(uint32_t u) { return u << 24 | (u & 0xff00) << 8 | (u & 0xff0000) >> 8 | u >> 24; }

inline uint8_t RotByteR(uint8_t v, int count)
{
    count &= 7;
    return uint8_t(v >> count | v << (8 - count));
}

inline uint8_t RotByteL(uint8_t v, int count)
{
    count &= 7;
    return uint8_t(v << count | v >> (8 - count));
}

inline uint32_t RotL(uint32_t v, int count)
{
    count &= 0x1f;
    return count == 0 ? v : v << count | v >> (32 - count);
}

inline uint32_t RotR(uint32_t v, int count)
{
    count &= 0x1f;
    return count == 0 ? v : v >> count | v << (32 - count);
}

/// Copies count bytes from src to dst inside data, repeating what is already copied when dst is
/// ahead of src by less than count (an LZ back reference); a reference outside data is an error.
inline void CopyOverlapped(Bytes& data, ptrdiff_t src, ptrdiff_t dst, ptrdiff_t count)
{
    if (count <= 0)
        return;
    if (src < 0 || dst < 0 || size_t(src + count) > data.size() || size_t(dst + count) > data.size())
        throw InvalidDataError("Back reference outside the data");
    if (dst > src)
    {
        while (count > 0)
        {
            ptrdiff_t preceding = std::min(dst - src, count);
            std::memmove(data.data() + dst, data.data() + src, size_t(preceding));
            dst += preceding;
            count -= preceding;
        }
    }
    else
        std::memmove(data.data() + dst, data.data() + src, size_t(count));
}

} // namespace Binary

// .NET 9+ converts floating point to integers saturating (NaN to 0); these do the same, so that the
// keys worked out with doubles (Decoder) come out as the C# player's do.
inline uint32_t SaturateToUInt32(double v)
{
    if (!(v > 0)) return 0;
    if (v >= 4294967296.0) return std::numeric_limits<uint32_t>::max();
    return uint32_t(v);
}

inline int64_t SaturateToInt64(double v)
{
    if (std::isnan(v)) return 0;
    if (v <= -9223372036854775808.0) return std::numeric_limits<int64_t>::min();
    if (v >= 9223372036854775808.0) return std::numeric_limits<int64_t>::max();
    return int64_t(v);
}

inline int32_t SaturateToInt32(double v)
{
    if (std::isnan(v)) return 0;
    if (v <= -2147483648.0) return std::numeric_limits<int32_t>::min();
    if (v >= 2147483648.0) return std::numeric_limits<int32_t>::max();
    return int32_t(v);
}

// The bytes made from doubles are of values from 0 to 255 already (a dword / 2^24): only the
// truncation matters
inline uint8_t SaturateToByte(double v)
{
    if (!(v > 0)) return 0;
    if (v >= 255.0) return 255;
    return uint8_t(v);
}

inline uint32_t SaturateToUInt32(float v) { return SaturateToUInt32(double(v)); }

} // namespace openshiina
