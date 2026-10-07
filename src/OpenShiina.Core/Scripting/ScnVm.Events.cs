// Window messages and timers, as the engine's window procedure (0x4355B0) takes them. It runs
// script slots to the end (FUN_0042BF60) for them:
// - first, for every message, the slot of op_07E4 (0x4880A8), with l[0] = the window, l[1] the
//   message, l[2] wParam, l[3] lParam on its stack; a non-zero "end" means the message was
//   taken and nothing else happens. START's (slot 248, 0x17D11) takes none but watches
//   WM_ACTIVATEAPP (b[17] & 4: keys are read), WM_MOUSEWHEEL (b[18]: 1 up, 2 down, for its
//   buttons), WM_LBUTTONDOWN / WM_RBUTTONDOWN (end AUTO / SKIP), WM_KEYDOWN 'A' / 'S' (AUTO /
//   SKIP on and off) and WM_PAINT (the skip clock);
// - (slots op_0849 registers for single messages: no game uses them);
// - then the engine's own handling: WM_CLOSE runs the slot of op_078A (0x488098) and closes the
//   window only when its "end" is not 0 (no slot: it closes); WM_ACTIVATEAPP the slots of op_076C
//   (getting the focus) / 076D (losing it); Alt+Enter the slot of op_0794; WM_TIMER the slot of op_0AF0.
// op_0AFA / 0AFB start and stop the timers (SetTimer / KillTimer). WM_PAINT comes after op_07D0
// (InvalidateRect), at the next pump of the message queue: the next frame here.

namespace OpenShiina.Scripting;

/// <summary>Window events the host passes on to the scripts.</summary>
public enum ScnEvent
{
    /// <summary>Alt+Enter (slot of op_0794, 0x48808C).</summary>
    ToggleFullScreen,
    /// <summary>The window lost the focus (op_076D, 0x488094; WM_ACTIVATEAPP with wParam 0).</summary>
    Deactivate,
    /// <summary>The window got the focus (op_076C, 0x488090; WM_ACTIVATEAPP with wParam 1).</summary>
    Activate,
}

/// <summary>The window messages the hosts pass on (Windows' numbers).</summary>
public static class ScnMessage
{
    public const int Paint = 0x000F, Close = 0x0010, ActivateApp = 0x001C, KeyDown = 0x0100, KeyUp = 0x0101,
        SysKeyDown = 0x0104, Timer = 0x0113, LButtonDown = 0x0201, LButtonUp = 0x0202, LButtonDoubleClick = 0x0203,
        RButtonDown = 0x0204, RButtonUp = 0x0205, MButtonDown = 0x0207, MButtonUp = 0x0208, MouseWheel = 0x020A;

    /// <summary>lParam of a mouse message: the point in the game's picture.</summary>
    public static int Point(int x, int y) => (y << 16) | (x & 0xFFFF);

    /// <summary>lParam of WM_KEYDOWN / WM_KEYUP: one press, the key's scan code, and the up / repeat bits.</summary>
    public static int Key(int scanCode, bool down, bool repeat) =>
        1 | (scanCode & 0xFF) << 16 | (repeat || !down ? 1 << 30 : 0) | (down ? 0 : 1 << 31);
}

public sealed partial class ScnVm
{
    // SetTimer: id -> interval and next due time (ms)
    private readonly Dictionary<int, (int Interval, uint Next)> m_timers = new();

    private static int EventGlobal(ScnEvent e) => e switch
    {
        ScnEvent.ToggleFullScreen => 0x48808C,
        ScnEvent.Deactivate => 0x488094,
        _ => 0x488090,
    };

    /// <summary>
    /// Runs a slot's code from its entry until it ends (FUN_0042BF60), as the window procedure
    /// does for an event. Returns the value of its "end", or 1 when the slot has no code.
    /// </summary>
    public int RunSlotSync(int slot)
    {
        var c = m_slots[slot];
        if (c.CodeBase == 0)
            return 1;
        int savedFlags = c.Flags;
        c.FrameTop = 0;
        c.Pc = c.Entry;
        c.Base = c.CodeBase;
        c.Flags = 1;
        for (int guard = 0; (c.Flags & 1) != 0 && !QuitRequested && guard < 1_000_000; guard++)
        {
            // A slot run to its end draws its 0083 text at once (text waiting for a key would
            // never end here: it is cut short)
            for (int step = 0; (c.Flags & 8) != 0 && !QuitRequested; step++)
            {
                if (step == 100_000)
                    c.Flags &= ~8;
                else
                    StepTaskText(c);
            }
            int result = Run(c);
            if (result is 1 or 3)
                break;
        }
        if ((savedFlags & 1) == 0)
            c.Flags = 0;
        TasksChanged();
        return c.ExitCode;
    }

