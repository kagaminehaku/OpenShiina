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
