// What of a 32-bit x86 instruction the routines' walk needs (the C# player takes it from Iced's
// decoder): its length, how it goes on (Iced's FlowControl) and a near branch's target; invalid
// bytes as Iced finds them for the instructions the games' routines hold (integer, x87, MMX, SSE,
// SSE2). Prefixes, ModRM / SIB, displacements and immediates as the Intel manual lays them out.
#pragma once

#include <cstddef>
#include <cstdint>

namespace openshiina::scripting {

enum class X86Flow : uint8_t
{
    Next,
    UnconditionalBranch,
    IndirectBranch,
    ConditionalBranch,
    Return,
    Call,
    IndirectCall,
    Interrupt,
    Exception,
};

struct X86Decoded
{
    bool Invalid = true;
    int Length = 0;
    X86Flow Flow = X86Flow::Next;
    /// A near branch (Iced's OpKind.NearBranch32): its target.
    bool NearBranch = false;
    uint32_t Target = 0;
};

/// The instruction at ip, from up to `available` bytes of `code` (15 is enough).
X86Decoded DecodeX86(const uint8_t* code, size_t available, uint32_t ip);

} // namespace openshiina::scripting
