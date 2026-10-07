// A game running: the interpreter on a thread of its own, so slow frames never hold up the
// window (its input, moving, resizing). Each frame the window shows, it lets the interpreter run
// one frame (RunFrame: until the scripts show a picture); a window that draws fewer than 60
// frames a second (or none, minimised) is topped up: the thread goes on after 1/60 s anyway. A picture the scripts showed is copied, as 32-bit
// BGRA, into a buffer the window takes when it draws. Window events and messages (focus,
// Alt+Enter, keys, mouse buttons, the wheel, closing) are queued for the interpreter's thread; the
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
    private readonly ConcurrentQueue<Action<ScnVm>> m_events = new();
    private SynchronizationContext? m_window;
    private volatile bool m_stop, m_finished, m_closed;

    // The latest picture (BGRA, Width x Height) and whether the window has taken it
    private readonly object m_frameLock = new();
    private byte[] m_front, m_back;
    private bool m_frameNew;

    // Frames a second and the slowest frame in the title, perf.log with OPENSHIINA_PERF=log
    private readonly PerfMeter? m_perf;

    // OPENSHIINA_PAINT=surface (for finding problems): show the display surface every frame, as
    // the players did before ScnVm.Paint, instead of the window's picture
    private readonly bool m_showSurface = Environment.GetEnvironmentVariable("OPENSHIINA_PAINT") == "surface";

    public int Width { get; }
    public int Height { get; }

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
        m_perf = PerfMeter.Create(vm, name, setup.SaveFolder);
        if (Environment.GetEnvironmentVariable("OPENSHIINA_TRACE") == "draw")
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

    /// <summary>Copies the latest picture into <paramref name="target"/> when there is a new one.</summary>
    public bool TakeFrame(Span<byte> target, int stride)
    {
        lock (m_frameLock)
        {
            if (!m_frameNew)
                return false;
            m_frameNew = false;
            int row = Width * 4;
            for (int y = 0; y < Height; y++)
                m_front.AsSpan(y * row, row).CopyTo(target.Slice(y * stride, row));
            return true;
        }
    }

    private void Run()
    {
        try
        {
            while (!m_stop)
            {
                m_frameTick.Wait(1000 / 60);
                if (m_stop)
                    break;
                long started = Stopwatch.GetTimestamp();
                while (!m_closed && m_events.TryDequeue(out var e))
                    e(m_vm);
                if (m_closed)
                    continue;
                if (!m_vm.RunFrame())
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
    }

    /// <summary>The window's picture (ScnVm.Window), as BGRA, into the back buffer; then the buffers swap.</summary>
    private void CopyFrame()
    {
        if (m_showSurface)
            CopySurface();
        else
        {
            var window = m_vm.Window;
            int stride = m_vm.ScreenWidth * 3;
            int w = Math.Min(m_vm.ScreenWidth, Width), h = Math.Min(m_vm.ScreenHeight, Height);
            for (int y = 0; y < h; y++)
            {
                var row = window.Slice(y * stride, w * 3);
                var dst = m_back.AsSpan(y * Width * 4, w * 4);
                for (int x = 0, s = 0, d = 0; x < w; x++, s += 3, d += 4)
                {
                    dst[d] = row[s];
                    dst[d + 1] = row[s + 1];
                    dst[d + 2] = row[s + 2];
                    dst[d + 3] = 0xFF;
                }
            }
        }
        lock (m_frameLock)
        {
            (m_front, m_back) = (m_back, m_front);
            m_frameNew = true;
        }
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
    }
}
