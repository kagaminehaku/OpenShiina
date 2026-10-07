// Joystick 0 through SDL3, read the way the engine reads winmm's joyGetPosEx: the first joystick
// SDL lists, its axes 0 and 1 moved to 0-65535 and its first two buttons. Polled on the window's
// thread each frame it draws (SDL wants its joysticks there); a joystick plugged in later is
// opened within a second, one pulled out is let go.

using OpenShiina.Scripting;
using SDL;

namespace OpenShiina.App;

public sealed unsafe class SdlJoystick : IDisposable
{
    private readonly bool m_ready;
    private SDL_Joystick* m_joystick;
    private long m_nextLook;

    public SdlJoystick()
    {
        m_ready = SDL3.SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_JOYSTICK);
    }

    /// <summary>The joystick now, or null when there is none.</summary>
    public ScnJoystick? Poll()
    {
        if (!m_ready)
            return null;
        SDL3.SDL_UpdateJoysticks();
        if (m_joystick != null && !SDL3.SDL_JoystickConnected(m_joystick))
        {
            SDL3.SDL_CloseJoystick(m_joystick);
            m_joystick = null;
        }
        if (m_joystick == null)
        {
            long now = Environment.TickCount64;
            if (now < m_nextLook)
                return null;
            m_nextLook = now + 1000;
            int count;
            var ids = SDL3.SDL_GetJoysticks(&count);
            if (ids == null)
                return null;
            if (count > 0)
                m_joystick = SDL3.SDL_OpenJoystick(ids[0]);
            SDL3.SDL_free(ids);
            if (m_joystick == null)
                return null;
        }
        int Axis(int n) => SDL3.SDL_GetJoystickAxis(m_joystick, n) + 32768;
        int buttons = (SDL3.SDL_GetJoystickButton(m_joystick, 0) ? 1 : 0) | (SDL3.SDL_GetJoystickButton(m_joystick, 1) ? 2 : 0);
        return new ScnJoystick(Axis(0), Axis(1), buttons);
    }

    public void Dispose()
    {
        if (m_joystick != null)
        {
            SDL3.SDL_CloseJoystick(m_joystick);
            m_joystick = null;
        }
        if (m_ready)
            SDL3.SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_JOYSTICK);
    }
}
