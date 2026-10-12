// The games the players have opened, for their home screen: library.json in the player folder
// (PlayerFolders.Root), shared by both players. A game is its folder, recognised by its .exe as
// when it is opened (FormatManager's GameMap), with the name of its scheme and when it was
// played last. A game whose .exe is not in GameMap keeps the scheme found for it
// (SchemeDetector, or the user's choice among those that fit). The folder played last before
// the library existed (last-game.txt) is taken in the first time the library is read. Each game
// keeps the time it was played (in front, counted by the player) and the size its folder had
// when last measured. Delete takes a game's folder off the disk, after checks that it is the
// game's own.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShiina.Archives;

namespace OpenShiina.Game;

/// <summary>
/// A game of the library: its folder, its name, its .exe (for the icon), when it was added and
/// played last, the scheme chosen for it when its .exe does not tell (else null), the seconds it
/// was played (since the players count it) and its folder's size when last measured (null: not yet).
/// </summary>
public sealed record LibraryGame(string Folder, string Name, string? Exe, DateTime Added, DateTime? LastPlayed, string? Scheme = null,
                                 long PlaySeconds = 0, long? Bytes = null)
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
    /// keys and no scheme chosen for it; WARC 1.0 / 1.1 games are named after their folder).
    /// <paramref name="chosen"/>: a scheme for a game its .exe does not tell (else the one the
    /// library keeps for the folder).
    /// </summary>
    public static (string Name, string? Exe, bool Chosen)? Identify(string folder, string? chosen = null)
    {
        if (GameData.FindArchive(folder) is not { } archive)
            return null;
        var formats = FormatManager.Instance;
        if (formats.SchemeForArchive(archive) is { } found)
        {
            string name = found.Name;
            foreach (var (file, scheme) in formats.GameMap)
                if (scheme == name && GameData.ResolvePath(folder, file) is { } path)
                    return (name, path, false);
            return (name, null, false);
        }
        chosen ??= Read()?.FirstOrDefault(g => SameFolder(g.Folder, folder))?.Scheme;
        if (chosen == null || formats.GetScheme(chosen) is not { } scheme2)
            return null;
        return (scheme2.Name, MainExe(folder), true);
    }

    /// <summary>
    /// The scheme to open the game in <paramref name="folder"/> with: the one its .exe tells, else
    /// the one chosen for it; null when neither.
    /// </summary>
    public static EncryptionScheme? SchemeFor(string folder)
    {
        if (GameData.FindArchive(folder) is not { } archive)
            return null;
        if (FormatManager.Instance.SchemeForArchive(archive) is { } scheme)
            return scheme;
        return Read()?.FirstOrDefault(g => SameFolder(g.Folder, folder))?.Scheme is { } chosen
            ? FormatManager.Instance.GetScheme(chosen)
            : null;
    }

    /// <summary>The game's own .exe for the icon, when the folder has one (the largest, installers apart).</summary>
    private static string? MainExe(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Where(f => !Path.GetFileNameWithoutExtension(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileNameWithoutExtension(f).StartsWith("setup", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the game in <paramref name="folder"/> (or finds it again); null when it is not
    /// recognised. <paramref name="scheme"/>: the scheme chosen for a game its .exe does not tell.
    /// </summary>
    public static LibraryGame? Add(string folder, string? scheme = null) => Update(folder, played: false, scheme);

    /// <summary>The game in <paramref name="folder"/> was played now: it is added if it was not there.</summary>
    public static LibraryGame? Played(string folder) => Update(folder, played: true);

    /// <summary>Takes the game out of the list (its files and saves stay).</summary>
    public static void Remove(string folder)
    {
        var games = Read() ?? [];
        if (games.RemoveAll(g => SameFolder(g.Folder, folder)) > 0)
            Write(games);
    }

    /// <summary>Puts a game taken out of the list back as it was (Undo).</summary>
    public static void Restore(LibraryGame game)
    {
        var games = Read() ?? [];
        if (games.Any(g => SameFolder(g.Folder, game.Folder)))
            return;
        games.Add(game);
        Write(games);
    }

    /// <summary>Adds <paramref name="time"/> to the game's play time.</summary>
    public static void AddPlayTime(string folder, TimeSpan time)
    {
        long seconds = (long)Math.Round(time.TotalSeconds);
        if (seconds > 0)
            Change(folder, g => g with { PlaySeconds = g.PlaySeconds + seconds });
    }

    /// <summary>The size of the game's folder, measured now.</summary>
    public static void SetBytes(string folder, long bytes) => Change(folder, g => g with { Bytes = bytes });

    private static void Change(string folder, Func<LibraryGame, LibraryGame> change)
    {
        if (Read() is not { } games)
            return;
        int at = games.FindIndex(g => SameFolder(g.Folder, folder));
        if (at < 0)
            return;
        var changed = change(games[at]);
        if (changed == games[at])
            return;
        games[at] = changed;
        Write(games);
    }

    /// <summary>The bytes of the files under <paramref name="folder"/> (0 when it cannot be read).</summary>
    public static long FolderBytes(string folder)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes the game's folder (and its saves when <paramref name="saves"/>) and takes it out of
    /// the list; null when done, else why not. Only a folder with the game's .WAR archives in it,
    /// never a drive, a user's or the system's own folder, one that holds the player's folder or
    /// another game of the list.
    /// </summary>
    public static string? Delete(LibraryGame game, bool saves)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.Folder));
        if (!Directory.Exists(folder))
            return "The folder is not there any more.";
        if (GameData.FindArchive(folder) == null)
            return "The folder has no .WAR archives in it any more, so it was left alone.";
        if (Path.GetPathRoot(folder) is not { } root || SameFolder(root, folder))
            return "The folder is a whole drive: it was left alone.";
        var guarded = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
        };
        if (guarded.Select(Environment.GetFolderPath).Any(p => p.Length > 0 && SameFolder(p, folder)))
            return "The folder is one of the system's own: it was left alone.";
        if (Inside(PlayerFolders.Root, folder))
            return "The folder holds OpenShiina's own (saves, settings): it was left alone.";
        if ((Read() ?? []).Any(g => !SameFolder(g.Folder, folder) && Inside(g.Folder, folder)))
            return "The folder holds another game of the list: it was left alone.";
        try
        {
            Directory.Delete(folder, recursive: true);
            if (saves && Directory.Exists(game.SaveFolder))
                Directory.Delete(game.SaveFolder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Remove(folder);
            return $"Not everything could be deleted: {e.Message}";
        }
        Remove(folder);
        return null;
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or under it.</summary>
    private static bool Inside(string path, string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(full, parent, comparison) || full.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    private static LibraryGame? Update(string folder, bool played, string? scheme = null)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Identify(folder, scheme) is not { } found)
            return null;
        var games = Read() ?? [];
        int at = games.FindIndex(g => SameFolder(g.Folder, folder));
        string? chosen = found.Chosen ? found.Name : null;
        var game = at >= 0
            ? games[at] with { Name = found.Name, Exe = found.Exe, Scheme = chosen }
            : new LibraryGame(folder, found.Name, found.Exe, DateTime.Now, null, chosen);
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
