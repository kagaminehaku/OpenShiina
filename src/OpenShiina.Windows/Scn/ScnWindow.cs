// Runs a game's own SCN scripts in ScnVm and shows the surface the engine shows, pixel for
// pixel. The interpreter runs on a thread of its own (Core's GameThread), one frame for each
// frame WPF renders, so slow frames never hold up the window (its input, moving, resizing); the
// window takes the latest picture when it renders. Text is drawn with GDI as the game does it,
// sounds and music play through NAudio; keys, mouse buttons and the joystick are read with the
// calls the engine makes (GetAsyncKeyState, joyGetPosEx) when the scripts ask. Save data goes to
// %AppData%\OpenShiina\scn\<game>, never the game folder.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShiina.Platform;
using OpenShiina.Scripting;

namespace OpenShiina.Windows.Scn;

public sealed class ScnWindow : Window
{
    private readonly GameData m_data;
    private readonly Host m_host;
    private readonly GameThread m_game;
    private readonly WriteableBitmap m_bitmap;
    private readonly byte[] m_frame;
    private bool m_stopped;

    public ScnWindow(GameData data)
    {
        m_data = data;
        var setup = GameSetup.Read(data);
        int width = setup.Width, height = setup.Height;
        m_bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        m_frame = new byte[width * height * 4];
        m_host = new Host(this, data, setup.SaveFolder);
        m_game = new GameThread(setup.CreateVm(m_host), setup, data.SchemeName);
        m_game.TitleChanged += title => Title = title;
        m_game.Stopped += Stop;

        Title = data.SchemeName;
        Background = Brushes.Black;
        Content = new Image { Source = m_bitmap, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode((Image)Content, BitmapScalingMode.NearestNeighbor);
        SizeToContent = SizeToContent.WidthAndHeight;
        ((Image)Content).Width = width;
        ((Image)Content).Height = height;
        Loaded += (_, _) =>
        {
            // From here the window can be resized; the picture keeps its proportions
            SizeToContent = SizeToContent.Manual;
            ((Image)Content).Width = double.NaN;
            ((Image)Content).Height = double.NaN;
        };
        SourceInitialized += (_, _) =>
        {
            m_host.Window = new WindowInteropHelper(this).Handle;
            m_game.Start();
            CompositionTarget.Rendering += OnRendering;
        };
        Activated += (_, _) => Focus(true);
        Deactivated += (_, _) => Focus(false);
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            m_game.Dispose();
            m_host.Dispose();
            m_data.Dispose();
        };
    }

    private void Focus(bool active)
    {
        m_host.Active = active;
        m_game.Post(active ? ScnEvent.Activate : ScnEvent.Deactivate);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Alt+Enter: the engine's full screen switch
        if (e.Key == Key.System && e.SystemKey == Key.Enter)
        {
            m_game.Post(ScnEvent.ToggleFullScreen);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Delta != 0)
            m_game.PostWheel(e.Delta);
        base.OnMouseWheel(e);
    }

    // The time of the frame WPF rendered last: Rendering can be raised more than once a frame
    private TimeSpan m_renderingTime;

