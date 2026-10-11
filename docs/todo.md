# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

On a PC the heaviest scene measured (Re:Rem Plus's held zoom) takes 2-3% of the machine at 60
frames a second: more work there is not worth it for now. What is left is for slow phones, and
should follow what their perf.log shows.

- [ ] **Still two to three times the original** in Re:Rem Plus's held kiss zoom (engine-notes.md
  section 7, "Vectors, fewer pieces, the changed part": the player 3.1-3.3% of the machine,
  the original 1.0%); about half is the frame (2.4 ms), half showing it. Measure
  with zoom.ps1 / cpuframe.ps1 (scratch sessions/2026-10-10-measure).
- [ ] **The Avalonia player's picture**: each new frame is copied into one of up to four pinned
  buffers that SKImages wrap without a copy (2026-10-11: no 3.7 MB array a frame to collect);
  left: a GPU texture that takes only the changed part (GameSession.TakeFrame gives it), which
  would save the upload of the whole picture.
- [ ] **Flat memory: what is left** (engine-notes.md section 10, "Flat memory").
  - Run it on Linux, macOS and Android (the `mmap` path has only been built, not run).
  - The 32-bit way (16 MB parts, `ScnAddressSpace.Segmented`): ScnBoot as a 32-bit process
    (scnboot.dll on the x86 .NET runtime, scratch scnboot32.sh / verify32.sh) gives the 64-bit
    one's pictures, 287 of 287 (Re: Rem Plus's zoom and first route, Oreimo's park route;
    2026-10-11). Left: an `android-arm` APK on a 32-bit phone or TV box.
- [ ] **The x86 translation, for games not tried yet.** No routine of the games tried runs
  translated any more (each has a C# version); X86Jit and the interpreter stay for builds no
  signature matches. Only worth it if other ShiinaRio games come: flags computed only where a
  later instruction reads them (each x86 instruction becomes several .NET ones now).
- [ ] **The SCN interpreter.** The engine's interpreter is compiled C++; ours dispatches each
  opcode in C# and works its operands out (variables, named ones by name) every time it runs
  (the instructions themselves are decoded once). Script logic is rarely where the
  time goes, but profile the dispatch (SCNBOOT_PROFILE) on a busy scene and trim what shows up.
- [ ] **Load files in the background.** The engine's own background loader (`00CE`) reads on
  another thread in the players now (2026-10-11), and files read again come from the cache; the
  pictures and sounds the scripts load with `00C9`, `04B0`, `06A6` are still read on the
  interpreter's thread the first time (a scene change waits for them).
- [ ] **`00A1`** (the incremental redraw of the message text, a character at a time as it
  fades in): its glyphs and the fonts' ascents are kept now (2026-10-11: 848 -> 298 ms over
  282 calls in Bitch Nee-chan, about 1 ms a call); what is left is every shown character drawn
  again at each call (the font made again costs little: keeping its ascent saved 3%).
- [ ] **Older phones, first.** Zooms and transitions dropped frames on a Galaxy S7 (Vulkan 1.0)
  before the vector routines and ParallelRows (2026-10-10, not built for Android yet). Build the
  APK, play Re:Rem Plus's prologue zoom, a scene change and a movie with perf.log on, and work
  on what it shows (the same vector code runs on NEON there; ParallelRows' piece size and
  MaxParts were measured on a PC only).

## The picture on large screens

The setting "Scaling" (sharp, whole numbers, smooth, nearest, and in the Avalonia player the
upscalers FSR 1 and Anime4K) and the graphics levels are done (engine-notes.md section 6,
"Scaling"). Left:

