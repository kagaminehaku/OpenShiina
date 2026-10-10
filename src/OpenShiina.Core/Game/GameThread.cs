// A game running: the interpreter on a thread of its own, so slow frames never hold up the
// window (its input, moving, resizing). It runs a frame (RunFrame: until the scripts show a
// picture) at the pace the settings ask for (PlayerSettings.FramePacing):
//   - the game's: up to 60 frames a second, or later when the scripts asked to sleep longer
//     (002A), as the engine's own loop goes (its Sleep(1) a round sleeps a tick of the Windows
//     clock); a new picture is posted to the window (FrameReady), which draws only then, so a
//     game that waits for a key costs next to nothing, as the original does;
//   - the screen's: each frame the window shows lets the interpreter run one frame, topped up
//     after 1/60 s when the window draws fewer (or none, minimised).
// A picture the scripts showed is copied, as 32-bit BGRA, into a buffer the window takes - only
// the part that changed (ScnVm.TakeChangedArea), and the window takes only what changed since it
// last took a picture. Window events and messages (focus,
// Alt+Enter, keys, mouse buttons, the wheel, closing) are queued for the interpreter's thread, or
// put off to later frames (Schedule: a tap on a phone, so that scripts reading the buttons once a
// frame see each step however long a frame takes); the
// title, the answer to closing and the end of the game are posted to the window's thread (the
// SynchronizationContext Start is called on). Used by both players.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace OpenShiina.Game;

public sealed class GameThread : IDisposable
{
    private readonly ScnVm m_vm;
    private readonly string m_saveFolder;
    private readonly Thread m_thread;
    private readonly SemaphoreSlim m_frameTick = new(0, 1);
    private readonly bool m_gamePace;
    // A FrameReady posted to the window and not run yet
    private int m_readyPosted;
    private readonly ConcurrentQueue<Action<ScnVm>> m_events = new();
    // Schedule's steps not due yet: the frame (m_frames) each runs before, in the order given
    private readonly List<(int Frame, Action<ScnVm> Do)> m_scheduled = new();
    private SynchronizationContext? m_window;
    private volatile bool m_stop, m_finished, m_closed;

    // The latest picture (BGRA, Width x Height) and whether the window has taken it; what the
    // back buffer lacks of the latest picture (it is a picture behind), and what changed since
    // the window took one
    private readonly object m_frameLock = new();
    private byte[] m_front, m_back;
    private bool m_frameNew;
    private ScreenArea m_backStale, m_untaken;

    // Frames a second and the slowest frame in the title, perf.log with OPENSHIINA_PERF=log
    private readonly PerfMeter? m_perf;

    // OPENSHIINA_PAINT=surface (for finding problems): show the display surface every frame, as
    // the players did before ScnVm.Paint, instead of the window's picture
    private readonly bool m_showSurface = Environment.GetEnvironmentVariable("OPENSHIINA_PAINT") == "surface";

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// The interpreter keeps its own pace (PlayerSettings.FramePacing.Game): the window draws on
    /// <see cref="FrameReady"/> instead of every refresh, and need not call <see cref="FrameTick"/>.
    /// </summary>
    public bool PacesItself => m_gamePace;

    /// <summary>A new picture to take (on the window's thread), at the game's pace.</summary>
    public event Action? FrameReady;

    /// <summary>The title the frame rate meter asks for (on the window's thread).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>
    /// The game stopped (on the window's thread): the scripts ended it (error null), or an error,
    /// with the path of the crash log it was written to (null when it could not be written).
    /// </summary>
    public event Action<Exception?, string?>? Stopped;

    /// <summary>The answer to <see cref="RequestClose"/> (on the window's thread): true when the window may close.</summary>
    public event Action<bool>? CloseAnswered;

    /// <summary>The interpreter is still running frames (not ended, stopped by an error, or closed).</summary>
    public bool Running => !m_finished && !m_stop && !m_closed;

