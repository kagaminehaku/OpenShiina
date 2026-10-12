// The settings page, opened from the home screen in its place: the pages on the left (Appearance,
// Graphics, Game, Storage, Debug), the chosen one's groups on the right, each a card of rows: a
// setting's name and a line about it, its switch, list or slider beside it. Saved as they change
// (Game/PlayerSettings.cs), counting from the next game started; the theme shows at once. Back
// (the button, Escape, a phone's back) returns to the library. The pages are made when shown: the
// GPU's name starts the GPU (which held the app's start for it).

using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using OpenShiina.Game;

namespace OpenShiina.App;

public sealed class SettingsView : UserControl
{
    private readonly PlayerSettings m_settings = PlayerSettings.Load();
    private readonly ListBox m_pages = new() { Background = Brushes.Transparent };
    private readonly TransitioningContentControl m_page = new() { PageTransition = new CrossFade(TimeSpan.FromMilliseconds(120)) };
    private readonly ScrollViewer m_scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ColumnDefinition m_pagesColumn = new(210, GridUnitType.Pixel);
    private readonly StackPanel m_header;
    private readonly TextBlock m_title;

    // The lists' and sliders' width
    private const double Wide = 230;

    /// <summary>Back to the library.</summary>
    public event Action? Closed;

    public SettingsView()
    {
        this.Use(BackgroundProperty, PlayerTheme.Keys.Background);
        Styles.AddRange(PlayerTheme.CardStyles());
        Styles.AddRange(PageListStyles());
        var back = new Button { Content = "← Library", VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => Close();
        m_title = new TextBlock { Text = "Settings", FontSize = 26, FontFamily = PlayerTheme.WordmarkFont, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
            .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        var note = new TextBlock { Text = "Saved as you change them, for the next game started.", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }
            .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);
        m_header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(24, 20, 24, 12), Children = { back, m_title, note } };

        foreach (var page in Enum.GetValues<SettingsPages>())
        {
            var item = new ListBoxItem { Content = page.ToString() };
            item.Classes.Add("page");
            m_pages.Items.Add(item);
        }
        m_pages.SelectionChanged += (_, _) => ShowPage();
        m_scroll.Content = m_page;
        var body = new Grid { ColumnDefinitions = { m_pagesColumn, new ColumnDefinition(GridLength.Star) } };
        m_pages.Margin = new Thickness(16, 0, 8, 16);
        body.Children.Add(m_pages);
        body.Children.Add(m_scroll);
        Grid.SetColumn(m_scroll, 1);
        var layout = new DockPanel { Children = { m_header, body } };
        DockPanel.SetDock(m_header, Dock.Top);
        Content = layout;
        // A phone held sideways: a narrower page list, a lower header
        SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 720 || e.NewSize.Height < 480;
            m_pagesColumn.Width = new GridLength(compact ? 150 : 210);
            m_header.Margin = compact ? new Thickness(16, 8, 16, 6) : new Thickness(24, 20, 24, 12);
            m_title.FontSize = compact ? 20 : 26;
            note.IsVisible = e.NewSize.Width >= 560;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        m_pages.SelectedIndex = 0;
    }

    public void Close() => Closed?.Invoke();

