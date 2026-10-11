// OpenShiina Tools: OpenShiinaTools [archive or .S25 file]. The archive tab browses, previews,
// extracts and repacks a game's archives; the translation tab exports the scenario text and
// builds a translation from it (Core Game/ScenarioText.cs).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace OpenShiina.Tools;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ToolsApplication>().UsePlatformDetect().LogToTrace();
}

public sealed class ToolsApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var archives = new ArchiveView();
            var translation = new TranslationView();
            var window = new Window
            {
                Title = "OpenShiina Tools",
                Width = 1180,
                Height = 760,
                MinWidth = 720,
                MinHeight = 480,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new TabControl
                {
                    Margin = new Thickness(8, 4, 8, 8),
                    ItemsSource = new[]
                    {
                        new TabItem { Header = "Archives", Content = archives },
                        new TabItem { Header = "Translation", Content = translation },
                    },
                },
            };
            window.Closing += (_, e) =>
            {
                if (archives.Busy || translation.Busy)
                {
                    e.Cancel = true;
                    _ = OpenShiina.App.MessageDialog.ShowAsync(window, "Please wait for the work going on to finish.", "OpenShiina Tools", 0);
                    return;
                }
                archives.Close();
            };
            desktop.MainWindow = window;
            if (desktop.Args is [var path, ..] && path.Length > 0)
                window.Opened += (_, _) => archives.Open(path);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