    public GameThread(ScnVm vm, GameSetup setup, string name)
    {
        m_vm = vm;
        m_saveFolder = setup.SaveFolder;
        Width = setup.Width;
        Height = setup.Height;
        m_perf = PerfMeter.Create(vm, name, setup.SaveFolder, setup.Settings);
        m_gamePace = setup.Settings.FramePacing == FramePacing.Game;
        if (setup.Settings.DrawTraceForGame())
            vm.Trace = new DrawTrace();
        m_front = new byte[Width * Height * 4];
        m_back = new byte[Width * Height * 4];
        m_thread = new Thread(Run) { IsBackground = true, Name = "OpenShiina interpreter" };
    }

    /// <summary>Starts the interpreter; events are raised on the calling thread's SynchronizationContext.</summary>
    public void Start()
    {
        m_window = SynchronizationContext.Current ?? new SynchronizationContext();
        m_thread.Start();
        new Thread(Watch) { IsBackground = true, Name = "OpenShiina stall watch" }.Start();
    }

    // Frames the interpreter has finished, for the stall watch
    private volatile int m_frames;

    /// <summary>
    /// A frame that runs over 5 s: where the interpreter is and where its time goes (counted for
    /// 2 s) are appended to stall.log in the save folder, once a stall.
    /// </summary>
    private void Watch()
    {
        int last = -1;
        var since = Stopwatch.StartNew();
        bool reported = false;
        while (!m_stop && !m_finished)
        {
            Thread.Sleep(500);
            int frames = m_frames;
            if (frames != last || m_closed)
            {
                last = frames;
                since.Restart();
                reported = false;
                m_vm.HotSpots = null;
                continue;
            }
            double seconds = since.Elapsed.TotalSeconds;
            if (reported || seconds < 5)
                continue;
            if (m_vm.HotSpots == null)
            {
                m_vm.HotSpots = new();
                continue;
            }
            if (seconds < 7)
                continue;
            reported = true;
            WriteStall(frames, seconds);
            m_vm.HotSpots = null;
        }
    }

    private void WriteStall(int frame, double seconds)
    {
        var hot = m_vm.HotSpots;
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: frame {frame + 1} has run {seconds:F0} s, {m_vm.FrameRounds} rounds");
        if (m_vm.CurrentTask is { } task)
            text.AppendLine($"  now slot {task.Slot}: {m_vm.DescribeAddress(task.Current)}, flags {task.Flags:X}");
        if (hot != null)
            lock (hot)
            {
                long total = hot.Values.Sum(v => (long)v);
                text.AppendLine($"  {total} instructions in 2 s; the most run:");
                foreach (var ((slot, codeBase, offset, op), n) in hot.OrderByDescending(h => h.Value).Take(20))
                    text.AppendLine($"    slot {slot,3} {m_vm.DescribeAddress(codeBase + offset)}: {n,10} op {op:X4}");
            }
        try
        {
            File.AppendAllText(Path.Combine(m_saveFolder, "stall.log"), text.ToString());
        }
        catch (IOException)
        {
            // Only for finding problems
        }
    }

    /// <summary>The window drew a frame: the interpreter may run the next one.</summary>
    public void FrameTick()
    {
        if (m_frameTick.CurrentCount == 0)
        {
            try
            {
                m_frameTick.Release();
            }
            catch (SemaphoreFullException)
            {
                // Released by another tick in between
            }
        }
    }

    /// <summary>A window event for the scripts (focus, Alt+Enter, closing).</summary>
    public void Post(ScnEvent e) => m_events.Enqueue(vm => vm.Notify(e));

    /// <summary>A window message for the scripts (ScnMessage: keys, mouse buttons, the wheel).</summary>
    public void PostMessage(int message, int wParam, int lParam) => m_events.Enqueue(vm => vm.WindowMessage(message, wParam, lParam));

    /// <summary>
    /// Steps run on the interpreter's thread before frames to come: frame 0 is the next one it
    /// runs, frame 1 the one after it, and so on (a step of a later frame never runs before one
    /// of an earlier frame, and steps of one frame run in the order given).
    /// </summary>
    public void Schedule(params (int Frame, Action<ScnVm> Do)[] steps) => m_events.Enqueue(_ =>
    {
        int now = m_frames;
        foreach (var (frame, step) in steps)
            m_scheduled.Add((now + Math.Max(frame, 0), step));
    });

