// OpenShiina for Windows: takes a game folder from the command line or asks for one, finds the
// game by its .exe (Formats.Json), opens its archives and plays the story.
//   OpenShiina.exe [--scn] [game folder]
// --scn runs the game's own SCN scripts instead (approach 2, being built: Scn/ScnWindow).

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
        bool scn = e.Args.Contains("--scn", StringComparer.OrdinalIgnoreCase);
        var rest = e.Args.Where(a => !a.Equals("--scn", StringComparison.OrdinalIgnoreCase)).ToArray();
        string? folder = rest.Length > 0 ? rest[0] : AskFolder();
        if (folder == null)
        {
            Shutdown();
            return;
        }

        try
        {
            var data = await Task.Run(() => OpenGame(folder));
            Window window;
            try
            {
                window = scn ? new Scn.ScnWindow(data) : new PlayerWindow(data);
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
    private static GameData OpenGame(string folder)
    {
        var formats = FormatManager.Instance;
        string archive = Directory.GetFiles(folder, "*.war").FirstOrDefault()
            ?? throw new InvalidDataException("The folder has no .WAR archives.");
        var scheme = formats.LookupGame(archive) is { } name ? formats.GetScheme(name) : null;
        if (scheme == null)
            throw new InvalidDataException("The game was not recognised: keep the game's own .exe in the folder.");
        if (!StoryPlayer.Supports(scheme.Name))
            throw new NotSupportedException($"{scheme.Name} cannot be played yet. Supported now: Oreimo Plus.");
        return GameData.Open(folder, scheme);
    }
}
