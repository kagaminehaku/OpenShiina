// C# Scripting/ScnVm.Movie.cs: movies. The executable plays them with DirectShow "multimedia
// streams" (16 records of 0x158 bytes at 0x4B7BB0): a DirectDraw stream sample draws each frame
// into a surface at (0, 0) in the movie's own size, and the script copies that surface where it
// wants it (0514 to the screen, 055E into a picture). IStreamSample::Update (op 05C0) waits for the
// next frame and draws it; at the end of the stream it starts again (looping movies) or stops the
// movie. START uses this path when RIO.INI MovieMode is 0 or 1; MovieMode 2 selects the other
// player (05C9-05D8, C# ScnVm.GraphMovie.cs).
//
// The C# player decodes the movies (Formats/Mpeg1Video, Formats/Vc1Video and their sounds); those
// decoders are not ported yet, so here every movie is one the C# player cannot decode: 05B5 leaves
// it as if it had played to its end, the surface as it was, and the game goes on past it.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <array>

namespace openshiina::scripting {

/// Opens the movie file of a record (FUN_0040B450); false when it cannot be played.
bool ScnVm::OpenMovie(Movie&)
{
    return false;
}

/// FUN_0040B530: plays a stopped movie from the start, or a paused one on.
bool ScnVm::RunMovie(Movie& movie)
{
    return OpenMovie(movie);
}

void ScnVm::RegisterMovie()
{
    // 05B5 n, file, loop, surface: open a movie that draws into surface n (-1: the screen)
    // and play it (FUN_0041C4B0)
    Register(0x05B5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        std::string name = vm.ReadString(vm.Value(c, i.Args[1]));
        Movie* movie = vm.MovieSlot(n);
        if (movie == nullptr)
            return 2;
        movie->Name = name;
        movie->Loop = vm.Value(c, i.Args[2]);
        movie->LoopCount = 0;
        int surface = vm.Value(c, i.Args[3]);
        movie->Surface = surface == -1 ? vm.DisplaySurface() : surface;
        if (movie->Surface < 0 || movie->Surface >= SurfaceCount || vm.SurfaceField(movie->Surface, 2) == 0)
            return 2;
        movie->State = 0;
        if (!vm.OpenMovie(*movie))
        {
            // A movie that is there but cannot be decoded here: it is as if it had played to its
            // end, the surface left as it was
            if (!vm.m_host.LooseFileSize(name) && !vm.ScriptFileSize(name))
                return 2;
            movie->Width = vm.ScreenWidth;
            movie->Height = vm.ScreenHeight;
            movie->State = 1;
            return 0;
        }
        return vm.RunMovie(*movie) ? 0 : 2;
    });
    // 05B6 n: stop (FUN_0040B780); the loop count becomes -1
    Register(0x05B6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        if (movie->State == 2 || movie->State == 3)
            movie->State = 1;
        movie->LoopCount = -1;
        return 0;
    });
    // 05B7 n: pause (FUN_0040B720)
    Register(0x05B7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        if (movie->State == 1 || movie->State == 3)
            movie->State = 2;
        return 0;
    });
    // 05B8 n: play
    Register(0x05B8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        vm.RunMovie(*movie);
        return 0;
    });
    // 05B9 n, v: v = 0x20D stopped, 0x211 paused, 0x20E playing (FUN_0040B7F0)
    Register(0x05B9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        vm.Store(c, i.Args[1], movie->State == 2 ? 0x211 : movie->State == 3 ? 0x20E : 0x20D);
        return 0;
    });
    // 05BC n, v: v = how many times the movie started again
    Register(0x05BC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        vm.Store(c, i.Args[1], movie->LoopCount);
        return 0;
    });
    // 05BF n, w, h: the movie's size
    Register(0x05BF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
        vm.Store(c, i.Args[1], movie != nullptr ? movie->Width : 0);
        vm.Store(c, i.Args[2], movie != nullptr ? movie->Height : 0);
        return 0;
    });
    // 05C0 n: draw the next frame into the surface (FUN_0040B8B0); no movie runs here
    Register(0x05C0, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
    // 05C1 n: 05C0, then the movie's surface Blt straight onto the window at its rectangle
    // (+0x20: 0, 0, w, h); only for a movie that was decoded
    Register(0x05C1, [](ScnVm&, ScnContext&, const ScnInstruction&) { return 0; });
    // 05C3 n, volume: volume 0-100 (FUN_0040B9A0)
    Register(0x05C3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), volume = vm.Value(c, i.Args[1]);
        if (Movie* movie = vm.MovieSlot(n))
            movie->Volume = volume;
        return 0;
    });
    // 0514 dst, x, y, w, h, src, sx, sy, rop (FUN_0041DC10): BitBlt (src -1: the window);
    // drawing into the shown surface shows it
    Register(0x0514, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::array<int, 9> v{};
        for (int k = 0; k < 9; k++)
            v[size_t(k)] = vm.Value(c, i.Args[size_t(k)]);
        int dst = v[0], src = v[5] == -1 ? vm.DisplaySurface() : v[5];
        if (v[8] != 0xCC0020)
            return 2;
        vm.BitBlt(dst, v[1], v[2], v[3], v[4], src, v[6], v[7]);
        if (dst == vm.DisplaySurface())
        {
            vm.PresentedByDirect3D(v[1], v[2], v[1] + v[3], v[2] + v[4]);
            vm.FrameShown = true;
        }
        return 0;
    });
    // 0516 dst, l, t, r, b, src, sl, st, sr, sb, flags, fx (FUN_0041DED0): DirectDraw's Blt of
    // the rectangle sl..sb of a surface onto l..b of another, stretched; dst -1 is the window
    // (client rectangle, not scaled). Only DDBLT_WAIT is used (Azu Plus: movies in a window)
    Register(0x0516, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::array<int, 12> v{};
        for (int k = 0; k < 12; k++)
            v[size_t(k)] = vm.Value(c, i.Args[size_t(k)]);
        if ((v[10] & ~0x01000000) != 0 || v[11] != 0 || v[5] < 0 || v[5] >= SurfaceCount)
            return 2;
        if (v[0] == -1)
            vm.BltToWindow(v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
        else if (v[0] >= 0 && v[0] < SurfaceCount)
        {
            vm.StretchCopy(v[0], v[1], v[2], v[3] - v[1], v[4] - v[2], v[5], v[6], v[7], v[8] - v[6], v[9] - v[7]);
            if (v[0] == vm.DisplaySurface())
                vm.FrameShown = true;
        }
        else
            return 2;
        return 0;
    });
    // 055E slot, frame, x, y, w, h, surface, sx, sy (FUN_00410BF0): a rectangle of a surface
    // into a picture's frame at (x, y); the rectangle is clipped to the surface only
    Register(0x055E, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::array<int, 9> v{};
        for (int k = 0; k < 9; k++)
            v[size_t(k)] = vm.Value(c, i.Args[size_t(k)]);
        vm.SurfaceToPicture(vm.Picture(v[0]), v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
        return 0;
    });
}

