// The window's picture. The scripts draw into the display surface (0x13B43E4, a DIB in memory);
// the window shows it only where Windows asks for a repaint: the rectangles made invalid by
// op_07D0 (InvalidateRect) and by the effects that paint the window themselves (0564 / 0566
// draw on it and invalidate their rectangle, 0568 / 056C and full screen invalidate all of it),
// repainted by WM_PAINT at the next pump of the message queue or by op_07D1 (UpdateWindow).
// That is the engine's GDI mode (RIO.INI has no "Render", 0x487F30 = -1: FUN_0040DB10 paints
// between BeginPaint and EndPaint, so only the invalid region changes). Everything else drawn
// into the display surface stays unseen until a repaint covers it - the save screen's cursor
// (START 0x2BE6C) is drawn beyond the slot it belongs to and only the slot is invalidated.
// The hosts show this picture (Window), not the display surface.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // The window's picture (24-bit, ScreenWidth x ScreenHeight) and its invalid rectangles
    private byte[]? m_window;
    private readonly List<(int L, int T, int R, int B)> m_invalid = [];

    /// <summary>The window's picture: 24-bit BGR rows of <see cref="ScreenWidth"/> pixels (stride ScreenWidth * 3).</summary>
    public ReadOnlySpan<byte> Window => m_window ??= new byte[ScreenWidth * ScreenHeight * 3];

    /// <summary>InvalidateRect: the rectangle (clipped to the window) is repainted from the display surface by the next WM_PAINT.</summary>
    public void InvalidateWindow(int l, int t, int r, int b)
    {
        l = Math.Max(l, 0);
        t = Math.Max(t, 0);
        r = Math.Min(r, ScreenWidth);
        b = Math.Min(b, ScreenHeight);
        if (l < r && t < b)
        {
            m_invalid.Add((l, t, r, b));
            m_paintPending = true;
        }
    }

    /// <summary>InvalidateRect(NULL): all of the window.</summary>
    public void InvalidateWindow() => InvalidateWindow(0, 0, ScreenWidth, ScreenHeight);

    /// <summary>
    /// WM_PAINT (0x435EA2 / FUN_0040DB10): the message slot sees it first, then the slots of
    /// op_04D3 / 04D4 run before and after the invalid rectangles are copied from the display
    /// surface into the window's picture.
    /// </summary>
    private void Paint()
    {
        if (!m_paintPending)
            return;
        m_paintPending = false;
        // A slot that takes WM_PAINT leaves the region invalid: Windows sends it again
        if (MessageHook(ScnMessage.Paint, 0, 0))
        {
            m_paintPending = true;
            return;
        }
        RunPaintSlot(0x4880B0);
        var window = m_window ??= new byte[ScreenWidth * ScreenHeight * 3];
        int surface = DisplaySurface;
        int pixels = SurfaceField(surface, 2), pitch = SurfaceField(surface, 10), bytes = SurfaceField(surface, 9) >> 3;
        int width = Math.Min(SurfaceField(surface, 7), ScreenWidth), height = Math.Min(SurfaceField(surface, 8), ScreenHeight);
        if (pixels != 0 && bytes is 3 or 4)
        {
            var row = new byte[ScreenWidth * bytes];
            foreach (var (l, t, r0, b0) in m_invalid)
            {
                int r = Math.Min(r0, width), b = Math.Min(b0, height);
                for (int y = t; y < b && l < r; y++)
                {
                    var src = row.AsSpan(0, (r - l) * bytes);
                    ReadBytes(pixels + y * pitch + l * bytes, src);
                    var dst = window.AsSpan((y * ScreenWidth + l) * 3, (r - l) * 3);
                    if (bytes == 3)
                        src.CopyTo(dst);
                    else
                        for (int x = 0, s = 0, d = 0; x < r - l; x++, s += 4, d += 3)
                        {
                            dst[d] = src[s];
                            dst[d + 1] = src[s + 1];
                            dst[d + 2] = src[s + 2];
                        }
                }
            }
        }
        m_invalid.Clear();
        ScreenInvalidated = true;
        RunPaintSlot(0x4880B4);
    }

    private void RunPaintSlot(int global)
    {
        int slot = EngineGlobals.GetValueOrDefault(global, -1);
        if (slot is >= 0 and < Slots)
            RunSlotSync(slot);
    }
}
