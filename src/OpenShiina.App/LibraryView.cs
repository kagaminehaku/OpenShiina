// The home screen: the logo and its wordmark, then the games of the library (Game/GameLibrary.cs),
// each a card with the icon of its .exe, its name and its folder; a click plays it, its menu (right
// click, or the "…" button) opens its save folder or takes it off the list. "Add a game…" asks for
// a folder. A game whose folder is gone is shown faded and cannot be played. "Settings" opens the
// settings page in the library's place (SettingsView.cs). Colours come from the theme (PlayerTheme.cs).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using OpenShiina.Formats;
using OpenShiina.Game;

namespace OpenShiina.App;

public sealed class LibraryView : UserControl
{
    private readonly WrapPanel m_cards = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock m_empty = new()
    {
        Text = "No games yet. Add the folder of an installed game: the one with its .exe and .WAR files.",
        TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24),
    };
    private readonly TextBlock m_message = new()
    {
        TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center,
    };

    // Icons read once per .exe
    private static readonly Dictionary<string, Bitmap?> s_icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A game was chosen: its folder.</summary>
    public event Action<string>? Play;

    /// <summary>"Add a game…": the host asks for a folder and opens it.</summary>
    public event Action? AddRequested;

    public LibraryView()
    {
        this.Use(BackgroundProperty, PlayerTheme.Keys.Background);
        m_empty.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);
        m_message.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        Styles.AddRange(PlayerTheme.CardStyles());
        var add = new Button { Content = "Add a game…", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        add.Classes.Add("accent");
        add.Click += (_, _) => AddRequested?.Invoke();
        var settings = new Button { Content = "Settings", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        settings.Click += (_, _) => OpenSettings();
        var wordmark = PlayerTheme.Wordmark(30);
        wordmark.VerticalAlignment = VerticalAlignment.Center;
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
            Children = { new Image { Source = LogoBitmap(), Width = 44, Height = 44 }, wordmark },
        };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(24, 20, 24, 12),
            Children = { title, settings, add },
        };
        Grid.SetColumn(settings, 1);
        Grid.SetColumn(add, 2);
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 16), Children = { m_cards, m_empty, m_message, m_choice } };
        m_home = new DockPanel { Children = { header, new ScrollViewer { Content = body } } };
        DockPanel.SetDock(header, Dock.Top);
        Content = m_home;
        Refresh();
    }

    // The library, or the settings page in its place (SettingsView, made each time it is opened)
    private readonly DockPanel m_home;

    private void OpenSettings()
    {
        var settings = new SettingsView();
        settings.Closed += () =>
        {
            Content = m_home;
            Focus();
        };
        Content = settings;
        settings.Focusable = true;
        settings.Focus();
    }

    /// <summary>Back from the settings to the library (a phone's back); false when they are not open.</summary>
    public bool CloseSettings()
    {
        if (Content is not SettingsView settings)
            return false;
        settings.Close();
        return true;
    }

    // A question under the cards (ChooseAsync): its text and a button an answer, then Cancel
    private readonly StackPanel m_choice = new() { Spacing = 6, IsVisible = false, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };

    /// <summary>
    /// Asks the user to pick one of <paramref name="answers"/> under the cards; the answer, or
    /// null for Cancel (or when another question replaces this one).
    /// </summary>
    public Task<string?> ChooseAsync(string question, IReadOnlyList<string> answers)
    {
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
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        cancel.Click += (_, _) => Answer(null);
        m_choice.Children.Add(cancel);
        m_choice.IsVisible = true;
        Message = "";
        return done.Task;
    }

    /// <summary>A line under the cards (opening, an error); empty to clear it.</summary>
    public string Message
    {
        get => m_message.Text ?? "";
        set => m_message.Text = value;
    }

    /// <summary>Reads the library again.</summary>
    public void Refresh()
    {
        m_cards.Children.Clear();
        var games = GameLibrary.Load();
        foreach (var game in games)
            m_cards.Children.Add(Card(game));
        m_empty.IsVisible = games.Count == 0;
        StartupLog.Mark($"the library read: {games.Count} games");
    }

    private Control Card(LibraryGame game)
    {
        bool available = game.Available;
        // The name's first letter until the .exe's icon is read (on a pool thread: a phone reads
        // each .exe, about 1 MB, from its shared storage)
        var icon = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = game.Name.Length > 0 ? game.Name[..1] : "?", FontSize = 30, FontFamily = PlayerTheme.WordmarkFont, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Highlight),
        }.Use(Border.BackgroundProperty, PlayerTheme.Keys.SurfaceHigh);
        _ = ShowIconAsync(game, icon);
        var name = new TextBlock
        {
            Text = game.Name, FontSize = 15, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        var detail = new TextBlock
        {
            Text = available ? game.LastPlayed is { } played ? $"Played {played:yyyy-MM-dd}" : "Not played yet" : "Folder not found",
            FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
        }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);
        var more = new Button { Content = "…", Padding = new Thickness(8, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        var card = new Button
        {
            Width = 200, Height = 170, Margin = new Thickness(8), Padding = new Thickness(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            IsEnabled = true, Opacity = available ? 1 : 0.5,
            Content = new Grid
            {
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                        Children = { icon, name, detail },
                    },
                    more,
                },
            },
        };
        card.Classes.Add("card");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(card, game.Folder);

        var menu = new MenuFlyout();
        var play = new MenuItem { Header = "Play", IsEnabled = available };
        play.Click += (_, _) => Play?.Invoke(game.Folder);
        var saves = new MenuItem { Header = "Open the save folder" };
        saves.Click += async (_, _) => await OpenFolderAsync(game.SaveFolder);
        var folder = new MenuItem { Header = "Open the game's folder", IsEnabled = available };
        folder.Click += async (_, _) => await OpenFolderAsync(game.Folder);
        var remove = new MenuItem { Header = "Remove from the list" };
        remove.Click += (_, _) =>
        {
            GameLibrary.Remove(game.Folder);
            Refresh();
        };
        menu.Items.Add(play);
        menu.Items.Add(saves);
        menu.Items.Add(folder);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        card.ContextFlyout = menu;
        more.Click += (_, e) =>
        {
            menu.ShowAt(more);
            e.Handled = true;
        };
        card.Click += (_, _) =>
        {
            if (available)
                Play?.Invoke(game.Folder);
            else
                Message = $"The folder of {game.Name} is not there any more:\n{game.Folder}";
        };
        return card;
    }

    /// <summary>The game's .exe icon in place of the letter, once read (on a pool thread, once per .exe).</summary>
    private static async Task ShowIconAsync(LibraryGame game, Border icon)
    {
        if (game.Exe is not { } exe)
            return;
        Bitmap? bitmap;
        lock (s_icons)
            s_icons.TryGetValue(exe, out bitmap);
        if (bitmap == null)
        {
            bitmap = await Task.Run(() =>
            {
                try
                {
                    return File.Exists(exe) && ExeIcon.Read(exe) is { } ico ? new Bitmap(new MemoryStream(ico)) : null;
                }
                catch (Exception)
                {
                    // No icon: the card keeps the name's first letter
                    return null;
                }
            });
            if (bitmap == null)
                return;
            lock (s_icons)
                s_icons[exe] = bitmap;
        }
        // the binding to the theme goes too (a later theme would paint behind the icon again)
        icon.ClearValue(Border.BackgroundProperty);
        icon.Child = new Image { Source = bitmap, Width = 64, Height = 64 };
    }

    /// <summary>The logo (the windows' icon) as a picture for the header.</summary>
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
            Message = $"Could not open {path}:\n{ex.Message}";
        }
    }
}
