// Small system opcodes met on the way into the game: memory status, DirectInput keys, the
// background file loader, the disc check and the extra data folder.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>
    /// The disc 0A30 found: the root of its drive ("E:\"), which the scripts put before its files'
    /// names to ask for their size (0158); Virtual when the player answers for a disc that is not
    /// there (see 0A30).
    /// </summary>
    private (string Root, bool Virtual)? m_disc;

    /// <summary>
    /// The CD / DVD drive of the machine holding the file (Windows; a mounted disc image is one
    /// too), else a disc of the player's own, seen as drive Q: (any letter: the scripts only put it
    /// before the names).
    /// </summary>
    private static (string Root, bool Virtual) DiscHolding(string file)
    {
        if (OperatingSystem.IsWindows() && file.Length > 0)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.CDRom && drive.IsReady && File.Exists(Path.Combine(drive.RootDirectory.FullName, file)))
                        return (drive.RootDirectory.FullName, false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A drive that cannot be read holds no disc for the game
                }
            }
        }
        return (@"Q:\", true);
    }

    // Instructions looked through after 0158 for the comparison of the size it gave
    private const int DiscCheckLookAhead = 8;

    /// <summary>
    /// The size 0158 gives a file on the player's own disc, whatever its name: the number the
    /// scripts then compare the size with, so that the check holds (Can Fes!'s START: "0158 path,
    /// l[0], l[1]", then "if l[0] == 1744830464"). The next few instructions are looked through,
    /// up to a jump, for an if (01F4 unsigned, 01FE signed) between a variable 0158 set and a
    /// number: == >= <= give the number, > one more, < one less, != the number too (a script asks
    /// again when the size is not the one it wants), "&" the number, "|" 1. Without one, 1: the
    /// file is there. Null when the path is not on that disc.
    /// </summary>
    private int? VirtualDiscSize(ScnContext c, ScnInstruction i, string path)
    {
        if (m_disc is not { Virtual: true } disc || !path.StartsWith(disc.Root, StringComparison.OrdinalIgnoreCase))
            return null;
        int at = i.Address + i.Length;
        for (int n = 0; n < DiscCheckLookAhead; n++)
        {
            ScnInstruction next;
            try
            {
                next = Decode(c, at);
            }
            catch (ScnException)
            {
                // data after the code
                break;
            }
            at += next.Length;
            if (next.Op is 0x01F4 or 0x01FE && next.Args.Length >= 2)
            {
                bool left = Sets(next.Args[0]), right = Sets(next.Args[1]);
                if (left == right)
                    continue;
                int number = Value(c, next.Args[left ? 1 : 0]);
                // the comparison as "size cmp number"
                int cmp = left ? next.Raw[0] : next.Raw[0] switch { 2 => 4, 3 => 5, 4 => 2, 5 => 3, var same => same };
                return cmp switch
                {
                    3 => number + 1,
                    5 => number - 1,
                    7 => 1,
                    _ => number,
                };
            }
            // a jump, call or end: the check goes on elsewhere
            if (next.Targets.Length > 0 || next.Op is 0x0000 or 0x0258 or 0x0267 or 0x026D)
                break;
        }
        return 1;

        bool Sets(ScnOperand operand) => i.Args.Skip(1).Any(a => a == operand);
    }

    // DirectInput scan code -> virtual key, for op_03EC (main keys only)
    private static readonly Dictionary<int, int> s_dikToVk = BuildDikTable();

    private static Dictionary<int, int> BuildDikTable()
    {
        var t = new Dictionary<int, int>
        {
            [0x01] = 0x1B, [0x0E] = 0x08, [0x0F] = 0x09, [0x1C] = 0x0D, [0x1D] = 0x11, [0x2A] = 0x10,
            [0x36] = 0x10, [0x38] = 0x12, [0x39] = 0x20, [0x9C] = 0x0D, [0x9D] = 0x11, [0xB8] = 0x12,
            [0xC7] = 0x24, [0xC8] = 0x26, [0xC9] = 0x21, [0xCB] = 0x25, [0xCD] = 0x27, [0xCF] = 0x23,
            [0xD0] = 0x28, [0xD1] = 0x22, [0xD2] = 0x2D, [0xD3] = 0x2E,
            [0x47] = 0x67, [0x48] = 0x68, [0x49] = 0x69, [0x4B] = 0x64, [0x4C] = 0x65, [0x4D] = 0x66,
            [0x4F] = 0x61, [0x50] = 0x62, [0x51] = 0x63, [0x52] = 0x60,
        };
        // digits 1-9, 0 and the letter rows
        for (int k = 0; k < 9; k++)
            t[0x02 + k] = '1' + k;
        t[0x0B] = '0';
        foreach (var (start, letters) in new[] { (0x10, "QWERTYUIOP"), (0x1E, "ASDFGHJKL"), (0x2C, "ZXCVBNM") })
            for (int k = 0; k < letters.Length; k++)
                t[start + k] = letters[k];
        for (int k = 0; k < 10; k++)
            t[0x3B + k] = 0x70 + k;      // F1-F10
        t[0x57] = 0x7A;
        t[0x58] = 0x7B;
        return t;
    }

    // op_00CE: the background loader (0x7DCD48: busy, data). With LoadInBackground a file not
    // kept yet is read on another thread while the scripts go on (START polls 00CF and draws its
    // frames meanwhile), and taken into script memory when 00D0 / 00D1 / the next 00CE asks
    private int m_loaded;
    private (string Name, Task<byte[]?> Read)? m_loading;

    /// <summary>
    /// The background loader (00CE) reads on another thread, as the engine's does: a scene
    /// change goes on drawing while a large picture loads. The players set it; tests keep the
    /// frames the same from run to run without it.
    /// </summary>
    public bool LoadInBackground { get; set; }

    /// <summary>The loading file, waited for and taken into script memory (0 when it could not be read).</summary>
    private void FinishLoading()
    {
        if (m_loading is not { } loading)
            return;
        m_loading = null;
        byte[]? data;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string how = loading.Read.IsCompleted ? ", background loader" : ", background loader, waited for";
        try
        {
            data = loading.Read.Result;
        }
        catch (AggregateException)
        {
            data = null;
        }
        if (data == null)
        {
            m_loaded = 0;
            return;
        }
        data = Kept(loading.Name, data);
        NoteRead(loading.Name, data.Length, started, how);
        ReadAheadAfter(loading.Name);
        if (loading.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            LearnScenario(loading.Name, data);
        Loaded(data);
    }

    private void Loaded(byte[] data)
    {
        m_loaded = AllocateCopy(data);
        m_fileSizes[m_loaded] = data.Length;
        EngineGlobals[0x4C4514] = data.Length;
    }

    private void RegisterMisc()
    {
        // 02C1 n, v: field n of GlobalMemoryStatus (3 = available physical memory); a current
        // PC has more than the 2 GB the call reports
        Register(0x02C1, (vm, c, i) =>
        {
            int n = vm.Value(c, i.Args[0]);
            int value = n switch { 0 => 0x20, 1 => 50, _ => 0x7FFFFFFF };
            vm.Store(c, i.Args[1], value);
            return 0;
        });
        // 03EC key, v: a DirectInput key (scan code) with key repeat, 0 when the window is inactive
        Register(0x03EC, (vm, c, i) =>
        {
            int key = vm.Value(c, i.Args[0]);
            if (!vm.m_host.Active)
            {
                vm.Store(c, i.Args[1], 0);
                return 0;
            }
            int held = s_dikToVk.TryGetValue(key & 0xFF, out int vk) && vm.m_host.KeyDown(vk) ? 0x80 : 0;
            vm.Store(c, i.Args[1], vm.RepeatButtons(key, held));
            return 0;
        });
        // 00CE file: load a file in the background (at once without LoadInBackground, or when it
        // is kept already); 00CF v: still loading (0 when done); 00D0 v: the data; 00D1: wait for
        // the loader
        Register(0x00CE, (vm, c, i) =>
        {
            string file = vm.ReadString(vm.Value(c, i.Args[0]));
            vm.FinishLoading();
            if (vm.LoadInBackground && vm.ResolvePath(file) is { Save: false } p && !vm.m_fileCache.ContainsKey(p.Name)
                && vm.m_host.ArchiveFileSize(p.Name) != null)
            {
                vm.m_loaded = 0;
                vm.m_loading = (p.Name, vm.TakeAheadRead(p.Name) ?? Task.Run(() => vm.ReadArchiveFile(p.Name)));
                return 0;
            }
            if (vm.ReadScriptFile(file) is not { } data)
                return 2;
            vm.Loaded(data);
            return 0;
        });
        Register(0x00CF, (vm, c, i) => { vm.Store(c, i.Args[0], vm.m_loading is { Read.IsCompleted: false } ? 1 : 0); return 0; });
        Register(0x00D0, (vm, c, i) =>
        {
            vm.FinishLoading();
            vm.Store(c, i.Args[0], vm.m_loaded);
            return 0;
        });
        Register(0x00D1, (vm, c, i) =>
        {
            vm.FinishLoading();
            return 0;
        });
        // 06C3: release the movie player's DirectShow objects
        Register(0x06C3, (vm, c, i) => 0);
        // 0140 program, arguments (Sena Plus 0x429D00, FUN_00406C60): CreateProcess of a program of
        // the game folder with its command line, shown normally; a script error when it does not
        // start. Aneiro's START has one for "krkrlt.exe", which its folder does not have. The player
        // starts no programs: the scripts go on as after one that started
        Register(0x0140, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            vm.Value(c, i.Args[1]);
            return 0;
        });
        // 0A30 file, dest: "X:\" of the CD / DVD drive holding the file, or "" (none). Can Fes!'s
        // START asks for its disc so (canfes.bin, its size read with 0158 and compared with a
        // number) until there is one, and reads nothing from it (every file is installed). The
        // machine's drives are looked in, as the engine does; without the disc the player answers
        // for it: a drive of its own, whose files are the size the scripts check for
        // (VirtualDiscSize)
        Register(0x0A30, (vm, c, i) =>
        {
            string file = vm.ReadString(vm.Value(c, i.Args[0]));
            vm.m_disc = DiscHolding(file);
            vm.WriteString(vm.Value(c, i.Args[1]), vm.m_disc.Value.Root);
            return 0;
        });
        // 0A31 path: an extra folder files are looked for in; "" for none. Can Fes!'s START gives
        // the disc 0A30 found, once it has checked it: every file of the game is installed, so
        // none is looked for there
        Register(0x0A31, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0]));
            if (path.Length > 0 && !(vm.m_disc is { } disc && path.Equals(disc.Root, StringComparison.OrdinalIgnoreCase)))
                throw vm.Error(c, $"An extra data folder ({path}) is not supported");
            return 0;
        });
    }
}
