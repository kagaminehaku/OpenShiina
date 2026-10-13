// The home screen. On the left a rail: the logo, the library, "Add a game…" (the host asks for a
// folder) and the settings (SettingsView.cs, in the page's place). Then the games of the library
// (Game/GameLibrary.cs) as tiles to choose from, the one played last first, each with its .exe's
// icon on its own colour (GameIcons.cs); and, on the right, the chosen game's card: its icon and
// name, when it was played last, for how long, the size of its folder and saves, and Play in the
// corner under the right thumb. A tap anywhere on the card plays; a tile chooses (a double tap
// plays). The card's "…" opens the save folder (and the game's on a desktop) or removes the game:
// from the list only (with Undo), or its folder too, after a dialog that says what goes. A game
// whose folder is gone shows faded and cannot be played. Colours come from the theme (PlayerTheme.cs).

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using OpenShiina.Game;
using OpenShiina.Localization;

namespace OpenShiina.App;

public sealed class LibraryView : UserControl
{
    private readonly Grid m_home;
    private readonly ContentControl m_page = new();
    private readonly WrapPanel m_tiles = new() { Orientation = Orientation.Horizontal };
    private readonly Border m_detail = new();
    private readonly ColumnDefinition m_detailColumn = new(380, GridUnitType.Pixel);
    private readonly ColumnDefinition m_railColumn = new(64, GridUnitType.Pixel);
    private readonly Control m_wordmark;
    private readonly Button m_railLibrary, m_railSettings;
    private readonly TextBlock m_empty = new()
    {
        Text = Strings.NoGamesYet,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 16),
    };
    private readonly TextBlock m_message = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // A question under the card (ChooseAsync): its text and a button an answer, then Cancel
    private readonly StackPanel m_choice = new() { Spacing = 6, IsVisible = false };
    private readonly Panel m_overlay = new() { IsVisible = false };
    private readonly Border m_toast = new() { IsVisible = false };
    private DispatcherTimer? m_toastTimer;

    private List<LibraryGame> m_games = [];
    private string? m_chosen;
    private readonly Dictionary<string, Button> m_tileOf = new(StringComparer.Ordinal);
    // The sizes measured this run: the game's folder and its saves
    private readonly Dictionary<string, (long Game, long Saves)> m_sizes = new(StringComparer.Ordinal);
    private bool m_compact = PlayerPlatform.Touch;

    /// <summary>A game was chosen: its folder.</summary>
    public event Action<string>? Play;

    /// <summary>"Add a game…": the host asks for a folder and opens it.</summary>
    public event Action? AddRequested;

    /// <summary>The language was changed in the settings: the host makes the screens again in it.</summary>
    public event Action? LanguageChanged;

    public LibraryView()
    {
        this.Use(BackgroundProperty, PlayerTheme.Keys.Background);
        Styles.AddRange(PlayerTheme.CardStyles());
        m_empty.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);
        m_message.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);

        m_railLibrary = RailButton(Icons.Library, Strings.Library, PlayerTheme.Keys.Ink);
        m_railLibrary.Click += (_, _) => ShowHome();
        var add = RailButton(Icons.Add, Strings.AddGame, PlayerTheme.Keys.Accent);
        add.Click += (_, _) => AddRequested?.Invoke();
        m_railSettings = RailButton(Icons.Settings, Strings.Settings, PlayerTheme.Keys.Ink);
        m_railSettings.Click += (_, _) => OpenSettings(SettingsPages.Appearance);
        var rail = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel
            {
                Spacing = 6, Margin = new Thickness(0, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Image { Source = LogoBitmap(), Width = 36, Height = 36, Margin = new Thickness(0, 0, 0, 14) },
                    m_railLibrary, add, m_railSettings,
                },
            },
        }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Surface).Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border);

        var wordmark = PlayerTheme.Wordmark(26);
        wordmark.Margin = new Thickness(8, 0, 0, 10);
        m_wordmark = wordmark;
        var list = new StackPanel { Children = { m_tiles, m_empty } };
        var tiles = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var messages = new ScrollViewer
        {
            MaxHeight = 220,
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { m_message, m_choice } },
        };
        var detail = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Children = { m_detail, messages } };
        Grid.SetRow(messages, 1);
        // The wordmark over both columns, so that the tiles and the card start on one line
        m_home = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), m_detailColumn },
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { m_wordmark, At(tiles, 0, 1), At(detail, 1, 1) },
        };
        Grid.SetColumnSpan(m_wordmark, 2);
        m_page.Content = m_home;
        var layout = new Grid { ColumnDefinitions = { m_railColumn, new ColumnDefinition(GridLength.Star) }, Children = { rail, m_page } };
        Grid.SetColumn(m_page, 1);
        Content = new Grid { Children = { layout, m_toast, m_overlay } };
        SizeChanged += (_, e) => Arrange(e.NewSize);
        Chosen(m_railLibrary, true);
        Refresh();
    }

    // ---- The layout

    /// <summary>The columns and tiles for the view's size: a phone held sideways has a narrower rail, two columns of tiles and a wider card.</summary>
    private void Arrange(Size size)
    {
        bool compact = PlayerPlatform.Touch || size.Width < 720 || size.Height < 480;
        double rail = compact ? 60 : 68;
        // (a view not laid out yet: Android's first pass can be empty)
        if (size.Width < rail + 200 || size.Height < 100)
            return;
        m_railColumn.Width = new GridLength(rail);
        double room = size.Width - rail;
        double detail = compact ? Math.Round(room * 0.52) : Math.Clamp(Math.Round(room * 0.4), 300, 520);
        m_detailColumn.Width = new GridLength(detail);
        double gap = compact ? 12 : 20;
        m_home.Margin = new Thickness(gap);
        // (level with the tiles' tops: they stand 5 inside their places)
        m_detail.Margin = new Thickness(gap / 2, 5, 0, 0);
        m_wordmark.IsVisible = !compact;
        // The tiles: two columns on a phone, as many as fit at about 150 wide on a desktop
        double list = room - detail - 2 * gap - 12;
        int columns = compact ? 2 : Math.Max(2, (int)(list / 150));
        double width = Math.Max(80, Math.Floor(list / columns));
        m_tiles.ItemWidth = width;
        m_tiles.ItemHeight = compact ? Math.Clamp(Math.Floor((size.Height - 2 * gap) / 2), 112, 170) : Math.Round(width * 0.9);
        if (compact != m_compact)
        {
            m_compact = compact;
            Choose(m_games.FirstOrDefault(g => g.Folder == m_chosen));
        }
    }

    /// <summary>The library again (after a game, a change of theme), the same game chosen when it is still there.</summary>
    public void Refresh()
    {
        m_games = GameLibrary.Load();
        m_tiles.Children.Clear();
        m_tileOf.Clear();
        foreach (var game in m_games)
        {
            var tile = Tile(game);
            m_tileOf[game.Folder] = tile;
            m_tiles.Children.Add(tile);
        }
        m_empty.IsVisible = m_games.Count == 0;
        Choose(m_games.FirstOrDefault(g => g.Folder == m_chosen) ?? m_games.FirstOrDefault());
        StartupLog.Mark($"the library read: {m_games.Count} games");
    }

    private void Choose(LibraryGame? game)
    {
        m_chosen = game?.Folder;
        foreach (var (folder, tile) in m_tileOf)
            tile.Classes.Set("chosen", folder == m_chosen);
        m_detail.Child = game == null ? Welcome() : Card(game);
        if (game != null)
            _ = MeasureAsync(game);
    }

    // ---- The tiles

    private Button Tile(LibraryGame game)
    {
        var face = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(8), Background = PlayerTheme.Tint(null, 0) };
        var icon = IconBox(game, 56, colour => face.Background = PlayerTheme.Tint(colour, 0.22));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var name = new TextBlock
        {
            Text = game.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        face.Child = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { icon, name } };
        var tile = new Button
        {
            Margin = new Thickness(5), Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Opacity = game.Available ? 1 : 0.5,
            Content = face,
        };
        tile.Classes.Add("card");
        AutomationProperties.SetName(tile, game.Name);
        ToolTip.SetTip(tile, game.Folder);
        tile.Click += (_, _) => Choose(game);
        tile.DoubleTapped += (_, _) => PlayGame(game);
        return tile;
    }

    // ---- The card

    private Control Card(LibraryGame game)
    {
        bool available = game.Available;
        var face = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(m_compact ? 16 : 20), Background = PlayerTheme.Tint(null, 0) };
        double iconSize = m_compact ? 72 : 96;
        var icon = IconBox(game, iconSize, colour => face.Background = PlayerTheme.Tint(colour, 0.32));

        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) };
        if (game.LastPlayed != null && m_games.FirstOrDefault()?.Folder == game.Folder)
            heading.Children.Add(new TextBlock { Text = Strings.Continue, FontSize = 13, FontWeight = FontWeight.SemiBold }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Highlight));
        heading.Children.Add(new TextBlock
        {
            Text = game.Name, FontSize = m_compact ? 19 : 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis,
        }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink));

        var more = new Button
        {
            Content = "…", FontSize = 18, Width = 44, Height = 40, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(more, Strings.More);
        var menu = Menu(game);
        more.Click += (_, e) =>
        {
            menu.ShowAt(more);
            e.Handled = true;
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { icon, At(heading, 1), At(more, 2) } };

        var facts = new StackPanel { Spacing = 4, Margin = new Thickness(0, 14, 0, 0) };
        facts.Children.Add(Fact(!available ? Strings.FolderGone : game.LastPlayed is { } played ? string.Format(Strings.PlayedWhen, Ago(played)) : Strings.NotPlayedYet));
        if (game.PlaySeconds > 0)
            facts.Children.Add(Fact(game.PlaySeconds < 60 ? Strings.PlayTimeUnderMinute : string.Format(Strings.PlayTime, Duration(game.PlaySeconds))));
        var size = Fact(SizeText(game));
        size.IsVisible = size.Text!.Length > 0;
        facts.Children.Add(size);
        if (!PlayerPlatform.Touch)
            facts.Children.Add(Fact(game.Folder, trim: true));

        var play = new Border
        {
            CornerRadius = new CornerRadius(24), Padding = new Thickness(20, 10, 24, 10),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 14, 0, 0), IsVisible = available,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new Path { Data = Icons.Play, Width = 14, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center }
                        .Use(Shape.FillProperty, PlayerTheme.Keys.OnHighlight),
                    new TextBlock { Text = Strings.Play, FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }
                        .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.OnHighlight),
                },
            },
        }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Highlight);

        face.Child = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Children = { top, At(facts, 0, 1), At(play, 0, 2) } };
        // (the size, filled in once measured)
        face.Tag = size;
        var card = new Button
        {
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = m_compact ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            MinHeight = m_compact ? 0 : 280,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Opacity = available ? 1 : 0.7,
            Content = face,
        };
        card.Classes.Add("card");
        card.Classes.Add("chosen");
        AutomationProperties.SetName(card, string.Format(Strings.PlayGame, game.Name));
        card.ContextFlyout = menu;
        card.Click += (_, _) => PlayGame(game);
        return card;
    }

    /// <summary>No games yet: what to do, and the button to do it.</summary>
    private Control Welcome()
    {
        var add = new Button { Content = Strings.AddGame, HorizontalAlignment = HorizontalAlignment.Left };
        add.Classes.Add("accent");
        add.Click += (_, _) => AddRequested?.Invoke();
        return new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(20), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    PlayerTheme.Wordmark(24),
                    new TextBlock { Text = Strings.WelcomeText, TextWrapping = TextWrapping.Wrap }
                        .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted),
                    add,
                },
            },
        }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Surface).Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border);
    }

    private void PlayGame(LibraryGame game)
    {
        if (game.Available)
            Play?.Invoke(game.Folder);
        else
            Message = string.Format(Strings.FolderGoneMessage, game.Name, game.Folder);
    }

    private MenuFlyout Menu(LibraryGame game)
    {
        var menu = new MenuFlyout();
        var saves = new MenuItem { Header = Strings.OpenSaveFolder };
        saves.Click += async (_, _) => await OpenFolderAsync(game.SaveFolder);
        menu.Items.Add(saves);
        if (!PlayerPlatform.Touch)
        {
            var folder = new MenuItem { Header = Strings.OpenGameFolder, IsEnabled = game.Available };
            folder.Click += async (_, _) => await OpenFolderAsync(game.Folder);
            menu.Items.Add(folder);
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = Strings.RemoveMenu }.Use(MenuItem.ForegroundProperty, PlayerTheme.Keys.Accent);
        remove.Click += (_, _) => ShowRemove(game);
        menu.Items.Add(remove);
        return menu;
    }

    // ---- Sizes

    /// <summary>Measures the game's folder and saves on a pool thread; the card shows them, the library keeps the folder's.</summary>
    private async Task MeasureAsync(LibraryGame game)
    {
        var sizes = await Task.Run(() =>
        {
            long folder = game.Available ? GameLibrary.FolderBytes(game.Folder) : 0;
            long saves = GameLibrary.FolderBytes(game.SaveFolder);
            if (game.Available && folder != game.Bytes)
                GameLibrary.SetBytes(game.Folder, folder);
            return (folder, saves);
        });
        m_sizes[game.Folder] = sizes;
        if (m_chosen == game.Folder && m_detail.Child is Button { Content: Border { Tag: TextBlock size } })
        {
            size.Text = SizeText(game);
            size.IsVisible = size.Text.Length > 0;
        }
    }

    private string SizeText(LibraryGame game)
    {
        (long Game, long Saves)? known = m_sizes.TryGetValue(game.Folder, out var sizes) ? sizes : null;
        if (!game.Available)
            return known is { } k && k.Saves > 0 ? string.Format(Strings.SavesOnly, Bytes(k.Saves)) : "";
        long? folder = known?.Game ?? game.Bytes;
        if (folder == null)
            return Strings.Measuring;
        return known is { } m ? $"{Bytes(folder.Value)} · {Saves(m.Saves)}" : Bytes(folder.Value);
    }

    // ---- Removing

    /// <summary>
    /// "Remove…": from the list only (the files and saves stay; Undo for a while after), or the
    /// game's folder deleted too, with its saves when asked; the button says what goes.
    /// </summary>
    private void ShowRemove(LibraryGame game)
    {
        var sizes = m_sizes.TryGetValue(game.Folder, out var s) ? s : (Game: game.Bytes ?? 0, Saves: 0L);
        var listOnly = new RadioButton { Content = Strings.RemoveListOnly, GroupName = "remove", IsChecked = true };
        var delete = new RadioButton
        {
            Content = sizes.Game > 0 ? string.Format(Strings.RemoveDeleteFrees, Bytes(sizes.Game)) : Strings.RemoveDelete,
            GroupName = "remove", IsVisible = game.Available,
        };
        var path = new TextBlock { Text = game.Folder, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(28, 0, 0, 0), IsVisible = false }
            .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);
        var saves = new CheckBox
        {
            Content = sizes.Saves > 0 ? string.Format(Strings.RemoveSavesSize, Bytes(sizes.Saves)) : Strings.RemoveSaves,
            Margin = new Thickness(28, 0, 0, 0), IsVisible = false,
        };
        var cancel = new Button { Content = Strings.Cancel };
        var go = new Button { Content = Strings.Remove };
        go.Classes.Add("accent");
        void Update()
        {
            bool deleting = delete.IsChecked == true;
            path.IsVisible = saves.IsVisible = deleting;
            go.Content = !deleting ? Strings.Remove : sizes.Game > 0 ? string.Format(Strings.DeleteSize, Bytes(sizes.Game)) : Strings.Delete;
        }
        listOnly.IsCheckedChanged += (_, _) => Update();
        delete.IsCheckedChanged += (_, _) => Update();
        cancel.Click += (_, _) => CloseOverlay();
        go.Click += async (_, _) =>
        {
            bool deleting = delete.IsChecked == true, withSaves = saves.IsChecked == true;
            CloseOverlay();
            if (!deleting)
            {
                GameLibrary.Remove(game.Folder);
                Refresh();
                Toast(string.Format(Strings.Removed, game.Name), () =>
                {
                    GameLibrary.Restore(game);
                    m_chosen = game.Folder;
                    Refresh();
                });
                return;
            }
            Message = string.Format(Strings.Deleting, game.Name);
            string? error = await Task.Run(() => GameLibrary.Delete(game, withSaves));
            Message = error ?? "";
            Refresh();
            if (error == null)
                Toast(sizes.Game > 0 ? string.Format(Strings.DeletedFreed, game.Name, Bytes(sizes.Game)) : string.Format(Strings.Deleted, game.Name), null);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { cancel, go } };
        var dialog = new Border
        {
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(20),
            MaxWidth = 520, Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = string.Format(Strings.RemoveTitle, game.Name), FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }
                        .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink),
                    listOnly, delete, path, saves, buttons,
                },
            },
        }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Surface).Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border);
        ShowOverlay(dialog);
    }

    private void ShowOverlay(Control dialog)
    {
        // The page dimmed behind it; a tap there cancels
        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
        shade.PointerPressed += (_, _) => CloseOverlay();
        m_overlay.Children.Clear();
        m_overlay.Children.Add(shade);
        m_overlay.Children.Add(new ScrollViewer { Content = dialog, VerticalAlignment = VerticalAlignment.Center });
        m_overlay.IsVisible = true;
    }

    private void CloseOverlay()
    {
        m_overlay.IsVisible = false;
        m_overlay.Children.Clear();
    }

    /// <summary>A line at the bottom for a few seconds, with Undo when <paramref name="undo"/> is given.</summary>
    private void Toast(string text, Action? undo)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16,
            Children = { new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink) },
        };
        if (undo != null)
        {
            var button = new Button { Content = Strings.Undo, Background = Brushes.Transparent }.Use(ForegroundProperty, PlayerTheme.Keys.Highlight);
            button.Click += (_, _) =>
            {
                m_toast.IsVisible = false;
                undo();
            };
            row.Children.Add(button);
        }
        m_toast.Child = row;
        m_toast.CornerRadius = new CornerRadius(12);
        m_toast.BorderThickness = new Thickness(1);
        m_toast.Padding = new Thickness(16, 8);
        m_toast.Margin = new Thickness(16);
        m_toast.HorizontalAlignment = HorizontalAlignment.Center;
        m_toast.VerticalAlignment = VerticalAlignment.Bottom;
        m_toast.Use(Border.BackgroundProperty, PlayerTheme.Keys.SurfaceHigh).Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border);
        m_toast.IsVisible = true;
        m_toastTimer?.Stop();
        m_toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        m_toastTimer.Tick += (_, _) =>
        {
            m_toastTimer?.Stop();
            m_toast.IsVisible = false;
        };
        m_toastTimer.Start();
    }

    // ---- The settings, in the page's place

    /// <summary>Shows the settings in the page's place, at <paramref name="page"/>.</summary>
    public void OpenSettings(SettingsPages page)
    {
        if (m_page.Content is SettingsView)
            return;
        var settings = new SettingsView();
        settings.Closed += ShowHome;
        settings.LanguageChanged += () => LanguageChanged?.Invoke();
        settings.Show(page);
        m_page.Content = settings;
        Chosen(m_railLibrary, false);
        Chosen(m_railSettings, true);
        settings.Focusable = true;
        settings.Focus();
    }

    private void ShowHome()
    {
        if (m_page.Content == m_home)
            return;
        m_page.Content = m_home;
        Chosen(m_railLibrary, true);
        Chosen(m_railSettings, false);
        // the theme may have changed: the cards' colours are made again
        Refresh();
        Focus();
    }

    /// <summary>Back from the settings to the library (a phone's back); false when they are not open.</summary>
    public bool CloseSettings()
    {
        if (m_page.Content is not SettingsView settings)
            return false;
        settings.Close();
        return true;
    }

    // ---- Messages

    /// <summary>
    /// Asks the user to pick one of <paramref name="answers"/> under the card; the answer, or
    /// null for Cancel (or when another question replaces this one).
    /// </summary>
    public Task<string?> ChooseAsync(string question, IReadOnlyList<string> answers)
    {
        ShowHome();
        var done = new TaskCompletionSource<string?>();
        void Answer(string? answer)
        {
            m_choice.IsVisible = false;
            m_choice.Children.Clear();
            done.TrySetResult(answer);
        }
        m_choice.Children.Clear();
        m_choice.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink));
        foreach (var answer in answers)
        {
            var button = new Button { Content = answer, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => Answer(answer);
            m_choice.Children.Add(button);
        }
        var cancel = new Button { Content = Strings.Cancel, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        cancel.Click += (_, _) => Answer(null);
        m_choice.Children.Add(cancel);
        m_choice.IsVisible = true;
        Message = "";
        return done.Task;
    }

    /// <summary>A line under the card (opening, an error); empty to clear it.</summary>
    public string Message
    {
        get => m_message.Text ?? "";
        set
        {
            m_message.Text = value;
            m_message.IsVisible = value.Length > 0;
        }
    }

    // ---- Parts

    /// <summary>
    /// The game's icon, <paramref name="size"/> square: the name's first letter on a tile until the
    /// .exe's icon is read, then the icon alone (a transparent one on the card's colour, which
    /// <paramref name="colour"/> sets).
    /// </summary>
    private static Border IconBox(LibraryGame game, double size, Action<Color?> colour)
    {
        var box = new Border
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                CornerRadius = new CornerRadius(size / 6),
                Child = new TextBlock
                {
                    Text = game.Name.Length > 0 ? game.Name[..1] : "?", FontSize = size * 0.47, FontFamily = PlayerTheme.TitleFont(game.Name.Length > 0 ? game.Name[..1] : "?"), FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Highlight),
            }.Use(Border.BackgroundProperty, PlayerTheme.Keys.SurfaceHigh),
        };
        var task = GameIcons.Of(game.Exe);
        if (task.IsCompletedSuccessfully)
            Show(task.Result);
        else
            _ = ShowAsync();
        return box;

        async Task ShowAsync() => Show(await task);

        void Show((Bitmap? Icon, Color? Colour) read)
        {
            if (read.Icon == null)
                return;
            box.Child = new Image { Source = read.Icon, Width = size, Height = size };
            colour(read.Colour);
        }
    }

    private static Button RailButton(Geometry icon, string name, string ink)
    {
        var button = new Button
        {
            Width = 48, Height = 48, Padding = new Thickness(12), CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center,
            Content = new Path { Data = icon, Stretch = Stretch.Uniform }.Use(Shape.FillProperty, ink),
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        return button;
    }

    /// <summary>The rail's page shown: a lighter square behind it.</summary>
    private static void Chosen(Button button, bool chosen)
    {
        if (chosen)
            button.Use(BackgroundProperty, PlayerTheme.Keys.SurfaceHigh);
        else
            button.Background = Brushes.Transparent;
    }

    private static TextBlock Fact(string text, bool trim = false) =>
        new TextBlock { Text = text, FontSize = 13, TextTrimming = trim ? TextTrimming.PathSegmentEllipsis : TextTrimming.None, TextWrapping = trim ? TextWrapping.NoWrap : TextWrapping.Wrap }
            .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);

    private static T At<T>(T control, int column, int row = 0) where T : Control
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        return control;
    }

    /// <summary>When, in words: "just now", "2 hours ago", "yesterday", "5 days ago", then the date.</summary>
    private static string Ago(DateTime when)
    {
        var past = DateTime.Now - when;
        if (past < TimeSpan.FromMinutes(1))
            return Strings.AgoJustNow;
        if (past < TimeSpan.FromHours(1))
            return past.Minutes == 1 ? Strings.AgoMinute : string.Format(Strings.AgoMinutes, past.Minutes);
        if (when.Date == DateTime.Today)
            return (int)past.TotalHours == 1 ? Strings.AgoHour : string.Format(Strings.AgoHours, (int)past.TotalHours);
        int days = (DateTime.Today - when.Date).Days;
        return days == 1 ? Strings.AgoYesterday : days < 7 ? string.Format(Strings.AgoDays, days) : string.Format(Strings.AgoDate, when.ToString("yyyy-MM-dd"));
    }

    /// <summary>A play time of a minute or more: "45 min", "14 h 20 min".</summary>
    private static string Duration(long seconds)
    {
        long minutes = seconds / 60, hours = minutes / 60;
        return hours == 0 ? string.Format(Strings.DurationMinutes, minutes)
            : minutes % 60 == 0 ? string.Format(Strings.DurationHours, hours) : string.Format(Strings.DurationHoursMinutes, hours, minutes % 60);
    }

    private static string Saves(long bytes) => bytes > 0 ? string.Format(Strings.SavesSize, Bytes(bytes)) : Strings.NoSavesYet;

    /// <summary>A size: "2.1 GB", "350 MB", "3.5 MB", "12 KB".</summary>
    private static string Bytes(long bytes)
    {
        string[] units = ["", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? string.Format(Strings.SizeBytes, bytes) : value >= 100 ? $"{value:F0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>The logo (the windows' icon) as a picture for the rail.</summary>
    private static Bitmap? LogoBitmap()
    {
        using var stream = typeof(OpenShiinaApplication).Assembly.GetManifestResourceStream("OpenShiina.App.icon.png");
        return stream == null ? null : new Bitmap(stream);
    }

    private async Task OpenFolderAsync(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
        catch (Exception ex)
        {
            Message = string.Format(Strings.CouldNotOpen, path, ex.Message);
        }
    }

    /// <summary>The rail's and the card's drawings, on a 24 by 24 grid.</summary>
    private static class Icons
    {
        // Three books on a shelf, the last leaning
        public static readonly Geometry Library = Geometry.Parse("M3,4 H7 V20 H3 Z M9,4 H13 V20 H9 Z M15.2,4.9 L19,3.9 L23,19.4 L19.2,20.4 Z");
        public static readonly Geometry Add = Geometry.Parse("M11,4 H13 V11 H20 V13 H13 V20 H11 V13 H4 V11 H11 Z");
        // Three sliders
        public static readonly Geometry Settings = Geometry.Parse(
            "M3,6 H21 V7.5 H3 Z M3,11.25 H21 V12.75 H3 Z M3,16.5 H21 V18 H3 Z M6,4.5 H10 V9 H6 Z M14,9.75 H18 V14.25 H14 Z M8,15 H12 V19.5 H8 Z");
        public static readonly Geometry Play = Geometry.Parse("M6,3 L21,12 L6,21 Z");
    }
}
