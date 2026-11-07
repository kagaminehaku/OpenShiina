// C# Scripting/ScnVm.Engine234.cs: engine v2.34 (Ao no Juuai's aoj.EXE, 2005; the table is
// Data/ScnOps/ops_v234.tsv): the opcodes the GRAND†CROSS games never use, and those whose operands
// differ from the later engines' (Register234: used where EngineVersion is below 240). The handlers
// named are aoj.EXE's.
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

int ScnVm::PolledOld(ScnVm& vm, ScnContext& c, const ScnInstruction& i)
{
    // The mouse and the time read twice in a frame by the old engines' tasks: the second read waits
    // for the next frame, else the handler the later engines have (m_polledLater)
    auto key = std::pair(c.Slot, i.Op);
    if (auto polled = vm.m_polled234.find(key); polled != vm.m_polled234.end() && polled->second == vm.m_frameNumber)
    {
        c.Pc = c.Current;
        vm.FrameShown = true;
        return 3;
    }
    vm.m_polled234[key] = vm.m_frameNumber;
    return vm.m_polledLater[i.Op](vm, c, i);
}

void ScnVm::RegisterEngine234()
{
    // 0A28 buffer (0x41AB10: lstrcpyA(buffer, 0x722B5C)): the game's folder, '\' at its end - Ao no
    // Juuai's START adds "save\system.bin" and the like for its save files; here the save folder
    // (DataPath), so that they go to the host's
    Register(0x0A28, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.WriteString(vm.Value(c, i.Args[0]), vm.DataPath());
        return 0;
    });
    // 0A5C v (0x41BA50): v = the engine draws with MMX (0x723910: cpuid says MMX and the setting at
    // 0x4691A0 allows it); the x86 routines run MMX here
    Register(0x0A5C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], 1);
        return 0;
    });
    // 0502 ms, level (0x411950 -> FUN_004089f0): the screen from the level it is at (0500) to level
    // in ms, from surface 1 as 0500 shows it. The engine loops inside the opcode (an end of the game
    // meanwhile gives 1); here the task waits a frame at a time
    Register(0x0502, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int ms = vm.Value(c, i.Args[0]), to = vm.Value(c, i.Args[1]);
        auto found = vm.m_fades234.find(c.Slot);
        if (found == vm.m_fades234.end())
            found = vm.m_fades234
                        .emplace(c.Slot, Fade234{vm.Clock(), vm.m_screenLevel >= 0 && vm.m_screenLevel <= 255 ? vm.m_screenLevel : 255,
                                                 std::clamp(to, 0, 255)})
                        .first;
        const Fade234 fade = found->second;
        uint32_t elapsed = vm.Clock() - fade.Start;
        bool done = ms <= 0 || elapsed >= uint32_t(ms);
        int level = done ? fade.To : fade.From + int((fade.To - fade.From) * int64_t(elapsed) / ms);
        vm.m_screenLevel = level;
        vm.EngineGlobals[0x4880D8] = level;
        vm.ShowAtLevel(1, level);
        vm.InvalidateWindow();
        if (!done)
        {
            c.Pc = c.Current;
            vm.FrameShown = true;
            return 3;
        }
        vm.m_fades234.erase(c.Slot);
        return 0;
    });
    // 04BD picture, frame, flags, priority, x, y, word 6 [, alpha] [, r, g, b] (0x410EC0 ->
    // FUN_0040b350): a sprite entry as v2.47's (eight dwords), the alpha ORed into the flags, the
    // tint red | green << 8 | blue << 16 (0 without one)
    Register234(0x04BD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int list = vm.m_spriteList;
        int e = vm.SpriteEntry(list, vm.m_spriteCounts[size_t(list)]);
        int v[7];
        for (int k = 0; k < 7; k++)
            v[k] = vm.Value(c, i.Args[size_t(k)]);
        size_t next = 7;
        int tint = 0;
        if (i.Raw[0] != 0)
            v[2] |= vm.Value(c, i.Args[next++]);
        if (i.Raw[1] != 0)
        {
            int r = vm.Value(c, i.Args[next]), g = vm.Value(c, i.Args[next + 1]), b = vm.Value(c, i.Args[next + 2]);
            tint = (r & 0xFF) | (g & 0xFF) << 8 | (b & 0xFF) << 16;
        }
        for (int k = 0; k < 7; k++)
            vm.Write32(e + 4 * k, v[k]);
        vm.Write32(e + 28, tint);
        vm.m_spriteCounts[size_t(list)]++;
        return 0;
    });
    // 0028 ms (0x415B70 -> FUN_00415ab0): the task waits ms, the engine pumping the window's messages
    // meanwhile (an end of the game gives 1); here a frame at a time. v2.47's (Oreimo 0x425B8D) waits
    // the same way (Sleep(1) until ms have passed; Hitozuma Gui's menus, v2.45), so it is every version's
    Register(0x0028, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SleepOld(c, vm.Value(c, i.Args[0])); });
    // 03BB (0x417650): a stopwatch starts (0x497C04 = the time); 03BC v (0x417660): v = the ms since;
    // 03BE ms (0x417690 -> FUN_00415ab0): the task waits until ms after it started
    Register(0x03BB, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        vm.m_stopwatch = vm.Clock();
        return 0;
    });
    Register(0x03BC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], int(vm.Clock() - vm.m_stopwatch));
        return 0;
    });
    Register(0x03BE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int ms = vm.Value(c, i.Args[0]);
        if (ms > 0 && vm.Clock() - vm.m_stopwatch < uint32_t(ms))
        {
            c.Pc = c.Current;
            vm.FrameShown = true;
            return 3;
        }
        return 0;
    });
    // The mouse (0456 position, 0458 buttons) and the time (03BD, 03BC; 03B6, timeGetTime, which
    // Cleavage's (v2.33) title polls until it changes): 2.34's menus and transitions poll them in a
    // loop until they change - the engine runs it as fast as it can (its main loop takes a step of
    // every task and pumps the window's messages only after 0033) and a frame ends only where a task
    // waits (RunFrame). Both change between frames here, so a task that reads one again in a frame
    // waits for the next one. 05B9 (a movie's state) too: Vanquish's (v2.33) movies play in a loop of
    // 05B9, 05C0 (the next frame), 0514 and 07D0 with no other wait
    for (int op : {0x0456, 0x0458, 0x03BD, 0x03BC, 0x03B6, 0x05B9})
    {
        m_polledLater[op] = m_handlers[size_t(op)];
        Register234(op, PolledOld);
    }
}

int ScnVm::SleepOld(ScnContext& c, int ms)
{
    // 0028 / 0029: the task waits ms, a frame at a time
    auto found = m_sleeps234.find(c.Slot);
    if (found == m_sleeps234.end())
        found = m_sleeps234.emplace(c.Slot, Clock()).first;
    if (ms > 0 && Clock() - found->second < uint32_t(ms))
    {
        c.Pc = c.Current;
        FrameShown = true;
        return 3;
    }
    m_sleeps234.erase(c.Slot);
    return 0;
}

} // namespace openshiina::scripting
