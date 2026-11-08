// C# Scripting/ScnVm.GraphMovie.cs: the other movie player (RIO.INI MovieMode 2; ERO-ON always uses
// it): a DirectShow filter graph with a sample grabber (16 records, 0x1B0 bytes at 0x4B9130 in v2.47,
// 0x220 at 0x4F14F0 in v2.49). The graph runs on its own and the grabber keeps the latest frame; the
// scripts copy it into a surface with op_05D4 (SetDIBitsToDevice at x, y). At the end of the movie
// (EC_COMPLETE) a looping movie goes back to the start and counts, any other is closed.
//
// v2.47 and v2.49 differ from op_05D1 on: in v2.49 the grabber itself draws each new frame into the
// place op_05D4 / op_05D5 set (05D4 waits for the frame that is due, 05D5 does not, 05D6 only
// waits), and op_05C9's flags can send the frames into a surface (bit 31; the surface in bits
// 16-23). Here the frames are decoded as for the other player (formats/MovieAhead) and the sound
// played as there.
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

void ScnVm::CloseGraphMovie(Movie& movie)
{
    // FUN_0040C140: the graph goes; the record is empty (state 0)
    MovieSoundStop(movie);
    movie.Video.reset();
    movie.State = 0;
    movie.Target = false;
    movie.LastRows.clear();
}

bool ScnVm::AdvanceGraphMovie(Movie& movie)
{
    // The graph running on: decodes the frames that are due and keeps the latest; true when there
    // is a new one. At the end a looping movie starts again, any other is closed
    if (movie.State != 3 || !movie.Video)
        return false;
    formats::MovieAhead& video = *movie.Video;
    uint32_t now = Clock();
    int64_t due = int64_t(double(now - movie.Start) * video.FrameRate() / 1000);
    const openshiina::Bytes* frame = nullptr;
    while (movie.Decoded <= due)
    {
        frame = video.Next();
        if (frame == nullptr)
            break;
        movie.Decoded++;
    }
    if (frame != nullptr)
    {
        movie.LastRows.assign(frame->begin(), frame->begin() + size_t(video.Width()) * size_t(video.Height()) * 3);
        return true;
    }
    if (movie.Decoded <= due)
    {
        if (movie.Loop != 0)
        {
            movie.LoopCount++;
            video.Rewind();
            movie.Decoded = 0;
            movie.Start = now;
            MovieSoundStart(movie);
        }
        else
            CloseGraphMovie(movie);
    }
    return false;
}

void ScnVm::DrawGraphMovie(Movie& movie)
{
    // SetDIBitsToDevice of the latest frame at the movie's place (or into its surface, v2.49 bit 31)
    if (movie.LastRows.empty() || !movie.Video)
        return;
    const openshiina::Bytes& rows = movie.LastRows;
    int surface = movie.DirectSurface >= 0 ? movie.DirectSurface : movie.Surface;
    if (surface < 0 || surface >= SurfaceCount || SurfaceField(surface, 2) == 0)
        return;
    int w = movie.Video->Width(), h = movie.Video->Height();
    int sw = SurfaceField(surface, 7), sh = SurfaceField(surface, 8), pitch = SurfaceField(surface, 10);
    int bytes = SurfaceField(surface, 9) >> 3, pixels = SurfaceField(surface, 2);
    if (bytes != 3 && bytes != 4)
        return;
    int x0 = std::max(movie.X, 0), x1 = std::min(movie.X + w, sw), y0 = std::max(movie.Y, 0), y1 = std::min(movie.Y + h, sh);
    if (x1 <= x0 || y1 <= y0)
        return;
    openshiina::Bytes line(size_t(x1 - x0) * size_t(bytes));
    for (int y = y0; y < y1; y++)
    {
        size_t from = (size_t(y - movie.Y) * size_t(w) + size_t(x0 - movie.X)) * 3;
        if (bytes == 3)
            std::copy_n(rows.begin() + std::ptrdiff_t(from), line.size(), line.begin());
        else
            for (size_t k = 0, q = from; k < line.size(); k += 4, q += 3)
            {
                line[k] = rows[q];
                line[k + 1] = rows[q + 1];
                line[k + 2] = rows[q + 2];
                line[k + 3] = 0;
            }
        WriteBytes(pixels + y * pitch + x0 * bytes, line);
    }
    Shown(surface);
}

