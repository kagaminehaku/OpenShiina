// System opcodes of START's start-up: the engine version, the window size and colour depth,
// DirectDraw / DirectSound, message boxes, memory blocks, and the registry key the installer
// wrote (DataPath = where the save data lives, InstMode). The registry is emulated: DataPath is
// the host's save folder, so the scripts' save files never go to the game folder.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>Engine version from RIO.INI ("2.47" gives 247), for op_03C0.</summary>
    public int EngineVersion { get; set; } = 247;

    /// <summary>The window size of RIO.INI (WindowWidth / WindowHeight).</summary>
    public int ScreenWidth { get; set; } = 800;
    public int ScreenHeight { get; set; } = 600;

    // The registry key opened by op_00FA, and the values the installer wrote there
    private string m_registryKey = "";

    private object? RegistryValue(string name) => name.ToLowerInvariant() switch
    {
        "datapath" => DataPath,
        "instmode" => InstallMode,
        _ => null,
    };

    /// <summary>
    /// InstMode as the installer wrote it: the last install choice of SETUP.INI (InstallFiles0,
    /// InstallFiles1, ...) whose files are all in the game's folder; 0 without one. The
    /// GRAND†CROSS games have one choice; Ao no Juuai's 1 is the full install (voices, music and
    /// movies on the disk too, so START does not ask for the disc).
    /// </summary>
    private int InstallMode => m_installMode ??= FindInstallMode();
    private int? m_installMode;

    private int FindInstallMode()
    {
        if (m_host.ReadLooseFile("SETUP.INI") is not { } bytes)
            return 0;
        int best = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Encodings.cp932.GetString(bytes), @"(?im)^InstallFiles(\d+)=(.*)$"))
        {
            int choice = int.Parse(m.Groups[1].Value);
            var files = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (choice > best && files.Length > 0 && files.All(f => m_host.LooseFileSize(f) != null))
                best = choice;
        }
        return best;
    }

    /// <summary>The save folder as the scripts see it: a path ending with '\'.</summary>
    public string DataPath => m_host.SaveFolder.TrimEnd('\\', '/') + "\\";

    private void RegisterSystem()
    {
        // 03C0 v, w: engine version (247) and 0x487F24
        Register(0x03C0, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], vm.EngineVersion);
            vm.Store(c, i.Args[1], vm.EngineGlobal(0x487F24));
            return 0;
        });
        // 03C2 v: 0x13B41BC, a checksum of the executable (FUN_004374D0 / FUN_004085C0: checksums of its
        // code, its "riox" section and its version resource). Only Azu Plus reads it: START 0x002F0
        // stops with "Program Revision Error" unless v ^ 306723180 is 1804523910 - the value of an
        // unchanged AZUPLUS.EXE.
        Register(0x03C2, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], 306723180 ^ 1804523910);
            return 0;
        });
        // 09F6 w, h: the window size
        Register(0x09F6, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], vm.ScreenWidth);
            vm.Store(c, i.Args[1], vm.ScreenHeight);
            return 0;
        });
        // 09E2 hdc, index, v: GetDeviceCaps of a true colour screen
        Register(0x09E2, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            int index = vm.Value(c, i.Args[1]);
            vm.Store(c, i.Args[2], index switch
            {
                8 => 1920, 10 => 1080, 12 => 32, 14 => 1, 88 or 90 => 96, 116 => 60, _ => 0,
            });
            return 0;
        });
        // 06C2 v / 06A4 v: DirectDraw / DirectSound started (non-zero = ready)
        Register(0x06C2, (vm, c, i) => { vm.Store(c, i.Args[0], 1); return 0; });
        Register(0x06A4, (vm, c, i) => { vm.Store(c, i.Args[0], 1); return 0; });
        // 06BA flags / 06BB v: DirectSound flags (0x13B52F0)
        Register(0x06BA, (vm, c, i) => vm.SetGlobal(0x13B52F0, c, i));
        Register(0x06BB, (vm, c, i) => { vm.Store(c, i.Args[0], vm.EngineGlobal(0x13B52F0)); return 0; });
        // 07E8 v: v = RIO.INI's Background (0x13B43D0), as 07E5 / 07E6 last set it
        Register(0x07E8, (vm, c, i) => { vm.Store(c, i.Args[0], vm.RunsInBackground ? 1 : 0); return 0; });
        // 073F v: the COM objects for movies were created
        Register(0x073F, (vm, c, i) => { vm.Store(c, i.Args[0], 1); return 0; });
        // 0780 text, caption, type, v: MessageBox
        Register(0x0780, (vm, c, i) =>
        {
            string text = vm.ReadString(vm.Value(c, i.Args[0]));
            string caption = vm.ReadString(vm.Value(c, i.Args[1]));
            int type = vm.Value(c, i.Args[2]);
            vm.Store(c, i.Args[3], vm.m_host.MessageBox(text, caption, type));
            return 0;
        });

        // 02BC n, v: GlobalAlloc (zeroed); 02BD p: GlobalFree. The block's size is kept like a
        // loaded file's (GlobalSize): START copies cached files into such blocks and opens music
        // streams on them (06D6 flag 1)
        Register(0x02BC, (vm, c, i) =>
        {
            int size = vm.Value(c, i.Args[0]);
            int block = vm.Allocate(size);
            vm.m_fileSizes[block] = size;
            vm.Store(c, i.Args[1], block);
            return 0;
        });
        Register(0x02BD, (vm, c, i) =>
        {
            vm.FreeScriptBlock(vm.Value(c, i.Args[0]));
            return 0;
        });

        // 00FA root, key: open a registry key (the copy-protection CRC of the exe is not checked)
        Register(0x00FA, (vm, c, i) =>
        {
            vm.EngineGlobals[0x4880CC] = vm.Value(c, i.Args[0]);
            vm.m_registryKey = vm.ReadString(vm.Value(c, i.Args[1]));
            return 0;
        });
        // 00FF name, v: whether the value exists
        Register(0x00FF, (vm, c, i) =>
        {
            string name = vm.ReadString(vm.Value(c, i.Args[0]));
            vm.Store(c, i.Args[1], vm.RegistryValue(name) != null ? 1 : 0);
            return 0;
        });
        // 00FE name, buffer: a string value
        Register(0x00FE, (vm, c, i) =>
        {
            string name = vm.ReadString(vm.Value(c, i.Args[0]));
            int buffer = vm.Value(c, i.Args[1]);
            if (vm.RegistryValue(name) is not string text)
                return 2;
            vm.WriteString(buffer, text);
            return 0;
        });
        // 00FD name, v: a number value
        Register(0x00FD, (vm, c, i) =>
        {
            string name = vm.ReadString(vm.Value(c, i.Args[0]));
            if (vm.RegistryValue(name) is not int number)
                return 2;
            vm.Store(c, i.Args[1], number);
            return 0;
        });
    }
}
