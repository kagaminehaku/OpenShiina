// Small system opcodes met on the way into the game: memory status, DirectInput keys, the
// background file loader, the disc check and the extra data folder.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
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

    // op_00CE: the background loader (0x7DCD48: busy, data)
    private int m_loaded;

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
        // 00CE file: load a file in the background (here: at once); 00CF v: still loading (0);
        // 00D0 v: the data; 00D1: wait for the loader
        Register(0x00CE, (vm, c, i) =>
        {
            string file = vm.ReadString(vm.Value(c, i.Args[0]));
            if (vm.ReadScriptFile(file) is not { } data)
                return 2;
            vm.m_loaded = vm.AllocateCopy(data);
            vm.m_fileSizes[vm.m_loaded] = data.Length;
            vm.EngineGlobals[0x4C4514] = data.Length;
            return 0;
        });
        Register(0x00CF, (vm, c, i) => { vm.Store(c, i.Args[0], 0); return 0; });
        Register(0x00D0, (vm, c, i) => { vm.Store(c, i.Args[0], vm.m_loaded); return 0; });
        Register(0x00D1, (vm, c, i) => 0);
        // 06C3: release the movie player's DirectShow objects
        Register(0x06C3, (vm, c, i) => 0);
        // 0A30 file, dest: "X:\" of the CD-ROM drive holding the file, or "" (no disc here)
        Register(0x0A30, (vm, c, i) =>
        {
            vm.Value(c, i.Args[0]);
            vm.WriteByte(vm.Value(c, i.Args[1]), 0);
            return 0;
        });
        // 0A31 path: an extra folder files are looked for in (the disc); "" for none
        Register(0x0A31, (vm, c, i) =>
        {
            string path = vm.ReadString(vm.Value(c, i.Args[0]));
            if (path.Length > 0)
                throw vm.Error(c, $"An extra data folder ({path}) is not supported");
            return 0;
        });
    }
}
