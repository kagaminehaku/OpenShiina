// C# Scripting/ScnVm.Core.cs: the interpreter's own opcodes - flow, calls between slots, tasks,
// variables, arithmetic, memory and strings, time and random numbers - each as its handler in the
// executable does it. Opcode numbers in the comments are the engine's.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <bit>
#include <chrono>
#include <ctime>
#include <format>
#include <stdexcept>

namespace openshiina::scripting {

namespace {

float AsFloat(int bits) { return std::bit_cast<float>(bits); }
int FloatBits(float value) { return std::bit_cast<int>(value); }

/// .NET's int / and % (OverflowException for int.MinValue / -1).
int Divide(int v, int d)
{
    if (v == int(0x80000000) && d == -1)
        throw std::overflow_error("Arithmetic operation resulted in an overflow.");
    return v / d;
}

int Remainder(int v, int d)
{
    if (v == int(0x80000000) && d == -1)
        throw std::overflow_error("Arithmetic operation resulted in an overflow.");
    return v % d;
}

int Abs(int v)
{
    if (v == int(0x80000000))
        throw std::overflow_error("Negating the minimum value of a twos complement number is invalid.");
    return v < 0 ? -v : v;
}

} // namespace

// ---- Named variables ----

// Per slot: scopes of "local" declarations, innermost last. Globals ("global", or a name used
// before any declaration) are shared. Blocks of scopes that were left, by size, are used again
// (zeroed) by later declarations.

int ScnVm::AllocateNamed(int count)
{
    int size = 4 * std::max(1, count);
    if (auto free = m_namedFree.find(size); free != m_namedFree.end() && !free->second.empty())
    {
        int block = free->second.back();
        free->second.pop_back();
        FillMemory(block, size, 0);
        return block;
    }
    int at = m_namedTop;
    m_namedTop += size;
    if (m_namedTop > NamedRegion + 0x01000000)
        throw ScnException("Named variables fill their memory", -1, 0, 0);
    return at;
}

void ScnVm::ReleaseScope(Scope& scope)
{
    scope.ForEach([this](const std::string&, const std::pair<int, int>& variable) {
        m_namedFree[4 * std::max(1, variable.second)].push_back(variable.first);
    });
}

void ScnVm::LeaveScopes(int slot, int n)
{
    auto& scopes = m_scopes[size_t(slot)];
    int drop = n < 0 ? int(scopes.size()) : std::min(n, int(scopes.size()));
    for (int k = 0; k < drop; k++)
    {
        auto scope = std::move(scopes.back());
        scopes.pop_back();
        ReleaseScope(*scope);
        scope->Clear();
        m_freeScopes.push_back(std::move(scope));
    }
}

std::pair<std::string, int> ScnVm::Declared(ScnContext& c, const std::string& text)
{
    size_t open = SjisFind(text, '[');
    if (open == std::string::npos)
        return {SjisTrim(text), 1};
    size_t close = SjisFindLast(text, ']');
    std::string size = text.substr(open + 1, (close != std::string::npos && close > open ? close : text.size()) - open - 1);
    int n;
    if (!NetTryParseInt(size, n))
        n = Calculate(c, size);
    return {SjisTrim(text.substr(0, open)), std::max(1, n)};
}

int ScnVm::NamedAddress(ScnContext& c, const std::string& text)
{
    std::string name = text;
    int index = 0;
    size_t open = SjisFind(text, '[');
    if (open != std::string::npos)
    {
        name = SjisTrim(text.substr(0, open));
        size_t close = SjisFindLast(text, ']');
        index = Calculate(c, text.substr(open + 1, (close != std::string::npos && close > open ? close : text.size()) - open - 1));
    }
    auto& scopes = m_scopes[size_t(c.Slot)];
    for (size_t i = scopes.size(); i-- > 0;)
        if (const auto* local = scopes[i]->Find(name))
            return local->first + 4 * index;
    auto global = m_globals.find(name);
    if (global == m_globals.end())
        global = m_globals.emplace(name, std::pair(AllocateNamed(1), 1)).first;
    return global->second.first + 4 * index;
}

template <typename F> int ScnVm::Binary(ScnContext& c, const ScnInstruction& i, F f)
{
    int a = Value(c, i.Args[0]), b = Value(c, i.Args[1]);
    Store(c, i.Args[1], f(a, b));
    return 0;
}

void ScnVm::RegisterCore()
{
    // ---- Tasks and modules ----

    // 0000 end v: the task stops; a value quits the game
    Register(0x0000, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        c.Flags = 0;
        vm.TasksChanged();
        c.ExitCode = vm.Value(c, i.Args[0]);
        return c.ExitCode != 0 ? 1 : 0;
    });
    // 0001 loadmod slot, file: load and run
    Register(0x0001, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        std::string file = vm.ReadString(vm.Value(c, i.Args[1]));
        if (slot < 0 || slot >= Slots || !vm.LoadModule(slot, file, true))
            throw vm.Error(c, std::format("loadmod {}, \"{}\" failed", slot, file));
        vm.LeaveScopes(slot, -1);
        // "Play from here": the title gone past (ScnVm.Warp.cs)
        vm.PlayFromModule(vm.m_slots[size_t(slot)], file);
        return 0;
    });
    // 0002 slot, file: load without running
    Register(0x0002, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        std::string file = vm.ReadString(vm.Value(c, i.Args[1]));
        if (slot < 0 || slot >= Slots || !vm.LoadModule(slot, file, false))
            throw vm.Error(c, std::format("load {}, \"{}\" failed", slot, file));
        return 0;
    });
    // 0009 slot: reset the slot's stack and named variables
    Register(0x0009, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (slot >= 0 && slot < Slots)
        {
            vm.m_slots[size_t(slot)].Sp = StackSize;
            vm.LeaveScopes(slot, -1);
        }
        return 0;
    });
    // 000C id, label: slot id runs the label of this module (high bit: an absolute address)
    Register(0x000C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int raw = vm.Value(c, i.Args[0]);
        int id = raw & 0x7FFFFFFF;
        if (id > 1000)
            return 2;
        int target = vm.Value(c, i.Args[1]);
        auto& s = vm.m_slots[size_t(id)];
        s.Loaded = raw < 0;
        s.Sp = StackSize;
        s.Flags = 0;
        vm.TasksChanged();
        s.Entry = s.Pc = target;
        if (raw < 0)
        {
            s.CodeBase = s.Base = target;
            s.Parent = -1;
        }
        else
        {
            s.CodeBase = s.Base = c.Base;
            s.Parent = c.Slot;
        }
        return 0;
    });
    // 000D slot: start the slot as a task from its entry
    Register(0x000D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (slot < 0 || slot > 1000)
            return 2;
        auto& s = vm.m_slots[size_t(slot)];
        s.Pc = s.Entry;
        s.Sp = StackSize;
        s.Flags = 1;
        vm.TasksChanged();
        return 0;
    });
    // 0016 thread, slot, priority: the slot runs from its entry on a thread of its own until its
    // `end`. Here it is a task like the others: the scripts that start one wait for it with 0012
    Register(0x0016, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        int slot = vm.Value(c, i.Args[1]);
        vm.Value(c, i.Args[2]);
        if (slot < 0 || slot > 1000)
            return 2;
        auto& s = vm.m_slots[size_t(slot)];
        s.Pc = s.Entry;
        s.Sp = StackSize;
        s.Flags = 1;
        vm.TasksChanged();
        return 0;
    });
    // 0012 slot, v: v = the slot's flags (1 while its thread runs, 0 after its end)
    Register(0x0012, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (slot < 0 || slot > 1000)
            return 2;
        vm.Store(c, i.Args[1], vm.m_slots[size_t(slot)].Flags);
        return 0;
    });
    // 000F slot (FUN_00415460): the slot stops and lets its module go: no code, flags 0
    Register(0x000F, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (slot < 0 || slot > 1000)
            return 2;
        auto& s = vm.m_slots[size_t(slot)];
        s.Flags = 0;
        s.Loaded = false;
        vm.TasksChanged();
        return 0;
    });
    // 000E slot / 0011 slot (v2.37): the slot's flags 0 (it stops where it is) / 1 (it goes on from
    // there); 0011 on a slot no label was given is an error
    Register(0x000E, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (slot < 0 || slot > 256)
            return 2;
        vm.m_slots[size_t(slot)].Flags = 0;
        vm.TasksChanged();
        return 0;
    });
    Register(0x0011, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        if (slot < 0 || slot > 256)
            return 2;
        auto& s = vm.m_slots[size_t(slot)];
        if (s.CodeBase == 0)
            return 2;
        s.Flags = 1;
        vm.TasksChanged();
        return 0;
    });
    // 001E / 001F: set / clear task flag 4
    Register(0x001E, [](ScnVm&, ScnContext& c, const ScnInstruction&) { c.Flags |= 4; return 0; });
    Register(0x001F, [](ScnVm&, ScnContext& c, const ScnInstruction&) { c.Flags &= ~4; return 0; });
    // 000A n: the most tasks the engine runs, which the scripts do not read back
    Register(0x000A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Value(c, i.Args[0]); return 0; });
    // D382 / AA82: switches set to their own opcode number; AA82 lets the scripts write files
    Register(0xD382, [](ScnVm& vm, ScnContext&, const ScnInstruction&) { vm.SwitchD382 = 0xD382; return 0; });
    Register(0xAA82, [](ScnVm& vm, ScnContext&, const ScnInstruction&) { vm.FileWritesEnabled = true; return 0; });
    // 0048: two engine globals take the opcode number, which is in EAX there
    Register(0x0048, [](ScnVm& vm, ScnContext&, const ScnInstruction&) { vm.Global13B5198 = vm.Global13B519C = 0x48; return 0; });
    // 0034: wait for the next frame (the scheduler yields after it)
    Register(0x0034, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
    // 0032 / 0033: stop / start yielding after every instruction
    Register(0x0032, [](ScnVm& vm, ScnContext&, const ScnInstruction&) { vm.YieldEveryInstruction = false; return 0; });
    Register(0x0033, [](ScnVm& vm, ScnContext&, const ScnInstruction&) { vm.YieldEveryInstruction = true; return 0; });
    // 02EE mode: push a flag - 0 the task's flag 4, 1 0033's yield flag, any other a 0
    Register(0x02EE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int mode = vm.Value(c, i.Args[0]);
        vm.Push(c, mode == 0 ? c.Flags & 4 : mode == 1 ? (vm.YieldEveryInstruction ? 1 : 0) : 0);
        return 0;
    });
    // 02EF mode: pop what 02EE pushed and put it back - 0 ORed into the task's flags, 1 the yield
    // flag, any other dropped
    Register(0x02EF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int saved = vm.Pop(c);
        switch (vm.Value(c, i.Args[0]))
        {
        case 0:
            c.Flags |= saved;
            break;
        case 1:
            vm.YieldEveryInstruction = saved != 0;
            break;
        }
        return 0;
    });

    // ---- Branches ----

    // 01F4 / 01FE / 01FF if a cmp b else goto L: unsigned / signed / float comparison
    Register(0x01F4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.If(c, i, 0); });
    Register(0x01FE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.If(c, i, 1); });
    Register(0x01FF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.If(c, i, 2); });
    Register(0x0258, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { c.Pc = vm.Value(c, i.Args[0]); return 0; });
    Register(0x0259, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int index = vm.Value(c, i.Args[0]);
        c.Pc = c.Base + (index >= 0 && index < int(i.Targets.size()) ? i.Targets[size_t(index)] : i.Raw[0]);
        return 0;
    });
    Register(0x0208, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.m_caseValue = vm.Value(c, i.Args[0]); return 0; });
    Register(0x0209, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        bool hit = std::find(i.CaseValues.begin(), i.CaseValues.end(), vm.m_caseValue) != i.CaseValues.end();
        c.Pc = c.Base + (hit ? i.Raw[0] : i.Raw[1]);
        return 0;
    });
    // 0212 offset, n: the loop counter (u32 at module + offset - 8) = n
    Register(0x0212, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Write32(c.Base + i.Raw[0] - 8, vm.Value(c, i.Args[0]));
        return 0;
    });
    // 0213 counter, target: count down; jump back while it is not 0
    Register(0x0213, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int counter = i.Address + 2;
        int n = vm.Read32(counter) - 1;
        vm.Write32(counter, n);
        if (n != 0)
            c.Pc = c.Base + vm.Read32(counter + 4);
        return 0;
    });

    // ---- Calls ----

    // 0262 label: call a label of this module
    Register(0x0262, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int target = vm.Value(c, i.Args[0]);
        vm.Push(c, c.Base);
        vm.Push(c, c.Pc);
        c.Pc = target;
        return 0;
    });
    // 0267 slot: run a slot's code (gosub)
    Register(0x0267, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto& s = vm.m_slots.at(size_t(vm.Value(c, i.Args[0])));
        vm.Push(c, c.Base);
        vm.Push(c, c.Pc);
        c.Base = s.CodeBase;
        c.Pc = s.Entry;
        return 0;
    });
    Register(0x026C, [](ScnVm& vm, ScnContext& c, const ScnInstruction&) {
        c.Pc = vm.Pop(c);
        c.Base = vm.Pop(c);
        return 0;
    });
    // 026D n: return and drop n stack values
    Register(0x026D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int pc = vm.Pop(c), baseAddress = vm.Pop(c);
        c.Sp += vm.Value(c, i.Args[0]);
        c.Pc = pc;
        c.Base = baseAddress;
        return 0;
    });
    // 0283 result, slot, #n args / 0281 result, label, #n args: call with arguments
    Register(0x0283, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.CallWithArguments(c, i, true); });
    Register(0x0281, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.CallWithArguments(c, i, false); });
    // 0280 #n vars: the called code takes its arguments
    Register(0x0280, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (size_t k = 0; k < i.Args.size(); k++)
            vm.Store(c, i.Args[k], c.Frames[c.FrameTop - int(k) - 2]);
        return 0;
    });
    // 0285 v: return v to the caller's result variable
    Register(0x0285, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int count = c.FrameTop > 0 ? c.Frames[c.FrameTop - 1] : 0;
        c.FrameTop = std::max(0, c.FrameTop - count - 2);
        int result = c.Frames[c.FrameTop];
        int value = vm.Value(c, i.Args[0]);
        if (result != 0)
            vm.Write32(result, value);
        c.Pc = vm.Pop(c);
        c.Base = vm.Pop(c);
        return 0;
    });

    // ---- Stack and declarations ----

    Register(0x02F8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Push(c, vm.Value(c, i.Args[0])); return 0; });
    Register(0x02F9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.Pop(c)); return 0; });
    Register(0x0316, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { c.Sp -= vm.Value(c, i.Args[0]); return 0; });
    Register(0x0317, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { c.Sp += vm.Value(c, i.Args[0]); return 0; });
    // 03CF local #n names: a new scope; #0 leaves the innermost one
    Register(0x03CF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto& scopes = vm.m_scopes[size_t(c.Slot)];
        if (i.Args.empty())
        {
            vm.LeaveScopes(c.Slot, 1);
            return 0;
        }
        std::unique_ptr<Scope> scope;
        if (!vm.m_freeScopes.empty())
        {
            scope = std::move(vm.m_freeScopes.back());
            vm.m_freeScopes.pop_back();
        }
        else
            scope = std::make_unique<Scope>();
        for (const auto& name : i.Args)
        {
            auto [n, count] = vm.Declared(c, name.Text);
            (*scope)[n] = {vm.AllocateNamed(count), count};
        }
        scopes.push_back(std::move(scope));
        return 0;
    });
    // 03CE global #n names
    Register(0x03CE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (const auto& name : i.Args)
        {
            auto [n, count] = vm.Declared(c, name.Text);
            if (!vm.m_globals.count(n))
                vm.m_globals[n] = {vm.AllocateNamed(count), count};
        }
        return 0;
    });
    // 03D0 slot, n: leave n scopes (-1 = all) of a slot (-1 = every slot)
    Register(0x03D0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]), n = vm.Value(c, i.Args[1]);
        int from = slot == -1 ? 0 : slot, to = slot == -1 ? Slots : slot + 1;
        for (int s = from; s < to && s < Slots; s++)
            vm.LeaveScopes(s, n == -1 ? -1 : n);
        return 0;
    });

    // ---- Arithmetic (second operand is the destination) ----

    Register(0x038E, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[1], vm.Value(c, i.Args[0])); return 0; });
    Register(0x038F, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[1], vm.AddressOf(c, i.Args[0])); return 0; });
    Register(0x0391, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.Value(c, i.Args[0]) + 1); return 0; });
    Register(0x0392, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.Value(c, i.Args[0]) - 1); return 0; });
    Register(0x0393, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.Binary(c, i, [](int a, int b) { return b + a; }); });
    Register(0x0394, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.Binary(c, i, [](int a, int b) { return b - a; }); });
    Register(0x0396, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.Binary(c, i, [](int a, int b) { return b & a; }); });
    Register(0x0397, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.Binary(c, i, [](int a, int b) { return b | a; }); });
    Register(0x0398, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.Binary(c, i, [](int a, int b) { return b ^ a; }); });
    Register(0x039A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.Binary(c, i, [](int a, int b) { return int(uint32_t(b) >> (a & 31)); });
    });
    Register(0x039B, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.Binary(c, i, [](int a, int b) { return int(uint32_t(b) << (a & 31)); });
    });
    Register(0x039D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.Binary(c, i, [](int a, int b) { return int(std::rotl(uint32_t(b), a & 31)); });
    });
    // 039C n, v: v rotated right n bits (VANQ.EXE 0x416EE0, a bit at a time)
    Register(0x039C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.Binary(c, i, [](int a, int b) { return int(std::rotr(uint32_t(b), a & 31)); });
    });
    Register(0x039E, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.Binary(c, i, [](int a, int b) { return int(uint32_t(b) * uint32_t(a)); });
    });
    Register(0x0399, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], int(0u - uint32_t(vm.Value(c, i.Args[0]))));
        return 0;
    });
    // 03A0 divisor, value, remainder: value /= divisor
    Register(0x03A0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int d = vm.Value(c, i.Args[0]), v = vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[1], d == 0 ? 0 : Divide(v, d));
        vm.Store(c, i.Args[2], d == 0 ? 0 : Remainder(v, d));
        return 0;
    });
    // 03DE eval "expression", mode, result: mode 0 stores the value as an int, 1 as the bits of a float
    Register(0x03DE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        double value = vm.CalculateDouble(c, vm.ReadString(vm.Value(c, i.Args[0])));
        vm.Store(c, i.Args[1], i.Raw[0] == 1 ? FloatBits(float(value)) : ToInt(value));
        return 0;
    });
    // 03DF v (KATEI.EXE 0x4123F0): v, a float, made a whole number (truncated, as ftol does);
    // 03E0 v (0x4123A0): v, a whole number, made a float
    Register(0x03DF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], ToInt(AsFloat(vm.Value(c, i.Args[0]))));
        return 0;
    });
    Register(0x03E0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], FloatBits(float(vm.Value(c, i.Args[0]))));
        return 0;
    });
    // 03AC n -> v: rand() % n
    Register(0x03AC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        vm.Store(c, i.Args[1], n == 0 ? 0 : int(uint32_t(vm.Rand()) % uint32_t(n)));
        return 0;
    });
    // 03AE seed: srand (the seed kept at 0x7238C8 for 03AD); 03AD v: v = that seed
    Register(0x03AE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.m_seedGiven = vm.Value(c, i.Args[0]);
        vm.SeedRand(vm.m_seedGiven);
        return 0;
    });
    Register(0x03AD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.m_seedGiven); return 0; });
    // 03A2 v: v = |v| (aoj.EXE 0x417290)
    Register(0x03A2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], Abs(vm.Value(c, i.Args[0]))); return 0; });
    // 03A3 s, v: v = atoi(s) (white space, a sign, the digits)
    Register(0x03A3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string s = vm.ReadString(vm.Value(c, i.Args[0]));
        size_t k = s.find_first_not_of(" \t\n\r\v\f");
        if (k == std::string::npos)
            k = s.size();
        int sign = 1;
        if (k < s.size() && (s[k] == '-' || s[k] == '+'))
        {
            sign = s[k] == '-' ? -1 : 1;
            k++;
        }
        uint32_t value = 0;
        for (; k < s.size() && s[k] >= '0' && s[k] <= '9'; k++)
            value = value * 10 + uint32_t(s[k] - '0');
        vm.Store(c, i.Args[1], int(value * uint32_t(sign)));
        return 0;
    });
    // 03A4 n, s: wsprintf(s, "%d", n) (0x417330)
    Register(0x03A4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        vm.WriteString(vm.Value(c, i.Args[1]), std::to_string(n));
        return 0;
    });
    // 03A5 v: v = v - the task's module base (an address as an offset, 0x417380)
    Register(0x03A5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.Value(c, i.Args[0]) - c.Base); return 0; });
    // 03BD v: the time in ms. The engine's clock runs on while it paints the window; here it moves
    // between frames, so a task that has painted (07D1) since it last read the clock in this frame
    // waits for the next one. Before 2.40 a task that reads it again where it read it last, in the
    // same frame, waits too
    Register(0x03BD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int paints = 0;
        if (auto found = vm.m_taskPaints.find(c.Slot); found != vm.m_taskPaints.end())
            paints = found->second;
        if (auto read = vm.m_clockReads.find(c.Slot); read != vm.m_clockReads.end() && read->second.Frame == vm.m_frameNumber
            && (read->second.Paints != paints || (vm.EngineVersion < 240 && read->second.Pc == c.Current)))
        {
            c.Pc = c.Current;
            vm.FrameShown = true;
            return 3;
        }
        vm.m_clockReads[c.Slot] = {vm.m_frameNumber, paints, c.Current};
        vm.Store(c, i.Args[0], int(vm.Clock()));
        return 0;
    });
    // 03B6 v: timeGetTime, the clock 03BD reads. Before 2.40 a task that reads it again where it
    // read it last, in the same frame, waits for the next one
    Register(0x03B6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        if (vm.EngineVersion < 240)
        {
            if (auto read = vm.m_timeReads.find(c.Slot);
                read != vm.m_timeReads.end() && read->second == std::pair(vm.m_frameNumber, c.Current))
            {
                c.Pc = c.Current;
                vm.FrameShown = true;
                return 3;
            }
            vm.m_timeReads[c.Slot] = {vm.m_frameNumber, c.Current};
        }
        vm.Store(c, i.Args[0], int(vm.Clock()));
        return 0;
    });
    // 0320 n, ms / 0321 n, v: timer n's period (0: 1) / its start, at v and the time now
    Register(0x0320, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), ms = vm.Value(c, i.Args[1]);
        vm.m_engineTimers[n].Period = ms == 0 ? 1 : ms;
        return 0;
    });
    Register(0x0321, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), v = vm.Value(c, i.Args[1]);
        auto& t = vm.m_engineTimers[n];
        t.Value = v;
        t.Count = 0;
        t.Start = vm.Clock();
        return 0;
    });
    // 07BF ms: MsgWaitForMultipleObjects on an event never set - the task waits ms, or less when
    // input comes; here a frame at a time as 0028
    Register(0x07BF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int ms = vm.Value(c, i.Args[0]);
        auto wait = vm.m_inputWaits.find(c.Slot);
        if (wait == vm.m_inputWaits.end())
            wait = vm.m_inputWaits.emplace(c.Slot, vm.Clock()).first;
        if (ms > 0 && vm.Clock() - wait->second < uint32_t(ms))
        {
            c.Pc = c.Current;
            vm.FrameShown = true;
            return 3;
        }
        vm.m_inputWaits.erase(c.Slot);
        return 0;
    });
    // 03B7 year, month, day, day of week / 03B8 hour, minute, second, ms (local time)
    Register(0x03B7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::time_t now = std::time(nullptr);
        std::tm local = *std::localtime(&now);
        vm.Store(c, i.Args[0], local.tm_year + 1900);
        vm.Store(c, i.Args[1], local.tm_mon + 1);
        vm.Store(c, i.Args[2], local.tm_mday);
        vm.Store(c, i.Args[3], local.tm_wday);
        return 0;
    });
    Register(0x03B8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto now = std::chrono::system_clock::now();
        std::time_t seconds = std::chrono::system_clock::to_time_t(now);
        std::tm local = *std::localtime(&seconds);
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()).count() % 1000;
        vm.Store(c, i.Args[0], local.tm_hour);
        vm.Store(c, i.Args[1], local.tm_min);
        vm.Store(c, i.Args[2], local.tm_sec);
        vm.Store(c, i.Args[3], int(ms));
        return 0;
    });

    // ---- Memory ----

    Register(0x0302, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[1], vm.Read32(vm.Value(c, i.Args[0]))); return 0; });
    Register(0x0303, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]);
        vm.Write32(a, vm.Value(c, i.Args[1]));
        return 0;
    });
    Register(0x0304, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[1], vm.ReadByte(vm.Value(c, i.Args[0]))); return 0; });
    Register(0x0305, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]);
        vm.WriteByte(a, uint8_t(vm.Value(c, i.Args[1])));
        return 0;
    });
    Register(0x0306, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[1], vm.Read16(vm.Value(c, i.Args[0]))); return 0; });
    Register(0x0307, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]);
        vm.Write16(a, vm.Value(c, i.Args[1]));
        return 0;
    });
    Register(0x0308, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[2], vm.Read32(a));
        return 0;
    });
    Register(0x0309, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
        vm.Write32(a, vm.Value(c, i.Args[2]));
        return 0;
    });
    Register(0x030A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[2], vm.ReadByte(a));
        return 0;
    });
    Register(0x030B, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
        vm.WriteByte(a, uint8_t(vm.Value(c, i.Args[2])));
        return 0;
    });
    // 02C6 dst, src, n: memmove, of any size
    Register(0x02C6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int dst = vm.Value(c, i.Args[0]), src = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
        vm.MoveMemory(dst, src, std::max(n, 0));
        return 0;
    });
    // 02C7 dst, n, byte: memset, of any size
    Register(0x02C7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int dst = vm.Value(c, i.Args[0]), n = vm.Value(c, i.Args[1]);
        vm.FillMemory(dst, std::max(n, 0), uint8_t(vm.Value(c, i.Args[2])));
        return 0;
    });
    // 02CB a, b, n, v: v = 0 when the n bytes at a and b are the same, else what was left to
    // compare (dwords, then the last 1-3 bytes)
    Register(0x02CB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
        int left = int(uint32_t(n) >> 2);
        for (; left > 0 && vm.Read32(a) == vm.Read32(b); left--, a += 4, b += 4) {}
        if (left == 0)
            for (left = n & 3; left > 0 && vm.ReadByte(a) == vm.ReadByte(b); left--, a++, b++) {}
        vm.Store(c, i.Args[3], left);
        return 0;
    });
    // 02E4 p: the slot's data pointer; 02E5 v: read a dword from it; 02E8 v: its module offset
    Register(0x02E4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { c.DataPointer = vm.Value(c, i.Args[0]); return 0; });
    Register(0x02E5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.Read32(c.DataPointer));
        c.DataPointer += 4;
        return 0;
    });
    Register(0x02E8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], c.DataPointer - c.Base); return 0; });
    // 02E6 v / 02E7 v (v2.31): v = the byte / word (unsigned) at the data pointer, which goes on by 1 / 2
    Register(0x02E6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.ReadByte(c.DataPointer));
        c.DataPointer += 1;
        return 0;
    });
    Register(0x02E7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.Read16(c.DataPointer));
        c.DataPointer += 2;
        return 0;
    });

    // ---- Strings ----

    // 02D5 lea "text", dst: a relative destination gets the module offset
    Register(0x02D5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int address = vm.Value(c, i.Args[0]);
        vm.Store(c, i.Args[1], i.Args[1].Relative ? address - c.Base : address);
        return 0;
    });
    Register(0x02D0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[1], vm.StringLength(vm.Value(c, i.Args[0]))); return 0; });
    // 02D1 dst, src: strcpy; 02D3 dst, src: strcat
    Register(0x02D1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int dst = vm.Value(c, i.Args[0]), src = vm.Value(c, i.Args[1]);
        int n = vm.StringLength(src);
        vm.WriteBytes(dst, vm.ReadBytes(src, n + 1));
        return 0;
    });
    Register(0x02D3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int dst = vm.Value(c, i.Args[0]), src = vm.Value(c, i.Args[1]);
        int n = vm.StringLength(src);
        vm.WriteBytes(dst + vm.StringLength(dst), vm.ReadBytes(src, n + 1));
        return 0;
    });
    // 02D4 a, b, result: strcmp; 02D6: case-insensitive
    Register(0x02D4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]);
        int d = vm.CompareStrings(a, b, false);
        vm.Store(c, i.Args[2], d < 0 ? -1 : d > 0 ? 1 : 0);
        return 0;
    });
    Register(0x02D6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]);
        int d = vm.CompareStrings(a, b, true);
        vm.Store(c, i.Args[2], d < 0 ? -1 : d > 0 ? 1 : 0);
        return 0;
    });
    // 0BCF a, b, n, result: strncmp (the difference of the first unequal bytes)
    Register(0x0BCF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
        int result = 0;
        for (uint32_t k = 0; k < uint32_t(n); k++)
        {
            uint8_t x = vm.ReadByte(a + int(k)), y = vm.ReadByte(b + int(k));
            if (x != y || x == 0)
            {
                result = x - y;
                break;
            }
        }
        vm.Store(c, i.Args[3], result);
        return 0;
    });
    // 0BD0 dst, src, n: strncpy (up to n bytes, stopping at the end of src; the rest of the n
    // filled with zeros, no end added when src is longer)
    Register(0x0BD0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int dst = vm.Value(c, i.Args[0]), src = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
        if (n <= 0)
            return 0;
        openshiina::Bytes bytes(static_cast<size_t>(n));
        for (int k = 0; k < n; k++)
            if ((bytes[size_t(k)] = vm.ReadByte(src + k)) == 0)
                break;
        vm.WriteBytes(dst, bytes);
        return 0;
    });
    // 0BD4 text, part, result: strstr (the address of the first match, or 0)
    Register(0x0BD4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int text = vm.Value(c, i.Args[0]), part = vm.Value(c, i.Args[1]);
        auto t = vm.ReadBytes(text, vm.StringLength(text)), p = vm.ReadBytes(part, vm.StringLength(part));
        auto at = std::search(t.begin(), t.end(), p.begin(), p.end());
        vm.Store(c, i.Args[2], at == t.end() && !p.empty() ? 0 : text + int(at - t.begin()));
        return 0;
    });
}

