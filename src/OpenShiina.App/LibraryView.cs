// The home screen: the games of the library (Game/GameLibrary.cs), each a card with the icon of
// its .exe, its name and its folder; a click plays it, its menu (right click, or the "…" button)
// opens its save folder or takes it off the list. "Add a game…" asks for a folder. A game whose
// folder is gone is shown faded and cannot be played. "Settings" opens the player's settings
// (Game/PlayerSettings.cs): drawing on the CPU or the GPU.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24),
    };
    private readonly TextBlock m_message = new()
    {
        Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center,
    };

    // Icons read once per .exe
    private static readonly Dictionary<string, Bitmap?> s_icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A game was chosen: its folder.</summary>
    public event Action<string>? Play;

    /// <summary>"Add a game…": the host asks for a folder and opens it.</summary>
    public event Action? AddRequested;

    public LibraryView()
    {
        var add = new Button { Content = "Add a game…", HorizontalAlignment = HorizontalAlignment.Right };
        add.Click += (_, _) => AddRequested?.Invoke();
        var settings = new Button { Content = "Settings", Margin = new Thickness(0, 0, 8, 0), Flyout = SettingsFlyout() };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(24, 20, 24, 8),
            Children =
            {
                new TextBlock { Text = "OpenShiina", FontSize = 28, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                settings,
                add,
            },
        };
        Grid.SetColumn(settings, 1);
        Grid.SetColumn(add, 2);
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 16), Children = { m_cards, m_empty, m_message } };
        var page = new DockPanel { Children = { header, new ScrollViewer { Content = body } } };
        DockPanel.SetDock(header, Dock.Top);
        Content = page;
        Refresh();
    }

    /// <summary>
    /// The settings, saved as they change: where the games' pictures are scaled and mixed (the
    /// CPU or the GPU through Vulkan), counting from the next game started.
    /// </summary>
    private static Flyout SettingsFlyout()
    {
        var settings = PlayerSettings.Load();
        var device = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(28, 0, 0, 0) };
        void ShowDevice() => device.Text = settings.Renderer != Renderer.Gpu ? ""
            : PlayerSettings.Gpu(out string? error) is { } gpu ? gpu.Name : $"No GPU to use ({error}): the games run on the CPU.";
        void Choose(Renderer renderer)
        {
            if (settings.Renderer == renderer)
                return;
            settings.Renderer = renderer;
            settings.Save();
            ShowDevice();
        }
        var cpu = new RadioButton { Content = "CPU", GroupName = "renderer", IsChecked = settings.Renderer == Renderer.Cpu };
        var gpu = new RadioButton { Content = "GPU (Vulkan)", GroupName = "renderer", IsChecked = settings.Renderer == Renderer.Gpu };
        cpu.IsCheckedChanged += (_, _) =>
        {
            if (cpu.IsChecked == true)
                Choose(Renderer.Cpu);
        };
        gpu.IsCheckedChanged += (_, _) =>
        {
            if (gpu.IsChecked == true)
                Choose(Renderer.Gpu);
        };
        ShowDevice();
        return new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new StackPanel
            {
                Width = 340,
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Drawing", FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Where the games' pictures are scaled and mixed. Both give the same pictures; the GPU takes the heavy work off the processor.",
                        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray,
                    },
                    cpu,
                    gpu,
                    device,
                    new TextBlock { Text = "A change counts from the next game started.", Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 0) },
                },
            },
        };
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
    }

    private Control Card(LibraryGame game)
    {
        bool available = game.Available;
        Control icon = Icon(game) is { } bitmap
            ? new Image { Source = bitmap, Width = 64, Height = 64 }
            : new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x48)),
                Child = new TextBlock
                {
                    Text = game.Name.Length > 0 ? game.Name[..1] : "?", FontSize = 30, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        var name = new TextBlock
        {
            Text = game.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var detail = new TextBlock
        {
            Text = available ? game.LastPlayed is { } played ? $"Played {played:yyyy-MM-dd}" : "Not played yet" : "Folder not found",
            FontSize = 12, Foreground = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var more = new Button { Content = "…", Padding = new Thickness(8, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
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

    private static Bitmap? Icon(LibraryGame game)
    {
        if (game.Exe is not { } exe)
            return null;
        if (s_icons.TryGetValue(exe, out var cached))
            return cached;
        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(exe) && ExeIcon.Read(exe) is { } ico)
                bitmap = new Bitmap(new MemoryStream(ico));
        }
        catch (Exception)
        {
            // No icon: the card shows the name's first letter
        }
        return s_icons[exe] = bitmap;
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
