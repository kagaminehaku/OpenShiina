// The keyboard, the mouse and the joystick as the interpreter asks for them (GetAsyncKeyState, DirectInput, joyGetPosEx):
// the window's thread records key and button changes as they come, the interpreter's thread reads
// the state when the scripts ask. With the interpreter on a thread of its own, the window's
// events are no longer held up by slow frames, so a released key is seen at once.

using Avalonia.Input;
using OpenShiina.Scripting;

namespace OpenShiina.App;

public sealed class InputState
{
    private readonly object m_lock = new();
    private readonly HashSet<int> m_keys = new();
    private volatile int m_buttons, m_x, m_y;
    private volatile bool m_active = true;

    /// <summary>The game's window is in front.</summary>
    public bool Active
    {
        get => m_active;
        set
        {
            m_active = value;
            if (!value)
                Clear();
        }
    }

    /// <summary>Mouse buttons held: 1 left, 2 right, 4 middle.</summary>
    public int Buttons
    {
        get => m_active ? m_buttons : 0;
        set => m_buttons = value;
    }

    /// <summary>Where the pointer is, in pixels of the game's picture.</summary>
    public (int X, int Y) Position
    {
        get => (m_x, m_y);
        set => (m_x, m_y) = value;
    }

    private ScnJoystick? m_joystick;

    /// <summary>Joystick 0 as the window last read it, or null when there is none.</summary>
    public ScnJoystick? Joystick
    {
        get
        {
            if (!m_active)
                return null;
            lock (m_lock)
                return m_joystick;
        }
        set
        {
            lock (m_lock)
                m_joystick = value;
        }
    }

    public bool IsDown(int virtualKey)
    {
        if (!m_active)
            return false;
        lock (m_lock)
            return m_keys.Contains(virtualKey);
    }

    /// <summary>A key went down or up: its Windows virtual key and, for Ctrl, Shift and Alt, the general one.</summary>
    public void Press(Key key, bool down)
    {
        int vk = VirtualKeys.From(key);
        if (vk == 0)
            return;
        int general = VirtualKeys.General(vk);
        lock (m_lock)
        {
            Set(vk);
            if (general != 0)
            {
                // The general key is held while either side is
                if (down)
                    m_keys.Add(general);
                else if (!VirtualKeys.Sides(general).Any(m_keys.Contains))
                    m_keys.Remove(general);
            }
        }

        void Set(int k)
        {
            if (down)
                m_keys.Add(k);
            else
                m_keys.Remove(k);
        }
    }

    public void Clear()
    {
        lock (m_lock)
            m_keys.Clear();
        m_buttons = 0;
    }
}

/// <summary>Avalonia's keys as Windows virtual-key codes, which the scripts and the engine use.</summary>
public static class VirtualKeys
{
    private const int Shift = 0x10, Control = 0x11, Menu = 0x12;

    public static int From(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
        Key.Back => 0x08,
        Key.Tab => 0x09,
        Key.Clear => 0x0C,
        Key.Enter => 0x0D,
        Key.Pause => 0x13,
        Key.CapsLock => 0x14,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.PrintScreen => 0x2C,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.Multiply => 0x6A,
        Key.Add => 0x6B,
        Key.Separator => 0x6C,
        Key.Subtract => 0x6D,
        Key.Decimal => 0x6E,
        Key.Divide => 0x6F,
        Key.LeftShift => 0xA0,
        Key.RightShift => 0xA1,
        Key.LeftCtrl => 0xA2,
        Key.RightCtrl => 0xA3,
        Key.LeftAlt => 0xA4,
        Key.RightAlt => 0xA5,
        _ => 0,
    };

    /// <summary>VK_SHIFT / VK_CONTROL / VK_MENU for a left or right one, else 0.</summary>
    public static int General(int vk) => vk switch
    {
        0xA0 or 0xA1 => Shift,
        0xA2 or 0xA3 => Control,
        0xA4 or 0xA5 => Menu,
        _ => 0,
    };

    public static int[] Sides(int general) => general switch
    {
        Shift => [0xA0, 0xA1],
        Control => [0xA2, 0xA3],
        _ => [0xA4, 0xA5],
    };
}
