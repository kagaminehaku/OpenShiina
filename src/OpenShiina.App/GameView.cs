// The player's one view, on every platform: first the home screen with the games of the library
// (LibraryView; or the folder given on the command line), then the game's picture, scaled to the view with its
// proportions kept, black around it. Keyboard, mouse, touch and joystick go to the game's
// InputState (a touch is the left button), and keys, buttons and the wheel to the scripts as the
// window messages of the engine. Closing asks the scripts first, as WM_CLOSE does. Each frame the view draws,
// it reads the joystick, takes the game's latest picture and lets the interpreter run its next frame.

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
    private readonly LibraryView m_library = new();
    private GameSession? m_session;
    private SdlJoystick? m_joystick;
    // Closing: asked the scripts and waiting for their answer / they let the window close
    private bool m_closeAsked, m_closeAllowed;
    private WriteableBitmap? m_bitmap;
    private bool m_animating;

    /// <summary>The window title the game asks for (the desktop window shows it).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>The game asks for full screen or a window.</summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>A game ended and the home screen is back (the window shows the player's title again).</summary>
    public event Action? GameEnded;

    public GameView()
    {
        Background = Brushes.Black;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(m_image, BitmapInterpolationMode.None);
        m_library.Play += async folder => await OpenAsync(folder);
        m_library.AddRequested += async () => await AddGameAsync();
        Content = new Grid { Children = { m_image, m_library } };

        m_image.PointerMoved += (_, e) => Pointer(e);
        m_image.PointerPressed += (_, e) => Pointer(e);
        m_image.PointerReleased += (_, e) => Pointer(e);
        m_image.PointerCaptureLost += (_, _) => { if (m_session != null) m_session.Input.Buttons = 0; };
        m_image.PointerWheelChanged += (_, e) =>
        {
            if (m_session != null && e.Delta.Y != 0)
                m_session.PostMessage(ScnMessage.MouseWheel, (int)Math.Round(e.Delta.Y * 120) << 16 | MouseKeys(e), PointOf(e));
        };
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
            // Minimised counts as not in front (a minimised window can be activated again)
            window.Activated += (_, _) => { m_activated = true; UpdateFocus(window); };
            window.Deactivated += (_, _) => { m_activated = false; UpdateFocus(window); };
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty)
                    UpdateFocus(window);
            };
        }
    }

    // The last activation event (IsActive may not be up to date while they are raised), and
    // what the game was last told: in front or not
    private bool m_activated = true;
    private bool? m_focused;

    private void UpdateFocus(Window window)
    {
        bool focused = m_activated && window.WindowState != WindowState.Minimized;
        if (focused == m_focused || m_session == null)
            return;
        m_focused = focused;
        Focus(focused);
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
        if (VirtualKeys.From(e.Key) is var vk and not 0)
        {
            // Ctrl, Shift and Alt as the general key, as WM_KEYDOWN gives them
            int general = VirtualKeys.General(vk);
            m_session.PostMessage(down ? ScnMessage.KeyDown : ScnMessage.KeyUp, general != 0 ? general : vk, ScnMessage.Key(0, down, false));
        }
        e.Handled = true;
    }

    /// <summary>wParam of a mouse message: MK_LBUTTON 1, MK_RBUTTON 2, MK_SHIFT 4, MK_CONTROL 8, MK_MBUTTON 0x10.</summary>
    private static int MouseKeys(PointerEventArgs e)
    {
        var p = e.GetCurrentPoint(null).Properties;
        return (p.IsLeftButtonPressed ? 1 : 0) | (p.IsRightButtonPressed ? 2 : 0) | (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 4 : 0) |
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 8 : 0) | (p.IsMiddleButtonPressed ? 0x10 : 0);
    }

    /// <summary>lParam of a mouse message: the point in the game's picture.</summary>
    private int PointOf(PointerEventArgs e)
    {
        if (m_session == null)
            return 0;
        var (scale, left, top) = Placement(m_session);
        if (scale <= 0)
            return 0;
        var p = e.GetCurrentPoint(m_image).Position;
        return ScnMessage.Point((int)Math.Floor((p.X - left) / scale), (int)Math.Floor((p.Y - top) / scale));
    }

    private void Pointer(PointerEventArgs e)
    {
        if (m_session == null || m_bitmap == null)
            return;
        var point = e.GetCurrentPoint(m_image);
        var (scale, left, top) = Placement(m_session);
        if (scale <= 0)
            return;
        m_session.Input.Position = ((int)Math.Floor((point.Position.X - left) / scale), (int)Math.Floor((point.Position.Y - top) / scale));
        var p = point.Properties;
        bool touch = e.Pointer.Type is PointerType.Touch or PointerType.Pen;
        m_session.Input.Buttons = (p.IsLeftButtonPressed || touch && e.RoutedEvent == PointerPressedEvent ? 1 : 0)
            | (p.IsRightButtonPressed ? 2 : 0) | (p.IsMiddleButtonPressed ? 4 : 0);
        if (touch && e.RoutedEvent == PointerReleasedEvent)
            m_session.Input.Buttons = 0;
        if (e.RoutedEvent == PointerPressedEvent)
            e.Pointer.Capture(m_image);
        // WM_xBUTTONDOWN / UP for the scripts
        if (e is PointerPressedEventArgs pressed)
        {
            int message = point.Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => pressed.ClickCount == 2 ? ScnMessage.LButtonDoubleClick : ScnMessage.LButtonDown,
                PointerUpdateKind.RightButtonPressed => ScnMessage.RButtonDown,
                PointerUpdateKind.MiddleButtonPressed => ScnMessage.MButtonDown,
                _ => touch ? ScnMessage.LButtonDown : 0,
            };
            if (message != 0)
                m_session.PostMessage(message, MouseKeys(e), PointOf(e));
        }
        else if (e is PointerReleasedEventArgs released)
        {
            int message = released.InitialPressMouseButton switch
            {
                MouseButton.Left => ScnMessage.LButtonUp,
                MouseButton.Right => ScnMessage.RButtonUp,
                MouseButton.Middle => ScnMessage.MButtonUp,
                _ => touch ? ScnMessage.LButtonUp : 0,
            };
            if (message != 0)
                m_session.PostMessage(message, MouseKeys(e), PointOf(e));
        }
    }

    /// <summary>The picture is scaled uniformly into the image's bounds, centred.</summary>
    private (double Scale, double Left, double Top) Placement(GameSession session)
    {
        var size = m_image.Bounds.Size;
        double scale = Math.Min(size.Width / session.Width, size.Height / session.Height);
        return (scale, (size.Width - session.Width * scale) / 2, (size.Height - session.Height * scale) / 2);
    }

    /// <summary>Asks for a game's folder (starting next to the game played last); null when none was chosen.</summary>
    private async Task<string?> ChooseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var options = new FolderPickerOpenOptions { Title = "Choose the folder of an installed game", AllowMultiple = false };
        try
        {
            if (GameLibrary.Load().FirstOrDefault(g => g.Available) is { } last && Path.GetDirectoryName(last.Folder) is { } near)
                options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(near);
        }
        catch (Exception)
        {
            // No folder to start from
        }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>"Add a game…": a folder into the library, when the game in it is recognised.</summary>
    private async Task AddGameAsync()
    {
        if (await ChooseFolderAsync() is not { } folder)
            return;
        var game = await Task.Run(() => GameLibrary.Add(folder));
        m_library.Message = game == null
            ? $"No game was recognised in {folder}: choose the folder with the game's own .exe and .WAR files."
            : "";
        m_library.Refresh();
    }

    /// <summary>Recognises the game in a folder, opens its archives and starts it.</summary>
    public async Task OpenAsync(string folder)
    {
        m_library.Message = "Opening the game…";
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
            m_library.Message = "";
            GameLibrary.Played(folder);
        }
        catch (Exception ex)
        {
            m_library.Message = $"Could not play the game in {folder}:\n{ex.Message}";
        }
    }

    private void Start(GameSession session)
    {
        m_session = session;
        m_bitmap = new WriteableBitmap(new PixelSize(session.Width, session.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        m_image.Source = m_bitmap;
        m_image.IsVisible = true;
        m_library.IsVisible = false;
        TitleChanged?.Invoke(session.Data.SchemeName);
        session.CloseAnswered += close =>
        {
            m_closeAsked = false;
            if (close)
            {
                m_closeAllowed = true;
                ReturnToLibrary();
            }
        };
        if (session.Setup.Joypad != false)
            m_joystick = new SdlJoystick();
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
        if (m_joystick != null)
            m_session.Input.Joystick = m_joystick.Poll();
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

    public void MovePointer(int x, int y)
    {
        if (m_session == null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var (scale, left, offset) = Placement(m_session);
        // The middle of the pixel, so that the pointer's move reads back as (x, y)
        if (scale > 0 && m_image.TranslatePoint(new Point(left + (x + 0.5) * scale, offset + (y + 0.5) * scale), top) is { } point)
            PointerWarp.To(top, point);
    }

    public Task<int> ShowMessageAsync(string text, string caption, int type) =>
        MessageDialog.ShowAsync(TopLevel.GetTopLevel(this), text, caption, type);

    public async void Stopped(Exception? error, string? log)
    {
        m_animating = false;
        OnFrame();
        if (error != null)
        {
            string message = error is ScnException ? error.Message : $"{error.GetType().Name}: {error.Message}";
            await MessageDialog.ShowAsync(TopLevel.GetTopLevel(this), log != null ? $"{message}\n\nDetails: {log}" : message, "OpenShiina", 0);
        }
        ReturnToLibrary();
    }

    /// <summary>
    /// The window is asked to close (its X), or the back button: true when nothing is playing (the
    /// window may close). While a game plays, the scripts are asked first (WM_CLOSE) and the home
    /// screen comes back when they let it; a second try before they answer, or a game that no
    /// longer runs, goes back at once.
    /// </summary>
    public bool AllowClose()
    {
        if (m_session == null)
            return true;
        if (m_closeAllowed || m_closeAsked || !m_session.Running)
            ReturnToLibrary();
        else
        {
            m_closeAsked = true;
            m_session.RequestClose();
        }
        return false;
    }

    /// <summary>The game is put away and the home screen shows again.</summary>
    public void ReturnToLibrary()
    {
        if (m_session == null)
            return;
        Close();
        m_closeAsked = m_closeAllowed = false;
        m_image.IsVisible = false;
        m_image.Source = null;
        m_bitmap?.Dispose();
        m_bitmap = null;
        m_library.IsVisible = true;
        m_library.Refresh();
        FullScreenChanged?.Invoke(false);
        TitleChanged?.Invoke("OpenShiina");
        GameEnded?.Invoke();
    }

    /// <summary>Stops the game (the window closes).</summary>
    public void Close()
    {
        m_animating = false;
        m_session?.Dispose();
        m_session = null;
        m_focused = null;
        m_joystick?.Dispose();
        m_joystick = null;
    }
}
