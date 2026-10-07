// Opcodes the v2.49 executable has and only the v2.50 games use (Re:Rem Plus, Maki Fes!), read
// from Sena Plus's executable (the handlers named are its functions).
//
// The window menu (0898 / 08AC / 08C0 / 08E8 / 08F2 / 08FC) is Windows' own: the engine makes a
// menu bar on the window, and a chosen item runs the slot 08AC registered for its id. The players
// have no menu bar, so the menus are kept as handles nothing shows and no item is ever chosen.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>
    /// 07E5 / 07E6: RIO.INI's Background (0xC51060) as the scripts set it: the game goes on, sound
    /// included, while another window is in front (the players go on in the background anyway).
    /// </summary>
    public bool RunsInBackground { get; private set; }

    // Handles of the menus 0898 made
    private int m_nextMenu = 0x00510000;

    private void RegisterMenus()
    {
        // 06CC v: v = Direct3D 9 is there (FUN_00418AA0 -> FUN_00403C70); the v2.50 games stop with
        // "Direct3D Error" without it. The picture is made here, so: there
        Register(0x06CC, (vm, c, i) => { vm.Store(c, i.Args[0], 1); return 0; });
        // 07E5 / 07E6: Background on / off
        Register(0x07E5, (vm, c, i) => { vm.RunsInBackground = true; return 0; });
        Register(0x07E6, (vm, c, i) => { vm.RunsInBackground = false; return 0; });
        // 0A00 w, h, flags (FUN_00426440 -> FUN_004260A0): the engine's screen becomes w x h at
        // bpp flags & 0xFF (0: 24); bit 31 keeps the surfaces, bit 30 sizes the window too. The
        // v2.50 games ask for the size RIO.INI gave at start (and others only from their window
        // size menu, which is not there)
        Register(0x0A00, (vm, c, i) =>
        {
            int w = vm.Value(c, i.Args[0]), h = vm.Value(c, i.Args[1]);
            vm.Value(c, i.Args[2]);
            if (w != vm.ScreenWidth || h != vm.ScreenHeight)
                vm.Trace?.Add($"f{vm.m_frameNumber} 0A00 asks for a {w} x {h} screen; it stays {vm.ScreenWidth} x {vm.ScreenHeight}");
            return 0;
        });
        // 09DD index, v: GetSystemMetrics (FUN_00425F80), as a 1920 x 1080 screen (09E2 too)
        Register(0x09DD, (vm, c, i) =>
        {
            int index = vm.Value(c, i.Args[0]);
            vm.Store(c, i.Args[1], index switch
            {
                0 or 16 => 1920,        // SM_CXSCREEN, SM_CXFULLSCREEN
                1 => 1080,              // SM_CYSCREEN
                17 => 1080 - 23,        // SM_CYFULLSCREEN
                4 => 23,                // SM_CYCAPTION
                5 or 6 => 1,            // SM_CXBORDER, SM_CYBORDER
                7 or 8 => 3,            // SM_CXFIXEDFRAME, SM_CYFIXEDFRAME
                15 => 20,               // SM_CYMENU
                32 or 33 => 4,          // SM_CXFRAME, SM_CYFRAME
                _ => 0,
            });
            return 0;
        });

        // 0898 v: CreateMenu (FUN_00424910): v = a new menu
        Register(0x0898, (vm, c, i) => { vm.Store(c, i.Args[0], vm.m_nextMenu++); return 0; });
        // 08AC menu, flags, id, text, slot: AppendMenuA, and the item's id runs the slot
        // (FUN_00424950: a table of 256 id / slot pairs at 0x8184E8)
        Register(0x08AC, (vm, c, i) =>
        {
            for (int k = 0; k < 5; k++)
                vm.Value(c, i.Args[k]);
            return 0;
        });
        // 08C0 menu: the window's menu bar (FUN_00424AC0: SetMenu when not full screen); 08F2 menu:
        // DestroyMenu (FUN_00424C40)
        Register(0x08C0, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        Register(0x08F2, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        // 08E8 menu, item, flags: CheckMenuItem (FUN_00424C10)
        Register(0x08E8, (vm, c, i) =>
        {
            for (int k = 0; k < 3; k++)
                vm.Value(c, i.Args[k]);
            return 0;
        });
        // 08FC a, height: the menu bar's settings (FUN_00424D10; height -1: SM_CYMENU)
        Register(0x08FC, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            vm.Value(c, i.Args[1]);
            return 0;
        });
        // 0014 v: v = the value the task was started with (ctx+0: the id of the menu item that
        // ran it). No item is ever chosen here: 0
        Register(0x0014, (vm, c, i) => { vm.Store(c, i.Args[0], 0); return 0; });

        // 030C a, b, v: v = the 16-bit word at a + b (FUN_004217F0)
        Register(0x030C, (vm, c, i) =>
        {
            int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], vm.Read16(a + b));
            return 0;
        });
        // 0395 v: v = ~v (FUN_004219B0)
        Register(0x0395, (vm, c, i) => { vm.Store(c, i.Args[0], ~vm.Value(c, i.Args[0])); return 0; });
        // 0BCC s, ch, v: v = strchr(s, ch) (FUN_004286C0 -> FUN_0044E340): the address of the
        // first ch in s, the terminator's for ch 0, else 0
        Register(0x0BCC, (vm, c, i) =>
        {
            int s = vm.Value(c, i.Args[0]);
            byte ch = (byte)vm.Value(c, i.Args[1]);
            int found = 0;
            for (int p = s; ; p++)
            {
                byte b = vm.ReadByte(p);
                if (b == ch)
                {
                    found = p;
                    break;
                }
                if (b == 0)
                    break;
            }
            vm.Store(c, i.Args[2], found);
            return 0;
        });
        // 012D folder, a, b: MoveFileA(folder\a, folder\b) (FUN_00425C40; FUN_00414800 adds the
        // '\'): a save file renamed; nothing happens when a is missing or b is there
        Register(0x012D, (vm, c, i) =>
        {
            string folder = vm.ReadString(vm.Value(c, i.Args[0]));
            string a = vm.ReadString(vm.Value(c, i.Args[1])), b = vm.ReadString(vm.Value(c, i.Args[2]));
            string Join(string name) => folder.Length > 0 && folder[^1] is not ('\\' or '/') ? folder + "\\" + name : folder + name;
            if (vm.FileWritesEnabled && vm.ResolvePath(Join(a)) is { Save: true } from && vm.ResolvePath(Join(b)) is { Save: true } to
                && vm.m_host.ReadSaveFile(from.Name) is { } data && vm.m_host.ReadSaveFile(to.Name) == null)
            {
                vm.m_host.WriteSaveFile(to.Name, data);
                vm.m_host.DeleteSaveFile(from.Name);
            }
            return 0;
        });
        // 0294 base, count, width, slot: sorts count elements of width bytes (FUN_004174E0)
        Register(0x0294, (vm, c, i) =>
        {
            int start = vm.Value(c, i.Args[0]), count = vm.Value(c, i.Args[1]);
            int width = vm.Value(c, i.Args[2]), slot = vm.Value(c, i.Args[3]);
            if (slot is < 0 or >= Slots)
                return 2;
            vm.SortElements(start, count, width, slot);
            return 0;
        });
    }

    /// <summary>
    /// The engine's qsort (FUN_004174E0): Visual C++ 6's, with its own stack of ranges and a
    /// selection sort for 8 elements or fewer (FUN_00417360); the comparison runs a slot to its
    /// end with l[0] the element in question and l[1] the one it is compared with, its "end" the
    /// result (a slot without code: 1). The engine calls it once more after a scan has run off
    /// its range and drops the result; that call is left out (the slot only answers).
    /// </summary>
    private void SortElements(int lo, int count, int width, int slot)
    {
        if ((uint)count < 2 || width == 0)
            return;
        var swap = new byte[width];
        var other = new byte[width];
        void Swap(int a, int b)
        {
            ReadBytes(a, swap);
            ReadBytes(b, other);
            WriteBytes(a, other);
            WriteBytes(b, swap);
        }
        int Compare(int element, int with)
        {
            var c = m_slots[slot];
            if (c.CodeBase == 0 || c.Sp < 2)
                return 1;
            c.Sp -= 2;
            Write32(StackAddress(slot, c.Sp), element);
            Write32(StackAddress(slot, c.Sp + 1), with);
            // Its "end" is the answer, not the end of the game (Run takes a non-zero end for that)
            bool quit = QuitRequested;
            int result = RunSlotSync(slot);
            QuitRequested = quit;
            c.Sp += 2;
            return result;
        }

        var stack = new Stack<(int Lo, int Hi)>();
        int hi = lo + (count - 1) * width;
        while (true)
        {
            uint size = (uint)(hi - lo) / (uint)width + 1;
            if (size <= 8)
            {
                // Selection sort: the greatest to the end, again and again
                for (int top = hi; top > lo; top -= width)
                {
                    int max = lo;
                    for (int p = lo + width; p <= top; p += width)
                        if (Compare(p, max) > 0)
                            max = p;
                    if (max != top)
                        Swap(max, top);
                }
            }
            else
            {
                int mid = lo + (int)(size / 2) * width;
                if (mid != lo)
                    Swap(mid, lo);
                int up = lo, down = hi + width;
                while (true)
                {
                    do
                        up += width;
                    while (up <= hi && Compare(up, lo) <= 0);
                    do
                        down -= width;
                    while (down > lo && Compare(down, lo) >= 0);
                    if (up > down)
                        break;
                    if (up != down)
                        Swap(up, down);
                }
                if (down != lo)
                    Swap(lo, down);
                if (down - 1 - lo >= hi - up)
                {
                    if (lo + width < down)
                        stack.Push((lo, down - width));
                    if (up < hi)
                    {
                        lo = up;
                        continue;
                    }
                }
                else
                {
                    if (up < hi)
                        stack.Push((up, hi));
                    if (lo + width < down)
                    {
                        hi = down - width;
                        continue;
                    }
                }
            }
            if (!stack.TryPop(out var range))
                return;
            (lo, hi) = range;
        }
    }
}
