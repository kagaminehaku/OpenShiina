// The games the players have opened, for their home screen: library.json in the player folder
// (PlayerFolders.Root), shared by both players. A game is its folder, recognised by its .exe as
// when it is opened (FormatManager's GameMap), with the name of its scheme and when it was
// played last. The folder played last before the library existed (last-game.txt) is taken in
// the first time the library is read.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShiina.Archives;

namespace OpenShiina.Game;

/// <summary>A game of the library: its folder, its name, its .exe (for the icon) and when it was added and played last.</summary>
public sealed record LibraryGame(string Folder, string Name, string? Exe, DateTime Added, DateTime? LastPlayed)
{
    /// <summary>The folder is still there (a drive may be gone, a folder renamed).</summary>
    [JsonIgnore]
    public bool Available => Directory.Exists(Folder);

    /// <summary>Where the players keep the game's saves.</summary>
    [JsonIgnore]
    public string SaveFolder => PlayerFolders.For("scn", Name);
}

public static class GameLibrary
{
    private static string LibraryFile => Path.Combine(PlayerFolders.Root, "library.json");
    private static string LastFolderFile => Path.Combine(PlayerFolders.Root, "last-game.txt");

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>The games, the one played last first (then by name).</summary>
    public static List<LibraryGame> Load()
    {
        var games = Read();
        if (games == null)
        {
            games = [];
            // The folder the players remembered before the library
            try
            {
                if (File.Exists(LastFolderFile) && File.ReadAllText(LastFolderFile).Trim() is { Length: > 0 } last
                    && Identify(last) is { } found)
                {
                    games.Add(new LibraryGame(Path.TrimEndingDirectorySeparator(Path.GetFullPath(last)), found.Name, found.Exe, DateTime.Now, null));
                    Write(games);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only a convenience
            }
        }
        return Sorted(games);
    }

    /// <summary>
    /// The game in <paramref name="folder"/>: the name of its scheme and its .exe, or null when it
    /// is not one the players know (no .WAR archives, or no .exe they recognise for archives with
    /// keys; WARC 1.0 / 1.1 games are named after their folder).
    /// </summary>
    public static (string Name, string? Exe)? Identify(string folder)
    {
        if (GameData.FindArchive(folder) is not { } archive)
            return null;
        var formats = FormatManager.Instance;
        if (formats.SchemeForArchive(archive) is not { } found)
            return null;
        string name = found.Name;
        string? exe = null;
        foreach (var (file, scheme) in formats.GameMap)
            if (scheme == name && GameData.ResolvePath(folder, file) is { } path)
            {
                exe = path;
                break;
            }
        return (name, exe);
    }

    /// <summary>Adds the game in <paramref name="folder"/> (or finds it again); null when it is not recognised.</summary>
    public static LibraryGame? Add(string folder) => Update(folder, played: false);

    /// <summary>The game in <paramref name="folder"/> was played now: it is added if it was not there.</summary>
    public static LibraryGame? Played(string folder) => Update(folder, played: true);

    /// <summary>Takes the game out of the list (its files and saves stay).</summary>
    public static void Remove(string folder)
    {
        var games = Read() ?? [];
        if (games.RemoveAll(g => SameFolder(g.Folder, folder)) > 0)
            Write(games);
    }

    private static LibraryGame? Update(string folder, bool played)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Identify(folder) is not { } found)
            return null;
        var games = Read() ?? [];
        int at = games.FindIndex(g => SameFolder(g.Folder, folder));
        var game = at >= 0
            ? games[at] with { Name = found.Name, Exe = found.Exe }
            : new LibraryGame(folder, found.Name, found.Exe, DateTime.Now, null);
        if (played)
            game = game with { LastPlayed = DateTime.Now };
        if (at >= 0)
            games[at] = game;
        else
            games.Add(game);
        Write(games);
        return game;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static List<LibraryGame> Sorted(List<LibraryGame> games) =>
        games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The list as saved, or null when there is none yet (or it cannot be read).</summary>
    private static List<LibraryGame>? Read()
    {
        try
        {
            return File.Exists(LibraryFile)
                ? JsonSerializer.Deserialize<List<LibraryGame>>(File.ReadAllText(LibraryFile), s_json) ?? []
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write(List<LibraryGame> games)
    {
        try
        {
            Directory.CreateDirectory(PlayerFolders.Root);
            // A whole file or the old one: written next to it, then put in its place
            string temp = LibraryFile + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(Sorted(games), s_json));
            File.Move(temp, LibraryFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The list is a convenience: playing goes on without it
        }
    }
}
