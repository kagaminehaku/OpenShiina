# Adding a game

How a ShiinaRio game OpenShiina has not played yet is tried, fixed and recorded. The 43 releases
that play went through these steps; most of what goes wrong with a new one is in the table of
symptoms below, with what fixed it before.

## The steps at a glance

```
dotnet build tools/ScnTools -c Release
dotnet build tests/OpenShiina.ScnBoot -c Release
.\tests\newgame.ps1 -Folder "<game folder>" -LikeFile <list of game folders that play>
```

`tests/newgame.ps1` runs every step below that needs no one at the keyboard and writes
`newgame-<folder>\report.md`: what passed, and for each finding the section of this page to
read. `-LikeFile` is a text file with one game folder per line (the games that play): routines
without a C# version are matched against theirs. `-NoSweep` stops before the sweep. Nothing is
written into the game folder; the tests save in their own folders.

1. **The game**: its executable and engine version.
2. **Archives** (`ScnTools arccheck`): every file of the archives comes out whole with the
   game's scheme; `ScnTools datefind` when DecodeBin's date is wrong.
3. **Opcodes and DLL functions** (`ScnTools opcheck`): what the scripts use that the player
   does not do yet.
4. **Embedded x86 routines** (`ScnTools routines`): each routine the scripts call, and its C#
   version.
5. **From the start** (ScnBoot): the logos, the title, the first scenes.
6. **The sweep** (`tests/sweep.ps1`): every scenario run to its end.
7. **The docs**, once it plays through.

## 1. The game

The engine version is in RIO.INI's first line (`[椎名里緒 v2.50]`; builds older than 2.4x name
the .INI after the .exe: Ao no Juuai's aoj.INI). The player picks the opcode table by it (2.47,
2.49 and 2.50 are known; 2.36 runs with 2.47's; others need a table: todo.md).

Never run a game's executable that is not what its maker shipped (a repacked or "fixed" .exe
can carry anything: Iyashinbo The Motion's was flagged). The archives and the scripts are all the
player needs; the .exe is read, not run, when a scheme has to be taken from it.

## 2. Archives

`ScnTools arccheck <game folder>` opens the archives with the scheme the .exe name gives
(Formats.Json's GameMap), or the one that decodes them when the name is unknown, and checks
every S25 (index and row tables inside the file), scenario TXT (strict Shift-JIS) and Ogg file
(page CRCs). It also lists the names found in more than one archive.

- **GameMap**: the scheme was detected, not found by the .exe name: add the .exe's name to
  Formats.Json's GameMap (the players find a game by it).
- **No scheme**: no keys for this game yet. They can be taken from the executable
  (engine-notes.md, "A scheme from the executable"; from a memory dump when the .exe is packed),
  and the ExtraCrypt from its plug-in (the `YPK` block).
- **DecodeBin's date**: arccheck says which files use the date at 0x1020; when those are the ones
  not whole, `ScnTools datefind <game folder>` finds the date that makes them whole, and
  `--apply src/OpenShiina.Core/Data/Formats.Json` gives the scheme its own DecodeBin with it
  (engine-notes.md, "DecodeBin's date"). A wrong date shows late and anywhere: a garbled script,
  a broken picture, a picture whose relocation writes over the scripts' memory (Chiccha na
  Hanayome: a process of 4 GB and frames of 30 s).
- **Files not whole** that do not use the date: another key of the scheme (ExtraCrypt, its
  seed or table, HelperKey). `ScnTools extract` takes files out to look at.
- **The same name in several archives**: the engine takes such a file from the first archive the
  scripts mount (`00DD`), in the order START mounts them; an update archive (`_U1`) comes before
  the ones it replaces files of, a scene replay's copies of the scenarios (Chiccha's `_E3`) are
  mounted only for it. Nothing to do unless the scripts read the wrong one.

## 3. Missing opcodes

`ScnTools opcheck [-o <folder>] <game folder>` lists the opcodes no handler of ScnVm takes and
the DLL functions of `01A4` it does not do, with where the scripts use them (`-o`: the
disassembly). For each:

1. Read what the scripts do around it (the disassembly) and what the opcode does in the engine:
   its handler's address is in the opcode table (`Data/ScnOps/ops_v*.tsv`), the code in an
   executable of that engine version (REMPLUS.EXE for 2.50, a memory dump for a packed one).
2. Write the handler as the engine does it, in the ScnVm file of its kind, with a comment saying
   what it is and where it was read.
3. A DLL function (user32, kernel32): what the game needs of it; a device or DRM library (HID,
   Steam) answers as when there is none.

opcheck also says when a module's code is mostly data (low "code %"): an opcode found there may
be data read as code (Libra's fan disc: `000A`).

## 4. Routines without C#

Every x86 routine a game runs gets a C# version, found by the signature of its code
(ScnVm.NativeKernels.cs, NativeRoutines.cs). `ScnTools routines <game folder> --like <folders>`
lists the routines the scripts call (`0276`), their signatures and C# versions, and matches
those without one against the other games' by their shape (the instructions without their
numbers):

