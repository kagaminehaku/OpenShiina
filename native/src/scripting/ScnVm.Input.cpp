// C# Scripting/ScnVm.Input.cs: input. The engine reads the keyboard through DirectInput
// (FUN_00413630) and folds it into a button mask, with the mouse buttons of the window and joystick 0:
//   1 up  2 down  4 left  8 right  0x10 cancel (X, right button)  0x20 decide (Z, space, return,
//   left button)  0x40 Esc / Home / Numpad 0  0x80 End  0x100 Ctrl  0x200 Tab  0x800 middle button
// FUN_00413810 adds key repeat: a new press at once, then after KeyRepeatDelay (300 ms) every
// KeyRepeatSpeed (50 ms) of RIO.INI. op_03E8 is GetAsyncKeyState of one virtual key. The joystick
// is read only when RIO.INI's Joypad (or Joystick, which wins) is not 0, or the player asks.
#include "scripting/ScnVm.h"

#include <initializer_list>

namespace openshiina::scripting {

std::string ScnVm::RioSection()
{
    // The first section of RIO.INI ("[椎名里緒 v2.47]"), where the engine's own settings are
    openshiina::Bytes bytes;
    if (!m_host.ReadLooseFile("RIO.INI", bytes))
        return "";
    std::string text(bytes.begin(), bytes.end());
    size_t start = 0;
    while (start <= text.size())
    {
        size_t end = text.find('\n', start);
        std::string t = SjisTrim(text.substr(start, end == std::string::npos ? std::string::npos : end - start));
        if (!t.empty() && t.front() == '[' && t.back() == ']')
            return t.substr(1, t.size() - 2);
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    return "";
}

bool ScnVm::KeyHeld(int virtualKey)
{
    // A key held, as the scripts see it (Ctrl too while a scene is skipped to a line to play from,
    // which is not here yet)
    return m_host.KeyDown(virtualKey);
}

int ScnVm::Buttons()
{
    // FUN_00413630: the buttons held now (0 when the window is not in front)
    if (!m_host.Active())
        return 0;
    auto down = [this](std::initializer_list<int> keys) {
        for (int key : keys)
            if (KeyHeld(key))
                return true;
        return false;
    };
    int b = 0;
    if (down({0x26, 0x68}))
        b |= 1;      // up, numpad 8
    if (down({0x28, 0x62}))
        b |= 2;      // down, numpad 2
    if (down({0x25, 0x64}))
        b |= 4;      // left, numpad 4
    if (down({0x27, 0x66}))
        b |= 8;      // right, numpad 6
    if (down({0x5A, 0x20, 0x0D}))
        b |= 0x20;   // Z, space, return
    if (down({0x58}))
        b |= 0x10;   // X
    if (down({0x24, 0x60, 0x1B}))
        b |= 0x40;   // Home, numpad 0, Esc
    if (down({0x23}))
        b |= 0x80;   // End
    if (down({0x11}))
        b |= 0x100;  // Ctrl
    if (down({0x09}))
        b |= 0x200;  // Tab
    int mouse = m_host.MouseButtons();
    if ((mouse & 1) != 0)
        b |= 0x20;
    if ((mouse & 2) != 0)
        b |= 0x10;
    if ((mouse & 4) != 0)
        b |= 0x800;
    if (JoypadOn())
        if (auto joy = m_host.Joystick())
        {
            int j = 0;
            if (joy->X < 0x2000)
                j |= 4;
            if (joy->X > 0xDFFF)
                j |= 8;
            if (joy->Y < 0x4000)
                j |= 1;
            if (joy->Y > 0xDFFF)
                j |= 2;
            if ((joy->Buttons & 1) != 0)
                j |= 0x20;
            if ((joy->Buttons & 2) != 0)
                j |= 0x10;
            // The engine notes that the joystick is in use (op_03F2 reads it; the mouse clears it)
            if (j != 0)
                EngineGlobals[0x13B4404] = 1;
            b |= j;
        }
    return b;
}

bool ScnVm::JoypadOn()
{
    if (Joypad)
        return *Joypad;
    if (m_joypad < 0)
    {
        std::string section = RioSection();
        m_joypad = IniInt("RIO.INI", section, "Joystick", IniInt("RIO.INI", section, "Joypad", 0)) != 0 ? 1 : 0;
    }
    return m_joypad != 0;
}

int ScnVm::RepeatButtons(int caller, int buttons)
{
    // FUN_00413810: the buttons with key repeat for one caller
    if (m_repeatDelay < 0)
    {
        std::string section = RioSection();
        m_repeatDelay = IniInt("RIO.INI", section, "KeyRepeatDelay", 300);
        m_repeatSpeed = IniInt("RIO.INI", section, "KeyRepeatSpeed", 50);
    }
    if (buttons == 0)
    {
        m_repeat[caller] = {};
        return 0;
    }
    RepeatState state = m_repeat[caller];
    uint32_t now = Clock();
    int delay;
    if ((state.Last & buttons) == buttons && state.Phase != 0)
    {
        if (state.Phase != 1)
        {
            state.Last = buttons;
            if (now - state.Start >= uint32_t(state.Delay))
                state.Phase = 1;
            m_repeat[caller] = state;
            return 0;
        }
        delay = m_repeatSpeed;
    }
    else
        delay = m_repeatDelay;
    m_repeat[caller] = {2, buttons, now, delay};
    return buttons;
}

int ScnVm::WaitHere(ScnContext& c, int phase)
{
    // Runs the current instruction again in a later frame (an opcode that waits)
    m_buttonWait[c.Slot] = phase;
    c.Pc = c.Current;
    FrameShown = true;
    return 3;
}

void ScnVm::RegisterInput()
{
    // 03E8 vk, v: GetAsyncKeyState (as a signed 16-bit value), 0 when the window is inactive
    Register(0x03E8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int vk = vm.Value(c, i.Args[0]);
        int state = vm.KeyHeld(vk) ? -0x8000 : 0;  // (short)0x8000
        vm.Store(c, i.Args[1], vm.m_host.Active() ? state : 0);
        return 0;
    });
    // 0456 x, y: where the mouse is (FUN_0040CE90; kept at 0xBCE348 / 0xBCE344)
    Register(0x0456, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto [x, y] = vm.m_host.MousePosition();
        vm.EngineGlobals[0xBCE348] = x;
        vm.EngineGlobals[0xBCE344] = y;
        vm.Store(c, i.Args[0], x);
        vm.Store(c, i.Args[1], y);
        return 0;
    });
    // 0457 x, y: move the mouse there
    Register(0x0457, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
        vm.EngineGlobals[0xBCE348] = x;
        vm.EngineGlobals[0xBCE344] = y;
        vm.m_host.SetMousePosition(x, y);
        return 0;
    });
    // 0458 l, r: the left and right mouse buttons held, 1 or 0
    Register(0x0458, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int mouse = vm.m_host.Active() ? vm.m_host.MouseButtons() : 0;
        vm.Store(c, i.Args[0], mouse & 1);
        vm.Store(c, i.Args[1], (mouse >> 1) & 1);
        return 0;
    });
    // 0459 v: mouse buttons held (1 left, 2 right, 4 middle; 0x13B52BC swaps left and right)
    Register(0x0459, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int mouse = vm.m_host.MouseButtons();
        if (vm.EngineGlobal(0x13B52BC) != 0)
            mouse = (mouse & 4) | (mouse & 1) << 1 | (mouse & 2) >> 1;
        vm.Store(c, i.Args[0], mouse & 7);
        return 0;
    });
    // 0460 / 0461: the mouse pointer shown again / hidden
    Register(0x0460, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        vm.m_host.ShowPointer(true);
        return 0;
    });
    Register(0x0461, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        vm.m_host.ShowPointer(false);
        return 0;
    });
    // 0492 x, y: window point -> picture point; the host already gives picture points
    Register(0x0492, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
        vm.Store(c, i.Args[0], x);
        vm.Store(c, i.Args[1], y);
        return 0;
    });
    // 03E9 v: the buttons held (keyboard, mouse and joystick)
    Register(0x03E9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.Buttons());
        return 0;
    });
    // 03EA v: the buttons with key repeat
    Register(0x03EA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.RepeatButtons(0x100, vm.Buttons()));
        return 0;
    });
    // 03EB mask: wait until every button is let go, then until one of mask is pressed (the task
    // yields meanwhile)
    Register(0x03EB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int mask = vm.Value(c, i.Args[0]);
        auto wait = vm.m_buttonWait.find(c.Slot);
        int phase = wait == vm.m_buttonWait.end() ? 0 : wait->second;
        int buttons = vm.RepeatButtons(0x100, vm.Buttons());
        if (phase == 0)
        {
            if (buttons != 0)
                return vm.WaitHere(c, 0);
            phase = 1;
        }
        if ((mask & buttons) == 0)
            return vm.WaitHere(c, 1);
        vm.m_buttonWait.erase(c.Slot);
        return 0;
    });
}

} // namespace openshiina::scripting
