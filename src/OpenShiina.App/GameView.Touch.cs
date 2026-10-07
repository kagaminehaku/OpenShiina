// The game with fingers (phones and tablets): the games are made for a mouse and a keyboard, so a
// finger stands in for both.
//   - Touching moves the pointer there without pressing (the menus light the item under it);
//     lifting the finger soon after, where it went down, clicks there (left button down, then up a
//     few frames later, so scripts that read the button between frames see it). Two taps close
//     together are a double click.
//   - Moving the finger further than a few pixels presses the left button where it went down and
//     drags (sliders, scroll bars) until it is lifted.
//   - Holding the finger still for half a second is a right click (the game's menu, hiding the
//     message window); so is tapping with two fingers.
//   - Two fingers moved up or down turn the wheel: down is a turn away (the backlog opens), up a
//     turn towards.
//   - Android's Back button is a right click while a game plays; on the home screen it leaves.
// A bar of buttons on the right, over the black beside the picture when there is room, does what
// the keyboard does in the games: Menu (right click), Auto ('A', AUTO on / off), Skip (hold to
// skip, as Ctrl on desktop), Log (a turn of the wheel away) and Exit (as the window's X: the scripts are asked);
// its arrow folds it away. It shows from the start where the screen is the only input
// (PlayerPlatform.Touch), elsewhere once a finger touches the game.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenShiina.Scripting;

namespace OpenShiina.App;

public sealed partial class GameView
{
    // How far a finger may wander and still tap (device independent pixels), how long it is held
    // for a right click, how long a click holds the button, how close two taps make a double
    // click, and how far two fingers go for a turn of the wheel
    private const double TouchSlop = 12, DoubleTapSlop = 24, WheelStep = 48;
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClickTime = TimeSpan.FromMilliseconds(70);
    private const long DoubleTapMilliseconds = 400;

    // The finger that leads (and a second one), where it went down, and what it is doing
    private IPointer? m_finger, m_secondFinger;
    private Point m_fingerStart, m_fingerLast;
    private bool m_dragging, m_held, m_twoFingers, m_scrolled;
    private double m_wheelRest;
    private IDisposable? m_holdTimer;
    private long m_lastTapTime = long.MinValue;
    private Point m_lastTap;

    private bool m_touchSeen;
    private bool m_barFolded;
    private StackPanel? m_barButtons;
    private Button? m_barFold;
    private Border? m_bar;
    private IDisposable? m_keepAwake;

