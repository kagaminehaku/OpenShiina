// The player's one view, on every platform: first the home screen with the games of the library
// (LibraryView; or the folder given on the command line), then the game's picture, scaled to the view with its
// proportions kept as the setting "Scaling" says (GamePicture), black around it. Keyboard, mouse, touch and joystick go to the game's
// InputState (fingers as GameView.Touch.cs makes them a mouse), and keys, buttons and the wheel to
// the scripts as the window messages of the engine. Closing asks the scripts first, as WM_CLOSE does. Each frame the view draws,
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
using OpenShiina.Localization;

namespace OpenShiina.App;

public sealed partial class GameView : UserControl, IGameWindow
{
    private readonly GamePicture m_image = new() { IsVisible = false };
    private LibraryView m_library;
    // False: a game alone, without the home screen (OpenShiina Tools plays a game so)
    private readonly bool m_hasLibrary;
    private readonly Grid m_layers;
    private GameSession? m_session;
    private IPlayerJoystick? m_joystick;
    // Closing: asked the scripts and waiting for their answer / they let the window close
    private bool m_closeAsked, m_closeAllowed;
    // The game's latest picture (BGRA), handed to the GamePicture when it changes
    private byte[]? m_frame;
    private bool m_animating;

    /// <summary>The window title the game asks for (the desktop window shows it).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>The game asks for full screen or a window.</summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>A game started: the size of its picture in pixels (the desktop window takes it).</summary>
    public event Action<int, int>? GameSizeChanged;

    /// <summary>A game ended and the home screen is back (the window shows the player's title again).</summary>
    public event Action? GameEnded;

