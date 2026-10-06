// Window events and timers. The engine's window procedure runs a script slot to the end for
// each event (FUN_0042BF60): WM_TIMER the slot of op_0AF0, and the slots that op_0794 / 076C /
// 076D / 078A / 07E4 registered for Alt+Enter, losing / getting the focus, the end of the game and
// a close request. op_0AFA / 0AFB start and stop the timers (SetTimer / KillTimer).

namespace OpenShiina.Scripting;

/// <summary>Window events the host passes on to the scripts.</summary>
public enum ScnEvent
{
    /// <summary>Alt+Enter (slot of op_0794, 0x48808C).</summary>
    ToggleFullScreen,
    /// <summary>The window lost the focus (op_076C, 0x488090).</summary>
    Deactivate,
    /// <summary>The window got the focus back (op_076D, 0x488094).</summary>
    Activate,
    /// <summary>The game ends (op_078A, 0x488098).</summary>
    Destroy,
    /// <summary>The player closes the window (op_07E4, 0x4880A8).</summary>
    CloseRequest,
}

public sealed partial class ScnVm
{
    // SetTimer: id -> interval and next due time (ms)
    private readonly Dictionary<int, (int Interval, uint Next)> m_timers = new();

    private static int EventGlobal(ScnEvent e) => e switch
    {
        ScnEvent.ToggleFullScreen => 0x48808C,
        ScnEvent.Deactivate => 0x488090,
        ScnEvent.Activate => 0x488094,
        ScnEvent.Destroy => 0x488098,
        _ => 0x4880A8,
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
            int result = Run(c);
            if (result is 1 or 3)
                break;
        }
        if ((savedFlags & 1) == 0)
            c.Flags = 0;
        TasksChanged();
        return c.ExitCode;
    }

    /// <summary>A window event: runs the slot the scripts registered for it, if any.</summary>
    public void Notify(ScnEvent e)
    {
        int slot = EngineGlobals.GetValueOrDefault(EventGlobal(e), -1);
        if (slot is >= 0 and < Slots)
            RunSlotSync(slot);
    }

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
