// C# Scripting/ScnVm.Native.cs: machine code inside the SCN modules ("op_0276 label"). The engine
// calls x86 routines with a pointer to {b, a, s, f of the slot, the stack top}; they read their
// arguments from l[0], l[1]... and work on picture buffers in script memory. A routine runs as a
// C++ version written by hand (ScnVm.NativeRoutines.cpp, ...), found by the signature of the whole
// routine (X86Routine) or by its bytes with a constant left open, and gives the bytes the x86 code
// gives. The C# player translates the others to .NET or interprets them; here the x86 interpreter
// is not ported yet, so a routine without a C++ version stops the game with a script error.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <format>

namespace openshiina::scripting {

void ScnVm::RegisterNative(const std::string& signature, const char* name, NativeRoutine run)
{
    m_natives[signature] = NativeEntry{name, std::move(run)};
}

void ScnVm::RegisterNative(std::initializer_list<const char*> signatures, const char* name, const NativeRoutine& run)
{
    for (const char* signature : signatures)
        RegisterNative(signature, name, run);
}

std::optional<std::string> ScnVm::NativeName(const std::string& signature) const
{
    auto found = m_natives.find(signature);
    return found == m_natives.end() ? std::nullopt : std::optional<std::string>(found->second.Name);
}

ScnVm::RoutineInfo& ScnVm::RoutineAt(int address)
{
    auto& info = m_routines[address];
    if (!info)
    {
        info = std::make_unique<RoutineInfo>(*this, address);
        if (auto native = m_natives.find(info->Code.Signature); native != m_natives.end())
            info->Native = native->second;
        else
            info->Native = NativeByPattern(address);
    }
    return *info;
}

void ScnVm::ForgetRoutines()
{
    // Code was loaded again: what was known of routines no longer applies
    m_routines.clear();
}

void ScnVm::RunX86(ScnContext& c, const ScnNativeArgs&)
{
    throw Error(c, std::format("Embedded x86 routine at module offset {:05X} (signature {}) needs the x86 interpreter, not ported yet",
                               m_nativeTarget - c.Base, NativeSignature(m_nativeTarget)));
}

// START 75C3D of Oreimo (6A6ED Azu, 5D88B Ero-On!, 75ED7 Homu, 76167 Yuru, 76687 Nyaru, 76647
// Rikka, 76097 Sena, 760B7 Kuroneko): xors count dwords at l[1] with a key stream from l[0] (xor
// [ebx],eax / inc eax / rol eax,3 / bswap eax), count a constant of each game. Found by its bytes
// with the count left open, ending with "ret 4" (v2.47) or "ret"
std::optional<ScnVm::NativeEntry> ScnVm::NativeByPattern(int address)
{
    static const uint8_t head[] = {0x9C, 0x60, 0x8B, 0x6C, 0x24, 0x28, 0x83, 0xEC, 0x24, 0xFF, 0x75, 0x00, 0xFF, 0x75, 0x04, 0xFF,
                                   0x75, 0x08, 0xFF, 0x75, 0x0C, 0xFF, 0x75, 0x10, 0x8B, 0x34, 0x24, 0x8B, 0x06, 0x8B, 0x5E, 0x04, 0xB9};
    static const uint8_t tail[] = {0x31, 0x03, 0x40, 0x83, 0xC3, 0x04, 0xC1, 0xC0, 0x03, 0x0F, 0xC8, 0x49, 0x75, 0xF2, 0x83, 0xC4, 0x38, 0x61, 0x9D};
    constexpr int length = int(sizeof head) + 4 + int(sizeof tail);
    auto code = ReadBytes(address, length + 3);
    if (!std::equal(head, head + sizeof head, code.begin()) || !std::equal(tail, tail + sizeof tail, code.begin() + sizeof head + 4)
        || !(code[length] == 0xC3 || (code[length] == 0xC2 && code[length + 1] == 4 && code[length + 2] == 0)))
        return std::nullopt;
    int count = int(LittleEndian::ToUInt32(&code[sizeof head]));
    if (count <= 0 || count > 0x4000000)
        return std::nullopt;
    return NativeEntry{"xorstream", [count](ScnVm& vm, ScnContext& c, const ScnNativeArgs& a) { vm.XorStream(c, a, count); }};
}

void ScnVm::RegisterNativeCall()
{
    Register(0x0276, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int target = vm.Value(c, i.Args[0]);
        ScnNativeArgs args{vm.BAddress(0), vm.AAddress(0), vm.SAddress(0), vm.FAddress(c.Slot, 0), vm.StackAddress(c.Slot, c.Sp)};
        vm.m_nativeTarget = target;
        auto& info = vm.RoutineAt(target);
        if (info.Native)
            info.Native->Run(vm, c, args);
        else
            vm.RunX86(c, args);
        return 0;
    });
}

} // namespace openshiina::scripting
