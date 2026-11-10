// C# Scripting/ScnVm.Menus.cs: opcodes the v2.49 executable has and only v2.50 games use, read from
// a v2.49 executable (the handlers named are its functions).
//
// The window menu (0898 / 08AC / 08C0 / 08E8 / 08F2 / 08FC) is Windows' own: the engine makes a
// menu bar on the window, and a chosen item runs the slot 08AC registered for its id. The players
// have no menu bar, so the menus are kept as handles nothing shows and no item is ever chosen.
#include "scripting/ScnVm.h"

#include <functional>

namespace openshiina::scripting {

namespace {

/// The VC6 qsort of 0294 over count elements of width bytes from lo.
void SortRange(int lo, int count, int width, const std::function<int(int, int)>& Compare,
               const std::function<void(int, int)>& Swap)
{
    std::vector<std::pair<int, int>> stack;
    int hi = lo + (count - 1) * width;
    while (true)
    {
        uint32_t size = uint32_t(hi - lo) / uint32_t(width) + 1;
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
            int mid = lo + int(size / 2) * width;
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
                    stack.emplace_back(lo, down - width);
                if (up < hi)
                {
                    lo = up;
                    continue;
                }
            }
            else
            {
                if (up < hi)
                    stack.emplace_back(up, hi);
                if (lo + width < down)
                {
                    hi = down - width;
                    continue;
                }
            }
        }
        if (stack.empty())
            return;
        std::tie(lo, hi) = stack.back();
        stack.pop_back();
    }
}

} // namespace

bool ScnVm::RunsInBackground()
{
    // RIO.INI's Background (0xC51060; Oreimo Plus 0x13B43D0), read at start, then as 07E5 / 07E6
    // set it: the game goes on, sound included, while another window is in front; without it the
    // game stands still (Paused)
    if (!m_background)
        m_background = IniInt("RIO.INI", RioSection(), "Background", 0) != 0;
    return *m_background;
}

void ScnVm::SetRunsInBackground(bool value)
{
    m_background = value;
    UpdatePause();
}

