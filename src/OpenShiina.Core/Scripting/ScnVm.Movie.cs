// Movies. The executable plays them with DirectShow "multimedia streams" (16 records of 0x158
// bytes at 0x4B7BB0): a DirectDraw stream sample draws each frame into a surface at (0, 0) in
// the movie's own size, and the script copies that surface where it wants it (0514 to the
// screen, 055E into a picture). IStreamSample::Update (op 05C0) waits for the next frame and
// draws it; at the end of the stream it starts again (looping movies) or stops the movie.
// START uses this path when RIO.INI MovieMode is 0 or 1 (b[9] = 1); MovieMode 2 selects the
// other player (05C9-05D8, ScnVm.GraphMovie.cs).
//
// Here the movie is decoded by Formats/Mpeg1Video and frame n is due n / rate seconds after the
// start; 05C0 draws the latest frame that is due. The sound (MPEG-1 Layer II, only in a few
// movies; Formats/MpegAudio) plays through the music streams (IScnMusic), started, paused and
// stopped with the picture, at the volume of the music streams' table.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private sealed class Movie
    {
        /// <summary>+0: 0 none, 1 stopped, 2 paused, 3 running.</summary>
        public int State;
        public string Name = "";
        /// <summary>+0x10: the movie starts again at its end.</summary>
        public int Loop;
        /// <summary>+0x18: how many times it started again (-1 after a stop).</summary>
        public int LoopCount;
        public int Surface;
        public int Width, Height;
        /// <summary>+0x34: volume 0-100.</summary>
        public int Volume = 100;
        public Mpeg1Video? Video;
        /// <summary>Frames decoded since the start, and the time frame 0 was due.</summary>
        public int Decoded;
        public uint Start;
        public uint PausedAt;
        /// <summary>The other player: where op_05D4 draws (surface, x, y), the latest frame, and v2.49's surface of op_05C9.</summary>
        public bool Target;
        public int X, Y;
        public int DirectSurface = -1;
        public byte[]? LastRows;
        /// <summary>The movie's sound as a WAVE (null when it has none) and its music stream while it plays.</summary>
        public byte[]? Wave;
        public int Sound;
    }

    private const int MovieSlots = 16;
    private readonly Movie[] m_movies = Enumerable.Range(0, MovieSlots).Select(_ => new Movie()).ToArray();

    private Movie? MovieSlot(int n) => n is >= 0 and < MovieSlots ? m_movies[n] : null;

    /// <summary>
    /// Opens the movie file of a record (FUN_0040B450); false when it cannot be played. Windows
    /// Media files (v2.50's ending, mv\ed.wmv) play as the MPEG-1 movie of the same name the games
    /// ship beside them: DirectShow plays either, the decoder here MPEG-1 only.
    /// </summary>
    private bool OpenMovie(Movie movie)
    {
        if (OpenMovieFile(movie, movie.Name))
            return true;
        return movie.Name.EndsWith(".wmv", StringComparison.OrdinalIgnoreCase)
            && OpenMovieFile(movie, Path.ChangeExtension(movie.Name, ".mpg"));
    }

    private bool OpenMovieFile(Movie movie, string name)
    {
        var data = m_host.ReadLooseFile(name) ?? ReadScriptFile(name);
        if (data == null)
            return false;
        MovieSoundStop(movie);
        try
        {
            var stream = new MpegSystemStream(data);
            movie.Video = new Mpeg1Video(stream.Video);
            if (!m_movieWaves.TryGetValue(name, out movie.Wave))
                m_movieWaves[name] = movie.Wave = Music != null ? MpegAudio.ToWave(stream.Audio) : null;
        }
        catch (InvalidDataException)
        {
            movie.Video = null;
            return false;
        }
        movie.Width = movie.Video.Width;
        movie.Height = movie.Video.Height;
        movie.State = 1;
        return true;
    }

    /// <summary>FUN_0040B530: plays a stopped movie from the start, or a paused one on.</summary>
    private bool RunMovie(Movie movie)
    {
        if (movie.Video == null && !OpenMovie(movie))
            return false;
        uint now = Clock;
        if (movie.State == 1)
        {
            movie.Video!.Rewind();
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

    // Movie sounds decoded so far, by file
    private readonly Dictionary<string, byte[]?> m_movieWaves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The movie's sound from the start (or <paramref name="milliseconds"/> into it).</summary>
    private void MovieSoundStart(Movie movie, long milliseconds = 0)
    {
        MovieSoundStop(movie);
        if (movie.Wave is not { } wave || Music is not { } music)
            return;
        movie.Sound = music.Open(milliseconds > 0 ? MpegAudio.From(wave, milliseconds) : wave);
        if (movie.Sound == 0)
            return;
        music.SetVolume(movie.Sound, s_musicVolumes[Math.Clamp(movie.Volume, 0, 100)]);
        music.Play(movie.Sound, false);
    }

    private void MovieSoundStop(Movie movie)
    {
        if (movie.Sound == 0)
            return;
        Music?.Stop(movie.Sound);
        Music?.Close(movie.Sound);
        movie.Sound = 0;
    }

    private void MovieSoundPause(Movie movie)
    {
        if (movie.Sound != 0)
            Music?.Pause(movie.Sound);
    }

    /// <summary>On from a pause (a movie paused before it played starts its sound where the picture is).</summary>
    private void MovieSoundResume(Movie movie)
    {
        if (movie.Sound != 0)
            Music?.Resume(movie.Sound);
        else
            MovieSoundStart(movie, Clock - movie.Start);
    }

    private void MovieSoundVolume(Movie movie, int volume)
    {
        movie.Volume = volume;
        if (movie.Sound != 0)
            Music?.SetVolume(movie.Sound, s_musicVolumes[Math.Clamp(volume, 0, 100)]);
    }

    /// <summary>IStreamSample::Update: draws the latest frame that is due; handles the end.</summary>
    private void UpdateMovie(Movie movie)
    {
        if (movie.State != 3 || movie.Video is not { } video)
            return;
        uint now = Clock;
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
            DrawMovieFrame(movie, frame);
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

    private byte[]? m_movieRows;

    private void DrawMovieFrame(Movie movie, MpegFrame frame)
    {
        int s = movie.Surface;
        int pixels = SurfaceField(s, 2);
        if (pixels == 0)
            return;
        int w = Math.Min(frame.Width, SurfaceField(s, 7)), h = Math.Min(frame.Height, SurfaceField(s, 8));
        int pitch = SurfaceField(s, 10), bpp = SurfaceField(s, 9);
        if (w <= 0 || h <= 0)
            return;
        var rows = m_movieRows is { } r && r.Length >= w * 3 * h ? r : m_movieRows = new byte[w * 3 * h];
        frame.ToBgr24(rows, w * 3, w, h);
        if (bpp == 24)
        {
            for (int y = 0; y < h; y++)
                WriteBytes(pixels + y * pitch, rows.AsSpan(y * w * 3, w * 3));
            return;
        }
        if (bpp != 32)
            return;
        var line = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                line[x * 4] = rows[(y * w + x) * 3];
                line[x * 4 + 1] = rows[(y * w + x) * 3 + 1];
                line[x * 4 + 2] = rows[(y * w + x) * 3 + 2];
            }
            WriteBytes(pixels + y * pitch, line);
        }
    }

    private void RegisterMovie()
    {
        // 05B5 n, file, loop, surface: open a movie that draws into surface n (-1: the screen)
        // and play it (FUN_0041C4B0)
        Register(0x05B5, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            string name = vm.ReadString(vm.Value(c, i.Args[1]));
            if (vm.MovieSlot(n) is not { } movie)
                return 2;
            movie.Name = name;
            movie.Loop = vm.Value(c, i.Args[2]);
            movie.LoopCount = 0;
            int surface = vm.Value(c, i.Args[3]);
            movie.Surface = surface == -1 ? vm.DisplaySurface : surface;
            if (movie.Surface is < 0 or >= SurfaceCount || vm.SurfaceField(movie.Surface, 2) == 0)
                return 2;
            vm.MovieSoundStop(movie);
            movie.Video = null;
            movie.State = 0;
            if (!vm.OpenMovie(movie))
                return 2;
            return vm.RunMovie(movie) ? 0 : 2;
        });
        // 05B6 n: stop (FUN_0040B780); the loop count becomes -1
        Register(0x05B6, (vm, c, i) =>
        {
            if (vm.MovieSlot(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            if (movie.State is 2 or 3)
            {
                movie.State = 1;
                vm.MovieSoundStop(movie);
            }
            movie.LoopCount = -1;
            return 0;
        });
        // 05B7 n: pause (FUN_0040B720)
        Register(0x05B7, (vm, c, i) =>
        {
            if (vm.MovieSlot(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            if (movie.State is 1 or 3)
            {
                if (movie.State == 1)
                {
                    movie.Video?.Rewind();
                    movie.Decoded = 0;
                    movie.Start = vm.Clock;
                }
                movie.PausedAt = vm.Clock;
                movie.State = 2;
                vm.MovieSoundPause(movie);
            }
            return 0;
        });
        // 05B8 n: play
        Register(0x05B8, (vm, c, i) =>
        {
            if (vm.MovieSlot(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            vm.RunMovie(movie);
            return 0;
        });
        // 05B9 n, v: v = 0x20D stopped, 0x211 paused, 0x20E playing (FUN_0040B7F0)
        Register(0x05B9, (vm, c, i) =>
        {
            if (vm.MovieSlot(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            vm.Store(c, i.Args[1], movie.State switch { 2 => 0x211, 3 => 0x20E, _ => 0x20D });
            return 0;
        });
        // 05BC n, v: v = how many times the movie started again
        Register(0x05BC, (vm, c, i) =>
        {
            if (vm.MovieSlot(vm.Value(c, i.Args[0])) is not { } movie)
                return 2;
            vm.Store(c, i.Args[1], movie.LoopCount);
            return 0;
        });
        // 05BF n, w, h: the movie's size
        Register(0x05BF, (vm, c, i) =>
        {
            var movie = vm.MovieSlot(vm.Value(c, i.Args[0]));
            vm.Store(c, i.Args[1], movie?.Width ?? 0);
            vm.Store(c, i.Args[2], movie?.Height ?? 0);
            return 0;
        });
        // 05C0 n: draw the next frame into the surface (FUN_0040B8B0)
        Register(0x05C0, (vm, c, i) =>
        {
            if (vm.MovieSlot(vm.Value(c, i.Args[0])) is { } movie)
                vm.UpdateMovie(movie);
            return 0;
        });
        // 05C1 n: 05C0, then the movie's surface Blt straight onto the window at its rectangle
        // (+0x20: 0, 0, w, h). Azu Plus plays 800 x 600 movies so in a window
        Register(0x05C1, (vm, c, i) =>
        {
            if (vm.MovieSlot(vm.Value(c, i.Args[0])) is not { } movie)
                return 0;
            vm.UpdateMovie(movie);
            if (movie.Video != null)
                vm.BltToWindow(0, 0, movie.Width, movie.Height, movie.Surface, 0, 0, movie.Width, movie.Height);
            return 0;
        });
        // 05C3 n, volume: volume 0-100 (FUN_0040B9A0)
        Register(0x05C3, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]), volume = vm.Value(c, i.Args[1]);
            if (vm.MovieSlot(n) is { } movie)
                vm.MovieSoundVolume(movie, volume);
            return 0;
        });
        // 0514 dst, x, y, w, h, src, sx, sy, rop (FUN_0041DC10): BitBlt (src -1: the window);
        // drawing into the shown surface shows it
        Register(0x0514, (vm, c, i) =>
        {
            var v = new int[9];
            for (int k = 0; k < 9; k++)
                v[k] = vm.Value(c, i.Args[k]);
            int dst = v[0], src = v[5] == -1 ? vm.DisplaySurface : v[5];
            if (v[8] != 0xCC0020)
                return 2;
            vm.BitBlt(dst, v[1], v[2], v[3], v[4], src, v[6], v[7]);
            if (dst == vm.DisplaySurface)
            {
                vm.PresentedByDirect3D(v[1], v[2], v[1] + v[3], v[2] + v[4]);
                vm.FrameShown = true;
            }
            return 0;
        });
        // 0516 dst, l, t, r, b, src, sl, st, sr, sb, flags, fx (FUN_0041DED0): DirectDraw's Blt of
        // the rectangle sl..sb of a surface onto l..b of another, stretched; dst -1 is the window
        // (client rectangle, not scaled). Only DDBLT_WAIT is used (Azu Plus: movies in a window)
        Register(0x0516, (vm, c, i) =>
        {
            var v = new int[12];
            for (int k = 0; k < 12; k++)
                v[k] = vm.Value(c, i.Args[k]);
            if ((v[10] & ~0x01000000) != 0 || v[11] != 0 || v[5] is < 0 or >= SurfaceCount)
                return 2;
            if (v[0] == -1)
                vm.BltToWindow(v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
            else if (v[0] is >= 0 and < SurfaceCount)
            {
                vm.StretchCopy(v[0], v[1], v[2], v[3] - v[1], v[4] - v[2], v[5], v[6], v[7], v[8] - v[6], v[9] - v[7]);
                if (v[0] == vm.DisplaySurface)
                    vm.FrameShown = true;
            }
            else
                return 2;
            return 0;
        });
        // 055E slot, frame, x, y, w, h, surface, sx, sy (FUN_00410BF0): a rectangle of a surface
        // into a picture's frame at (x, y); the rectangle is clipped to the surface only
        Register(0x055E, (vm, c, i) =>
        {
            var v = new int[9];
            for (int k = 0; k < 9; k++)
                v[k] = vm.Value(c, i.Args[k]);
            vm.SurfaceToPicture(vm.Picture(v[0]), v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
            return 0;
        });
    }

    private void SurfaceToPicture(int picture, int frame, int x, int y, int w, int h, int surface, int sx, int sy)
    {
        if (picture == 0 || (uint)frame > (uint)Read32(picture + 4) || surface is < 0 or >= SurfaceCount)
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
        r = Math.Min(r, sw);
        b = Math.Min(b, sh);
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
            rows = Math.Max(0, Math.Min(rows, frameHeight - y));
        if (y < 0 || x < 0)
            return;
        int dst = Read32(f + 0x14 + 4 * y) + bytes * x + 8;
        var line = new byte[width * 3];
        var wide = bytes == 4 ? new byte[width * 4] : null;
        for (int row = 0; row < rows; row++, src += pitch, dst += stride)
        {
            if (wide == null)
            {
                CopyMemory(dst, src, width * 3);
                continue;
            }
            ReadBytes(src, line);
            for (int k = 0; k < width; k++)
            {
                wide[k * 4] = 0xFF;
                wide[k * 4 + 1] = line[k * 3];
                wide[k * 4 + 2] = line[k * 3 + 1];
                wide[k * 4 + 3] = line[k * 3 + 2];
            }
            WriteBytes(dst, wide);
        }
    }
}
