// The window's picture. The scripts draw into the display surface (0x13B43E4, a DIB in memory);
// the window shows it only where Windows asks for a repaint: the rectangles made invalid by
// op_07D0 (InvalidateRect) and by the effects that paint the window themselves (0564 / 0566
// draw on it and invalidate their rectangle, 0568 / 056C and full screen invalidate all of it),
// repainted by WM_PAINT at the next pump of the message queue or by op_07D1 (UpdateWindow).
// That is the engine's GDI mode (RIO.INI has no "Render", 0x487F30 = -1: FUN_0040DB10 paints
// between BeginPaint and EndPaint, so only the invalid region changes). Copies into the display
// surface request a repaint themselves; Direct3D submits those copies directly.
// Other drawing stays unseen until a repaint or a submission covers it - the save screen's cursor
// (START 0x2BE6C) is drawn beyond the slot it belongs to and only the slot is invalidated.
// The hosts show this picture (Window), not the display surface, and copy only the part of it
// that changed (TakeChangedArea: the rectangles repainted since they last asked).

namespace OpenShiina.Scripting;

/// <summary>A rectangle of the screen, (L, T) included to (R, B) excluded; empty when R <= L or B <= T.</summary>
public readonly record struct ScreenArea(int L, int T, int R, int B)
{
    public bool IsEmpty => R <= L || B <= T;

    public int Width => R - L;

    public int Height => B - T;

    /// <summary>The smallest rectangle holding both.</summary>
    public ScreenArea Union(ScreenArea other) =>
        IsEmpty ? other : other.IsEmpty ? this : new(Math.Min(L, other.L), Math.Min(T, other.T), Math.Max(R, other.R), Math.Max(B, other.B));

    /// <summary>The part inside (0, 0)-(width, height).</summary>
    public ScreenArea Clip(int width, int height) => new(Math.Max(L, 0), Math.Max(T, 0), Math.Min(R, width), Math.Min(B, height));
}

public sealed partial class ScnVm
{
    private enum WindowRenderer
    {
        Default = -1,
        Gdi = 0,
        DirectDraw = 1,
        Direct3D = 2,
    }

    private const int RendererGlobal = 0x487F30;
    private bool m_rendererInitialised;
    private (int A, int B, int C) m_rendererFilters;

    // Keep the script-visible renderer in one place, whether selected by RIO.INI or 04E8.
    private WindowRenderer CurrentRenderer
    {
        get
        {
            if (!m_rendererInitialised)
            {
                m_rendererInitialised = true;
                if (!EngineGlobals.TryGetValue(RendererGlobal, out int mode))
                    mode = IniInt("RIO.INI", RioSection(), "Render", -1);
                if (mode is >= 0 and <= 2)
                    EngineGlobals[RendererGlobal] = mode;
                m_rendererFilters = mode == 2 ? (2, 2, 0) : default;
            }
            return (WindowRenderer)EngineGlobals.GetValueOrDefault(RendererGlobal, -1);
        }
    }

    private bool SelectRenderer(int mode, int a, int b, int c)
    {
        _ = CurrentRenderer;
        if (mode is < 0 or > 2)
            return false;
        EngineGlobals[RendererGlobal] = mode;
        if (mode == (int)WindowRenderer.Gdi)
        {
            EngineGlobals[0x482940] = a;
            EngineGlobals[0x482944] = b;
        }
        m_rendererFilters = (a, b, c);
        InvalidateWindow();
        return true;
    }

    private (int A, int B, int C)? RendererFilters => CurrentRenderer switch
    {
        WindowRenderer.Gdi => (EngineGlobals.GetValueOrDefault(0x482940, 3), EngineGlobals.GetValueOrDefault(0x482944, 3), 0),
        WindowRenderer.DirectDraw or WindowRenderer.Direct3D => m_rendererFilters,
        _ => null,
    };

    // The window's picture (24-bit, ScreenWidth x ScreenHeight) and its invalid rectangles
    private byte[]? m_window;
    private readonly List<(int L, int T, int R, int B)> m_invalid = [];
    // What changed in the window's picture since TakeChangedArea; all of it at first
    private ScreenArea m_changed = new(0, 0, int.MaxValue, int.MaxValue);

    /// <summary>The window's picture: 24-bit BGR rows of <see cref="ScreenWidth"/> pixels (stride ScreenWidth * 3).</summary>
    public ReadOnlySpan<byte> Window => m_window ??= new byte[ScreenWidth * ScreenHeight * 3];

    /// <summary>
    /// The part of the window's picture that changed since the last call (the whole picture the
    /// first time), for the hosts to copy only that.
    /// </summary>
    public ScreenArea TakeChangedArea()
    {
        var area = m_changed.Clip(ScreenWidth, ScreenHeight);
        m_changed = default;
        return area;
    }