int ScnVm::If(ScnContext& c, const ScnInstruction& i, int mode)
{
    int x = Value(c, i.Args[0]), y = Value(c, i.Args[1]);
    int cmp = i.Raw[0];
    bool holds;
    if (mode == 0)
    {
        switch (cmp)
        {
        case 0: holds = x == y; break;
        case 1: holds = x != y; break;
        case 2: holds = uint32_t(x) >= uint32_t(y); break;
        case 3: holds = uint32_t(x) > uint32_t(y); break;
        case 4: holds = uint32_t(x) <= uint32_t(y); break;
        case 5: holds = uint32_t(x) < uint32_t(y); break;
        case 6: holds = (x & y) != 0; break;
        default: holds = (x | y) != 0; break;
        }
    }
    else if (mode == 1)
    {
        switch (cmp)
        {
        case 0: holds = x == y; break;
        case 1: holds = x != y; break;
        case 2: holds = x >= y; break;
        case 3: holds = x > y; break;
        case 4: holds = x <= y; break;
        default: holds = x < y; break;
        }
    }
    else
    {
        float a = AsFloat(x), b = AsFloat(y);
        switch (cmp)
        {
        case 0: holds = a == b; break;
        case 1: holds = a != b; break;
        case 2: holds = a >= b; break;
        case 3: holds = a > b; break;
        case 4: holds = a <= b; break;
        default: holds = a < b; break;
        }
    }
    if (!holds)
        c.Pc = c.Base + i.Raw[1];
    return 0;
}

