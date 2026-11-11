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

    // The window's client size the scripts last asked for with 0A00 (0: the screen's): the games
    // with a window size setting (Nukitashi 2: 960 x 540 to 1920 x 1080 for a 1600 x 900 screen)
    // keep their surfaces and have the engine stretch the screen into a window of another size
    private int m_clientWidth, m_clientHeight;

    /// <summary>The window's client size (0A00), the screen's until the scripts ask for another.</summary>
    public int ClientWidth => m_clientWidth > 0 ? m_clientWidth : ScreenWidth;
    public int ClientHeight => m_clientHeight > 0 ? m_clientHeight : ScreenHeight;

    /// <summary>
    /// A point of the screen (where the host puts the mouse) as the window's client sees it, the
    /// way 0456 gives it: the scripts that sized the window turn it back with
    /// (x * screen + client / 2) / client.
    /// </summary>
    private (int X, int Y) ScreenToClient(int x, int y) =>
        ((int)(((long)x * ClientWidth + ScreenWidth / 2) / ScreenWidth), (int)(((long)y * ClientHeight + ScreenHeight / 2) / ScreenHeight));

    /// <summary>A point of the window's client (0457) on the screen.</summary>
    private (int X, int Y) ClientToScreen(int x, int y) =>
        ((int)(((long)x * ScreenWidth + ClientWidth / 2) / ClientWidth), (int)(((long)y * ScreenHeight + ClientHeight / 2) / ClientHeight));

    // The registry key opened by op_00FA, and the values the installer wrote there
    private string m_registryKey = "";

    private object? RegistryValue(string name) => name.ToLowerInvariant() switch
    {
        "datapath" => DataPath,
        "instmode" => InstallMode,
        // the folder the game was installed in (Vanquish's START makes it the current directory):
        // the game folder, which the player's file names are relative to
        "instpath" => ".\\",
        _ => null,
    };

    /// <summary>
    /// InstMode as the installer wrote it: the last install choice of SETUP.INI (InstallFiles0,
    /// InstallFiles1, ...) whose files beyond the earlier choices' are all in the game's folder;
    /// 0 without one. The
    /// GRAND†CROSS games have one choice; Ao no Juuai's 1 is the full install (voices, music and
    /// movies on the disk too, so START does not ask for the disc).
    /// </summary>
    private int InstallMode => m_installMode ??= FindInstallMode();
    private int? m_installMode;

    private int FindInstallMode()
    {
        if (m_host.ReadLooseFile("SETUP.INI") is not { } bytes)
            return 0;
        var choices = new SortedDictionary<int, string[]>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Encodings.cp932.GetString(bytes), @"(?im)^InstallFiles(\d+)=(.*)$"))
            choices[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // A choice counts by the files it adds to the ones before it (Ao no Juuai's full install:
        // the voices, the music and the movies): a copied game may lack the others (guilty.url)
        int best = 0;
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (choice, files) in choices)
        {
            var added = files.Where(f => !before.Contains(f)).ToList();
            if (choice > best && added.Count > 0 && added.All(f => m_host.LooseFileSize(f) != null))
                best = choice;
            before.UnionWith(files);
        }
        return best;
    }

    /// <summary>The save folder as the scripts see it: a path ending with '\'.</summary>
    public string DataPath => m_host.SaveFolder.TrimEnd('\\', '/') + "\\";

    /// <summary>The game being played (its scheme's name, "Azu Plus"): what the executable's own checks expect.</summary>
    public string Game { get; set; } = "";

    /// <summary>
    /// The game's .exe as GameMap names it for <see cref="Game"/>, the one in the game folder when
    /// it names several (Chikan Circle 2's CHIKAN2.exe / CHIKAN2_.exe); null for a game not listed.
    /// </summary>
    private string? ExeFileName()
    {
        var names = Archives.FormatManager.Instance.GameMap.Where(e => e.Value == Game).Select(e => e.Key).ToList();
        return names.FirstOrDefault(n => m_host.LooseFileSize(n) != null) ?? names.FirstOrDefault();
    }

    // 03C2's checksums of the unchanged executables, by game: Azu Plus (START 0x002F0 wants
    // v ^ 306723180 = 1804523910), Chuuchuu Nurse (CHUCHU.EXE, v2.45: START 0x0033F wants
    // -462117567, the CRC-32 of its decrypted code XOR that of 0x8000 bytes at 47B774), Can Fes!
    // (CANFES.EXE, v2.47: START 0x004EE wants v ^ 306723180 = -1948973168), Saimin Seikatsu
    // (SAIMIN.EXE, v2.47: START 0x00484 wants v ^ 306723180 = -628140931), Doushite Daite Kurenai
    // no!? (DODAKURE.EXE, v2.50: START 0x006A6 wants v ^ 306723180 = -1421357033), Nakadashi
    // Trilogy's Miko Dashi (MIKODS.EXE, v2.47: START 0x0037A wants v ^ 306723180 = 228471671), Imouto
    // Smile (IMOSMILE.EXE, v2.47: START 0x003C4 wants v ^ 306723180 = 969238634), Yokorenbo
    // (YOKOREN.EXE, v2.44: START 0x00352 wants v ^ 306723180 = -1396214426, 555116478 with f[257] bit 1),
    // Hana to Otome ni Shukufuku o (HANAOTO.EXE, v2.46: START 0x0037E wants v ^ 306723180 = 222200098),
    // and, the value without f[257] bit 1 as for Yokorenbo: Engage Links (v2.45: START 0x0039B wants
    // 1224645659), Chikan Densha Otoko 2 (v2.43: 0x005F5, 741346554), Chikan Densha Otoko Gaiden
    // (v2.47: 0x003CB, -1138810104), Tsuyoimo x Yowaimo (IMOIMO.EXE, v2.45: 0x00322, -770208518),
    // Mahou Shoujo no Taisetsu na Koto (v2.47: 0x003E2, -1545457906; with f[257] bit 1 none is asked),
    // Shokuinshitsu (TEACH.exe, v2.46: 0x00369, -661847700; -75841587 with f[257] bit 1)
    private static readonly Dictionary<string, int> s_exeChecksums = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Azu Plus"] = 306723180 ^ 1804523910,
        ["Chuuchuu Nurse"] = unchecked((int)0xF63C9C2D),
        ["Can Fes! ~Itazura Majo to Naisho no Gakuensai~"] = 306723180 ^ -1948973168,
        ["Hana to Otome ni Shukufuku o Royal Bouquet"] = 306723180 ^ 1872894084,
        ["Saimin Seikatsu"] = 306723180 ^ -628140931,
        ["Doushite Daite Kurenai no!?"] = 306723180 ^ -1421357033,
        ["Nakadashi Trilogy"] = 306723180 ^ 228471671,
        ["Imouto Smile"] = 306723180 ^ 969238634,
        ["Yokorenbo ~Immoral Mother~"] = 306723180 ^ -1396214426,
        ["Hana to Otome ni Shukufuku o"] = 306723180 ^ 222200098,
        ["Engage Links"] = 306723180 ^ 1224645659,
        ["Chikan Densha Otoko 2"] = 306723180 ^ 741346554,
        ["Chikan Densha Otoko Gaiden"] = 306723180 ^ -1138810104,
        ["Tsuyoimo x Yowaimo"] = 306723180 ^ -770208518,
        ["Mahou Shoujo no Taisetsu na Koto"] = 306723180 ^ -1545457906,
        ["Shokuinshitsu"] = 306723180 ^ -661847700,
    };

    private void RegisterSystem()
    {
        // 03C0 v, w: engine version (247) and 0x487F24
        Register(0x03C0, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], vm.EngineVersion);
            vm.Store(c, i.Args[1], vm.EngineGlobal(0x487F24));
            return 0;
        });
        // 03C2 v: 0x13B41BC, a checksum of the executable made at start: the CRC-32 of its code as
        // the .riox stub left it, XORed with another (of its "riox" section, then in v2.45 of
        // 0x8000 bytes of its data). START stops with "Program Revision Error" unless v ^ 306723180
        // is the value of the unchanged executable, a constant of each game's START: OpenShiina
        // gives that value (ExeChecksums), Azu Plus's for a game not listed
        Register(0x03C2, (vm, c, i) =>
        {
            vm.Store(c, i.Args[0], s_exeChecksums.GetValueOrDefault(vm.Game, s_exeChecksums["Azu Plus"]));
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
        // 049D v / 049C v: set / get the engine's flag at 0xD0AD3C (REMPLUS.EXE 0x423340 and
        // 0x423320); Tanetsuke Mura's START sets it from RIO.INI's MouseLR_Change
        Register(0x049D, (vm, c, i) => vm.SetGlobal(0xD0AD3C, c, i));
        Register(0x049C, (vm, c, i) => { vm.Store(c, i.Args[0], vm.EngineGlobal(0xD0AD3C)); return 0; });
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
