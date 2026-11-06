// C# Scripting/ScnVm.Surfaces.cs: the engine's picture memory - numbered surfaces ("VRAM pages",
// 0x7DDF90 + n * 0x2C in the executable), each a top-down DIB in CPU memory that the opcodes and
// the embedded MMX routines draw into. The descriptor keeps the executable's layout so that script
// code reading it sees the same fields:
//   [0] HBITMAP  [1] HDC  [2] pixel address  [3] BITMAPINFO  [4] palette  [5] flags | 2
//   [6] DirectDraw surface  [7] width  [8] height  [9] bits per pixel  [10] bytes per row
// Pixels are 24-bit BGR by default (RIO.INI Bpp, default 24).
#include "scripting/ScnVm.h"

namespace openshiina::scripting {

void ScnVm::CreateSurface(int index, int width, int height, int bpp, int flags)
{
    int d = SurfaceAddress(index);
    int pitch = (bpp >> 3) * width;
    // A surface made again lets its old bitmap go
    if (int old = Read32(d + 8); old != 0)
        Free(old);
    int pixels = Allocate(pitch * height);
    for (int f = 0; f < 11; f++)
        Write32(d + 4 * f, 0);
    Write32(d + 0, 0x00020000 + index);     // stand-in handles, never 0
    Write32(d + 4, 0x00030000 + index);
    Write32(d + 8, pixels);
    Write32(d + 20, flags | 2);
    Write32(d + 28, width);
    Write32(d + 32, height);
    Write32(d + 36, bpp);
    Write32(d + 40, pitch);
}

void ScnVm::FreeSurface(int index)
{
    int d = SurfaceAddress(index);
    int pixels = Read32(d + 8);
    if (pixels != 0)
        Free(pixels);
    Write32(d + 20, 0);
    Write32(d + 4, 0);
    Write32(d, 0);
    Write32(d + 8, 0);
    Write32(d + 12, 0);
}

void ScnVm::MakePages()
{
    // Surfaces 0 to Vram - 1 of RIO.INI (default 2; surface 0 is shown, 1 is the composed
    // picture), the size of the window, made before the first frame
    if (m_pagesMade)
        return;
    m_pagesMade = true;
    int pages = IniInt("RIO.INI", RioSection(), "Vram", 2);
    if (pages == 0)
        pages = 1;
    for (int i = 0; i < pages && i < SurfaceCount; i++)
        CreateSurface(i, ScreenWidth, ScreenHeight, Bpp, 0);
}

int ScnVm::TableAddress(int index) const
{
    auto found = m_tables.find(index);
    return found == m_tables.end() ? 0 : found->second;
}

void ScnVm::RegisterSurfaces()
{
    // 0546 n: surface n the size of the window (bits 30-31 of n are flags)
    Register(0x0546, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int raw = vm.Value(c, i.Args[0]);
        int flags = raw < 0 ? int(uint32_t(raw) & 0xC0000000u) : 0;
        int index = raw < 0 ? raw & 0x3FFFFFFF : raw;
        if (index < 0 || index >= SurfaceCount)
            return 2;
        vm.CreateSurface(index, vm.ScreenWidth, vm.ScreenHeight, vm.Bpp, flags);
        return 0;
    });
    // 0548 n, w, h, bpp, flags: surface n of that size
    Register(0x0548, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int index = vm.Value(c, i.Args[0]), width = vm.Value(c, i.Args[1]), height = vm.Value(c, i.Args[2]);
        int bpp = vm.Value(c, i.Args[3]), flags = vm.Value(c, i.Args[4]);
        if (index < 0 || index >= SurfaceCount || width <= 0 || height <= 0 || (bpp != 24 && bpp != 32))
            return 2;
        vm.CreateSurface(index, width, height, bpp, flags);
        return 0;
    });
    // 0547 n: free surface n (when it was made)
    Register(0x0547, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        if (n >= 0 && n < SurfaceCount && (vm.SurfaceField(n, 5) & 2) != 0)
            vm.FreeSurface(n);
        return 0;
    });
    // 0528 n, v: v = surface n's pixels; 0529 n, v: its device context (-1: the window's)
    Register(0x0528, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        vm.Store(c, i.Args[1], n >= 0 && n < SurfaceCount ? vm.SurfaceField(n, 2) : 0);
        return 0;
    });
    Register(0x0529, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        vm.Store(c, i.Args[1], n == -1 ? 0x00040001 : n >= 0 && n < SurfaceCount ? vm.SurfaceField(n, 1) : 0);
        return 0;
    });
    // 04B5 n, count: table n of count (0 = 1024) 32-byte entries; 04B6 n: free it
    Register(0x04B5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), count = vm.Value(c, i.Args[1]);
        vm.m_tables[n] = vm.Allocate((count == 0 ? 0x400 : count) << 5);
        return 0;
    });
    Register(0x04B6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.m_tables.erase(vm.Value(c, i.Args[0])); return 0; });
    // 00DD archive: mount a WAR archive (a file in several of them comes from a mounted one);
    // 00DF archive: unmount it
    Register(0x00DD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.MountArchive(vm.ReadString(vm.Value(c, i.Args[0])), true);
        return 0;
    });
    Register(0x00DF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.MountArchive(vm.ReadString(vm.Value(c, i.Args[0])), false);
        return 0;
    });
    // 0A8D: detach the input method from the window
    Register(0x0A8D, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
}

} // namespace openshiina::scripting
