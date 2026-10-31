using System.Text.RegularExpressions;
using Iced.Intel;
using OpenShiina.Archives;
using OpenShiina.Scripting;

// The x86 routines embedded in a game's SCN modules, for giving each its C# version
// (ScnVm.NativeKernels.cs: a C# routine is found by the signature of the code).
//
// ScnTools routines <game folder> [--like <game folder>...]: every routine the scripts call (the
// targets of 0276), with its signature and the C# routine registered for it. With --like, each
// routine without one is matched against those of other games by its shape (its instructions
// with numbers left out: offsets of a build's data, PIC adjustments): the same shape as a routine
// that has a C# version is most often that routine with its buffers elsewhere, and its signature
// can be added to that C# version (then SCNBOOT_VERIFY_NATIVE=<signature> to check the bytes).
// ScnTools routine <game folder> <module> <hex offset> [--like <game folder> <module> <hex offset>]:
// one routine's code and signature; with --like, the instructions where it differs from another.
static class Routines
{
    sealed record Found(string Game, string Module, int Offset, X86Routine Code, string? Native, string[] Shape);

    public static int RunAll(string[] args)
    {
        if (args.Length < 1 || args.Length > 1 && args[1] != "--like")
        {
            Console.WriteLine("usage: ScnTools routines <game folder> [--like <game folder>...]");
            return 1;
        }
        var mine = Collect(args[0]);
        if (mine == null)
            return 3;
        var known = new List<Found>();
        foreach (var other in args.Skip(2))
            known.AddRange(Collect(other, quiet: true) ?? []);
        var byShape = known.Where(k => k.Native != null).GroupBy(k => string.Join("\n", k.Shape)).ToDictionary(g => g.Key, g => g.First());

        int without = 0;
        foreach (var r in mine)
        {
            string state = r.Native != null ? $"C# {r.Native}" : r.Code.Open ? "no C# (code not followed everywhere)" : "no C#";
            Console.WriteLine($"  {r.Module} {r.Offset:X5}  {r.Code.Signature}  {r.Code.Code.Count,5} instr{(r.Code.Calls ? " calls" : "")}{(r.Code.Loops ? " loops" : "")}  {state}");
            if (r.Native != null)
                continue;
            without++;
            if (known.Count == 0)
                continue;
            if (byShape.TryGetValue(string.Join("\n", r.Shape), out var same))
            {
                Console.WriteLine($"      same shape as {same.Game} {same.Module} {same.Offset:X5} = C# {same.Native}");
                continue;
            }
            // The nearest of the same length
            var near = known.Where(k => k.Native != null && k.Shape.Length == r.Shape.Length)
                            .Select(k => (k, Differ: k.Shape.Zip(r.Shape).Count(p => p.First != p.Second)))
                            .Where(p => p.Differ <= Math.Max(4, r.Shape.Length / 50)).OrderBy(p => p.Differ).FirstOrDefault();
            if (near.k != null)
                Console.WriteLine($"      {near.Differ} instructions differ from {near.k.Game} {near.k.Module} {near.k.Offset:X5} = C# {near.k.Native} (ScnTools routine ... --like to see them)");
        }
        Console.WriteLine($"{mine.Count} routines, {without} without a C# version.");
        return without == 0 ? 0 : 2;
    }

