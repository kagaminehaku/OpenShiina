// The players' colours: a theme of PlayerSettings (Shiina, the logo's, by default) sets the
// brushes the screens take by name (Keys, as dynamic resources, so that a change of theme shows at
// once) and Fluent's palette (the colours of its buttons, lists and menus), light or dark as the
// theme is. The wordmark is the logo's: "OpenShiina" in Cinzel with the S, the first i and the n
// in red, the sin in the name (tools/LogoGen).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using OpenShiina.Game;
using OpenShiina.Localization;

namespace OpenShiina.App;

public static class PlayerTheme
{
    /// <summary>The names of the theme's brushes.</summary>
    public static class Keys
    {
        /// <summary>The page behind everything.</summary>
        public const string Background = "OpenShiina.Background";
        /// <summary>Cards and panels on the page.</summary>
        public const string Surface = "OpenShiina.Surface";
        /// <summary>A card pointed at, a chosen item, a picture's place before it is read.</summary>
        public const string SurfaceHigh = "OpenShiina.SurfaceHigh";
        /// <summary>Hairlines round cards and between rows.</summary>
        public const string Border = "OpenShiina.Border";
        /// <summary>Text.</summary>
        public const string Ink = "OpenShiina.Ink";
        /// <summary>Text that says less (help, dates).</summary>
        public const string InkMuted = "OpenShiina.InkMuted";
        /// <summary>The one thing to do on a page (Add a game), and what is chosen.</summary>
        public const string Accent = "OpenShiina.Accent";
        /// <summary>Text on the accent.</summary>
        public const string OnAccent = "OpenShiina.OnAccent";
        /// <summary>The gold of the lock: small marks (the selected section, Play).</summary>
        public const string Highlight = "OpenShiina.Highlight";
        /// <summary>Text on the gold.</summary>
        public const string OnHighlight = "OpenShiina.OnHighlight";
        /// <summary>The wordmark's letters and its red ones.</summary>
        public const string WordmarkInk = "OpenShiina.WordmarkInk";
        public const string WordmarkRed = "OpenShiina.WordmarkRed";
    }

    private sealed record Palette(bool Dark, Color Background, Color Surface, Color SurfaceHigh, Color Border, Color Ink, Color InkMuted,
                                  Color Accent, Color OnAccent, Color Highlight, Color OnHighlight, Color WordmarkInk, Color WordmarkRed);

    private static Color C(string hex) => Color.Parse(hex);

    // The logo's colours (tools/LogoGen): night #1A0A10, leather #2A0A12, spine #4A0A18, cover
    // #6B1024, red #C8102E (#E23B4E on dark), gold #D4A649 (#A9792B its shade), pages #F3E6C8
    private static Palette Of(AppTheme theme) => theme switch
    {
        AppTheme.Parchment => new(false, C("#F3E6C8"), C("#FBF6EC"), C("#EADBB8"), C("#D6C49C"), C("#2B1A20"), C("#6B4E44"),
                                  C("#C8102E"), C("#FBF6EC"), C("#A9792B"), C("#FBF6EC"), C("#2B1A20"), C("#C8102E")),
        AppTheme.Graphite => new(true, C("#16161A"), C("#222228"), C("#30303A"), C("#3A3A44"), C("#EDEDF0"), C("#A8A8B2"),
                                 C("#C8102E"), C("#FFFFFF"), C("#D4A649"), C("#16161A"), C("#EDEDF0"), C("#E23B4E")),
        _ => new(true, C("#1A0A10"), C("#2A0A12"), C("#4A0A18"), C("#4A0A18"), C("#F3E6C8"), C("#C9B49A"),
                 C("#C8102E"), C("#FBF6EC"), C("#D4A649"), C("#2A0A12"), C("#F3E6C8"), C("#E23B4E")),
    };

    /// <summary>The choices of the settings, in the screens' language: each theme with its name and a line about it.</summary>
    public static IReadOnlyList<(AppTheme Theme, string Label, string Help)> Choices =>
    [
        (AppTheme.Shiina, Strings.ThemeShiina, Strings.ThemeShiinaHelp),
        (AppTheme.Parchment, Strings.ThemeParchment, Strings.ThemeParchmentHelp),
        (AppTheme.Graphite, Strings.ThemeGraphite, Strings.ThemeGraphiteHelp),
    ];

