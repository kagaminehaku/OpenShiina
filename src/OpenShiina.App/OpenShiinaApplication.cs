// The Avalonia application: on the desktop a window with the game view (its title, full screen
// and size follow the game; a folder on the command line starts that game at once), on phones and
// tablets the game view alone.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace OpenShiina.App;

public sealed class OpenShiinaApplication : Application
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
            var view = new GameView();
            var window = new Window
            {
                Title = "OpenShiina",
                Content = view,
                Width = 800,
                Height = 600,
                Background = Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            view.TitleChanged += title => window.Title = title;
            view.FullScreenChanged += full => window.WindowState = full ? WindowState.FullScreen : WindowState.Normal;
            view.GameEnded += window.Close;
            window.Closing += (_, _) => view.Close();
            desktop.MainWindow = window;
            if (desktop.Args is [var folder, ..] && folder.Length > 0)
                window.Opened += async (_, _) => await view.OpenAsync(folder);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new GameView();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