void ScnVm::RegisterMenus()
{
    // 06CC v: v = Direct3D 9 is there (FUN_00418AA0 -> FUN_00403C70); the v2.50 games stop with
    // "Direct3D Error" without it. The picture is made here, so: there
    Register(0x06CC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], 1);
        return 0;
    });
    // 07E5 / 07E6: Background on / off
    Register(0x07E5, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        vm.SetRunsInBackground(true);
        return 0;
    });
    Register(0x07E6, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        vm.SetRunsInBackground(false);
        return 0;
    });
    // 0A00 w, h, flags (FUN_00426440 -> FUN_004260A0): the engine's screen becomes w x h at bpp
    // flags & 0xFF (0: 24); bit 31 keeps the surfaces, bit 30 sizes the window too. The v2.50
    // games ask for the size RIO.INI gave at start; Nukitashi 2's window size setting asks for
    // another and keeps the surfaces: the screen is stretched into that window, and the scripts
    // take the mouse (0456, 0457) and the parts to show (07D0, full screen) in the window's
    // pixels. The host still shows the screen as it does: the window's size only changes those
    Register(0x0A00, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int w = vm.Value(c, i.Args[0]), h = vm.Value(c, i.Args[1]);
        vm.Value(c, i.Args[2]);
        if (w > 0 && h > 0)
        {
            vm.ClientWidthSet = w;
            vm.ClientHeightSet = h;
        }
        return 0;
    });
    // 09DD index, v: GetSystemMetrics (FUN_00425F80), as a 1920 x 1080 screen (09E2 too)
    Register(0x09DD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int value;
        switch (vm.Value(c, i.Args[0]))
        {
        case 0:
        case 16: value = 1920; break;        // SM_CXSCREEN, SM_CXFULLSCREEN
        case 1: value = 1080; break;         // SM_CYSCREEN
        case 17: value = 1080 - 23; break;   // SM_CYFULLSCREEN
        case 4: value = 23; break;           // SM_CYCAPTION
        case 5:
        case 6: value = 1; break;            // SM_CXBORDER, SM_CYBORDER
        case 7:
        case 8: value = 3; break;            // SM_CXFIXEDFRAME, SM_CYFIXEDFRAME
        case 15: value = 20; break;          // SM_CYMENU
        case 32:
        case 33: value = 4; break;           // SM_CXFRAME, SM_CYFRAME
        default: value = 0; break;
        }
        vm.Store(c, i.Args[1], value);
        return 0;
    });

    // 0898 v: CreateMenu (FUN_00424910): v = a new menu
    Register(0x0898, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.m_nextMenu++);
        return 0;
    });
    // 08AC menu, flags, id, text, slot: AppendMenuA, and the item's id runs the slot
    // (FUN_00424950: a table of 256 id / slot pairs at 0x8184E8)
    Register(0x08AC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (int k = 0; k < 5; k++)
            vm.Value(c, i.Args[k]);
        return 0;
    });
    // 08C0 menu: the window's menu bar (FUN_00424AC0: SetMenu when not full screen); 08F2 menu:
    // DestroyMenu (FUN_00424C40)
    Register(0x08C0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        return 0;
    });
    Register(0x08F2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        return 0;
    });
    // 08E8 menu, item, flags: CheckMenuItem (FUN_00424C10)
    Register(0x08E8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (int k = 0; k < 3; k++)
            vm.Value(c, i.Args[k]);
        return 0;
    });
    // 08FC a, height: the menu bar's settings (FUN_00424D10; height -1: SM_CYMENU)
    Register(0x08FC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        vm.Value(c, i.Args[1]);
        return 0;
    });
    // 0014 v: v = the value the task was started with (ctx+0: the id of the menu item that ran
    // it). No item is ever chosen here: 0
    Register(0x0014, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], 0);
        return 0;
    });

    // 030C a, b, v: v = the 16-bit word at a + b (FUN_004217F0)
    Register(0x030C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[2], vm.Read16(a + b));
        return 0;
    });
    // 030D a, b, v: the 16-bit word at a + b = v (FUN_00422240)
    Register(0x030D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a = vm.Value(c, i.Args[0]), b = vm.Value(c, i.Args[1]);
        vm.Write16(a + b, vm.Value(c, i.Args[2]));
        return 0;
    });
    // 0390 x, y: x and y swapped (FUN_004222B0)
    Register(0x0390, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[0], y);
        vm.Store(c, i.Args[1], x);
        return 0;
    });
    // 09EC window, l, t, r, b: GetWindowRect of the window (0: the game's; FUN_00426A60). The host
    // makes the window around the picture: the screen's size at (0, 0) (Sorcery Jokers' hook for a
    // window being sized reads it)
    Register(0x09EC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        vm.Store(c, i.Args[1], 0);
        vm.Store(c, i.Args[2], 0);
        vm.Store(c, i.Args[3], vm.ScreenWidth);
        vm.Store(c, i.Args[4], vm.ScreenHeight);
        return 0;
    });
    // 0A06 on: whether the window can be sized by its frame (WS_THICKFRAME, RIO.INI's FreeWindow;
    // FUN_00426F40): the host's window is sized as the player's settings say
    Register(0x0A06, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        return 0;
    });
    // 0395 v: v = ~v (FUN_004219B0)
    Register(0x0395, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], ~vm.Value(c, i.Args[0]));
        return 0;
    });
    // 0BCC s, ch, v: v = strchr(s, ch) (FUN_004286C0 -> FUN_0044E340): the address of the first ch
    // in s, the terminator's for ch 0, else 0
    Register(0x0BCC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int s = vm.Value(c, i.Args[0]);
        uint8_t ch = uint8_t(vm.Value(c, i.Args[1]));
        int found = 0;
        for (int p = s;; p++)
        {
            uint8_t b = uint8_t(vm.ReadByte(p));
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
    // 012D folder, a, b: MoveFileA(folder\a, folder\b) (FUN_00425C40; FUN_00414800 adds the '\'):
    // a save file renamed; nothing happens when a is missing or b is there
    Register(0x012D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string folder = vm.ReadString(vm.Value(c, i.Args[0]));
        std::string a = vm.ReadString(vm.Value(c, i.Args[1])), b = vm.ReadString(vm.Value(c, i.Args[2]));
        bool separated = folder.empty() || SjisEndsWithIgnoreCase(folder, "\\") || SjisEndsWithIgnoreCase(folder, "/");
        auto join = [&](const std::string& name) { return separated ? folder + name : folder + "\\" + name; };
        if (!vm.FileWritesEnabled)
            return 0;
        auto from = vm.ResolvePath(join(a)), to = vm.ResolvePath(join(b));
        openshiina::Bytes data, existing;
        if (from && from->Save && to && to->Save && vm.ReadSave(from->Name, data) && !vm.ReadSave(to->Name, existing))
        {
            vm.WriteSave(to->Name, data);
            vm.DeleteSave(from->Name);
        }
        return 0;
    });
    // 0294 base, count, width, slot: sorts count elements of width bytes (FUN_004174E0)
    Register(0x0294, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int start = vm.Value(c, i.Args[0]), count = vm.Value(c, i.Args[1]);
        int width = vm.Value(c, i.Args[2]), slot = vm.Value(c, i.Args[3]);
        if (slot < 0 || slot >= Slots)
            return 2;
        vm.SortElements(start, count, width, slot);
        return 0;
    });
}