- [ ] FSR and Anime4K on phones: how fast on an old one (the S7: Anime4K's four half-float
  passes at the picture's size each new frame); measured on a PC only (Intel UHD, 60 fps).

Text cannot be drawn at the screen's resolution: the engine draws it into the game's surfaces,
which the scripts then copy and blend.

## Touch

- [x] **Taps count in the game's frames, not milliseconds** (2026-10-10). A tap was the button
  down when the finger lifted and up 70 ms later; on a phone whose frames take 55-100 ms the
  scripts (which read the buttons once a frame) could miss it (Ao no Juuai's opening movie was
  not skipped by a tap), and the first tap on a button only lit it. Now the pointer is over the
  place for 2 frames before the button goes down, and the button is down for 2 frames
  (GameThread.Schedule: steps run before a given frame); the Menu button and Auto's key too.
- [x] **Holding a finger still holds the left button** (after 0.3 s, until it lifts; moving it
  then drags) (2026-10-10).

## Translations

A scenario `.TXT` that starts with a UTF-8 byte order mark is read as a translation (2026-10-11,
engine-notes.md section 6, "Translations"): Latin letters half-width with their own advances,
Vietnamese included, words wrapped at the window's edge; files in the game folder's
`translation\` come before the archives'. Left:

- [ ] A translated scene on a phone. In the Avalonia player on Windows (Skia fonts) Bitch
  Nee-chan's translated first scene draws right (2026-10-11: Vietnamese and English lines, the
  name from `strings.txt`, words wrapped within the edge); Skia's widths are GDI's within 2%
  (scratch fonttest), so a line may break a word apart from GDI's.
- [ ] Speaker names where the game looks them up: a translated `【name】` is drawn as the
  translation's letters (Bitch Nee-chan draws names as text), but Oreimo Plus's name plates are
  pictures found by name in `NWINTBL.BIN` (and voices may be set by name): those need the
  original names (kept in the scenario, drawn translated through `strings.txt` where the game
  draws them as text); the plates themselves are pictures (below).
- [ ] Choices written in the SCN code: `translation\strings.txt` (original, tab, translation)
  replaces a text the scripts pass to 0083 / 0084 / 00A0 when it is the whole of one original
  (2026-10-11; checked on Bitch Nee-chan's speaker names). Left: Oreimo Plus's menus, and texts
  the scripts build from parts (a name then a word) or draw another way.
- [ ] Pictures with Japanese text (menus, buttons, titles): OpenShiina Tools can repack an
  archive's files but has no S25 encoder yet.
- [ ] OpenShiina Tools takes the scenarios' message lines and speakers' names into the tables; the
  texts the scripts write themselves (choices, menus) go into strings.tsv by hand. Left: finding
  those texts in the SCN modules (Shift-JIS strings the scripts pass to 0083 / 0084 / 00A0) to
  list them too, a translation run on a whole game (are 2,820 codes enough for every letter pair
  of a full English or Vietnamese script? Check counts them), Enter moving to the next line in
  the editor, and a machine translation of the empty lines (the user's own key).

## Other ShiinaRio games

`Formats.Json` has the keys of 138 ShiinaRio games now (engine-notes.md section 1, "The schemes").

- [ ] Run one of each era with ScnBoot (a 2.49 / 2.50 game first: they are closest to ours) and
  write down what it stops on.
- [ ] Bitch Gakuen ga Seijun na Hazu ga Nai!!? (v2.50, ver 1.00, next to Bitch Nee-chan here).
  2026-10-11 in ScnBoot (the movie skipped at frame 6100, START at (160, 690) at frame 8100,
  then (640, 300) every 40 frames): the movie, the title and the story to frame 30,000 with
  text. Its executable has the same interpreter and opcode table as REMPLUS.EXE (opscan with
  REMPLUS's addresses gives ops_v250.tsv again). It needed: user32 SetMenuItemInfoW, and 009C
  read as the engine reads it (layout GLYPH250: w, h, pixels only after a text other than 0;
  its START removes a glyph with text 0, and the fixed five operands decoded the next
  instruction from the middle). Next: play it on in the players.
- [ ] Bitch Nee-chan ga Seijun na Hazu ga Nai! (v2.50, ver 1.02, D:\SusGame\ｏｎｏｍａｔｏｐｅ＊\ here).
  2026-10-11 in ScnBoot (START at (130, 680) at frame 5000, then (640, 300) every 40 frames): the
  caution, the opening movie, the title and the story to frame 30,000 with text and music. It
  needed: BITCHES.EXE in GameMap (GARbro's has only BITCHES2/3); a DecodeBin for its scheme
  (GARbro-Mod's has none, so Decrypt2 was skipped: start.scn came out with garbage at 0x200); its
  date at 0x1020 is 0x20160409, worked out from the 16 pictures whose bytes use that slot (none of
  its 11,406 Ogg files does: their headers are alike): with it each one's row table runs on
  evenly (0x20150818, the exe's build date, broke them; the pictures decode either way, so a
  look at the bytes was needed). Bitch Gakuen's GARbro date (0x20170901) passes the same check; 0410
  (GetKeyboardState mode: 0); kernel32 GetVersionExA, LoadLibraryA and HID_ONAHOLE.dll (its USB
  device, as it is without one), user32 GetMessageExtraInfo; 06D6 of a music file copied into a
  buffer (the length from its header); SSE2: its START will not run without it, so cpuid says
  SSE2 for a START with that refusal, the interpreter runs SSE / SSE2 (X86Cpu.Sse.cs) and X86Jit
  translates it (the same X86Cpu.SseCompute); its SSE2 scale32 as C# of its own
  (ScnVm.Scale32Sse.cs; the MMX build's C# version gave other bytes; translated it took 700 ms a
  call, every frame behind the menus: 1 fps) and its blur as C# (ScnVm.Blur32.cs; 290 -> 6 ms),
  both the same bytes as their x86 code on random cases (scratch sse32test, blurtest), its
  C17B8 as subpixel32 v2.50 (1,330 calls the same); glyphs drawn into a layer are clipped to the frame
  (each character goes into a 28 x 29 frame; the overflow broke the next row's header). Next:
  play it on in the players.
- [x] Aneiro (v2.49, D:\SusGame\ALcot Honey Comb\アネイロ here, updated to 1.03a). 2026-10-10 in
  ScnBoot (START at (705, 338) at frame 1100, then the middle every 40 frames): the title
  ("Version 1.03") and the story to frame 9,000 with text and music. It needed: 010E finding
  the save folder itself (START checks DataPath with it, and has kernel32 CreateDirectoryA
  make it), 06E6 (a music stream's sample rate, for its loop points in ms), and kernel32
  MultiByteToWideChar / WideCharToMultiByte (its UTF-8 .wav.sli files into Shift-JIS), and
  02CB (memcmp, at the end). The user skipped it to the end with no error (2026-10-10).
  ALcot's updaters (aneiro_UPDATE_0101.exe, aneiro_update_103a.exe) find the game by the
  registry key the installer writes (Software\ALcot Honey Comb\アネイロ, InstPath): a copied
  game has none. Their files are an FA2 archive in the resource "UPDATE" (records of 0x118
  bytes: name 260, flags 2 = packed with GARbro's Fa2 LZ, file time, unpacked size, size);
  1.03a has all of 1.01's, newer (ANEIRO.EXE, ANEIRO_S / _T / _U1.WAR, SETUP.INI). Taken out
  with a script (scratchpad aneiro_update_103a) and copied into the game.
- [ ] Try a real WARC 1.0-1.6 game (1.0 / 1.1 only on archives made for the test, 1.2-1.6's
  range decoder only against GARbro's on random input).
- [ ] Ao no Juuai (engine v2.34, D:\SusGame\Guilty\青の獣愛 here; aoj.EXE, not packed). State
  2026-10-10 (ScnBoot: skip the movie with a click at frame 600, INITIAL START at (365, 213) at
  frame 760, then a click at (400, 228) every 30 frames: the first choice's first answer and
  the text): the logo, the opening movie, the title menu and the story to frame 30,000 (1,202
  lines, choices) with no opcode missing. Next: play on (later chapters, saves, the system
  screens) in the players, and the opcodes it reaches.
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
  - A frame of 2.34 ends where a task waits (an opcode gave 3), not at the first picture
    (RunFrame): its text loop draws the CLICK mark every round, and a frame ended there
    never reached the mouse. The time (03BD, 03BC) read again in a frame waits as the mouse
    does (a transition polls the clock in a loop).
  - 06E3 s, v (Music.cs): the stream's volume, -1 without one; 06EC s, ms, delta, target: a
    fade (a step every ms, the stream stopped at the end of a fade down to a target without
    bit 31), run before each frame.
  - 2026-10-11: 03A2 (abs), 03A3 (atoi), 03A4 (wsprintf "%d"), 03A5 (an address as an
    offset), 03AD (03AE's seed) - the user's play stopped on 03A2; 0A28's old behaviour had
    also made START's file name buffer grow over its code. All six SCN files read with
    ops_v234 use no other opcode without a handler; ScnBoot ran on to frame 65,000 (20
    music tracks, choices) without a stop.
  - A named operand in 2.34 is read up to its 0 and named up to the first '}' (the local
    "{ret_flag}" is "{ret_flag"; ReadOperand in ScnVm.cs).
  - aoj.EXE: dispatcher 0x41E5C0, GetVar 0x40D700, SetVar 0x40D000, GetVarAdr 0x40D520 (they
    match ours: 0x80 adds the module base to the value, kind 4 is base + value), main loop
    0x4207B0, message pump 0x40B9E0 (only while 0x468DF8, 0033's flag, is set).
  - The first music file (19,842,092 bytes) is a RIFF WAVE (the players read it; ScnBoot's
    TestMusic now does too).
  Left for other 2.34 games: the 11 opcodes that take other operands than v2.47's (music with
  one stream: 0686-068B; 055A, 055B, 04EC, 0B7D) and the 9 new ones (007D-0080, 060E-0610,
  0614, 077B), 03BF: none of Ao no Juuai's eight SCN files uses any of them (2026-10-11).
- [ ] Opcode tables for the other engine versions (2.35-2.46, 2.48; opscan on the game's
  executable, the aoj preset as an example of an old build).
- [ ] The ExtraCrypts ported for those games (PostAdler, PreAdler, Binbo, Count, AltCount,
  Ushimitsu, Nukitashi2, Saimin) against their archives.

## Platforms

- [ ] iOS player on `OpenShiina.App`.
- [ ] Run and record the games on Linux and macOS (compatibility.md, Platforms).
