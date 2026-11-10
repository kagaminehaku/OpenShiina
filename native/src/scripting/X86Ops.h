// C# Scripting/X86Ops.cs, its MMX lanes: an MMX register as a uint64_t of four 16-bit words (or
// eight bytes), each operation as the instruction computes it. The pixel routines written out from
// the games' MMX code use these so that every product and sum wraps where the x86 code's does.
// (The rest of X86Ops - the integer and flag operations - comes with the x86 interpreter.)
#pragma once

#include <algorithm>
#include <cstdint>

#if defined(__aarch64__)
#include <arm_neon.h>
#elif defined(_M_X64) || defined(__x86_64__)
#include <emmintrin.h>
#endif

namespace openshiina::scripting::X86Ops {

inline uint16_t W(uint64_t v, int k) { return uint16_t(v >> (16 * k)); }

inline uint64_t Words(int w0, int w1, int w2, int w3)
{
    return uint64_t(uint16_t(w0)) | uint64_t(uint16_t(w1)) << 16 | uint64_t(uint16_t(w2)) << 32 | uint64_t(uint16_t(w3)) << 48;
}

// The top bit of each lane
constexpr uint64_t LaneTops = 0x8000'8000'8000'8000ull;

/// The lanes added in one 64-bit sum: the low 15 bits of each, then each top bit as the two tops
/// and the carry into it give it (no carry crosses a lane).
inline uint64_t Paddw(uint64_t a, uint64_t b) { return ((a & ~LaneTops) + (b & ~LaneTops)) ^ ((a ^ b) & LaneTops); }

/// The lanes taken away in one 64-bit difference (each lane's top bit set first, so that no borrow
/// crosses a lane, and put right after).
inline uint64_t Psubw(uint64_t a, uint64_t b) { return ((a | LaneTops) - (b & ~LaneTops)) ^ ((a ^ ~b) & LaneTops); }

/// The low 16 bits of each lane's product: pmullw itself on a PC, NEON's mul .4h on the Switch.
inline uint64_t Pmullw(uint64_t a, uint64_t b)
{
#if defined(__aarch64__)
    return vget_lane_u64(vreinterpret_u64_u16(vmul_u16(vreinterpret_u16_u64(vcreate_u64(a)), vreinterpret_u16_u64(vcreate_u64(b)))), 0);
#elif defined(_M_X64) || defined(__x86_64__)
    return uint64_t(_mm_cvtsi128_si64(_mm_mullo_epi16(_mm_cvtsi64_si128(int64_t(a)), _mm_cvtsi64_si128(int64_t(b)))));
#else
    // (unsigned products: 0xFFFF * 0xFFFF does not fit an int)
    return Words(int(uint32_t(W(a, 0)) * W(b, 0)), int(uint32_t(W(a, 1)) * W(b, 1)), int(uint32_t(W(a, 2)) * W(b, 2)),
                 int(uint32_t(W(a, 3)) * W(b, 3)));
#endif
}

inline uint64_t Psrlw(uint64_t a, uint64_t count)
{
    return count > 15 ? 0 : (a >> count) & (0xFFFFull >> count) * 0x0001000100010001ull;
}

inline uint64_t Psllw(uint64_t a, uint64_t count)
{
    return count > 15 ? 0 : (a << count) & ((0xFFFFull << count) & 0xFFFF) * 0x0001000100010001ull;
}

/// The low 4 bytes of v, one in the low byte of each 16-bit lane.
inline uint64_t SpreadBytes(uint64_t v)
{
    v &= 0xFFFFFFFF;
    v = (v | v << 16) & 0x0000FFFF0000FFFFull;
    return (v | v << 8) & 0x00FF00FF00FF00FFull;
}

inline uint64_t Punpcklbw(uint64_t a, uint64_t b) { return SpreadBytes(a) | SpreadBytes(b) << 8; }

/// A signed 16-bit lane saturated to an unsigned byte.
inline uint64_t SaturateByte(int16_t v) { return uint64_t(std::clamp<int>(v, 0, 255)); }

/// The four signed 16-bit lanes of v saturated to unsigned bytes.
inline uint64_t PackUnsigned(uint64_t v)
{
    // Fast path: every lane already 0-255
    if ((v & 0xFF00FF00FF00FF00ull) == 0)
    {
        v = (v | v >> 8) & 0x0000FFFF0000FFFFull;
        return (v | v >> 16) & 0xFFFFFFFF;
    }
    return SaturateByte(int16_t(v)) | SaturateByte(int16_t(v >> 16)) << 8 | SaturateByte(int16_t(v >> 32)) << 16
           | SaturateByte(int16_t(v >> 48)) << 24;
}

inline uint64_t Packuswb(uint64_t a, uint64_t b) { return PackUnsigned(a) | PackUnsigned(b) << 32; }

} // namespace openshiina::scripting::X86Ops
