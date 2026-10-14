// Settings files and loose files of the game folder: the engine's GetPrivateProfile* calls on
// RIO.INI (op_0104 chooses the file, op_0107 reads a number), read here from the bytes the host
// gives for a loose file.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // op_0104: the settings file (0xBCC9F4)
    private string m_iniFile = "";

    /// <summary>
    /// A number from an INI file of the game folder, as GetPrivateProfileInt reads it: the
    /// leading digits of the value (with a sign), or <paramref name="fallback"/> when the
    /// section or key is missing.
    /// </summary>
    public int IniInt(string file, string section, string key, int fallback)
    {
        if (IniString(file, section, key) is not { } text)
            return fallback;
        text = text.Trim();
        int at = 0;
        bool negative = false;
        if (at < text.Length && text[at] is '-' or '+')
            negative = text[at++] == '-';
        long value = 0;
        while (at < text.Length && char.IsAsciiDigit(text[at]))
            value = value * 10 + (text[at++] - '0');
        return (int)(negative ? -value : value);
    }

    /// <summary>A value of an INI file of the game folder, or null.</summary>
    public string? IniString(string file, string section, string key)
    {
        var bytes = m_host.ReadLooseFile(file);
        if (bytes == null)
            return null;
        string? current = null;
        foreach (var raw in Encodings.cp932.GetString(bytes).Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals > 0 && current != null && current.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(equals + 1)..];
        }
        return null;
    }

    // Open files (op_0154): the bytes and the read position
    private readonly Dictionary<int, (byte[] Data, int Position)> m_files = new();
    private int m_nextHandle = 0x100;

    /// <summary>
    /// A path of the scripts: under DataPath it is a save file (the host's save folder; one in a
    /// folder of it, as Ao no Juuai's save\, goes there too), any other absolute path is
    /// refused, and a relative name is a file of the game.
    /// </summary>
    private (bool Save, string Name)? ResolvePath(string path)
    {
        string data = DataPath;
        if (path.StartsWith(data, StringComparison.OrdinalIgnoreCase))
            return (true, path[(path.LastIndexOfAny(['\\', '/']) + 1)..]);
        if (path.Contains(':'))
            return null;
        return (false, path);
    }

    /// <summary>
    /// A file the scripts open for reading: a save file, a file of the archives (kept decoded,
    /// see <see cref="FileCacheBytes"/>) or of the game folder's translation\ (which comes first),
    /// or a loose file. A .TXT that starts with a UTF-8 byte order mark is a translated scenario,
    /// read as Shift-JIS and gaiji (ScnGaiji). The arrays are only read.
    /// </summary>
    public byte[]? ReadScriptFile(string path) => ReadScriptFile(path, load: true);

    /// <param name="load">False when only the length is asked for (a scenario read for it is not the one to put in place).</param>
    private byte[]? ReadScriptFile(string path, bool load)
    {
        if (ResolvePath(path) is not { } p)
            return null;
        if (p.Save)
            return ReadSave(p.Name);
        // "Play from here": the scenario put in place of the one asked for (ScnVm.Warp.cs)
        if (PlayFromScenario(p.Name, load) is { } play)
        {
            LearnScenario(play.Name, play.Data);
            return play.Data;
        }
        if (m_fileCache.TryGetValue(p.Name, out var kept))
        {
            m_fileCacheOrder.Remove(kept);
            m_fileCacheOrder.AddFirst(kept);
            ReadAheadAfter(p.Name);
            if (p.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                LearnScenario(p.Name, kept.Value.Data);
            return kept.Value.Data;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((TakeAhead(p.Name, out string how) ?? ReadArchiveFile(p.Name)) is not { } data)
            return m_host.ReadLooseFile(p.Name);
        ReadAheadAfter(p.Name);
        data = Kept(p.Name, data);
        NoteRead(p.Name, data.Length, started, how);
        if (p.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            LearnScenario(p.Name, data);
        return data;
    }

    /// <summary>
    /// A file of the game folder's translation\ or of the archives as it is (any thread may read
    /// one: the archives read under a lock), or null.
    /// </summary>
    private byte[]? ReadArchiveFile(string name) =>
        m_host.ReadLooseFile(TranslationFolder + name) ?? (TranslationByFileName(name) is { } plain ? m_host.ReadLooseFile(plain) : null)
        ?? m_host.ReadFile(name);

    /// <summary>
    /// A translation's file may also be named as its entry alone (translation\PRO01_01.TXT for
    /// "p\pro01_01.txt"), as the archives find it and OpenShiina Tools writes it; null for a name
    /// without a folder.
    /// </summary>
    private static string? TranslationByFileName(string name) =>
        name.LastIndexOfAny(['\\', '/']) is >= 0 and int at ? TranslationFolder + name[(at + 1)..] : null;

    /// <summary>The size of a translation's file for the name, or null when there is none.</summary>
    private long? TranslationFileSize(string name) =>
        m_host.LooseFileSize(TranslationFolder + name) ?? (TranslationByFileName(name) is { } plain ? m_host.LooseFileSize(plain) : null);

    // The translation folder's tables (text.tsv, names.tsv, strings.tsv: OpenShiina Tools), read
    // on the first scenario or string that may need them; null when there are none
    private TranslationTables? m_translationTables;
    private bool m_translationTablesRead;

    private TranslationTables? Tables
    {
        get
        {
            if (!m_translationTablesRead)
            {
                m_translationTablesRead = true;
                var tables = TranslationTables.Read(name => m_host.ReadLooseFile(TranslationFolder + name));
                m_translationTables = tables.Lines.Count + tables.Names.Count + tables.Strings.Count > 0 ? tables : null;
            }
            return m_translationTables;
        }
    }

    /// <summary>
    /// The translation's files changed while the game plays (OpenShiina Tools saved them): the
    /// tables and strings are read again when next needed, and the scenarios kept go, so that a
    /// scenario the scripts read from now on (the next one, or the one a save loads) has the new
    /// text. The scenario playing keeps the text it was read with. The interpreter's thread.
    /// </summary>
    public void ReloadTranslation()
    {
        m_translationTablesRead = false;
        m_translationTables = null;
        m_strings = null;
        m_playData = null;
        ForgetKept(n => n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The files kept whose names match go (read again when next asked for).</summary>
    private void ForgetKept(Func<string, bool> match)
    {
        foreach (var name in m_fileCache.Keys.Where(match).ToList())
        {
            var kept = m_fileCache[name];
            m_fileCacheOrder.Remove(kept);
            m_fileCache.Remove(name);
            m_fileCacheSize -= kept.Value.Data.Length;
        }
    }

    /// <summary>
    /// 00DD / 00DF: an archive mounted or unmounted. A file in several archives comes from a
    /// mounted one (GameData.Mount), so what was kept or read ahead may now be another archive's.
    /// </summary>
    private void MountArchive(string archive, bool mounted)
    {
        if (!m_host.MountArchive(archive, mounted))
            return;
        ForgetKept(_ => true);
        m_ahead.Clear();
    }

    /// <summary>
    /// A file of the archives read: a scenario translated by text.tsv, a translated scenario
    /// turned into Shift-JIS and gaiji, then kept (the interpreter's thread).
    /// </summary>
    private byte[] Kept(string name, byte[] data)
    {
        data = Translate(name, data);
        if (data.Length <= FileCacheBytes / 2 && !m_fileCache.ContainsKey(name))
        {
            m_fileCache[name] = m_fileCacheOrder.AddFirst((name, data));
            m_fileCacheSize += data.Length;
            while (m_fileCacheSize > FileCacheBytes && m_fileCacheOrder.Last is { } oldest)
            {
                m_fileCacheOrder.RemoveLast();
                m_fileCache.Remove(oldest.Value.Name);
                m_fileCacheSize -= oldest.Value.Data.Length;
            }
        }
        return data;
    }

    /// <summary>A file of the archives as the scripts read it: a scenario translated by text.tsv, a translated scenario in Shift-JIS and gaiji.</summary>
    private byte[] Translate(string name, byte[] data)
    {
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !ScnGaiji.IsUtf8(data)
            && Tables?.Apply(name[(name.LastIndexOfAny(['\\', '/']) + 1)..], data) is { } translated)
            data = translated;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && ScnGaiji.IsUtf8(data))
        {
            data = m_gaiji.Scenario(data);
            if (m_host.Fonts is { } fonts)
                fonts.Gaiji = m_gaiji.Texts;
        }
        return data;
    }

    /// <summary>
    /// The memory the archives' files the scripts read are kept in, decoded, the last read first
    /// (the setting "File cache", Game/FileCacheSize.cs); a file of up to half of it is kept.
    /// Scripts read a character's pictures again at every change of expression (Bitch Nee-chan's
    /// 2.6 MB sheet 28 times in a scene, 10-40 ms each to decrypt and unpack) and an event picture
    /// at every variant of it (its 22 MB e\eva01.s25).
    /// </summary>
    public long FileCacheBytes { get; set; } = 64L << 20;

    // Files of a translation, in place of the archives' files of the same names
    private const string TranslationFolder = "translation\\";
    private readonly ScnGaiji m_gaiji = new();
    private readonly Dictionary<string, LinkedListNode<(string Name, byte[] Data)>> m_fileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Name, byte[] Data)> m_fileCacheOrder = new();
    private long m_fileCacheSize;

    /// <summary>
    /// The length ReadScriptFile gives: from the archive's index for a file of the archives, but
    /// for a .TXT (a translated one's length is its Shift-JIS one) or a file of translation\.
    /// </summary>
    public long? ScriptFileSize(string path)
    {
        // A file on the game's disc in a drive (0A30)
        if (m_disc is { Virtual: false } disc && path.StartsWith(disc.Root, StringComparison.OrdinalIgnoreCase))
            return File.Exists(path) ? new FileInfo(path).Length : null;
        if (ResolvePath(path) is not { } p)
            return null;
        if (p.Save)
            return ReadSave(p.Name)?.Length;
        if (p.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || TranslationFileSize(p.Name) != null)
            return ReadScriptFile(path, load: false)?.Length;
        return m_host.ArchiveFileSize(p.Name) ?? m_host.LooseFileSize(p.Name);
    }

    /// <summary>A file the scripts write: only save files, and only once op_AA82 allowed writes.</summary>
    public bool WriteScriptFile(string path, byte[] data)
    {
        if (!FileWritesEnabled)
            return true;    // the engine reports success and writes nothing
        if (ResolvePath(path) is not { Save: true } p)
            return false;
        WriteSave(p.Name, data);
        return true;
    }

    /// <summary>
    /// The save folder (DataPath) or a folder in it: there, as the engine's GetFileAttributes
    /// finds it (Aneiro's START checks DataPath with 010E, Ao no Juuai's its save\, and they have
    /// kernel32 CreateDirectoryA make it when it is not). A name with a '.' is a file.
    /// </summary>
    private bool IsSaveFolder(string path)
    {
        string folder = DataPath.TrimEnd('\\', '/');
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return false;
        string rest = path[folder.Length..].TrimEnd('\\', '/');
        return rest.Length == 0 || path[folder.Length] is '\\' or '/' && !Path.GetFileName(rest).Contains('.');
    }

    private void RegisterFiles()
    {
        // 010E file, v: whether the file exists - for a file of the archives from their index
        // (decoding it to know took 64 ms for some of Re: Rem Plus's)
        Register(0x010E, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0]));
            vm.Store(c, i.Args[1], vm.ScriptFileSize(path) != null || vm.IsSaveFolder(path) ? 1 : 0);
            return 0;
        });
        // 0158 file, size, packed size: from the archive's index, as the engine does (START reads
        // the size before every 00CE; decoding the file for it took as long as the load itself)
        Register(0x0158, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0]));
            if ((vm.VirtualDiscSize(c, i, path) ?? vm.ScriptFileSize(path)) is not { } size)
                return 2;
            vm.Store(c, i.Args[1], (int)size);
            vm.Store(c, i.Args[2], (int)size);
            return 0;
        });
        // 0154 file, access, offset, size, packed size, handle: open for reading
        Register(0x0154, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0]));
            vm.Value(c, i.Args[1]);
            if (vm.ReadScriptFile(path) is not { } bytes)
                return 2;
            int handle = vm.m_nextHandle++;
            vm.m_files[handle] = (bytes, 0);
            vm.Store(c, i.Args[2], 0);
            vm.Store(c, i.Args[3], bytes.Length);
            vm.Store(c, i.Args[4], bytes.Length);
            vm.Store(c, i.Args[5], handle);
            return 0;
        });
        // 0156 handle, buffer, n, v: ReadFile; v = the bytes read
        Register(0x0156, (vm, c, i) =>
        {
            int handle = vm.Value(c, i.Args[0]), buffer = vm.Value(c, i.Args[1]), n = vm.Value(c, i.Args[2]);
            if (!vm.m_files.TryGetValue(handle, out var file))
                return 2;
            int count = Math.Clamp(n, 0, file.Data.Length - file.Position);
            vm.WriteBytes(buffer, file.Data.AsSpan(file.Position, count));
            vm.m_files[handle] = (file.Data, file.Position + count);
            vm.Store(c, i.Args[3], count);
            return 0;
        });
        // 0155 handle: CloseHandle
        Register(0x0155, (vm, c, i) => { vm.m_files.Remove(vm.Value(c, i.Args[0])); return 0; });
        // 0123 folder, file: DeleteFile (a damaged save file)
        Register(0x0123, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0])) + vm.ReadString(vm.Value(c, i.Args[1]));
            if (vm.ResolvePath(path) is { Save: true } p && vm.FileWritesEnabled)
                vm.DeleteSave(p.Name);
            return 0;
        });
        // 00D2 buffer, n, file: write a file
        Register(0x00D2, (vm, c, i) =>
        {
            int buffer = vm.Value(c, i.Args[0]), n = vm.Value(c, i.Args[1]);
            string path = vm.ReadString(vm.Value(c, i.Args[2]));
            return vm.WriteScriptFile(path, vm.ReadBytes(buffer, Math.Max(0, n))) ? 0 : 2;
        });

        // 0104 file: the settings file for 0107
        Register(0x0104, (vm, c, i) => { vm.m_iniFile = vm.ReadString(vm.Value(c, i.Args[0])); return 0; });
        // 0107 section, key, v: v = the number in the settings file, v's own value when missing
        Register(0x0107, (vm, c, i) =>
        {
            string section = vm.ReadString(vm.Value(c, i.Args[0]));
            string key = vm.ReadString(vm.Value(c, i.Args[1]));
            int fallback = vm.Value(c, i.Args[2]);
            vm.Store(c, i.Args[2], vm.IniInt(vm.m_iniFile, section, key, fallback));
            return 0;
        });
    }
}
