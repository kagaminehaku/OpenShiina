// ScnTools: research tools for ShiinaRio .SCN bytecode (see docs/engine-notes.md).
//
//   ScnTools opscan <preset> <out.tsv>
//   ScnTools opscan <exe> <interp> <dispStart> <loopHead> <invalid> <getVar> <setVar> <getVarAdr> <out.tsv> <dispEnd> [ctxReg] [pcPtrReg] [pcGlobal]
//       Derive the opcode table from an unpacked engine executable (addresses in hex).
//   ScnTools dis <table.tsv> <out.txt> <file.scn>...
//       Disassemble .SCN files with an opcode table (tables\ops_*.tsv).
//   ScnTools opcheck [-o <folder>] <game folder>...
//       The opcodes and DLL functions of a game's scripts the player does not run yet (OpCheck.cs);
//       -o writes each game's disassembly there.
//   ScnTools arccheck <game folder>
//       Whether every file of a game's archives comes out whole with its scheme (ArcCheck.cs).
//   ScnTools datefind <game folder> [--apply <Formats.Json>]
//       The date of DecodeBin at 0x1020 that makes the game's files whole (DateFind.cs).
//   ScnTools files <game folder> [pattern] / extract <game folder> <pattern> <out folder>
//       The entries of a game's archives, or those files decoded (GameFiles.cs).
//   ScnTools routines <game folder> [--like <game folder>...]
//   ScnTools routine <game folder> <module> <hex offset> [--like <game folder> <module> <hex offset>]
//       The embedded x86 routines, their signatures and C# versions (Routines.cs).
//   The steps of trying a new game with these: docs/adding-a-game.md (tests/newgame.ps1).

// Engine builds already analysed. The executables are memory dumps of the unpacked games;
// the addresses are those of that dump.
var presets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
{
    // SENAPLUS_dump_SCY.exe: interpreter FUN_00428c30, handlers use EBP as the context
    ["senaplus"] = new[] { "428c30", "428c97", "428c66", "42f7d2", "416000", "415ab0", "415e60", "", "428cc3", "EBP" },
    // OREIMOPLUS_dump_SCY.exe: interpreter FUN_00423940, context in EBX, EDI = &context->pc
    ["oreimoplus"] = new[] { "423940", "42399f", "423963", "429cbe", "414e30", "4148e0", "414ca0", "", "4239c7", "EBX", "EDI" },
    // aoj.EXE (Ao no Juuai, engine v2.34, not packed): dispatcher FUN_0041e5c0 (a slot's next
    // opcode, handlers are functions), the tasks in a global array (script pointer at 71E0E4 +
    // slot * 0x44), GetVar 40d700 / SetVar 40d000 / GetVarAdr 40d520 get &pc
    ["aoj"] = new[] { "41e5c0", "41e5f5", "0", "41f32b", "40d700", "40d000", "40d520", "", "0", "None", "None", "71e0e4" },
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
if (args.Length >= 2 && args[0] == "opcheck")
    return OpCheck.Run(args.Skip(1).ToArray());
switch (args.Length > 0 ? args[0] : "")
{
    case "arccheck": return ArcCheck.Run(args[1..]);
    case "datefind": return DateFind.Run(args[1..]);
    case "files": return GameFiles.List(args[1..]);
    case "extract": return GameFiles.Extract(args[1..]);
    case "routines": return Routines.RunAll(args[1..]);
    case "routine": return Routines.RunOne(args[1..]);
}
if (args.Length >= 4 && args[0] == "dis")
{
    ScnDisasm.Run(args.Skip(1).ToArray());
    return 0;
}

Console.WriteLine("""
    ScnTools opscan <preset> <exe> <out.tsv>        presets: senaplus, oreimoplus, aoj
    ScnTools opscan <exe> <interp> <dispStart> <loopHead> <invalid> <getVar> <setVar> <getVarAdr> <out.tsv> <dispEnd> [ctxReg] [pcPtrReg] [pcGlobal]
    ScnTools dis <table.tsv> <out.txt> <file.scn>...
    ScnTools opcheck [-o <folder>] <game folder>...
    ScnTools arccheck <game folder>
    ScnTools datefind <game folder> [--apply <Formats.Json>]
    ScnTools files <game folder> [pattern]
    ScnTools extract <game folder> <pattern> <out folder>
    ScnTools routines <game folder> [--like <game folder>...]
    ScnTools routine <game folder> <module> <hex offset> [--like <game folder> <module> <hex offset>]
    """);
return 1;
