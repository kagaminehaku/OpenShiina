// Engine v2.34 (Ao no Juuai's aoj.EXE, 2005; the table is Data/ScnOps/ops_v234.tsv): the opcodes
// the GRAND†CROSS games never use, and those whose operands differ from the later engines'
// (Register234: used where EngineVersion is below 240). The handlers named are aoj.EXE's.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // 0502's fades: by task, when they started, from and to which level
    private readonly Dictionary<int, (uint Start, int From, int To)> m_fades234 = new();

    private void RegisterEngine234()
    {
        // 0A28 buffer (0x41AB10: lstrcpyA(buffer, 0x722B5C)): the game's folder, '\' at its end -
        // Ao no Juuai's START adds "save\system.bin" and the like for its save files; here the
        // save folder (DataPath), so that they go to the host's
        Register(0x0A28, (vm, c, i) =>
        {
            vm.WriteString(vm.Value(c, i.Args[0]), vm.DataPath);
            return 0;
        });
        // 0A5C v (0x41BA50): v = the engine draws with MMX (0x723910: cpuid says MMX and the
        // setting at 0x4691A0 allows it); the x86 routines run MMX here
        Register(0x0A5C, (vm, c, i) => { vm.Store(c, i.Args[0], 1); return 0; });
        // 0502 ms, level (0x411950 -> FUN_004089f0): the screen from the level it is at (0500) to
        // level in ms, from surface 1 as 0500 shows it. The engine loops inside the opcode (an
        // end of the game meanwhile gives 1); here the task waits a frame at a time
        Register(0x0502, (vm, c, i) =>
        {
            int ms = vm.Value(c, i.Args[0]), to = vm.Value(c, i.Args[1]);
            if (!vm.m_fades234.TryGetValue(c.Slot, out var fade))
                vm.m_fades234[c.Slot] = fade = (vm.Clock, vm.m_screenLevel is >= 0 and <= 255 ? vm.m_screenLevel : 255, Math.Clamp(to, 0, 255));
            uint elapsed = vm.Clock - fade.Start;
            bool done = ms <= 0 || elapsed >= (uint)ms;
            int level = done ? fade.To : fade.From + (int)((fade.To - fade.From) * (long)elapsed / ms);
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
            vm.m_fades234.Remove(c.Slot);
            return 0;
        });
        // 04BD picture, frame, flags, priority, x, y, word 6 [, alpha] [, r, g, b] (0x410EC0 ->
        // FUN_0040b350): a sprite entry as v2.47's (eight dwords), the alpha ORed into the flags,
        // the tint red | green << 8 | blue << 16 (0 without one)
        Register234(0x04BD, (vm, c, i) =>
        {
            int list = vm.m_spriteList;
            int e = vm.SpriteEntry(list, vm.m_spriteCounts[list]);
            var v = new int[7];
            for (int k = 0; k < 7; k++)
                v[k] = vm.Value(c, i.Args[k]);
            int next = 7, tint = 0;
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
            vm.m_spriteCounts[list]++;
            return 0;
        });
        // 0028 ms (0x415B70 -> FUN_00415ab0): the task waits ms, the engine pumping the window's
        // messages meanwhile (an end of the game gives 1); here a frame at a time
        Register234(0x0028, (vm, c, i) =>
        {
            int ms = vm.Value(c, i.Args[0]);
            if (!vm.m_sleeps234.TryGetValue(c.Slot, out uint start))
                vm.m_sleeps234[c.Slot] = start = vm.Clock;
            if (ms > 0 && vm.Clock - start < (uint)ms)
            {
                c.Pc = c.Current;
                vm.FrameShown = true;
                return 3;
            }
            vm.m_sleeps234.Remove(c.Slot);
            return 0;
        });
        // 03BB (0x417650): a stopwatch starts (0x497C04 = the time); 03BC v (0x417660): v = the
        // ms since; 03BE ms (0x417690 -> FUN_00415ab0): the task waits until ms after it started
        Register(0x03BB, (vm, c, i) => { vm.m_stopwatch = vm.Clock; return 0; });
        Register(0x03BC, (vm, c, i) => { vm.Store(c, i.Args[0], (int)(vm.Clock - vm.m_stopwatch)); return 0; });
        Register(0x03BE, (vm, c, i) =>
        {
            int ms = vm.Value(c, i.Args[0]);
            if (ms > 0 && vm.Clock - vm.m_stopwatch < (uint)ms)
            {
                c.Pc = c.Current;
                vm.FrameShown = true;
                return 3;
            }
            return 0;
        });
        // The mouse (0456 position, 0458 buttons) and the time (03BD, 03BC): 2.34's menus and
        // transitions poll them in a loop until they change - the engine runs it as fast as it
        // can (its main loop takes a step of every task and pumps the window's messages only
        // after 0033) and a frame ends only where a task waits (RunFrame). Both change between
        // frames here, so a task that reads one again in a frame waits for the next one
        foreach (int op in new[] { 0x0456, 0x0458, 0x03BD, 0x03BC })
        {
            var later = m_handlers[op];
            Register234(op, (vm, c, i) =>
            {
                if (vm.m_polled234.GetValueOrDefault((c.Slot, i.Op)) == vm.m_frameNumber)
                {
                    c.Pc = c.Current;
                    vm.FrameShown = true;
                    return 3;
                }
                vm.m_polled234[(c.Slot, i.Op)] = vm.m_frameNumber;
                return later(vm, c, i);
            });
        }
    }

    // 0028's waits by task (when they started), and the frame each task last read the mouse or
    // the time in (by task and opcode)
    private readonly Dictionary<int, uint> m_sleeps234 = new();
    private readonly Dictionary<(int Slot, int Op), int> m_polled234 = new();

    // 03BB's stopwatch: when it started
    private uint m_stopwatch;
}