    /// <summary>Runs the scheduled steps due by this frame.</summary>
    private void RunScheduled()
    {
        if (m_scheduled.Count == 0)
            return;
        // Stable: in frame order, the order given within a frame
        var due = m_scheduled.Where(s => s.Frame <= m_frames).OrderBy(s => s.Frame).ToList();
        if (due.Count == 0)
            return;
        m_scheduled.RemoveAll(s => s.Frame <= m_frames);
        foreach (var (_, step) in due)
            step(m_vm);
    }

    /// <summary>
    /// The player closes the window (WM_CLOSE): the scripts run what they do on closing (START
    /// saves what it keeps) and answer with <see cref="CloseAnswered"/>; when they let the window
    /// close, no more frames run.
    /// </summary>
    public void RequestClose() => m_events.Enqueue(vm =>
    {
        bool close = vm.CloseWindow();
        if (close)
            m_closed = true;
        m_window!.Post(_ => CloseAnswered?.Invoke(close), null);
    });

    /// <summary>
    /// When there is a new picture, copies into <paramref name="target"/> (which keeps the last
    /// one taken) the part that changed since then: <paramref name="area"/>, maybe empty.
    /// </summary>
    public bool TakeFrame(Span<byte> target, int stride, out ScreenArea area)
    {
        lock (m_frameLock)
        {
            area = default;
            if (!m_frameNew)
                return false;
            m_frameNew = false;
            area = m_untaken;
            m_untaken = default;
            int row = Width * 4, from = area.L * 4, bytes = area.Width * 4;
            for (int y = area.T; y < area.B; y++)
                m_front.AsSpan(y * row + from, bytes).CopyTo(target.Slice(y * stride + from, bytes));
            return true;
        }
    }

    /// <summary>Copies the part of the latest picture that changed into <paramref name="target"/> (which keeps the last one taken).</summary>
    public bool TakeFrame(Span<byte> target, int stride) => TakeFrame(target, stride, out _);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private void Run()
    {
        // Waits at the game's pace end on the clock's ticks: 15.6 ms on Windows unless asked for
        // 1 ms (as games do), which would turn 1/60 s into two ticks
        bool fineClock = m_gamePace && OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0;
        long next = Stopwatch.GetTimestamp(), interval = Stopwatch.Frequency / 60;
        try
        {
            while (!m_stop)
            {
                if (m_gamePace)
                {
                    for (long wait; !m_stop && (wait = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency) > 0; )
                        m_frameTick.Wait((int)Math.Min(wait, 100));
                }
                else
                    m_frameTick.Wait(1000 / 60);
                if (m_stop)
                    break;
                long started = Stopwatch.GetTimestamp();
                while (!m_closed && m_events.TryDequeue(out var e))
                    e(m_vm);
                if (!m_closed)
                    RunScheduled();
                if (m_closed)
                    continue;
                if (m_vm.Paused)
                {
                    // Behind another window, standing still (ScnVm.Paused): only its events, and
                    // no stall for the watch
                    m_frames++;
                    next = Stopwatch.GetTimestamp() + interval;
                    continue;
                }
                bool running = m_vm.RunFrame();
                m_frames++;
                if (!running)
                {
                    m_finished = true;
                    m_window!.Post(_ => Stopped?.Invoke(null, null), null);
                    return;
                }
                long engine = Stopwatch.GetTimestamp();
                if (m_vm.ScreenInvalidated || m_showSurface)
                {
                    m_vm.ScreenInvalidated = false;
                    CopyFrame();
                }
                if (m_perf?.Frame(started, engine, Stopwatch.GetTimestamp()) is { } title)
                    m_window!.Post(_ => TitleChanged?.Invoke(title), null);
                if (m_gamePace)
                {
                    // The next frame 1/60 s after this one was due (late by more than a frame: after
                    // this one, no catching up), or after the sleep the scripts asked for
                    long due = started - next > interval ? started + interval : next + interval;
                    long sleep = Math.Min(m_vm.SleepRequested, 1000) * Stopwatch.Frequency / 1000;
                    next = sleep > interval ? started + sleep : due;
                }
            }
        }
        catch (Exception ex)
        {
            m_finished = true;
            if (m_stop)
                return;
            // What the engine knows about the error, for a bug report
            string? log = Path.Combine(m_saveFolder, "crash.log");
            try
            {
                File.AppendAllText(log, m_vm.CrashReport(ex) + Environment.NewLine);
            }
            catch (IOException)
            {
                log = null;
            }
            CopyFrame();
            m_window!.Post(_ => Stopped?.Invoke(ex, log), null);
        }
        finally
        {
            if (fineClock)
                timeEndPeriod(1);
        }
    }

