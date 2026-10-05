// Resources of an installed game for the story player: every .WAR archive of the game folder,
// looked up the way the scripts name them ("e\ev01_00.S25", "v\KIR0001.ogv", ...).
// The directory prefix only tells the engine which archive to search, and the extension may
// differ from the stored one (scripts say "bgm16.ogg" for BGM16.OGV), so files are found by
// their stem.

using System.IO;

namespace OpenShiina.Story;

/// <summary>A decoded picture and where its top-left corner lies on the 800x600 screen.</summary>
public sealed record StageImage(PixelImage Image, int Left, int Top)
{
    /// <summary>File stem of the picture ("EV02_07B"); save thumbnails are chosen by it.</summary>
    public string Name { get; init; } = "";
}

/// <summary>
/// Pictures with a fixed save thumbnail in THSAVE.S25 (file stems, START.SCN 2D173): picture n
/// has frame n, movie n (shown on plane 0) frame 3000 + n, and overlay n (a picture drawn over
/// the thumbnail at its plane position) frame 2000 + n.
/// </summary>
public sealed record ThumbnailTables(string[] Pictures, string[] Movies, string[] Overlays);

public sealed class GameData : IDisposable
{
    private const int CacheSize = 24;

    public string Folder { get; }
    public string SchemeName { get; }

    private readonly List<WarcArchive> m_archives = new();
    private readonly Dictionary<string, List<(WarcArchive Archive, Entry Entry)>> m_byStem = new(StringComparer.OrdinalIgnoreCase);

    // Decoded S25 files, most recently used last
    private readonly object m_cacheLock = new();
    private readonly LinkedList<(string Key, List<S25Frame> Frames)> m_frameCache = new();

    private MontageTable? m_montageTable;
    private Dictionary<string, int>? m_nameWindows;
    private Dictionary<int, S25Frame>? m_system;
    private ThumbnailTables? m_thumbnailTables;

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
            foreach (var path in Directory.GetFiles(folder, "*.war").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
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

    public byte[]? Read(string path, params string[] extensions)
    {
        var found = Find(path, extensions);
        return found == null ? null : WarcOpener.OpenEntry(found.Value.Archive, found.Value.Entry);
    }

    /// <summary>Scenario text by file name ("p\ore01.txt" or "ORE01").</summary>
    public ScenarioScript? LoadScript(string path)
    {
        var bytes = Read(path, ".TXT");
        return bytes == null ? null : ScenarioScript.Parse(Stem(path).ToUpperInvariant(), Encodings.cp932.GetString(bytes));
    }

    /// <summary>
    /// The scene title the writers put in a comment near the top of a scenario file
    /// (";【オープニング】"), or null. Comments ending in "へ" point to the next scene instead.
    /// </summary>
    public string? ScriptTitle(string path)
    {
        var bytes = Read(path, ".TXT");
        if (bytes == null)
            return null;
        foreach (var line in Encodings.cp932.GetString(bytes).Split('\n').Take(40))
        {
            string text = line.Trim();
            if (text.StartsWith(';') && text.EndsWith('】') && text.IndexOf('【') is int open and > 0)
                return text[(open + 1)..^1];
        }
        return null;
    }

    /// <summary>
    /// Audio in a form AudioEngine can read: Ogg Vorbis bytes (from OGV or plain Ogg) or a WAV
    /// (from PAD). Null when missing.
    /// </summary>
    public byte[]? ReadAudio(string path)
    {
        var data = Read(path, ".OGV", ".OGG", ".PAD", ".WAV");
        if (data == null)
            return null;
        if (AudioDecoder.IsOgv(data))
            return AudioDecoder.DecodeOgv(data);
        if (AudioDecoder.IsPad(data))
            return AudioDecoder.DecodePad(data);
        return data;
    }

    /// <summary>Frames of an S25 file (decoded once and kept for a while). Null when missing.</summary>
    public List<S25Frame>? LoadFrames(string path)
    {
        string key = Stem(path).ToUpperInvariant();
        lock (m_cacheLock)
        {
            for (var node = m_frameCache.First; node != null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    m_frameCache.Remove(node);
                    m_frameCache.AddLast(node);
                    return node.Value.Frames;
                }
            }
        }

        var data = Read(path, ".S25");
        if (data == null || !S25Decoder.IsS25(data))
            return null;
        var frames = S25Decoder.DecodeAllFrames(data);

        lock (m_cacheLock)
        {
            m_frameCache.AddLast((key, frames));
            while (m_frameCache.Count > CacheSize)
                m_frameCache.RemoveFirst();
        }
        return frames;
    }

    /// <summary>
    /// A picture as one bitmap: the given slots of a layered S25 drawn over each other, or
    /// its first frame when no slots are given. Null when the file or the slots are missing.
    /// </summary>
    public StageImage? LoadImage(string path, IReadOnlyList<int>? slots = null)
    {
        var frames = LoadFrames(path);
        if (frames == null || frames.Count == 0)
            return null;

        string name = Stem(path).ToUpperInvariant();
        if (slots == null || slots.Count == 0)
        {
            var first = frames.OrderBy(f => f.Slot).First();
            return new StageImage(first.Image, first.OffsetX, first.OffsetY) { Name = name };
        }

        var bySlot = frames.ToDictionary(f => f.Slot);
        var parts = slots.Where(bySlot.ContainsKey).Select(s => bySlot[s]).ToList();
        if (parts.Count == 0)
            return null;
        if (parts.Count == 1)
            return new StageImage(parts[0].Image, parts[0].OffsetX, parts[0].OffsetY) { Name = name };

        var (bitmap, left, top) = S25Composer.ComposeFrames(parts);
        return new StageImage(bitmap, left, top) { Name = name };
    }

