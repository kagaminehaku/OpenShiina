// Runs a game's own SCN scripts in ScnVm (approach 2) and shows the surface the engine shows,
// pixel for pixel (OpenShiina.exe's default). Each display frame runs the engine until
// the scripts show a picture; the picture is copied into a bitmap the size of the game's window.
// Text is drawn with GDI as the game does it, sounds and music play through NAudio, the keyboard and
// mouse are passed on. Save data goes to %AppData%\OpenShiina\scn\<game>, never the game folder.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenShiina.Platform;
using OpenShiina.Scripting;

namespace OpenShiina.Windows.Scn;

public sealed class ScnWindow : Window
{
    private readonly GameData m_data;
    private readonly ScnVm m_vm;
    private readonly Host m_host;
    private readonly WriteableBitmap m_bitmap;
    private readonly byte[] m_frame;
    private bool m_stopped;

    public ScnWindow(GameData data)
    {
        m_data = data;
        var setup = GameSetup.Read(data);
        int width = setup.Width, height = setup.Height;
        string saves = m_saves = setup.SaveFolder;
        m_host = new Host(this, data, saves);
        m_vm = setup.CreateVm(m_host);
        m_perf = PerfMeter.Create(m_vm, data.SchemeName, saves);

        m_bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        m_frame = new byte[width * height * 3];
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
        CompositionTarget.Rendering += OnRendering;
        Activated += (_, _) => Notify(ScnEvent.Activate);
        Deactivated += (_, _) => Notify(ScnEvent.Deactivate);
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            m_perf?.Dispose();
            m_host.Dispose();
            m_data.Dispose();
        };
    }

    private void Notify(ScnEvent e)
    {
        if (m_stopped)
            return;
        try
        {
            m_vm.Notify(e);
        }
        catch (Exception ex)
        {
            Stop(ex);
        }
    }

    // Frames a second and the slowest frame in the title, perf.log with OPENSHIINA_PERF=log
    private readonly PerfMeter? m_perf;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (m_stopped)
            return;
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!m_vm.RunFrame())
            {
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            Stop(ex);
        }
        long engine = Stopwatch.GetTimestamp();
        if (m_vm.ScreenInvalidated)
        {
            m_vm.ScreenInvalidated = false;
            Present();
        }
        if (m_perf?.Frame(started, engine, Stopwatch.GetTimestamp()) is { } title)
            Title = title;
    }

    /// <summary>Copies the surface the engine shows into the bitmap.</summary>
    private void Present()
    {
        int surface = m_vm.DisplaySurface;
        int pixels = m_vm.SurfaceField(surface, 2);
        int w = Math.Min(m_vm.SurfaceField(surface, 7), m_bitmap.PixelWidth);
        int h = Math.Min(m_vm.SurfaceField(surface, 8), m_bitmap.PixelHeight);
        int pitch = m_vm.SurfaceField(surface, 10);
        if (pixels == 0 || w <= 0 || h <= 0)
            return;
        int stride = m_bitmap.PixelWidth * 3;
        for (int y = 0; y < h; y++)
            m_vm.ReadBytes(pixels + y * pitch, m_frame.AsSpan(y * stride, w * 3));
        m_bitmap.WritePixels(new Int32Rect(0, 0, m_bitmap.PixelWidth, m_bitmap.PixelHeight), m_frame, stride, 0);
    }

    /// <summary>The scripts reached something the interpreter does not do yet: show it and stop.</summary>
    private void Stop(Exception ex)
    {
        m_stopped = true;
        Present();
        Title = $"{m_data.SchemeName} - stopped";
        // What the engine knows about the error, for a bug report
        string log = Path.Combine(m_saves, "crash.log");
        try
        {
            File.AppendAllText(log, m_vm.CrashReport(ex) + Environment.NewLine);
        }
        catch (IOException)
        {
            log = "";
        }
        string message = ex is ScnException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        MessageBox.Show(this, log.Length > 0 ? $"{message}\n\nDetails: {log}" : message,
            "OpenShiina (SCN)", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private readonly string m_saves;

    private sealed class Host(ScnWindow window, GameData data, string saveFolder) : IScnHost, IDisposable
    {
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private readonly GdiFonts m_fonts = new();
        // One sound output for the buffers and the music streams: the mix on the Windows sound device
        private readonly (ScnMixer Mixer, ScnSound Sound, ScnMusic Music, NAudio.Wave.WaveOutEvent Output) m_audio = CreateAudio();

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

        public void SetTitle(string title) => window.Title = title;

        public void SetFullScreen(bool fullScreen)
        {
            window.WindowStyle = fullScreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
            window.WindowState = fullScreen ? WindowState.Maximized : WindowState.Normal;
        }

        public string SaveFolder => saveFolder;

        public byte[]? ReadSaveFile(string name) =>
            File.Exists(Path.Combine(saveFolder, name)) ? File.ReadAllBytes(Path.Combine(saveFolder, name)) : null;

        public void WriteSaveFile(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(saveFolder, name), bytes);

        public void DeleteSaveFile(string name) => File.Delete(Path.Combine(saveFolder, name));

        public int MessageBox(string text, string caption, int type)
        {
            var buttons = (type & 0xF) switch
            {
                1 => MessageBoxButton.OKCancel,
                3 => MessageBoxButton.YesNoCancel,
                4 => MessageBoxButton.YesNo,
                _ => MessageBoxButton.OK,
            };
            return System.Windows.MessageBox.Show(window, text, caption, buttons) switch
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
        // them (GetAsyncKeyState, DirectInput). WPF's own key events and Mouse.LeftButton only
        // change when the window's input is processed, which waits behind slow frames (the game
        // runs in CompositionTarget.Rendering, before input): a released Ctrl went on skipping.
        public bool KeyDown(int virtualKey) => window.IsActive && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        public int MouseButtons
        {
            get
            {
                if (!window.IsActive)
                    return 0;
                // GetAsyncKeyState gives the physical buttons; the logical ones follow the system's swap
                bool swapped = GetSystemMetrics(SM_SWAPBUTTON) != 0;
                bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
                return (Down(swapped ? VK_RBUTTON : VK_LBUTTON) ? 1 : 0) |
                       (Down(swapped ? VK_LBUTTON : VK_RBUTTON) ? 2 : 0) |
                       (Down(VK_MBUTTON) ? 4 : 0);
            }
        }

        private const int VK_LBUTTON = 1, VK_RBUTTON = 2, VK_MBUTTON = 4, SM_SWAPBUTTON = 23;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        public bool Active => window.IsActive;

        public (int X, int Y) MousePosition
        {
            get
            {
                var image = (Image)window.Content;
                var p = Mouse.GetPosition(image);
                double sx = image.ActualWidth > 0 ? window.m_bitmap.PixelWidth / image.ActualWidth : 1;
                double sy = image.ActualHeight > 0 ? window.m_bitmap.PixelHeight / image.ActualHeight : 1;
                return ((int)Math.Floor(p.X * sx), (int)Math.Floor(p.Y * sy));
            }
        }

        public void SetMousePosition(int x, int y)
        {
            var image = (Image)window.Content;
            if (image.ActualWidth <= 0 || image.ActualHeight <= 0)
                return;
            var screen = image.PointToScreen(new Point(x * image.ActualWidth / window.m_bitmap.PixelWidth,
                y * image.ActualHeight / window.m_bitmap.PixelHeight));
            SetCursorPos((int)screen.X, (int)screen.Y);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        public void Dispose()
        {
            m_audio.Output.Stop();
            m_audio.Output.Dispose();
            m_audio.Sound.Dispose();
            m_audio.Music.Dispose();
            m_audio.Mixer.Dispose();
            m_fonts.Dispose();
        }
    }
}
