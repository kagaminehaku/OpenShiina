// Engine v2.33 (Cleavage's CLEAVAGE.EXE, 2005; Vanquish's VANQ.EXE is the same build; the table is
// Data/ScnOps/ops_v233.tsv, v2.34's but for 06EC): the opcodes its scripts use that Ao no Juuai's
// (v2.34) do not. Registered for the engines before 2.40 only (Register234); the handlers named
// are CLEAVAGE.EXE's.
//
// Its movies: RIO.INI's MovieMode 2 plays them with DirectDraw and DirectShow streams (05B5,
// ScnVm.Movie.cs), 0 and 1 in a window of their own over the game's (MCI or DirectShow,
// 0578-057F, the engine's records at 0x48DA40 / 0x48C740, 09CF switching between the two). Here
// that window is drawn into the game window's picture, the movie stretched to its rectangle.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // The movie of 0579 and the rectangle of 0578
    private readonly Movie m_windowMovie = new();
    private ScreenArea m_windowMovieArea;

    private void RegisterEngine233()
    {
        // 0029 ms (0x415B50): the task sleeps ms (the time at 0x49984C + slot * 4, the task's flag
        // 2 until the main loop finds it has passed); here as 0028, a frame at a time
        Register234(0x0029, (vm, c, i) =>
        {
            int ms = vm.Value(c, i.Args[0]);
            if (!vm.m_sleeps234.TryGetValue(c.Slot, out uint start))
                vm.m_sleeps234[c.Slot] = start = vm.Clock;
            if (ms > 0 && vm.Clock - start < (uint)ms)
            {
                c.Pc = c.Current;
                vm.FrameShown = true;
                return 3;
            }
            vm.m_sleeps234.Remove(c.Slot);
            return 0;
        });
        // 0263 end, index, labels... (0x40FF00; YODUMA.EXE 0x41E6DE, v2.37, the same): gosub the
        // index-th label of the list (the return to the end of the list); an index past the list
        // is an error
        Register(0x0263, (vm, c, i) =>
        {
            int index = vm.Value(c, i.Args[0]);
            if (index < 0 || index >= i.Targets.Length)
                return 2;
            vm.Push(c, c.Base);
            vm.Push(c, c.Base + i.Raw[0]);
            c.Pc = c.Base + i.Targets[index];
            return 0;
        });
        // 06DD s, ms, d / 06DE s, ms, d (0x41D170 / 0x41D1E0 -> FUN_0043F5B0, its timer
        // 0x43F730): the volume goes up (06DD) or down (06DE) by d (0: down by 1) every
        // (ms / 5 + 1) ticks of 5 ms (ms / 5 at least 1) until it passes 100 (it stays 100) or 0
        // (the stream stops: its flag 0xE8, set when it is made); nothing for a stream not playing
        Register234(0x06DD, (vm, c, i) => vm.FadeOld(c, i, 1));
        Register234(0x06DE, (vm, c, i) => vm.FadeOld(c, i, -1));
        // FDF2 v, FE06 v, FE10 v (YODUMA.EXE 0x42221F, 0x422289, v2.37): an operand read, nothing
        // done (Tekoire Princess!'s START runs FE10 and FE06 when a game starts). v2.47 has
        // handlers of its own for them, not read yet
        foreach (int op in new[] { 0xFDF2, 0xFE06, 0xFE10 })
            RegisterBefore240(op, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        // Vanquish (VANQ.EXE, v2.33, the same build as CLEAVAGE.EXE). 0190 name (0x417F70): the
        // game folder's DLL loaded (its START loads Export.dll), the one before freed; 2 when it is
        // not there. The player loads no DLL and answers as loaded; 0191 name (0x417FC0, a function
        // of it called by name: catpath, Brows, FileOperation, SetCopyFile... the copying of files
        // of the menu that exports them) is not run
        Register234(0x0190, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        // 046A v (0x417D00): the cursor shape v (-1: none) - SetCursor; the host draws its own
        Register234(0x046A, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        // 073E v (0x41B430): v = the screen has a mode of the window's size at its colour depth
        // (EnumDisplaySettings), full screen being possible
        Register234(0x073E, (vm, c, i) => { vm.Store(c, i.Args[0], 1); return 0; });
        // 07C6 v (0x41A230): an engine flag (0x46716C) = v
        Register234(0x07C6, (vm, c, i) => { vm.EngineGlobals[0x46716C] = vm.Value(c, i.Args[0]); return 0; });
        // 08CA (0x419B00): DrawMenuBar; 08D4 menu, item, flags (0x419B20): EnableMenuItem - the
        // window's menu bar, which the player's window does not have
        Register234(0x08CA, (vm, c, i) => 0);
        Register234(0x08D4, (vm, c, i) =>
        {
            foreach (var arg in i.Args)
                vm.Value(c, arg);
            return 0;
        });
        // 0A29 text (0x41AAE0): the text copied into a path of the engine's (0x6D9358); 0A2A text
        // (0x41AB10): SetCurrentDirectoryA (2 when it fails). The player's files are found from
        // the game folder, whatever the current directory
        Register234(0x0A29, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        Register234(0x0A2A, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        // 06E0 s, v (VANQ.EXE 0x41D2A0 -> FUN_0043F280 with its third argument 0): the stream's
        // volume as DirectSound takes it (hundredths of a dB, 0 to -10000), not by 06DF's table
        // of 0-100; nothing without a stream
        Register234(0x06E0, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            int volume = vm.Value(c, i.Args[1]);
            if (s != null && s.Handle != 0)
                vm.Music!.SetVolume(s.Handle, volume);
            return 0;
        });
        // 050A to, from, ms, flags (VANQ.EXE 0x411950 -> FUN_004089D0, its blend FUN_00427EE0): the
        // screen (surface 0) goes from surface from (with bit 31: the grey from & 0xFF) to surface
        // to over ms, the window painted at each step: each byte (from * (255 - t) + to * t) / 255,
        // t the part of ms passed out of 255. The engine waits in its own loop until ms have
        // passed; here the task waits a frame at a time. At the end surface 0 is to. Vanquish's
        // START draws the new screen, keeps it in surface 1, puts the old one back on the screen
        // and runs 050A 1, old
        Register234(0x050A, (vm, c, i) =>
        {
            int to = vm.Value(c, i.Args[0]), from = vm.Value(c, i.Args[1]), ms = vm.Value(c, i.Args[2]);
            vm.Value(c, i.Args[3]);
            if (!vm.m_crossfades.TryGetValue(c.Slot, out uint start))
                vm.m_crossfades[c.Slot] = start = vm.Clock;
            uint passed = vm.Clock - start;
            int t = ms <= 0 || passed >= (uint)ms ? 255 : (int)(passed * 255 / (uint)ms);
            vm.Crossfade(from, to, t);
            vm.InvalidateWindow(0, 0, vm.ScreenWidth, vm.ScreenHeight);
            vm.FrameShown = true;
            if (t < 255)
            {
                c.Pc = c.Current;
                return 3;
            }
            vm.m_crossfades.Remove(c.Slot);
            return 0;
        });
        // 073A v (0x41B230): v = DirectShow is there (0x466F5C, made when it is not yet); the
        // player decodes the movies itself
        Register234(0x073A, (vm, c, i) => { vm.Store(c, i.Args[0], 1); return 0; });
        // 07BE (0x41A1C0): WaitMessage - the task waits for the next frame
        Register234(0x07BE, (vm, c, i) =>
        {
            vm.FrameShown = true;
            return 3;
        });
        // 09CF mode (0x413760): the window movies' player, MCI (0) or DirectShow (other), the
        // movies playing carried over to the other at the same position; one player here
        Register234(0x09CF, (vm, c, i) => { vm.Value(c, i.Args[0]); return 0; });
        // 0578 x, y, w, h (0x413A10): the movie window's rectangle
        Register234(0x0578, (vm, c, i) =>
        {
            int x = vm.Value(c, i.Args[0]), y = vm.Value(c, i.Args[1]);
            int w = vm.Value(c, i.Args[2]), h = vm.Value(c, i.Args[3]);
            vm.m_windowMovieArea = new ScreenArea(x, y, x + w, y + h);
            return 0;
        });
        // 0579 file, v (0x413C40): open the movie (a file of the game folder) and play it in the
        // window (FUN_00413AD0 0, 1); v is kept with it (0 in Cleavage's scripts). A movie that
        // cannot be played here is as one played to its end
        Register234(0x0579, (vm, c, i) =>
        {
            string name = vm.ReadString(vm.Value(c, i.Args[0]));
            vm.Value(c, i.Args[1]);
            var movie = vm.m_windowMovie;
            vm.MovieSoundStop(movie);
            movie.Name = name;
            movie.Loop = 0;
            movie.LoopCount = 0;
            movie.Video = null;
            movie.Window = vm.m_windowMovieArea;
            if (!vm.OpenMovie(movie))
            {
                movie.State = 1;
                vm.Trace?.Add($"f{vm.m_frameNumber} 0579 {name}: not played");
                return 0;
            }
            vm.RunMovie(movie);
            vm.UpdateMovie(movie);
            return 0;
        });
        // 057A (0x413CF0): stop the movie and close it
        Register234(0x057A, (vm, c, i) =>
        {
            var movie = vm.m_windowMovie;
            vm.MovieSoundStop(movie);
            movie.State = movie.Video != null || movie.State != 0 ? 1 : 0;
            movie.Video = null;
            return 0;
        });
        // 057E v (0x413DC0): v = the movie's mode, MCI's 0x20E playing, else 0x20D stopped
        Register234(0x057E, (vm, c, i) =>
        {
            vm.UpdateMovie(vm.m_windowMovie);
            vm.Store(c, i.Args[0], vm.m_windowMovie.State == 3 ? 0x20E : 0x20D);
            return 0;
        });
        // 057F v (0x413E00): v = its position (ms here), 0 before it plays; START waits until it is not 0
        Register234(0x057F, (vm, c, i) =>
        {
            var movie = vm.m_windowMovie;
            vm.UpdateMovie(movie);
            vm.Store(c, i.Args[0], movie.State switch { 3 => Math.Max(1, (int)(vm.Clock - movie.Start)), 0 => 0, _ => 1 });
            return 0;
        });
    }

    // 050A's crossfades by task: when they started
    private readonly Dictionary<int, uint> m_crossfades = new();

    /// <summary>Surface 0 = surface <paramref name="from"/> (or the grey from &amp; 0xFF when its bit 31 is set) mixed with surface <paramref name="to"/> by t / 255.</summary>
    private void Crossfade(int from, int to, int t)
    {
        bool grey = from < 0;
        if ((uint)to >= SurfaceCount || !grey && from >= SurfaceCount)
            return;
        int dst = SurfaceField(0, 2), b = SurfaceField(to, 2), a = grey ? 0 : SurfaceField(from, 2);
        if (dst == 0 || b == 0 || !grey && a == 0)
            return;
        int bytesPerPixel = SurfaceField(0, 9) >> 3;
        int width = Math.Min(SurfaceField(0, 7), SurfaceField(to, 7)), height = Math.Min(SurfaceField(0, 8), SurfaceField(to, 8));
        if (!grey)
        {
            width = Math.Min(width, SurfaceField(from, 7));
            height = Math.Min(height, SurfaceField(from, 8));
        }
        int bytes = width * bytesPerPixel, value = from & 0xFF;
        for (int y = 0; y < height; y++)
        {
            var rowB = ReadBytes(b + y * SurfaceField(to, 10), bytes);
            var rowA = grey ? null : ReadBytes(a + y * SurfaceField(from, 10), bytes);
            for (int x = 0; x < bytes; x++)
                rowB[x] = (byte)(((rowA?[x] ?? value) * (255 - t) + rowB[x] * t) / 255);
            WriteBytes(dst + y * SurfaceField(0, 10), rowB);
        }
    }

    /// <summary>06DD / 06DE: a fade of v2.33's music streams, up (sign 1) or down (-1).</summary>
    private int FadeOld(ScnContext c, ScnInstruction i, int sign)
    {
        var s = Stream(Value(c, i.Args[0]));
        int ms = Value(c, i.Args[1]), d = Value(c, i.Args[2]) * sign;
        if (s == null)
            return 0;
        s.FadeStepMs = 0;
        if ((s.Status & 1) == 0)
            return 0;
        s.FadeStepMs = (Math.Max((int)((uint)ms / 5), 1) + 1) * 5;
        s.FadeDelta = d == 0 ? -1 : d;
        s.FadeOld = true;
        s.FadeDue = Clock + (uint)s.FadeStepMs;
        return 0;
    }

    /// <summary>A movie frame into the game window's picture, stretched to the movie window's rectangle.</summary>
    private void DrawMovieToWindow(MpegFrame frame, ScreenArea area)
    {
        int w = frame.Width, h = frame.Height;
        if (w <= 0 || h <= 0 || area.IsEmpty)
            return;
        var rows = m_movieRows is { } r && r.Length >= w * 3 * h ? r : m_movieRows = new byte[w * 3 * h];
        frame.ToBgr24(rows, w * 3, w, h);
        var window = m_window ??= new byte[ScreenWidth * ScreenHeight * 3];
        m_changed = m_changed.Union(area.Clip(ScreenWidth, ScreenHeight));
        for (int y = Math.Max(area.T, 0); y < Math.Min(area.B, ScreenHeight); y++)
        {
            int fy = (int)((long)(y - area.T) * h / area.Height);
            for (int x = Math.Max(area.L, 0); x < Math.Min(area.R, ScreenWidth); x++)
            {
                int fx = (int)((long)(x - area.L) * w / area.Width);
                int s = (fy * w + fx) * 3, d = (y * ScreenWidth + x) * 3;
                window[d] = rows[s];
                window[d + 1] = rows[s + 1];
                window[d + 2] = rows[s + 2];
            }
        }
        ScreenInvalidated = true;
        FrameShown = true;
    }
}