int ScnVm::Compare16(int x, int y)
{
    // 16 bytes against 16 as signed bytes (Re: Rem Plus START 8BBD0, the C++ compare16): -1, 0 or 1
    for (int k = 0; k < 16; k++)
    {
        int8_t p = int8_t(ReadByte(x + k)), q = int8_t(ReadByte(y + k));
        if (p != q)
            return p > q ? 1 : -1;
    }
    return 0;
}

bool ScnVm::IsCompare16Slot(int slot)
{
    // Whether the slot is "0276 <compare16>" then "end f[0]"
    auto& c = m_slots[size_t(slot)];
    if (c.CodeBase == 0)
        return false;
    int savedBase = c.Base;
    c.Base = c.CodeBase;
    bool result = false;
    try
    {
        std::unique_ptr<ScnInstruction> transientCall, transientEnd;
        const auto& call = Decode(c, c.Entry, transientCall);
        if (call.Op == 0x0276 && call.Args.size() == 1)
        {
            auto& routine = RoutineAt(Value(c, call.Args[0]));
            if (routine.Native && routine.Native->Name == "compare16")
            {
                const auto& end = Decode(c, c.Entry + call.Length, transientEnd);
                if (end.Op == 0x0000 && end.Args.size() == 1)
                {
                    const auto& o = end.Args[0];
                    result = !o.AddressOf && !o.Relative && (o.Kind & 1) == 0 && o.Kind != 4 && o.Kind != 6
                             && o.Kind != 0x10 && o.Kind != 0x12 && AddressOf(c, o) == FAddress(slot, 0);
                }
            }
        }
    }
    catch (const ScnException&)
    {
        result = false;
    }
    c.Base = savedBase;
    return result;
}

void ScnVm::SortElements(int lo, int count, int width, int slot)
{
    // The engine's qsort (FUN_004174E0): Visual C++ 6's, with its own stack of ranges and a
    // selection sort for 8 elements or fewer (FUN_00417360); the comparison runs a slot to its end
    // with l[0] the element in question and l[1] the one it is compared with, its "end" the result
    // (a slot without code: 1). The engine calls it once more after a scan has run off its range
    // and drops the result; that call is left out (the slot only answers)
    if (uint32_t(count) < 2 || width == 0)
        return;
    std::vector<uint8_t> held(static_cast<size_t>(uint32_t(width))), moved(static_cast<size_t>(uint32_t(width)));
    auto Swap = [&](int a, int b) {
        ReadBytes(a, held.data(), width);
        ReadBytes(b, moved.data(), width);
        WriteBytes(a, moved.data(), width);
        WriteBytes(b, held.data(), width);
    };
    // Re: Rem Plus and Maki Fes! sort with a slot that is "0276 compare16 / end f[0]": the compare
    // is done here at once (1.7 million runs of the slot took 0.9 s at start), and what the slot
    // leaves behind (l[0], l[1], f[0], its end) is written for the last one
    bool direct = IsCompare16Slot(slot);
    int last = 0, lastElement = 0, lastWith = 0;
    auto Compare = [&](int element, int with) {
        auto& c = m_slots[size_t(slot)];
        if (c.CodeBase == 0 || c.Sp < 2)
            return 1;
        if (direct)
        {
            lastElement = element;
            lastWith = with;
            return last = Compare16(element, with);
        }
        c.Sp -= 2;
        Write32(StackAddress(slot, c.Sp), element);
        Write32(StackAddress(slot, c.Sp + 1), with);
        // Its "end" is the answer, not the end of the game (Run takes a non-zero end for that)
        bool quit = QuitRequested;
        int result = RunSlotSync(slot);
        QuitRequested = quit;
        c.Sp += 2;
        return result;
    };
    auto finish = [&] {
        if (direct && lastElement != 0)
        {
            auto& c = m_slots[size_t(slot)];
            Write32(StackAddress(slot, c.Sp - 2), lastElement);
            Write32(StackAddress(slot, c.Sp - 1), lastWith);
            Write32(FAddress(slot, 0), last);
            c.ExitCode = last;
        }
    };
    try
    {
        SortRange(lo, count, width, Compare, Swap);
    }
    catch (...)
    {
        finish();
        throw;
    }
    finish();
}

} // namespace openshiina::scripting
