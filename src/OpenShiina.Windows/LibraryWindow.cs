// The home screen of the Windows player: the games of the library (Core Game/GameLibrary.cs, the
// same list as the Avalonia player's), each a card with the icon of its .exe, its name and when
// it was played; a click plays it, its menu (right click) opens its save folder or its folder or
// takes it off the list. "Add a game…" asks for a folder. A game whose folder is gone is faded.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenShiina.Formats;

namespace OpenShiina.Windows;

public sealed class LibraryWindow : Window
{
    private readonly WrapPanel m_cards = new() { Margin = new Thickness(12, 0, 12, 12) };
    private readonly TextBlock m_empty = new()
    {
        Text = "No games yet. Add the folder of an installed game: the one with its .exe and .WAR files.",
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 12, 24, 12), Foreground = Brushes.DimGray,
    };
    private readonly TextBlock m_message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 24, 16) };

    /// <summary>A game was chosen: its folder.</summary>
    public event Action<string>? Play;

    public LibraryWindow()
    {
        Title = "OpenShiina";
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var add = new Button { Content = "Add a game…", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Right };
        add.Click += (_, _) => AddGame();
        var title = new TextBlock { Text = "OpenShiina", FontSize = 26, VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(24, 16, 24, 8), LastChildFill = false };
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(title);
        var body = new StackPanel { Children = { m_cards, m_empty, m_message } };
        var page = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);
        page.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = page;
        Refresh();
    }

    /// <summary>A line under the cards (opening, an error); empty to clear it.</summary>
    public string Message
    {
        get => m_message.Text;
        set => m_message.Text = value;
    }

    /// <summary>Reads the library again.</summary>
    public void Refresh()
    {
        m_cards.Children.Clear();
        var games = GameLibrary.Load();
        foreach (var game in games)
            m_cards.Children.Add(Card(game));
        m_empty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddGame()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder of an installed game (where its .exe and .WAR files are)" };
        if (GameLibrary.Load().FirstOrDefault(g => g.Available) is { } last && Path.GetDirectoryName(last.Folder) is { } near)
            dialog.InitialDirectory = near;
        if (dialog.ShowDialog(this) != true)
            return;
        Message = GameLibrary.Add(dialog.FolderName) == null
            ? $"No game was recognised in {dialog.FolderName}: choose the folder with the game's own .exe and .WAR files."
            : "";
        Refresh();
    }

    private UIElement Card(LibraryGame game)
    {
        bool available = game.Available;
        UIElement icon = GameIcon(game) is { } image
            ? new Image { Source = image, Width = 64, Height = 64 }
            : new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(8), Background = Brushes.Gainsboro,
                Child = new TextBlock
                {
                    Text = game.Name.Length > 0 ? game.Name[..1] : "?", FontSize = 30,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        var content = new StackPanel { Children = { icon } };
        content.Children.Add(new TextBlock
        {
            Text = game.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = available ? game.LastPlayed is { } played ? $"Played {played:yyyy-MM-dd}" : "Not played yet" : "Folder not found",
            FontSize = 12, Foreground = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Center,
        });
        var card = new Button
        {
            Width = 200, Height = 160, Margin = new Thickness(8), Padding = new Thickness(8),
            Content = content, ToolTip = game.Folder, Opacity = available ? 1 : 0.5,
        };

        var menu = new ContextMenu();
        var play = new MenuItem { Header = "Play", IsEnabled = available };
        play.Click += (_, _) => Play?.Invoke(game.Folder);
        var saves = new MenuItem { Header = "Open the save folder" };
        saves.Click += (_, _) => OpenFolder(game.SaveFolder);
        var folder = new MenuItem { Header = "Open the game's folder", IsEnabled = available };
        folder.Click += (_, _) => OpenFolder(game.Folder);
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
        card.ContextMenu = menu;
        card.Click += (_, _) =>
        {
            if (available)
                Play?.Invoke(game.Folder);
            else
                Message = $"The folder of {game.Name} is not there any more:\n{game.Folder}";
        };
        return card;
    }

    private static ImageSource? GameIcon(LibraryGame game)
    {
        try
        {
            if (game.Exe is { } exe && File.Exists(exe) && ExeIcon.Read(exe) is { } ico)
            {
                var frame = BitmapFrame.Create(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
        }
        catch (Exception)
        {
            // No icon: the card shows the name's first letter
        }
        return null;
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Message = $"Could not open {path}:\n{ex.Message}";
        }
    }
}
