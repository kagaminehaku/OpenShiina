// A game running: the interpreter on a thread of its own, so slow frames never hold up the
// window (its input, moving, resizing). Each frame the window shows, it lets the interpreter run
// one frame (RunFrame: until the scripts show a picture), as the WPF player did on its render
// event; without frames from the window (minimised) the thread goes on every 1/60 s. A picture
// the scripts showed is copied, as 32-bit BGRA, into a buffer the window takes when it draws.
// Window events and calls the scripts make to the window (title, full screen, message boxes)
// cross between the threads.

using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using OpenShiina.Audio;
using OpenShiina.Game;
using OpenShiina.Scripting;

namespace OpenShiina.App;

/// <summary>What the game needs from the window it runs in; called on the window's thread.</summary>
public interface IGameWindow
{
    void SetTitle(string title);
    void SetFullScreen(bool fullScreen);
    Task<int> ShowMessageAsync(string text, string caption, int type);

    /// <summary>The game stopped: the scripts ended it (error null), or an error with the crash log's path.</summary>
    void Stopped(Exception? error, string? log);
}

public sealed class GameSession : IDisposable
{
    public GameData Data { get; }
    public GameSetup Setup { get; }
    public InputState Input { get; } = new();
    public int Width => Setup.Width;
    public int Height => Setup.Height;

    private readonly IGameWindow m_window;
    private readonly Host m_host;
    private readonly ScnVm m_vm;
    private readonly Thread m_thread;
    private readonly SemaphoreSlim m_frameTick = new(0, 1);
    private readonly ConcurrentQueue<ScnEvent> m_events = new();
    private volatile bool m_stop;

    // The latest picture (BGRA, Width x Height) and whether the window has taken it
    private readonly object m_frameLock = new();
    private byte[] m_front, m_back;
    private bool m_frameNew;

    // Frames a second and the slowest frame in the title, perf.log with OPENSHIINA_PERF=log
    private readonly PerfMeter? m_perf;

    public GameSession(GameData data, IGameWindow window)
    {
        Data = data;
        m_window = window;
        Setup = GameSetup.Read(data);
        m_host = new Host(this);
        m_vm = Setup.CreateVm(m_host);
        m_perf = PerfMeter.Create(m_vm, data.SchemeName, Setup.SaveFolder);
        m_front = new byte[Width * Height * 4];
        m_back = new byte[Width * Height * 4];
        m_thread = new Thread(Run) { IsBackground = true, Name = "OpenShiina interpreter" };
    }

    public void Start() => m_thread.Start();

    /// <summary>The window drew a frame: the interpreter may run the next one.</summary>
    public void FrameTick()
    {
        if (m_frameTick.CurrentCount == 0)
            m_frameTick.Release();
    }

    /// <summary>Window events for the scripts (focus, Alt+Enter, closing).</summary>
    public void Post(ScnEvent e) => m_events.Enqueue(e);

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
                while (m_events.TryDequeue(out var e))
                    m_vm.Notify(e);
                if (!m_vm.RunFrame())
                {
                    Dispatcher.UIThread.Post(() => m_window.Stopped(null, null));
                    return;
                }
                long engine = Stopwatch.GetTimestamp();
                if (m_vm.ScreenInvalidated)
                {
                    m_vm.ScreenInvalidated = false;
                    CopyFrame();
                }
                if (m_perf?.Frame(started, engine, Stopwatch.GetTimestamp()) is { } title)
                    Dispatcher.UIThread.Post(() => m_window.SetTitle(title));
            }
        }
        catch (Exception ex)
        {
            if (m_stop)
                return;
            // What the engine knows about the error, for a bug report
            string? log = Path.Combine(Setup.SaveFolder, "crash.log");
            try
            {
                File.AppendAllText(log, m_vm.CrashReport(ex) + Environment.NewLine);
            }
            catch (IOException)
            {
                log = null;
            }
            CopyFrame();
            Dispatcher.UIThread.Post(() => m_window.Stopped(ex, log));
        }
    }

    /// <summary>The surface the engine shows, as BGRA, into the back buffer; then the buffers swap.</summary>
    private void CopyFrame()
    {
        int surface = m_vm.DisplaySurface;
        int pixels = m_vm.SurfaceField(surface, 2);
        int w = Math.Min(m_vm.SurfaceField(surface, 7), Width), h = Math.Min(m_vm.SurfaceField(surface, 8), Height);
        int pitch = m_vm.SurfaceField(surface, 10), bytes = m_vm.SurfaceField(surface, 9) >> 3;
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
        lock (m_frameLock)
        {
            (m_front, m_back) = (m_back, m_front);
            m_frameNew = true;
        }
    }

    public void Dispose()
    {
        if (m_stop)
            return;
        m_stop = true;
        FrameTick();
        // A message box the scripts wait for can hold the thread; it is a background thread
        m_thread.Join(1000);
        m_perf?.Dispose();
        m_host.Dispose();
        Data.Dispose();
    }

    /// <summary>The platform for the interpreter: called on the interpreter's thread.</summary>
    private sealed class Host : IScnHost, IDisposable
    {
        private readonly GameSession m_session;
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private readonly ScnMixer m_mixer = new();
        private readonly ScnSound m_sound;
        private readonly ScnMusic m_music;
        private readonly SdlAudioOutput? m_output;
        private readonly SkiaFonts m_fonts = new();

        public Host(GameSession session)
        {
            m_session = session;
            m_sound = new ScnSound(m_mixer);
            m_music = new ScnMusic(m_mixer);
            m_output = SdlAudioOutput.TryStart(m_mixer.Output);
        }

        public byte[]? ReadFile(string name) => m_session.Data.Read(name);

        public byte[]? ReadLooseFile(string name) => m_session.Data.LooseFile(name) is { } path ? File.ReadAllBytes(path) : null;

        public uint Milliseconds => (uint)m_clock.ElapsedMilliseconds;

        public void SetTitle(string title) => Dispatcher.UIThread.Post(() => m_session.m_window.SetTitle(title));

        public void SetFullScreen(bool fullScreen) => Dispatcher.UIThread.Post(() => m_session.m_window.SetFullScreen(fullScreen));

        public string SaveFolder => m_session.Setup.SaveFolder;

        private string SavePath(string name) => Path.Combine(SaveFolder, Path.GetFileName(name));

        public byte[]? ReadSaveFile(string name) => File.Exists(SavePath(name)) ? File.ReadAllBytes(SavePath(name)) : null;

        public void WriteSaveFile(string name, byte[] data) => File.WriteAllBytes(SavePath(name), data);

        public void DeleteSaveFile(string name) => File.Delete(SavePath(name));

        /// <summary>The scripts wait for the answer, as MessageBoxA waits; the window stays live.</summary>
        public int MessageBox(string text, string caption, int type) =>
            Dispatcher.UIThread.InvokeAsync(() => m_session.m_window.ShowMessageAsync(text, caption, type)).GetAwaiter().GetResult();

        public IScnFonts? Fonts => m_fonts;

        public IScnSound? Sound => m_sound;

        public IScnMusic? Music => m_music;

        public bool KeyDown(int virtualKey) => m_session.Input.IsDown(virtualKey);

        public int MouseButtons => m_session.Input.Buttons;

        public bool Active => m_session.Input.Active;

        public (int X, int Y) MousePosition => m_session.Input.Position;

        public void Dispose()
        {
            m_output?.Dispose();
            m_sound.Dispose();
            m_music.Dispose();
            m_mixer.Dispose();
            m_fonts.Dispose();
        }
    }
}
