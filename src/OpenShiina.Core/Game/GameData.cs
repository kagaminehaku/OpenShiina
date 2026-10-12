// An installed game: every .WAR archive of the game folder, with files looked up the way the
// scripts name them ("e\ev01_00.S25", "v\KIR0001.ogv", ...). The directory prefix only tells the
// engine which archive to search, and the extension may differ from the stored one (scripts say
// "bgm16.ogg" for BGM16.OGV), so files are found by their stem. Loose files of the game folder
// (RIO.INI, movies) are found by their path. Names are matched without regard to case and with
// either slash, as on Windows, also where the file system cares (Linux, Android).

using System.IO;

namespace OpenShiina.Game;

public sealed class GameData : IDisposable
{
    public string Folder { get; }
    public string SchemeName { get; }

    private readonly List<WarcArchive> m_archives = new();
    private readonly Dictionary<string, List<(WarcArchive Archive, Entry Entry)>> m_byStem = new(StringComparer.OrdinalIgnoreCase);

    private GameData(string folder, string schemeName)
    {
        Folder = folder;
        SchemeName = schemeName;
    }

    /// <summary>Opens every archive of the game folder with <paramref name="scheme"/>.</summary>
    public static GameData Open(string folder, EncryptionScheme scheme)
    {
        var data = new GameData(folder, scheme.Name);
        try
        {
            foreach (var path in Directory.GetFiles(folder, "*.war", s_anyCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var view = new ArcView(path);
                WarcArchive? warc;
                try
                {
                    warc = WarcOpener.TryOpen(view, scheme);
                }
                catch
                {
                    warc = null;
                }
                if (warc == null)
                {
                    view.Dispose();
                    continue;
                }
                data.m_archives.Add(warc);
                foreach (var entry in warc.Entries)
                {
                    string stem = Stem(entry.Name);
                    if (!data.m_byStem.TryGetValue(stem, out var list))
                        data.m_byStem[stem] = list = new();
                    list.Add((warc, entry));
                }
            }
        }
        catch
        {
            data.Dispose();
            throw;
        }
        if (data.m_archives.Count == 0)
            throw new InvalidDataException($"No archive in {folder} could be opened with the {scheme.Name} scheme.");
        return data;
    }

    private static readonly EnumerationOptions s_anyCase = new() { MatchCasing = MatchCasing.CaseInsensitive };

    /// <summary>The first .WAR archive of a game folder (by name, any case), or null.</summary>
    public static string? FindArchive(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.war", s_anyCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;

    /// <summary>
    /// The file a Windows-style relative path names under <paramref name="folder"/>, matching each
    /// part without regard to case; null when there is none or the path leaves the folder.
    /// </summary>
    public static string? ResolvePath(string folder, string path)
    {
        string current = folder;
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part is "." or ".." || part.Contains(':'))
                return null;
            string exact = Path.Combine(current, part);
            bool last = i == parts.Length - 1;
            if (last ? File.Exists(exact) : Directory.Exists(exact))
            {
                current = exact;
                continue;
            }
            if (!Directory.Exists(current))
                return null;
            string? match = (last ? Directory.EnumerateFiles(current) : Directory.EnumerateDirectories(current))
                .FirstOrDefault(p => Path.GetFileName(p).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                return null;
            current = match;
        }
        return parts.Length > 0 ? current : null;
    }

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Trim());

