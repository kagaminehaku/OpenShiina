// C# Scripting/ScnVm.OldEngines.cs: opcodes of the oldest engines (v2.02 to v2.20: Calendar Girl, the
// Hana no Kioku games, Kanawanu Koi no Monogatari, Hachigatsu no Nostalgia, Itsuka, Dokoka de) that the
// later games do not use, read from EDEN IV's EDEN4.EXE (v2.20, not packed; the handlers named are its
// functions). The later tables have them all with the same operands, so they are registered for every
// engine.
//
// The flag files: the old engines keep a game's state in two files of their own (0138 / 0139) and the
// game's system state in a third (013A / 013B), the variables copied whole. 0136 turns on a light
// encryption of them (FUN_00403320 / FUN_00403380).
//
// The popup menus (08A2 ... 08B6, the Foster games' right-click menu) are Windows' own, as the window
// menu is (ScnVm.Menus): the players have none, so the menus are kept as handles nothing shows and no
// item is ever chosen.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

namespace openshiina::scripting {

namespace {

// The slots the mouse buttons start (0474 / 0475; EDEN4.EXE 0x437840 / 0x43783C, -1 at start)
constexpr int LeftClickSlotGlobal = 0x437840, RightClickSlotGlobal = 0x43783C;
// 0136's switch: the flag files encrypted (EDEN4.EXE 0x43785C)
constexpr int FlagFileCryptGlobal = 0x4880D0;

// The flag file: s[0..999] (EDEN4.EXE 0x46D9C0) and a[0..999] (0x4811B8); the system flag file:
// b[0..999] (0x57C158)
std::vector<std::pair<int, int>> flagParts(ScnVm& vm) { return {{vm.SAddress(0), 1000}, {vm.AAddress(0), 4000}}; }
std::vector<std::pair<int, int>> systemParts(ScnVm& vm) { return {{vm.BAddress(0), 4000}}; }

} // namespace

void ScnVm::StartClickSlot(int message)
{
    // A mouse button went down (WM_LBUTTONDOWN / WM_RBUTTONDOWN, EDEN4.EXE 0x41B7BF / 0x41B874): the
    // slot 0474 / 0475 gave starts from its entry and runs at once until it yields (FUN_00416520),
    // then goes on as a task; nothing when it already runs or has no code
    int slot = EngineGlobalOr(message == ScnMessage::LButtonDown ? LeftClickSlotGlobal : RightClickSlotGlobal, -1);
    if (slot < 0 || slot >= Slots)
        return;
    auto& s = m_slots[size_t(slot)];
    if (s.Flags == 1 || s.CodeBase == 0)
        return;
    s.Pc = s.Entry;
    s.Base = s.CodeBase;
    s.Flags = 1;
    TasksChanged();
    if ((s.Flags & 8) == 0)
        Run(s);
}

bool ScnVm::WriteFlagFile(const std::string& path, const std::vector<std::pair<int, int>>& parts)
{
    // A flag file written (FUN_004033D0 / FUN_00403470): the bytes of each part, then 8 bytes of 0;
    // with 0136 on, encrypted as FUN_00403320 does it (a key from rand() XORed with 0xF4 into every
    // byte, the key ^ 0xB1 after them and a sum of the bytes ^ 0xD3, XORed with 0x4BC57D21, 4 bytes
    // after that). False when it could not be written
    int length = 0;
    for (auto& part : parts)
        length += part.second;
    openshiina::Bytes data(size_t(length + 8), 0);
    int at = 0;
    for (auto& [address, n] : parts)
    {
        ReadBytes(address, data.data() + at, n);
        at += n;
    }
    if (EngineGlobalOr(FlagFileCryptGlobal, 0) != 0)
    {
        uint8_t key = uint8_t(Rand() & 0xFF);
        uint32_t sum = 0;
        for (int k = 0; k < length; k++)
        {
            sum += uint32_t(data[size_t(k)] ^ 0xD3);
            data[size_t(k)] = uint8_t(data[size_t(k)] ^ key ^ 0xF4);
        }
        data[size_t(length)] = uint8_t(key ^ 0xB1);
        uint32_t check = sum ^ 0x4BC57D21u;
        std::memcpy(data.data() + length + 4, &check, 4);
    }
    return WriteScriptFile(path, data);
}

bool ScnVm::ReadFlagFile(const std::string& path, const std::vector<std::pair<int, int>>& parts)
{
    // A flag file read back (FUN_00403500 / FUN_00403590): only one of the length the engine writes
    // ("Not Flag file." else), decrypted with 0136 on (FUN_00403380, whose sum is not looked at), its
    // parts copied into the variables. False when it is not there or not one
    int length = 0;
    for (auto& part : parts)
        length += part.second;
    openshiina::Bytes data;
    if (!ReadScriptFile(path, data) || int(data.size()) != length + 8)
        return false;
    if (EngineGlobalOr(FlagFileCryptGlobal, 0) != 0)
    {
        uint8_t key = uint8_t(data[size_t(length)] ^ 0xB1);
        for (int k = 0; k < length; k++)
            data[size_t(k)] = uint8_t(data[size_t(k)] ^ key ^ 0xF4);
    }
    int at = 0;
    for (auto& [address, n] : parts)
    {
        WriteBytes(address, data.data() + at, n);
        at += n;
    }
    return true;
}

void ScnVm::CrossfadeRect(int from, int fx, int fy, int to, int tx, int ty, int x, int y, int w, int h, int t)
{
    // 050C's step: the screen's w x h at (x, y) as each byte (from * (255 - t) + to * t) / 255, from
    // surface from's at (fx, fy) (negative: the grey from & 0xFF) and surface to's at (tx, ty), the
    // three clipped together to their surfaces
    bool grey = from < 0;
    if (uint32_t(to) >= uint32_t(SurfaceCount) || (!grey && from >= SurfaceCount))
        return;
    int dst = SurfaceField(0, 2), b = SurfaceField(to, 2), a = grey ? 0 : SurfaceField(from, 2);
    if (dst == 0 || b == 0 || (!grey && a == 0))
        return;
    int bpp = SurfaceField(0, 9) >> 3;
    // Clipped where any of the three runs off its surface
    int left = std::max(std::max(-x, -tx), grey ? 0 : -fx), top = std::max(std::max(-y, -ty), grey ? 0 : -fy);
    left = std::max(left, 0);
    top = std::max(top, 0);
    x += left; tx += left; fx += left; w -= left;
    y += top; ty += top; fy += top; h -= top;
    w = std::min(w, std::min(SurfaceField(0, 7) - x, SurfaceField(to, 7) - tx));
    h = std::min(h, std::min(SurfaceField(0, 8) - y, SurfaceField(to, 8) - ty));
    if (!grey)
    {
        w = std::min(w, SurfaceField(from, 7) - fx);
        h = std::min(h, SurfaceField(from, 8) - fy);
    }
    if (w <= 0 || h <= 0)
        return;
    int bytes = w * bpp, value = from & 0xFF;
    for (int row = 0; row < h; row++)
    {
        auto rowB = ReadBytes(b + (ty + row) * SurfaceField(to, 10) + tx * bpp, bytes);
        openshiina::Bytes rowA = grey ? openshiina::Bytes() : ReadBytes(a + (fy + row) * SurfaceField(from, 10) + fx * bpp, bytes);
        for (int k = 0; k < bytes; k++)
            rowB[size_t(k)] = uint8_t(((grey ? value : rowA[size_t(k)]) * (255 - t) + rowB[size_t(k)] * t) / 255);
        WriteBytes(dst + (y + row) * SurfaceField(0, 10) + x * bpp, rowB);
    }
}

void ScnVm::RegisterOldEngines()
{
    // 0474 slot / 0475 slot: the slot the left / right mouse button starts (FUN_00410850 /
    // FUN_00410890; -1: none)
    Register(0x0474, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(LeftClickSlotGlobal, c, i); });
    Register(0x0475, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(RightClickSlotGlobal, c, i); });
    // 0138 name / 0139 name: the flag file read / written; 013A name / 013B name: the system flag
    // file (FUN_0040C050 ... FUN_0040C0E0); a script error (2) when it fails
    Register(0x0138, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.ReadFlagFile(vm.ReadString(vm.Value(c, i.Args[0])), flagParts(vm)) ? 0 : 2;
    });
    Register(0x0139, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.WriteFlagFile(vm.ReadString(vm.Value(c, i.Args[0])), flagParts(vm)) ? 0 : 2;
    });
    Register(0x013A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.ReadFlagFile(vm.ReadString(vm.Value(c, i.Args[0])), systemParts(vm)) ? 0 : 2;
    });
    Register(0x013B, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        return vm.WriteFlagFile(vm.ReadString(vm.Value(c, i.Args[0])), systemParts(vm)) ? 0 : 2;
    });
    // 03CA lo, hi / 03CB lo, hi: QueryPerformanceCounter / QueryPerformanceFrequency (FUN_00410580 /
    // FUN_004105D0), here a counter of microseconds on the scripts' clock
    Register(0x03CA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int64_t now = int64_t(vm.Clock()) * 1000;
        vm.Store(c, i.Args[0], int(now));
        vm.Store(c, i.Args[1], int(now >> 32));
        return 0;
    });
    Register(0x03CB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], 1000000);
        vm.Store(c, i.Args[1], 0);
        return 0;
    });
    // 050C x, y, from, fx, fy, w, h, to, tx, ty, ms, flags (FUN_0040CC40 -> FUN_00406A20): 050A on a
    // rectangle: the screen's w x h at (x, y) goes from surface from's at (fx, fy) (with bit 31: the
    // grey from & 0xFF) to surface to's at (tx, ty) over ms, the rectangles clipped to the screen
    // first (FUN_0040C700). Itsuka, Dokoka de's START fades its PDA's buttons so
    Register(0x050C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a[12];
        for (int k = 0; k < 12; k++)
            a[k] = vm.Value(c, i.Args[size_t(k)]);
        int ms = a[10];
        auto found = vm.m_crossfades.find(c.Slot);
        if (found == vm.m_crossfades.end())
            found = vm.m_crossfades.emplace(c.Slot, vm.Clock()).first;
        uint32_t passed = vm.Clock() - found->second;
        int t = ms <= 0 || passed >= uint32_t(ms) ? 255 : int(passed * 255 / uint32_t(ms));
        vm.CrossfadeRect(a[2], a[3], a[4], a[7], a[8], a[9], a[0], a[1], a[5], a[6], t);
        vm.InvalidateWindow(a[0], a[1], a[0] + a[5], a[1] + a[6]);
        vm.FrameShown = true;
        if (t < 255)
        {
            c.Pc = c.Current;
            return 3;
        }
        vm.m_crossfades.erase(c.Slot);
        return 0;
    });
    // 053C dx, dy, l, t, r, b, cl, ct, cr, cb: ScrollWindowEx of the window by (dx, dy) in the
    // rectangle, clipped to the second, the part left behind to be painted (FUN_00413390). The players
    // paint the window from the screen surface whole: the rectangle is painted again
    Register(0x053C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int a[10];
        for (int k = 0; k < 10; k++)
            a[k] = vm.Value(c, i.Args[size_t(k)]);
        vm.InvalidateWindow(a[2], a[3], a[4], a[5]);
        return 0;
    });
    // 0119 folder, name: the file name (of the archives) copied to folder\name (FUN_00412C80 ->
    // FUN_00402EE0; Itsuka, Dokoka de's RESULT.SCN gives its voices to a folder of the player's,
    // present\). Only a save folder is written to; elsewhere the copy is left out
    Register(0x0119, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string folder = vm.ReadString(vm.Value(c, i.Args[0])), name = vm.ReadString(vm.Value(c, i.Args[1]));
        openshiina::Bytes data;
        if (!vm.ReadScriptFile(name, data))
            return 2;
        if (folder.empty() && name.empty())
            return 2;
        std::string path = !folder.empty() && folder.back() != '\\' && folder.back() != '/' ? folder + "\\" + name : folder + name;
        auto resolved = vm.ResolvePath(path);
        if (resolved && resolved->Save)
            return vm.WriteScriptFile(path, data) ? 0 : 2;
        return 0;
    });
    // 0191 name: the function name of the library 0190 loaded called with the addresses of b[], a[],
    // s[], the task's f[] and its stack (FUN_00410A10; 2 without a library). Itsuka, Dokoka de's
    // SUBLIB.DLL has one, GetFreeDiskSpace: the free bytes of the drive of the path at b[990] into
    // b[990] (low) and b[991] (high) - here 64 GB, as GetDiskFreeSpaceExA
    Register(0x0191, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string function = vm.ReadString(vm.Value(c, i.Args[0]));
        if (function != "GetFreeDiskSpace")
            throw vm.Error(c, "The scripts call the library function " + function + ", which is not supported yet");
        int64_t free = int64_t(64) << 30;
        vm.SetB(990, int(free));
        vm.SetB(991, int(free >> 32));
        return 0;
    });
    // 0A3D folders: more folders (';' between them, 16 in all) whose archives are looked in
    // (FUN_00412A30 -> FUN_00404200): Mahou no Shippo na Dai San wa adds the first two episodes',
    // EDEN V EDEN IV's, as the registry gives their folders. Only the game's own folder is looked in here
    Register(0x0A3D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        return 0;
    });
    // ---- Of the engines 2.34 to 2.39 too (Ao no Juuai's aoj.EXE, Nagagutsu wo Haita Deco's DECO.EXE,
    // used by Sabae no Ou and Deco) ----

    // 0064 l, t, r, b / 0065 v: the box and the bytes (a string, at the address v) of the character
    // just drawn, which the engine keeps for the record's after-character slot (aoj.EXE 0x40E330 /
    // 0x40E3C0, kept at 0x4F7EF4 by its text step)
    Register(0x0064, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (size_t k = 0; k < 4; k++)
            vm.Store(c, i.Args[k], vm.m_charBox[k]);
        return 0;
    });
    Register(0x0065, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int at = vm.Value(c, i.Args[0]);
        vm.WriteBytes(at, vm.m_charText);
        vm.WriteByte(at + int(vm.m_charText.size()), 0);
        return 0;
    });
    // 007A x, y, left: the task's text record goes on at (x, y), lines starting at left
    Register(0x007A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]), left = vm.Value(c, i.Args[2]);
        vm.RecordPosition(c, x, y, left);
        return 0;
    });
    // 00AA r, g, b / 00AE r, g, b: the record's text / background colour, taken at the next character
    Register(0x00AA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.RecordColor(c, i, false); });
    Register(0x00AE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.RecordColor(c, i, true); });
    // 06EB s, v: v = how long the stream has played, in ms (aoj.EXE 0x41D660 -> FUN_0043F880: the
    // bytes its buffer played * 1000 / its bytes a second; -1 without one), on the scripts' clock,
    // its pauses left out
    Register(0x06EB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        int ms = -1;
        if (s)
            ms = !s->Played ? 0 : int(((s->Status & 4) != 0 ? s->PausedAt : vm.Clock()) - s->PlayedAt - s->PausedFor);
        vm.Store(c, i.Args[1], ms);
        return 0;
    });
    // 077C window, after, x, y, w, h, flags: SetWindowPos (DECO.EXE 0x421FBE; Deco moves its window
    // so). The host places the window
    Register(0x077C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (size_t k = 0; k < 7; k++)
            vm.Value(c, i.Args[k]);
        return 0;
    });

    // 08A2 v: CreatePopupMenu (FUN_00411720): v = a new menu
    Register(0x08A2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.m_nextMenu++);
        return 0;
    });
    // 08B6 menu, flags, x, y: TrackPopupMenu at (x, y) of the window (FUN_00411810), then the
    // engine's message 0x7E8 that the menu closed. Nothing is shown: no item chosen
    Register(0x08B6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (size_t k = 0; k < 4; k++)
            vm.Value(c, i.Args[k]);
        return 0;
    });
}

} // namespace openshiina::scripting
