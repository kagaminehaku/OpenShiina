// C# Scripting/ScnVm.Paint.cs: the window's picture. The scripts draw into the display surface
// (0x13B43E4, a DIB in memory); the window shows it only where Windows asks for a repaint: the
// rectangles made invalid by op_07D0 (InvalidateRect) and by the effects that paint the window
// themselves, repainted by WM_PAINT at the next pump of the message queue or by op_07D1
// (UpdateWindow). That is the engine's GDI mode: only the invalid region changes. The hosts show
// this picture (Window), not the display surface, and copy only the part of it that changed
// (TakeChangedArea).
#include "scripting/ScnVm.h"

#include <algorithm>
#include <cstring>
#if defined(__ARM_NEON) || defined(__aarch64__)
#include <arm_neon.h>
#endif

namespace openshiina::scripting {

namespace {

// BGRX (4 bytes a pixel) to BGR, 16 pixels a step with NEON (C# PixelRows.BgrxToBgr)
void BgrxToBgr(const uint8_t* src, uint8_t* dst, int pixels)
{
    int x = 0;
#if defined(__ARM_NEON) || defined(__aarch64__)
    for (; x + 16 <= pixels; x += 16)
    {
        uint8x16x4_t v = vld4q_u8(src + x * 4);
        uint8x16x3_t bgr = {{v.val[0], v.val[1], v.val[2]}};
        vst3q_u8(dst + x * 3, bgr);
    }
#endif
    for (; x < pixels; x++)
    {
        dst[x * 3] = src[x * 4];
        dst[x * 3 + 1] = src[x * 4 + 1];
        dst[x * 3 + 2] = src[x * 4 + 2];
    }
}

// The bytes where two rows differ, [first, last); false when they are the same (C# PixelRows.Differs)
bool Differs(const uint8_t* a, const uint8_t* b, int length, int& first, int& last)
{
    first = 0;
    while (first + 64 <= length && std::memcmp(a + first, b + first, 64) == 0)
        first += 64;
    while (first < length && a[first] == b[first])
        first++;
    if (first >= length)
        return false;
    last = length;
    while (last - first > 64 && std::memcmp(a + last - 64, b + last - 64, 64) == 0)
        last -= 64;
    while (last > first + 1 && a[last - 1] == b[last - 1])
        last--;
    return true;
}

} // namespace

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
        // (the same rectangle may be invalid several times, and one of all the window covers the rest)
        if (std::any_of(m_invalid.begin(), m_invalid.end(),
                        [&](const auto& i) { return i[0] <= 0 && i[1] <= 0 && i[2] >= width && i[3] >= height; }))
        {
            m_invalid.clear();
            m_invalid.push_back({0, 0, width, height});
        }
        m_paintRow.resize(size_t(ScreenWidth) * 4);
        m_paintBgr.resize(size_t(ScreenWidth) * 3);
        for (const auto& [l, t, r0, b0] : m_invalid)
        {
            int r = std::min(r0, width), b = std::min(b0, height);
            for (int y = t; y < b && l < r; y++)
            {
                uint8_t* src = m_paintRow.data();
                ReadBytes(pixels + y * pitch + l * bytes, src, (r - l) * bytes);
                const uint8_t* line = src;
                if (bytes == 4)
                {
                    BgrxToBgr(src, m_paintBgr.data(), r - l);
                    line = m_paintBgr.data();
                }
                // Only what differs from the window's picture is written and counted as changed: a
                // screen invalidated whole each frame (Re:Rem Plus's title) costs only what moved
                uint8_t* dst = &m_window[(size_t(y) * size_t(ScreenWidth) + size_t(l)) * 3];
                int first = 0, last = 0;
                if (!Differs(line, dst, (r - l) * 3, first, last))
                    continue;
                std::memcpy(dst + first, line + first, size_t(last - first));
                m_changed = m_changed.Union({l + first / 3, y, l + (last + 2) / 3, y + 1});
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
    // From v2.49 on, a copy into the display surface shows by itself: the engine's copies there end
    // in a helper that is InvalidateRect in GDI mode, a Direct3D present otherwise (0x418780 in
    // every v2.49 executable read); v2.47's leave it unseen
    if (EngineVersion >= 249)
        InvalidateWindow(std::min(l, r), std::min(t, b), std::max(l, r), std::max(t, b));
}

void ScnVm::RunPaintSlot(int global)
{
    int slot = EngineGlobalOr(global, -1);
    if (slot >= 0 && slot < Slots)
        RunSlotSync(slot);
}

} // namespace openshiina::scripting
