// OpenShiina on the desktop: OpenShiina [game folder]. Without a folder it asks for one.
// OPENSHIINA_WAYLAND=1 uses Avalonia's own Wayland backend on Linux where Wayland runs (else X11).

using Avalonia;
using OpenShiina.App;

namespace OpenShiina.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<OpenShiinaApplication>().UsePlatformDetect().LogToTrace();
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("OPENSHIINA_WAYLAND") == "1")
            builder = builder.UseWaylandWithFallback();
        return builder;
    }
}
