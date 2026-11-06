// C# Scripting/ScnVm.Events.cs: window messages and timers, as the engine's window procedure
// (0x4355B0) takes them. It runs script slots to the end (FUN_0042BF60) for them:
// - first, for every message, the slot of op_07E4 (0x4880A8), with l[0] = the window, l[1] the
//   message, l[2] wParam, l[3] lParam on its stack; a non-zero "end" means the message was taken
//   and nothing else happens;
// - then the engine's own handling: WM_CLOSE runs the slot of op_078A (0x488098) and closes the
//   window only when its "end" is not 0 (no slot: it closes); WM_ACTIVATEAPP the slots of op_076C
//   (getting the focus) / 076D (losing it); Alt+Enter the slot of op_0794; WM_TIMER the slot of
//   op_0AF0. op_0AFA / 0AFB start and stop the timers. WM_PAINT comes after op_07D0
//   (InvalidateRect), at the next pump of the message queue: the next frame here.
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

namespace {

int EventGlobal(ScnEvent e)
{
    return e == ScnEvent::ToggleFullScreen ? 0x48808C : e == ScnEvent::Deactivate ? 0x488094 : 0x488090;
}

} // namespace

int ScnVm::RunSlotSync(int slot)
{
    auto& c = m_slots[size_t(slot)];
    if (c.CodeBase == 0)
        return 1;
    int savedFlags = c.Flags;
    c.FrameTop = 0;
    c.Pc = c.Entry;
    c.Base = c.CodeBase;
    c.Flags = 1;
    for (int guard = 0; (c.Flags & 1) != 0 && !QuitRequested && guard < 1000000; guard++)
    {
        // A slot run to its end draws its 0083 text at once (text waiting for a key would never
        // end here: it is cut short)
        for (int step = 0; (c.Flags & 8) != 0 && !QuitRequested; step++)
        {
            if (step == 100000)
                c.Flags &= ~8;
            else
                StepTaskText(c);
        }
        int result = Run(c);
        if (result == 1 || result == 3)
            break;
    }
    if ((savedFlags & 1) == 0)
        c.Flags = 0;
    TasksChanged();
    return c.ExitCode;
}

void ScnVm::Notify(ScnEvent e)
{
    if (e == ScnEvent::Activate || e == ScnEvent::Deactivate)
    {
        m_inFront = e == ScnEvent::Activate;
        UpdatePause();
    }
    bool taken = e == ScnEvent::ToggleFullScreen
        ? MessageHook(ScnMessage::SysKeyDown, 0x0D, ScnMessage::Key(0x1C, true, false) | 1 << 29)
        : MessageHook(ScnMessage::ActivateApp, e == ScnEvent::Activate ? 1 : 0, 0);
    if (taken)
        return;
    int slot = EngineGlobalOr(EventGlobal(e), -1);
    if (slot >= 0 && slot < Slots)
        RunSlotSync(slot);
}

bool ScnVm::MessageHook(int message, int wParam, int lParam)
{
    int slot = EngineGlobalOr(0x4880A8, -1);
    if (slot < 0 || slot >= Slots)
        return false;
    auto& c = m_slots[size_t(slot)];
    if (c.Sp < 4)
        return false;
    c.Sp -= 4;
    Write32(StackAddress(slot, c.Sp), WindowHandle);
    Write32(StackAddress(slot, c.Sp + 1), message);
    Write32(StackAddress(slot, c.Sp + 2), wParam);
    Write32(StackAddress(slot, c.Sp + 3), lParam);
    int result = RunSlotSync(slot);
    c.Sp += 4;
    return result != 0;
}

void ScnVm::WindowMessage(int message, int wParam, int lParam)
{
    bool taken = MessageHook(message, wParam, lParam);
    if (taken)
        return;
    if (message == ScnMessage::MouseWheel)
        MouseWheel(int16_t(wParam >> 16));
}

bool ScnVm::CloseWindow()
{
    if (MessageHook(ScnMessage::Close, 0, 0))
        return false;
    int slot = EngineGlobalOr(0x488098, -1);
    if (slot < 0 || slot >= Slots)
        return true;
    return RunSlotSync(slot) != 0;
}

void ScnVm::FireTimers()
{
    if (m_timers.Count() == 0)
        return;
    uint32_t now = Clock();
    for (const auto& [id, timer] : m_timers.Items())
    {
        if (int(now - timer.second) < 0)
            continue;
        uint32_t next = timer.second + uint32_t(timer.first);
        if (int(now - next) >= 0)
            next = now + uint32_t(timer.first);
        m_timers[id] = {timer.first, next};
        if (MessageHook(ScnMessage::Timer, id, 0))
            continue;
        int slot = EngineGlobalOr(0x4880AC, -1);
        if (slot >= 0 && slot < Slots)
            RunSlotSync(slot);
    }
}

void ScnVm::SetPauseInBackground(bool value)
{
    m_pauseInBackground = value;
    UpdatePause();
}

void ScnVm::UpdatePause()
{
    bool paused = !m_inFront && (!RunsInBackground() || m_pauseInBackground);
    if (paused == m_paused)
        return;
    uint32_t now = m_host.Milliseconds();
    if (paused)
        m_pausedAt = now;
    else
        m_pausedFor += now - m_pausedAt;
    m_paused = paused;
    m_host.PauseSound(paused);
}

void ScnVm::RegisterEvents()
{
    // 0AF0 slot: the slot run on WM_TIMER
    Register(0x0AF0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x4880AC, c, i); });
    // 0AFA id, ms, v: SetTimer; v = the timer id
    Register(0x0AFA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int id = vm.Value(c, i.Args[0]), ms = std::max(1, vm.Value(c, i.Args[1]));
        vm.m_timers[id] = {ms, vm.Clock() + uint32_t(ms)};
        vm.Store(c, i.Args[2], id);
        return 0;
    });
    // 0AFB id: KillTimer (an unknown timer is a script error)
    Register(0x0AFB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.m_timers.Remove(vm.Value(c, i.Args[0])) ? 0 : 2; });
}

} // namespace openshiina::scripting
