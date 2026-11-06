// C# Scripting/ScnVm.Paint.cs: the window's picture. The scripts draw into the display surface
// (0x13B43E4, a DIB in memory); the window shows it only where Windows asks for a repaint: the
// rectangles made invalid by op_07D0 (InvalidateRect) and by the effects that paint the window
// themselves, repainted by WM_PAINT at the next pump of the message queue or by op_07D1
// (UpdateWindow). That is the engine's GDI mode: only the invalid region changes. The hosts show
// this picture (Window), not the display surface, and copy only the part of it that changed
// (TakeChangedArea).
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

const openshiina::Bytes& ScnVm::Window()
{
    if (m_window.empty())
        m_window.assign(size_t(ScreenWidth) * size_t(ScreenHeight) * 3, 0);
    return m_window;
}

ScreenArea ScnVm::TakeChangedArea()
{
    ScreenArea area = m_changed.Clip(ScreenWidth, ScreenHeight);
    m_changed = {};
    return area;
}

void ScnVm::InvalidateWindow(int l, int t, int r, int b)
{
    l = std::max(l, 0);
    t = std::max(t, 0);
    r = std::min(r, ScreenWidth);
    b = std::min(b, ScreenHeight);
    if (l < r && t < b)
    {
        m_invalid.push_back({l, t, r, b});
        m_paintPending = true;
    }
}

void ScnVm::Paint()
{
    if (!m_paintPending)
        return;
    m_paintPending = false;
    // A slot that takes WM_PAINT leaves the region invalid: Windows sends it again
    if (MessageHook(ScnMessage::Paint, 0, 0))
    {
        m_paintPending = true;
        return;
    }
    RunPaintSlot(0x4880B0);
    Window();
    int surface = DisplaySurface();
    int pixels = SurfaceField(surface, 2), pitch = SurfaceField(surface, 10), bytes = SurfaceField(surface, 9) >> 3;
    int width = std::min(SurfaceField(surface, 7), ScreenWidth), height = std::min(SurfaceField(surface, 8), ScreenHeight);
    if (pixels != 0 && (bytes == 3 || bytes == 4))
    {
        openshiina::Bytes row(size_t(ScreenWidth) * size_t(bytes));
        for (const auto& [l, t, r0, b0] : m_invalid)
        {
            int r = std::min(r0, width), b = std::min(b0, height);
            m_changed = m_changed.Union({l, t, r, b});
            for (int y = t; y < b && l < r; y++)
            {
                uint8_t* src = row.data();
                ReadBytes(pixels + y * pitch + l * bytes, src, (r - l) * bytes);
                uint8_t* dst = &m_window[(size_t(y) * size_t(ScreenWidth) + size_t(l)) * 3];
                if (bytes == 3)
                    std::memcpy(dst, src, size_t(r - l) * 3);
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
    m_invalid.clear();
    ScreenInvalidated = true;
    m_paints++;
    RunPaintSlot(0x4880B4);
}

void ScnVm::BltToWindow(int l, int t, int r, int b, int src, int sl, int st, int sr, int sb)
{
    int pixels = SurfaceField(src, 2), pitch = SurfaceField(src, 10), bytes = SurfaceField(src, 9) >> 3;
    int sw = SurfaceField(src, 7), sh = SurfaceField(src, 8);
    if (pixels == 0 || (bytes != 3 && bytes != 4) || r <= l || b <= t || sr <= sl || sb <= st)
        return;
    Window();
    m_changed = m_changed.Union(ScreenArea{l, t, r, b}.Clip(ScreenWidth, ScreenHeight));
    openshiina::Bytes row(size_t(sr - sl) * size_t(bytes));
    for (int y = std::max(t, 0); y < std::min(b, ScreenHeight); y++)
    {
        int fy = st + int(int64_t(y - t) * (sb - st) / (b - t));
        if (fy < 0 || fy >= sh)
            continue;
        int x0 = std::max(sl, 0), x1 = std::min(sr, sw);
        if (x0 >= x1)
            continue;
        ReadBytes(pixels + fy * pitch + x0 * bytes, &row[size_t(x0 - sl) * size_t(bytes)], (x1 - x0) * bytes);
        for (int x = std::max(l, 0); x < std::min(r, ScreenWidth); x++)
        {
            int fx = sl + int(int64_t(x - l) * (sr - sl) / (r - l));
            if (fx < x0 || fx >= x1)
                continue;
            size_t s = size_t(fx - sl) * size_t(bytes), d = (size_t(y) * size_t(ScreenWidth) + size_t(x)) * 3;
            m_window[d] = row[s];
            m_window[d + 1] = row[s + 1];
            m_window[d + 2] = row[s + 2];
        }
    }
    ScreenInvalidated = true;
    FrameShown = true;
}

void ScnVm::PresentedByDirect3D(int l, int t, int r, int b)
{
    // v2.50 presents the display surface with Direct3D, so a copy into it shows without
    // InvalidateRect; earlier versions (GDI) leave it unseen
    if (EngineVersion >= 250)
        InvalidateWindow(std::min(l, r), std::min(t, b), std::max(l, r), std::max(t, b));
}

void ScnVm::RunPaintSlot(int global)
{
    int slot = EngineGlobalOr(global, -1);
    if (slot >= 0 && slot < Slots)
        RunSlotSync(slot);
}

} // namespace openshiina::scripting