    /// <summary>
    /// The save thumbnail tables, read from the data of START.SCN's save function: a list of
    /// "e\....s25" names, an empty string, the "mv\....mpg" movies, an empty string, and the
    /// overlay pictures. Null when the game has no such tables.
    /// </summary>
    public ThumbnailTables? GetThumbnailTables()
    {
        if (m_thumbnailTables != null)
            return m_thumbnailTables.Pictures.Length == 0 ? null : m_thumbnailTables;
        m_thumbnailTables = new ThumbnailTables([], [], []);
        var code = Read("START.SCN", ".SCN");
        if (code == null)
            return null;

        static bool IsName(string s, string ext) =>
            s.Length > 6 && s.IndexOf('\\') is 1 or 2 && s.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && s.All(c => c > ' ' && c < 0x7F);
        string StringAt(int at)
        {
            int end = Array.IndexOf(code, (byte)0, at);
            return end < 0 ? "" : Encodings.cp932.GetString(code, at, end - at);
        }
        List<string> Forward(ref int at, string ext)
        {
            var list = new List<string>();
            for (string s; IsName(s = StringAt(at), ext); at += s.Length + 1)
                list.Add(Stem(s).ToUpperInvariant());
            return list;
        }

        // The picture list ends with ".s25\0\0" and the movie list follows
        var marker = Encodings.cp932.GetBytes(".s25\0\0mv\\");
        int found = code.AsSpan().IndexOf(marker);
        if (found < 0)
            return null;

        // Walk back over the picture names
        var pictures = new List<string>();
        int start = found + 4;      // the 0 after the last name
        while (true)
        {
            int prev = Array.LastIndexOf(code, (byte)0, start - 1);
            if (prev < 0)
                break;
            string s = Encodings.cp932.GetString(code, prev + 1, start - prev - 1);
            if (!IsName(s, ".s25"))
                break;
            pictures.Insert(0, Stem(s).ToUpperInvariant());
            start = prev;
        }
        int next = found + 6;
        var movies = Forward(ref next, ".mpg");
        next++;
        var overlays = Forward(ref next, ".s25");
        m_thumbnailTables = new ThumbnailTables(pictures.ToArray(), movies.ToArray(), overlays.ToArray());
        return pictures.Count == 0 ? null : m_thumbnailTables;
    }

    /// <summary>
    /// Picture for an expression code of "$L_MONT,plane,,x,y,?,M,code", from MONTBL.BIN.
    /// Null when the code is not in the table.
    /// </summary>
    public Montage? GetMontage(int code)
    {
        m_montageTable ??= new MontageTable(Read("MONTBL.BIN", ".BIN") ?? Array.Empty<byte>());
        return m_montageTable.Get(code);
    }

    /// <summary>
    /// Name plate for a speaker, from NWINTBL.BIN (0x24-byte entries: Shift-JIS name, u32
    /// number). The plate is SYSTEM.S25 slot 29 + number. Null for speakers without a plate.
    /// </summary>
    public S25Frame? GetNamePlate(string speaker)
    {
        if (m_nameWindows == null)
        {
            m_nameWindows = new Dictionary<string, int>();
            var table = Read("NWINTBL.BIN", ".BIN");
            for (int at = 0; table != null && at + 0x24 <= table.Length; at += 0x24)
            {
                int end = Array.IndexOf(table, (byte)0, at, 0x20);
                string name = Encodings.cp932.GetString(table, at, (end < 0 ? at + 0x20 : end) - at);
                int number = BitConverter.ToInt32(table, at + 0x20);
                if (name.Length > 0 && number > 0)
                    m_nameWindows[name] = number;
            }
        }
        return m_nameWindows.TryGetValue(speaker, out int n) ? GetSystemFrame(29 + n) : null;
    }

    /// <summary>A frame of the user-interface sheet SYSTEM.S25 by slot (message window = 0).</summary>
    public S25Frame? GetSystemFrame(int slot)
    {
        m_system ??= (LoadFrames("SYSTEM.S25") ?? new List<S25Frame>()).ToDictionary(f => f.Slot);
        return m_system.TryGetValue(slot, out var frame) ? frame : null;
    }

    /// <summary>Full path of a loose file of the game folder (movies: "mv\ev03a.mpg"), or null.</summary>
    public string? LooseFile(string path)
    {
        string full = Path.GetFullPath(Path.Combine(Folder, path.Replace('/', '\\')));
        return full.StartsWith(Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    public void Dispose()
    {
        foreach (var archive in m_archives)
            archive.Dispose();
        m_archives.Clear();
        m_byStem.Clear();
    }
}