    private Control TouchBar()
    {
        Button Make(string text, Action action)
        {
            var button = new Button
            {
                Content = text, Width = 64, Height = 44, Padding = new Thickness(0), FontSize = 14,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, Foreground = Brushes.White, Focusable = false,
            };
            button.Click += (_, _) => action();
            return button;
        }
        var skipButton = new Button
        {
            Content = "Skip", Width = 64, Height = 44, Padding = new Thickness(0), FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, Foreground = Brushes.White, Focusable = false,
        };
        skipButton.AddHandler(PointerPressedEvent, (_, e) =>
        {
            HoldCtrl(true);
            skipButton.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            e.Pointer.Capture(skipButton);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        skipButton.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            HoldCtrl(false);
            skipButton.Background = Brushes.Transparent;
        }, RoutingStrategies.Tunnel);
        skipButton.PointerCaptureLost += (_, _) =>
        {
            HoldCtrl(false);
            skipButton.Background = Brushes.Transparent;
        };
        m_barButtons = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Make("Menu", RightClick),
                Make("Auto", () => TypeKey(Avalonia.Input.Key.A)),
                skipButton,
                Make("Log", () => Wheel(1)),
                Make("Exit", () => AllowClose()),
            },
        };
        m_barFold = Make("›", () =>
        {
            m_barFolded = !m_barFolded;
            UpdateTouchBar();
        });
        m_bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x10, 0x14)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.85,
            IsVisible = false,
            Child = new StackPanel { Spacing = 2, Children = { m_barFold, m_barButtons } },
        };
        return m_bar;
    }

    /// <summary>Hold or release Ctrl (VK_CONTROL) for hold-to-skip on touch.</summary>
    private void HoldCtrl(bool down)
    {
        if (m_session is not { } session)
            return;
        // VK_LCONTROL = 0xA2, VK_CONTROL = 0x11
        session.Input.Press(Avalonia.Input.Key.LeftCtrl, down);
        session.PostMessage(down ? ScnMessage.KeyDown : ScnMessage.KeyUp, 0x11, ScnMessage.Key(0, down, false));
    }

    private void UpdateTouchBar()
    {
        if (m_bar == null || m_barButtons == null || m_barFold == null)
            return;
        m_bar.IsVisible = m_session != null && (PlayerPlatform.Touch || m_touchSeen);
        m_barButtons.IsVisible = !m_barFolded;
        m_barFold.Content = m_barFolded ? "‹" : "›";
    }

    /// <summary>A game started or ended: the bar, the system bars and keeping the screen on (phones and tablets).</summary>
    private async void TouchSession(bool playing)
    {
        ResetTouch();
        UpdateTouchBar();
        if (!PlayerPlatform.Touch || TopLevel.GetTopLevel(this) is not { } top)
            return;
        if (top.InsetsManager is { } insets)
        {
            insets.DisplayEdgeToEdgePreference = playing;
            insets.IsSystemBarVisible = !playing;
        }
        m_keepAwake?.Dispose();
        m_keepAwake = null;
        if (playing)
        {
            try
            {
                var keepAwake = await top.RequestPlatformInhibition(PlatformInhibitionType.AppSleep, "Playing a game");
                if (m_session != null && m_keepAwake == null)
                    m_keepAwake = keepAwake;
                else
                    keepAwake.Dispose();
            }
            catch (Exception)
            {
                // The screen may go dark on its own
            }
        }
    }

    /// <summary>Android's Back: a right click while a game plays (else the platform's own: leaving).</summary>
    private void OnBackRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (m_session == null)
            return;
        e.Handled = true;
        RightClick();
    }

    /// <summary>A point of the image as a point of the game's picture, or null before the picture is placed.</summary>
    private (int X, int Y)? GamePoint(Point p)
    {
        if (m_session == null)
            return null;
        var (scale, left, top) = Placement(m_session);
        if (scale <= 0)
            return null;
        return ((int)Math.Floor((p.X - left) / scale), (int)Math.Floor((p.Y - top) / scale));
    }

    /// <summary>The pointer goes to <paramref name="p"/> (of the image); its point in the game.</summary>
    private int MoveTo(Point p)
    {
        if (m_session == null || GamePoint(p) is not { } point)
            return 0;
        m_session.Input.Position = point;
        return ScnMessage.Point(point.X, point.Y);
    }

    private int CurrentPoint()
    {
        if (m_session == null)
            return 0;
        var (x, y) = m_session.Input.Position;
        return ScnMessage.Point(x, y);
    }

    private void Touch(PointerEventArgs e)
    {
        if (m_session == null)
            return;
        if (!m_touchSeen)
        {
            m_touchSeen = true;
            UpdateTouchBar();
        }
        var p = e.GetCurrentPoint(m_image).Position;
        if (e.RoutedEvent == PointerPressedEvent)
        {
            if (m_finger == null)
            {
                m_finger = e.Pointer;
                m_fingerStart = m_fingerLast = p;
                m_dragging = m_held = m_twoFingers = m_scrolled = false;
                m_wheelRest = 0;
                e.Pointer.Capture(m_image);
                MoveTo(p);
                m_holdTimer?.Dispose();
                m_holdTimer = DispatcherTimer.RunOnce(OnHold, HoldTime);
            }
            else if (m_secondFinger == null && !m_dragging && !m_held)
            {
                // Two fingers: a right click when they lift, or the wheel when they move
                m_secondFinger = e.Pointer;
                m_twoFingers = true;
                e.Pointer.Capture(m_image);
                CancelHold();
            }
        }
        else if (e.RoutedEvent == PointerMovedEvent)
        {
            if (e.Pointer != m_finger)
                return;
            if (m_twoFingers)
            {
                m_wheelRest += p.Y - m_fingerLast.Y;
                while (Math.Abs(m_wheelRest) >= WheelStep)
                {
                    // Fingers going down pull the text down: a turn away (back to earlier lines)
                    int turn = m_wheelRest > 0 ? 1 : -1;
                    Wheel(turn);
                    m_wheelRest -= turn * WheelStep;
                    m_scrolled = true;
                }
            }
            else if (m_dragging)
                MoveTo(p);
            else if (!m_held && Distance(p, m_fingerStart) > TouchSlop)
            {
                // A drag: the button goes down where the finger did, then follows it
                CancelHold();
                m_dragging = true;
                int start = MoveTo(m_fingerStart);
                m_session.Input.Buttons = 1;
                m_session.PostMessage(ScnMessage.LButtonDown, 1, start);
                MoveTo(p);
            }
            m_fingerLast = p;
        }
        else if (e.RoutedEvent == PointerReleasedEvent)
        {
            if (e.Pointer == m_secondFinger)
            {
                m_secondFinger = null;
                return;
            }
            if (e.Pointer != m_finger)
                return;
            CancelHold();
            if (m_twoFingers)
            {
                if (!m_scrolled)
                    RightClick();
            }
            else if (m_dragging)
            {
                int point = MoveTo(p);
                m_session.Input.Buttons = 0;
                m_session.PostMessage(ScnMessage.LButtonUp, 0, point);
            }
            else if (!m_held)
                Tap(m_fingerStart);
            m_finger = null;
            m_dragging = false;
        }
    }

    /// <summary>The system took the finger away (a gesture of its own): a drag lets go.</summary>
    private void TouchLost(IPointer pointer)
    {
        if (pointer == m_secondFinger)
            m_secondFinger = null;
        if (pointer != m_finger)
            return;
        CancelHold();
        if (m_dragging && m_session != null)
        {
            m_session.Input.Buttons = 0;
            m_session.PostMessage(ScnMessage.LButtonUp, 0, CurrentPoint());
        }
        m_finger = null;
        m_dragging = false;
    }

    private void ResetTouch()
    {
        CancelHold();
        m_finger = m_secondFinger = null;
        m_dragging = m_held = m_twoFingers = m_scrolled = false;
        m_lastTapTime = long.MinValue;
    }

    private void CancelHold()
    {
        m_holdTimer?.Dispose();
        m_holdTimer = null;
    }

    private void OnHold()
    {
        m_holdTimer = null;
        if (m_finger == null || m_dragging || m_twoFingers || m_session == null)
            return;
        m_held = true;
        RightClick();
    }

    /// <summary>A left click at <paramref name="p"/> (of the image): down now, up a few frames later.</summary>
    private void Tap(Point p)
    {
        if (m_session is not { } session)
            return;
        int point = MoveTo(p);
        long now = Environment.TickCount64;
        bool second = now - m_lastTapTime < DoubleTapMilliseconds && Distance(p, m_lastTap) < DoubleTapSlop;
        // A double click is the second of two clicks (as Windows sends it); a third starts again
        m_lastTapTime = second ? long.MinValue : now;
        m_lastTap = p;
        session.Input.Buttons = 1;
        session.PostMessage(second ? ScnMessage.LButtonDoubleClick : ScnMessage.LButtonDown, 1, point);
        DispatcherTimer.RunOnce(() =>
        {
            if (m_session != session)
                return;
            // A drag that started since keeps its button
            if (!m_dragging)
                session.Input.Buttons = 0;
            session.PostMessage(ScnMessage.LButtonUp, 0, point);
        }, ClickTime);
    }

    /// <summary>A right click where the pointer is.</summary>
    private void RightClick()
    {
        if (m_session is not { } session)
            return;
        int point = CurrentPoint();
        session.Input.Buttons = 2;
        session.PostMessage(ScnMessage.RButtonDown, 2, point);
        DispatcherTimer.RunOnce(() =>
        {
            if (m_session != session)
                return;
            if (!m_dragging)
                session.Input.Buttons = 0;
            session.PostMessage(ScnMessage.RButtonUp, 0, point);
        }, ClickTime);
    }

    /// <summary>Turns of the wheel where the pointer is: 1 away from the user, -1 towards.</summary>
    private void Wheel(int turns) =>
        m_session?.PostMessage(ScnMessage.MouseWheel, turns * 120 << 16, CurrentPoint());

    /// <summary>A key pressed and let go, as the keyboard would.</summary>
    private void TypeKey(Key key)
    {
        if (m_session is not { } session)
            return;
        int vk = VirtualKeys.From(key);
        session.Input.Press(key, true);
        session.PostMessage(ScnMessage.KeyDown, vk, ScnMessage.Key(0, true, false));
        DispatcherTimer.RunOnce(() =>
        {
            if (m_session != session)
                return;
            session.Input.Press(key, false);
            session.PostMessage(ScnMessage.KeyUp, vk, ScnMessage.Key(0, false, false));
        }, ClickTime);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
