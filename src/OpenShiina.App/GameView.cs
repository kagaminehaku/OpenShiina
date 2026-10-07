// The player's one view, on every platform: first a page to choose the game's folder (or the
// folder given on the command line), then the game's picture, scaled to the view with its
// proportions kept, black around it. Keyboard, mouse and touch go to the game's InputState; a
// touch is the left button. Each frame the view draws, it takes the game's latest picture and
// lets the interpreter run its next frame.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenShiina.Archives;
using OpenShiina.Game;
using OpenShiina.Scripting;

namespace OpenShiina.App;

public sealed class GameView : UserControl, IGameWindow
{
    private readonly Image m_image = new() { Stretch = Stretch.Uniform, IsVisible = false };
    private readonly StackPanel m_start;
    private readonly TextBlock m_message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, MaxWidth = 560 };
    private GameSession? m_session;
    private WriteableBitmap? m_bitmap;
    private bool m_animating;

    /// <summary>The window title the game asks for (the desktop window shows it).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>The game asks for full screen or a window.</summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>The game ended itself (the window should close).</summary>
    public event Action? GameEnded;

    private static string LastFolderFile => Path.Combine(PlayerFolders.Root, "last-game.txt");

    public GameView()
    {
        Background = Brushes.Black;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(m_image, BitmapInterpolationMode.None);
        var choose = new Button { Content = "Choose the game's folder…", HorizontalAlignment = HorizontalAlignment.Center };
        choose.Click += async (_, _) => await ChooseFolderAsync();
        m_start = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "OpenShiina", FontSize = 28, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = "Choose the folder of an installed game: the one with its .exe and .WAR files.",
                    Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
                },
                choose,
                m_message,
            },
        };
        Content = new Grid { Children = { m_image, m_start } };

        m_image.PointerMoved += (_, e) => Pointer(e);
        m_image.PointerPressed += (_, e) => Pointer(e);
        m_image.PointerReleased += (_, e) => Pointer(e);
        m_image.PointerCaptureLost += (_, _) => { if (m_session != null) m_session.Input.Buttons = 0; };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        // Keys reach the game wherever the focus is
        top.AddHandler(KeyDownEvent, (_, k) => Key(k, true), RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(KeyUpEvent, (_, k) => Key(k, false), RoutingStrategies.Tunnel, handledEventsToo: true);
        if (top is Window window)
        {
            window.Activated += (_, _) => Focus(true);
            window.Deactivated += (_, _) => Focus(false);
        }
    }

    private void Focus(bool active)
    {
        if (m_session == null)
            return;
        m_session.Input.Active = active;
        m_session.Post(active ? ScnEvent.Activate : ScnEvent.Deactivate);
    }

    private void Key(KeyEventArgs e, bool down)
    {
        if (m_session == null)
            return;
        // Alt+Enter: the engine's full screen switch
        if (down && e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            m_session.Post(ScnEvent.ToggleFullScreen);
            e.Handled = true;
            return;
        }
        m_session.Input.Press(e.Key, down);
        e.Handled = true;
    }

    private void Pointer(PointerEventArgs e)
    {
        if (m_session == null || m_bitmap == null)
            return;
        var point = e.GetCurrentPoint(m_image);
        // The picture is scaled uniformly into the image's bounds, centred
        var size = m_image.Bounds.Size;
        double scale = Math.Min(size.Width / m_session.Width, size.Height / m_session.Height);
        if (scale <= 0)
            return;
        double left = (size.Width - m_session.Width * scale) / 2, top = (size.Height - m_session.Height * scale) / 2;
        m_session.Input.Position = ((int)Math.Floor((point.Position.X - left) / scale), (int)Math.Floor((point.Position.Y - top) / scale));
        var p = point.Properties;
        bool touch = e.Pointer.Type is PointerType.Touch or PointerType.Pen;
        m_session.Input.Buttons = (p.IsLeftButtonPressed || touch && e.RoutedEvent == PointerPressedEvent ? 1 : 0)
            | (p.IsRightButtonPressed ? 2 : 0) | (p.IsMiddleButtonPressed ? 4 : 0);
        if (touch && e.RoutedEvent == PointerReleasedEvent)
            m_session.Input.Buttons = 0;
        if (e.RoutedEvent == PointerPressedEvent)
            e.Pointer.Capture(m_image);
    }

    /// <summary>Asks for the game's folder, starting from the one played last.</summary>
    public async Task ChooseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var options = new FolderPickerOpenOptions { Title = "Choose the folder of an installed game", AllowMultiple = false };
        try
        {
            if (File.Exists(LastFolderFile) && File.ReadAllText(LastFolderFile).Trim() is { Length: > 0 } last && Directory.Exists(last))
                options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(last);
        }
        catch (Exception)
        {
            // No remembered folder
        }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await OpenAsync(path);
    }

    /// <summary>Recognises the game in a folder, opens its archives and starts it.</summary>
    public async Task OpenAsync(string folder)
    {
        m_message.Text = "Opening the game…";
        try
        {
            var data = await Task.Run(() =>
            {
                string archive = GameData.FindArchive(folder) ?? throw new InvalidDataException("The folder has no .WAR archives.");
                var formats = FormatManager.Instance;
                var scheme = formats.LookupGame(archive) is { } name ? formats.GetScheme(name) : null;
                if (scheme == null)
                    throw new InvalidDataException("The game was not recognised: keep the game's own .exe in the folder.");
                return GameData.Open(folder, scheme);
            });
            GameSession session;
            try
            {
                session = await Task.Run(() => new GameSession(data, this));
            }
            catch
            {
                data.Dispose();
                throw;
            }
            Start(session);
            Remember(folder);
        }
        catch (Exception ex)
        {
            m_message.Text = $"Could not play the game in {folder}:\n{ex.Message}";
        }
    }

    private static void Remember(string folder)
    {
        try
        {
            Directory.CreateDirectory(PlayerFolders.Root);
            File.WriteAllText(LastFolderFile, folder);
        }
        catch (Exception)
        {
            // Only a convenience
        }
    }

    private void Start(GameSession session)
    {
        m_session = session;
        m_bitmap = new WriteableBitmap(new PixelSize(session.Width, session.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        m_image.Source = m_bitmap;
        m_image.IsVisible = true;
        m_start.IsVisible = false;
        TitleChanged?.Invoke(session.Data.SchemeName);
        session.Start();
        Focus();
        m_animating = true;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (m_animating && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(_ => OnFrame());
    }

    /// <summary>A frame of the window: the game's latest picture, and the interpreter's next frame.</summary>
    private unsafe void OnFrame()
    {
        if (m_session == null || m_bitmap == null)
            return;
        using (var buffer = m_bitmap.Lock())
        {
            var target = new Span<byte>((void*)buffer.Address, buffer.RowBytes * m_session.Height);
            if (m_session.TakeFrame(target, buffer.RowBytes))
                m_image.InvalidateVisual();
        }
        m_session.FrameTick();
        RequestFrame();
    }

    public void SetTitle(string title) => TitleChanged?.Invoke(title);

    public void SetFullScreen(bool fullScreen) => FullScreenChanged?.Invoke(fullScreen);

    public Task<int> ShowMessageAsync(string text, string caption, int type) =>
        MessageDialog.ShowAsync(TopLevel.GetTopLevel(this), text, caption, type);

    public void Stopped(Exception? error, string? log)
    {
        m_animating = false;
        OnFrame();
        if (error == null)
        {
            GameEnded?.Invoke();
            return;
        }
        string message = error is ScnException ? error.Message : $"{error.GetType().Name}: {error.Message}";
        _ = MessageDialog.ShowAsync(TopLevel.GetTopLevel(this), log != null ? $"{message}\n\nDetails: {log}" : message, "OpenShiina", 0);
    }

    /// <summary>Stops the game (the window closes).</summary>
    public void Close()
    {
        m_animating = false;
        m_session?.Dispose();
        m_session = null;
    }
}
