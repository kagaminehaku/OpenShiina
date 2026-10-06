// The other movie player (RIO.INI MovieMode 2; ERO-ON always uses it): a DirectShow filter graph
// with a sample grabber (16 records, 0x1B0 bytes at 0x4B9130 in v2.47, 0x220 at 0x4F14F0 in
// v2.49). The graph runs on its own and the grabber keeps the latest frame; the scripts copy
// it into a surface with op_05D4 (SetDIBitsToDevice at x, y). At the end of the movie (EC_COMPLETE)
// a looping movie goes back to the start and counts, any other is closed.
//
// v2.47 and v2.49 differ from op_05D1 on: in v2.49 the grabber itself draws each new frame into
// the place op_05D4 / op_05D5 set (05D4 waits for the frame that is due, 05D5 does not, 05D6 only
// waits), and op_05C9's flags can send the frames into a surface (bit 31; the surface in bits
// 16-23). Here the frames are decoded by Formats/Mpeg1Video and the sound played as for the
// other player.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private readonly Movie[] m_graphMovies = Enumerable.Range(0, MovieSlots).Select(_ => new Movie()).ToArray();

    private Movie? GraphMovie(int n) => n is >= 0 and < MovieSlots ? m_graphMovies[n] : null;

    private bool Version249 => EngineVersion >= 249;

    /// <summary>FUN_0040C140: the graph goes; the record is empty (state 0).</summary>
    private void CloseGraphMovie(Movie movie)
    {
        MovieSoundStop(movie);
        movie.Video = null;
        movie.State = 0;
        movie.Target = false;
        movie.LastRows = null;
    }

    /// <summary>
    /// The graph running on: decodes the frames that are due and keeps the latest; true when
    /// there is a new one. At the end a looping movie starts again, any other is closed.
    /// </summary>
    private bool AdvanceGraphMovie(Movie movie)
    {
        if (movie.State != 3 || movie.Video is not { } video)
            return false;
        uint now = m_host.Milliseconds;
        long due = (long)((now - movie.Start) * video.FrameRate / 1000);
        MpegFrame? frame = null;
        while (movie.Decoded <= due)
        {
            frame = video.Next();
            if (frame == null)
                break;
            movie.Decoded++;
        }
        if (frame != null)
        {
            int w = frame.Width, h = frame.Height;
            if (movie.LastRows == null || movie.LastRows.Length != w * h * 3)
                movie.LastRows = new byte[w * h * 3];
            frame.ToBgr24(movie.LastRows, w * 3, w, h);
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

    /// <summary>SetDIBitsToDevice of the latest frame at the movie's place (or into its surface, v2.49 bit 31).</summary>
    private void DrawGraphMovie(Movie movie)
    {
        if (movie.LastRows is not { } rows || movie.Video is not { } video)
            return;
        int surface = movie.DirectSurface >= 0 ? movie.DirectSurface : movie.Surface;
        if (surface is < 0 or >= SurfaceCount || SurfaceField(surface, 2) == 0)
            return;
        int w = video.Width, h = video.Height;
        int sw = SurfaceField(surface, 7), sh = SurfaceField(surface, 8), pitch = SurfaceField(surface, 10);
        int bytes = SurfaceField(surface, 9) >> 3, pixels = SurfaceField(surface, 2);
        if (bytes is not (3 or 4))
            return;
        int x0 = Math.Max(movie.X, 0), x1 = Math.Min(movie.X + w, sw), y0 = Math.Max(movie.Y, 0), y1 = Math.Min(movie.Y + h, sh);
        if (x1 <= x0 || y1 <= y0)
            return;
        var line = new byte[(x1 - x0) * bytes];
        for (int y = y0; y < y1; y++)
        {
            int from = ((y - movie.Y) * w + x0 - movie.X) * 3;
            if (bytes == 3)
                rows.AsSpan(from, line.Length).CopyTo(line);
            else
                for (int k = 0, q = from; k < line.Length; k += 4, q += 3)
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

    /// <summary>v2.49: the grabber draws each new frame where op_05D4 / op_05D5 put it (once a host frame).</summary>
    private void PumpGraphMovies()
    {
        if (!Version249)
            return;
        foreach (var movie in m_graphMovies)
            if (movie.State == 3 && movie.Target && AdvanceGraphMovie(movie))
                DrawGraphMovie(movie);
    }

    private void RegisterGraphMovie()
    {
        // 05C9 n, file, loop (v2.49: file 0 keeps the one there; flags: bit 0 loop, bit 31 into
        // the surface of bits 16-23): open and play (FUN_00407360, FUN_0040BFE0)
        Register(0x05C9, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]), file = vm.Value(c, i.Args[1]);
            if (vm.GraphMovie(n) is not { } movie)
                return 2;
            int flags = vm.Value(c, i.Args[2]);
            if (vm.Version249)
            {
                movie.Loop = flags & 1;
                movie.DirectSurface = flags < 0 ? (flags >> 16) & 0xFF : -1;
            }
            else
                movie.Loop = flags;
            movie.LoopCount = 0;
            if (file != 0 || !vm.Version249)
            {
                movie.Name = vm.ReadString(file);
                vm.CloseGraphMovie(movie);
                if (!vm.OpenMovie(movie))
                    return 2;
            }
            return vm.RunMovie(movie) ? 0 : 2;
        });
        // 05D1 n, file, loop: open without playing (v2.49 leaves the loop flag as it is)
        Register(0x05D1, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            string name = vm.ReadString(vm.Value(c, i.Args[1]));
            int loop = vm.Value(c, i.Args[2]);
            if (vm.GraphMovie(n) is not { } movie)
                return 2;
            movie.Name = name;
            if (!vm.Version249)
            {
                movie.Loop = loop;
                movie.LoopCount = 0;
            }
            vm.CloseGraphMovie(movie);
            return vm.OpenMovie(movie) ? 0 : 2;
        });
        // 05CA n: stop and close; the loop count becomes -1
        Register(0x05CA, (vm, c, i) =>
        {
            if (vm.GraphMovie(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            if (movie.State is 2 or 3)
                vm.CloseGraphMovie(movie);
            movie.LoopCount = -1;
            return 0;
        });
        // 05CB n: pause (FUN_0040A670; from stopped it shows the first frame)
        Register(0x05CB, (vm, c, i) =>
        {
            if (vm.GraphMovie(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            if (movie.State is 1 or 3)
            {
                if (movie.State == 1)
                {
                    movie.Video?.Rewind();
                    movie.Decoded = 0;
                    movie.Start = vm.m_host.Milliseconds;
                }
                movie.PausedAt = vm.m_host.Milliseconds;
                movie.State = 2;
                vm.MovieSoundPause(movie);
            }
            return 0;
        });
        // 05CC n: play (opens the file again when it was closed)
        Register(0x05CC, (vm, c, i) =>
        {
            if (vm.GraphMovie(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            if (movie.State == 0 && movie.Name.Length > 0)
                vm.OpenMovie(movie);
            vm.RunMovie(movie);
            return 0;
        });
        // 05CD n, v: v = 0x20D stopped or closed, 0x211 paused, 0x20E playing (FUN_0040C3B0)
        Register(0x05CD, (vm, c, i) =>
        {
            if (vm.GraphMovie(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            vm.AdvanceGraphMovie(movie);
            vm.Store(c, i.Args[1], movie.State switch { 2 => 0x211, 3 => 0x20E, _ => 0x20D });
            return 0;
        });
        // 05CE n, v: v = the position in ms (IMediaPosition::get_CurrentPosition * 1000), -1
        // when neither playing nor paused
        Register(0x05CE, (vm, c, i) =>
        {
            if (vm.GraphMovie(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            vm.AdvanceGraphMovie(movie);
            int position = movie.State switch
            {
                3 => (int)(vm.m_host.Milliseconds - movie.Start),
                2 => (int)(movie.PausedAt - movie.Start),
                _ => -1,
            };
            vm.Store(c, i.Args[1], position);
            return 0;
        });
        // 05CF n, s: go to s seconds (put_CurrentPosition) while playing or paused
        Register(0x05CF, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            uint seconds = (uint)vm.Value(c, i.Args[1]);
            if (vm.GraphMovie(n) is not { } movie)
                return 2;
            if (movie.State is 2 or 3 && movie.Video is { } video)
            {
                uint now = vm.m_host.Milliseconds;
                video.Rewind();
                movie.Decoded = 0;
                movie.Start = (movie.State == 2 ? movie.PausedAt : now) - seconds * 1000;
                if (movie.State == 3)
                    vm.MovieSoundStart(movie, seconds * 1000L);
                else
                    vm.MovieSoundStop(movie);
            }
            return 0;
        });
        // 05D0 n, v: v = how many times the movie started again
        Register(0x05D0, (vm, c, i) =>
        {
            if (vm.GraphMovie(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            vm.Store(c, i.Args[1], movie.LoopCount);
            return 0;
        });
        // 05D2 n, ..., -1: play every one of them that is open
        Register(0x05D2, (vm, c, i) =>
        {
            foreach (var a in i.Args)
            {
                int n = vm.Value(c, a);
                if (n == -1)
                    break;
                if (vm.GraphMovie(n) is { Video: not null } movie)
                    vm.RunMovie(movie);
            }
            return 0;
        });
        // 05D3 n, w, h: the movie's size (FUN_0040BEC0)
        Register(0x05D3, (vm, c, i) =>
        {
            var movie = vm.GraphMovie(vm.Value(c, i.Args[0]));
            vm.Store(c, i.Args[1], movie?.Video != null ? movie.Width : 0);
            vm.Store(c, i.Args[2], movie?.Video != null ? movie.Height : 0);
            return 0;
        });
        // 05D4 n, surface, x, y: the frame that is due into the surface at (x, y) (FUN_0040C4D0;
        // v2.49 FUN_0040CF50: from now on the grabber draws there, surface -1 none)
        Register(0x05D4, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]), surface = vm.Value(c, i.Args[1]);
            int x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
            if (vm.GraphMovie(n) is not { } movie || movie.State is not (2 or 3))
                return 0;
            movie.Surface = surface;
            movie.X = x;
            movie.Y = y;
            movie.Target = true;
            // The executable waits here (Sleep) until the frame is due: with no new frame the
            // host frame ends, so that time goes on
            if (!vm.AdvanceGraphMovie(movie))
                vm.FrameShown = true;
            vm.DrawGraphMovie(movie);
            return 0;
        });
        // 05D5 n, surface, x, y (v2.49): where the grabber draws, without waiting (FUN_0040CE90);
        // 05D5 n, v (v2.47): v = the volume
        Register(0x05D5, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            if (!vm.Version249)
            {
                vm.Store(c, i.Args[1], vm.GraphMovie(n)?.Volume ?? 0);
                return 0;
            }
            int surface = vm.Value(c, i.Args[1]), x = vm.Value(c, i.Args[2]), y = vm.Value(c, i.Args[3]);
            if (vm.GraphMovie(n) is { State: 2 or 3 } movie)
            {
                movie.Surface = surface;
                movie.X = x;
                movie.Y = y;
                movie.Target = true;
            }
            return 0;
        });
        // 05D6 n (v2.49): wait for the frame that is due (FUN_0040CED0); 05D6 n, v (v2.47): the volume
        Register(0x05D6, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            if (!vm.Version249)
            {
                int volume = vm.Value(c, i.Args[1]);
                if (vm.GraphMovie(n) is { } m)
                    vm.MovieSoundVolume(m, volume);
                return 0;
            }
            if (vm.GraphMovie(n) is { State: 2 or 3 } movie)
            {
                if (!vm.AdvanceGraphMovie(movie))
                    vm.FrameShown = true;
                else if (movie.Target)
                    vm.DrawGraphMovie(movie);
            }
            return 0;
        });
        // 05D7 n, v (v2.49): v = the volume; 05D8 n, v: set it (IBasicAudio)
        Register(0x05D7, (vm, c, i) =>
        {
            vm.Store(c, i.Args[1], vm.GraphMovie(vm.Value(c, i.Args[0]))?.Volume ?? 0);
            return 0;
        });
        Register(0x05D8, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]), volume = vm.Value(c, i.Args[1]);
            if (vm.GraphMovie(n) is { } movie)
                vm.MovieSoundVolume(movie, volume);
            return 0;
        });
    }
}
