// A game running: the interpreter on a thread of its own (Core's GameThread), so slow frames
// never hold up the window (its input, moving, resizing); each frame the window shows lets it run
// one frame, and the window takes the latest picture as BGRA. Calls the scripts make to the window
// (title, full screen, message boxes, moving the pointer) cross to the window's thread.

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

    /// <summary>Moves the system's pointer to (x, y) of the game's picture, where it can.</summary>
    void MovePointer(int x, int y);

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
    private readonly GameThread m_thread;

    public GameSession(GameData data, IGameWindow window)
    {
        Data = data;
        m_window = window;
        Setup = GameSetup.Read(data);
        m_host = new Host(this);
        m_thread = new GameThread(Setup.CreateVm(m_host), Setup, data.SchemeName);
        m_thread.TitleChanged += title => m_window.SetTitle(title);
        m_thread.Stopped += (error, log) => m_window.Stopped(error, log);
        m_thread.CloseAnswered += close => CloseAnswered?.Invoke(close);
    }

    /// <summary>The scripts' answer to <see cref="RequestClose"/> (on the window's thread): true when the window may close.</summary>
    public event Action<bool>? CloseAnswered;

    /// <summary>The interpreter still runs frames.</summary>
    public bool Running => m_thread.Running;

    /// <summary>The player closes the window: the scripts run what they do on closing (WM_CLOSE) and answer.</summary>
    public void RequestClose() => m_thread.RequestClose();

    /// <summary>Starts the interpreter; call it on the window's thread.</summary>
    public void Start() => m_thread.Start();

    /// <summary>The window drew a frame: the interpreter may run the next one.</summary>
    public void FrameTick() => m_thread.FrameTick();

    /// <summary>Window events for the scripts (focus, Alt+Enter, closing).</summary>
    public void Post(ScnEvent e) => m_thread.Post(e);

    /// <summary>A window message for the scripts (ScnMessage: keys, mouse buttons, the wheel).</summary>
    public void PostMessage(int message, int wParam, int lParam) => m_thread.PostMessage(message, wParam, lParam);

    /// <summary>Copies the latest picture into <paramref name="target"/> when there is a new one.</summary>
    public bool TakeFrame(Span<byte> target, int stride) => m_thread.TakeFrame(target, stride);

    private bool m_disposed;

    public void Dispose()
    {
        if (m_disposed)
            return;
        m_disposed = true;
        m_thread.Dispose();
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
        private readonly IDisposable? m_output;
        private readonly SkiaFonts m_fonts = new();

        public Host(GameSession session)
        {
            m_session = session;
            m_sound = new ScnSound(m_mixer);
            m_music = new ScnMusic(m_mixer);
            m_output = PlayerPlatform.StartSound(m_mixer.Output);
        }

        public byte[]? ReadFile(string name) => m_session.Data.Read(name);
        public long? ArchiveFileSize(string name) => m_session.Data.Size(name);

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

        /// <summary>The scripts see the new position at once, whether or not the platform can move the pointer.</summary>
        public void SetMousePosition(int x, int y)
        {
            m_session.Input.Position = (x, y);
            Dispatcher.UIThread.Post(() => m_session.m_window.MovePointer(x, y));
        }

        public ScnJoystick? Joystick => m_session.Input.Joystick;

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