    /// <summary>The wordmark's font: Cinzel (bold), as the logo's.</summary>
    public static FontFamily WordmarkFont { get; } = new("avares://OpenShiina.App/Resources/Fonts#Cinzel");

    /// <summary>
    /// The wordmark's font for a title in it, when Cinzel has all its letters (plain Latin);
    /// else the screens' own font (Cinzel has no Vietnamese, Japanese... letters: a few of them
    /// drawn in another font would not match).
    /// </summary>
    public static FontFamily TitleFont(string text) => text.All(c => c < 0x80) ? WordmarkFont : FontFamily.Default;

    private static readonly ColorPaletteResources s_fluentDark = new(), s_fluentLight = new();

    // The theme applied last (for the windows' title bars)
    private static Palette s_current = Of(AppTheme.Shiina);

    /// <summary>Fluent's theme with the palettes Apply sets (the application's styles take it).</summary>
    public static FluentTheme Fluent()
    {
        var fluent = new FluentTheme();
        fluent.Palettes[ThemeVariant.Dark] = s_fluentDark;
        fluent.Palettes[ThemeVariant.Light] = s_fluentLight;
        return fluent;
    }

    /// <summary>The theme's brushes and Fluent's palette, for the whole application.</summary>
    public static void Apply(Application application, AppTheme theme)
    {
        var p = s_current = Of(theme);
        var resources = application.Resources;
        resources[Keys.Background] = new SolidColorBrush(p.Background);
        resources[Keys.Surface] = new SolidColorBrush(p.Surface);
        resources[Keys.SurfaceHigh] = new SolidColorBrush(p.SurfaceHigh);
        resources[Keys.Border] = new SolidColorBrush(p.Border);
        resources[Keys.Ink] = new SolidColorBrush(p.Ink);
        resources[Keys.InkMuted] = new SolidColorBrush(p.InkMuted);
        resources[Keys.Accent] = new SolidColorBrush(p.Accent);
        resources[Keys.OnAccent] = new SolidColorBrush(p.OnAccent);
        resources[Keys.Highlight] = new SolidColorBrush(p.Highlight);
        resources[Keys.OnHighlight] = new SolidColorBrush(p.OnHighlight);
        resources[Keys.WordmarkInk] = new SolidColorBrush(p.WordmarkInk);
        resources[Keys.WordmarkRed] = new SolidColorBrush(p.WordmarkRed);

        // Fluent's colours as its theme editor names them: Alt the ground, Base the ink, Chrome
        // the controls' surfaces, List the rows pointed at and chosen
        var f = p.Dark ? s_fluentDark : s_fluentLight;
        f.Accent = p.Accent;
        f.RegionColor = p.Background;
        // (Fluent's own: the ink and the ground at 100, 80, 60, 40 and 20 % opacity; but BaseLow,
        // the buttons' ground, the theme's own surface: a faded ink made them grey)
        f.BaseHigh = p.Ink;
        f.BaseMediumHigh = Fade(p.Ink, 0xCC);
        f.BaseMedium = Fade(p.Ink, 0x99);
        f.BaseMediumLow = Fade(p.Ink, 0x66);
        f.BaseLow = p.SurfaceHigh;
        f.AltHigh = p.Background;
        f.AltMediumHigh = Fade(p.Background, 0xCC);
        f.AltMedium = Fade(p.Background, 0x99);
        f.AltMediumLow = Fade(p.Background, 0x66);
        f.AltLow = Fade(p.Background, 0x33);
        f.ChromeLow = f.ChromeMediumLow = f.ChromeMedium = p.Surface;
        f.ChromeHigh = f.ChromeDisabledHigh = p.Border;
        f.ChromeDisabledLow = f.ChromeGray = p.InkMuted;
        f.ChromeAltLow = p.Ink;
        // text on the accent (an accent button's), white in Fluent's own palettes
        f.ChromeWhite = p.OnAccent;
        f.ChromeBlackHigh = f.ChromeBlackMedium = f.ChromeBlackMediumLow = p.Dark ? Colors.Black : p.Ink;
        f.ChromeBlackLow = p.Dark ? p.Ink : p.InkMuted;
        f.ListLow = p.Surface;
        f.ListMedium = p.SurfaceHigh;
        f.ErrorText = p.Dark ? C("#FF99A4") : C("#C42B1C");
        application.RequestedThemeVariant = p.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (application.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows)
                TitleBar(window);
    }

