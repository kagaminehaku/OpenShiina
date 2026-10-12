// OpenShiina Tools: OpenShiinaTools [archive, .S25 file or game folder]. The archive tab browses,
// previews, extracts and repacks a game's archives; the translation tab translates a game (a
// folder given opens there), its tables kept in the game folder's translation\ for the player to
// read (Core Game/ScenarioText.cs).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace OpenShiina.Tools;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ToolsApplication>().UsePlatformDetect().LogToTrace();
}

public sealed class ToolsApplication : Application
{
    /// <summary>The tools' icon (the player's book with a quill) for their windows.</summary>
    private static WindowIcon? Logo()
    {
        using var stream = typeof(ToolsApplication).Assembly.GetManifestResourceStream("OpenShiina.Tools.icon.png");
        return stream == null ? null : new WindowIcon(stream);
    }

    public override void Initialize()
    {
        // The player's theme (its settings), so that both look alike
        Styles.Add(OpenShiina.App.PlayerTheme.Fluent());
        OpenShiina.App.PlayerTheme.Apply(this, OpenShiina.Game.PlayerSettings.Load().Theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var archives = new ArchiveView();
            var translation = new TranslationView();
            var tabs = new TabControl
            {
                Margin = new Thickness(8, 4, 8, 8),
                ItemsSource = new[]
                {
                    new TabItem { Header = "Archives", Content = archives },
                    new TabItem { Header = "Translation", Content = translation },
                },
            };
            var window = new Window
            {
                Title = "OpenShiina Tools",
                Icon = Logo(),
                Width = 1180,
                Height = 760,
                MinWidth = 720,
                MinHeight = 480,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = tabs,
            };
            OpenShiina.App.PlayerTheme.Follow(window);
            window.Closing += (_, e) =>
            {
                if (archives.Busy || translation.Busy)
                {
                    e.Cancel = true;
                    _ = OpenShiina.App.MessageDialog.ShowAsync(window, "Please wait for the work going on to finish.", "OpenShiina Tools", 0);
                    return;
                }
                translation.Save();
                archives.Close();
            };
            desktop.MainWindow = window;
            if (desktop.Args is [var path, ..] && path.Length > 0)
                window.Opened += (_, _) =>
                {
                    if (Directory.Exists(path))
                    {
                        tabs.SelectedIndex = 1;
                        translation.Open(path);
                    }
                    else
                        archives.Open(path);
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
