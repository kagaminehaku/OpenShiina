// Input. The engine reads the keyboard through DirectInput (FUN_00413630) and folds it into a
// button mask, with the mouse buttons of the window and joystick 0:
//   1 up  2 down  4 left  8 right  0x10 cancel (X, right button)  0x20 decide (Z, space, return,
//   left button)  0x40 Esc / Home / Numpad 0  0x80 End  0x100 Ctrl  0x200 Tab  0x800 middle button
// FUN_00413810 adds key repeat: a new press at once, then after KeyRepeatDelay (300 ms) every
// KeyRepeatSpeed (50 ms) of RIO.INI. op_03E8 is GetAsyncKeyState of one virtual key.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // FUN_00413810 state per caller (0x7DB928: phase, last buttons, start, delay)
    private readonly Dictionary<int, (int Phase, int Last, uint Start, int Delay)> m_repeat = new();
    private int m_repeatDelay = -1, m_repeatSpeed = -1;

    /// <summary>FUN_00413630: the buttons held now (0 when the window is not in front).</summary>
    public int Buttons()
    {
        if (!m_host.Active)
            return 0;
        bool Down(params int[] keys) => keys.Any(m_host.KeyDown);
        int b = 0;
        if (Down(0x26, 0x68)) b |= 1;                 // up, numpad 8
        if (Down(0x28, 0x62)) b |= 2;                 // down, numpad 2
        if (Down(0x25, 0x64)) b |= 4;                 // left, numpad 4
        if (Down(0x27, 0x66)) b |= 8;                 // right, numpad 6
        if (Down(0x5A, 0x20, 0x0D)) b |= 0x20;        // Z, space, return (and numpad enter)
        if (Down(0x58)) b |= 0x10;                    // X
        if (Down(0x24, 0x60, 0x1B)) b |= 0x40;        // Home, numpad 0, Esc
        if (Down(0x23)) b |= 0x80;                    // End
        if (Down(0x11)) b |= 0x100;                   // Ctrl
        if (Down(0x09)) b |= 0x200;                   // Tab
        int mouse = m_host.MouseButtons;
        if ((mouse & 1) != 0) b |= 0x20;
        if ((mouse & 2) != 0) b |= 0x10;
        if ((mouse & 4) != 0) b |= 0x800;
        return b;
    }

    /// <summary>FUN_00413810: the buttons with key repeat for one caller.</summary>
    public int RepeatButtons(int caller, int buttons)
    {
        if (m_repeatDelay < 0)
        {
            string section = RioSection();
            m_repeatDelay = IniInt("RIO.INI", section, "KeyRepeatDelay", 300);
            m_repeatSpeed = IniInt("RIO.INI", section, "KeyRepeatSpeed", 50);
        }
        if (buttons == 0)
        {
            m_repeat[caller] = default;
            return 0;
        }
        var state = m_repeat.GetValueOrDefault(caller);
        uint now = m_host.Milliseconds;
        int delay;
        if ((state.Last & buttons) == buttons && state.Phase != 0)
        {
            if (state.Phase != 1)
            {
                state.Last = buttons;
                if ((uint)(now - state.Start) >= (uint)state.Delay)
                    state.Phase = 1;
                m_repeat[caller] = state;
                return 0;
            }
            delay = m_repeatSpeed;
        }
        else
            delay = m_repeatDelay;
        m_repeat[caller] = (2, buttons, now, delay);
        return buttons;
    }

    /// <summary>The first section of RIO.INI ("[椎名里緒 v2.47]"), where the engine's own settings are.</summary>
    private string RioSection()
    {
        if (m_host.ReadLooseFile("RIO.INI") is not { } bytes)
            return "";
        foreach (var line in Encodings.cp932.GetString(bytes).Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
                return t[1..^1];
        }
        return "";
    }

    // op_03EB: where a task waiting for buttons is (0 wait for release, 1 wait for a press)
    private readonly Dictionary<int, int> m_buttonWait = new();

    private void RegisterInput()
    {
        // 03E8 vk, v: GetAsyncKeyState (as a signed 16-bit value), 0 when the window is inactive
        Register(0x03E8, (vm, c, i) =>
        {
            int vk = vm.Value(c, i.Args[0]);
            int state = vm.m_host.KeyDown(vk) ? unchecked((short)0x8000) : 0;
            vm.Store(c, i.Args[1], vm.m_host.Active ? state : 0);
            return 0;
        });
        // 0456 x, y: where the mouse is (FUN_0040CE90; kept at 0xBCE348 / 0xBCE344)
        Register(0x0456, (vm, c, i) =>
        {
            var (x, y) = vm.m_host.MousePosition;
            vm.EngineGlobals[0xBCE348] = x;
            vm.EngineGlobals[0xBCE344] = y;
            vm.Store(c, i.Args[0], x);
            vm.Store(c, i.Args[1], y);
            return 0;
        });
        // 0457 x, y: move the mouse there
        Register(0x0457, (vm, c, i) =>
        {
            int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
            vm.EngineGlobals[0xBCE348] = x;
            vm.EngineGlobals[0xBCE344] = y;
            vm.m_host.SetMousePosition(x, y);
            return 0;
        });
        // 0459 v: mouse buttons held (DirectInput: 1 left, 2 right, 4 middle; 0x13B52BC swaps
        // left and right)
        Register(0x0459, (vm, c, i) =>
        {
            int mouse = vm.m_host.MouseButtons;
            if (vm.EngineGlobals.GetValueOrDefault(0x13B52BC) != 0)
                mouse = (mouse & 4) | (mouse & 1) << 1 | (mouse & 2) >> 1;
            vm.Store(c, i.Args[0], mouse & 7);
            return 0;
        });
        // 0492 x, y: window point -> picture point (x - offset) / scale, both in and out; the
        // host already gives picture points, so they stay as they are
        Register(0x0492, (vm, c, i) =>
        {
            int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[0], x);
            vm.Store(c, i.Args[1], y);
            return 0;
        });
        // 03E9 v: the buttons held (keyboard, mouse and joystick)
        Register(0x03E9, (vm, c, i) => { vm.Store(c, i.Args[0], vm.Buttons()); return 0; });
        // 03EA v: the buttons with key repeat
        Register(0x03EA, (vm, c, i) => { vm.Store(c, i.Args[0], vm.RepeatButtons(0x100, vm.Buttons())); return 0; });
        // 03EB mask: wait until every button is let go, then until one of mask is pressed
        // (the executable loops inside the opcode with Sleep(10); here the task yields instead)
        Register(0x03EB, (vm, c, i) =>
        {
            int mask = vm.Value(c, i.Args[0]);
            int phase = vm.m_buttonWait.GetValueOrDefault(c.Slot);
            int buttons = vm.RepeatButtons(0x100, vm.Buttons());
            if (phase == 0)
            {
                if (buttons != 0)
                    return vm.WaitHere(c, 0);
                phase = 1;
            }
            if ((mask & buttons) == 0)
                return vm.WaitHere(c, 1);
            vm.m_buttonWait.Remove(c.Slot);
            return 0;
        });
    }

    /// <summary>Runs the current instruction again in a later frame (an opcode that waits).</summary>
    private int WaitHere(ScnContext c, int phase)
    {
        m_buttonWait[c.Slot] = phase;
        c.Pc = c.Current;
        FrameShown = true;
        return 3;
    }
}
