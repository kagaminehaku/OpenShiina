// C# Platform/GdiShapes.cs: the part of the surface is copied into a DIB section of its size and the
// shape drawn there by GDI itself, so it comes out exactly as the game draws it.
#include "GdiShapes.h"

#include <cstring>
#include <vector>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace openshiina::tools {

void GdiShapes::Ellipse(uint8_t* pixels, int width, int height, int stride, int bytesPerPixel, int l, int t, int r, int b, std::optional<int> brush,
                        std::optional<scripting::ScnPen> pen)
{
    if (width <= 0 || height <= 0 || (bytesPerPixel != 3 && bytesPerPixel != 4))
        return;
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = WORD(bytesPerPixel * 8);
    HDC dc = CreateCompatibleDC(nullptr);
    void* bits = nullptr;
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bitmap)
    {
        DeleteDC(dc);
        return;
    }
    HGDIOBJ oldBitmap = SelectObject(dc, bitmap);
    size_t dibStride = size_t(width * bytesPerPixel + 3) & ~size_t(3), rowBytes = size_t(width) * size_t(bytesPerPixel);
    auto* dib = static_cast<uint8_t*>(bits);
    for (int y = 0; y < height; y++)
        std::memcpy(dib + size_t(y) * dibStride, pixels + size_t(y) * size_t(stride), rowBytes);
    HGDIOBJ brushHandle = brush ? HGDIOBJ(CreateSolidBrush(COLORREF(*brush))) : GetStockObject(NULL_BRUSH);
    HGDIOBJ penHandle = pen ? HGDIOBJ(CreatePen(pen->Style, pen->Width, COLORREF(pen->Color))) : GetStockObject(NULL_PEN);
    HGDIOBJ oldBrush = SelectObject(dc, brushHandle), oldPen = SelectObject(dc, penHandle);
    ::Ellipse(dc, l, t, r, b);
    GdiFlush();
    for (int y = 0; y < height; y++)
        std::memcpy(pixels + size_t(y) * size_t(stride), dib + size_t(y) * dibStride, rowBytes);
    SelectObject(dc, oldBrush);
    SelectObject(dc, oldPen);
    SelectObject(dc, oldBitmap);
    if (brush)
        DeleteObject(brushHandle);
    if (pen)
        DeleteObject(penHandle);
    DeleteObject(bitmap);
    DeleteDC(dc);
}

} // namespace openshiina::tools