- **Same shape as a routine with a C# version**: most often that routine with its buffers
  elsewhere (Chuuni Hime's and Chiccha's blur). `ScnTools routine <game> <module> <offset>
  --like <other game> <module> <offset>` shows the instructions that differ, with their numbers:
  if they are only addresses of the routine's own data, add the signature to the C# version; if
  they are constants the C# code uses (a size, a count: Chiccha's samples are 26 x 26 where
  Chuuni Hime's are 25 x 25), make the C# version take them.
- **A new routine**: read engine-notes.md's list of routines first (the names say what they do;
  "16" in a name is the build, not the pixel size), then write it in C# with the x86 code's
  results, byte for byte.
- **Check**: `SCNBOOT_VERIFY_NATIVE=<signature>` in ScnBoot runs the x86 code as well and stops
  at the first byte the C# version writes otherwise (`=1`: every C# routine; large zooms take
  long on the interpreter). A routine neither the start nor the sweep reaches (menus, saves) is
  checked on its own: `ScnTools routinecheck <game> <module> <offset> <l[] spec>` runs both on
  random input (the spec gives each l[i]: `b<bytes>` a buffer, `300` a number, `396-7680` a
  random one, `_` 0; Chiccha's block: `b29700,b576000,396-7680`).

A routine the sweep runs without a C# version shows in its logs (`routine ... : interpreter` or
`translated`); newgame.ps1 lists them.

## 5. From the start

`scnboot <game folder> 6000` runs the game from START.SCN for 6000 frames (100 s of game time).
It stops on a script error with the opcode and where; the usual first stops:

- **A message box at the start**: START checks the executable (`03C2`: its checksum, read from
  the error message or the script's comparison), the disc (`0A30`) or a file. The player answers
  as the engine would for the shipped .exe (Can Fes!, Saimin Seikatsu, Chuuchuu Nurse).
- **A DLL function or an opcode**: as in step 3 (opcheck does not see DLL calls made through a
  table at run time).
- **It stands still**: SCNBOOT_STALL=<s> stops a frame running longer and reports where the
  interpreter is; SCNBOOT_MOUSE / SCNBOOT_PRESS click or press past a screen waiting for input.

## 6. The sweep

`tests/sweep.ps1 -Folder <game folder>` runs every scenario from its first line with skip held
(compatibility.md, "Sweeping"). Every scenario "done" is the goal.

- **Stopped**: the scenario's log (`logs\<scenario>.txt`) says where; a missing file the scripts
  ask for (`0158`) may be one read from the wrong archive (step 2).
- **Stuck / no summary**: SCNBOOT_STALL_REPORT in the log says which instructions ran; a loop
  that runs on and on over a table often means memory written over (below).
- **Slow frames** (the slowest are listed): a routine on the x86 interpreter (step 4), or a
  file read again and again.
- **Lines under 90%**: choices' other branches, or messages of several lines shown together
  (the line counts are of the scenario's lines).

**Memory written over** (a table of the scripts holds pixels, values go wild): find what writes
it. A temporary check in `ScnVm.Execute` that prints the opcode after which a word of memory
changed finds the instruction; then print what that handler is given (Chiccha: `04B2` relocated
a picture whose index had a wrong dword, from DecodeBin's date). Take such checks out again.

## 7. When it plays through

1. compatibility.md: the game's row (✅, the date), a paragraph under **Notes** (engine
   version, executable, what was added for it, the sweep), its row in **Sweeping**'s table and
   date list (compatibility.md, "Updating").
2. README: the counts of games and releases that play, and of the games with keys not tried.
3. todo.md: the same counts under "Other ShiinaRio games".
4. CHANGELOG.md: under "Unreleased", the game and what was fixed for it.
5. engine-notes.md: what was learnt about the engine (an opcode, a routine, a scheme).
6. A scheme, a DecodeBin date or a GameMap name GARbro gets wrong or lacks is worth sending to
   GARbro-Mod as well.

When a fix changes code every game goes through (decryption, the archives' lookup, a handler or
a C# routine others use), sweep the games it touches again before committing.

## Symptoms and what they were

| Symptom | What it was | Games |
|---|---|---|
| The archives do not open | the .exe not in GameMap; no scheme yet | Pure Love!, Tsugou no Ii Idol; School Saimin Paradise!, Sis△Camp, Raillore |
| A garbled script, a broken picture, memory written over, a process of gigabytes | DecodeBin's date | Bitch Nee-chan, Chiccha na Hanayome, Libra (English), Sorcery Jokers (English) |
| A file the scripts ask for is missing | a file of another archive read (mount order) | Chiccha na Hanayome |
| A message box at the start | `03C2` checksum, the disc check | Can Fes!, Chuuchuu Nurse, Saimin Seikatsu |
| A script error on an opcode | a handler not written yet (opcheck) | most games |
| Frames of seconds | an x86 routine on the interpreter (a new build of a known one) | Chuuni Hime, Chiccha na Hanayome, Sorcery Jokers |
| 1 frame a second in the menus | SSE2 routines on the interpreter | the Bitch games |
| Out of the scripts' memory | the heap's size | Bitch Gakuen, Kyuuketsuki no Libra |