    public static int RunOne(string[] args)
    {
        if (args.Length is not (3 or 7) || args.Length == 7 && args[3] != "--like")
        {
            Console.WriteLine("usage: ScnTools routine <game folder> <module> <hex offset> [--like <game folder> <module> <hex offset>]");
            return 1;
        }
        var one = Load(args[0], args[1], Convert.ToInt32(args[2], 16));
        if (one == null)
            return 3;
        if (args.Length == 3)
        {
            Console.WriteLine($"===== {one.Module} {one.Offset:X5}  sig {one.Code.Signature}  {one.Code.Code.Count} instr{(one.Code.Calls ? " calls" : "")}{(one.Code.Open ? " open" : "")}  {(one.Native != null ? "C# " + one.Native : "no C#")}");
            foreach (var line in Listing(one))
                Console.WriteLine(line);
            return 0;
        }
        var other = Load(args[4], args[5], Convert.ToInt32(args[6], 16));
        if (other == null)
            return 3;
        Console.WriteLine($"{one.Module} {one.Offset:X5} ({one.Code.Code.Count} instr, {one.Native ?? "no C#"}) against {other.Game} {other.Module} {other.Offset:X5} ({other.Code.Code.Count} instr, {other.Native ?? "no C#"})");
        // Compared with their numbers (a constant of the C# version may be one of them), branch
        // targets taken from each routine's entry
        var a = Listing(one, labels: false);
        var b = Listing(other, labels: false);
        int differ = 0, shape = 0;
        for (int k = 0; k < Math.Max(a.Count, b.Count); k++)
        {
            string x = k < a.Count ? a[k] : "", y = k < b.Count ? b[k] : "";
            if (Relative(x, one.Offset) == Relative(y, other.Offset))
                continue;
            differ++;
            if (Strip(x) != Strip(y))
                shape++;
            if (differ <= 60)
                Console.WriteLine($"  {x}\n    other: {y}");
        }
        Console.WriteLine(differ == 0 ? "The same instructions." : $"{differ} instructions differ ({shape} of them in more than their numbers).");
        return 0;
    }

    /// <summary>An instruction of a listing without its address, branch targets from the routine's entry.</summary>
    static string Relative(string line, int entry) =>
        Regex.Replace(Regex.Replace(line, @"^\s*[0-9A-F]{5}\s+", ""), @"L_([0-9A-F]+)", m => $"L+{Convert.ToInt32(m.Groups[1].Value, 16) - entry:X}");

    /// <summary>Every routine 0276 calls in a game's SCN modules, or null when its archives do not open.</summary>
    static List<Found>? Collect(string folder, bool quiet = false)
    {
        using var game = GameArchives.Open(folder, out string? why);
        if (game == null || game.EngineVersion.Length == 0)
        {
            Console.WriteLine($"{folder}: {why ?? "no engine version in its settings"}");
            return null;
        }
        if (!quiet)
            Console.WriteLine($"{game.Scheme.Name}: engine v{game.EngineVersion}");
        var table = ScnDisasm.LoadTable(ScnOpcodes.TableText(game.EngineVersion).Split('\n'));
        ScnDisasm.Version = game.EngineVersion;
        var vm = NewVm(game.EngineVersion);
        var result = new List<Found>();
        foreach (var (archive, warc, entry) in game.Entries())
        {
            if (!entry.Name.EndsWith(".SCN", StringComparison.OrdinalIgnoreCase))
                continue;
            byte[] bytes = WarcOpener.OpenEntry(warc, entry);
            var targets = new SortedSet<int>();
            foreach (var line in ScnDisasm.Disassemble(bytes, table, out _, out _))
                if (Regex.Match(line, @"^  [0-9A-F]{5}  0276  \S+\s+L_([0-9A-F]+)") is { Success: true } m)
                    targets.Add(Convert.ToInt32(m.Groups[1].Value, 16));
            if (targets.Count == 0)
                continue;
            int module = vm.AllocateCopy(bytes);
            foreach (int offset in targets)
                if (offset < bytes.Length)
                    result.Add(Make(game.Scheme.Name, entry.Name, offset, vm, module));
        }
        return result;
    }

    static Found? Load(string folder, string moduleName, int offset)
    {
        using var game = GameArchives.Open(folder, out string? why);
        if (game == null)
        {
            Console.WriteLine($"{folder}: {why}");
            return null;
        }
        var found = game.Entries().FirstOrDefault(e => e.Entry.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
        if (found.Entry == null)
        {
            Console.WriteLine($"{folder}: no {moduleName} in its archives");
            return null;
        }
        var vm = NewVm(game.EngineVersion.Length > 0 ? game.EngineVersion : "2.49");
        int module = vm.AllocateCopy(WarcOpener.OpenEntry(found.Warc, found.Entry));
        return Make(game.Scheme.Name, found.Entry.Name, offset, vm, module);
    }

    static Found Make(string game, string module, int offset, ScnVm vm, int moduleAddress)
    {
        var code = new X86Routine(vm, (uint)(moduleAddress + offset));
        var shape = code.Code.Values.Select(i => Strip(Format(i))).ToArray();
        return new Found(game, module, offset, code, vm.NativeName(code.Signature), shape);
    }

    static ScnVm NewVm(string version) => new(ScnOpcodes.ForVersion(version), new ToolHost()) { JitInBackground = false, JitX86 = false };

    static readonly IntelFormatter s_formatter = new() { Options = { FirstOperandCharIndex = 8, HexPrefix = "0x", HexSuffix = "" } };

    static string Format(Instruction ins)
    {
        var output = new StringOutput();
        s_formatter.Format(ins, output);
        return output.ToStringAndReset();
    }

    /// <summary>An instruction without its numbers (offsets, displacements, immediates, targets).</summary>
    static string Strip(string text) => Regex.Replace(Regex.Replace(text, @"^\s*(L_[0-9A-F]+:)?\s*([0-9A-F]{5}\s+)?", ""), @"0x[0-9A-F]+|\b[0-9A-F]{5,8}\b|L_[0-9A-F]+", "#");

    /// <summary>The routine's instructions, branch targets as module offsets (L_xxxxx).</summary>
    static List<string> Listing(Found r, bool labels = true)
    {
        uint module = r.Code.Entry - (uint)r.Offset;
        var lines = new List<string>();
        uint previous = 0;
        foreach (var (ip, ins) in r.Code.Code)
        {
            if (labels && previous != 0 && ip != previous)
                lines.Add("        ...");
            string text = Format(ins);
            if (ins.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.Call && ins.Op0Kind == OpKind.NearBranch32)
                text = $"{ins.Mnemonic.ToString().ToLowerInvariant(),-7} L_{ins.NearBranch32 - module:X5}";
            string label = labels && r.Code.Targets.Contains(ip) ? $"L_{ip - module:X5}:" : "";
            lines.Add($"{label,-9}{ip - module:X5}  {text}");
            previous = ins.NextIP32;
        }
        return lines;
    }
}

/// <summary>A VM host that only lets the tools read code: nothing is shown, played or saved.</summary>
sealed class ToolHost : IScnHost
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
