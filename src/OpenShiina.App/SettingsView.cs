// The settings page, opened from the home screen in its place: the pages on the left (Appearance,
// Graphics, Game, Storage, Debug), the chosen one's groups on the right, each a card of rows: a
// setting's name and a line about it, its switch, list or slider beside it. Saved as they change
// (Game/PlayerSettings.cs), counting from the next game started; the theme shows at once. Back
// (the button, Escape, a phone's back) returns to the library. The pages are made when shown: the
// GPU's name starts the GPU (which held the app's start for it). Appearance also picks the
// language of the screens (Localization/Languages.cs): the host makes them again in it.

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
using OpenShiina.Localization;

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

    /// <summary>The language was changed (and applied): the screens are to be made again in it.</summary>
    public event Action? LanguageChanged;

    public SettingsView()
    {
        this.Use(BackgroundProperty, PlayerTheme.Keys.Background);
        Styles.AddRange(PlayerTheme.CardStyles());
        Styles.AddRange(PageListStyles());
        var back = new Button { Content = Strings.BackToLibrary, VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => Close();
        m_title = new TextBlock { Text = Strings.Settings, FontSize = 26, FontFamily = PlayerTheme.TitleFont(Strings.Settings), FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }
            .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        var note = new TextBlock { Text = Strings.SettingsNote, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }
            .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);
        m_header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(24, 20, 24, 12), Children = { back, m_title, note } };

        foreach (var page in Enum.GetValues<SettingsPages>())
        {
            var item = new ListBoxItem { Content = PageName(page) };
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

    /// <summary>Shows <paramref name="page"/> (the language's page, after the screens were made again in a new language).</summary>
    public void Show(SettingsPages page) => m_pages.SelectedIndex = (int)page;

    private static string PageName(SettingsPages page) => page switch
    {
        SettingsPages.Appearance => Strings.PageAppearance,
        SettingsPages.Graphics => Strings.PageGraphics,
        SettingsPages.Game => Strings.PageGame,
        SettingsPages.Storage => Strings.PageStorage,
        _ => Strings.PageDebug,
    };

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
            SettingsPages.Debug => [Note(Strings.DebugNote)],
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

    /// <summary>
    /// The theme: a sample of each, the chosen one with a gold line; it shows at once. The language
    /// of the screens: the device's, English or a translation; the screens are made again in it.
    /// </summary>
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
            var button = ChoiceCard(label, help, picture, 160);
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
        var languages = Languages.Choices();
        var language = Choice(languages.Select(l => (l.Code, l.Name)).ToList(), m_settings.Language, code =>
        {
            if (code == m_settings.Language)
                return;
            m_settings.Language = code;
            m_settings.Save();
            Languages.Apply(code);
            LanguageChanged?.Invoke();
        });
        return [Group(Strings.GroupTheme, samples), Group(Strings.GroupLanguage, Row(Strings.Language, Strings.LanguageHelp, language))];
    }

    /// <summary>
    /// The graphics level (GraphicsPresets), with what it sets one by one under Details (the
    /// scaling, FSR's sharpening, where the games' pictures are scaled and mixed, the frame rate);
    /// phones and tablets show their two profiles (PhoneProfiles) first, the level among the
    /// details. Each shows the others' changes (showing is set while they are shown, so their
    /// handlers leave the settings be).
    /// </summary>
    private List<Control> GraphicsGroups()
    {
        var s = m_settings;
        bool showing = false;
        var levels = GraphicsPresets.Choices.Append((GraphicsQuality.Custom, Strings.Custom, Strings.CustomHelp)).ToList();
        var level = new ComboBox { Width = Wide };
        foreach (var (quality, label, _) in levels)
            level.Items.Add(new ComboBoxItem { Content = label, IsEnabled = quality != GraphicsQuality.Custom });
        var levelHelp = Muted("");

        var scalings = PictureScaling.Choices;
        var scaling = new ComboBox { Width = Wide };
        foreach (var (_, name, _) in scalings)
            scaling.Items.Add(new ComboBoxItem { Content = name });
        var scalingHelp = Muted("");
        var sharpness = new Slider { Minimum = 0, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true, Width = Wide };
        var sharpnessHelp = Muted("");

        var paces = new (FramePacing Pacing, string Label, string Help)[]
        {
            (FramePacing.Game, Strings.PaceGame, Strings.PaceGameHelp),
            (FramePacing.Display, Strings.PaceDisplay, Strings.PaceDisplayHelp),
        };
        var pacing = new ComboBox { Width = Wide };
        foreach (var (_, label, _) in paces)
            pacing.Items.Add(new ComboBoxItem { Content = label });
        var paceHelp = Muted("");

        var profiles = new List<(PhoneProfile Profile, Button Card)>();
        var profileNote = Muted(Strings.ProfileOwn);
        void Show()
        {
            showing = true;
            var current = GraphicsPresets.Of(s);
            int at = levels.FindIndex(l => l.Item1 == current);
            level.SelectedIndex = at;
            levelHelp.Text = at >= 0 ? levels[at].Item3 : "";
            int scaled = scalings.ToList().FindIndex(c => c.Scaling == s.Scaling);
            scaling.SelectedIndex = scaled;
            scalingHelp.Text = scaled >= 0 ? scalings[scaled].Help : "";
            sharpness.Value = s.FsrSharpness;
            sharpness.IsEnabled = s.Scaling == Scaling.Fsr;
            sharpnessHelp.Text = s.Scaling != Scaling.Fsr ? Strings.FsrOnlyWithFsr : s.FsrSharpness == 0 ? Strings.FsrNone : string.Format(Strings.FsrOf, s.FsrSharpness);
            int paced = Array.FindIndex(paces, p => p.Pacing == s.FramePacing);
            pacing.SelectedIndex = paced;
            paceHelp.Text = paced >= 0 ? paces[paced].Help : "";
            var profile = PhoneProfiles.Of(s);
            foreach (var (p, card) in profiles)
                card.Classes.Set("chosen", p == profile);
            profileNote.IsVisible = profile == null;
            showing = false;
        }
        void Changed()
        {
            s.Save();
            Show();
        }
        level.SelectionChanged += (_, _) =>
        {
            if (showing || level.SelectedIndex < 0 || levels[level.SelectedIndex].Item1 == GraphicsQuality.Custom)
                return;
            GraphicsPresets.Apply(s, levels[level.SelectedIndex].Item1);
            Changed();
        };
        scaling.SelectionChanged += (_, _) =>
        {
            if (showing || scaling.SelectedIndex < 0)
                return;
            s.Scaling = scalings[scaling.SelectedIndex].Scaling;
            Changed();
        };
        sharpness.ValueChanged += (_, _) =>
        {
            int value = (int)Math.Round(sharpness.Value);
            if (showing || value == s.FsrSharpness)
                return;
            s.FsrSharpness = value;
            Changed();
        };
        pacing.SelectionChanged += (_, _) =>
        {
            if (showing || pacing.SelectedIndex < 0)
                return;
            s.FramePacing = paces[pacing.SelectedIndex].Pacing;
            Changed();
        };

        // Where the pictures are drawn, and the GPU it would be
        var device = Muted("");
        void ShowDevice() => device.Text = s.Renderer != Renderer.Gpu ? Strings.DrawingCpu
            : PlayerSettings.Gpu(out string? error) is { } gpu ? gpu.Name : string.Format(Strings.NoGpu, error);
        ShowDevice();
        var drawing = Choice([(Renderer.Cpu, Strings.Cpu), (Renderer.Gpu, Strings.GpuVulkan)], s.Renderer, renderer =>
        {
            s.Renderer = renderer;
            s.Save();
            ShowDevice();
        });

        var levelRow = Row(Strings.GraphicsLevel, Strings.GraphicsLevelHelp, level, levelHelp);
        var details = new List<Control>
        {
            Row(Strings.Scaling, null, scaling, scalingHelp),
            Row(Strings.FsrSharpening, null, sharpness, sharpnessHelp),
            Row(Strings.Drawing, Strings.DrawingHelp, drawing, device),
            Row(Strings.FrameRate, null, pacing, paceHelp),
        };
        List<Control> groups;
        if (PlayerPlatform.Touch)
        {
            var cards = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (profile, label, help) in PhoneProfiles.Choices)
            {
                var card = ChoiceCard(label, help, null, 220);
                card.Click += (_, _) =>
                {
                    PhoneProfiles.Apply(s, profile);
                    Changed();
                };
                profiles.Add((profile, card));
                cards.Children.Add(card);
            }
            details.Insert(0, levelRow);
            groups = [Group(Strings.GroupQuality, new StackPanel { Children = { cards, profileNote } }), Group(Strings.GroupDetails, details.ToArray())];
        }
        else
            groups = [Group(Strings.GroupQuality, levelRow), Group(Strings.GroupDetails, details.ToArray())];
        Show();
        return groups;
    }

    /// <summary>The memory for the archives' decoded files (FileCachePresets).</summary>
    private List<Control> StorageGroups()
    {
        var s = m_settings;
        var help = FileCachePresets.Choices.ToDictionary(c => c.Size, c => c.Help);
        var detail = Muted(help[s.FileCache]);
        var cache = Choice(
            FileCachePresets.Choices.Select(c => (c.Size, c.Size == FileCacheSize.Auto ? string.Format(Strings.FileCacheAutoHere, c.Label, FileCachePresets.Auto() >> 20) : c.Label)).ToList(),
            s.FileCache, size =>
            {
                s.FileCache = size;
                s.Save();
                detail.Text = help[size];
            });
        return
        [
            Group(Strings.GroupMemory, Row(Strings.FileCache, Strings.FileCacheHelp, cache, detail)),
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
        // (a finger's room on phones and tablets)
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = PlayerPlatform.Touch ? 56 : 44, Children = { text, At(control, 1) } };
    }

    /// <summary>An on / off setting's row.</summary>
    private Control Switch(SettingSwitch item)
    {
        var toggle = new ToggleSwitch { IsChecked = item.Get(m_settings), OnContent = Strings.On, OffContent = Strings.Off };
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

    /// <summary>A card to choose (a theme, a profile): its picture, its name and a line about it; "chosen" marks it.</summary>
    private static Button ChoiceCard(string label, string help, Control? picture, double width)
    {
        var content = new StackPanel { Spacing = 6 };
        if (picture != null)
            content.Children.Add(picture);
        content.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink));
        content.Children.Add(Muted(help));
        var card = new Button
        {
            Width = width, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(PlayerPlatform.Touch ? 12 : 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            Content = content,
        };
        card.Classes.Add("card");
        AutomationProperties.SetName(card, label);
        return card;
    }

    /// <summary>A line on a page of its own, above its groups.</summary>
    private static Control Note(string text) => Muted(text);

    private static TextBlock Muted(string text) =>
        new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);

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
                new Setter(TemplatedControl.PaddingProperty, PlayerPlatform.Touch ? new Thickness(16, 14) : new Thickness(14, 10)),
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
