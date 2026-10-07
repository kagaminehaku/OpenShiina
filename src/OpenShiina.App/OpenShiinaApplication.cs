// The Avalonia application: on the desktop a window with the game view (its title, full screen
// and size follow the game; a folder on the command line starts that game at once; a game that
// ends goes back to the home screen), on phones and tablets the game view alone.

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
            // A game opens a game pixel to a screen pixel (Avalonia sizes in device-independent
            // units: a screen scaled to 125% would stretch the picture), smaller only where it
            // does not fit the screen's working area, in the middle of that screen
            view.GameSizeChanged += (width, height) =>
            {
                if (window.WindowState != WindowState.Normal)
                    return;
                var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
                double scale = screen?.Scaling ?? window.RenderScaling;
                double w = width / scale, h = height / scale;
                if (screen != null)
                {
                    var area = screen.WorkingArea;
                    double room = Math.Min((area.Width / scale - 16) / w, (area.Height / scale - 48) / h);
                    if (room < 1)
                    {
                        w *= room;
                        h *= room;
                    }
                    window.Width = w;
                    window.Height = h;
                    window.Position = new PixelPoint(area.X + (int)((area.Width - w * scale) / 2), area.Y + (int)((area.Height - h * scale) / 2));
                }
                else
                {
                    window.Width = w;
                    window.Height = h;
                }
            };
            // The X while a game plays goes back to the home screen; on the home screen it closes
            window.Closing += (_, e) =>
            {
                if (view.AllowClose())
                    view.Close();
                else
                    e.Cancel = true;
            };
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
