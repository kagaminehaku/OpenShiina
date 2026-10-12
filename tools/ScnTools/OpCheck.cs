using System.Text;
using System.Text.RegularExpressions;
using OpenShiina.Archives;
using OpenShiina.Game;
using OpenShiina.IO;
using OpenShiina.Scripting;

// ScnTools opcheck [-o <folder>] <game folder>...: what of a game's scripts the player does not
// run yet, before trying it. Every SCN of every archive is disassembled with the player's own
// opcode table (ScnDisasm with Core's layouts), and each opcode is checked against ScnVm's
// handlers for the game's engine version, each DLL function of 01A4 against ScnVm's. The code
// it leaves out is mostly data (embedded x86, buffers, tables, texts); read the coverage and the
// "-o" disassembly when a module's is low. Handlers that exist but go wrong in a case are not
// found this way.
static class OpCheck
{
    public static int Run(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string? outDir = null;
        var folders = new List<string>();
        for (int k = 0; k < args.Length; k++)
        {
            if (args[k] == "-o" && k + 1 < args.Length)
                outDir = args[++k];
            else
                folders.Add(args[k]);
        }
        if (outDir != null)
            Directory.CreateDirectory(outDir);
        var everyGame = new SortedDictionary<string, List<string>>();
        foreach (var folder in folders)
        {
            var missing = Check(folder, outDir);
            foreach (var (what, game) in missing)
            {
                if (!everyGame.TryGetValue(what, out var games))
                    everyGame[what] = games = new();
                games.Add(game);
            }
        }
        Console.WriteLine();
        Console.WriteLine(everyGame.Count == 0 ? "Nothing missing." : "Missing:");
        foreach (var (what, games) in everyGame)
            Console.WriteLine($"  {what}: {string.Join(", ", games)}");
        return everyGame.Count == 0 ? 0 : 2;
    }

    static List<(string What, string Game)> Check(string folder, string? outDir)
    {
        var result = new List<(string, string)>();
        string ini = GameData.EngineIni(folder) is { } iniPath ? File.ReadAllText(iniPath, Encoding.GetEncoding(932)) : "";
        string version = Regex.Match(ini, @"v(\d+\.\d+)").Groups[1].Value;
        var scheme = GameData.FindArchive(folder) is { } archive ? FormatManager.Instance.SchemeForArchive(archive) : null;
        if (scheme == null || version.Length == 0)
        {
            Console.WriteLine($"##### {folder}: {(scheme == null ? "not recognised" : "no engine version in its settings")}");
            return result;
        }
        var table = ScnDisasm.LoadTable(ScnOpcodes.TableText(version).Split('\n'));
        ScnDisasm.Version = version;
        ScnDisasm.Uncertain.Clear();
        var vm = new ScnVm(ScnOpcodes.ForVersion(version), new NoHost())
        {
            EngineVersion = (int)Math.Round(double.Parse(version, System.Globalization.CultureInfo.InvariantCulture) * 100),
        };
        Console.WriteLine($"##### {scheme.Name}: engine v{version}, {ScnOpcodes.TableName(version)}");

        // Every SCN of every archive, by archive and name
        var modules = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var war in Directory.GetFiles(folder, "*.war", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Order(StringComparer.OrdinalIgnoreCase))
        {
            using var warc = WarcOpener.TryOpen(new ArcView(war), scheme);
            if (warc == null)
                continue;
            foreach (var entry in warc.Entries)
                if (entry.Name.EndsWith(".SCN", StringComparison.OrdinalIgnoreCase))
                    modules[Path.GetFileName(war) + ":" + entry.Name] = WarcOpener.OpenEntry(warc, entry);
        }

        var used = new SortedDictionary<int, List<string>>();
        var dlls = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var dis = outDir != null ? new StreamWriter(Path.Combine(outDir, PlayerFolders.FolderName(scheme.Name) + ".dis.txt")) : null;
        foreach (var (name, bytes) in modules)
        {
            var lines = ScnDisasm.Disassemble(bytes, table, out int covered, out var errors);
            Console.WriteLine($"  {name}: {bytes.Length} bytes, code {100.0 * covered / bytes.Length:F1}%");
            dis?.WriteLine($"===== {name}");
            string module = name[(name.IndexOf(':') + 1)..];
            foreach (var line in lines)
            {
                dis?.WriteLine(line);
                var m = Regex.Match(line, @"^  ([0-9A-F]{5})  ([0-9A-F]{4})  \S+\s*(.*)$");
                if (!m.Success)
                    continue;
                int op = Convert.ToInt32(m.Groups[2].Value, 16);
                string at = $"{module}:{m.Groups[1].Value}";
                if (!used.TryGetValue(op, out var places))
                    used[op] = places = new();
                places.Add(at);
                // 01A4 -, "dll", "function", ...
                if (op == 0x01A4 && Regex.Match(m.Groups[3].Value, "^[^,]*, \"([^\"]*)\", \"([^\"]*)\"") is { Success: true } call)
                {
                    string key = call.Groups[1].Value + "!" + call.Groups[2].Value;
                    if (!dlls.TryGetValue(key, out var calls))
                        dlls[key] = calls = new();
                    calls.Add(at);
                }
            }
        }
        foreach (var u in ScnDisasm.Uncertain.Take(3))
            Console.WriteLine($"  length taken as the longer: {u}");

        var noHandler = used.Keys.Where(op => !vm.HasHandler(op)).ToList();
        Console.WriteLine($"  {used.Count} opcodes, {noHandler.Count} without a handler{(noHandler.Count > 0 ? ":" : "")}");
        foreach (int op in noHandler)
        {
            Console.WriteLine($"    {op:X4} x{used[op].Count}  {string.Join(" ", used[op].Take(6))}");
            result.Add(($"opcode {op:X4}", scheme.Name));
        }
        var noDll = dlls.Keys.Where(k => !vm.HasDllFunction(k[..k.IndexOf('!')], k[(k.IndexOf('!') + 1)..])).ToList();
        Console.WriteLine($"  {dlls.Count} DLL functions, {noDll.Count} not done{(noDll.Count > 0 ? ":" : "")}");
        foreach (var key in noDll)
        {
            Console.WriteLine($"    {key} x{dlls[key].Count}  {string.Join(" ", dlls[key].Take(6))}");
            result.Add((key, scheme.Name));
        }
        return result;
    }

    // The VM only answers which opcodes and DLL functions it has: nothing is read or shown
    sealed class NoHost : IScnHost
    {
        public byte[]? ReadFile(string name) => null;
        public byte[]? ReadLooseFile(string name) => null;
        public uint Milliseconds => 0;
        public void SetTitle(string title) { }
        public void SetFullScreen(bool fullScreen) { }
        public string SaveFolder => Path.GetTempPath();
        public byte[]? ReadSaveFile(string name) => null;
        public void WriteSaveFile(string name, byte[] data) { }
        public void DeleteSaveFile(string name) { }
        public int MessageBox(string text, string caption, int type) => 1;
    }
}
