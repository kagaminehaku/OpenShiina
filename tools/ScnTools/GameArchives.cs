using System.Text;
using System.Text.RegularExpressions;
using OpenShiina.Archives;
using OpenShiina.Game;
using OpenShiina.IO;

// A game folder's archives opened with its scheme, for the commands that read a game's files
// (arccheck, datefind, files, extract, routines). The scheme is the one the archives' names give
// (GameMap, as the players find it); without one, the scheme SchemeDetector finds decodes them.
sealed class GameArchives : IDisposable
{
    public string Folder { get; }
    public EncryptionScheme Scheme { get; }
    /// <summary>How the scheme was found: "GameMap" or "detected".</summary>
    public string SchemeSource { get; }
    /// <summary>The engine version RIO.INI (or the .INI of an older build) names, as "2.50", or "".</summary>
    public string EngineVersion { get; }
    public List<(string Name, WarcArchive Archive)> Archives { get; } = new();
    /// <summary>The archives that could not be opened with the scheme.</summary>
    public List<string> NotOpened { get; } = new();

    GameArchives(string folder, EncryptionScheme scheme, string source, string version)
    {
        Folder = folder;
        Scheme = scheme;
        SchemeSource = source;
        EngineVersion = version;
    }

    /// <summary>The game's archives, or null (and why) when no scheme decodes them.</summary>
    public static GameArchives? Open(string folder, out string? why, EncryptionScheme? scheme = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        why = null;
        if (GameData.FindArchive(folder) is not { } first)
        {
            why = "no .WAR archive in the folder";
            return null;
        }
        string source = "given";
        if (scheme == null)
        {
            source = "GameMap";
            scheme = FormatManager.Instance.SchemeForArchive(first);
        }
        if (scheme == null)
        {
            source = "detected";
            var found = SchemeDetector.Detect(folder);
            if (found.Count > 0)
                scheme = FormatManager.Instance.GetScheme(found[0].Name);
        }
        if (scheme == null)
        {
            why = "no scheme decodes its archives (no keys for this game yet)";
            return null;
        }
        string ini = GameData.EngineIni(folder) is { } iniPath ? File.ReadAllText(iniPath, Encoding.GetEncoding(932)) : "";
        var game = new GameArchives(folder, scheme, source, Regex.Match(ini, @"v(\d+\.\d+)").Groups[1].Value);
        foreach (var war in Directory.GetFiles(folder, "*.war", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            WarcArchive? warc = null;
            try
            {
                warc = WarcOpener.TryOpen(new ArcView(war), scheme);
            }
            catch
            {
                // counted as not opened
            }
            if (warc == null)
                game.NotOpened.Add(Path.GetFileName(war));
            else
                game.Archives.Add((Path.GetFileName(war), warc));
        }
        return game;
    }

    /// <summary>Every entry with its archive, in the archives' order.</summary>
    public IEnumerable<(string Archive, WarcArchive Warc, Entry Entry)> Entries() =>
        Archives.SelectMany(a => a.Archive.Entries.Select(e => (a.Name, a.Archive, e)));

    /// <summary>WARC flags of an entry (0 for other kinds).</summary>
    public static uint Flags(Entry entry) => entry is WarcEntry w ? w.Flags : 0;

    public void Dispose()
    {
        foreach (var (_, warc) in Archives)
            warc.Dispose();
    }
}
