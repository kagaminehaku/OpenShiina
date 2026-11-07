// C# Scripting/ScnVm.Movie.cs: movies. The executable plays them with DirectShow "multimedia
// streams" (16 records of 0x158 bytes at 0x4B7BB0): a DirectDraw stream sample draws each frame
// into a surface at (0, 0) in the movie's own size, and the script copies that surface where it
// wants it (0514 to the screen, 055E into a picture). IStreamSample::Update (op 05C0) waits for the
// next frame and draws it; at the end of the stream it starts again (looping movies) or stops the
// movie. START uses this path when RIO.INI MovieMode is 0 or 1; MovieMode 2 selects the other
// player (05C9-05D8, C# ScnVm.GraphMovie.cs).
//
// Here the movie is decoded by formats/MpegVideo (MPEG-1) and frame n is due n / rate seconds
// after the start; 05C0 draws the latest frame that is due. The sound (MPEG-1 Layer II) plays
// through the music streams (IScnMusic), started, paused and stopped with the picture, at the
// volume of the music streams' table. The C# player also decodes Windows Media (WMV3 / VC-1 in an
// ASF file, Formats/Vc1Video); not ported yet: such a movie with an MPEG-1 one beside it plays as
// that one, as the C# player does too, and one without is a movie that cannot be decoded (05B5
// leaves it as if it had played to its end).
#include "scripting/ScnVm.h"

#include <algorithm>
#include <array>

#include "formats/MpegAudio.h"
#include "platform/Files.h"
#include "text/Text.h"

