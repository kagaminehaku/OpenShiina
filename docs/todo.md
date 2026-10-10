# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

On a PC the heaviest scene measured (Re:Rem Plus's held zoom) takes 2-3% of the machine at 60
frames a second: more work there is not worth it for now. What is left is for slow phones, and
should follow what their perf.log shows.

- [ ] **Still two to three times the original** in Re:Rem Plus's held kiss zoom (engine-notes.md
  section 7, "Vectors, fewer pieces, the changed part": WPF 2.2-2.4% of the machine, Avalonia
  3.1-3.3%, the original 1.0%); about half is the frame (2.4 ms), half showing it. Measure
  with zoom.ps1 / cpuframe.ps1 (scratch sessions/2026-10-10-measure).
- [ ] **The Avalonia player's picture**: each new frame is copied whole into a new SKImage (the
  render thread draws the one it holds while the next is made). Keep two or three pixel buffers
  and wrap them (SKImage.FromPixels with a release), or keep a texture and update only the
  changed part (GameSession.TakeFrame gives it).
- [ ] **Flat memory: what is left** (engine-notes.md section 10, "Flat memory").
  - Run it on Linux, macOS and Android (the `mmap` path has only been built, not run).
  - The 32-bit way (16 MB parts, `ScnAddressSpace.Segmented`) builds but has not run: ScnBoot
    as a 32-bit program (`dotnet publish tests/OpenShiina.ScnBoot -c Release -r win-x86
    --self-contained`) against the 64-bit one's pictures (Re: Rem Plus's zoom, Oreimo's park
    route), then an `android-arm` APK on a 32-bit phone or TV box.
