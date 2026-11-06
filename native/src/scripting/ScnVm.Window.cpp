// C# Scripting/ScnVm.Window.cs: window and system opcodes (07xx). On Windows the engine calls
// user32 directly; here the title and full screen go to the host, and the calls that only make
// sense for a Win32 window (find a window, class styles, posted messages, repaints) answer the way
// a single running copy of the game would see them.
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

int ScnVm::EngineGlobal(int address) const
{
    return EngineGlobalOr(address, 0);
}

int ScnVm::EngineGlobalOr(int address, int otherwise) const
{
    auto found = EngineGlobals.find(address);
    return found == EngineGlobals.end() ? otherwise : found->second;
}

int ScnVm::SetGlobal(int address, ScnContext& c, const ScnInstruction& i)
{
    EngineGlobals[address] = Value(c, i.Args[0]);
    return 0;
}

int ScnVm::WindowMessage(ScnContext& c, const ScnInstruction& i)
{
    Value(c, i.Args[0]);
    int message = Value(c, i.Args[1]);
    Value(c, i.Args[2]);
    Value(c, i.Args[3]);
    // WM_CLOSE / WM_DESTROY to the game window end the game
    if (message == 0x10 || message == 0x02)
        QuitRequested = true;
    return 0;
}

void ScnVm::RegisterWindow()
{
    // 07EE hwnd, index, v: GetClassLong (0 = the game window); 07EF hwnd, index, value: SetClassLong
    Register(0x07EE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        int index = vm.Value(c, i.Args[1]);
        auto found = vm.m_classLongs.find(index);
        vm.Store(c, i.Args[2], found != vm.m_classLongs.end() ? found->second : index == -26 ? 0x0B : 0);
        return 0;
    });
    Register(0x07EF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        int index = vm.Value(c, i.Args[1]);
        vm.m_classLongs[index] = vm.Value(c, i.Args[2]);
        return 0;
    });
    // 07B2 class, title, v: FindWindow - no other copy of the game is running
    Register(0x07B2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[2], 0);
        return 0;
    });
    // 07B3 v: the game window's handle
    Register(0x07B3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], WindowHandle); return 0; });
    // 07BC / 07BD hwnd, message, wparam, lparam: PostMessage / SendMessage
    Register(0x07BC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.WindowMessage(c, i); });
    Register(0x07BD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.WindowMessage(c, i); });
    // 07D1: UpdateWindow - WM_PAINT now if part of the window is invalid (counted by task for 03BD)
    Register(0x07D1, [](ScnVm& vm, ScnContext& c, const ScnInstruction&) {
        int before = vm.m_paints;
        vm.Paint();
        if (vm.m_paints != before)
            vm.m_taskPaints[c.Slot]++;
        return 0;
    });
    // 07D2 l, t, r, b: ValidateRect - nothing to do
    Register(0x07D2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (const auto& a : i.Args)
            vm.Value(c, a);
        return 0;
    });
    // 07A8 hwnd, text: SetWindowTextA (0 is the game window): the title, or a control's text
    Register(0x07A8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int hwnd = vm.Value(c, i.Args[0]);
        std::string text = vm.ReadString(vm.Value(c, i.Args[1]));
        if (vm.m_controls.count(hwnd))
            vm.m_controls[hwnd] = text;
        else if (hwnd == 0 || hwnd == WindowHandle)
        {
            vm.m_windowTitle = text;
            vm.m_host.SetTitle(text);
        }
        return 0;
    });
    // 07A9 hwnd, buffer, size: GetWindowTextA: at most size - 1 bytes and a 0
    Register(0x07A9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int hwnd = vm.Value(c, i.Args[0]), buffer = vm.Value(c, i.Args[1]), size = vm.Value(c, i.Args[2]);
        std::string text;
        if (auto found = vm.m_controls.find(hwnd); found != vm.m_controls.end())
            text = found->second;
        else if (hwnd == 0 || hwnd == WindowHandle)
            text = vm.m_windowTitle;
        if (size > 0)
        {
            int n = std::min(int(text.size()), size - 1);
            vm.WriteBytes(buffer, reinterpret_cast<const uint8_t*>(text.data()), n);
            vm.WriteByte(buffer + n, 0);
        }
        return 0;
    });
    // 0960 text, x, y, w, h, v: an edit box on the window (at most 256 controls), v its handle;
    // 097E hwnd: it goes. The players show no box yet: its text is what the scripts set
    Register(0x0960, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string text = vm.ReadString(vm.Value(c, i.Args[0]));
        for (int k = 1; k <= 4; k++)
            vm.Value(c, i.Args[size_t(k)]);
        if (int(vm.m_controls.size()) >= MaxControls)
            return 2;
        int hwnd = ControlHandle;
        while (vm.m_controls.count(hwnd))
            hwnd++;
        vm.m_controls[hwnd] = text;
        vm.Store(c, i.Args[5], hwnd);
        return 0;
    });
    Register(0x097E, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.m_controls.erase(vm.Value(c, i.Args[0]));
        return 0;
    });
    // 0778 on: full screen on / off; 0776: full screen off
    Register(0x0778, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.m_host.SetFullScreen(vm.Value(c, i.Args[0]) != 0);
        vm.InvalidateWindow();
        return 0;
    });
    Register(0x0776, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        vm.m_host.SetFullScreen(false);
        vm.InvalidateWindow();
        return 0;
    });
    // 079E: the window gets a maximise box; 079F: it loses it
    Register(0x079E, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
    Register(0x079F, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
    // Engine settings kept in globals
    Register(0x076C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x488090, c, i); });
    Register(0x076D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x488094, c, i); });
    Register(0x078A, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x488098, c, i); });
    Register(0x0794, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x48808C, c, i); });
    Register(0x07E4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x4880A8, c, i); });
    Register(0x0136, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x4880D0, c, i); });
}

} // namespace openshiina::scripting