int ScnVm::CallWithArguments(ScnContext& c, const ScnInstruction& i, bool toSlot)
{
    const auto& first = i.Args[0];
    int result = first.Kind == 4 && first.Value == 0 && !first.Relative ? 0 : AddressOfResult(c, first);
    int target = Value(c, i.Args[1]);
    int n = i.Raw[0];
    int* f = c.Frames;
    int top = c.FrameTop;
    if (top + n + 2 > int(std::size(c.Frames)))
        throw Error(c, "callmod frames overflow");
    f[top] = result;
    f[top + n + 1] = n;
    for (int k = n; k >= 1; k--)
        f[top + k] = Value(c, i.Args[size_t(2 + (n - k))]);
    c.FrameTop = top + n + 2;
    Push(c, c.Base);
    Push(c, c.Pc);
    if (toSlot)
    {
        auto& s = m_slots.at(size_t(target));
        c.Base = s.CodeBase;
        c.Pc = s.Entry;
    }
    else
        c.Pc = target;
    return 0;
}

int ScnVm::AddressOfResult(ScnContext& c, const ScnOperand& o)
{
    return o.Kind == 4 || o.Kind == 5 ? Value(c, o) : AddressOf(c, o);
}

int ScnVm::CompareStrings(int a, int b, bool ignoreCase)
{
    for (int k = 0;; k++)
    {
        int x = ReadByte(a + k), y = ReadByte(b + k);
        if (ignoreCase)
        {
            if (x >= 'A' && x <= 'Z')
                x += 32;
            if (y >= 'A' && y <= 'Z')
                y += 32;
        }
        if (x != y || x == 0)
            return x - y;
    }
}

} // namespace openshiina::scripting