- [ ] **The x86 translation, for games beyond the eleven.** No routine of the eleven games runs
  translated any more (each has a C# version); X86Jit and the interpreter stay for builds no
  signature matches. Only worth it if other ShiinaRio games come: flags computed only where a
  later instruction reads them (each x86 instruction becomes several .NET ones now).
- [ ] **The SCN interpreter.** The engine's interpreter is compiled C++; ours dispatches each
  opcode in C# and works its operands out (variables, named ones by name) every time it runs
  (the instructions themselves are decoded once). Script logic is rarely where the
  time goes, but profile the dispatch (SCNBOOT_PROFILE) on a busy scene and trim what shows up.
- [ ] **Load files in the background.** Pictures and sounds are read and decoded on the
  interpreter's thread, so a scene change waits for them. `ArcView` is not thread-safe: a
  loader thread needs its own view of the archives.
- [ ] **`00A1`** (the incremental redraw of the message text, a character at a time as it
  fades in): still on the list of slow spots; profile it and make it cheaper.
- [ ] **Older phones, first.** Zooms and transitions dropped frames on a Galaxy S7 (Vulkan 1.0)
  before the vector routines and ParallelRows (2026-10-10, not built for Android yet). Build the
  APK, play Re:Rem Plus's prologue zoom, a scene change and a movie with perf.log on, and work
  on what it shows (the same vector code runs on NEON there; ParallelRows' piece size and
  MaxParts were measured on a PC only).

## The picture on large screens

The setting "Scaling" (sharp, whole numbers, smooth, nearest) is done (engine-notes.md section 6,
"Scaling"). Left:

- [ ] **Upscaler** on the GPU: FSR 1 (EASU + RCAS) or an anime-art upscaler (Anime4K, xBRZ);
  heavier, an option for desktop GPUs, maybe too slow for old phones. Avalonia could run it as
  SkSL like the sharp shader; WPF has no shader path yet (its sharp scaling is a CPU copy).
- [ ] WPF's sharp scaling on the GPU (a `ShaderEffect`, or the picture drawn through Direct3D),
  as the Avalonia player does it: now the picture multiplied by a whole number on the CPU, which
  uploads a picture up to the window's size each frame.
- [ ] Check on an unlocked screen that the WPF player's picture fills the window after a resize
  (captured while the PC was locked, the bottom 52 pixels stayed black in the stretched modes).

Text cannot be drawn at the screen's resolution: the engine draws it into the game's surfaces,
which the scripts then copy and blend.

## Touch

- [ ] **Taps count in the game's frames, not milliseconds.** A tap is now the button down when
  the finger lifts and up 70 ms later; on a phone whose frames take 55-100 ms the scripts (which
  read the buttons once a frame) can miss it, and the first tap on a button only lights it.
  Instead: the pointer over the place for 2 frames before the button goes down, the button down
  for 2 frames (GameThread running window messages at a given frame).
- [ ] **Holding a finger still holds the left button** (after about 0.3 s, until it lifts);
  now only a drag holds it.

## Translations

The scenario files are Shift-JIS and the engine makes half-width characters full-width, so a
translated script can only use what Shift-JIS has: no Vietnamese letters, and English comes out
spaced as full-width.

- [ ] Read a scenario `.TXT` as UTF-8 when it starts with a byte order mark.
- [ ] Keep Latin letters half-width in such files, and draw them with a font that has them
  (Vietnamese included) and the game's measures.
- [ ] Speaker names: `【name】` is looked up in `NWINTBL.BIN` for the name plate; translated
  names need the table translated too, or a map from the new names to the old.
- [ ] Choices written in the SCN code (Oreimo Plus's menus pass them from the scripts): a way
  to replace those strings without moving the code.
- [ ] Pictures with Japanese text (menus, buttons, titles): GrandCrossExtractor can repack an
  archive's files but has no S25 encoder yet.

## Other ShiinaRio games

`Formats.Json` has the keys of 138 ShiinaRio games now (engine-notes.md section 1, "The schemes").

- [ ] Run one of each era with ScnBoot (a 2.49 / 2.50 game first: they are closest to ours) and
  write down what it stops on.
- [ ] Try a real WARC 1.0-1.6 game (1.0 / 1.1 only on archives made for the test, 1.2-1.6's
  range decoder only against GARbro's on random input).
- [ ] Ao no Juuai (engine v2.34, D:\SusGame\Guilty\青の獣愛 here; aoj.EXE, not packed). State
  2026-10-10 (not committed yet; ScnBoot: skip the movie with a click at frame 600, INITIAL START
  at (365, 213) at frame 760): the logo, the opening movie, the title menu, then the first scene
  with its first line of text. **Next: a click does not take the text on** (the line at frame 1300
  is still there at 2400, clicks at (400, 500) every 100 frames); trace where slot 1 waits
  (SCNBOOT_HOT / SCNBOOT_TRACE around a click: in 9 frames it ran 0458 once and 0456 twice, its
  most run code the 02C6 loop at +169E1).
  Done so far (ScnVm.Engine234.cs unless named; Register234 = for EngineVersion < 240 only):
  - ops_v234.tsv (543 opcodes; ScnOpcodes "2.34", SPRITE234 layout of 04BD) and Register234 /
    HandlerFor in ScnVm.cs.
  - 0A28 (extra file source), 0A5C (MMX = 1), 0502 (timed fade), 04BD (7-dword entry, alpha and
    tint), 0028 ms (wait, a frame at a time), 03BB / 03BC / 03BE (stopwatch: start, ms since,
    wait until), 0458 l, r (Input.cs), kernel32 CreateDirectoryA (Gdi.cs, returns 1).
  - 02EE / 02EF fixed for all versions (Core.cs); "instmode" from SETUP.INI's InstallFilesN
    (System.cs); 051E onto the display surface invalidates the window in 2.34 (Sprites.cs).
  - The menus of 2.34 poll the mouse in a loop that draws nothing (the engine's main loop runs one
    instruction of each task a round and pumps messages only after 0033: it spins): a task that
    reads 0456 / 0458 a second time in a frame waits for the next one (Register234).
  - A named operand in 2.34 is read up to its 0 and named up to the first '}' (the local
    "{ret_flag}" is "{ret_flag"; ReadOperand in ScnVm.cs).
  - aoj.EXE: dispatcher 0x41E5C0, GetVar 0x40D700, SetVar 0x40D000, GetVarAdr 0x40D520 (they
    match ours: 0x80 adds the module base to the value, kind 4 is base + value), main loop
    0x4207B0, message pump 0x40B9E0 (only while 0x468DF8, 0033's flag, is set).
  Still to do: the first music file (19,842,092 bytes) is "not Ogg" (find its format); the 11
  opcodes that take other operands than v2.47's (music with one stream: 0686-068B; 055A, 055B,
  04EC, 0B7D) and the 9 new ones (007D-0080, 060E-0610, 0614, 077B) as the game reaches them;
  03BF; then run verify.sh / allgames.sh (02EE / 02EF, instmode and 0458 touch every version)
  before committing.
- [ ] Opcode tables for the other engine versions (2.35-2.46, 2.48; opscan on the game's
  executable, the aoj preset as an example of an old build).
- [ ] The ExtraCrypts ported for those games (PostAdler, PreAdler, Binbo, Count, AltCount,
  Ushimitsu, Nukitashi2, Saimin) against their archives.
- [ ] A scheme chooser in the players for a game whose .exe is not in GameMap (renamed, or a
  version GARbro does not list).

## Platforms

- [ ] iOS player on `OpenShiina.App`.
- [ ] Run and record the games on Linux and macOS (compatibility.md, Platforms).