    /// <summary>
    /// A window event: the message goes to the slot of op_07E4 first (WM_ACTIVATEAPP; Alt+Enter as
    /// WM_SYSKEYDOWN), then, if it did not take it, the slot the scripts registered for the event runs.
    /// </summary>
    public void Notify(ScnEvent e)
    {
        bool taken = e switch
        {
            ScnEvent.ToggleFullScreen => MessageHook(ScnMessage.SysKeyDown, 0x0D, ScnMessage.Key(0x1C, true, false) | 1 << 29),
            _ => MessageHook(ScnMessage.ActivateApp, e == ScnEvent.Activate ? 1 : 0, 0),
        };
        if (taken)
            return;
        int slot = EngineGlobals.GetValueOrDefault(EventGlobal(e), -1);
        if (slot is >= 0 and < Slots)
            RunSlotSync(slot);
    }

    /// <summary>
    /// The slot of op_07E4 sees a message (0x4355B0): window, message, wParam and lParam pushed on
    /// its stack (l[0..3]) while it runs. True when it took the message (its "end" is not 0).
    /// </summary>
    private bool MessageHook(int message, int wParam, int lParam)
    {
        int slot = EngineGlobals.GetValueOrDefault(0x4880A8, -1);
        if (slot is < 0 or >= Slots)
            return false;
        var c = m_slots[slot];
        if (c.Sp < 4)
            return false;
        c.Sp -= 4;
        Write32(StackAddress(slot, c.Sp), WindowHandle);
        Write32(StackAddress(slot, c.Sp + 1), message);
        Write32(StackAddress(slot, c.Sp + 2), wParam);
        Write32(StackAddress(slot, c.Sp + 3), lParam);
        int result = RunSlotSync(slot);
        c.Sp += 4;
        if (result != 0 && message != ScnMessage.Paint)
            Trace?.Add($"f{m_frameNumber} r{m_rounds} message slot {slot} ended {result} on {message:X4}");
        return result != 0;
    }

    /// <summary>
    /// A window message the host passes on (keys, mouse buttons, the wheel; ScnMessage): the slot
    /// of op_07E4 sees it, then the engine's own handling (the wheel's 0x13B52B4).
    /// </summary>
    public void WindowMessage(int message, int wParam, int lParam)
    {
        bool taken = MessageHook(message, wParam, lParam);
        Trace?.Add($"f{m_frameNumber} r{m_rounds} message {message:X4} {wParam:X} {lParam:X}{(taken ? " taken" : "")}");
        if (taken)
            return;
        if (message == ScnMessage.MouseWheel)
            MouseWheel((short)(wParam >> 16));
    }

    /// <summary>
    /// The player closes the window (WM_CLOSE): true when it may close - the slot of op_078A ran
    /// and ended with a value other than 0 (START's saves what it keeps and ends with 1), or the
    /// scripts registered none.
    /// </summary>
    public bool CloseWindow()
    {
        if (MessageHook(ScnMessage.Close, 0, 0))
            return false;
        int slot = EngineGlobals.GetValueOrDefault(0x488098, -1);
        if (slot is < 0 or >= Slots)
            return true;
        return RunSlotSync(slot) != 0;
    }

    // An invalid part of the window waits for WM_PAINT
    private bool m_paintPending;

    /// <summary>The messages the engine's queue would have for the window by now: WM_PAINT for an invalid part.</summary>
    private void PumpMessages() => Paint();

    /// <summary>Runs the timer slot once for every timer that is due (WM_TIMER is not queued twice).</summary>
    private void FireTimers()
    {
        if (m_timers.Count == 0)
            return;
        uint now = m_host.Milliseconds;
        foreach (var (id, timer) in m_timers.ToList())
        {
            if ((int)(now - timer.Next) < 0)
                continue;
            uint next = timer.Next + (uint)timer.Interval;
            if ((int)(now - next) >= 0)
                next = now + (uint)timer.Interval;
            m_timers[id] = (timer.Interval, next);
            if (MessageHook(ScnMessage.Timer, id, 0))
                continue;
            int slot = EngineGlobals.GetValueOrDefault(0x4880AC, -1);
            if (slot is >= 0 and < Slots)
                RunSlotSync(slot);
        }
    }

    private void RegisterEvents()
    {
        // 0AF0 slot: the slot run on WM_TIMER
        Register(0x0AF0, (vm, c, i) => vm.SetGlobal(0x4880AC, c, i));
        // 0AFA id, ms, v: SetTimer; v = the timer id
        Register(0x0AFA, (vm, c, i) =>
        {
            int id = vm.Value(c, i.Args[0]), ms = Math.Max(1, vm.Value(c, i.Args[1]));
            vm.m_timers[id] = (ms, vm.m_host.Milliseconds + (uint)ms);
            vm.Store(c, i.Args[2], id);
            return 0;
        });
        // 0AFB id: KillTimer (an unknown timer is a script error)
        Register(0x0AFB, (vm, c, i) => vm.m_timers.Remove(vm.Value(c, i.Args[0])) ? 0 : 2);
    }
}