namespace openshiina::scripting {

namespace {

bool EndsWithIgnoreCase(const std::string& text, const char* end)
{
    size_t n = std::char_traits<char>::length(end);
    return text.size() >= n && Text::EqualsIgnoreCase(text.substr(text.size() - n), end);
}

} // namespace

/// Opens the movie file of a record (FUN_0040B450); false when it cannot be played. A Windows
/// Media file (mv\*.wmv) the games also ship as MPEG-1 beside it plays as that MPEG-1 movie.
bool ScnVm::OpenMovie(Movie& movie)
{
    if (EndsWithIgnoreCase(movie.Name, ".wmv") && OpenMovieFile(movie, movie.Name.substr(0, movie.Name.size() - 4) + ".mpg"))
        return true;
    return OpenMovieFile(movie, movie.Name);
}

bool ScnVm::OpenMovieFile(Movie& movie, const std::string& name)
{
    openshiina::Bytes data;
    if (!m_host.ReadLooseFile(name, data) && !ReadScriptFile(name, data))
        return false;
    MovieSoundStop(movie);
    try
    {
        // Windows Media (an ASF file): not decoded here yet
        if (data.size() >= 4 && data[0] == 0x30 && data[1] == 0x26 && data[2] == 0xB2 && data[3] == 0x75)
            return false;
        if (data.size() >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0xB3)
        {
            // An MPEG-1 video stream alone, from its sequence header: the animations of Iyashinbo
            // The Motion (a\*.a, 800 x 600), which have no sound
            movie.Video = std::make_unique<formats::Mpeg1Video>(data);
            movie.Wave.clear();
        }
        else
        {
            formats::MpegSystemStream stream(data);
            data.clear();
            data.shrink_to_fit();
            movie.Wave = std::move(stream.Audio);
            movie.Video = std::make_unique<formats::Mpeg1Video>(std::move(stream));
        }
    }
    catch (const InvalidDataError&)
    {
        movie.Video.reset();
        return false;
    }
    if (Music() == nullptr || !formats::MpegAudio::IsMpegAudio(movie.Wave))
        movie.Wave.clear();
    movie.Width = movie.Video->Width();
    movie.Height = movie.Video->Height();
    movie.State = 1;
    return true;
}

/// FUN_0040B530: plays a stopped movie from the start, or a paused one on.
bool ScnVm::RunMovie(Movie& movie)
{
    if (!movie.Video && !OpenMovie(movie))
        return false;
    uint32_t now = Clock();
    if (movie.State == 1)
    {
        movie.Video->Rewind();
        movie.Decoded = 0;
        movie.Start = now;
        movie.State = 3;
        MovieSoundStart(movie);
    }
    else if (movie.State == 2)
    {
        movie.Start += now - movie.PausedAt;
        movie.State = 3;
        MovieSoundResume(movie);
    }
    return true;
}

/// The movie's sound from the start (or milliseconds into it).
void ScnVm::MovieSoundStart(Movie& movie, int64_t milliseconds)
{
    MovieSoundStop(movie);
    IScnMusic* music = Music();
    if (movie.Wave.empty() || music == nullptr)
        return;
    movie.Sound = music->Open(milliseconds > 0 ? formats::MpegAudio::From(movie.Wave, milliseconds) : movie.Wave);
    if (movie.Sound == 0)
        return;
    music->SetVolume(movie.Sound, MusicVolume(movie.Volume));
    music->Play(movie.Sound, false);
}

void ScnVm::MovieSoundStop(Movie& movie)
{
    if (movie.Sound == 0)
        return;
    if (IScnMusic* music = Music())
    {
        music->Stop(movie.Sound);
        music->Close(movie.Sound);
    }
    movie.Sound = 0;
}

void ScnVm::MovieSoundPause(Movie& movie)
{
    if (movie.Sound != 0)
        if (IScnMusic* music = Music())
            music->Pause(movie.Sound);
}

/// On from a pause (a movie paused before it played starts its sound where the picture is).
void ScnVm::MovieSoundResume(Movie& movie)
{
    if (movie.Sound != 0)
    {
        if (IScnMusic* music = Music())
            music->Resume(movie.Sound);
    }
    else
        MovieSoundStart(movie, int64_t(Clock() - movie.Start));
}

void ScnVm::MovieSoundVolume(Movie& movie, int volume)
{
    movie.Volume = volume;
    if (movie.Sound != 0)
        if (IScnMusic* music = Music())
            music->SetVolume(movie.Sound, MusicVolume(volume));
}

/// IStreamSample::Update: draws the latest frame that is due; handles the end.
void ScnVm::UpdateMovie(Movie& movie)
{
    if (movie.State != 3 || !movie.Video)
        return;
    formats::IMovieVideo& video = *movie.Video;
    uint32_t now = Clock();
    int64_t due = int64_t(double(now - movie.Start) * video.FrameRate() / 1000);
    const formats::MpegFrame* frame = nullptr;
    while (movie.Decoded <= due)
    {
        frame = video.Next();
        if (frame == nullptr)
            break;
        movie.Decoded++;
    }
    if (frame != nullptr)
        DrawMovieFrame(movie, *frame);
    else if (movie.Decoded <= due)
    {
        // The end of the stream
        if (movie.Loop != 0)
        {
            movie.LoopCount++;
            video.Rewind();
            movie.Decoded = 0;
            movie.Start = now;
            MovieSoundStart(movie);
        }
        else
        {
            movie.State = 1;
            MovieSoundStop(movie);
        }
    }
}

void ScnVm::DrawMovieFrame(const Movie& movie, const formats::MpegFrame& frame)
{
    int s = movie.Surface;
    int pixels = SurfaceField(s, 2);
    if (pixels == 0)
        return;
    int w = std::min(frame.Width, SurfaceField(s, 7)), h = std::min(frame.Height, SurfaceField(s, 8));
    int pitch = SurfaceField(s, 10), bpp = SurfaceField(s, 9);
    if (w <= 0 || h <= 0)
        return;
    if (m_movieRows.size() < size_t(w) * 3 * size_t(h))
        m_movieRows.resize(size_t(w) * 3 * size_t(h));
    frame.ToBgr24(m_movieRows.data(), w * 3, w, h);
    if (bpp == 24)
    {
        for (int y = 0; y < h; y++)
            WriteBytes(pixels + y * pitch, m_movieRows.data() + size_t(y) * size_t(w) * 3, w * 3);
        return;
    }
    if (bpp != 32)
        return;
    std::vector<uint8_t> line(size_t(w) * 4);
    for (int y = 0; y < h; y++)
    {
        const uint8_t* row = m_movieRows.data() + size_t(y) * size_t(w) * 3;
        for (int x = 0; x < w; x++)
        {
            line[size_t(x) * 4] = row[x * 3];
            line[size_t(x) * 4 + 1] = row[x * 3 + 1];
            line[size_t(x) * 4 + 2] = row[x * 3 + 2];
        }
        WriteBytes(pixels + y * pitch, line.data(), int(line.size()));
    }
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
        vm.MovieSoundStop(*movie);
        movie->Video.reset();
        movie->State = 0;
        if (!vm.OpenMovie(*movie))
        {
            // A movie that is there but cannot be decoded here (a format or a Windows Media
            // feature not supported): it is as if it had played to its end, the surface left as
            // it was
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
        {
            movie->State = 1;
            vm.MovieSoundStop(*movie);
        }
        movie->LoopCount = -1;
        return 0;
    });
    // 05B7 n: pause (FUN_0040B720)
    Register(0x05B7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
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
    // 05C0 n: draw the next frame into the surface (FUN_0040B8B0)
    Register(0x05C0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        if (Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0])))
            vm.UpdateMovie(*movie);
        return 0;
    });
    // 05C1 n: 05C0, then the movie's surface Blt straight onto the window at its rectangle
    // (+0x20: 0, 0, w, h). Azu Plus plays 800 x 600 movies so in a window
    Register(0x05C1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        Movie* movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
        if (movie == nullptr)
            return 0;
        vm.UpdateMovie(*movie);
        if (movie->Video)
            vm.BltToWindow(0, 0, movie->Width, movie->Height, movie->Surface, 0, 0, movie->Width, movie->Height);
        return 0;
    });
    // 05C3 n, volume: volume 0-100 (FUN_0040B9A0)
    Register(0x05C3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int n = vm.Value(c, i.Args[0]), volume = vm.Value(c, i.Args[1]);
        if (Movie* movie = vm.MovieSlot(n))
            vm.MovieSoundVolume(*movie, volume);
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
