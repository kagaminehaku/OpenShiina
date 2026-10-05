// Save and load pages, drawn with the game's own SAVE / LOAD graphics (SYSTEM.S25 slots 2010 /
// 2011, page tabs 2030 + 10 per page, back button 2170): ten slots a page in two columns of five,
// numbered by 2300 + page (the AUTO page: AUTO1-9 and QUICK), the gold frame 2230 on the slot
// under the mouse and NEW (2220) on the slot saved last. Thumbnails follow START.SCN 2CBF4
// (StoryPlayer.SaveThumbnail).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenShiina.Windows;

public partial class PlayerWindow
{
    private SaveStore Saves => m_player.Saves;

    private int m_savePage;
    private bool m_saving;
    private PixelImage? m_pendingThumbnail;

    private bool CanSave => IsRunning && ChoiceLayer.Visibility != Visibility.Visible && m_player.HasPosition;

    /// <summary>Opens the SAVE page (<paramref name="saving"/>) or the LOAD page.</summary>
    private void ShowSavePage(bool saving)
    {
        if (saving && !CanSave)
            return;
        m_saving = saving;
        // The thumbnail shows the screen as it is now, before the page covers it
        if (saving)
            m_pendingThumbnail = m_player.SaveThumbnail();
        m_stage.PauseMovie(true);
        BuildSavePage();
        SaveLayer.Visibility = Visibility.Visible;
    }

    private void HideSavePage()
    {
        SaveLayer.Visibility = Visibility.Collapsed;
        SaveLayer.Children.Clear();
        m_stage.PauseMovie(false);
        Focus();
    }

    private void BuildSavePage()
    {
        SaveLayer.Children.Clear();
        if (m_data.GetSystemFrame(m_saving ? 2010 : 2011) is { } page)
            AddFrame(page);

        // Page tabs: slot 2030 + 10 per page; +2 = the current page, +1 = highlighted
        for (int p = 0; p < SaveStore.Pages; p++)
        {
            int number = p;
            var normal = m_data.GetSystemFrame(2030 + p * 10);
            if (normal == null)
                continue;
            var current = m_data.GetSystemFrame(2032 + p * 10) ?? normal;
            var hot = m_data.GetSystemFrame(2031 + p * 10) ?? normal;
            AddButton(p == m_savePage ? current : normal, p == m_savePage ? current : hot, () =>
            {
                m_savePage = number;
                BuildSavePage();
            });
        }
        if (m_data.GetSystemFrame(2170) is { } back)
            AddButton(back, m_data.GetSystemFrame(2171) ?? back, HideSavePage);
        if (m_data.GetSystemFrame(2300 + m_savePage) is { } numbers)
            AddFrame(numbers);

        // NEW marks the slot saved last from this page (auto and quick saves do not count)
        var saves = Enumerable.Range(0, SaveStore.Pages * SaveStore.SlotsPerPage).Select(Saves.Read).ToList();
        int newest = -1;
        for (int slot = 0; slot < SaveStore.FirstAuto; slot++)
            if (saves[slot] is { } save && (newest < 0 || save.Time > saves[newest]!.Time))
                newest = slot;

        var gold = m_data.GetSystemFrame(2230);
        var marker = m_data.GetSystemFrame(2220);
        for (int k = 0; k < SaveStore.SlotsPerPage; k++)
        {
            int slot = m_savePage * SaveStore.SlotsPerPage + k;
            // Slot k: the hit box 2200 (300 x 88 at 57,57) moved 352 px per column, 101 px per row
            var data = saves[slot];
            var cell = new Canvas { Width = 300, Height = 88, Background = Brushes.Transparent };
            Canvas.SetLeft(cell, 57 + 352 * (k / 5));
            Canvas.SetTop(cell, 57 + 101 * (k % 5));
            if (data != null && Saves.Thumbnail(slot) is { } file && Bitmaps.Decode(file) is { } thumbnail)
            {
                var image = new Image { Source = thumbnail, Width = 100, Height = 75, Stretch = Stretch.Fill };
                Canvas.SetLeft(image, 8);
                Canvas.SetTop(image, 7);
                cell.Children.Add(image);
            }
            if (data != null)
            {
                cell.Children.Add(SlotText(data.Time.ToString("yyyy/MM/dd/HH:mm"), 138, 13, 15));
                cell.Children.Add(SlotText(SaveStore.Caption(data.Text), 138, 43, 14));
            }
            if (slot == newest && marker != null)
                cell.Children.Add(SlotPicture(marker));
            if (m_saving || data != null)
            {
                cell.Cursor = Cursors.Hand;
                var frame = gold == null ? null : SlotPicture(gold);
                cell.MouseEnter += (_, _) =>
                {
                    if (frame != null && !cell.Children.Contains(frame))
                        cell.Children.Add(frame);
                };
                cell.MouseLeave += (_, _) => cell.Children.Remove(frame);
                cell.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SlotClicked(slot, data);
                };
            }
            SaveLayer.Children.Add(cell);
        }
    }

    /// <summary>A SYSTEM.S25 picture placed for slot 0 (hit box at 57,57), put in a slot cell.</summary>
    private static Image SlotPicture(S25Frame frame)
    {
        var image = new Image { Source = frame.Image.ToBitmapSource(), Width = frame.Width, Height = frame.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        Canvas.SetLeft(image, frame.OffsetX - 57);
        Canvas.SetTop(image, frame.OffsetY - 57);
        return image;
    }

    private static TextBlock SlotText(string text, double x, double y, double size)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, Foreground = Brushes.White, IsHitTestVisible = false,
            FontFamily = new FontFamily("MS Gothic, ＭＳ ゴシック, Yu Gothic"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x80, 0x20, 0x40), BlurRadius = 0, ShadowDepth = 1 },
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        return block;
    }

    private void SlotClicked(int slot, SaveData? data)
    {
        if (m_saving)
        {
            var save = m_player.CurrentSave();
            try
            {
                Saves.Write(slot, save, m_pendingThumbnail ?? m_player.SaveThumbnail());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save:\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            BuildSavePage();
            return;
        }
        if (data == null)
            return;
        HideSavePage();
        Load(data);
    }

    private void AddFrame(S25Frame frame)
    {
        var image = new Image { Source = frame.Image.ToBitmapSource(), Width = frame.Width, Height = frame.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(image, frame.OffsetX);
        Canvas.SetTop(image, frame.OffsetY);
        SaveLayer.Children.Add(image);
    }

    private void AddButton(S25Frame normal, S25Frame hot, Action action)
    {
        var image = new Image { Stretch = Stretch.Fill, Cursor = Cursors.Hand };
        void Show(S25Frame f)
        {
            image.Source = f.Image.ToBitmapSource();
            image.Width = f.Width;
            image.Height = f.Height;
            Canvas.SetLeft(image, f.OffsetX);
            Canvas.SetTop(image, f.OffsetY);
        }
        Show(normal);
        image.MouseEnter += (_, _) => Show(hot);
        image.MouseLeave += (_, _) => Show(normal);
        image.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        SaveLayer.Children.Add(image);
    }

    private void Load(SaveData data) => Start(data.File, data.Played, data.Line >= 0 ? data.Line : data.Message, data.Vars, data.Line >= 0);

    private void BtnSave_Click(object sender, RoutedEventArgs e) => ShowSavePage(saving: true);

    private void BtnLoad_Click(object sender, RoutedEventArgs e) => ShowSavePage(saving: false);
}
