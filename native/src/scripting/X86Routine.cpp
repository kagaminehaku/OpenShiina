// C# Scripting/X86Routine.cs
#include "scripting/X86Routine.h"

#include <vector>

#include "scripting/ScnVm.h"
#include "util/Encoding.h"

namespace openshiina::scripting {

namespace {

// A routine is a few thousand instructions at most; past this the code is not a routine
constexpr size_t MaxInstructions = 20000;

} // namespace

X86Routine::X86Routine(ScnVm& vm, uint32_t entry) : Entry(entry)
{
    std::vector<uint32_t> work{entry};
    uint8_t bytes[15];
    while (!work.empty() && Code.size() < MaxInstructions)
    {
        uint32_t ip = work.back();
        work.pop_back();
        while (!Code.count(ip) && Code.size() < MaxInstructions)
        {
            for (int k = 0; k < 15; k++)
                bytes[k] = vm.ReadByte(int(ip + uint32_t(k)));
            X86Decoded ins = DecodeX86(bytes, 15, ip);
            if (ins.Invalid)
            {
                Open = true;
                break;
            }
            Code[ip] = ins;
            if (ins.Flow == X86Flow::Return)
                break;
            if (ins.Flow == X86Flow::UnconditionalBranch)
            {
                if (ins.NearBranch)
                {
                    Targets.insert(ins.Target);
                    work.push_back(ins.Target);
                }
                else
                    Open = true;
                break;
            }
            if (ins.Flow == X86Flow::ConditionalBranch)
            {
                Targets.insert(ins.Target);
                work.push_back(ins.Target);
            }
            else if (ins.Flow == X86Flow::Call)
            {
                Calls = true;
                work.push_back(ins.Target);
            }
            else if (ins.Flow == X86Flow::IndirectBranch || ins.Flow == X86Flow::IndirectCall || ins.Flow == X86Flow::Interrupt
                     || ins.Flow == X86Flow::Exception)
            {
                Open = true;
                if (ins.Flow == X86Flow::IndirectBranch || ins.Flow == X86Flow::Exception)
                    break;
            }
            ip += uint32_t(ins.Length);
        }
    }
    if (!work.empty())
        Open = true;

    Encoding::Sha1 sha;
    for (const auto& [ip, ins] : Code)
    {
        uint32_t offset = ip - entry;
        uint8_t le[4] = {uint8_t(offset), uint8_t(offset >> 8), uint8_t(offset >> 16), uint8_t(offset >> 24)};
        sha.Append(le, 4);
        auto code = vm.ReadBytes(int(ip), ins.Length);
        sha.Append(code.data(), code.size());
    }
    auto digest = sha.Finish();
    Signature = Encoding::ToHex(digest.data(), digest.size());
}

bool X86Routine::Loops() const
{
    for (const auto& [ip, ins] : Code)
        if ((ins.Flow == X86Flow::ConditionalBranch || ins.Flow == X86Flow::UnconditionalBranch) && ins.NearBranch && ins.Target <= ip)
            return true;
    return false;
}

} // namespace openshiina::scripting
