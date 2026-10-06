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
        string ini = OpenShiina.IO.Encodings.cp932.GetString(data.LooseFile("RIO.INI") is { } path ? File.ReadAllBytes(path) : []);
        string version = System.Text.RegularExpressions.Regex.Match(ini, @"v(\d+\.\d+)").Groups[1].Value;
        string start = System.Text.RegularExpressions.Regex.Match(ini, @"(?im)^Scn=(.+)$").Groups[1].Value.Trim();
        int width = IniNumber(ini, "WindowWidth", 800), height = IniNumber(ini, "WindowHeight", 600);

        string saves = PlayerFolders.For("scn", data.SchemeName);
        Directory.CreateDirectory(saves);
        m_host = new Host(this, data, saves);
        m_vm = new ScnVm(ScnOpcodes.ForVersion(version), m_host)
        {
            EngineVersion = (int)Math.Round(double.Parse(version, System.Globalization.CultureInfo.InvariantCulture) * 100),
            ScreenWidth = width,
            ScreenHeight = height,
        };
        if (!m_vm.LoadModule(0, start, start: true))
            throw new InvalidDataException($"{start} is missing.");

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
        Deactivated += (_, _) =>
        {
            m_host.Keys.Clear();
            Notify(ScnEvent.Deactivate);
        };
        KeyDown += (_, e) => m_host.Press(e, true);
        KeyUp += (_, e) => m_host.Press(e, false);
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            m_host.Dispose();
            m_data.Dispose();
        };
    }

    private static int IniNumber(string ini, string key, int fallback) =>
        System.Text.RegularExpressions.Regex.Match(ini, $@"(?im)^{key}=(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : fallback;

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

    private void OnRendering(object? sender, EventArgs e)
    {
        if (m_stopped)
            return;
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
        if (m_vm.ScreenInvalidated)
        {
            m_vm.ScreenInvalidated = false;
            Present();
        }
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
        MessageBox.Show(this, ex is ScnException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}",
            "OpenShiina (SCN)", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed class Host(ScnWindow window, GameData data, string saveFolder) : IScnHost, IDisposable
    {
        private readonly Stopwatch m_clock = Stopwatch.StartNew();
        private readonly GdiFonts m_fonts = new();
        private readonly ScnSound m_sound = new();
        private readonly ScnMusic m_music = new();
        public readonly HashSet<int> Keys = new();

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

        public IScnSound? Sound => m_sound;

        public IScnMusic? Music => m_music;

        public bool KeyDown(int virtualKey) => Keys.Contains(virtualKey);

        public int MouseButtons =>
            (Mouse.LeftButton == MouseButtonState.Pressed ? 1 : 0) |
            (Mouse.RightButton == MouseButtonState.Pressed ? 2 : 0) |
            (Mouse.MiddleButton == MouseButtonState.Pressed ? 4 : 0);

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

        public void Press(KeyEventArgs e, bool down)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            int general = key switch
            {
                Key.LeftCtrl or Key.RightCtrl => 0x11,
                Key.LeftShift or Key.RightShift => 0x10,
                Key.LeftAlt or Key.RightAlt => 0x12,
                _ => 0,
            };
            foreach (int k in general != 0 ? [vk, general] : new[] { vk })
                if (down)
                    Keys.Add(k);
                else
                    Keys.Remove(k);
        }

        public void Dispose()
        {
            m_sound.Dispose();
            m_music.Dispose();
            m_fonts.Dispose();
        }
    }
}