    /// <summary>A frame WPF renders: the game's latest picture, and the interpreter's next frame.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == m_renderingTime)
                return;
            m_renderingTime = r.RenderingTime;
        }
        Present();
        if (!m_stopped)
            m_game.FrameTick();
    }

    private void Present()
    {
        int stride = m_bitmap.PixelWidth * 4;
        if (m_game.TakeFrame(m_frame, stride))
            m_bitmap.WritePixels(new Int32Rect(0, 0, m_bitmap.PixelWidth, m_bitmap.PixelHeight), m_frame, stride, 0);
    }

    /// <summary>The scripts ended the game (the window closes), or reached something the interpreter does not do yet.</summary>
    private void Stop(Exception? error, string? log)
    {
        m_stopped = true;
        Present();
        if (error == null)
        {
            Close();
            return;
        }
        Title = $"{m_data.SchemeName} - stopped";
        string message = error is ScnException ? error.Message : $"{error.GetType().Name}: {error.Message}";
        MessageBox.Show(this, log != null ? $"{message}\n\nDetails: {log}" : message,
            "OpenShiina (SCN)", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The platform for the interpreter: called on the interpreter's thread.</summary>
    private sealed class Host(ScnWindow window, GameData data, string saveFolder) : IScnHost, IDisposable
    {
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private readonly GdiFonts m_fonts = new();
        // One sound output for the buffers and the music streams: the mix on the Windows sound device
        private readonly (ScnMixer Mixer, ScnSound Sound, ScnMusic Music, NAudio.Wave.WaveOutEvent Output) m_audio = CreateAudio();
        private readonly int m_width = window.m_bitmap.PixelWidth, m_height = window.m_bitmap.PixelHeight;

        /// <summary>The window's handle (set on the window's thread before the interpreter starts).</summary>
        public volatile IntPtr Window;

        private volatile bool m_active = true;

        private static (ScnMixer, ScnSound, ScnMusic, NAudio.Wave.WaveOutEvent) CreateAudio()
        {
            var mixer = new ScnMixer();
            var output = new NAudio.Wave.WaveOutEvent { DesiredLatency = 100 };
            output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(mixer.Output));
            output.Play();
            return (mixer, new ScnSound(mixer), new ScnMusic(mixer), output);
        }

        public byte[]? ReadFile(string name) => data.Read(name.Replace('/', '\\'));

        public byte[]? ReadLooseFile(string name) => data.LooseFile(name) is { } path ? File.ReadAllBytes(path) : null;

        public uint Milliseconds => (uint)m_clock.ElapsedMilliseconds;

        public void SetTitle(string title) => window.Dispatcher.BeginInvoke(() => window.Title = title);

        public void SetFullScreen(bool fullScreen) => window.Dispatcher.BeginInvoke(() =>
        {
            window.WindowStyle = fullScreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
            window.WindowState = fullScreen ? WindowState.Maximized : WindowState.Normal;
        });

        public string SaveFolder => saveFolder;

        public byte[]? ReadSaveFile(string name) =>
            File.Exists(Path.Combine(saveFolder, name)) ? File.ReadAllBytes(Path.Combine(saveFolder, name)) : null;

        public void WriteSaveFile(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(saveFolder, name), bytes);

        public void DeleteSaveFile(string name) => File.Delete(Path.Combine(saveFolder, name));

        /// <summary>The scripts wait for the answer, as MessageBoxA waits; the window stays live.</summary>
        public int MessageBox(string text, string caption, int type)
        {
            var buttons = (type & 0xF) switch
            {
                1 => MessageBoxButton.OKCancel,
                3 => MessageBoxButton.YesNoCancel,
                4 => MessageBoxButton.YesNo,
                _ => MessageBoxButton.OK,
            };
            var result = window.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(window, text, caption, buttons));
            return result switch
            {
                MessageBoxResult.OK => 1,
                MessageBoxResult.Cancel => 2,
                MessageBoxResult.Yes => 6,
                MessageBoxResult.No => 7,
                _ => 2,
            };
        }

        public IScnFonts? Fonts => m_fonts;
        public IScnShapes? Shapes { get; } = new GdiShapes();

        public IScnSound? Sound => m_audio.Sound;

        public IScnMusic? Music => m_audio.Music;

        // Keys and mouse buttons as they are at the moment the scripts ask, like the engine reads
        // them (GetAsyncKeyState, DirectInput), not from WPF's events.
        public bool KeyDown(int virtualKey) => m_active && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        public int MouseButtons
        {
            get
            {
                if (!m_active)
                    return 0;
                // GetAsyncKeyState gives the physical buttons; the logical ones follow the system's swap
                bool swapped = GetSystemMetrics(SM_SWAPBUTTON) != 0;
                bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
                return (Down(swapped ? VK_RBUTTON : VK_LBUTTON) ? 1 : 0) |
                       (Down(swapped ? VK_LBUTTON : VK_RBUTTON) ? 2 : 0) |
                       (Down(VK_MBUTTON) ? 4 : 0);
            }
        }

        /// <summary>The window is in front (set on the window's thread).</summary>
        public bool Active
        {
            get => m_active;
            set => m_active = value;
        }

        /// <summary>
        /// The picture is scaled uniformly into the client area and centred (Stretch.Uniform):
        /// its scale and offset in device pixels.
        /// </summary>
        private (double Scale, double Left, double Top) Placement()
        {
            if (!GetClientRect(Window, out var client))
                return (0, 0, 0);
            int cw = client.Right - client.Left, ch = client.Bottom - client.Top;
            double scale = Math.Min((double)cw / m_width, (double)ch / m_height);
            return (scale, (cw - m_width * scale) / 2, (ch - m_height * scale) / 2);
        }

        public (int X, int Y) MousePosition
        {
            get
            {
                var (scale, left, top) = Placement();
                if (scale <= 0 || !GetCursorPos(out var p) || !ScreenToClient(Window, ref p))
                    return (0, 0);
                return ((int)Math.Floor((p.X - left) / scale), (int)Math.Floor((p.Y - top) / scale));
            }
        }

        public void SetMousePosition(int x, int y)
        {
            var (scale, left, top) = Placement();
            if (scale <= 0)
                return;
            // The middle of the pixel, so that the pointer's move reads back as (x, y)
            var p = new POINT { X = (int)(left + (x + 0.5) * scale), Y = (int)(top + (y + 0.5) * scale) };
            if (ClientToScreen(Window, ref p))
                SetCursorPos(p.X, p.Y);
        }

        // joyGetPosEx of joystick 0, as the engine calls it (JOY_RETURNX | JOY_RETURNY | JOY_RETURNBUTTONS).
        // The scripts ask several times a frame: a reading is kept for 4 ms. Without a joystick the
        // call is slow, so after a failure it is tried again a second later.
        private long m_joystickNext;
        private ScnJoystick? m_joystick;

        public ScnJoystick? Joystick
        {
            get
            {
                if (!m_active)
                    return null;
                long now = Environment.TickCount64;
                if (now < m_joystickNext)
                    return m_joystick;
                var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = 0x83 };
                bool read = joyGetPosEx(0, ref info) == 0;
                m_joystick = read ? new ScnJoystick(info.dwXpos, info.dwYpos, info.dwButtons) : null;
                m_joystickNext = now + (read ? 4 : 1000);
                return m_joystick;
            }
        }

        public void Dispose()
        {
            m_audio.Output.Stop();
            m_audio.Output.Dispose();
            m_audio.Sound.Dispose();
            m_audio.Music.Dispose();
            m_audio.Mixer.Dispose();
            m_fonts.Dispose();
        }

        private const int VK_LBUTTON = 1, VK_RBUTTON = 2, VK_MBUTTON = 4, SM_SWAPBUTTON = 23;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOYINFOEX
        {
            public int dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons,
                dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr window, ref POINT point);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref POINT point);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("winmm.dll")]
        private static extern int joyGetPosEx(int id, ref JOYINFOEX info);
    }
}