void ScnVm::SurfaceToPicture(int picture, int frame, int x, int y, int w, int h, int surface, int sx, int sy)
{
    if (picture == 0 || uint32_t(frame) > uint32_t(Read32(picture + 4)) || surface < 0 || surface >= SurfaceCount)
        return;
    int sw = SurfaceField(surface, 7), sh = SurfaceField(surface, 8), pitch = SurfaceField(surface, 10);
    int pixels = SurfaceField(surface, 2);
    // FUN_00417790 on the source rectangle; the destination moves with its left / top edge
    int l = sx, t = sy, r = sx + w, b = sy + h;
    if (l > sw || t > sh)
        return;
    if (l < 0)
    {
        if (r <= 0)
            return;
        x -= l;
        l = 0;
    }
    if (t < 0)
    {
        if (b <= 0)
            return;
        y -= t;
        t = 0;
    }
    r = std::min(r, sw);
    b = std::min(b, sh);
    int f = Read32(picture + 8 + 4 * frame);
    if (f == 0 || pixels == 0)
        return;
    int bytes = FrameBytes(picture, frame);
    int width = r - l, rows = b - t;
    int frameWidth = Read32(f), frameHeight = Read32(f + 4);
    if (width <= 0 || rows <= 0)
        return;
    int stride = frameWidth * bytes + 8;
    int src = pixels + t * pitch + l * bytes;
    // Rows follow each other in the frame; stay inside it
    if (y < 0 || x < 0 || y + rows > frameHeight)
        rows = std::max(0, std::min(rows, frameHeight - y));
    if (y < 0 || x < 0)
        return;
    int dst = Read32(f + 0x14 + 4 * y) + bytes * x + 8;
    std::vector<uint8_t> line(size_t(width) * 3);
    std::vector<uint8_t> wide(bytes == 4 ? size_t(width) * 4 : 0);
    for (int row = 0; row < rows; row++, src += pitch, dst += stride)
    {
        if (wide.empty())
        {
            CopyMemory(dst, src, width * 3);
            continue;
        }
        ReadBytes(src, line.data(), int(line.size()));
        for (int k = 0; k < width; k++)
        {
            wide[size_t(k) * 4] = 0xFF;
            wide[size_t(k) * 4 + 1] = line[size_t(k) * 3];
            wide[size_t(k) * 4 + 2] = line[size_t(k) * 3 + 1];
            wide[size_t(k) * 4 + 3] = line[size_t(k) * 3 + 2];
        }
        WriteBytes(dst, wide.data(), int(wide.size()));
    }
}

} // namespace openshiina::scripting
