// OpenShiina for Windows: the home screen with the games of the library (LibraryWindow), or the
// game folder given on the command line; finds the game by its .exe (Formats.Json), opens its
// archives and runs the game's own SCN scripts (Scn/ScnWindow).
//   OpenShiina.exe [game folder]

using System.IO;
using System.Windows;
using OpenShiina.Archives;

namespace OpenShiina.Windows;

public partial class App : Application
{
    private LibraryWindow? m_library;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            if (!await PlayAsync(e.Args[0]))
                Shutdown();
            return;
        }
        m_library = new LibraryWindow();
        m_library.Play += async folder =>
        {
            m_library.Message = "Opening the game…";
            m_library.IsEnabled = false;
            bool started = await PlayAsync(folder);
            m_library.IsEnabled = true;
            if (started)
                m_library.Close();
        };
        // Closed without a game: the player ends
        m_library.Closed += (_, _) =>
        {
            if (MainWindow is not Scn.ScnWindow)
                Shutdown();
        };
        MainWindow = m_library;
        m_library.Show();
    }

    /// <summary>Opens the game in <paramref name="folder"/> in its window; false (and a message) when it cannot be played.</summary>
    private async Task<bool> PlayAsync(string folder)
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
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            GameLibrary.Played(folder);
            return true;
        }
        catch (Exception ex)
        {
            string message = $"Could not play the game in\n{folder}\n\n{ex.Message}";
            if (m_library != null)
                m_library.Message = message;
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
