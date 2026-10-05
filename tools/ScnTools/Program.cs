// ScnTools: research tools for ShiinaRio .SCN bytecode (see docs/engine-notes.md).
//
//   ScnTools opscan <preset> <out.tsv>
//   ScnTools opscan <exe> <interp> <dispStart> <loopHead> <invalid> <getVar> <setVar> <getVarAdr> <out.tsv> <dispEnd> [ctxReg] [pcPtrReg]
//       Derive the opcode table from an unpacked engine executable (addresses in hex).
//   ScnTools dis <table.tsv> <out.txt> <file.scn>...
//       Disassemble .SCN files with an opcode table (tables\ops_*.tsv).

// Engine builds already analysed. The executables are memory dumps of the unpacked games;
// the addresses are those of that dump.
var presets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
{
    // SENAPLUS_dump_SCY.exe: interpreter FUN_00428c30, handlers use EBP as the context
    ["senaplus"] = new[] { "428c30", "428c97", "428c66", "42f7d2", "416000", "415ab0", "415e60", "", "428cc3", "EBP" },
    // OREIMOPLUS_dump_SCY.exe: interpreter FUN_00423940, context in EBX, EDI = &context->pc
    ["oreimoplus"] = new[] { "423940", "42399f", "423963", "429cbe", "414e30", "4148e0", "414ca0", "", "4239c7", "EBX", "EDI" },
};

if (args.Length >= 3 && args[0] == "opscan" && presets.TryGetValue(args[1], out var preset))
{
    if (args.Length < 4) { Console.WriteLine("usage: ScnTools opscan <preset> <exe> <out.tsv>"); return 1; }
    var a = (string[])preset.Clone();
    a[7] = args[3];
    OpAnalyzer.Run(new[] { args[2] }.Concat(a).ToArray());
    return 0;
}
if (args.Length >= 11 && args[0] == "opscan")
{
    OpAnalyzer.Run(args.Skip(1).ToArray());
    return 0;
}
if (args.Length >= 4 && args[0] == "dis")
{
    ScnDisasm.Run(args.Skip(1).ToArray());
    return 0;
}

Console.WriteLine("""
    ScnTools opscan <preset> <exe> <out.tsv>        presets: senaplus, oreimoplus
    ScnTools opscan <exe> <interp> <dispStart> <loopHead> <invalid> <getVar> <setVar> <getVarAdr> <out.tsv> <dispEnd> [ctxReg] [pcPtrReg]
    ScnTools dis <table.tsv> <out.txt> <file.scn>...
    """);
return 1;