void ScnVm::PumpGraphMovies()
{
    // v2.49: the grabber draws each new frame where op_05D4 / op_05D5 put it (once a host frame)
    if (!Version249())
        return;
    for (Movie& movie : m_graphMovies)
        if (movie.State == 3 && movie.Target && AdvanceGraphMovie(movie))
            DrawGraphMovie(movie);
}

void ScnVm::RegisterGraphMovie()
{
    // 05C9 n, file, loop (v2.49: file 0 keeps the one there; flags: bit 0 loop, bit 31 into the
    // surface of bits 16-23): open and play (FUN_00407360, FUN_0040BFE0)
    Register(0x05C9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), file = vm.Value(c, i.Args[1]);
        Movie* movie = vm.GraphMovie(n);
        if (movie == nullptr)
            return 2;
        int flags = vm.Value(c, i.Args[2]);
        if (vm.Version249())
        {
            movie->Loop = flags & 1;
            movie->DirectSurface = flags < 0 ? (flags >> 16) & 0xFF : -1;
        }
        else
            movie->Loop = flags;
        movie->LoopCount = 0;
        if (file != 0 || !vm.Version249())
        {
            movie->Name = vm.ReadString(file);
            vm.CloseGraphMovie(*movie);
            if (!vm.OpenMovie(*movie))
                return 2;
        }
        return vm.RunMovie(*movie) ? 0 : 2;
    });
    // 05D1 n, file, loop: open without playing (v2.49 leaves the loop flag as it is)
    Register(0x05D1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        std::string name = vm.ReadString(vm.Value(c, i.Args[1]));
        int loop = vm.Value(c, i.Args[2]);
        Movie* movie = vm.GraphMovie(n);
        if (movie == nullptr)
            return 2;
        movie->Name = name;
        if (!vm.Version249())
        {
            movie->Loop = loop;
            movie->LoopCount = 0;
        }
        vm.CloseGraphMovie(*movie);
        return vm.OpenMovie(*movie) ? 0 : 2;
    });
    // 05CA n: stop and close; the loop count becomes -1
    Register(0x05CA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        if (movie->State == 2 || movie->State == 3)
            vm.CloseGraphMovie(*movie);
        movie->LoopCount = -1;
        return 0;
    });
    // 05CB n: pause (FUN_0040A670; from stopped it shows the first frame)
    Register(0x05CB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        if (movie->State == 1 || movie->State == 3)
        {
            if (movie->State == 1)
            {
                if (movie->Video)
                    movie->Video->Rewind();
                movie->Decoded = 0;
                movie->Start = vm.Clock();
            }
            movie->PausedAt = vm.Clock();
            movie->State = 2;
            vm.MovieSoundPause(*movie);
        }
        return 0;
    });
    // 05CC n: play (opens the file again when it was closed)
    Register(0x05CC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        if (movie->State == 0 && !movie->Name.empty())
            vm.OpenMovie(*movie);
        vm.RunMovie(*movie);
        return 0;
    });
    // 05CD n, v: v = 0x20D stopped or closed, 0x211 paused, 0x20E playing (FUN_0040C3B0)
    Register(0x05CD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        vm.AdvanceGraphMovie(*movie);
        vm.Store(c, i.Args[1], movie->State == 2 ? 0x211 : movie->State == 3 ? 0x20E : 0x20D);
        return 0;
    });
    // 05CE n, v: v = the position in ms (IMediaPosition::get_CurrentPosition * 1000), -1 when
    // neither playing nor paused
    Register(0x05CE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        vm.AdvanceGraphMovie(*movie);
        int position = movie->State == 3 ? int(vm.Clock() - movie->Start) : movie->State == 2 ? int(movie->PausedAt - movie->Start) : -1;
        vm.Store(c, i.Args[1], position);
        return 0;
    });
    // 05CF n, s: go to s seconds (put_CurrentPosition) while playing or paused
    Register(0x05CF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        uint32_t seconds = uint32_t(vm.Value(c, i.Args[1]));
        Movie* movie = vm.GraphMovie(n);
        if (movie == nullptr)
            return 2;
        if ((movie->State == 2 || movie->State == 3) && movie->Video)
        {
            uint32_t now = vm.Clock();
            movie->Video->Rewind();
            movie->Decoded = 0;
            movie->Start = (movie->State == 2 ? movie->PausedAt : now) - seconds * 1000;
            if (movie->State == 3)
                vm.MovieSoundStart(*movie, int64_t(seconds) * 1000);
            else
                vm.MovieSoundStop(*movie);
        }
        return 0;
    });
    // 05D0 n, v: v = how many times the movie started again
    Register(0x05D0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 2;
        vm.Store(c, i.Args[1], movie->LoopCount);
        return 0;
    });
    // 05D2 n, ..., -1: play every one of them that is open
    Register(0x05D2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        for (const auto& a : i.Args)
        {
            int n = vm.Value(c, a);
            if (n == -1)
                break;
            if (Movie* movie = vm.GraphMovie(n); movie != nullptr && movie->Video)
                vm.RunMovie(*movie);
        }
        return 0;
    });
    // 05D3 n, w, h: the movie's size (FUN_0040BEC0)
    Register(0x05D3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        bool open = movie != nullptr && movie->Video;
        vm.Store(c, i.Args[1], open ? movie->Width : 0);
        vm.Store(c, i.Args[2], open ? movie->Height : 0);
        return 0;
    });
    // 05D4 n, surface, x, y: the frame that is due into the surface at (x, y) (FUN_0040C4D0; v2.49
    // FUN_0040CF50: from now on the grabber draws there, surface -1 none)
    Register(0x05D4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), surface = vm.Value(c, i.Args[1]);
        int x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
        Movie* movie = vm.GraphMovie(n);
        if (movie == nullptr || (movie->State != 2 && movie->State != 3))
            return 0;
        movie->Surface = surface;
        movie->X = x;
        movie->Y = y;
        movie->Target = true;
        // The executable waits here (Sleep) until the frame is due: with no new frame the host
        // frame ends, so that time goes on
        if (!vm.AdvanceGraphMovie(*movie))
            vm.FrameShown = true;
        vm.DrawGraphMovie(*movie);
        return 0;
    });
    // 05D5 n, surface, x, y (v2.49): where the grabber draws, without waiting (FUN_0040CE90);
    // 05D5 n, v (v2.47): v = the volume
    Register(0x05D5, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        if (!vm.Version249())
        {
            Movie* movie = vm.GraphMovie(n);
            vm.Store(c, i.Args[1], movie != nullptr ? movie->Volume : 0);
            return 0;
        }
        int surface = vm.Value(c, i.Args[1]), x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
        if (Movie* movie = vm.GraphMovie(n); movie != nullptr && (movie->State == 2 || movie->State == 3))
        {
            movie->Surface = surface;
            movie->X = x;
            movie->Y = y;
            movie->Target = true;
        }
        return 0;
    });
    // 05D6 n (v2.49): wait for the frame that is due (FUN_0040CED0); 05D6 n, v (v2.47): the volume
    Register(0x05D6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]);
        if (!vm.Version249())
        {
            int volume = vm.Value(c, i.Args[1]);
            if (Movie* movie = vm.GraphMovie(n))
                vm.MovieSoundVolume(*movie, volume);
            return 0;
        }
        if (Movie* movie = vm.GraphMovie(n); movie != nullptr && (movie->State == 2 || movie->State == 3))
        {
            if (!vm.AdvanceGraphMovie(*movie))
                vm.FrameShown = true;
            else if (movie->Target)
                vm.DrawGraphMovie(*movie);
        }
        return 0;
    });
    // 05D7 n, v (v2.49): v = the volume; 05D8 n, v: set it (IBasicAudio)
    Register(0x05D7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
        vm.Store(c, i.Args[1], movie != nullptr ? movie->Volume : 0);
        return 0;
    });
    Register(0x05D8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), volume = vm.Value(c, i.Args[1]);
        if (Movie* movie = vm.GraphMovie(n))
            vm.MovieSoundVolume(*movie, volume);
        return 0;
    });
}

} // namespace openshiina::scripting