    private void ShowPage()
    {
        if (m_pages.SelectedIndex < 0)
            return;
        var page = (SettingsPages)m_pages.SelectedIndex;
        var groups = page switch
        {
            SettingsPages.Appearance => AppearanceGroups(),
            SettingsPages.Graphics => GraphicsGroups(),
            SettingsPages.Storage => StorageGroups(),
            SettingsPages.Debug => [Note("For bug reports: what these write goes into the game's save folder (its menu in the library opens it).")],
            _ => [],
        };
        var content = new StackPanel { Spacing = 20, Margin = new Thickness(8, 0, 24, 24) };
        content.Children.AddRange(groups);
        // then its on / off settings, in their groups
        foreach (var group in PlayerSettings.Switches.Where(s => s.Page == page).GroupBy(s => s.Group))
            content.Children.Add(Group(group.Key, group.Select(Switch).ToArray()));
        // as wide as the window, up to a line's comfortable length, on the left
        m_page.Content = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star) { MaxWidth = 800 } }, Children = { content } };
        m_scroll.Offset = default;
    }

    // ---- The pages

    /// <summary>The theme: a sample of each, the chosen one with a gold line; it shows at once.</summary>
    private List<Control> AppearanceGroups()
    {
        var samples = new WrapPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<(AppTheme Theme, Button Button)>();
        foreach (var (theme, label, help) in PlayerTheme.Choices)
        {
            var colours = PlayerTheme.Sample(theme);
            var picture = new Border
            {
                Height = 64, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(colours.Background), Padding = new Thickness(10),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new Border
                        {
                            CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(colours.Surface), Padding = new Thickness(8, 4),
                            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock { Text = "Aa", FontFamily = PlayerTheme.WordmarkFont, FontWeight = FontWeight.Bold, FontSize = 18, Foreground = new SolidColorBrush(colours.Ink) },
                        },
                        At(new StackPanel
                        {
                            Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new Border { Width = 36, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(colours.Accent) },
                                new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(colours.Highlight) },
                            },
                        }, 1),
                    },
                },
            };
            var button = new Button
            {
                Width = 160, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        picture,
                        new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink),
                        Muted(help),
                    },
                },
            };
            button.Classes.Add("card");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                if (m_settings.Theme != theme)
                {
                    m_settings.Theme = theme;
                    m_settings.Save();
                    if (Application.Current is { } application)
                        PlayerTheme.Apply(application, theme);
                }
                foreach (var (other, b) in buttons)
                    b.Classes.Set("chosen", other == theme);
            };
            button.Classes.Set("chosen", m_settings.Theme == theme);
            buttons.Add((theme, button));
            samples.Children.Add(button);
        }
        return [Group("Theme", samples)];
    }

    /// <summary>
    /// The graphics level (GraphicsPresets), with what it sets one by one under Details (where the
    /// games' pictures are scaled and mixed, the scaling, FSR's sharpening, the frame rate); each
    /// shows the other's changes (showing is set while they are shown, so their handlers leave the
    /// settings be).
    /// </summary>
    private List<Control> GraphicsGroups()
    {
        var s = m_settings;
        bool showing = false;
        var levels = GraphicsPresets.Choices.Append((GraphicsQuality.Custom, "Custom", "Set one by one under Details.")).ToList();
        var level = new ComboBox { Width = Wide };
        foreach (var (quality, label, _) in levels)
            level.Items.Add(new ComboBoxItem { Content = label, IsEnabled = quality != GraphicsQuality.Custom });
        var levelHelp = Muted("");

        var scalings = PictureScaling.Choices.Select(c => Split(c.Label)).ToList();
        var scaling = new ComboBox { Width = Wide };
        foreach (var (name, _) in scalings)
            scaling.Items.Add(new ComboBoxItem { Content = name });
        var scalingHelp = Muted("");
        var sharpness = new Slider { Minimum = 0, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true, Width = Wide };
        var sharpnessHelp = Muted("");
        void Show()
        {
            showing = true;
            var current = GraphicsPresets.Of(s);
            int at = levels.FindIndex(l => l.Item1 == current);
            level.SelectedIndex = at;
            levelHelp.Text = at >= 0 ? levels[at].Item3 : "";
            int scaled = PictureScaling.Choices.ToList().FindIndex(c => c.Scaling == s.Scaling);
            scaling.SelectedIndex = scaled;
            scalingHelp.Text = scaled >= 0 ? scalings[scaled].Help : "";
            sharpness.Value = s.FsrSharpness;
            sharpness.IsEnabled = s.Scaling == Scaling.Fsr;
            sharpnessHelp.Text = s.Scaling != Scaling.Fsr ? "With FSR 1 scaling." : s.FsrSharpness == 0 ? "None." : $"{s.FsrSharpness} of 10.";
            showing = false;
        }
        level.SelectionChanged += (_, _) =>
        {
            if (showing || level.SelectedIndex < 0 || levels[level.SelectedIndex].Item1 == GraphicsQuality.Custom)
                return;
            GraphicsPresets.Apply(s, levels[level.SelectedIndex].Item1);
            s.Save();
            Show();
        };
        scaling.SelectionChanged += (_, _) =>
        {
            if (showing || scaling.SelectedIndex < 0)
                return;
            s.Scaling = PictureScaling.Choices[scaling.SelectedIndex].Scaling;
            s.Save();
            Show();
        };
        sharpness.ValueChanged += (_, _) =>
        {
            int value = (int)Math.Round(sharpness.Value);
            if (showing || value == s.FsrSharpness)
                return;
            s.FsrSharpness = value;
            s.Save();
            Show();
        };
        Show();

        // Where the pictures are drawn, and the GPU it would be
        var device = Muted("");
        void ShowDevice() => device.Text = s.Renderer != Renderer.Gpu ? "The processor's cores, with SIMD."
            : PlayerSettings.Gpu(out string? error) is { } gpu ? gpu.Name : $"No GPU to use ({error}): the games run on the CPU.";
        ShowDevice();
        var drawing = Choice([(Renderer.Cpu, "CPU"), (Renderer.Gpu, "GPU (Vulkan)")], s.Renderer, renderer =>
        {
            s.Renderer = renderer;
            s.Save();
            ShowDevice();
        });

        var paceHelp = new Dictionary<FramePacing, string>
        {
            [FramePacing.Game] = "As the original: up to 60 frames a second, little processor while it waits.",
            [FramePacing.Display] = "Smoother on a fast screen, more processor.",
        };
        var paceDetail = Muted(paceHelp[s.FramePacing]);
        var pacing = Choice([(FramePacing.Game, "The game's pace"), (FramePacing.Display, "Every screen refresh")], s.FramePacing, pace =>
        {
            s.FramePacing = pace;
            s.Save();
            paceDetail.Text = paceHelp[pace];
        });

        return
        [
            Group("Quality",
                Row("Graphics level", "How the game's picture is scaled to the window, from the lightest to the heaviest. FSR and Anime4K run on the GPU that draws the window (without one, as Low).", level, levelHelp)),
            Group("Details",
                Row("Scaling", null, scaling, scalingHelp),
                Row("FSR sharpening", null, sharpness, sharpnessHelp),
                Row("Drawing", "Where the games' pictures are scaled and mixed. Both give the same pictures; the GPU takes the heavy work off the processor.", drawing, device),
                Row("Frame rate", null, pacing, paceDetail)),
        ];
    }

    /// <summary>The memory for the archives' decoded files (FileCachePresets).</summary>
    private List<Control> StorageGroups()
    {
        var s = m_settings;
        var help = FileCachePresets.Choices.ToDictionary(c => c.Size, c => c.Help);
        var detail = Muted(help[s.FileCache]);
        var cache = Choice(
            FileCachePresets.Choices.Select(c => (c.Size, c.Size == FileCacheSize.Auto ? $"{c.Label} ({FileCachePresets.Auto() >> 20} MB here)" : c.Label)).ToList(),
            s.FileCache, size =>
            {
                s.FileCache = size;
                s.Save();
                detail.Text = help[size];
            });
        return
        [
            Group("Memory",
                Row("File cache", "Memory kept for the pictures and sounds a game read, so that it does not decode them again. Bigger is smoother on scene changes and event pictures.", cache, detail)),
        ];
    }

    // ---- The parts of a page

    /// <summary>A group: its name in gold, then a card with its rows, a hairline between them.</summary>
    private static Control Group(string title, params Control[] rows)
    {
        var card = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                card.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 12) }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Border));
            card.Children.Add(rows[i]);
        }
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = title.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.2, Margin = new Thickness(4, 0, 0, 0) }
                    .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Highlight),
                new Border { Padding = new Thickness(16, 14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = card }
                    .Use(Border.BackgroundProperty, PlayerTheme.Keys.Surface).Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border),
            },
        };
    }

    /// <summary>A row: the setting's name and a line about it (and one about its value), its control on the right.</summary>
    private static Control Row(string title, string? help, Control control, TextBlock? detail = null)
    {
        var text = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = title, FontSize = 15, TextWrapping = TextWrapping.Wrap }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink) },
        };
        if (help != null)
            text.Children.Add(Muted(help));
        if (detail != null)
            text.Children.Add(detail);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        AutomationProperties.SetName(control, title);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 44, Children = { text, At(control, 1) } };
    }

    /// <summary>An on / off setting's row.</summary>
    private Control Switch(SettingSwitch item)
    {
        var toggle = new ToggleSwitch { IsChecked = item.Get(m_settings), OnContent = "On", OffContent = "Off" };
        toggle.IsCheckedChanged += (_, _) =>
        {
            item.Set(m_settings, toggle.IsChecked == true);
            m_settings.Save();
        };
        return Row(item.Label, item.Help, toggle);
    }

    /// <summary>A list of choices with <paramref name="current"/> chosen; <paramref name="chosen"/> on a change.</summary>
    private static ComboBox Choice<T>(IReadOnlyList<(T Value, string Label)> options, T current, Action<T> chosen)
    {
        var box = new ComboBox { Width = Wide };
        foreach (var (_, label) in options)
            box.Items.Add(new ComboBoxItem { Content = label });
        box.SelectedIndex = options.ToList().FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, current));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
                chosen(options[box.SelectedIndex].Value);
        };
        return box;
    }

    /// <summary>A line on a page of its own, above its groups.</summary>
    private static Control Note(string text) => Muted(text);

    private static TextBlock Muted(string text) =>
        new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);

    /// <summary>"Name: what it does" as the name and the rest, a sentence.</summary>
    private static (string Name, string Help) Split(string label)
    {
        int colon = label.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
            return (label, "");
        string rest = label[(colon + 2)..];
        return (label[..colon], char.ToUpperInvariant(rest[0]) + rest[1..] + ".");
    }

    private static T At<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    /// <summary>The page list: rounded rows, the chosen one lighter with a gold mark on its left.</summary>
    private static IEnumerable<IStyle> PageListStyles()
    {
        yield return new Style(x => x.OfType<ListBoxItem>().Class("page"))
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(14, 10)),
                new Setter(TemplatedControl.FontSizeProperty, 15.0),
                new Setter(MarginProperty, new Thickness(0, 2)),
            },
        };
        foreach (bool pointed in new[] { false, true })
            yield return new Style(x => (pointed ? x.OfType<ListBoxItem>().Class("page").Class(":selected").Class(":pointerover") : x.OfType<ListBoxItem>().Class("page").Class(":selected"))
                .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension(PlayerTheme.Keys.SurfaceHigh)),
                    new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension(PlayerTheme.Keys.Highlight)),
                    new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(3, 0, 0, 0)),
                    new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension(PlayerTheme.Keys.Ink)),
                },
            };
    }
}
