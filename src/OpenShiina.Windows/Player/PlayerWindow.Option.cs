// The game's in-story interface, drawn with SYSTEM.S25: the button bar of the message window
// (slots 80-190), the OPTION page (1000) and the backlog page (900). The settings they change
// and the backlog itself are kept by StoryPlayer.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OpenShiina.Windows;

public partial class PlayerWindow
{
    private PlayerConfig Config => m_player.Config;

    private void SaveConfig() => m_player.SaveConfig();

    /// <summary>Volumes and screen mode from the settings (the text speed is read as each message is shown).</summary>
    private void ApplyConfig()
    {
        m_player.ApplyAudioConfig();
        if (Config.FullScreen != (WindowStyle == WindowStyle.None))
            ToggleFullScreen();
    }

    /// <summary>
    /// A SYSTEM.S25 button on <paramref name="layer"/>: slot = normal, slot + 1 = highlighted,
    /// slot + 2 = selected (shown instead of the normal picture when <paramref name="selected"/>).
    /// </summary>
    private Image? SystemButton(Canvas layer, int slot, Action? action, bool selected = false)
    {
        var normal = m_data.GetSystemFrame(selected ? slot + 2 : slot) ?? m_data.GetSystemFrame(slot);
        if (normal == null)
            return null;
        var hot = m_data.GetSystemFrame(slot + 1) ?? normal;
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
            image.MouseRightButtonUp += (_, e) => e.Handled = true;
        }
        layer.Children.Add(image);
        return image;
    }

    private void AddSystemPicture(Canvas layer, int slot)
    {
        if (m_data.GetSystemFrame(slot) is not { } f)
            return;
        var image = new Image { Source = f.Image.ToBitmapSource(), Width = f.Width, Height = f.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        Canvas.SetLeft(image, f.OffsetX);
        Canvas.SetTop(image, f.OffsetY);
        layer.Children.Add(image);
    }

    #region Button bar

    /// <summary>The message window's buttons: QSAVE QLOAD AUTO SAVE LOAD SKIP OPTION TITLE QUIT ×.</summary>
    private void BuildBar()
    {
        BarLayer.Children.Clear();
        SystemButton(BarLayer, 120, QuickSave);
        SystemButton(BarLayer, 130, QuickLoad);
        SystemButton(BarLayer, 100, ToggleAuto, m_player.Auto);
        SystemButton(BarLayer, 80, () => ShowSavePage(saving: true));
        SystemButton(BarLayer, 90, () => ShowSavePage(saving: false));
        SystemButton(BarLayer, 150, ToggleSkip, m_player.Skip);
        SystemButton(BarLayer, 140, ShowOption);
        SystemButton(BarLayer, 110, BackToTitle);
        SystemButton(BarLayer, 190, QuitGame);
        SystemButton(BarLayer, 180, ToggleHideWindow);
    }

    // Quick save: QUICK, the last slot of the AUTO page
    private const int QuickSlot = SaveStore.QuickSlot;

    private void QuickSave()
    {
        if (!CanSave)
            return;
        try
        {
            Saves.Write(QuickSlot, m_player.CurrentSave(), m_player.SaveThumbnail());
            ShowNotice("QUICK SAVE");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save:\n{ex.Message}", "Quick save", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void QuickLoad()
    {
        if (Saves.Read(QuickSlot) is not { } data)
            ShowNotice("NO QUICK SAVE");
        else
            Load(data);
    }

    /// <summary>Shows a short notice where the AUTO / SKIP mode is shown, then puts the mode back.</summary>
    private async void ShowNotice(string text)
    {
        TxtMode.Text = text;
        await Task.Delay(1500);
        UpdateModeButtons();
    }

    #endregion

    #region OPTION page

    private void ShowOption()
    {
        m_stage.PauseMovie(true);
        BuildOption();
        OptionLayer.Visibility = Visibility.Visible;
    }

    private void HideOption()
    {
        OptionLayer.Visibility = Visibility.Collapsed;
        OptionLayer.Children.Clear();
        m_stage.PauseMovie(false);
        Focus();
    }

    /// <summary>
    /// SYSTEM.S25 1000 with its controls: volume sliders (knob 1600 / 1400 / 1500, arrows +10 /
    /// +20, ON / OFF 1820 / 1800 / 1810 and +3), screen mode (1940 full, 1930 window), message
    /// speed (1130-1100, 高速 to 遅い), auto wait (1230-1200), message skip (1300 既読, 1310 未読),
    /// TITLE 1050 and BACK 1060.
    /// </summary>
    private void BuildOption()
    {
        var c = Config;
        OptionLayer.Children.Clear();
        AddSystemPicture(OptionLayer, 1000);

        void Slider(int knob, int toggle, Func<double> get, Action<double> set, Func<bool> on, Action<bool> setOn)
        {
            var frame = m_data.GetSystemFrame(knob);
            if (frame == null)
                return;
            const double Range = 197;
            void Change(double v)
            {
                set(Math.Clamp(v, 0, 1));
                SaveConfig();
                ApplyConfig();
                BuildOption();
            }
            // The track: clicking or dragging sets the value
            var track = new Rectangle { Width = Range + frame.Width, Height = frame.Height + 8, Fill = Brushes.Transparent, Cursor = Cursors.Hand };
            Canvas.SetLeft(track, frame.OffsetX);
            Canvas.SetTop(track, frame.OffsetY - 4);
            double ValueAt(MouseEventArgs e) => (e.GetPosition(OptionLayer).X - frame.OffsetX - frame.Width / 2.0) / Range;
            track.MouseLeftButtonDown += (_, e) => { track.CaptureMouse(); e.Handled = true; };
            track.MouseLeftButtonUp += (_, e) => { track.ReleaseMouseCapture(); e.Handled = true; Change(ValueAt(e)); };
            OptionLayer.Children.Add(track);
            SystemButton(OptionLayer, knob + 10, () => Change(get() - 1 / 49.0));
            SystemButton(OptionLayer, knob + 20, () => Change(get() + 1 / 49.0));
            var image = new Image { Source = frame.Image.ToBitmapSource(), Width = frame.Width, Height = frame.Height, IsHitTestVisible = false };
            Canvas.SetLeft(image, frame.OffsetX + Range * get());
            Canvas.SetTop(image, frame.OffsetY);
            OptionLayer.Children.Add(image);
            SystemButton(OptionLayer, toggle, () => { setOn(true); SaveConfig(); ApplyConfig(); BuildOption(); }, on());
            SystemButton(OptionLayer, toggle + 3, () => { setOn(false); SaveConfig(); ApplyConfig(); BuildOption(); }, !on());
        }
        Slider(1600, 1820, () => c.MusicVolume, v => c.MusicVolume = v, () => c.MusicOn, v => c.MusicOn = v);
        Slider(1400, 1800, () => c.VoiceVolume, v => c.VoiceVolume = v, () => c.VoiceOn, v => c.VoiceOn = v);
        Slider(1500, 1810, () => c.EffectVolume, v => c.EffectVolume = v, () => c.EffectOn, v => c.EffectOn = v);

        void Choice(int slot, bool selected, Action set) => SystemButton(OptionLayer, slot, () =>
        {
            set();
            SaveConfig();
            ApplyConfig();
            BuildOption();
        }, selected);
        Choice(1940, c.FullScreen, () => c.FullScreen = true);
        Choice(1930, !c.FullScreen, () => c.FullScreen = false);
        for (int level = 0; level < 4; level++)
        {
            int value = level;
            Choice(1130 - 10 * level, c.MessageSpeed == level, () => c.MessageSpeed = value);
            Choice(1230 - 10 * level, c.AutoWait == level, () => c.AutoWait = value);
        }
        Choice(1300, !c.SkipUnread, () => c.SkipUnread = false);
        Choice(1310, c.SkipUnread, () => c.SkipUnread = true);

        if (IsRunning)
            SystemButton(OptionLayer, 1050, BackToTitle);
        SystemButton(OptionLayer, 1060, HideOption);
    }

    #endregion

    #region Backlog page

    // First message shown on the backlog page (index into the player's log)
    private int m_logTop;

    private IReadOnlyList<LogEntry> m_log => m_player.Log;

    private const double LogLeft = 126, LogTextTop = 40, LogBottom = 560;

    private void ShowLog()
    {
        if (MenuLayer.Visibility == Visibility.Visible || ChoiceLayer.Visibility == Visibility.Visible || m_log.Count == 0 || !IsRunning)
            return;
        m_logTop = LastLogTop();
        BuildLog();
        LogLayer.Visibility = Visibility.Visible;
    }

    private void HideLog()
    {
        LogLayer.Visibility = Visibility.Collapsed;
        LogLayer.Children.Clear();
    }

    private static double LogHeight(LogEntry entry) =>
        (entry.Speaker.Length > 0 ? MessageText.LineHeight : 0) +
        MessageLayout.LineCount(entry.Text, MessageText.FullAdvance * 25) * MessageText.LineHeight + 12;

    /// <summary>The first message to show so that the newest one ends the page.</summary>
    private int LastLogTop()
    {
        double height = 0;
        int top = m_log.Count;
        while (top > 0 && height + LogHeight(m_log[top - 1]) <= LogBottom - LogTextTop)
            height += LogHeight(m_log[--top]);
        return Math.Min(top, m_log.Count - 1);
    }

    private void ScrollLog(int delta)
    {
        int last = LastLogTop();
        if (delta > 0 && m_logTop >= last)
        {
            HideLog();      // scrolling past the newest message closes the page
            return;
        }
        m_logTop = Math.Clamp(m_logTop + delta, 0, last);
        BuildLog();
    }

    /// <summary>SYSTEM.S25 900 with the TOP / END bar (knob 901, arrows 950 / 960) and the back button 910.</summary>
    private void BuildLog()
    {
        LogLayer.Children.Clear();
        AddSystemPicture(LogLayer, 900);
        SystemButton(LogLayer, 910, HideLog);
        SystemButton(LogLayer, 950, () => ScrollLog(-1));
        SystemButton(LogLayer, 960, () => ScrollLog(1));
        if (m_data.GetSystemFrame(901) is { } knob)
        {
            int last = Math.Max(1, LastLogTop());
            double y = knob.OffsetY + (494 - 79 - knob.OffsetY) * Math.Min(1.0, m_logTop / (double)last);
            var image = new Image { Source = knob.Image.ToBitmapSource(), Width = knob.Width, Height = knob.Height, IsHitTestVisible = false };
            Canvas.SetLeft(image, knob.OffsetX);
            Canvas.SetTop(image, y);
            LogLayer.Children.Add(image);
        }

        double top = LogTextTop;
        for (int i = m_logTop; i < m_log.Count; i++)
        {
            var entry = m_log[i];
            double height = LogHeight(entry);
            if (top + height > LogBottom && i > m_logTop)
                break;
            var block = new Canvas { Width = 600, Height = height, Background = Brushes.Transparent };
            Canvas.SetLeft(block, LogLeft);
            Canvas.SetTop(block, top);
            double y = 0;
            if (entry.Speaker.Length > 0)
            {
                var name = new MessageText { Width = 575, Height = MessageText.LineHeight, Foreground = new SolidColorBrush(Color.FromRgb(255, 240, 0)) };
                name.SetText($"【{entry.Speaker}】", entry.Speaker.Length + 2);
                block.Children.Add(name);
                y = MessageText.LineHeight;
            }
            var text = new MessageText { Width = MessageText.FullAdvance * 25, Height = height - y };
            text.Gaiji = TxtMessage.Gaiji;
            text.SetText(entry.Text, entry.Text.Length);
            Canvas.SetTop(text, y);
            block.Children.Add(text);
            // A message with a voice plays it again when clicked
            if (entry.Voice is { } voice)
            {
                block.Cursor = Cursors.Hand;
                block.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (m_data.ReadAudio(voice) is { } audio)
                        m_audio.Play(AudioEngine.Voice, audio, 1);
                };
            }
            LogLayer.Children.Add(block);
            top += height;
        }
    }

    #endregion
}