    /// <param name="library">False for a game alone: the home screen never shows (a game that ends raises <see cref="GameEnded"/>).</param>
    public GameView(bool library = true)
    {
        StartupLog.Mark("the library made (GameView)");
        Background = Brushes.Black;
        Focusable = true;
        m_hasLibrary = library;
        m_library = NewLibrary();
        m_library.IsVisible = library;
        m_layers = new Grid { Children = { m_image, m_frameRate, m_lineId, TouchBar(), m_library } };
        Content = m_layers;

        m_image.PointerMoved += (_, e) => Pointer(e);
        m_image.PointerPressed += (_, e) => Pointer(e);
        m_image.PointerReleased += (_, e) => Pointer(e);
        m_image.PointerCaptureLost += (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Touch)
                TouchLost(e.Pointer);
            else if (m_session != null)
                m_session.Input.Buttons = 0;
        };
        m_image.PointerWheelChanged += (_, e) =>
        {
            if (m_session != null && e.Delta.Y != 0)
                m_session.PostMessage(ScnMessage.MouseWheel, (int)Math.Round(e.Delta.Y * 120) << 16 | MouseKeys(e), PointOf(e));
        };
    }

    /// <summary>The home screen, wired to the view.</summary>
    private LibraryView NewLibrary()
    {
        var library = new LibraryView();
        library.Play += async folder => await OpenAsync(folder);
        library.AddRequested += async () => await AddGameAsync();
        library.LanguageChanged += () => Dispatcher.UIThread.Post(Relanguage);
        return library;
    }

    /// <summary>
    /// The language changed in the settings: the home screen and the touch bar are made again in
    /// it, the settings open where they were (on their Appearance page).
    /// </summary>
    private void Relanguage()
    {
        var old = m_library;
        m_library = NewLibrary();
        m_library.IsVisible = old.IsVisible;
        m_layers.Children[m_layers.Children.IndexOf(old)] = m_library;
        if (m_bar != null)
        {
            m_layers.Children[m_layers.Children.IndexOf(m_bar)] = TouchBar();
            UpdateTouchBar();
        }
        m_library.OpenSettings(SettingsPages.Appearance);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        top.RequestAnimationFrame(_ => StartupLog.Finish("the first frame on screen"));
        // Keys reach the game wherever the focus is
        top.AddHandler(KeyDownEvent, (_, k) => Key(k, true), RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(KeyUpEvent, (_, k) => Key(k, false), RoutingStrategies.Tunnel, handledEventsToo: true);
        top.BackRequested += OnBackRequested;
        // Phones: the app going to the background or coming back
        m_activeChanged = active =>
        {
            m_activated = active;
            UpdateFocus(top as Window);
        };
        PlayerPlatform.ActiveChanged += m_activeChanged;
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

    private Action<bool>? m_activeChanged;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // A view of a window that closed (each game OpenShiina Tools plays has its own) is not kept
        PlayerPlatform.ActiveChanged -= m_activeChanged;
        m_activeChanged = null;
    }

    // The last activation event (IsActive may not be up to date while they are raised), and
    // what the game was last told: in front or not
    private bool m_activated = true;
    private bool? m_focused;

    private void UpdateFocus(Window? window)
    {
        bool focused = m_activated && window?.WindowState != WindowState.Minimized;
        if (focused == m_focused || m_session == null)
            return;
        m_focused = focused;
        Focus(focused);
    }

    private void Focus(bool active)
    {
        if (m_session == null)
            return;
        if (active)
            m_playClock.Start();
        else
            CountPlayTime();
        if (!active)
            ResetTouch();
        m_session.Input.Active = active;
        m_session.Post(active ? ScnEvent.Activate : ScnEvent.Deactivate);
    }

    private void Key(KeyEventArgs e, bool down)
    {
        if (m_session == null)
            return;
        // The system's keys (volume, media, and keys Avalonia has no name for) are left to it:
        // taken here, a phone's volume buttons did nothing while a game ran
        if (e.Key is Avalonia.Input.Key.None or Avalonia.Input.Key.VolumeUp or Avalonia.Input.Key.VolumeDown
            or Avalonia.Input.Key.VolumeMute or Avalonia.Input.Key.MediaPlayPause or Avalonia.Input.Key.MediaStop
            or Avalonia.Input.Key.MediaNextTrack or Avalonia.Input.Key.MediaPreviousTrack)
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
        if (m_session == null || m_frame == null)
            return;
        // Fingers are a mouse of their own (GameView.Touch.cs); a pen is a mouse
        if (e.Pointer.Type == PointerType.Touch)
        {
            Touch(e);
            return;
        }
        var point = e.GetCurrentPoint(m_image);
        var (scale, left, top) = Placement(m_session);
        if (scale <= 0)
            return;
        m_session.Input.Position = ((int)Math.Floor((point.Position.X - left) / scale), (int)Math.Floor((point.Position.Y - top) / scale));
        var p = point.Properties;
        bool touch = e.Pointer.Type == PointerType.Pen;
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

    /// <summary>Where the picture is in the view, as the setting places it (GamePicture.Placement).</summary>
    private (double Scale, double Left, double Top) Placement(GameSession session) => m_image.Placement();

    /// <summary>Asks for a game's folder (starting next to the game played last); null when none was chosen.</summary>
    private async Task<string?> ChooseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;
        var options = new FolderPickerOpenOptions { Title = Strings.ChooseFolderTitle, AllowMultiple = false };
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
        if (folders.Count == 0)
            return null;
        if (PlayerPlatform.FolderPath(folders[0]) is { } path)
            return path;
        m_library.Message = Strings.CannotReadFolder;
        return null;
    }

    /// <summary>
    /// "Add a game…": a folder into the library, when the game in it is recognised: by its .exe,
    /// else by the keys that open its archives (SchemeDetector; the user picks when several fit).
    /// </summary>
    private async Task AddGameAsync()
    {
        if (!await FileAccessAsync())
            return;
        if (await ChooseFolderAsync() is not { } folder)
            return;
        var game = await Task.Run(() => GameLibrary.Add(folder));
        if (game == null && GameData.FindArchive(folder) != null)
        {
            m_library.Message = Strings.UnknownExeLooking;
            var found = await Task.Run(() => SchemeDetector.Detect(folder));
            string? chosen = found.Count switch
            {
                0 => null,
                1 => found[0].Name,
                _ => await m_library.ChooseAsync(
                    string.Format(Strings.WhichGame, found.Count, folder),
                    found.Select(c => c.Name).ToList()),
            };
            if (chosen != null)
                game = await Task.Run(() => GameLibrary.Add(folder, chosen));
            else if (found.Count > 0)
            {
                m_library.Message = "";
                return;
            }
        }
        m_library.Message = game == null
            ? string.Format(Strings.NoGameRecognised, folder)
            : "";
        m_library.Refresh();
    }

    /// <summary>The player may read the games' folders (Android asks the user once); else it says so.</summary>
    private async Task<bool> FileAccessAsync()
    {
        try
        {
            if (await PlayerPlatform.EnsureFileAccessAsync())
                return true;
        }
        catch (Exception)
        {
            // Asked below
        }
        m_library.Message = Strings.NeedFileAccess;
        return false;
    }

    /// <summary>
    /// Recognises the game in a folder (or takes <paramref name="scheme"/>), opens its archives and
    /// starts it. Null when it started, else why not (the home screen says it too).
    /// </summary>
    public async Task<string?> OpenAsync(string folder, EncryptionScheme? scheme = null)
    {
        if (!await FileAccessAsync())
            return m_library.Message;
        m_library.Message = Strings.OpeningGame;
        try
        {
            var data = await Task.Run(() =>
            {
                _ = GameData.FindArchive(folder) ?? throw new InvalidDataException(Strings.NoWarArchives);
                var found = scheme ?? GameLibrary.SchemeFor(folder)
                    ?? throw new InvalidDataException(Strings.GameNotRecognised);
                return GameData.Open(folder, found);
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
            m_playFolder = folder;
            m_playClock.Restart();
            return null;
        }
        catch (Exception ex)
        {
            m_library.Message = string.Format(Strings.CouldNotPlay, folder, ex.Message);
            return m_library.Message;
        }
    }

    private void Start(GameSession session)
    {
        m_session = session;
        m_frame = new byte[session.Width * session.Height * 4];
        m_image.Scaling = session.Setup.Settings.Scaling;
        m_image.FsrSharpness = session.Setup.Settings.FsrSharpness;
        m_image.SetFrame(m_frame, session.Width, session.Height, session.Width * 4);
        m_image.IsVisible = true;
        m_library.IsVisible = false;
        TitleChanged?.Invoke(session.Data.SchemeName);
        GameSizeChanged?.Invoke(session.Width, session.Height);
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
            m_joystick = PlayerPlatform.OpenJoystick();
        WatchLines(session);
        session.Start();
        TouchSession(true);
        Focus();
        if (session.PacesItself)
        {
            // The game's pace: a new picture is drawn when the interpreter has one, the joystick
            // read 60 times a second (nothing runs while the game waits)
            session.FrameReady += TakeNewFrame;
            if (m_joystick != null)
            {
                m_joystickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / 60), DispatcherPriority.Input, (_, _) =>
                {
                    if (m_session != null && m_joystick != null)
                        m_session.Input.Joystick = m_joystick.Poll();
                });
                m_joystickTimer.Start();
            }
            return;
        }
        m_animating = true;
        RequestFrame();
    }

    private DispatcherTimer? m_joystickTimer;

    /// <summary>The game's translation was saved anew (OpenShiina Tools): the scenarios read from now on have it (ScnVm.ReloadTranslation).</summary>
    public void ReloadTranslation() => m_session?.Schedule((0, vm => vm.ReloadTranslation()));

    /// <summary>The game's pace: the interpreter's new picture into the view.</summary>
    private void TakeNewFrame()
    {
        if (m_session == null || m_frame == null)
            return;
        if (m_session.TakeFrame(m_frame, m_session.Width * 4, out var area) && !area.IsEmpty)
            m_image.SetFrame(m_frame, m_session.Width, m_session.Height, m_session.Width * 4);
    }

    private void RequestFrame()
    {
        if (m_animating && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(_ => OnFrame());
    }

    /// <summary>A frame of the window: the game's latest picture, and the interpreter's next frame.</summary>
    private void OnFrame()
    {
        if (m_session == null || m_frame == null)
            return;
        if (m_joystick != null)
            m_session.Input.Joystick = m_joystick.Poll();
        TakeNewFrame();
        m_session.FrameTick();
        RequestFrame();
    }

    public void SetTitle(string title) => TitleChanged?.Invoke(title);

    // The frame rate on phones and tablets, which have no title: a line over the game's corner
    private readonly TextBlock m_frameRate = new()
    {
        FontSize = 12, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
        Padding = new Thickness(6, 2), Margin = new Thickness(4),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false, IsVisible = false,
    };

    // The scenario line shown, over the game's other corner (ShowsLineIds)
    private readonly TextBlock m_lineId = new()
    {
        FontSize = 12, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
        Padding = new Thickness(6, 2), Margin = new Thickness(4),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false, IsVisible = false,
    };

    /// <summary>A message line of a scenario was shown ("MAK00.TXT:28", ScnVm.LineShown); OpenShiina Tools goes to it in the translation.</summary>
    public event Action<string>? LineShown;

    /// <summary>The scenario line shown is written over the game's corner (OpenShiina Tools).</summary>
    public bool ShowsLineIds { get; init; }

    /// <summary>
    /// "Play from here" (OpenShiina Tools): a new game starts at this line of a scenario, one of
    /// the game's scenarios (ScnVm.PlayFrom); the game's saves stay in memory.
    /// </summary>
    public (string File, int Line, IReadOnlyCollection<string> Scenarios)? PlayFrom { get; init; }

    /// <summary>The game started: its lines are told when something wants them (they are looked for only then).</summary>
    private void WatchLines(GameSession session)
    {
        if (PlayFrom is { } from)
        {
            session.Schedule((0, vm => vm.PlayFrom(from.File, from.Line, from.Scenarios)));
            m_lineId.Text = string.Format(Strings.PlayFromHint, $"{from.File}:{from.Line}");
            m_lineId.IsVisible = true;
        }
        if (LineShown == null && !ShowsLineIds)
            return;
        session.Schedule((0, vm => vm.LineShown += id => Dispatcher.UIThread.Post(() =>
        {
            if (m_session != session)
                return;
            m_lineId.Text = id;
            m_lineId.IsVisible = ShowsLineIds;
            LineShown?.Invoke(id);
        })));
    }

    public void ShowFrameRate(string text)
    {
        if (!PlayerPlatform.Touch)
        {
            TitleChanged?.Invoke(text);
            return;
        }
        m_frameRate.Text = text;
        m_frameRate.IsVisible = m_session != null;
    }

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

    /// <summary>The pointer over the game hidden or shown again (0461 / 0460).</summary>
    public void ShowPointer(bool shown) => Cursor = shown || m_session == null ? null : new Cursor(StandardCursorType.None);

    public Task<int> ShowMessageAsync(string text, string caption, int type) =>
        MessageDialog.ShowAsync(TopLevel.GetTopLevel(this), text, caption, type);

    public async void Stopped(Exception? error, string? log)
    {
        m_animating = false;
        Cursor = null;
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
        m_image.Clear();
        m_frame = null;
        m_library.IsVisible = m_hasLibrary;
        m_library.Refresh();
        m_frameRate.IsVisible = false;
        m_lineId.IsVisible = false;
        TouchSession(false);
        FullScreenChanged?.Invoke(false);
        TitleChanged?.Invoke("OpenShiina");
        GameEnded?.Invoke();
        // What the game left, once the calls it posted to this thread have run
        Dispatcher.UIThread.Post(GameMemory.CollectAfterGame, DispatcherPriority.Background);
    }

    // The game's play time: counted while it is in front, added to the library when it goes
    // behind (a phone's app may be closed there unseen) and when it ends
    private readonly System.Diagnostics.Stopwatch m_playClock = new();
    private string? m_playFolder;

    private void CountPlayTime()
    {
        m_playClock.Stop();
        if (m_playFolder != null && m_playClock.Elapsed >= TimeSpan.FromSeconds(1))
            GameLibrary.AddPlayTime(m_playFolder, m_playClock.Elapsed);
        m_playClock.Reset();
    }

    /// <summary>Stops the game (the window closes).</summary>
    public void Close()
    {
        CountPlayTime();
        m_playFolder = null;
        m_animating = false;
        m_joystickTimer?.Stop();
        m_joystickTimer = null;
        m_session?.Dispose();
        m_session = null;
        m_focused = null;
        m_keepAwake?.Dispose();
        m_keepAwake = null;
        m_joystick?.Dispose();
        m_joystick = null;
    }
}