    /// <summary>
    /// The part of the window's picture (ScnVm.Window) that changed, as BGRA, into the back buffer
    /// (with what it lacked: what changed the picture before); then the buffers swap.
    /// </summary>
    private void CopyFrame()
    {
        ScreenArea changed;
        if (m_showSurface)
        {
            CopySurface();
            changed = new ScreenArea(0, 0, Width, Height);
        }
        else
        {
            changed = m_vm.TakeChangedArea().Clip(Width, Height);
            var copy = changed.Union(m_backStale).Clip(Math.Min(m_vm.ScreenWidth, Width), Math.Min(m_vm.ScreenHeight, Height));
            var window = m_vm.Window;
            int stride = m_vm.ScreenWidth * 3;
            // A pixel a store (BGR to BGRA with A = FF)
            for (int y = copy.T; y < copy.B; y++)
            {
                var row = window.Slice(y * stride + copy.L * 3, copy.Width * 3);
                var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(m_back.AsSpan((y * Width + copy.L) * 4, copy.Width * 4));
                for (int x = 0, s = 0; x < dst.Length; x++, s += 3)
                    dst[x] = row[s] | (uint)row[s + 1] << 8 | (uint)row[s + 2] << 16 | 0xFF000000;
            }
        }
        lock (m_frameLock)
        {
            (m_front, m_back) = (m_back, m_front);
            m_backStale = changed;
            m_untaken = m_untaken.Union(changed);
            m_frameNew = true;
        }
        if (m_gamePace && m_window != null && Interlocked.Exchange(ref m_readyPosted, 1) == 0)
            m_window.Post(_ =>
            {
                Volatile.Write(ref m_readyPosted, 0);
                FrameReady?.Invoke();
            }, null);
    }

    /// <summary>The display surface itself, as BGRA, into the back buffer (OPENSHIINA_PAINT=surface).</summary>
    private void CopySurface()
    {
        int surface = m_vm.DisplaySurface;
        int pixels = m_vm.SurfaceField(surface, 2), pitch = m_vm.SurfaceField(surface, 10), bytes = m_vm.SurfaceField(surface, 9) >> 3;
        int w = Math.Min(m_vm.SurfaceField(surface, 7), Width), h = Math.Min(m_vm.SurfaceField(surface, 8), Height);
        if (pixels == 0 || w <= 0 || h <= 0 || bytes is not (3 or 4))
            return;
        var row = new byte[w * bytes];
        for (int y = 0; y < h; y++)
        {
            m_vm.ReadBytes(pixels + y * pitch, row);
            var dst = m_back.AsSpan(y * Width * 4, w * 4);
            for (int x = 0, s = 0, d = 0; x < w; x++, s += bytes, d += 4)
            {
                dst[d] = row[s];
                dst[d + 1] = row[s + 1];
                dst[d + 2] = row[s + 2];
                dst[d + 3] = 0xFF;
            }
        }
    }

    /// <summary>Stops the interpreter; a message box the scripts wait for can hold it, so it is not waited for long.</summary>
    public void Dispose()
    {
        if (m_stop)
            return;
        m_stop = true;
        FrameTick();
        if (m_thread.IsAlive)
            m_thread.Join(1000);
        m_perf?.Dispose();
        try
        {
            m_vm.Trace?.Save(Path.Combine(m_saveFolder, "draw-trace.log"));
        }
        catch (IOException)
        {
            // Only for finding problems
        }
        // The scripts' memory goes back now when the interpreter has stopped; a thread still
        // held by a message box leaves it to the finalizer, once the thread is gone
        if (!m_thread.IsAlive)
            m_vm.Dispose();
    }
}
