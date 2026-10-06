// The interpreter's own opcodes: flow, calls between slots, tasks, variables, arithmetic, memory
// and strings, time and random numbers - each as its handler in the executable does it
// (docs/engine-notes.md, section 10). Opcode numbers in the comments are the engine's.

using System.Numerics;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    #region Named variables

    // Per slot: scopes of "local" declarations, innermost last. Globals ("global", or a name
    // used before any declaration) are shared.
    private readonly List<Dictionary<string, (int Address, int Count)>>[] m_scopes =
        Enumerable.Range(0, Slots).Select(_ => new List<Dictionary<string, (int, int)>>()).ToArray();
    private readonly Dictionary<string, (int Address, int Count)> m_globals = new(StringComparer.Ordinal);

    // Blocks of scopes that were left, by size, used again (zeroed) by later declarations
    private readonly Dictionary<int, Stack<int>> m_namedFree = new();

    private int AllocateNamed(int count)
    {
        int size = 4 * Math.Max(1, count);
        if (m_namedFree.TryGetValue(size, out var free) && free.Count > 0)
        {
            int block = free.Pop();
            FillMemory(block, size, 0);
            return block;
        }
        int at = m_namedTop;
        m_namedTop += size;
        if (m_namedTop > NamedRegion + 0x01000000)
            throw new ScnException("Named variables fill their memory", -1, 0, 0);
        return at;
    }

    /// <summary>A scope is left: its variables' memory can be used again.</summary>
    private void ReleaseScope(Dictionary<string, (int Address, int Count)> scope)
    {
        foreach (var (address, count) in scope.Values)
        {
            int size = 4 * Math.Max(1, count);
            if (!m_namedFree.TryGetValue(size, out var free))
                m_namedFree[size] = free = new Stack<int>();
            free.Push(address);
        }
    }

    /// <summary>Leaves the last n scopes of a slot (all with n = -1).</summary>
    private void LeaveScopes(int slot, int n)
    {
        var scopes = m_scopes[slot];
        int drop = n < 0 ? scopes.Count : Math.Min(n, scopes.Count);
        for (int k = 0; k < drop; k++)
        {
            ReleaseScope(scopes[^1]);
            scopes.RemoveAt(scopes.Count - 1);
        }
    }

    /// <summary>"name" or "name[n]" of a declaration: the name and its element count.</summary>
    private (string Name, int Count) Declared(ScnContext c, string text)
    {
        int open = text.IndexOf('[');
        if (open < 0)
            return (text.Trim(), 1);
        int close = text.LastIndexOf(']');
        string size = text[(open + 1)..(close > open ? close : text.Length)];
        return (text[..open].Trim(), Math.Max(1, int.TryParse(size, out int n) ? n : Calculate(c, size)));
    }

    /// <summary>Address of a named variable, "name" or "name[index]" (FUN_00434F60).</summary>
    public int NamedAddress(ScnContext c, string text)
    {
        string name = text;
        int index = 0;
        int open = text.IndexOf('[');
        if (open >= 0)
        {
            name = text[..open].Trim();
            int close = text.LastIndexOf(']');
            index = Calculate(c, text[(open + 1)..(close > open ? close : text.Length)]);
        }
        var scopes = m_scopes[c.Slot];
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(name, out var local))
                return local.Address + 4 * index;
        if (!m_globals.TryGetValue(name, out var global))
            m_globals[name] = global = (AllocateNamed(1), 1);
        return global.Address + 4 * index;
    }

    /// <summary>A global by name, for the host (g$sel ...); created when missing.</summary>
    public int GlobalAddress(string name) => NamedAddress(m_slots[0], name);

    #endregion

    private void RegisterCore()
    {
        // ---- Tasks and modules ----

        // 0000 end v: the task stops; a value quits the game
        Register(0x0000, (vm, c, i) =>
        {
            c.Flags = 0;
            vm.TasksChanged();
            c.ExitCode = vm.Value(c, i.Args[0]);
            return c.ExitCode != 0 ? 1 : 0;
        });
        // 0001 loadmod slot, file: load and run
        Register(0x0001, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            string file = vm.ReadString(vm.Value(c, i.Args[1]));
            if (slot is < 0 or >= Slots || !vm.LoadModule(slot, file, start: true))
                throw vm.Error(c, $"loadmod {slot}, \"{file}\" failed");
            vm.LeaveScopes(slot, -1);
            return 0;
        });
        // 0002 slot, file: load without running
        Register(0x0002, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            string file = vm.ReadString(vm.Value(c, i.Args[1]));
            if (slot is < 0 or >= Slots || !vm.LoadModule(slot, file, start: false))
                throw vm.Error(c, $"load {slot}, \"{file}\" failed");
            return 0;
        });
        // 0009 slot: reset the slot's stack and named variables
        Register(0x0009, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            if (slot is >= 0 and < Slots)
            {
                vm.m_slots[slot].Sp = StackSize;
                vm.LeaveScopes(slot, -1);
            }
            return 0;
        });
        // 000C id, label: slot id runs the label of this module (high bit: an absolute address)
        Register(0x000C, (vm, c, i) =>
        {
            int raw = vm.Value(c, i.Args[0]);
            int id = raw & 0x7FFFFFFF;
            if (id > 1000)
                return 2;
            int target = vm.Value(c, i.Args[1]);
            var s = vm.m_slots[id];
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
        Register(0x000D, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            if (slot is < 0 or > 1000)
                return 2;
            var s = vm.m_slots[slot];
            s.Pc = s.Entry;
            s.Sp = StackSize;
            s.Flags = 1;
            vm.TasksChanged();
            return 0;
        });
        // 001E / 001F: set / clear task flag 4
        Register(0x001E, (vm, c, i) => { c.Flags |= 4; return 0; });
        Register(0x001F, (vm, c, i) => { c.Flags &= ~4; return 0; });
        // D382 / AA82: switches set to their own opcode number; AA82 lets the scripts write files
        Register(0xD382, (vm, c, i) => { vm.SwitchD382 = 0xD382; return 0; });
        Register(0xAA82, (vm, c, i) => { vm.FileWritesEnabled = true; return 0; });
        // 0048: two engine globals (0x13B5198 / 0x13B519C) take the opcode number, which is in EAX there
        Register(0x0048, (vm, c, i) => { vm.Global13B5198 = vm.Global13B519C = 0x48; return 0; });
        // 0034: wait for the next frame (the scheduler yields after it)
        Register(0x0034, (vm, c, i) => 0);
        // 0032 / 0033: stop / start yielding after every instruction
        Register(0x0032, (vm, c, i) => { vm.YieldEveryInstruction = false; return 0; });
        Register(0x0033, (vm, c, i) => { vm.YieldEveryInstruction = true; return 0; });
        // 02EE mode: 1 = push that flag
        Register(0x02EE, (vm, c, i) =>
        {
            if (vm.Value(c, i.Args[0]) == 1)
                vm.Push(c, vm.YieldEveryInstruction ? 1 : 0);
            return 0;
        });
        // 02EF mode: pop the flag pushed by 02EE; 1 = put it back
        Register(0x02EF, (vm, c, i) =>
        {
            int saved = vm.Pop(c);
            if (vm.Value(c, i.Args[0]) == 1)
                vm.YieldEveryInstruction = saved != 0;
            return 0;
        });

        // ---- Branches ----

        // 01F4 / 01FE / 01FF if a cmp b else goto L: unsigned / signed / float comparison
        Register(0x01F4, (vm, c, i) => vm.If(c, i, 0));
        Register(0x01FE, (vm, c, i) => vm.If(c, i, 1));
        Register(0x01FF, (vm, c, i) => vm.If(c, i, 2));
        Register(0x0258, (vm, c, i) => { c.Pc = vm.Value(c, i.Args[0]); return 0; });
        Register(0x0259, (vm, c, i) =>
        {
            int index = vm.Value(c, i.Args[0]);
            c.Pc = c.Base + (index >= 0 && index < i.Targets.Length ? i.Targets[index] : i.Raw[0]);
            return 0;
        });
        Register(0x0208, (vm, c, i) => { vm.m_caseValue = vm.Value(c, i.Args[0]); return 0; });
        Register(0x0209, (vm, c, i) =>
        {
            c.Pc = c.Base + (i.CaseValues.Contains(vm.m_caseValue) ? i.Raw[0] : i.Raw[1]);
            return 0;
        });
        // 0212 offset, n: the loop counter (u32 at module + offset - 8) = n
        Register(0x0212, (vm, c, i) =>
        {
            vm.Write32(c.Base + i.Raw[0] - 8, vm.Value(c, i.Args[0]));
            return 0;
        });
        // 0213 counter, target: count down; jump back while it is not 0
        Register(0x0213, (vm, c, i) =>
        {
            int counter = i.Address + 2;
            int n = vm.Read32(counter) - 1;
            vm.Write32(counter, n);
            if (n != 0)
                c.Pc = c.Base + vm.Read32(counter + 4);
            return 0;
        });

        // ---- Calls ----

        // 0262 label: call a label of this module
        Register(0x0262, (vm, c, i) =>
        {
            int target = vm.Value(c, i.Args[0]);
            vm.Push(c, c.Base);
            vm.Push(c, c.Pc);
            c.Pc = target;
            return 0;
        });
        // 0267 slot: run a slot's code (gosub)
        Register(0x0267, (vm, c, i) =>
        {
            var s = vm.m_slots[vm.Value(c, i.Args[0])];
            vm.Push(c, c.Base);
            vm.Push(c, c.Pc);
            c.Base = s.CodeBase;
            c.Pc = s.Entry;
            return 0;
        });
        Register(0x026C, (vm, c, i) =>
        {
            c.Pc = vm.Pop(c);
            c.Base = vm.Pop(c);
            return 0;
        });
        // 026D n: return and drop n stack values
        Register(0x026D, (vm, c, i) =>
        {
            int pc = vm.Pop(c), baseAddress = vm.Pop(c);
            c.Sp += vm.Value(c, i.Args[0]);
            c.Pc = pc;
            c.Base = baseAddress;
            return 0;
        });
        // 0283 result, slot, #n args / 0281 result, label, #n args: call with arguments
        Register(0x0283, (vm, c, i) => vm.CallWithArguments(c, i, toSlot: true));
        Register(0x0281, (vm, c, i) => vm.CallWithArguments(c, i, toSlot: false));
        // 0280 #n vars: the called code takes its arguments
        Register(0x0280, (vm, c, i) =>
        {
            for (int k = 0; k < i.Args.Length; k++)
                vm.Store(c, i.Args[k], c.Frames[c.FrameTop - k - 2]);
            return 0;
        });
        // 0285 v: return v to the caller's result variable
        Register(0x0285, (vm, c, i) =>
        {
            int count = c.FrameTop > 0 ? c.Frames[c.FrameTop - 1] : 0;
            c.FrameTop = Math.Max(0, c.FrameTop - count - 2);
            int result = c.Frames[c.FrameTop];
            int value = vm.Value(c, i.Args[0]);
            if (result != 0)
                vm.Write32(result, value);
            c.Pc = vm.Pop(c);
            c.Base = vm.Pop(c);
            return 0;
        });

        // ---- Stack and declarations ----

        Register(0x02F8, (vm, c, i) => { vm.Push(c, vm.Value(c, i.Args[0])); return 0; });
        Register(0x02F9, (vm, c, i) => { vm.Store(c, i.Args[0], vm.Pop(c)); return 0; });
        Register(0x0316, (vm, c, i) => { c.Sp -= vm.Value(c, i.Args[0]); return 0; });
        Register(0x0317, (vm, c, i) => { c.Sp += vm.Value(c, i.Args[0]); return 0; });
        // 03CF local #n names: a new scope; #0 leaves the innermost one
        Register(0x03CF, (vm, c, i) =>
        {
            var scopes = vm.m_scopes[c.Slot];
            if (i.Args.Length == 0)
            {
                vm.LeaveScopes(c.Slot, 1);
                return 0;
            }
            var scope = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (var name in i.Args)
            {
                var (n, count) = vm.Declared(c, name.Text ?? "");
                scope[n] = (vm.AllocateNamed(count), count);
            }
            scopes.Add(scope);
            return 0;
        });
        // 03CE global #n names
        Register(0x03CE, (vm, c, i) =>
        {
            foreach (var name in i.Args)
            {
                var (n, count) = vm.Declared(c, name.Text ?? "");
                if (!vm.m_globals.ContainsKey(n))
                    vm.m_globals[n] = (vm.AllocateNamed(count), count);
            }
            return 0;
        });
        // 03D0 slot, n: leave n scopes (-1 = all) of a slot (-1 = every slot)
        Register(0x03D0, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]), n = vm.Value(c, i.Args[1]);
            int from = slot == -1 ? 0 : slot, to = slot == -1 ? Slots : slot + 1;
            for (int s = from; s < to && s < Slots; s++)
                vm.LeaveScopes(s, n == -1 ? -1 : n);
            return 0;
        });

        // ---- Arithmetic (second operand is the destination) ----

        Register(0x038E, (vm, c, i) => { vm.Store(c, i.Args[1], vm.Value(c, i.Args[0])); return 0; });
        Register(0x038F, (vm, c, i) => { vm.Store(c, i.Args[1], vm.AddressOf(c, i.Args[0])); return 0; });
        Register(0x0391, (vm, c, i) => { vm.Store(c, i.Args[0], vm.Value(c, i.Args[0]) + 1); return 0; });
        Register(0x0392, (vm, c, i) => { vm.Store(c, i.Args[0], vm.Value(c, i.Args[0]) - 1); return 0; });
        Register(0x0393, (vm, c, i) => vm.Binary(c, i, (a, b) => b + a));
        Register(0x0394, (vm, c, i) => vm.Binary(c, i, (a, b) => b - a));
        Register(0x0396, (vm, c, i) => vm.Binary(c, i, (a, b) => b & a));
        Register(0x0397, (vm, c, i) => vm.Binary(c, i, (a, b) => b | a));
        Register(0x0398, (vm, c, i) => vm.Binary(c, i, (a, b) => b ^ a));
        Register(0x039A, (vm, c, i) => vm.Binary(c, i, (a, b) => (int)((uint)b >> (a & 31))));
        Register(0x039B, (vm, c, i) => vm.Binary(c, i, (a, b) => b << (a & 31)));
        Register(0x039D, (vm, c, i) => vm.Binary(c, i, (a, b) => (int)BitOperations.RotateLeft((uint)b, a)));
        Register(0x039E, (vm, c, i) => vm.Binary(c, i, (a, b) => b * a));
        Register(0x0399, (vm, c, i) => { vm.Store(c, i.Args[0], -vm.Value(c, i.Args[0])); return 0; });
        // 03A0 divisor, value, remainder: value /= divisor
        Register(0x03A0, (vm, c, i) =>
        {
            int d = vm.Value(c, i.Args[0]), v = vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[1], d == 0 ? 0 : v / d);
            vm.Store(c, i.Args[2], d == 0 ? 0 : v % d);
            return 0;
        });
        // 03DE eval "expression", mode, result
        Register(0x03DE, (vm, c, i) =>
        {
            int value = vm.Calculate(c, vm.ReadString(vm.Value(c, i.Args[0])));
            vm.Store(c, i.Args[1], value);
            return 0;
        });
        // 03AC n -> v: rand() % n
        Register(0x03AC, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            vm.Store(c, i.Args[1], n == 0 ? 0 : (int)((uint)vm.Rand() % (uint)n));
            return 0;
        });
        // 03AE seed: srand
        Register(0x03AE, (vm, c, i) => { vm.SeedRand(vm.Value(c, i.Args[0])); return 0; });
        // 03BD v: the time in ms
        Register(0x03BD, (vm, c, i) => { vm.Store(c, i.Args[0], (int)vm.m_host.Milliseconds); return 0; });
        // 03B7 year, month, day, day of week / 03B8 hour, minute, second, ms (local time)
        Register(0x03B7, (vm, c, i) =>
        {
            var now = DateTime.Now;
            vm.Store(c, i.Args[0], now.Year);
            vm.Store(c, i.Args[1], now.Month);
            vm.Store(c, i.Args[2], now.Day);
            vm.Store(c, i.Args[3], (int)now.DayOfWeek);
            return 0;
        });
        Register(0x03B8, (vm, c, i) =>
        {
            var now = DateTime.Now;
            vm.Store(c, i.Args[0], now.Hour);
            vm.Store(c, i.Args[1], now.Minute);
            vm.Store(c, i.Args[2], now.Second);
            vm.Store(c, i.Args[3], now.Millisecond);
            return 0;
        });

        // ---- Memory ----

        Register(0x0302, (vm, c, i) => { vm.Store(c, i.Args[1], vm.Read32(vm.Value(c, i.Args[0]))); return 0; });
        Register(0x0303, (vm, c, i) => { vm.Write32(vm.Value(c, i.Args[0]), vm.Value(c, i.Args[1])); return 0; });
        Register(0x0304, (vm, c, i) => { vm.Store(c, i.Args[1], vm.ReadByte(vm.Value(c, i.Args[0]))); return 0; });
        Register(0x0305, (vm, c, i) => { int a = vm.Value(c, i.Args[0]); vm.WriteByte(a, (byte)vm.Value(c, i.Args[1])); return 0; });
        Register(0x0306, (vm, c, i) => { vm.Store(c, i.Args[1], vm.Read16(vm.Value(c, i.Args[0]))); return 0; });
        Register(0x0307, (vm, c, i) => { int a = vm.Value(c, i.Args[0]); vm.Write16(a, vm.Value(c, i.Args[1])); return 0; });
        Register(0x0308, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], vm.Read32(a));
            return 0;
        });
        Register(0x0309, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
            vm.Write32(a, vm.Value(c, i.Args[2]));
            return 0;
        });
        Register(0x030A, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], vm.ReadByte(a));
            return 0;
        });
        Register(0x030B, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]) + vm.Value(c, i.Args[1]);
            vm.WriteByte(a, (byte)vm.Value(c, i.Args[2]));
            return 0;
        });
        // 02C6 dst, src, n: memmove
        Register(0x02C6, (vm, c, i) =>
        {
            int dst = vm.Value(c, i.Args[0]), src = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
            vm.WriteBytes(dst, vm.ReadBytes(src, Math.Clamp(n, 0, 0x4000000)));
            return 0;
        });
        // 02C7 dst, n, byte: memset
        Register(0x02C7, (vm, c, i) =>
        {
            int dst = vm.Value(c, i.Args[0]), n = vm.Value(c, i.Args[1]);
            byte b = (byte)vm.Value(c, i.Args[2]);
            for (int k = 0; k < n && k < 0x4000000; k++)
                vm.WriteByte(dst + k, b);
            return 0;
        });
        // 02E4 p: the slot's data pointer; 02E5 v: read a dword from it; 02E8 v: its module offset
        Register(0x02E4, (vm, c, i) => { c.DataPointer = vm.Value(c, i.Args[0]); return 0; });
        Register(0x02E5, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], vm.Read32(c.DataPointer));
            c.DataPointer += 4;
            return 0;
        });
        Register(0x02E8, (vm, c, i) => { vm.Store(c, i.Args[0], c.DataPointer - c.Base); return 0; });

        // ---- Strings ----

        // 02D5 lea "text", dst: a relative destination gets the module offset
        Register(0x02D5, (vm, c, i) =>
        {
            int address = vm.Value(c, i.Args[0]);
            vm.Store(c, i.Args[1], i.Args[1].Relative ? address - c.Base : address);
            return 0;
        });
        Register(0x02D0, (vm, c, i) => { vm.Store(c, i.Args[1], vm.StringLength(vm.Value(c, i.Args[0]))); return 0; });
        // 02D1 dst, src: strcpy; 02D3 dst, src: strcat
        Register(0x02D1, (vm, c, i) =>
        {
            int dst = vm.Value(c, i.Args[0]), src = vm.Value(c, i.Args[1]);
            int n = vm.StringLength(src);
            vm.WriteBytes(dst, vm.ReadBytes(src, n + 1));
            return 0;
        });
        Register(0x02D3, (vm, c, i) =>
        {
            int dst = vm.Value(c, i.Args[0]), src = vm.Value(c, i.Args[1]);
            int n = vm.StringLength(src);
            vm.WriteBytes(dst + vm.StringLength(dst), vm.ReadBytes(src, n + 1));
            return 0;
        });
        // 02D4 a, b, result: strcmp; 02D6: case-insensitive
        Register(0x02D4, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], Math.Sign(vm.CompareStrings(a, b, ignoreCase: false)));
            return 0;
        });
        Register(0x02D6, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], Math.Sign(vm.CompareStrings(a, b, ignoreCase: true)));
            return 0;
        });
        // 0BCF a, b, n, result: strncmp (the C runtime's: the difference of the first unequal bytes)
        Register(0x0BCF, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
            int result = 0;
            for (uint k = 0; k < (uint)n; k++)
            {
                byte x = vm.ReadByte(a + (int)k), y = vm.ReadByte(b + (int)k);
                if (x != y || x == 0)
                {
                    result = x - y;
                    break;
                }
            }
            vm.Store(c, i.Args[3], result);
            return 0;
        });
        // 0BD4 text, part, result: strstr (the address of the first match, or 0)
        Register(0x0BD4, (vm, c, i) =>
        {
            int text = vm.Value(c, i.Args[0]), part = vm.Value(c, i.Args[1]);
            byte[] t = vm.ReadBytes(text, vm.StringLength(text)), p = vm.ReadBytes(part, vm.StringLength(part));
            int at = t.AsSpan().IndexOf(p);
            vm.Store(c, i.Args[2], at < 0 ? 0 : text + at);
            return 0;
        });
    }

    private int m_caseValue;

    /// <summary>Engine switch 0x4B3E08, set by op_D382.</summary>
    public int SwitchD382 { get; private set; }

    /// <summary>Engine switch 0x4B3E0C (op_AA82): without it the engine's file writes do nothing.</summary>
    public bool FileWritesEnabled { get; private set; }

    /// <summary>Engine globals 0x13B5198 / 0x13B519C (op_0048 sets them).</summary>
    public int Global13B5198 { get; set; }
    public int Global13B519C { get; set; }

    private int If(ScnContext c, ScnInstruction i, int mode)
    {
        int x = Value(c, i.Args[0]), y = Value(c, i.Args[1]);
        int cmp = i.Raw[0];
        bool holds = mode switch
        {
            0 => cmp switch
            {
                0 => x == y, 1 => x != y, 2 => (uint)x >= (uint)y, 3 => (uint)x > (uint)y,
                4 => (uint)x <= (uint)y, 5 => (uint)x < (uint)y, 6 => (x & y) != 0, _ => (x | y) != 0,
            },
            1 => cmp switch { 0 => x == y, 1 => x != y, 2 => x >= y, 3 => x > y, 4 => x <= y, _ => x < y },
            _ => FloatCompare(BitConverter.Int32BitsToSingle(x), BitConverter.Int32BitsToSingle(y), cmp),
        };
        if (!holds)
            c.Pc = c.Base + i.Raw[1];
        return 0;

        static bool FloatCompare(float a, float b, int cmp) => cmp switch
        {
            0 => a == b, 1 => a != b, 2 => a >= b, 3 => a > b, 4 => a <= b, _ => a < b,
        };
    }

    /// <summary>Two-operand arithmetic: the second operand = f(first, second).</summary>
    private int Binary(ScnContext c, ScnInstruction i, Func<int, int, int> f)
    {
        int a = Value(c, i.Args[0]), b = Value(c, i.Args[1]);
        Store(c, i.Args[1], f(a, b));
        return 0;
    }

    /// <summary>callmod (0283) / call with arguments (0281): FUN_00416010 / FUN_00415EE0.</summary>
    private int CallWithArguments(ScnContext c, ScnInstruction i, bool toSlot)
    {
        int result = i.Args[0].Kind is 4 && i.Args[0].Value == 0 && !i.Args[0].Relative ? 0 : AddressOfResult(c, i.Args[0]);
        int target = Value(c, i.Args[1]);
        int n = i.Raw[0];
        var f = c.Frames;
        int top = c.FrameTop;
        if (top + n + 2 > f.Length)
            throw Error(c, "callmod frames overflow");
        f[top] = result;
        f[top + n + 1] = n;
        for (int k = n; k >= 1; k--)
            f[top + k] = Value(c, i.Args[2 + (n - k)]);
        c.FrameTop = top + n + 2;
        Push(c, c.Base);
        Push(c, c.Pc);
        if (toSlot)
        {
            var s = m_slots[target];
            c.Base = s.CodeBase;
            c.Pc = s.Entry;
        }
        else
        {
            c.Pc = target;
        }
        return 0;
    }

    /// <summary>The result operand of callmod: GETADR, where a constant 0 means "no result".</summary>
    private int AddressOfResult(ScnContext c, ScnOperand o) => o.Kind is 4 or 5 ? Value(c, o) : AddressOf(c, o);

    private int CompareStrings(int a, int b, bool ignoreCase)
    {
        for (int k = 0; ; k++)
        {
            int x = ReadByte(a + k), y = ReadByte(b + k);
            if (ignoreCase)
            {
                if (x is >= 'A' and <= 'Z') x += 32;
                if (y is >= 'A' and <= 'Z') y += 32;
            }
            if (x != y || x == 0)
                return x - y;
        }
    }

    private int Execute(ScnContext c, ScnInstruction ins)
    {
        if (!m_handlers.TryGetValue(ins.Op, out var handler))
            throw Error(c, $"Opcode {ins.Op:X4} is not supported yet");
        return handler(this, c, ins);
    }
}
