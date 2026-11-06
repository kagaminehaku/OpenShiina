// An x86 routine embedded in an SCN module (C# Scripting/X86Routine.cs), as far as its code can be
// followed from the entry: every reachable instruction (through jumps, conditional branches and
// direct calls), the jump targets, and a signature of the whole routine. The signature (SHA-1 of
// each instruction's offset from the entry and its bytes) is what the routines' C++ versions are
// found by, so a routine that starts like a known one but differs further on is not taken for it.
#pragma once

#include <cstdint>
#include <map>
#include <set>
#include <string>

#include "scripting/X86Decoder.h"

namespace openshiina::scripting {

class ScnVm;

class X86Routine
{
public:
    X86Routine(ScnVm& vm, uint32_t entry);

    /// The entry point (an address in VM memory).
    uint32_t Entry;
    /// Reachable instructions by address.
    std::map<uint32_t, X86Decoded> Code;
    /// Addresses some branch jumps to.
    std::set<uint32_t> Targets;
    /// The code could not be followed everywhere (an indirect jump or call, invalid bytes).
    bool Open = false;
    /// The routine calls code (a direct call).
    bool Calls = false;
    /// SHA-1 of the routine, in hex.
    std::string Signature;

    /// Some branch goes back (a loop): the routine may run long.
    bool Loops() const;
};

} // namespace openshiina::scripting
