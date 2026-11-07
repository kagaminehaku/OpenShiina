// C# Scripting/X86Ops.cs, its MMX lanes: an MMX register as a uint64_t of four 16-bit words (or
// eight bytes), each operation as the instruction computes it. The pixel routines written out from
// the games' MMX code use these so that every product and sum wraps where the x86 code's does.
// (The rest of X86Ops - the integer and flag operations - comes with the x86 interpreter.)
#pragma once

#include <algorithm>
#include <cstdint>

namespace openshiina::scripting::X86Ops {

inline uint16_t W(uint64_t v, int k) { return uint16_t(v >> (16 * k)); }

inline uint64_t Words(int w0, int w1, int w2, int w3)
{
    return uint64_t(uint16_t(w0)) | uint64_t(uint16_t(w1)) << 16 | uint64_t(uint16_t(w2)) << 32 | uint64_t(uint16_t(w3)) << 48;
}

inline uint64_t Paddw(uint64_t a, uint64_t b) { return Words(W(a, 0) + W(b, 0), W(a, 1) + W(b, 1), W(a, 2) + W(b, 2), W(a, 3) + W(b, 3)); }

inline uint64_t Psubw(uint64_t a, uint64_t b) { return Words(W(a, 0) - W(b, 0), W(a, 1) - W(b, 1), W(a, 2) - W(b, 2), W(a, 3) - W(b, 3)); }

inline uint64_t Pmullw(uint64_t a, uint64_t b)
{
    // (unsigned products: 0xFFFF * 0xFFFF does not fit an int)
    return Words(int(uint32_t(W(a, 0)) * W(b, 0)), int(uint32_t(W(a, 1)) * W(b, 1)), int(uint32_t(W(a, 2)) * W(b, 2)),
                 int(uint32_t(W(a, 3)) * W(b, 3)));
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