    /// <summary>InvalidateRect: the rectangle (clipped to the window) is repainted from the display surface by the next WM_PAINT.</summary>
    public void InvalidateWindow(int l, int t, int r, int b)
    {
        Trace?.Add($"f{m_frameNumber} r{m_rounds} invalidate {l},{t}-{r},{b}");
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
            Trace?.Add($"f{m_frameNumber} r{m_rounds} WM_PAINT taken by the message slot, {m_invalid.Count} rectangles left invalid");
            m_paintPending = true;
            return;
        }
        Trace?.Add($"f{m_frameNumber} r{m_rounds} WM_PAINT: {m_invalid.Count} rectangles from surface {DisplaySurface}");
        RunPaintSlot(0x4880B0);
        if (CurrentRenderer is WindowRenderer.DirectDraw or WindowRenderer.Direct3D)
            CopyDisplayToWindow(new ScreenArea(0, 0, ScreenWidth, ScreenHeight));
        else
        {
            foreach (var (l, t, r, b) in m_invalid)
                CopyDisplayToWindow(new ScreenArea(l, t, r, b));
        }
        m_invalid.Clear();
        ScreenInvalidated = true;
        m_paints++;
        RunPaintSlot(0x4880B4);
    }

    /// <summary>
    /// DirectDraw's Blt onto the primary surface (op_0516 into -1, op_05C1): a rectangle of a
    /// surface drawn straight onto the window, stretched to the rectangle (l, t, r, b), past the
    /// display surface; a later WM_PAINT covers it again where it repaints.
    /// </summary>
    private void BltToWindow(int l, int t, int r, int b, int src, int sl, int st, int sr, int sb)
    {
        Trace?.Add($"f{m_frameNumber} r{m_rounds} blt onto the window {l},{t}-{r},{b} from surface {src} {sl},{st}-{sr},{sb}");
        int pixels = SurfaceField(src, 2), pitch = SurfaceField(src, 10), bytes = SurfaceField(src, 9) >> 3;
        int sw = SurfaceField(src, 7), sh = SurfaceField(src, 8);
        if (pixels == 0 || bytes is not (3 or 4) || r <= l || b <= t || sr <= sl || sb <= st)
            return;
        var window = m_window ??= new byte[ScreenWidth * ScreenHeight * 3];
        m_changed = m_changed.Union(new ScreenArea(l, t, r, b).Clip(ScreenWidth, ScreenHeight));
        var row = new byte[(sr - sl) * bytes];
        for (int y = Math.Max(t, 0); y < Math.Min(b, ScreenHeight); y++)
        {
            int fy = st + (int)((long)(y - t) * (sb - st) / (b - t));
            if (fy < 0 || fy >= sh)
                continue;
            int x0 = Math.Max(sl, 0), x1 = Math.Min(sr, sw);
            if (x0 >= x1)
                continue;
            ReadBytes(pixels + fy * pitch + x0 * bytes, row.AsSpan((x0 - sl) * bytes, (x1 - x0) * bytes));
            for (int x = Math.Max(l, 0); x < Math.Min(r, ScreenWidth); x++)
            {
                int fx = sl + (int)((long)(x - l) * (sr - sl) / (r - l));
                if (fx < x0 || fx >= x1)
                    continue;
                int s = (fx - sl) * bytes, d = (y * ScreenWidth + x) * 3;
                window[d] = row[s];
                window[d + 1] = row[s + 1];
                window[d + 2] = row[s + 2];
            }
        }
        ScreenInvalidated = true;
        FrameShown = true;
    }

    // 0514 / 051E request presentation when their destination is the display surface.
    // GDI and DirectDraw queue a repaint (Chou Saiminjutsu Gakuen trial, 0x418780);
    // Direct3D submits the whole surface without going through the WM_PAINT callbacks.
    private void PresentSurfaceCopy(int l, int t, int r, int b)
    {
        var area = new ScreenArea(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b)).Clip(ScreenWidth, ScreenHeight);
        if (area.IsEmpty)
            return;
        if (CurrentRenderer == WindowRenderer.Direct3D)
            CopyDisplayToWindow(new ScreenArea(0, 0, ScreenWidth, ScreenHeight));
        else
            InvalidateWindow(area.L, area.T, area.R, area.B);
    }

    private void CopyDisplayToWindow(ScreenArea area)
    {
        int surface = DisplaySurface;
        int pixels = SurfaceField(surface, 2), pitch = SurfaceField(surface, 10), bytes = SurfaceField(surface, 9) >> 3;
        area = area.Clip(Math.Min(SurfaceField(surface, 7), ScreenWidth), Math.Min(SurfaceField(surface, 8), ScreenHeight));
        if (pixels == 0 || bytes is not (3 or 4) || area.IsEmpty)
            return;
        var window = m_window ??= new byte[ScreenWidth * ScreenHeight * 3];
        var row = new byte[area.Width * bytes];
        for (int y = area.T; y < area.B; y++)
        {
            ReadBytes(pixels + y * pitch + area.L * bytes, row);
            var dst = window.AsSpan((y * ScreenWidth + area.L) * 3, area.Width * 3);
            if (bytes == 3)
                row.CopyTo(dst);
            else
                for (int x = 0, s = 0, d = 0; x < area.Width; x++, s += 4, d += 3)
                {
                    dst[d] = row[s];
                    dst[d + 1] = row[s + 1];
                    dst[d + 2] = row[s + 2];
                }
        }
        m_changed = m_changed.Union(area);
        ScreenInvalidated = true;
    }

    private void RunPaintSlot(int global)
    {
        int slot = EngineGlobals.GetValueOrDefault(global, -1);
        if (slot is >= 0 and < Slots)
            RunSlotSync(slot);
    }
}
