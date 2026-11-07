// C# Scripting/ScnVm.Misc.cs: small system opcodes met on the way into the game: memory status,
// DirectInput keys, the background file loader, the disc check and the extra data folder.
//
// The background loader (the C# player's LoadInBackground, reading on another thread while the
// scripts go on) comes with ScnVm.ReadAhead: until then 00CE reads at once, as the C# player's tests
// run it. The disc 0A30 looks for: the C# player looks in Windows' CD / DVD drives first; the Switch
// has none, so the player always answers for a disc of its own.
#include "scripting/ScnVm.h"

#include <unordered_map>

#include "text/Text.h"

namespace openshiina::scripting {

namespace {

// Instructions looked through after 0158 for the comparison of the size it gave
constexpr int DiscCheckLookAhead = 8;

/// The player's own disc, seen as drive Q: (any letter: the scripts only put it before the names).
constexpr const char* VirtualDiscRoot = "Q:\\";

bool SameOperand(const ScnOperand& a, const ScnOperand& b)
{
    return a.Kind == b.Kind && a.Relative == b.Relative && a.AddressOf == b.AddressOf && a.Value == b.Value && a.Text == b.Text;
}

// DirectInput scan code -> virtual key, for op_03EC (main keys only)
std::unordered_map<int, int> BuildDikTable()
{
    std::unordered_map<int, int> t{
        {0x01, 0x1B}, {0x0E, 0x08}, {0x0F, 0x09}, {0x1C, 0x0D}, {0x1D, 0x11}, {0x2A, 0x10}, {0x36, 0x10}, {0x38, 0x12},
        {0x39, 0x20}, {0x9C, 0x0D}, {0x9D, 0x11}, {0xB8, 0x12}, {0xC7, 0x24}, {0xC8, 0x26}, {0xC9, 0x21}, {0xCB, 0x25},
        {0xCD, 0x27}, {0xCF, 0x23}, {0xD0, 0x28}, {0xD1, 0x22}, {0xD2, 0x2D}, {0xD3, 0x2E}, {0x47, 0x67}, {0x48, 0x68},
        {0x49, 0x69}, {0x4B, 0x64}, {0x4C, 0x65}, {0x4D, 0x66}, {0x4F, 0x61}, {0x50, 0x62}, {0x51, 0x63}, {0x52, 0x60},
    };
    // digits 1-9, 0 and the letter rows
    for (int k = 0; k < 9; k++)
        t[0x02 + k] = '1' + k;
    t[0x0B] = '0';
    const std::pair<int, const char*> rows[] = {{0x10, "QWERTYUIOP"}, {0x1E, "ASDFGHJKL"}, {0x2C, "ZXCVBNM"}};
    for (const auto& [start, letters] : rows)
        for (int k = 0; letters[k] != 0; k++)
            t[start + k] = letters[k];
    for (int k = 0; k < 10; k++)
        t[0x3B + k] = 0x70 + k;      // F1-F10
    t[0x57] = 0x7A;
    t[0x58] = 0x7B;
    return t;
}

const std::unordered_map<int, int> kDikToVk = BuildDikTable();

} // namespace

std::optional<int> ScnVm::VirtualDiscSize(ScnContext& c, const ScnInstruction& i, const std::string& path)
{
    // The size 0158 gives a file on the player's own disc, whatever its name: the number the scripts
    // then compare the size with, so that the check holds (Can Fes!'s START: "0158 path, l[0], l[1]",
    // then "if l[0] == 1744830464"). The next few instructions are looked through, up to a jump, for
    // an if (01F4 unsigned, 01FE signed) between a variable 0158 set and a number: == >= <= give the
    // number, > one more, < one less, != the number too (a script asks again when the size is not the
    // one it wants), "&" the number, "|" 1. Without one, 1: the file is there. None when the path is
    // not on that disc.
    if (!m_disc || !m_disc->second || !SjisStartsWithIgnoreCase(path, m_disc->first))
        return std::nullopt;
    auto sets = [&](const ScnOperand& operand) {
        for (size_t k = 1; k < i.Args.size(); k++)
            if (SameOperand(i.Args[k], operand))
                return true;
        return false;
    };
    int at = i.Address + i.Length;
    for (int n = 0; n < DiscCheckLookAhead; n++)
    {
        std::unique_ptr<ScnInstruction> transient;
        const ScnInstruction* next;
        try
        {
            next = &Decode(c, at, transient);
        }
        catch (const ScnException&)
        {
            // data after the code
            break;
        }
        at += next->Length;
        if ((next->Op == 0x01F4 || next->Op == 0x01FE) && next->Args.size() >= 2)
        {
            bool left = sets(next->Args[0]), right = sets(next->Args[1]);
            if (left == right)
                continue;
            int number = Value(c, next->Args[left ? 1 : 0]);
            // the comparison as "size cmp number"
            int raw = next->Raw[0];
            int cmp = left ? raw : raw == 2 ? 4 : raw == 3 ? 5 : raw == 4 ? 2 : raw == 5 ? 3 : raw;
            return cmp == 3 ? number + 1 : cmp == 5 ? number - 1 : cmp == 7 ? 1 : number;
        }
        // a jump, call or end: the check goes on elsewhere
        if (!next->Targets.empty() || next->Op == 0x0000 || next->Op == 0x0258 || next->Op == 0x0267 || next->Op == 0x026D)
            break;
    }
    return 1;
}

void ScnVm::Loaded(const openshiina::Bytes& data)
{
    m_loaded = AllocateCopy(data);
    m_fileSizes[m_loaded] = int(data.size());
    EngineGlobals[0x4C4514] = int(data.size());
}

void ScnVm::RegisterMisc()
{
    // 02C1 n, v: field n of GlobalMemoryStatus (3 = available physical memory); a current PC has
    // more than the 2 GB the call reports
    Register(0x02C1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        vm.Store(c, i.Args[1], n == 0 ? 0x20 : n == 1 ? 50 : 0x7FFFFFFF);
        return 0;
    });
    // 03EC key, v: a DirectInput key (scan code) with key repeat, 0 when the window is inactive
    Register(0x03EC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int key = vm.Value(c, i.Args[0]);
        if (!vm.m_host.Active())
        {
            vm.Store(c, i.Args[1], 0);
            return 0;
        }
        auto vk = kDikToVk.find(key & 0xFF);
        int held = vk != kDikToVk.end() && vm.KeyHeld(vk->second) ? 0x80 : 0;
        vm.Store(c, i.Args[1], vm.RepeatButtons(key, held));
        return 0;
    });
    // 00CE file: load a file (the background loader, here at once); 00CF v: still loading (0 when
    // done); 00D0 v: the data; 00D1: wait for the loader. (The C# player's "Play from here" puts
    // its scenario in place of the one asked for here: that comes with ScnVm.Warp.)
    Register(0x00CE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string file = vm.ReadString(vm.Value(c, i.Args[0]));
        openshiina::Bytes data;
        if (!vm.ReadScriptFile(file, data))
            return 2;
        vm.Loaded(data);
        return 0;
    });
    Register(0x00CF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], 0);
        return 0;
    });
    Register(0x00D0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.m_loaded);
        return 0;
    });
    Register(0x00D1, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
    // 06C3: release the movie player's DirectShow objects
    Register(0x06C3, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
    // 0140 program, arguments (Sena Plus 0x429D00, FUN_00406C60): CreateProcess of a program of the
    // game folder with its command line, shown normally; a script error when it does not start.
    // Aneiro's START has one for "krkrlt.exe", which its folder does not have. The player starts no
    // programs: the scripts go on as after one that started
    Register(0x0140, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        vm.Value(c, i.Args[1]);
        return 0;
    });
    // 0A30 file, dest: "X:\" of the CD / DVD drive holding the file, or "" (none). Can Fes!'s START
    // asks for its disc so (canfes.bin, its size read with 0158 and compared with a number) until
    // there is one, and reads nothing from it (every file is installed). The player answers for it:
    // a drive of its own, whose files are the size the scripts check for (VirtualDiscSize)
    Register(0x0A30, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.ReadString(vm.Value(c, i.Args[0]));
        vm.m_disc = std::pair<std::string, bool>(VirtualDiscRoot, true);
        vm.WriteString(vm.Value(c, i.Args[1]), vm.m_disc->first);
        return 0;
    });
    // 0A31 path: an extra folder files are looked for in; "" for none. Can Fes!'s START gives the
    // disc 0A30 found, once it has checked it: every file of the game is installed, so none is
    // looked for there
    Register(0x0A31, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string path = vm.ReadString(vm.Value(c, i.Args[0]));
        if (!path.empty() && !(vm.m_disc && SjisEqualsIgnoreCase(path, vm.m_disc->first)))
            throw vm.Error(c, "An extra data folder (" + Text::Cp932ToUtf8(path) + ") is not supported");
        return 0;
    });
}

} // namespace openshiina::scripting
