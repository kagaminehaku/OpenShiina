// Story player window: shows a game's scenario the way the engine does (pictures, text,
// voice, music, choices), reading everything from the game's own archives. The story itself
// (flow, commands, modes, saves) runs in the engine library's StoryPlayer; this window is its
// IStoryView and draws the screen (Stage), the message window, the choices and the pages.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenShiina.Windows;

public partial class PlayerWindow : Window, IStoryView
{
    private readonly GameData m_data;
    private readonly Stage m_stage;
    private readonly StoryPlayer m_player;

    private AudioEngine m_audio => m_player.Audio;

    private CancellationTokenSource? m_run;

    // The token of the run (story or title screen) the calling code belongs to
    private CancellationToken m_token => m_player.Token;

    // The message window was hidden by the user (right click); the next click shows it again
    private bool m_userHidWindow;

    /// <summary>Plays the story of the game in <paramref name="data"/>, which the window then owns.</summary>
    public PlayerWindow(GameData data)
    {
        InitializeComponent();
        m_data = data;
        m_stage = new Stage(StageCanvas);
        m_player = new StoryPlayer(data, m_stage, this, new AudioEngine(new WaveOutput()));

        Title = $"Play Story - {m_player.Title}";
        TxtTitle.Text = m_player.Title;
        TxtSubtitle.Text = "Plays the story from the game's own files: its flow script SRC_MAIN.SCN and scenario files, " +
                           "with the engine's effects, saves and settings.";

        var chapters = new ListCollectionView(m_player.Outline.GetChapters(data.ScriptTitle).ToList());
        chapters.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StoryChapter.Group)));
        ChapterList.ItemsSource = chapters;

        var window = data.GetSystemFrame(0);
        if (window != null)
        {
            ImgWindow.Source = window.Image.ToBitmapSource();
            Canvas.SetLeft(ImgWindow, window.OffsetX);
            Canvas.SetTop(ImgWindow, window.OffsetY);
            ImgWindow.Width = window.Width;
            ImgWindow.Height = window.Height;
        }
        else
        {
            WindowFallback.Visibility = Visibility.Visible;
        }

        // Gaiji (①...) are frames of GAIJI.S25 in order; the click wait icon is SYSTEM2.S25 slot 20
        var gaiji = data.LoadFrames("GAIJI.S25")?.OrderBy(f => f.Slot).ToList();
        TxtMessage.Gaiji = c => gaiji != null && c - '①' is int i && i >= 0 && i < gaiji.Count ? gaiji[i] : null;
        if (data.LoadFrames("SYSTEM2.S25")?.FirstOrDefault(f => f.Slot == 20) is { } wait)
        {
            TxtNext.Source = wait.Image.ToBitmapSource();
            Canvas.SetLeft(TxtNext, wait.OffsetX);
            Canvas.SetTop(TxtNext, wait.OffsetY);
        }
        UpdateModeButtons();
    }

    #region Title screen

    // The title screen runs outside a story run; starting a chapter ends it
    private CancellationTokenSource? m_titleRun;
    private bool m_onTitle;

    /// <summary>
    /// The title screen (TOPMENU.SCN, StoryPlayer.PlayTitleAsync): with <paramref name="opening"/>
    /// the logo and caution screens first; then the title picture with the theme and the four
    /// TITLE.S25 buttons.
    /// </summary>
    private async void ShowTitle(bool opening)
    {
        m_run?.Cancel();
        m_run = null;
        m_titleRun?.Cancel();
        var run = new CancellationTokenSource();
        m_titleRun = run;
        m_player.BeginRun(run.Token);
        HideMenu();
        ResetScreen();
        m_onTitle = true;
        try
        {
            var frames = await m_player.PlayTitleAsync(opening);

            TitleLayer.Children.Clear();
            AddTitleButton(frames, 10, () => Start(null));       // スタート
            AddTitleButton(frames, 20, () => ShowSavePage(saving: false));   // ロード
            AddTitleButton(frames, 30, ShowOption);              // オプション
            AddTitleButton(frames, 80, QuitGame);                // おわる
            TitleLayer.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // A chapter was started, or the window closed
        }
    }

    /// <summary>A TITLE.S25 button: slot = normal picture, slot + 1 = the larger highlighted one.</summary>
    private void AddTitleButton(Dictionary<int, S25Frame> frames, int slot, Action? action)
    {
        if (!frames.TryGetValue(slot, out var normal))
            return;
        var hot = frames.GetValueOrDefault(slot + 1) ?? normal;
        var image = new Image { Stretch = Stretch.Fill };
        void Show(S25Frame f)
        {
            image.Source = f.Image.ToBitmapSource();
            image.Width = f.Width;
            image.Height = f.Height;
            Canvas.SetLeft(image, f.OffsetX);
            Canvas.SetTop(image, f.OffsetY);
        }
        Show(normal);
        if (action != null)
        {
            image.Cursor = Cursors.Hand;
            image.MouseEnter += (_, _) => Show(hot);
            image.MouseLeave += (_, _) => Show(normal);
            image.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                action();
            };
        }
        TitleLayer.Children.Add(image);
    }

    #endregion

    #region Menu

    private bool IsRunning => m_run != null;

    private void ShowMenu()
    {
        // From the title screen the button goes back to it
        BtnResume.Visibility = IsRunning || m_onTitle ? Visibility.Visible : Visibility.Collapsed;
        BtnResume.Content = IsRunning ? "▶ Resume" : "◀ Title";
        BtnStart.Content = IsRunning ? "↺ Start from the beginning" : "▶ Start from the beginning";
        MenuLayer.Visibility = Visibility.Visible;
        m_stage.PauseMovie(true);
    }

    private void HideMenu()
    {
        MenuLayer.Visibility = Visibility.Collapsed;
        m_stage.PauseMovie(false);
        Focus();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e) => Start(null);

    private void BtnResume_Click(object sender, RoutedEventArgs e) => HideMenu();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuLayer.Visibility == Visibility.Visible && IsRunning)
            HideMenu();
        else
            ShowMenu();
    }

    private void ChapterList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChapterList.SelectedItem is StoryChapter chapter)
            Start(chapter.File);
    }

    /// <summary>
    /// Plays the story from a scenario file (null = from the beginning), ending any run in
    /// progress. A loaded save gives the routes played and the message to resume at.
    /// </summary>
    private async void Start(string? file, int played = 0, int message = -1, IReadOnlyDictionary<string, int>? vars = null, bool byLine = false)
    {
        m_run?.Cancel();
        m_titleRun?.Cancel();
        m_titleRun = null;
        m_onTitle = false;
        var run = new CancellationTokenSource();
        m_run = run;
        m_player.BeginRun(run.Token, message, byLine);
        HideMenu();
        ResetScreen();

        try
        {
            // A save of the flow script restarts it with its variables; older saves start at the file
            await m_player.RunAsync(vars == null ? file : null, played, vars);
            // The game goes back to the title screen after the ending
            if (m_run == run)
                ShowTitle(false);
        }
        catch (OperationCanceledException)
        {
            // Another chapter was started, or the window closed
        }
        catch (Exception ex)
        {
            if (m_run == run)
            {
                m_run = null;
                ResetScreen();
                MessageBox.Show(this, $"The story stopped because of an error:\n{ex.Message}", "Play Story",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowTitle(false);
            }
        }
    }

    private void ResetScreen()
    {
        m_stage.Reset();
        m_audio.StopAll();
        HideMessageWindow();
        ChoiceLayer.Visibility = Visibility.Collapsed;
        ChoiceLayer.Children.Clear();
        HideLog();
        m_userHidWindow = false;
        TitleLayer.Visibility = Visibility.Collapsed;
        TitleLayer.Children.Clear();
    }

    #endregion

    #region Input

    /// <summary>Click, Enter, Space: ends animations, shows the whole message, or goes on.</summary>
    private void Advance()
    {
        if (MenuLayer.Visibility == Visibility.Visible || ChoiceLayer.Visibility == Visibility.Visible ||
            TitleLayer.Visibility == Visibility.Visible || SaveLayer.Visibility == Visibility.Visible ||
            OptionLayer.Visibility == Visibility.Visible || DialogOpen)
            return;
        if (LogLayer.Visibility == Visibility.Visible)
        {
            HideLog();
            return;
        }
        if (m_userHidWindow)
        {
            m_userHidWindow = false;
            if (m_messageShown)
                MessageLayer.Visibility = Visibility.Visible;
            return;
        }
        m_player.Click();
    }

    private void Screen_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Advance();

    private void Screen_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Right click answers NO, closes a page, as in the game, or hides the message window
        if (DialogOpen)
            CloseDialog(false);
        else if (LogLayer.Visibility == Visibility.Visible)
            HideLog();
        else if (OptionLayer.Visibility == Visibility.Visible)
            HideOption();
        else if (SaveLayer.Visibility == Visibility.Visible)
            HideSavePage();
        else
            ToggleHideWindow();
    }

    private void ToggleHideWindow()
    {
        if (MenuLayer.Visibility == Visibility.Visible || ChoiceLayer.Visibility == Visibility.Visible || DialogOpen)
            return;
        if (m_userHidWindow)
        {
            Advance();
            return;
        }
        if (!m_messageShown)
            return;
        m_userHidWindow = true;
        MessageLayer.Visibility = Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (DialogOpen)
        {
            // Only the dialog's own answers while it is open
            if (key == Key.Escape)
                CloseDialog(false);
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Alt) != 0:
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.Enter:
            case Key.Space:
            case Key.PageDown:
                if (MenuLayer.Visibility == Visibility.Visible && key == Key.Enter && ChapterList.SelectedItem is StoryChapter chapter)
                    Start(chapter.File);
                else
                    Advance();
                break;
            case Key.LeftCtrl:
            case Key.RightCtrl:
                m_player.SetCtrlHeld(true);
                break;
            case Key.A:
                ToggleAuto();
                break;
            case Key.S:
                ToggleSkip();
                break;
            case Key.H:
            case Key.Delete:
                ToggleHideWindow();
                break;
            case Key.L:
            case Key.PageUp:
                ShowLog();
                break;
            case Key.Escape:
                if (SaveLayer.Visibility == Visibility.Visible)
                    HideSavePage();
                else if (OptionLayer.Visibility == Visibility.Visible)
                    HideOption();
                else if (LogLayer.Visibility == Visibility.Visible)
                    HideLog();
                else if (WindowStyle == WindowStyle.None)
                    ToggleFullScreen();
                else if (MenuLayer.Visibility == Visibility.Visible)
                {
                    if (IsRunning || m_onTitle)
                        HideMenu();
                }
                else
                    ShowMenu();
                break;
            default:
                return;
        }
        // Keep the chapter list's own keyboard navigation
        if (MenuLayer.Visibility != Visibility.Visible || key is Key.Escape or Key.F11 or Key.Enter)
            e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            m_player.SetCtrlHeld(false);
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MenuLayer.Visibility == Visibility.Visible || DialogOpen)
            return;
        if (LogLayer.Visibility == Visibility.Visible)
        {
            // Scrolling down past the newest message closes the backlog
            ScrollLog(e.Delta < 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (OptionLayer.Visibility == Visibility.Visible || SaveLayer.Visibility == Visibility.Visible)
            return;
        if (e.Delta > 0)
            ShowLog();
        else
            Advance();
        e.Handled = true;
    }

    private void ToolBar_MouseEnter(object sender, MouseEventArgs e) => ToolBar.Opacity = 1;

    private void ToolBar_MouseLeave(object sender, MouseEventArgs e) => ToolBar.Opacity = 0;

    // A click on the tool bar is not a click on the story
    private void ToolBar_MouseButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void BtnAuto_Click(object sender, RoutedEventArgs e) => ToggleAuto();

    private void BtnSkip_Click(object sender, RoutedEventArgs e) => ToggleSkip();

    private void BtnLog_Click(object sender, RoutedEventArgs e) => ShowLog();

    private void BtnHide_Click(object sender, RoutedEventArgs e) => ToggleHideWindow();

    private void BtnFullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleAuto() => m_player.ToggleAuto();

    private void ToggleSkip() => m_player.ToggleSkip();

    private void UpdateModeButtons()
    {
        BtnAuto.Tag = m_player.Auto ? "On" : null;
        BtnSkip.Tag = m_player.Skipping ? "On" : null;
        TxtMode.Text = m_player.Skipping ? "SKIP ▶▶" : m_player.Auto ? "AUTO ▶" : "";
        // The AUTO / SKIP buttons of the message window show their state
        BuildBar();
    }

    private WindowState m_restoreState;

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = m_restoreState;
        }
        else
        {
            m_restoreState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Re-enter maximized so the window covers the task bar
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
    }

    #endregion

    #region Message window

    private bool m_messageShown;

    private void ShowMessageWindow()
    {
        m_messageShown = true;
        if (!m_userHidWindow)
            MessageLayer.Visibility = Visibility.Visible;
    }

    private void HideMessageWindow()
    {
        m_messageShown = false;
        MessageLayer.Visibility = Visibility.Collapsed;
        TxtNext.Visibility = Visibility.Collapsed;
    }

    private void SetSpeaker(string? speaker)
    {
        var plate = speaker != null ? m_data.GetNamePlate(speaker) : null;
        if (plate != null)
        {
            ImgNamePlate.Source = plate.Image.ToBitmapSource();
            ImgNamePlate.Width = plate.Width;
            ImgNamePlate.Height = plate.Height;
            Canvas.SetLeft(ImgNamePlate, plate.OffsetX);
            Canvas.SetTop(ImgNamePlate, plate.OffsetY);
            ImgNamePlate.Visibility = Visibility.Visible;
            TxtName.Text = "";
        }
        else
        {
            ImgNamePlate.Visibility = Visibility.Collapsed;
            TxtName.Text = speaker ?? "";
        }
    }

    /// <summary>Shows the first <paramref name="shown"/> characters; the rest keeps its place, invisible.</summary>
    private void SetMessageText(string text, int shown) => TxtMessage.SetText(text, shown);

    #endregion

    #region IStoryView

    void IStoryView.ShowMessageWindow() => ShowMessageWindow();

    void IStoryView.HideMessageWindow() => HideMessageWindow();

    void IStoryView.SetSpeaker(string? speaker) => SetSpeaker(speaker);

    void IStoryView.SetMessageText(string text, int shown) => SetMessageText(text, shown);

    void IStoryView.ShowClickWait(bool visible) => TxtNext.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    void IStoryView.ModesChanged() => UpdateModeButtons();

    Task<int> IStoryView.ChooseAsync(IReadOnlyList<string> options, int imageSlot, int played) => ChooseAsync(options, imageSlot, played);

    #endregion

    #region Choices

    /// <summary>
    /// Shows a choice and waits for the answer, laid out as function 203 of START.SCN does.
    /// With <paramref name="imageSlot"/> the options are the game's picture buttons (SYSTEM.S25
    /// slot + 10 per option; +1 highlighted, +3 already played) in a fixed grid of two columns
    /// of four, 391 x 86 px apart; options whose bit is set in <paramref name="played"/> stay
    /// visible but dimmed and cannot be chosen. Otherwise the options are text on the plain
    /// button (slot 311 / 312), 100 px apart and centred around y = 250. Returns the option's index.
    /// </summary>
    private async Task<int> ChooseAsync(IReadOnlyList<string> options, int imageSlot, int played)
    {
        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChoiceLayer.Children.Clear();
        bool images = imageSlot >= 0 && Enumerable.Range(0, options.Count).All(i => m_data.GetSystemFrame(imageSlot + i * 10) != null);
        var plain = m_data.GetSystemFrame(311);
        var plainHot = m_data.GetSystemFrame(312) ?? plain;

        for (int index = 0; index < options.Count; index++)
        {
            int option = index;
            FrameworkElement element;
            bool enabled = true;
            if (images)
            {
                var normal = m_data.GetSystemFrame(imageSlot + index * 10)!;
                bool done = (played & (1 << index)) != 0;
                if (done)
                {
                    // Played: the "done" picture at 160/255, not selectable
                    var dim = m_data.GetSystemFrame(imageSlot + index * 10 + 3) ?? normal;
                    element = new Image { Source = dim.Image.ToBitmapSource(), Stretch = Stretch.Fill, Opacity = 160 / 255.0, Width = dim.Width, Height = dim.Height };
                    enabled = false;
                }
                else
                {
                    var hot = m_data.GetSystemFrame(imageSlot + index * 10 + 1) ?? normal;
                    element = HoverImage(normal.Image.ToBitmapSource(), hot.Image.ToBitmapSource());
                    element.Width = normal.Width;
                    element.Height = normal.Height;
                }
                Canvas.SetLeft(element, 391 * (index / 4) + normal.OffsetX);
                Canvas.SetTop(element, 86 * (index % 4) + normal.OffsetY);
            }
            else
            {
                double top = 250 - (96 + (options.Count - 1) * 100) / 2 - 48 + index * 100;
                var row = new Canvas { Width = Stage.Width, Height = 96, Background = Brushes.Transparent };
                if ((played & (1 << index)) != 0)
                {
                    // Played (kind 1): slot 314 at 160/255, not selectable
                    var dim = m_data.GetSystemFrame(314) ?? plain;
                    if (dim != null)
                    {
                        var picture = new Image { Source = dim.Image.ToBitmapSource(), Stretch = Stretch.Fill, Width = dim.Width, Height = dim.Height };
                        Canvas.SetLeft(picture, dim.OffsetX);
                        row.Children.Add(picture);
                    }
                    row.Opacity = 160 / 255.0;
                    enabled = false;
                }
                else if (plain != null)
                {
                    var button = HoverImage(plain.Image.ToBitmapSource(), plainHot!.Image.ToBitmapSource());
                    button.Width = plain.Width;
                    button.Height = plain.Height;
                    Canvas.SetLeft(button, plain.OffsetX);
                    row.Children.Add(button);
                }
                // Style bank 2: ＭＳ ゴシック 31 px, black shadow at (1,1), centred between x 240 and 560, y + 32
                var label = new TextBlock
                {
                    Text = options[index], FontSize = 31, Foreground = Brushes.White, IsHitTestVisible = false,
                    FontFamily = new FontFamily("MS Gothic, ＭＳ ゴシック, Yu Gothic"),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 0, ShadowDepth = 1.4, Direction = 315 },
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, 240 + (320 - label.DesiredSize.Width) / 2);
                Canvas.SetTop(label, 32);
                row.Children.Add(label);
                element = row;
                Canvas.SetLeft(element, 0);
                Canvas.SetTop(element, top);
            }

            if (enabled)
            {
                element.Cursor = Cursors.Hand;
                element.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    answer.TrySetResult(option);
                };
            }
            ChoiceLayer.Children.Add(element);
        }

        ChoiceLayer.Visibility = Visibility.Visible;
        try
        {
            return await answer.Task.WaitAsync(m_token);
        }
        finally
        {
            ChoiceLayer.Visibility = Visibility.Collapsed;
            ChoiceLayer.Children.Clear();
        }
    }

    private static Image HoverImage(ImageSource normal, ImageSource hot)
    {
        var image = new Image { Source = normal, Stretch = Stretch.Fill };
        image.MouseEnter += (_, _) => image.Source = hot;
        image.MouseLeave += (_, _) => image.Source = normal;
        return image;
    }

    #endregion

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Focus();
        ApplyConfig();
        BuildBar();
        ShowTitle(opening: true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        m_run?.Cancel();
        m_run = null;
        m_titleRun?.Cancel();
        m_stage.Reset();
        // Keeps the messages read, and closes the sound and the archives
        m_player.Dispose();
        base.OnClosing(e);
    }
}