    /// <summary>
    /// The window's title bar in the theme's colours before it is shown (its first frame drawn so),
    /// then again when it opens and once it is on the screen (Windows 10 drew the frame in its
    /// own colours now and then otherwise)
    /// </summary>
    public static void Follow(Window window)
    {
        TitleBar(window);
        window.Opened += (_, _) =>
        {
            TitleBar(window);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TitleBar(window), Avalonia.Threading.DispatcherPriority.Background);
        };
    }

    /// <summary>
    /// Windows: the window's title bar in the theme's colours (Windows 11: the page's colour and
    /// the ink; Windows 10: dark or light). Other systems draw their own.
    /// </summary>
    public static void TitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == 0)
            return;
        var p = s_current;
        int dark = p.Dark ? 1 : 0;
        int caption = ColorRef(p.Background), text = ColorRef(p.Ink);
        // DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWA_CAPTION_COLOR, DWMWA_TEXT_COLOR (the last two ignored before Windows 11)
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        // Windows 10 draws the new colours only when the frame is drawn again
        // (SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED)
        _ = SetWindowPos(handle, 0, 0, 0, 0, 0, 0x27);

        static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static Color Fade(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Binds <paramref name="property"/> of <paramref name="target"/> to the theme's brush <paramref name="key"/>.</summary>
    public static T Use<T>(this T target, AvaloniaProperty property, string key) where T : AvaloniaObject
    {
        target.Bind(property, new DynamicResourceExtension(key));
        return target;
    }

    /// <summary>The wordmark, <paramref name="size"/> high (its capitals' size).</summary>
    public static TextBlock Wordmark(double size)
    {
        var mark = new TextBlock { FontFamily = WordmarkFont, FontWeight = FontWeight.Bold, FontSize = size };
        // "OpenShiina": S, the first i and n red
        foreach (var (text, red) in new[] { ("Open", false), ("S", true), ("h", false), ("i", true), ("i", false), ("n", true), ("a", false) })
            mark.Inlines!.Add(new Run(text).Use(TextElement.ForegroundProperty, red ? Keys.WordmarkRed : Keys.WordmarkInk));
        return mark;
    }

    /// <summary>
    /// The theme's surface with <paramref name="amount"/> (0 to 1) of <paramref name="colour"/> in
    /// it (a game's own colour: a deep shade on a dark theme, a pale one on a light theme); the
    /// surface alone when there is no colour. Made again when the theme changes.
    /// </summary>
    public static IBrush Tint(Color? colour, double amount)
    {
        var surface = s_current.Surface;
        if (colour is not { } c)
            return new SolidColorBrush(surface);
        byte Mix(byte from, byte to) => (byte)Math.Round(from + (to - from) * amount);
        return new SolidColorBrush(Color.FromRgb(Mix(surface.R, c.R), Mix(surface.G, c.G), Mix(surface.B, c.B)));
    }

    /// <summary>A theme's own colours whatever theme is applied (the settings' samples of each).</summary>
    public static (Color Background, Color Surface, Color Ink, Color Accent, Color Highlight) Sample(AppTheme theme)
    {
        var p = Of(theme);
        return (p.Background, p.Surface, p.Ink, p.Accent, p.Highlight);
    }

    /// <summary>
    /// Buttons of the class "card": the theme's surface with a hairline round it, lighter with a
    /// gold line when pointed at or pressed (Fluent's button colours otherwise); with "chosen" too,
    /// a gold line always (the settings' theme samples)
    /// </summary>
    public static IEnumerable<IStyle> CardStyles()
    {
        yield return new Style(x => x.OfType<Button>().Class("card"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension(Keys.Surface)),
                new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension(Keys.Border)),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(12)),
            },
        };
        yield return new Style(x => x.OfType<Button>().Class("card").Class("chosen"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension(Keys.Highlight)),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(2)),
            },
        };
        foreach (string state in new[] { ":pointerover", ":pressed" })
            yield return new Style(x => x.OfType<Button>().Class("card").Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension(Keys.SurfaceHigh)),
                    new Setter(ContentPresenter.BorderBrushProperty, new DynamicResourceExtension(Keys.Highlight)),
                },
            };
    }
}
