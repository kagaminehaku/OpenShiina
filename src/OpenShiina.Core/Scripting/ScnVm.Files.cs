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
    /// A path of the scripts: under DataPath it is a save file (the host's save folder), any
    /// other absolute path is refused, and a relative name is a file of the game.
    /// </summary>
    private (bool Save, string Name)? ResolvePath(string path)
    {
        string data = DataPath;
        if (path.StartsWith(data, StringComparison.OrdinalIgnoreCase))
            return (true, path[data.Length..]);
        if (path.Contains(':'))
            return null;
        return (false, path);
    }

    /// <summary>A file the scripts open for reading: a save file, a file of the archives, or a loose file.</summary>
    public byte[]? ReadScriptFile(string path)
    {
        if (ResolvePath(path) is not { } p)
            return null;
        if (p.Save)
            return m_host.ReadSaveFile(p.Name);
        return m_host.ReadFile(p.Name) ?? m_host.ReadLooseFile(p.Name);
    }

    /// <summary>The length ReadScriptFile gives, from the archive's index for a file of the archives.</summary>
    public long? ScriptFileSize(string path)
    {
        if (ResolvePath(path) is not { } p)
            return null;
        if (p.Save)
            return m_host.ReadSaveFile(p.Name)?.Length;
        return m_host.ArchiveFileSize(p.Name) ?? m_host.ReadLooseFile(p.Name)?.Length;
    }

    /// <summary>A file the scripts write: only save files, and only once op_AA82 allowed writes.</summary>
    public bool WriteScriptFile(string path, byte[] data)
    {
        if (!FileWritesEnabled)
            return true;    // the engine reports success and writes nothing
        if (ResolvePath(path) is not { Save: true } p)
            return false;
        m_host.WriteSaveFile(p.Name, data);
        return true;
    }

    private void RegisterFiles()
    {
        // 010E file, v: whether the file exists - for a file of the archives from their index
        // (decoding it to know took 64 ms for some of Re: Rem Plus's)
        Register(0x010E, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0]));
            vm.Store(c, i.Args[1], vm.ScriptFileSize(path) != null ? 1 : 0);
            return 0;
        });
        // 0158 file, size, packed size: from the archive's index, as the engine does (START reads
        // the size before every 00CE; decoding the file for it took as long as the load itself)
        Register(0x0158, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0]));
            if (vm.ScriptFileSize(path) is not { } size)
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
                vm.m_host.DeleteSaveFile(p.Name);
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
