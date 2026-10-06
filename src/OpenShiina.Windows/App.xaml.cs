// OpenShiina for Windows: takes a game folder from the command line or asks for one, finds the
// game by its .exe (Formats.Json), opens its archives and runs the game's own SCN scripts
// (approach 2, Scn/ScnWindow).
//   OpenShiina.exe [--story] [game folder]
// --story plays it with the story engine and this player's own screens instead (approach 1.5,
// Player/PlayerWindow; Oreimo Plus only).

using System.IO;
using System.Windows;
using Microsoft.Win32;
using OpenShiina.Archives;

namespace OpenShiina.Windows;

public partial class App : Application
{
    // The folder played last, offered first the next time
    private static string LastFolderFile => Path.Combine(PlayerFolders.Root, "last-game.txt");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool story = e.Args.Contains("--story", StringComparer.OrdinalIgnoreCase);
        var rest = e.Args.Where(a => !a.Equals("--story", StringComparison.OrdinalIgnoreCase)).ToArray();
        string? folder = rest.Length > 0 ? rest[0] : AskFolder();
        if (folder == null)
        {
            Shutdown();
            return;
        }

        try
        {
            var data = await Task.Run(() => OpenGame(folder, story));
            Window window;
            try
            {
                window = story ? new PlayerWindow(data) : new Scn.ScnWindow(data);
            }
            catch
            {
                data.Dispose();
                throw;
            }
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            RememberFolder(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not play the game in\n{folder}\n\n{ex.Message}", "OpenShiina", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        }
    }

    private static string? AskFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder of an installed game (where its .exe and .WAR files are)" };
        try
        {
            if (File.Exists(LastFolderFile) && File.ReadAllText(LastFolderFile).Trim() is { Length: > 0 } last && Directory.Exists(last))
                dialog.InitialDirectory = last;
        }
        catch
        {
            // No remembered folder
        }
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static void RememberFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(PlayerFolders.Root);
            File.WriteAllText(LastFolderFile, folder);
        }
        catch
        {
            // Only a convenience
        }
    }

    /// <summary>Recognises the game in <paramref name="folder"/> and opens its archives.</summary>
    private static GameData OpenGame(string folder, bool story)
    {
        var formats = FormatManager.Instance;
        string archive = Directory.GetFiles(folder, "*.war").FirstOrDefault()
            ?? throw new InvalidDataException("The folder has no .WAR archives.");
        var scheme = formats.LookupGame(archive) is { } name ? formats.GetScheme(name) : null;
        if (scheme == null)
            throw new InvalidDataException("The game was not recognised: keep the game's own .exe in the folder.");
        if (story && !StoryPlayer.Supports(scheme.Name))
            throw new NotSupportedException($"{scheme.Name} cannot be played with --story. Supported: Oreimo Plus.");
        return GameData.Open(folder, scheme);
    }
}
