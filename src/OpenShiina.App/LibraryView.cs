// The home screen: the games of the library (Game/GameLibrary.cs), each a card with the icon of
// its .exe, its name and its folder; a click plays it, its menu (right click, or the "…" button)
// opens its save folder or takes it off the list. "Add a game…" asks for a folder. A game whose
// folder is gone is shown faded and cannot be played. "Settings" opens the player's settings
// (Game/PlayerSettings.cs): drawing on the CPU or the GPU, the frame rate, and the on / off
// settings.

using Avalonia;
using Avalonia.Automation;
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
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 16), Children = { m_cards, m_empty, m_message, m_choice } };
        var page = new DockPanel { Children = { header, new ScrollViewer { Content = body } } };
        DockPanel.SetDock(header, Dock.Top);
        Content = page;
        Refresh();
    }

    /// <summary>
    /// The settings, saved as they change, counting from the next game started: the graphics level
    /// (GraphicsPresets), with what it sets one by one under Advanced graphics (where the games'
    /// pictures are scaled and mixed, the CPU or the GPU through Vulkan, the frame rate, the
    /// scaling, FSR's sharpening), then the on / off settings (PlayerSettings.Switches) in their
    /// sections; it scrolls on small screens.
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
        static TextBlock Heading(string text) =>
            new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        static TextBlock Help(string text, double indent) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(indent, 0, 0, 0) };

        // The levels and the advanced settings they set, each showing the other's changes
        // (showing is set while they are shown, so their handlers leave the settings be)
        bool showing = false;
        var levels = new Dictionary<GraphicsQuality, RadioButton>();
        var scalings = new Dictionary<Scaling, RadioButton>();
        var sharpness = new Slider { Minimum = 0, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(28, 0, 0, 0) };
        var sharpnessLabel = new TextBlock { Margin = new Thickness(28, 4, 0, 0) };
        void Show()
        {
            showing = true;
            var level = GraphicsPresets.Of(settings);
            foreach (var (quality, button) in levels)
                button.IsChecked = quality == level;
            levels[GraphicsQuality.Custom].IsVisible = level == GraphicsQuality.Custom;
            foreach (var (scaling, button) in scalings)
                button.IsChecked = settings.Scaling == scaling;
            sharpness.Value = settings.FsrSharpness;
            sharpness.IsEnabled = settings.Scaling == Scaling.Fsr;
            sharpnessLabel.Text = "FSR sharpening: " + (settings.FsrSharpness == 0 ? "none" : $"{settings.FsrSharpness} of 10");
            showing = false;
        }
        var page = new StackPanel
        {
            Width = 340,
            Spacing = 4,
            Children =
            {
                Heading("Graphics"),
                Help("How the game's picture is scaled to the window, from the lightest to the heaviest. FSR and Anime4K run on the GPU that draws the window (without one, as Low).", 0),
            },
        };
        foreach (var (quality, label, help) in GraphicsPresets.Choices.Append((GraphicsQuality.Custom, "Custom", "Set one by one under Advanced graphics.")))
        {
            var choice = new RadioButton
            {
                Content = new StackPanel { Children = { new TextBlock { Text = label }, Help(help, 0) } },
                GroupName = "graphics", IsEnabled = quality != GraphicsQuality.Custom,
            };
            AutomationProperties.SetName(choice, label);
            choice.IsCheckedChanged += (_, _) =>
            {
                if (showing || choice.IsChecked != true)
                    return;
                GraphicsPresets.Apply(settings, quality);
                settings.Save();
                Show();
            };
            levels[quality] = choice;
            page.Children.Add(choice);
        }

        var advanced = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Heading("Drawing"),
                Help("Where the games' pictures are scaled and mixed. Both give the same pictures; the GPU takes the heavy work off the processor.", 0),
                cpu,
                gpu,
                device,
                Heading("Frame rate"),
            },
        };
        foreach (var (pacing, label) in new[] { (FramePacing.Game, "The game's pace (as the original: up to 60 frames a second, little processor while it waits)"), (FramePacing.Display, "Every refresh of the screen (smoother on a fast screen, more processor)") })
        {
            var choice = new RadioButton
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                GroupName = "pacing", IsChecked = settings.FramePacing == pacing,
            };
            choice.IsCheckedChanged += (_, _) =>
            {
                if (choice.IsChecked != true)
                    return;
                settings.FramePacing = pacing;
                settings.Save();
            };
            advanced.Children.Add(choice);
        }
        advanced.Children.Add(Heading("Scaling"));
        advanced.Children.Add(Help("How the game's picture is scaled to the window.", 0));
        foreach (var (scaling, label) in PictureScaling.Choices)
        {
            var choice = new RadioButton
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                GroupName = "scaling",
            };
            choice.IsCheckedChanged += (_, _) =>
            {
                if (showing || choice.IsChecked != true)
                    return;
                settings.Scaling = scaling;
                settings.Save();
                Show();
            };
            scalings[scaling] = choice;
            advanced.Children.Add(choice);
        }
        sharpness.ValueChanged += (_, _) =>
        {
            int value = (int)Math.Round(sharpness.Value);
            if (showing || value == settings.FsrSharpness)
                return;
            settings.FsrSharpness = value;
            settings.Save();
            Show();
        };
        advanced.Children.Add(sharpnessLabel);
        advanced.Children.Add(sharpness);
        page.Children.Add(new Expander
        {
            Header = new TextBlock { Text = "Advanced graphics", FontSize = 16, FontWeight = FontWeight.SemiBold },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
            Content = advanced,
        });
        Show();

        string? section = null;
        foreach (var item in PlayerSettings.Switches)
        {
            if (item.Section != section)
            {
                section = item.Section;
                page.Children.Add(Heading(section));
            }
            var box = new CheckBox { Content = item.Label, IsChecked = item.Get(settings) };
            box.IsCheckedChanged += (_, _) =>
            {
                item.Set(settings, box.IsChecked == true);
                settings.Save();
            };
            page.Children.Add(box);
            page.Children.Add(Help(item.Help, 28));
        }
        page.Children.Add(new TextBlock { Text = "A change counts from the next game started.", Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 0) });
        return new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new ScrollViewer { MaxHeight = 560, Content = page },
        };
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
        m_choice.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
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
