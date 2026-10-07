// OpenShiina for Windows: the home screen with the games of the library (LibraryWindow), or the
// game folder given on the command line; finds the game by its .exe (Formats.Json), opens its
// archives and runs the game's own SCN scripts (Scn/ScnWindow). A game window that closes brings
// the home screen back; closing the home screen ends the player.
//   OpenShiina.exe [game folder]

using System.IO;
using System.Windows;
using OpenShiina.Archives;

namespace OpenShiina.Windows;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The GPU mode (Settings): Vulkan compute, made when a game first asks for it
        PlayerSettings.AcceleratorFactory = () => (Gpu.VulkanAccelerator.TryCreate(out string? error), error);
        if (e.Args.Length > 0 && await PlayAsync(e.Args[0], null))
            return;
        ShowLibrary();
    }

    /// <summary>The home screen; closing it (without starting a game) ends the player.</summary>
    private void ShowLibrary()
    {
        var library = new LibraryWindow();
        bool playing = false;
        library.Play += async folder =>
        {
            library.Message = "Opening the game…";
            library.IsEnabled = false;
            playing = await PlayAsync(folder, library);
            library.IsEnabled = true;
            if (playing)
                library.Close();
        };
        library.Closed += (_, _) =>
        {
            if (!playing)
                Shutdown();
        };
        MainWindow = library;
        library.Show();
    }

    /// <summary>
    /// Opens the game in <paramref name="folder"/> in its window, which goes back to the home
    /// screen when it closes; false (and a message) when it cannot be played.
    /// </summary>
    private async Task<bool> PlayAsync(string folder, LibraryWindow? library)
    {
        try
        {
            var data = await Task.Run(() => OpenGame(folder));
            Window window;
            try
            {
                window = new Scn.ScnWindow(data);
            }
            catch
            {
                data.Dispose();
                throw;
            }
            window.Closed += (_, _) => ShowLibrary();
            MainWindow = window;
            window.Show();
            GameLibrary.Played(folder);
            return true;
        }
        catch (Exception ex)
        {
            string message = $"Could not play the game in\n{folder}\n\n{ex.Message}";
            if (library != null)
                library.Message = message;
            else
                MessageBox.Show(message, "OpenShiina", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>Recognises the game in <paramref name="folder"/> and opens its archives.</summary>
    private static GameData OpenGame(string folder)
    {
        var formats = FormatManager.Instance;
        string archive = GameData.FindArchive(folder)
            ?? throw new InvalidDataException("The folder has no .WAR archives.");
        var scheme = formats.LookupGame(archive) is { } name ? formats.GetScheme(name) : null;
        if (scheme == null)
            throw new InvalidDataException("The game was not recognised: keep the game's own .exe in the folder.");
        return GameData.Open(folder, scheme);
    }
}