    /// <summary>Finds an entry by a script path; an entry with one of <paramref name="extensions"/> wins.</summary>
    private (WarcArchive Archive, Entry Entry)? Find(string path, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || !m_byStem.TryGetValue(Stem(path), out var list))
            return null;
        string ext = Path.GetExtension(path.Trim());
        foreach (var candidate in list)
            if (Path.GetExtension(candidate.Entry.Name).Equals(ext, StringComparison.OrdinalIgnoreCase))
                return candidate;
        foreach (var candidate in list)
            if (extensions.Any(e => Path.GetExtension(candidate.Entry.Name).Equals(e, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        return list[0];
    }

    /// <summary>A file of the archives by a script path, or null.</summary>
    public byte[]? Read(string path, params string[] extensions)
    {
        var found = Find(path, extensions);
        return found == null ? null : WarcOpener.OpenEntry(found.Value.Archive, found.Value.Entry);
    }

    /// <summary>The size <see cref="Read"/> gives for a script path, from the archive's index (nothing is decoded), or null.</summary>
    public long? Size(string path, params string[] extensions) => Find(path, extensions)?.Entry.UnpackedSize;

    /// <summary>
    /// The size of a file of one archive, named by its file ("BITCHES3_PA.WAR"; the same stem and
    /// extension as <see cref="Find"/> takes), or null when that archive is not open or does not
    /// have the file.
    /// </summary>
    public long? Size(string archive, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !m_byStem.TryGetValue(Stem(path), out var list))
            return null;
        string ext = Path.GetExtension(path.Trim());
        foreach (var (warc, entry) in list)
            if (Path.GetFileName(warc.File.Name).Equals(archive.Trim(), StringComparison.OrdinalIgnoreCase)
                && (ext.Length == 0 || Path.GetExtension(entry.Name).Equals(ext, StringComparison.OrdinalIgnoreCase)))
                return entry.UnpackedSize;
        return null;
    }

    /// <summary>Full path of a loose file of the game folder (movies: "mv\ev03a.mpg"), or null.</summary>
    public string? LooseFile(string path) =>
        ResolveListed(path) ?? (path.Equals("RIO.INI", StringComparison.OrdinalIgnoreCase) ? EngineIni(Folder) : null);

    // The folders of the game folder as first listed (their files and folders by name, any case):
    // the scripts ask for loose files all the time (every file read looks in translation\ first,
    // a missing one included), and listing a folder costs milliseconds on a phone's shared
    // storage. A folder is listed once a session: what is added later is seen the next time
    private readonly Dictionary<string, (Dictionary<string, string> Files, Dictionary<string, string> Folders)?> m_listings = new(StringComparer.Ordinal);

    private (Dictionary<string, string> Files, Dictionary<string, string> Folders)? Listing(string folder)
    {
        lock (m_listings)
        {
            if (m_listings.TryGetValue(folder, out var known))
                return known;
            (Dictionary<string, string>, Dictionary<string, string>)? listing = null;
            try
            {
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(folder))
                    files.TryAdd(Path.GetFileName(file), file);
                foreach (var sub in Directory.EnumerateDirectories(folder))
                    folders.TryAdd(Path.GetFileName(sub), sub);
                listing = (files, folders);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not there, or not readable: no files
            }
            m_listings[folder] = listing;
            return listing;
        }
    }

    /// <summary><see cref="ResolvePath"/> under the game folder, from the folders' listings.</summary>
    private string? ResolveListed(string path)
    {
        string current = Folder;
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part is "." or ".." || part.Contains(':') || Listing(current) is not { } listing)
                return null;
            var names = i == parts.Length - 1 ? listing.Files : listing.Folders;
            if (!names.TryGetValue(part, out var next))
                return null;
            current = next;
        }
        return parts.Length > 0 ? current : null;
    }

    /// <summary>
    /// The engine's settings in <paramref name="folder"/>: RIO.INI, or for older builds the .INI
    /// named as their .exe (Ao no Juuai's aoj.EXE reads aoj.INI): the .INI whose first section is
    /// the engine's ("[椎名里緒 v2.34]"), one named as an .exe first. Asked for as RIO.INI.
    /// </summary>
    public static string? EngineIni(string folder)
    {
        if (ResolvePath(folder, "RIO.INI") is { } rio)
            return rio;
        if (!Directory.Exists(folder))
            return null;
        var exes = Directory.GetFiles(folder, "*.exe", s_anyCase).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.GetFiles(folder, "*.ini", s_anyCase)
            .Where(IsEngineIni)
            .OrderBy(p => exes.Contains(Path.GetFileNameWithoutExtension(p)) ? 0 : 1)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsEngineIni(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encodings.cp932);
            for (string? line; (line = reader.ReadLine()) != null; )
            {
                line = line.Trim();
                if (line.Length != 0 && !line.StartsWith(';'))
                    return line.StartsWith("[椎名里緒", StringComparison.Ordinal);
            }
        }
        catch (IOException)
        {
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var archive in m_archives)
            archive.Dispose();
        m_archives.Clear();
        m_byStem.Clear();
    }
}

/// <summary>
/// Where the player keeps its data: %AppData%\OpenShiina (or OPENSHIINA_DATA), with a folder per
/// kind and game ("scn\Oreimo Plus").
/// </summary>
public static class PlayerFolders
{
    public static string Root =>
        Environment.GetEnvironmentVariable("OPENSHIINA_DATA") is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenShiina");

    public static string For(string kind, string game) => Path.Combine(Root, kind, FolderName(game));

    /// <summary>A game's name as a folder name: characters a file name cannot hold become '_' ("Re: Rem Plus" -> "Re_ Rem Plus").</summary>
    public static string FolderName(string game) =>
        string.Concat(game.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
