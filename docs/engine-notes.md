# ShiinaRio engine notes (GRAND†CROSS "Plus" games)

Research notes for OpenShiina (the player), the SCN tools (`tools/ScnTools`) and the
GrandCrossExtractor archive tool they started in. Facts marked *(verified)* were checked
against all 11 games' data or the engine code; everything else is an inference and says so.

## 1. Archives

Each game folder holds `*_D.WAR` (UI graphics), `*_G.WAR` (CG, sprites, backgrounds; Azu Plus also
keeps voice/BGM here), `*_M.WAR` (BGM, SE), `*_S.WAR` (compiled scripts `.SCN`), `*_T.WAR`
(scenario `.TXT`, Shift-JIS) and `*_V.WAR` (voice `.OGV`). Movies are loose `mv\*.mpg` files.
Script paths use prefixes: `e\` and `b\` and `c\` = images in `_G`, `v\` = voice, `m\` = BGM,
`se\` = sound effects, `mv\` = movies, `d\` = `_D`, `p\` = scenario TXT, `t\` = SCN.

## 2. S25 layered images *(verified)*

Frames are grouped by their slot number in the S25 header:

| Slots | Meaning |
|---|---|
| 0-99 | base pictures (alternatives, all with the same size and offset) |
| 100-199, 200-299, ... 900-999 | layer 1, 2, ... (mouth, eyes, arms, effects - meaning depends on the file) |
| 1000+ | not part of the picture (hit masks, ...) |

Each frame is drawn at its own screen offset. UI sprite sheets (`SYSTEM`, `RUBY`, `THSAVE`...) are
not layered: rejected because their "bases" differ in size/offset or slots run across a hundred
boundary (99 and 100 both used).

Which combinations are shown is written in the scenario: `$L_MONT,<plane>,<file>,x,y,?,m,<base>,<l1>,<l2>,...`
(also `$L_CHR ...,m,...` for sprites). Value v at position k = slot k*100+v; -1 or a missing value
switches the layer off. All ~4000 such commands in the 11 games point at existing slots. Zoomed CGs
(`EV02_02L`, `..M`) are never named in the script; the engine swaps them in, and they share the
slot table of the unzoomed file, so they use its combinations. Oreimo's sprite `KIR.S25` is
changed with `$L_MONT,1,,0,0,0,M,101` (upper-case M, no file): these are expression codes,
looked up in `MONTBL.BIN` (in `_T`) *(verified)*:

- 100,000 entries of 8 bytes, then a string table (`st\kir.s25`, `f\0.s25`, `st\kir_L.s25`).
- Entry = u16 offset of the S25 name in the string table, u16 offset of a face picture name,
  u32 with six 5-bit slot values (base, layers 1-5; 31 = layer off). All `FF` = unused code.
- Oreimo: 101-107 = mouth 100 + eyes 200-206, 121-127 = the same with blush (300),
  151-157 / 171-177 = open mouth 101; 2xx = the same on the zoomed `kir_L.s25`.
- Oreimo's scripts use only these codes (101-107 and 155, all on `KIR.S25`), never the `m` form,
  so the extractor's "Compose layers" reads them through `MontageTable` too: 8 pictures of
  `KIR.S25` instead of its 11 separate frames.

About 3,700 eye/mouth frames of the other games are never used by any script combination.
Possibly blink / lip-sync frames switched by the engine, or simply unused art - **unknown**.
Oreimo has none: its only expression sprite `KIR.S25` has 11 frames (base, mouths 100 / 101,
eyes 200-206, blush 300), and the 8 expression codes its scripts use cover all of them; the open
mouth is chosen by the script (code 155), not switched by the engine.

## 3. Scenario TXT

Lines are commands `$NAME,args`, speaker lines `【name】`, dialogue `「...」`, narration, or
comments `;`. Every non-empty text line is one message (one click); no message spans two lines.
Comments carry the writers' notes (`;【...】` = scene title, `;/// 差分：... ///` = CG variant
wanted here, `;※i ...` = director's instructions, `;;$L_MONT...` = disabled command).
`①` in the text is the heart gaiji (`GAIJI.S25` in `_D`). Oreimo Plus: 35 files, ~15,000
lines, 2,842 messages. Command use in Oreimo Plus, as the scripts read them:

| Command | Uses | Arguments and meaning |
|---|---|---|
| VOICE | 1116 | `file, loop, slot, wait` - voice of the next message |
| DRAW_EX | 592 | `kind, rule S25, ms, hide window` - replace the screen and wait: kind 0 cross-fade, 1 cut, 2 rule wipe bright parts first, 47 dark parts first (section 8) |
| L_BG | 352 | `file, reset, x, y, zoom%` - background plane 0; reset 0 also clears planes 1-9 (face overlays like `ev01_01` would otherwise stay) |
| L_CHR | 372 | `plane, file (empty = clear), x, y, type[, m, slots...]` - type = plane transition on the next DRAW (0 = 500 ms cross-fade) |
| A_CHR | 334 | `code, plane, ...` - plane animation, queued until the next DRAW (section 8) |
| DRAW | 200 | show the prepared picture at once and start the queued animations; does not wait |
| WAITA | 152 | wait for animations that are not background ones (wf = 0), finite loops and a non-looping movie |
| WAIT | 120 | `ms` |
| WINDOW | 118 | `0` = hide the message window (the next message shows it) |
| EFECT | 67 | `n, ...` - screen effect, the engine waits: 0 / 1 / 2 shake (16 / 32 / 8 px), 12 zoom pulse (section 8) |
| L_MONT | 54 | `plane, file, x, y, ?, m/M, ...` - section 2 |
| EX | 36 | `9,0,count,width` / `9,1,slot,file` / `9,2,speed` / `9,4` = background scroll setup, picture, start (px/s, positive moves the picture right), end; `10,2,var,value` = set `_Dvar`; `2,0` = wait for a key; `4` = ? |
| L_MOVIE / WAIT_L_MOVIE | 36 / 18 | `plane, mv\file.mpg (empty = stop), loop, ?` / `plane` = wait for the end of the current round |
| MUSIC / MUSIC_FADE | 31 / 32 | `file (empty = stop), loop, fade-in ms` / `[ms]` |
| SE / SE_FADE | 25 / 7 | `file (empty = stop), mode (0 once, 1 loop, 2 once and wait, 3 load only), channel` / `ms, channel` |
| PRELOAD | 18 | `file` - cache hint |
| LABEL / CJUMP / EVENT_BLOCK | 4 | `n` / `_D710==0, label` / `1, label` = skipping inside the block jumps to the label |

`A_CHR` codes are listed in section 8 (read from START.SCN).

The message window is `SYSTEM.S25` (in `_D`) slot 0 at (70,451). Name plates are slot 29 + n,
where n comes from `NWINTBL.BIN` (0x24-byte entries: Shift-JIS name, u32 n): 俺 = 30, 桐乃 = 31,
ＰＣ = 32. Route menu buttons are slots 400 + 10·i (+1 highlighted), text choices use slot 311
with the text drawn on it.

Every resource Oreimo Plus references exists (1,515 files).

## 4. SCN bytecode *(verified on all 11 SRC_MAIN.SCN, 0 decode errors)*

The engine interprets **TXT at run time**; the SCN files are the engine's own script code:

| File | Role |
|---|---|
| START.SCN | engine library: TXT command implementations, text system, UI, saves, CG list |
| SRC_MAIN.SCN | game flow: which TXT runs, choice menus, ending, staff roll |
| PLAUNCH.SCN | script number -> TXT file (save loading) |
| LAUNCH.SCN | event-number dispatcher |
| TOPMENU.SCN | logo, caution, title screen |
| EFCLIB.SCN | visual effects, calls GDI (`gdi32.dll`) directly |

**Instruction** = u16 opcode + operands. **Operand** = type byte + payload (self-delimiting):

| Type (low bits) | Payload | Meaning |
|---|---|---|
| 0x02/0x06/0x08/0x0A/0x0C/0x0E (+1 = dereference) | u16 index | variable in one of six areas (printed g/s/l/a/f/b) |
| 0x04 (0x05 = deref) | i32 | immediate |
| 0x10 | NUL-terminated string | string literal |
| 0x11 | optional '.', NUL-terminated string | expression text, e.g. `_D780|(1<<_D990)` |
| 0x12 (0x13 = deref) | name up to NUL or `}` | named variable |
| flag 0x80 | | address relative to the script base (jump targets are `0x84 <i32>`) |
| flag 0x40 | | take the address (GetVarAdr) |

Opcode tables (`tools/ScnTools/tables/ops_*.tsv`) give each opcode's operand signature:
`V` = operand, `bN` = N raw bytes. Variable-length instructions handled by hand:

| Opcode | Layout |
|---|---|
| 0x3CE / 0x3CF | global / local declarations: u16 count + count operands |
| 0x283 callmod | V V, u16 argc, argc operands |
| 0x259 switch | u32 table end, index V, case targets V... up to the end |
| 0x209 case | u32 address of the index operand (inside a 0x208), u32 target, values V... , 0xFF, u32 next case |
| 0x2DB | printf-like message: operands until a 0xFF byte |
| 0x1F4 if | V, cmp byte (0 == 1 != 2 >= 3 > 4 <= 5 < 6 & 7 \|, unsigned), V, u32 target taken when the condition is FALSE |

No fall-through after 0x0000 (end), 0x0258 (goto), 0x026C (ret), 0x026D (ret value), 0x0209, and
`if` with constant operands that is always false. Code embeds data after jumps: choice texts,
u32 address tables (`f = idx<<2; f += TABLE; f = *f; goto f`), callback records, even native x86.

Named opcodes (handler read): 0000 end, 0001 loadmod, 01F4 if, 0208 caseidx, 0209 case,
0258 goto, 0259 switch, 0267 gosub, 026C ret, 026D retv, 0283 callmod, 02D1 strcpy,
02D5 lea (address of string), 0302 load (`dst = *src`), 0303 store, 0316/0317 stack alloc/free,
038E mov (`src, dst`), 038F addr, 0393 add, 0394 sub, 0396 and, 0397 or, 0399 neg, 039A shr,
039B shl, 03CE global, 03CF local, 03DE eval.

### Engine builds

Opcode tables come from the unpacked executables by `ScnTools opscan`: emulate the dispatcher
for all 65,536 values, then count operand reads (GetVar / SetVar / GetVarAdr) and raw reads of
the script pointer `[ctx+10h]` along each handler.

| | Sena Plus dump | Oreimo Plus dump |
|---|---|---|
| Interpreter | FUN_00428c30 | FUN_00423940 |
| GetVar / SetVar / GetVarAdr | 416000 / 415ab0 / 415e60 | 414e30 / 4148e0 / 414ca0 |
| Context register in handlers | EBP | EBX (EDI = &ctx->pc) |
| Valid opcodes | 1,658 | 704 |

Of the 704 opcodes both builds know, 699 have the same signature; 0x02DB (varargs, overridden),
0x04E7, 0x05D5, 0x05D6, 0x0C30 differ (Oreimo is the older engine). Either table decodes Oreimo's
SRC_MAIN identically; use the game's own table when one exists.

Decoding coverage of Oreimo's SCN: LAUNCH 100 %, PLAUNCH 97 %, EFCLIB 95 %, SRC_MAIN 83 % (rest is
data), START 54 %, TOPMENU 56 % - START and TOPMENU still have unhandled special instructions.

## 5. Oreimo Plus game flow (SRC_MAIN.SCN)

1. Start: `b[250]` (scenario number) indexes a 40-entry address table - resumes a saved game.
2. Run a TXT: set `filename = "p\oreXX.txt"`, `msg_no`, `msg_gno`, `save_no`, then `gosub 240`.
3. `ore01.txt`, then the menu: `callmod 203` with the 8 choice texts (頑張って考える, 公園に行く,
   コンビニに行く, 桐乃の学校を見学, 桐乃の部活を見学, ラブホに行きたい, レンタルルームへ, アキバへ行く);
   `a[780]` is the bitmask of routes already played (the menu shows only the rest), the
   result `g$sel` goes to `a[990]`, `eval "_D780|(1<<_D990)"` marks it, `switch` jumps to the route.
4. Each route runs `oreNN-01/-02/-03.txt` and returns to the menu. Routes 04, 06, 07 have a
   choice 中に出す / 外に出す after `-02` leading to `-02b` / `-02c` (`-02d` also exists).
5. When `a[780] == 255`: `ore10.txt` (ending), then back to `topmenu.scn`. The staff roll is
   part of `ore10.txt` itself (`e\staff_1-5.s25` with `$WAIT` and the music `m\oreplus_02`,
   then a click wait). The staff roll code in SRC_MAIN (L_02080: `staff0-5.s25`,
   `vor0x.ogv`, "staff.asm") is never called: it is a template shared by the engine's games.

## 6. The player

OpenShiina runs the game's own SCN files (START.SCN, TOPMENU.SCN, EFCLIB.SCN, SRC_MAIN.SCN, ...)
in an interpreter of the engine's bytecode (section 10), so every screen, effect and menu is the
game's own. It is split in two projects:

- `OpenShiina.Core` (`net10.0`, no WPF): archives and decryption (`Archives/`), decoders
  (`Formats/`), the game folder (`Game/GameData.cs`, `PlayerFolders`), the SCN interpreter with
  its embedded x86 code (`Scripting/`). The platform supplies the window, input, sound and fonts
  through `IScnHost`, `IScnSound`, `IScnMusic`, `IScnFonts`, `IScnShapes`.
- Both players run the interpreter on a thread of its own (Core `Game/GameThread.cs`), one
  engine frame per frame the window draws (and at least one every 1/60 s), and take the picture
  as BGRA; window events and messages (focus, Alt+Enter, keys, mouse buttons, the wheel) are
  queued for that thread, the title and the game's end posted back. Slow frames no longer hold up
  the window's input, moving or resizing. They show the window's picture (ScnVm.Window: what
  WM_PAINT has put there, see section 10), not the display surface. The X button sends WM_CLOSE to the scripts first
  (`GameThread.RequestClose`): the window closes when they let it (START saves its system data
  then), and at once when the game no longer runs or on a second try before they answer.
- `OpenShiina.App` (Avalonia, every platform) with the head `OpenShiina.Desktop` (Windows,
  Linux, macOS): `GameSession` holds the GameThread and the host;
  `GameView` shows the picture scaled and passes on keys, mouse, touch, the wheel and joystick 0
  (`SdlJoystick`: SDL3's first joystick, axes 0 / 1 and buttons 1 / 2, polled each frame the
  window draws) (`InputState`, Windows virtual keys; `0457` moves the system pointer with `PointerWarp`: SetCursorPos, XWarpPointer,
  CGWarpMouseCursorPosition, none on Wayland and phones, where the scripts see the new position
  until the pointer moves); sound is the Core mixer (`Audio/ScnMixer.cs`) on SDL3
  (`SdlAudioOutput`); text is `SkiaFonts`: GDI's font calls on SkiaSharp, with a Japanese
  stand-in for a missing face, measured as MS Gothic (cell = em, ascent 0.859 em, average width
  half an em); shapes are Core's `Platform/DibShapes.cs` (GDI's Ellipse, worked out from Windows' pixels).
- `OpenShiina.Windows` (WPF): `Scn/ScnWindow.cs` lets the GameThread run a frame on each frame
  WPF renders (Rendering raised twice for one RenderingTime counts once); sound through the same
  mixer on NAudio's wave output; text and shapes with GDI (Core `Platform/GdiFonts.cs`,
  `GdiShapes.cs`, Windows only), pixel for pixel as the games. Keys, mouse buttons and the
  joystick are read as the engine reads them, when the scripts ask: GetAsyncKeyState,
  joyGetPosEx(0) (a reading kept 4 ms; after a failure, no joystick for a second), the pointer
  with GetCursorPos / ScreenToClient against the picture's place in the client area. Keys, mouse
  buttons, the wheel and Alt+Enter also go to the scripts as window messages. (Before the thread, WPF's key events and `Mouse.LeftButton`
  changed only after the frame, so with slow frames a released Ctrl stayed held.)
- **Scaling** (2026-10-10; Core `Game/PictureScaling.cs`, setting "Scaling" in settings.json).
  At a scale that is not a whole number (x1.77 in a 1684 x 1061 window, x3.6 full screen on 4K)
  nearest-neighbour makes some game pixels one screen pixel wider than others (uneven strokes),
  bilinear blurs. Sharp (the default): whole game pixels as far as they fit, the rest bilinear.
  The Avalonia player (`GamePicture`, its own control drawing with SkiaSharp) runs the "sharp
  bilinear" shader (SkSL: within each game pixel the sampling point is its middle, on its edges
  it slides towards the next one over 1 / scale of a pixel, so only the screen pixels on an edge
  are mixed; Skia runs it on the GPU, or the CPU); the WPF player multiplies the picture by the
  whole part of its scale on the CPU (at least 2 above x1, so the bilinear rest makes it
  smaller) and lets WPF draw it with linear filtering. Whole numbers: x1, x2... centred with
  black around (nearest). Smooth: Catmull-Rom (Avalonia), WPF's high quality (Fant). Nearest:
  as before. The pointer follows the picture's place (`PictureScaling.Placement`, in screen
  pixels). The WPF player is per-monitor DPI aware (app.manifest, PerMonitorV2): it was system
  aware, so Windows stretched its window again on a screen with another scale.
- Both players start on a home screen of the games added (Core `Game/GameLibrary.cs`:
  library.json in the player folder with each game's folder, scheme name, .exe and when it was
  played; the .exe's icon read by `Formats/ExeIcon.cs` from its PE resources, without Windows):
  Avalonia's `LibraryView`, WPF's `LibraryWindow`. A folder on the command line plays at once.
- Both players open one sound output for the whole game (`ScnMixer`, 44.1 kHz stereo; sound
  buffers and music streams are voices of it). Opening a WaveOutEvent per sound took 17-33 ms
  (107 ms the first time) on the window's thread and made every hover sound drop frames.

An earlier player (2026-10-03 to 10-07, "Play Story", `OpenShiina.exe --story`) rewrote the
story engine in C# instead: StoryPlayer, ScnMachine (SRC_MAIN only), StageMath, its own WPF
screens. It was removed on 2026-10-07 once the SCN interpreter played Oreimo Plus through; it is
in the git history. What it found about the engine is in sections 3, 8 and 9.

## 7. Tools

```
dotnet run --project tools/ScnTools -- opscan oreimoplus <OREIMOPLUS_dump_SCY.exe> ops.tsv
dotnet run --project tools/ScnTools -- dis tools/ScnTools/tables/ops_oreimoplus.tsv out.txt SRC_MAIN.SCN
```

Finding problems:

- **crash.log** (save folder): on a script error both players append `ScnVm.CrashReport`: the
  module and offset of the address, whether the code there is still as loaded (the changed
  bytes), the task's module, base, pc and previous instruction, and the 64 latest embedded
  routine calls with their l[0..13], marking those that point into the damage. ScnBoot prints it.
- **stall.log** (save folder, both players): a frame that runs over 5 s gets where the
  interpreter is and the instructions it ran most in the next 2 s (GameThread's stall watch).
- **OPENSHIINA_PERF** (`Game/PerfMeter.cs`, both players): frames a second and the slowest frame
  in the title (on unless `0`); `log` also writes perf.log every second (slowest engine and
  picture times, main-loop rounds, heap in use, the costliest opcodes, C# routines and x86
  routines); timing every opcode halves the interpreter's speed. OPENSHIINA_X86JIT=0 keeps
  embedded x86 on the interpreter; OPENSHIINA_DATA moves the save folder (tests);
  OPENSHIINA_JOYPAD=0 hides the joystick from the scripts, =ini follows RIO.INI's Joypad.
  OPENSHIINA_PAINT=surface shows the display surface every frame instead of the window's
  picture (ScnVm.Paint), to tell a drawing problem from a repainting one.
  OPENSHIINA_TRACE=draw keeps the latest 200,000 drawing events (text started and where it
  ended, 0078, 04C4 / 04C5, 04C6, 04E2, 04F6, 07D0 and every rectangle invalidated, WM_PAINT,
  the window messages and what the message slot of 07E4 answered, with frame, round, slot and
  offset) and writes them to draw-trace.log in the save folder when the game closes
  (ScnVm.Trace.cs).
- **ScnBoot** (`tests/OpenShiina.ScnBoot`, `scnboot [folder] [frames] [picture folder] [every]`,
  60 frames a second of virtual time): SCNBOOT_PRESS="frame:vk[:frames],...",
  SCNBOOT_MOUSE="frame:x,y[:buttons[:frames]];...", SCNBOOT_SURFACES=1,2 (save more surfaces),
  SCNBOOT_INI="Key=value;..." (RIO.INI as the scripts read it, e.g. MovieMode=2),
  SCNBOOT_TRACE="from:to" (time and main-loop rounds of each frame), SCNBOOT_JIT=0|sync,
  SCNBOOT_JOY="frame:x,y[:buttons[:frames]];..." (joystick 0, 0-65535, read whatever RIO.INI says),
  SCNBOOT_WHEEL="frame[:-],..." (a notch away from / towards the user), SCNBOOT_CLOSE=frame
  (WM_CLOSE; presses and clicks are sent as window messages too), SCNBOOT_FOCUS="frame:0|1,..."
  (focus lost / back), SCNBOOT_HOT="from:to" (every instruction run in those frames, by module
  and offset), SCNBOOT_FILE="name=file;..." (a file of the archives read from a file on disk
  instead, e.g. a test scenario as REM00_00.TXT; the game folder is not touched); frame_NNNN.png is the window's picture (ScnVm.Window), _sK the surfaces asked for,
  SCNBOOT_VERIFY_NATIVE=1, SCNBOOT_PROFILE=1, SCNBOOT_STALL / SCNBOOT_STALL_REPORT (section 10),
  SCNBOOT_GPU=1 (the GPU mode; with SCNBOOT_VERIFY_NATIVE=1 the GPU's bytes are checked against
  the x86 code), SCNBOOT_GPUCHECK=cases (GpuCheck.cs: every GPU kernel against the CPU code on
  random pictures and rectangles, byte for byte, then the time of a large case each way; runs no
  frame).
- `tools/ShaderBuild`: compiles the GPU mode's GLSL compute shaders (src/OpenShiina.Gpu/Shaders,
  *.comp) to SPIR-V (*.spv, kept in the repository and embedded) with shaderc.
- **The GPU mode** (`IScnAccelerator`, `src/OpenShiina.Gpu`; 2026-10-07): Vulkan 1.0 compute on
  the best real GPU (discrete before integrated, never a software one). A C# routine keeps its
  tables and its reads and writes of the scripts' memory, gathers what the shader needs straight
  into mapped buffers (host-visible, cached: on a desktop card the shader reads them over PCIe,
  cheaper than the CPU writing into the card's memory) and writes the output back. Kernels
  (Shaders/, one invocation a destination pixel): `scale` (scale32 and scale16; the MMX code's
  16-bit sums wrap, and sums modulo 65536 do not depend on their order, so the bytes are the
  same), `enlarge16`, `subpixel32` (with its unpacked edge pixels), `rotatezoom` (0574; the
  64-bit steps in 32-bit halves). Each routine chooses CPU or GPU by timing its first large calls
  both ways (ScnVm.Accelerator.cs; perf.log says what it chose): on an RTX 2070 against 6 cores,
  1280 x 720, scale32 6.0 / 2.1 ms (CPU / GPU), scale16 6-8 / 2-3.5, enlarge16 1.1 / 2.4,
  subpixel32 1.0 / 2.9, 0574 1.5 / 4.5: routines that only mix a few pixels lose more to the
  copies over PCIe than they gain (a phone's GPU shares the CPU's memory). For the CPU mode too,
  enlarge16 and 0574 now share their rows out between the cores (2.5 -> 1.1 ms, 2.7 -> 1.5 ms;
  0574 row by row as before when its source and destination share memory).
- **The compositor on all cores** (04C4 / 04C5, ScnVm.Sprites.cs): the sprites stay in order,
  but each row of a frame goes into its own destination row, so a large frame's rows are shared
  out between the cores (per-thread row buffers). The pictures of
  ScnBoot runs are the same as before (Re: Rem Plus's zoom, 109 frames; Oreimo's park route to
  frame 8000, 161 frames), the compositor's time 5.6 -> 2.1 s and 5.5 -> 3.1 s. The GPU would
  have to take whole surfaces there and back for each call, as the pictures live in the scripts'
  memory, so it stays on the CPU.
- **More of the time found by SCNBOOT_PROFILE** (2026-10-08): `010E` (a file exists) decoded the
  whole file to know (64 ms for some of Re: Rem Plus's); it asks the archive's index now, as
  `0158` does. `04F6` / `0500` (BlendRows, two surfaces mixed in a fade) went row by row through
  local functions: tight loops now, the rows on all cores where no row reads what another
  writes (the source the destination itself or apart from it), 6.1 -> 0.9 ms at 1280 x 720
  (600 random cases the same as before, sources a few rows off the destination among them).
  The key stream XOR of 9 games' START (Oreimo 75C3D: 0xD94A4 dwords, a count of each game) is
  found by its bytes with the count left open and runs as C# (`xorstream`, 21 -> 3 ms a call;
  SCNBOOT_VERIFY_NATIVE on Oreimo's park route). `0294` with a slot that is "0276 compare16 / end
  f[0]" (Re: Rem Plus, Maki Fes!) compares in C# and leaves the slot as its last run would
  (l[0], l[1], f[0], its end): the same sorted table, 0.65 s -> 80 ms at start. `06D6` looked
  slow in ScnBoot only: its test host measures each piece of music with NVorbis; the players
  only read the file.
- **From a Galaxy S7's perf.log** (Exynos 8890, Mono, 2026-10-08; Maki Fes!): Release builds of
  the Android player use LLVM (6.2 -> 18.4 fps over the session with today's changes). Then:
  `CopyMemory` (04E2's rows, 02C6) made a buffer a call - page to page now (`MoveMemory`: exact
  memmove, 3,000 random cases); Maki Fes! / Re: Rem Plus have v2.50's own subpixel32 (START
  850F8 / 85C68: its loop over the rows from two source rows draws one row fewer and the
  partly covered bottom row is not drawn; `Subpixel32(v250)`, 387 random cases the same as the
  x86 code; translated it took 93 ms a call there); the MPEG-1 decoder decodes a picture's slices
  on all cores (ed.mpg has about 3 a picture) and ToBgr24 shares its rows out (7.1 -> 2.8 ms a
  1280 x 720 frame, the same bytes on all 3,115 frames); Ogg sounds are decoded as they play
  (ScnMusic.Source) instead of all at 06B1 (100 ms a voice there; only the headers are read
  first); 051E's stretches copied a pixel through a 3-byte array (CopyPixel24 now); 0568
  (RuleBlend) goes in 64 KB parts on all cores where its inputs are the output or apart from it
  (19.4 -> 4.0 ms, 300 random cases the same). ScnBoot's pictures of Re: Rem Plus's zoom and
  Oreimo's park route are the same as before all of these.
- **The game's pace** (GameThread, PlayerSettings.FramePacing, 2026-10-08): the players ran a
  frame for every refresh of the screen (WPF's CompositionTarget.Rendering, Avalonia's animation
  frames), four times the engine's pace on a 240 Hz screen, and drew all the time. The engine's
  loop sleeps with Sleep(1) (a tick of the Windows clock, 15.6 ms), so it goes at about 60 a
  second and next to no processor while it waits. Now by default the interpreter's thread keeps
  that pace itself (60 a second, later when 002A asks to sleep longer; timeBeginPeriod(1) on
  Windows, so that 1/60 s is not two ticks) and posts each new picture (FrameReady), which the
  window draws then; "every refresh" is still a setting. Thread pool workers no longer spin for
  work (UnfairSemaphoreSpinLimit 0), and the BGR to BGRA copy of a picture is a store a pixel.
  Re: Rem Plus's title (animated, 60 new pictures a second), whole machine (12 threads): Avalonia
  11.4% -> about 3%, WPF about 3% (it was 2.7-6.6% depending on the moment).
  The original REMPLUS.EXE (through Locale Emulator) at the same title: 2.9-3.0%.
- **Against the original in a heavy scene** (2026-10-10; i7-10750H, 12 threads, 1920 x 1080 at
  96 dpi). Re: Rem Plus's prologue kiss (REM00_00 text 114: `$A_CHR,50,2,640,360,130,3000,1`)
  held at 130 %: the scripts enlarge the picture every frame (enlarge32, START 866A8). Save slot 1
  made in ScnBoot (scratch sessions/2026-10-10-measure: savedata, zoom.ps1 loads it from the title
  and clicks once, then measures the process 10 s; the original reads the save through its
  registry DataPath pointed to a temporary folder for the run). The original runs as
  administrator (no clicks or closing from another program): zoom.ps1 -Manual watches the
  window until its picture is the held zoom and counts only the seconds it stays.
  Whole machine / one core: original 1.0% / 12%; WPF 12.0-12.1% / 145%; Avalonia 12.6% / 151%
  (both 60 fps, slowest frame 6.5-8.2 ms; working sets 297-339 MB alike). Per frame in ScnBoot
  (cpuframe.ps1): 18 ms of processor for 2.6 ms of wall time; on two threads (thread pool forced
  to one worker) 13.5 ms of processor, 7 ms wall, 6 of them enlarge32 (op 0276). The original
  spends about 2 ms a frame. enlarge32 is the MMX code done on 64-bit integers (X86Ops.Pmullw,
  Paddw, Psrlw, Packuswb: four lanes by shifts and masks), tens of operations a pixel, about six
  times the MMX; Parallel.For in bands of 16 rows adds about 5 ms of processor a frame. The zoom
  itself (3 s from 100 to 130 %) is cheap (0.7 ms a frame).
- **Vectors, fewer pieces, the changed part** (2026-10-10, after the measure above).
  - enlarge32's pixels are Vector128 of 16-bit lanes (lanes 0-3 used): pmullw, paddw, psrlw are
    one instruction each, and as the values packed never pass 255 packuswb is a narrowing. The
    same numbers (enl32test: 1,195 random cases against the x86 code, none differ); a 1280 x 720
    130 % zoom 12 -> 2.6 ms on one thread. subpixel32 already did its whole pixels on vectors;
    only its two edge pixels a row use X86Ops.
  - ParallelRows (Scripting/ParallelRows.cs) shares every pixel routine's rows (enlarge16/32,
    scale16/32, subpixel32, the compositor, BlendRows, RuleBlend, rotatezoom, ToBgr24, the WPF
    whole-number copy): equal pieces of at least 262,144 pixels, at most four, else the calling
    thread. Bands of 16 rows on 12 threads cost more processor than they saved. On the held
    zoom (ScnBoot, one frame): one thread 2.3 ms processor and wall, two or three pieces 2.4
    for 1.3 ms, four 3.3 for 1.5 ms, twelve 4.1 for 0.9 ms. OPENSHIINA_THREADS sets the most
    pieces (1: everything on the interpreter's thread). ScnBoot has the players'
    UnfairSemaphoreSpinLimit 0 now, so cpuframe.ps1 measures what they spend.
  - Held zoom, per frame in ScnBoot: 18 ms of processor for 2.6 ms wall -> 2.4 ms for 1.1-1.4.
    The players (zoom.ps1, two runs each): WPF 12.0% of the machine / 145% of one core ->
    2.2-2.4% / 26-29%, Avalonia 12.6% / 151% -> 3.1-3.3% / 37-40%, both 60 fps, slowest frame
    3.0-4.3 ms; the original 1.0% / 12%. Of the WPF player's 26-29%, about 14 is the frame
    (2.4 ms at 60 a second), the rest showing it (the whole picture changes every frame there).
  - The compositor (Re: Rem Plus's first route, 12,000 frames, one thread: 04C4 24.4 -> 14.9 s,
    about 1.1 ms a call for some 950,000 pixels): runs of one colour (method 3) are filled by
    copying what is filled after itself; pixels with an alpha each (method 4, and blend
    0x40000000's, alpha through T1) go four at a time: all 0xFF (88% of them in Re: Rem Plus)
    copies the colours with one shuffle, all 0 leaves the picture, else d + ((s - d) a >> 8) on
    twelve 32-bit lanes with the 0xFF ones copied. 200,000 random rows the same as the byte
    formula (scratch drawtest). Runs average 25 pixels (copies), 148 (one colour), 637 (alpha).
  - MPEG-1: the IDCT on Vector128<double>, two outputs a vector, each the sum of the same
    products in the same order (no fused multiply-add), so the same rounding; prediction with
    the formula chosen outside the loops; ToBgr24 four pixels at a time on 32-bit lanes. The
    same bytes on every frame of four movies (Re: Rem Plus, Maki Fes!, Azu Plus; scratch
    mpegbench), decoding 11.5 -> 7.1 ms a 1280 x 720 frame (ev02_mv1), 4.2 -> 2.0 (ed.mpg).
  - The changed part only (ScreenArea): ScnVm.TakeChangedArea gives the bounding rectangle of
    what WM_PAINT and BltToWindow changed since the last call (all of it at first). GameThread
    converts to BGRA only that part and what its back buffer lacks (the area of the frame
    before), and keeps what changed since the window took a picture; TakeFrame copies that
    into the window's buffer and gives it; the WPF player writes and marks only that part of
    its bitmap (and of the multiplied one), the Avalonia player skips frames that changed
    nothing. SCNBOOT_AREAS=1 checks it: a copy updated only in the areas given is the picture
    after every frame (Re: Rem Plus's first route and zoom, Oreimo's park route: no frame
    missed). In Re: Rem Plus's first route a third of the frames change under a tenth of the
    screen (the text), two thirds all of it (the scenes move).
  - Checked: ScnBoot's pictures of Re: Rem Plus's zoom (105) and first route (121) and Oreimo's
    park route (61) the same as before, and of all eleven games from their title for 6,000
    frames (61 each, built from the commit before; scratch allgames.sh) the same.
- **enlarge32** (ScnVm.Enlarge32.cs, 2026-10-08): the enlarging case of the zoom and pan, Oreimo
  START 77108 / Re: Rem Plus 866A8, in 10 of the 11 games (not Ero-On!, which has enlarge16), three
  builds (v2.47, v2.49, v2.50 signatures). A table of the destination columns that mix two source
  columns (an accumulator that loses sW a pixel, weights acc * 257 / sW by scale32's reciprocal
  division; the partly covered first and last columns their own, alpha lane scaled by the part),
  then rows of five kinds (partly covered top and bottom, from one source row or two mixed;
  whole rows from one or two), each with the x86 code's MMX formulas and its quirk (a used-up
  partly covered first column makes the rows from one source row take that pixel again). Each
  build checked against its x86 code on the interpreter (150 random cases each, 899 for Re:
  Rem Plus's). Re: Rem Plus's kiss zoom in the prologue ($A_CHR 50 to 130 %): 50 ms a frame
  translated, 9 ms with the C# version (4 ms the routine; rows on all cores).
- `tools/X86Gen` (an embedded routine to C# ahead of time; X86Jit now does it at run time) and
  `tools/GdiEllipseCheck` (DibShapes against GDI, and `dump` of what GDI draws).

## 8. START.SCN / EFCLIB.SCN findings (Oreimo build, used by the player)

Read from START.SCN with ScnTools after fixing 0x280 (`N2V`), 0x281 (`V V N2V`, local call) and
following `op_000C id, label` (function registration) as code. Command table at 0x3B020
(name + id); dispatcher `case idx@39C96`. Handlers: L_BG 3D467, L_CHR 3E0A5, DRAW 4192D,
DRAW_EX 423F5, EFECT 42EC3, SE 454A6, EX 46E58, A_CHR 4766C, WAITA 41B0B, L_MONT 3FA48.

- **Action queue**: A_CHR / L_BG / L_CHR / EX,9 push numbers with `callmod 0,321`; `$DRAW`
  (function 312) draws, then runs the queue (323, at 637D3) into 384-byte plane records at `b[980]`.
  Per-frame update = function 220 (L_133CD).
- **A_CHR last argument (wf)**: 1 = background animation (WAITA does not wait, a click does not
  end it); 0 = normal (WAITA waits, click finishes it). `$DRAW` itself never waits.
- **A_CHR 1-6** `cycles (0 = forever), amplitude, period ms (min 30)`; phase = elapsed % period:
  1 y -= sin(pi*ph/P)*A; 2 y += same; 3/4 triangle ±A/2 on y/x; 5 y -= sin(2pi*ph/P)*A/2;
  6 x += sin(2pi*ph/P)*A/2. 0 = stop at end of the current cycle, 9 = stop now.
- **A_CHR 40** target rect, **41** source rect (1/16 px), **42/43/44** pan to rect with easing
  linear / 1-cos (ease-in) / sin (ease-out).
- **A_CHR 60-63** rule fade of a plane: 60/62 in, 61/63 out then remove; 62/63 reversed rule.
  Plane update 13DB7: trs = 511·e/d (record +332 != 0, out) or 511 - 511·e/d (in), 0 = shown.
  16D5E calls the MMX routine 796E5 on the plane's alpha with imin / imax = 0 / trs (trs < 256)
  or trs - 256 / 255, rule value m (255 - m when record +336 says reversed): m >= imax keeps the
  alpha, imin < m < imax scales it by (m - imin + 1)·(0x8080 / (imax - imin + 1)) >> 15 (bias
  0 when imin = 0), m <= imin clears it. So bright rule pixels appear first and vanish last.
- **A_CHR 100-129** = function 306 (L_674FE) type c-100; **150** = type 13 (fade out, then
  remove plane), **151** = type 19 (fade in, default 1000 ms), **152** = type 0 (plane
  cross-fade, default 500). Type table at 0x680F0: 1-3,15 slide in from (0,800)/(-800,0)/(800,0)/
  (0,-800) easing 1; 4-6,16 slide out + remove; 7-9,17 / 10-12,18 same with easing 3; 20-23 /
  24-27 easing 2; 14 / 28 / 29 = move from the current position, easing 1 / 3 / 2, default 500.
  Move easing: 1 linear, 2 = end-cos(pi t/2)(end-start), 3 = sin(pi t/2). Fades are linear.
- **A_CHR 90,plane,ch** replays sound slot ch+11 at every loop cycle (footsteps), 91 stops.
- **L_BG** `file, reset, x, y, zoom%(100)`; reset 0 clears planes 1-9. **L_CHR** 5th arg =
  function-306 type (0 = 500 ms plane cross-fade on DRAW).
- **DRAW_EX** `kind, rule, ms, hidewindow`: always blocking (click ends it). kind 0 cross-fade,
  1 instant cut, 2 rule wipe, 47 same reversed; 37/48 also rule. 4th arg hides the message window.
  Rule blend (exe 437B40): weight of new = clamp(t + rule - 256, 0, 256)/256, t 0..511 -> kind 2:
  **bright rule pixels change first**; kind 47: dark first. Soft edge = full 256 levels.
- **EFECT n** (function 217, blocking): 0/1/2 = EFCLIB 34 shake with zoom-in a = 16/32/8 px,
  offsets (0,-a) (-a/2,-a/2) (-a,-a) (-a/2,-a/2), 15 fps, 2 rounds; 12 = EFCLIB 35 zoom pulse
  crop 16z x 12z, z = 1,2,3,2,1 at 15 fps; 4 white / 5 red 50 ms flash; 3 / 6 = EFCLIB 21
  with 255: native code at EFCLIB 1E9E XORs every screen byte with 255 (negative), held 50 /
  1000 ms, then the screen saved in VRAM 1 comes back.
- **EX,9** `0,count,width` / `1,slot,file` / `2,speed` / `4`: speed = px per second, sign =
  direction (positive moves the picture right). **SE** `file, mode (0 once, 1 loop, 2 once and
  wait, 3 load only), channel` (slot = channel + 11). **MUSIC** `file, loop, fade-in ms`.
  **VOICE** `file, loop, slot, wait`. **SE_FADE** `ms, channel`.

## 9. The other games (survey of all 11, 2026-10-03)

| Engine (START.SCN) | Games | TXT command table vs Oreimo |
|---|---|---|
| v2.47 | Oreimo Plus, Azu Plus | Azu: no `L_MONT`, `WAIT_L_MOVIE` (40 commands) |
| v2.49 | Homu, Yuru, Nyaru, Rikka, Sena, Kuroneko Plus | identical (42 commands, same ids) |
| v2.49 | ERO-ON | subset (34 commands, PRELOAD renumbered) |
| v2.50 | Maki Fes!, Re: Rem Plus | 53 commands: adds `WAITSE L_DELAY FADEVOICE L_SMOVIE L_ZBG L_ZBG2 LOOP DATE DELAYRUN DELAYRESET REGMSG L_PRIORITY`; opcodes numbered apart from v2.49 above 0C30 (section 10, "Engine v2.50") |

Every scenario command used in any game is in that game's table. Beyond what the Oreimo
player handles, the scripts use:

- `L_MONT ...,m,slots` (9 games; slot lists, section 2) instead of Oreimo's `M` codes.
- A_CHR 02 / 05 (loops, formulas known), 10, 11, 20, 50, 144 (Rikka); 43 / 44 (known).
- DRAW_EX kinds 11, 16, 17, 44, 45; EFECT 16, 17 (ERO-ON), 29 (Re: Rem); `EX,10,0`; SE mode 2.
- v2.50 only: `L_DELAY` (Maki Fes 133 uses), `DELAYRESET`, `MOVIE`; Re: Rem also `FACE`, `EMOTION`.

SRC_MAIN.SCN of every game follows the Oreimo pattern (opening, route menu that hides played
routes, ejaculation choices, ending). Across all 11 they use few instructions: mov / local / if /
goto / gosub / lea / switch / case / eval and arithmetic; TXT files run through `gosub 240`
(357 calls), menus through `callmod 0,203` or `callmod 0,270`; the rest is the staff roll's
drawing. The old story player (section 6) ran this subset with its `ScnMachine`:

- The script starts with `goto table[b[250]]` (table of code addresses): b[250] is the scene
  number set before each `gosub 240`, so loading a save (its a[] / b[] restored) or starting a
  chapter (b[250] = its scene) restarts SRC_MAIN and lands on that file, after the b[160] auto
  save request.
- `callmod 0,203,#5, mode, ?, table, kind, mask`: the table is a count byte and the option
  strings; kind 0 = text, 1 = text with the options in `mask` greyed (slot 314 at 160/255),
  >= 2 = pictures from SYSTEM.S25 400 (+10 per option; START 7031A). The answer goes to `g$sel`.
- Expressions: `_Dn` = a[n] (also the `_D` variables of the scenario files), `_Sn` = b[n],
  `_Ln` = f[n], `_Zn` = l[n], `{name}`, `shl(x,y)`.
- Drawing / staff roll instructions are skipped; the time (op 03BD) advances 100 ms per read.

### Text, choices, L_MONT (read for the old story player)

- **Message text** (style bank 0, START 22393): font `b[150]` = ＭＳ ゴシック, `_H24_X11_XZ23_Y29`
  (24 px glyphs, advance 11 half width / 23 full width, 29 px lines), white, text area from
  (126,456), 575 wide, right margin `_l` 701. Edge by `b[159]`: 0 none (default), 1 black shadow
  at (1,1), 2 outline. Speed `_w` = `b[3]` (default 2) x 18 = 36 ms per character. Kinsoku table
  `_P` at 222C4: no line start `。，、．：；゛゜ヽヾゝゞ々）〕］｝〉》」』】°′″℃￠％‰”―　・` and small kana,
  no line end `（〔［｛〈《「『【￥＄￡`. Gaiji `①...` = frames of `GAIJI.S25`; click wait icon =
  `SYSTEM2.S25` slot 20.
- **Choices** (function 203, 062A4): picture buttons in a fixed 2 x 4 grid, 391 x 86 px apart,
  plus the frame offset; played options stay visible with slot + 3 at alpha 160 and cannot be
  chosen. Text choices: slot 311 / 312 rows 100 px apart, first at
  y = 250 - (96 + (n - 1) * 100) / 2 - 48; text ＭＳ ゴシック 31 px (`_H31_X15_XZ30`), black
  shadow (1,1), centred between x 240 and 560 at row y + 32.
- **L_MONT** (function 304) queues like L_CHR (10001): its 5th argument is the plane
  transition of function 306, so an expression change cross-fades 500 ms on `$DRAW` unless
  A_CHR 152 gives another time.
- **Title** (TOPMENU.SCN): voice `d\CMA002`, `d\logo_gc` (1 s fade, 3 s), `d\white` (0.4 s),
  `d\caution` (0.6 s fade, until a click), `d\white`, then TITLE.S25 slot 0 with `m\oreplus_01`.
  Buttons (function 230) are TITLE.S25 slot groups 10 スタート, 20 ロード, 30 オプション, 80 おわる
  (slot + 1 = larger highlighted picture). After the ending SRC_MAIN loads topmenu.scn again.
- **Save / load pages** (function 247, 27D3C): SYSTEM.S25 2010 SAVE / 2011 LOAD, 2 x 5 slots with
  hit boxes 2200 (300 x 88 at 57,57, transparent) moved 352·col, 101·row; slot numbers 2300 + page
  (2309 = AUTO1-9 and QUICK); gold frame 2230 on the slot under the mouse; NEW 2220 on the slot
  saved last (b[219]); page tabs 2030 + 10·k (PAGE 1-9, AUTO; +1 highlighted, +2 current), BACK
  2170. Slots are page·10 + n; 90-98 = AUTO1-9, 99 = QUICK (QSAVE / QLOAD, no dialog). Saving
  over a slot and loading ask nothing. The message stored with a save is cut after 22 bytes + "...".
  900 = backlog page, 1000 = OPTION page. A player save is {file, message number, routes played};
  loading replays the file silently to that message.
- **Auto save** (function 197, 2DBD2): SRC_MAIN sets b[160] = 1 before every `gosub 240` except
  the opening (ore01); the message routine (3D1B1) then auto saves at that file's first message:
  slots 90-97 move down one (98 dropped) and the save goes to 90 (AUTO1).
- **Save thumbnail** (2CBF4): THSAVE.S25 is loaded as bank 26. Planes 9 down to 0: the first
  whose picture is in the table at 2D173 (141 event CGs) gives frame n; on plane 0 a playing
  movie (b[19] & 2, name b[420]) is looked up in the next table (24 `mv\*.mpg`, frame 3000 + n)
  instead. Then for that plane and the planes above, a picture of the third table (9 CGs) adds
  frame 2000 + n at (x·100/800, y·75/600). No match: the screen scaled to 100 x 75.
- **YES / NO dialog** (function 244, 231F4, argument = question slot): records bank 27 slot 0 (the
  screen), SYSTEM.S25 10 twice (black, alpha 128), the question 231 終了しますか？ (QUIT, title
  おわる) / 232 タイトルへ戻りますか？ (TITLE, only when b[167] = 0), buttons YES 220-222 at (293,229)
  and NO 210-212 at (427,229); a right click or NO returns -1.
- **Message window bar**: SYSTEM.S25 120 QSAVE, 130 QLOAD, 100 AUTO, 80 SAVE, 90 LOAD, 150 SKIP,
  140 OPTION, 110 TITLE, 190 QUIT, 180 × (hide window); +1 highlighted, +2 on.
- **OPTION page** (1000): volume knobs 1600 / 1400 / 1500 (music, voice, SE; 197 px range, arrows
  +10 / +20), ON 1820 / 1800 / 1810 and OFF +3, screen mode 1940 full / 1930 window, message
  speed 1130 高速 ... 1100 遅い (`_w` 0 / 18 / 36 / 54 ms), auto wait 1230 ... 1200, message skip
  1300 既読 / 1310 未読, TITLE 1050, BACK 1060. Engine defaults: sliders b[1] / b[2] / b[5] =
  19 / 39 / 26 of 49, speed b[3] = 2 (標準).
- **Backlog page** (900, semi-transparent): back 910, up / down 950 / 960, knob 901 between TOP and
  END; names in `_c255,240,0` (yellow). 2200 is the "NEW" frame of the newest save slot.

## 10. Running the SCN files themselves (approach 2) - opcode survey, 2026-10-06

Goal: run START.SCN, TOPMENU.SCN and EFCLIB.SCN of each game in the interpreter, as the
engine does, instead of reimplementing their behaviour by hand (sections 6 and 8). All 66 SCN
files of the 11 games were disassembled with ScnTools (Oreimo / Azu with the Oreimo table,
the others with the Sena table):

- **Opcode numbers are shared by v2.47 and v2.49** (not v2.50: see "Engine v2.50" below): the Oreimo table (v2.47, 704
  opcodes) is a subset of the Sena table (1,658); only 5 opcodes (02DB, 04E7, 05D5, 05D6,
  0C30) have different operands.
- **Oreimo's six files use 247 distinct opcodes** (START 237, TOPMENU 60, EFCLIB 50, SRC_MAIN
  51, PLAUNCH 27, LAUNCH 17). All 11 games together use 273; 234 are used by every game.
- Beyond Oreimo's set: Homu, Yuru, Nyaru, Sena, Kuroneko 1 opcode each; Rikka 2; ERO-ON and Azu
  3; Maki Fes! 22 and Re: Rem Plus 21 (engine v2.50). Running Oreimo's files covers almost all
  of the other games.
- 56 of Oreimo's opcodes were run by the old `ScnMachine` (flow, variables, arithmetic,
  strings, calls); 29 have names. The list with uses per file is `docs/scn-opcodes.tsv`.

The handlers of opcodes below 0x561 are not in the Ghidra decompile (its jump table has "too
many branches"): they are read from the x86 at the handler addresses of the opcode table, and
the functions they call from the decompile. First findings:

- `0002 slot, name`: loads another SCN module into one of the 1,000 script slots
  (executable memory, like `loadmod`).
- `000C id, label`: registers a script function (section 8).
- `0066`, `0078`, `0079`, `0083`, `00A0`, `00B4`, `00B6`: a text-file reader (per-file
  records at 0x4C7D28, current file in context +0xFF8): START.SCN parses the scenario TXT
  itself, character by character (Shift-JIS aware).
- `0032` / `0033`: clear / set a global flag (0x487F28).

### The interpreter as the executable runs it (OpenShiina.Core/Scripting/ScnVm)

- **Slots**: 1,000, each both a module and a task (context records of 0x101C bytes). Boot:
  RIO.INI `Scn=start.scn` is loaded into slot 0 and started (the built-in default name is
  autoexec.scn). `0001 slot, file` loads a file into a slot and starts it; `0002` loads without
  starting; `000C id, label` makes slot `id` run a label of the current module (its parent is
  the current slot; with bit 31 set the label is an absolute address); `000D slot` starts a
  slot from its entry; `0009 slot` resets its stack and named variables.
- **Calls**: per-slot return stack of 1,000 dwords (a push goes down). `0262 label`: push base,
  push pc, jump. `0267 slot` (gosub): the same, then run the slot's code (base = its module).
  `026C`: pop pc, pop base. `026D n`: the same, then drop n values. `0283 result, slot, #n, args`
  (callmod) / `0281 result, label, #n, args`: frame {result address, args (first argument at the
  top), n} in the context, then like gosub; `0280 #n vars` takes the arguments, `0285 v` stores v
  into the result address and returns.
- **Variables** (GETV / SETV / GETADR at 414E30 / 4148E0 / 414CA0): g, b, a arrays and s byte
  flags are global; f is 1,000 dwords per slot; l is on the slot's stack at the stack top +
  index (`0316 n` salloc / `0317 n` sfree move the top); named variables live in per-slot scope
  tables (`03CF local #n names`, `#0` leaves the scope; `03CE global`), `{name}` falls back to a
  global. Odd operand kinds dereference; flag 0x80 adds the base of the running module; a string
  operand's value is its address; 0x11 is an expression.
- **Scheduler**: every frame each running slot (flags bit 0, not 2 or 8) runs until it yields:
  after `0034` (the frame wait), after every instruction while `0033` is in force (`0032` ends
  it), after the instruction budget, or when a handler returns 1 (quit) / 2 (script error).
  `0000 v` ends the task; a non-zero v quits.
- **Expressions** (rio/Calc.cpp, FUN_00403340): doubles; `+ -` bind loosest, every other binary
  operator shares one level, left to right; `,` sequences; assignments `= += -= *= /= %= &= |=
  ^=`; `?:`. Terms: numbers (decimal, 0x, fractions), `_Xnnn` (exactly three digits: D = a, L =
  f of the slot, M = g, S = b, Z = l) with suffix f / i / o (float bits, signed, plus the slot's
  module base), `{name}`, the calculator's letter registers (double arrays, `dim(A[n])`) and
  sin asin cos acos tan atan atan2 sqrt int rnd rand pow fabs abs log ceil floor shl shr sar
  RGB min max peekb peekw peek dim -( !( ~(. Oreimo uses rnd, sin, RGB, sqrt and - + * / | &.
- **Core opcodes read so far**: 01F4 / 01FE / 01FF if with unsigned / signed / float compare;
  0212 offset, n + 0213 counter, target (a counted loop that keeps its counter in the code);
  0398 xor, 039D rotate left; 0302-030B load / store dword, byte, word, with offsets; 02C6
  memmove, 02C7 memset; 02D0 strlen, 02D1 strcpy, 02D3 strcat, 02D4 strcmp, 02D6 stricmp;
  02E4 / 02E5 / 02E8 a per-slot data reader; 03AC rand() % n, 03AE srand (MS C runtime rand);
  03BD time in ms; 03B7 / 03B8 local date / time; 03D0 leave scopes; 02EE / 02EF save /
  restore the 0033 flag; 001E / 001F set / clear task flag 4; 03DE "expression", mode,
  result: mode 0 stores the value as an int (truncated), mode 1 as the bits of a float, which
  START reads back with `_Xnnnf` (the phase of the A_CHR 1-6 loops, eased slides and pans, the
  1/16-pixel positions: 86 of its 231 evals).
- **Input**: 03E8 key state of a virtual key; 03E9 button mask (keyboard and joypad); 03EA the
  same with key repeat; 03EB mask waits for buttons.
- **Embedded x86** (`0276 label`): calls machine code inside the module with a pointer to
  {b, a, s, f of the slot, the stack top}. Oreimo has 19 such routines (17 in START, 2 in
  EFCLIB): 75A84 CPUID (bits: 1 Intel, 2 AMD, 10h MMX, 20h SSE, 40h SSE2, 80h SSE3, 100h SSSE3,
  200h SSE4.1, 400h SSE4.2, 800h SSE4a; stored in gCPUID), 796E5 the MMX blend of rule fades,
  EFCLIB 1E9E the negative; the others are still to be read. They work on picture buffers in
  script memory, so they have to be reimplemented one by one.

### Start-up of START.SCN (as ScnBoot runs it, 2026-10-06)

- Checks: engine version `03C0` (247), window size `09F6` (RIO.INI 800 x 600), colour depth
  `09E2` (GetDeviceCaps BITSPIXEL >= 16), DirectDraw `06C2` / DirectSound `06A4` ready, no other
  copy running (`07B2` FindWindow), `D382` / `AA82` switches (AA82 enables file writes).
- Azu Plus also checks the executable: `03C2 v` gives 0x13B41BC, a checksum the engine makes at
  start (FUN_004374D0 / FUN_004085C0: checksums of its code, its "riox" section and its version
  resource), and START 0x002F0 stops with "Program Revision Error" unless v ^ 306723180 is
  1804523910 (or when bit 0 of 0x487F24 from `03C0` is set). OpenShiina gives the value of an
  unchanged AZUPLUS.EXE (0x79C6E0EA). No other game reads it.
- Registry (`00FA` key, `00FF` exists, `00FE` string, `00FD` number): HKCU\software\GrandCross\
  <title>, DataPath (the save folder; START appends `oreimoplus_save.bin`) and InstMode (0 - one
  install choice in SETUP.INI). OpenShiina emulates it: DataPath is the host's save folder.
- Settings: `0104 "RIO.INI"` then `0107 section, key, v` = GetPrivateProfileInt (default = v).
  Keys read: FullScreen, WideMode, SSE2, MovieMode, KeyRepeatDelay / Speed, CacheSize(NA)...
- System save: `010E` exists, `0154` open (offset, size, packed size, handle), `0156` read,
  `0155` close, `0158` size, `0123` delete, `00D2 buffer, n, file` write. A file of the wrong
  size is deleted and START starts from defaults. The file is unpacked by embedded routine 75C3D.
- Surfaces: `0546 n` makes surface n the window size (descriptor at 0x7DDF90 + n * 0x2C:
  HBITMAP, HDC, pixels, BITMAPINFO, palette, flags | 2, DirectDraw surface, width, height, bpp,
  pitch), a top-down DIB of RIO.INI `Bpp` bits (default 24: BGR, pitch 2400). Oreimo makes 2, 3,
  4, 7, 8, 20, 21; the engine itself makes 0 to RIO.INI `Vram` - 1 (default 2) at start-up:
  0 is the screen (0x13B43E4 = the shown surface), 1 the composed picture. `04B5 n, count` / `04B6 n`: tables of
  32-byte entries at 0x7DC938[n].
- `0500 level`: surface 0 = surface 1 at a brightness (0 black, 255 copy, else FUN_004396F0:
  with MMX, (v * (level * 256 / 255)) >> 8 per byte in 8- and 4-byte blocks, bytes outside them
  by the table level * v / 255; 256 or the last level again: nothing).
- Window events: each runs a slot to its end (FUN_0042BF60): WM_TIMER the slot of `0AF0`
  (Oreimo 193, every 100 ms from `0AFA id, ms, v`; `0AFB` KillTimer), Alt+Enter `0794`
  (252), focus back `076C` (253: music on, play time counted again), focus lost `076D` (254:
  music paused; 0x488090 runs in WM_ACTIVATEAPP's active branch, 0x488094 in the other - they
  were swapped here until 2026-10-07, so music stopped when the window got the focus back),
  the players count a minimised window as not in front (Windows can activate it again while
  minimised; the original then gets WM_ACTIVATEAPP false as the next window is activated) and
  take the focus from the activation events themselves, since IsActive is not always up to
  date while they are raised (coming back to the window left the game paused),
  WM_CLOSE `078A` (255), and
  every message first `07E4` (248; see "Window messages" below). `00DD` mounts each WAR archive; `0A8D` detaches the IME.
- The main loop is not tied to frames: it pumps messages and runs every task once per round
  (`0033` makes tasks yield after every instruction, `0032` ends that); pictures reach the
  window only through the drawing opcodes.

### Text, pictures, sound, input (ScnVm, 2026-10-06)

- **Text records** (0x4C7D28, 0x161C bytes, 38 of them; 18-37 are scratch copies for measuring
  a line): font fields for CreateFontA, colours (text 0x144, edge 0x147, shadow 0x14A, background
  0x14D), position, pitch, waits, kinsoku lists, the text pointer (+0x160). `0096 n` picks the
  task's record, `0084 surface, text` lays out and draws at once (-1: control codes only),
  `0083 surface, text` draws over frames (task flag 8: the main loop runs one text step a round),
  `00A0 text` / `00A1 slot, v` keep a snapshot and redraw it incrementally, `0066` reads a
  character, `0078 x, y` / `0079 x, y` position, `00B4` / `00B6` layer and offset.
- **Control codes** (FUN_00431960): `_F face/ _H height _h width _f weight _I _o _q quality
  _q+ / _q- antialias _c r,g,b[,a] _ca _E r,g,b,dx,dy (edge) _S r,g,b,dx,dy[,a] (shadow) _b
  (background) _e effects _X pitch[,spacing] _XZ wide pitch _Y / _R line height _x _y _l right
  edge _i indent _P kinsoku lists (separated by //) _w ms per character _W ms wait _s skip keys
  _t / _t/ / _t! callback slots _a align (measures the line in a scratch record) _r new line
  _g layer _d _p fixed extent _u ruby record _( _) save / restore position _Z _z _A _K _k`;
  outside them `*n` / `+n text/` macros, `@` katakana, `|` full-width, `$`, `{ruby}`, `~`.
  Numbers are FUN_004317F0 (decimal, 0x hex, "," "." " " end them, `{name}` reads a variable).
- **Glyphs**: GetGlyphOutlineA GGO_GRAY8_BITMAP (0-64) of the record's font, cached per record,
  blended by the engine (FUN_004325A0): a = (v * 255 >> 6) * alpha >> 8, d += (c - d) * a >> 8
  in unsigned 32-bit arithmetic; edges / shadow are the same glyph drawn first at offsets. Fonts
  are made lazily; a font handle deleted by the alignment measure stays dead in the record (GDI
  semantics kept). Half-width characters are made full-width with the table at 0x485504.
- `02DB dest, format, args` = wsprintfA through the engine's "call a DLL function" path;
  `02DA dest, format, list` = wvsprintfA.
- **Pictures**: slots 0xBCF2F0[0..256]. `04B0 n, file` loads an S25 file as it is, turns frame
  and row offsets into pointers (FUN_00439DD0), clears the magic and delta-decodes rows of
  frames with flag 0x80000000 in place, once per row reference (FUN_00403430). `00C9 file, v`
  loads a file into memory, `04B2 n, address` makes it slot n. `055A n, w, h, bytes, frames`:
  a blank picture of raw rows (code 0x80000000 | (w * 4 + 6), w, pixels). Pixel (x, y) of a
  frame = row pointer + 8 + x * bytes (FUN_00410520). `04CE n`: surface n black.
- **Sprite lists**: `04B8 n` current list, `04B9 n, v` its table, `04BA` empty, `04BB n` / `04BC v`
  count, `04BD slot, frame, flags, priority, x, y, -, extra` add (32-byte entries), `04C4 n`
  composes the list into surface n (FUN_00439E10): shown entries (flags bit 31) in increasing
  unsigned priority, each through FUN_0043A040. The executable picks its kernels by its own CPU
  check (0x494F40: 1 PentiumPro, 2 MMX, 4 SSE = 7 on any current PC, path 0x4409E0); with no
  blend mode the runs are: 0-1 transparent, 2 BGR copy, 3 one BGR, 4 ABGR each (255 copies,
  0 skips, else d + ((s - d) * a >> 8)), 5+ one ABGR blended the same way (alpha 255 included).
  The MMX blocks give the same bytes as the scalar code. Blend modes (bits 28-30 with alpha
  bits 0-8, table set-up 0x44346A) and modes 0x0C000000: see "More opcodes and compositor modes"
  below.
- Picture queries and the rest of the list opcodes: `04C9 slot, frame, v` (1 when the frame
  exists), `04C8 slot, frame, l, t, r, b` / `04CB slot, frame, w, h` (frame offset and size,
  FUN_004116A0; a missing frame is a script error), `04C5 n, l, t, r, b` (compose with a clip
  rectangle), `04C6 n, show` (surface 0 = surface n at the last `0500` brightness, always
  redone), `04C7 x, y, v` (FUN_004114B0: from the highest priority down, shown or not, the
  first entry with a run of method >= 1 under the point gives its word 6; -1 for none; a missing
  picture or frame ends the search), `04D3` / `04D4` (0x4880B0 / 0x4880B4).
- Mouse: `0456 x, y` cursor in client pixels (scaled back in full screen), `0457 x, y` sets it,
  `0459 v` DirectInput buttons (1 left, 2 right, 4 middle; 0x13B52BC swaps left and right),
  `0492 x, y` window point -> picture point ((v - offset) / scale, in place).
- `04E2 dst, x, y, w, h, src, sx, sy`: rectangle copy (clipped to dst only, FUN_00417900);
  `04F6 dst, x, y, A, ax, ay, B, bx, by, w, h, a, b`: dst = A * a / (a + b) + B * b / (a + b)
  (FUN_004396F0; B with bit 31 = a grey level). MMX is used when 0x13B5300 is set (RIO.INI MMX,
  default on): blocks (A * m + B * (256 - m)) >> 8 with m = a * 256 / (a + b), the bytes before
  an 8-byte aligned destination and the last 1-3 from two tables built by stepping a counter
  (FUN_004396B4). `07D0 l, t, r, b` = InvalidateRect (the frame is shown); `002A ms` = Sleep.
- **Sound effects**: DirectSound buffers 0x7DAC20[0..256]; files are made RIFF WAVE first (PAD,
  OggS and OGV decoded). `06A6 n, file`, `06B1 n, address` make a buffer, `06A7 n, flags` plays
  from the start (1 loop), `06A8` stops, `06A9 n, cB` volume, `06AF n, v` DSBSTATUS (-1 none),
  `06B0` releases, `06A5` releases all. Values above 0xFFFF are buffers themselves.
- **Music streams** ("Synthia PCM", FUN_00453220 / 00454FB0 ...): `06D6 source, flags, v` opens
  a stream (flag 1: source is a file in memory from `00C9`, else a file name; Oreimo passes 9)
  into the table at 0xBCDF44; `06D9 s, flags` plays from the start (2 loop, bits 16-23 loop
  count; the handler adds 0x10 / 0x40 / 0x80 from engine settings), `06DA` stops, `06DB` /
  `06DC` pause / resume (status bit 4; START does it on losing / getting the focus), `06DF s, v`
  volume 0-100 through the table at 0x4A15E0 (1000 * log2(v / 100) hundredths of a dB, 0 =
  -10000), `06E8 s, v` status (1 playing), `06D8` closes, `06EF s, start ms, loop ms, count`
  (-1 keeps a value; an error on streams that are not compressed). START's music routine
  (0x202C2) loads `m\*.ogv` with 00C9, opens it with flags 9, sets 06EF and plays.
- **Input** (FUN_00413630, DirectInput scan codes): 1 up, 2 down, 4 left, 8 right, 0x10 cancel
  (X, right button), 0x20 decide (Z, space, return, left button), 0x40 Esc / Home / Numpad 0,
  0x80 End, 0x100 Ctrl, 0x200 Tab, 0x800 middle button, joystick 0 too. Key repeat
  (FUN_00413810): a press at once, then after KeyRepeatDelay (300) every KeyRepeatSpeed (50 ms).
  `03E8 vk, v` GetAsyncKeyState, `03E9 v` buttons, `03EA v` with repeat, `03EB mask` waits for a
  release and then a press.
- **Joystick** (same function, v2.47 and v2.49 alike): only when RIO.INI's Joypad (or Joystick,
  read after it) is not 0, winmm `joyGetPosEx(0)` with X / Y / buttons: X < 0x2000 left, X >
  0xDFFF right, Y < 0x4000 up (not 0x2000), Y > 0xDFFF down, button 1 0x20, button 2 0x10; any
  of them sets 0x13B4404 ("the joystick is in use", read by `03F2` and the engine's own menus;
  the window procedure clears it on mouse moves and clicks, sets it on keys). All eleven games
  ship with Joypad=0; the players read the joystick anyway (OPENSHIINA_JOYPAD), ScnBoot as RIO.INI says.
- **0x400** is never in the mask `03E9` gives, though START's button routine (0x1871D) keeps it
  with the held bits (`and 1295`). The window procedure (0x435xxx, not in Ghidra's output) ORs
  0x420 into the masks 0x13B43FC / 0x13B4400 on WM_LBUTTONDBLCLK (0x20 on a left press, 0x10
  right, 0x800 middle); only the engine's built-in menus read those (FUN_00411770 / FUN_00411B40,
  opcodes `0B72` / `0B86`, which none of the eleven games uses).
- **Window messages** (window procedure 0x4355B0; Ghidra leaves it out, read from the x86):
  every message first goes to the slot of `07E4` (0x4880A8) with l[0] the window, l[1] the
  message, l[2] wParam, l[3] lParam pushed on its stack; an "end" other than 0 takes the message.
  Then the slots of `0849` (message -> slot pairs, 0x7DB128; `0848` clears, `084A` removes; no
  game uses them), then the engine's own handling: WM_CLOSE runs the slot of `078A` and closes
  only when its end is not 0 (no slot: closes); WM_ACTIVATEAPP the focus slots; WM_PAINT (after
  `07D0`'s InvalidateRect) shows the back surface; WM_xBUTTONDOWN / UP keep the button masks
  0x13B52AC (held) / 0x13B52B0 (pressed since the last read) / 0x13B43FC / 0x13B4400 (with 0x20,
  0x10, 0x800, and 0x420 for a double click); WM_MOUSEMOVE clears 0x13B4404.
  START's message slot (248, 0x17D11) takes nothing (end 0) but watches: WM_ACTIVATEAPP (b[17]
  bit 4: read the keys; START sets it at boot too), WM_MOUSEWHEEL (b[18] |= 1 away from the
  user, 2 towards: its button engine (0x7CDED) gives the turns to buttons - a turn away opens
  the backlog, as in the game), WM_LBUTTONDOWN / WM_RBUTTONDOWN (end AUTO / SKIP, b[226] = 0),
  WM_KEYDOWN 'A' (AUTO on / off: b[226] 2) and 'S' (SKIP on / off: b[226] 1), and WM_PAINT
  (in SKIP with b[240] & 2: a clock drawn with sprites 4100+). Oreimo's START closes
  with slot 255 (0x01363): it saves its system data (`gosub 330` / `333`), frees its blocks and
  ends with 1 - no question asked; a game closed without it loses what it keeps there.
  ScnVm: `Notify` (focus, Alt+Enter as WM_SYSKEYDOWN), `WindowMessage` (keys, buttons, wheel),
  `CloseWindow`; WM_PAINT is sent at the start of the frame after `07D0`, WM_TIMER before the
  timer slot. Checked in ScnBoot: a turn away opens the backlog, 'S' skips to the first choice,
  'A' turns AUTO on, closing writes the save file (it did not before).
- **Focus** (2026-10-10). RIO.INI's `Background` (read at start into 0x13B43D0, then `07E5` /
  `07E6`) is what `07E8` gives; START puts it in b[17] bit 1 (Re:Rem Plus 0x00B08; bit 2: in
  front). The focus slots (Re:Rem Plus 0x04B75 back, 0x04BFE lost; Oreimo Plus 0x01289 /
  0x012F0) do nothing when it is set; when it is not, losing the focus pauses the music (`06DB`)
  and the movie (`callmod 364`), takes the time from b[52] (the scripts' clock is now - b[52]) and
  empties the file cache (Re:Rem Plus L_24EF1, Oreimo Plus L_1F30C); getting it back adds the
  time to b[52] and resumes music and movie. The scripts count on the engine standing still
  meanwhile: on WM_ACTIVATEAPP the window procedure (0x435858) keeps "in front" in 0x13B43C8
  and, with Background 0, in 0x13B43CC, and the main loop (FUN_00412880) waits for messages
  while 0x13B43CC is 0. `07E8` gave the never-set 0x13B43D0 and nothing stood still, so every
  game ran the slots while going on: animations jumped by the time behind, a movie went black.
  Background is 1 in eight games, 0 in Ero-On, Azu Plus and Oreimo Plus. ScnVm.Paused: behind,
  with Background 0 or the setting "Pause behind other windows" (on by default), GameThread runs
  no frames, the scripts' clock (ScnVm.Clock, 03BD and every timing) stands still and the mixer
  is silent without reading its voices, so the game goes on where it was (b[52] moves by
  nothing). Checked in ScnBoot (SCNBOOT_FOCUS, SCNBOOT_PAUSE): Re:Rem Plus's first route and
  Oreimo Plus's park route with the focus lost for 5-8 s at a time give the pictures of a run
  without (Oreimo: a line's text appearing a frame or two later, the cache read again). The mouse buttons the engine reads
  (FUN_00413630) come from WM_xBUTTONDOWN / UP (0x13B52AC), not GetAsyncKeyState: the press on
  the title bar that brings the window back is no click (the WPF player counts a button only
  when it went down in the window; it skipped a line).
- **The window's picture** (ScnVm.Paint.cs): the engine draws into the display surface
  (0x13B43E4, a DIB) and the window shows it only where it is repainted. With RIO.INI's `Render`
  unset (all eleven games; 0x487F30 = -1) WM_PAINT is GDI (FUN_0040DB10: BeginPaint, the
  surface blitted, EndPaint), so only the invalid region changes (Render 1-3 = DirectDraw /
  Direct3D / OpenGL blit all of it). What invalidates: `07D0 l, t, r, b`; `04C6 n, 1`, `0568`
  and `056C` into the display surface (all of the window); `0564` / `0566` (they draw on the
  window and invalidate their rectangle); text drawn by the engine into the display surface
  without a layer (the character's rectangle, FUN_00417560); full screen on / off. `07D1` is
  UpdateWindow (WM_PAINT at once); otherwise WM_PAINT comes at the next pump of the queue, after
  the round. OpenShiina adds one of its own: `04E2` into the display surface invalidates where
  it copied (see "04E2 into the display surface repaints there" below). Sprites, fades (`04F6`), BitBlt / StretchBlt, fills and movies only draw into
  surfaces. The hosts show ScnVm.Window, so what the original never put on the screen stays
  off it: the save screen's cursor (START 0x2BE6C, frame 2230 of slot 20, 49..365 x 49..153)
  is drawn after the slot (57..357 x 57..145) is composed again, and only the slot is
  invalidated - showing the whole surface left an orange ring after the pointer moved away.
- **0083 waits** (2026-10-07): the task stops at `0083` until the main loop has drawn its text
  (flag 8, a step a round); a slot run to its end (events, callbacks) draws it at once. Until
  then the task ran on, so each `0083` replaced the text of the one before it unfinished: the
  backlog (START 0x2FE10: 【, name, 】, _r, a measuring pass, the line) showed only the last
  line it drew - the voiced lines, drawn again every frame (menu item flag 256), lost theirs.
- **04E2 into the display surface repaints there** (2026-10-07, OpenShiina's own, not the
  executable's): START's fade into the title (slot 212, 0x197AF) under Ctrl (skip) copies the
  title into the display surface with `04E2` and no `07D0`, after the title menu (0x7C7D4 /
  0x7C7FC) has saved the composed title to surface 1, put the old picture back from surface 4
  and let a WM_PAINT show it: the window stayed white but where the pointer went over buttons.
  Not checked against the original.
- **Text over rounds**: `0083` text goes on a character a round, and the engine's rounds take
  no time; the backlog draws its lines again every frame, so a frame that ended at the
  `07D0` showed them half drawn (the first line came and went). RunFrame now goes on with rounds
  while a task's text goes on without waiting for time (text that waits between characters
  still appears over time).
- **Wheel** in the engine: WM_MOUSEWHEEL also sets 0x13B52B4 to 1 (away from the user) or -1,
  read only by text: with `_s` key 8 a turn ends the text's waits (FUN_00432F00), and `0083`
  clears it. START's message window sets `_s13` (decide, Ctrl, wheel), but that only counts
  when the engine types the line (`0083` at 0x0486B, when b[240] & 2 is clear); in normal play
  START shows the line itself, a character at a time fading in (0x0579F: `00A0` / `00A1`, the
  characters drawn as sprites), and ends that on a click, Return or Ctrl read with `03E9`.
- **Heap** (2026-10-07): GlobalAlloc / VirtualAlloc blocks come from ScnVm.Allocate and go back
  with ScnVm.Free: `04B1` and every opcode that puts a new picture into a slot (`04B0`, `055A`,
  `055C`, `04B2`) free the slot's picture - also a block `04B2` put there: START loads pictures
  with its cached loader (callmod 215), `04B2`s them and frees them only with `04B1`, never
  `02BD` - `02BD` frees a block of `02BC` / `00C9` / `00CE` (slots showing it no longer own it,
  so it is not freed twice), `0547` and a surface made again free the surface's bitmap. Free ranges are
  merged and used again (best fit) and kept zero (whole pages given back to the system). No
  block crosses a 16 MB boundary (one over 16 MB starts on one; see "Flat memory"). Before, the heap
  only grew: after a long play it reached the modules' code at 0x40000000 and pictures were
  written over START.SCN (unknown opcodes in START). Running into the code now stops with "Out
  of script memory".
- Named variables (`03CF local`) live in per-slot scopes; their memory is used again when the
  scope is left (the start-up fade alone declares thousands in a few seconds).
- Status: START, TOPMENU's logo, white and caution screens run and are pixel-identical to the
  extractor's pictures (ScnBoot saves the shown surface as PNG; `OpenShiina.exe` shows it).
  Music streams run too (the title track, 281 s, loops). The title screen with its menu runs;
  against the 1.5 player's title only 858 pixels on anti-aliased button edges differ by 1-2
  (ScnVm uses the executable's d + ((s - d) * a >> 8)).

### Into the game (ScnVm, 2026-10-06)

- **Embedded x86** now runs on an interpreter (Scripting/X86Cpu, Iced for decoding) over the VM's
  flat memory: integer, string and MMX instructions; `cpuid` reports "GenuineIntel" family 6 with
  FPU, CMOV and MMX only, so Oreimo's CPUID routine gives 0x11 and every script takes its MMX
  paths (the SSE paths in all 11 games are never run). A survey of the 230 routines of the 11
  games (57,393 instructions) found about 100 mnemonics; xmm ones only on SSE2 paths.
- Title menu: the menu engine (START 230, "menu5.asm") selects with the mouse (`04C7` hit test)
  or the keyboard cursor; decide (0x20) acts only on the item under the cursor. Moving with the
  keyboard puts the mouse on the item's middle (`0457`), so the hit test follows.
- `00C8 file, address` loads a file into script memory; `04B1 n` frees a picture slot; `055B
  slot, frame, x, y, w, h, colour` fills a rectangle (4 bytes a pixel: the dword; 3 bytes: its
  bytes 2, 3, 1); `055C slot, surface` copies a surface into a new picture; `0560` / `0561` set /
  get a frame's offset; `0FD4` row bytes; `0BCF` strncmp, `0BD4` strstr; `02C1` GlobalMemoryStatus;
  `03EC` DirectInput key with repeat; `00CE`-`00D1` background file loader (done at once here);
  `0A30` / `0A31` disc check / extra folder.
- **32-bit pictures** (text and button layers): `0562 picture, frame, x, y, slot, frame` draws a
  frame over one (FUN_004436F0 / 004437C0, one entry): source alpha 0 skips, 255 or a transparent
  destination copies, else a = (0xFE01 - (255-sa)(255-da)) * 0x10203 >> 24 (32 bits) and each
  channel (s sa 255 + (255-sa) da d) * (2^24 / a) >> 32. Text drawn into a layer ("_g n,i" /
  `00B4`) uses tables made at start-up (FUN_00408EB0): share[da | sa << 8] = trunc(255 * (A/B) /
  (1 - A + A/B)) with A = sa/255, B = da/255 (255 when da = 0), alpha = 255 - (255-da)(255-sa)/255.
- Compositor mode 0x40000000 (alpha a): tables T1 = a/256 and T2 = (256-a)/256 stepped as in
  FUN_004396B4; BGR runs blend two sources ((s a + d (256-a)) >> 8 in MMX blocks, T1[s] + T2[d] at
  the edges), one-colour runs add (c a >> 8) to T2[d] (MMX (d (256-a) >> 8) in 8-pixel groups
  from an 8-byte aligned pixel when 15 or more), alpha runs use T1[alpha].
- Status: clicking Start runs the opening: the first scene with Kirino, the message window,
  the name icon and the first line ("たまには、俺達もどっか出かけるか") are drawn; it waits for a click.
- Compositor mode 0x20000000 (alpha a, clamped to 256; a = 0 draws plainly): the colours of the
  sprite are mixed with a tint colour (entry +0x1C, blue / green / red) before drawing: c' =
  T2[c] + T1[t] with the tables of mode 0x40000000. BGR runs write c' (from 15 pixels on, groups
  of 8 pixels from a 4-byte aligned pixel, after one 4-pixel table block when not 8-byte aligned,
  take MMX: c + ((t - c) a >> 8) in 16-bit words); one-colour runs fill c'; alpha runs blend c'
  with the pixel's own alpha (d + ((c' - d) alpha >> 8)).

### More opcodes and compositor modes (ScnVm, 2026-10-07)

- The compositor's remaining modes (path 0x4409E0), checked byte for byte against the
  executable's own code on the x86 interpreter (random sprite lists of every run method, clip
  rectangles, every mode; scratchpad blendtest):
  - bit 28 set (0x10000000, 0x30000000, 0x50000000, 0x70000000; 0x442930) adds, saturated:
    BGR runs d + T1[c] (from 5 pixels on, 8-byte groups from an 8-byte aligned byte take MMX:
    (c a & 0xFFFF) >> 8), one-colour runs d + T1[c], alpha runs d + (T1[c] alpha >> 8).
    0x10000000 keeps alpha up to 0x1FF, the others stop at 0x100.
  - 0x60000000 (0x442070) paints the tint colour t (entry +0x1C) through the sprite's shape:
    alpha runs d + ((t - d) T1[alpha] >> 8); opaque runs (BGR, one colour) are only counted and
    painted later (0x4424C0: T2[d] + T1[t], dword blocks whose sums carry into the next byte, MMX
    groups d + ((t - d) a >> 8)) where the next run that is not of method 1-3 starts or at the
    end of the row. A method 1 run after them moves the painting on - at the end of a row past
    the row, even past the picture. Executable bug left out: a one-colour alpha run cut by the
    left edge indexes T1 with its whole dword; OpenShiina uses its alpha byte.
  - 0x08000000 (0x442E8B) copies the colours without alpha; one-colour alpha runs write blue,
    green, blue (sic). 0x04000000 (0x4431C0) draws the alpha as grey (colour runs white).
- `04D8 n, x, y, w, h, r, g, b` FillRect. `04FB` / `04FC dst, x, y, w, h, src, sx, sy, a` add /
  subtract src * a >> 8 a byte at a time, saturated (3 bytes a pixel, dst's pitch for both; the
  MMX subtraction's first (3 w mod 8) bytes of each row compute max(0, s a / 256 - d) instead).
  `0564 dst, x, y, w, h, src, sx, sy, size` mosaic (block colours sampled at size / 2 + k size
  counted from 0, not from the rectangle; size < 1 copies). `0566 dst, x, y, w, h, src, sx, sy,
  phase, step, height, flags` turns each row (bit 0: column) round by sin * height >> 16
  pixels with BitBlt (columns are w pixels high). `056C dst, src, phase, frequency, height`
  ripples (tables of distance and angle * 128 / pi per pixel of a quarter, made per size;
  sine tables * 256 of WinMain). `055F` copies a rectangle between pictures' frames (FUN_00410DC0:
  clipped to the destination frame's size only; 3 -> 4 bytes writes FF B G R for 3/4 of the
  row; rows forwards a dword at a time). `0574` rotates and zooms a 32-bit frame into another in
  32.32 fixed point (sin / cos * 2^32, divided by the zoom; outside the source rectangle the
  pixel keeps its colour with flags bit 0, else gets the colour argument). All checked against
  the x86 code where it runs without the FPU or GDI (04FB, 04FC, 055F); the rest read from it.
- `01A4 -, dll, function, arguments..., FF, v` calls a DLL function (FUN_0041DA80). The games
  call gdi32 CreateSolidBrush, CreatePen, SelectObject, Ellipse, DeleteObject (EFCLIB's circle
  wipe draws grey discs into a surface's device context, op_0529), user32 GetForegroundWindow,
  AdjustWindowRect and kernel32 GlobalMemoryStatusEx. Brushes and pens are kept by the VM;
  shapes go to IScnShapes: GDI itself on Windows (GdiShapes), elsewhere DibShapes. Worked out
  from Windows 10's pixels (`GdiEllipseCheck dump`): Ellipse(l, t, r, b) in GM_COMPATIBLE is the
  path StrokeAndFillPath would draw (the same pixels): four Béziers around the box (16l, 16t,
  16(r - 1), 16(b - 1)) in 28.4, from (right, middle) counterclockwise, control points
  ceil(k * w / 2) across and floor(k * h / 2) up and down from the middles (k = 4(√2 - 1)/3);
  flattened by GDI's hybrid forward differencing (the code is in WPF's bezier.cpp, with GDI's
  2/3-pixel error); filled at pixel centres (left and top edges in); and drawn over by the pen
  with GDI's cosmetic lines: a pixel is lit when the line meets its diamond |x| + |y| < 1/2, with
  the right and bottom corners, the pixel holding the line's end left out; a line at exactly 45
  degrees counts as moved a little (right and down for ++ and --, left and down for +- and -+).
  Checked: all 1401 circles to 2200 across but 2048, 25577 of 25600 ellipses to 160 x 160 (the
  rest a pixel off, at 45-degree ties), every flattened path (182), pen alone and two colours
  (182), moved and cut circles. Not worked out: with a null pen the brush fills
  CreateEllipticRgn(l, t, r, b) (symmetric, a pixel smaller), approximated; pens wider than a
  pixel, approximated. `dotnet run -c Release --project tools/GdiEllipseCheck` on Windows
  compares the two (gdi-ellipse-check.txt).
- `006F -, v` lists the fixed-pitch TrueType SHIFTJIS_CHARSET faces (no "@" faces) in 32-byte
  entries (IScnFonts.FixedPitchJapaneseFaces). `04E8 mode, a, b, c, v` picks the renderer (0
  GDI, 2 Direct3D, 3 OpenGL): only GDI is offered (v = 1; 0 for the others, the scripts then
  fall back). `04EE x, y, w, h` / `04F0 sx, sy` give where and how big the picture is shown
  (full screen scales it): (0, 0, width, height) and 1.0f, the host does the scaling.
- The x86 interpreter does cmc / stc / clc.

### Movies (ScnVm, 2026-10-06)

- Every movie of the 11 games is an MPEG-1 system stream (`mv\*.mpg` or `a\*.mpg` in the game
  folder, 800 x 600 or 1280 x 720 at 30 frames a second; only a few carry MPEG-1 Layer II sound:
  Oreimo's sepa.mpg, the ending movies ed.mpg of Maki Fes! and Re: Rem Plus). OpenShiina decodes
  them itself (Formats/MpegVideo.cs: system stream, MPEG-1 video with the tables of ISO/IEC
  11172-2, BT.601 colours); all 125 files decode without an error.
- START picks the player from RIO.INI MovieMode (b[9]; 0 and 1 give 1). With 1 the executable
  uses DirectShow multimedia streams (16 records of 0x158 bytes at 0x4B7BB0): `05B5 n, file,
  loop, surface` opens the file and plays it into a DirectDraw surface (made by `0546 n |
  0x80000000`; -1 the screen) at (0, 0) in the movie's size, `05C0 n` (IStreamSample::Update)
  draws the next frame (at the end: starts again and counts when looping, else stops), `05B6`
  stop (count -1), `05B7` pause, `05B8` play, `05B9 n, v` status (0x20D stopped, 0x211 paused,
  0x20E playing), `05BC` loop count, `05BF n, w, h` size, `05C3 n, v` volume 0-100 (the table of
  the music streams). The script copies the surface with `0514 dst, x, y, w, h, src, sx, sy, rop`
  (BitBlt; showing it when dst is the shown surface) or into a picture with `055E slot, frame, x,
  y, w, h, surface, sx, sy` (FUN_00410BF0: the source rectangle clipped to the surface, the
  destination moving with its left and top edge; 3-byte frames take the bytes, 4-byte frames
  get FF, B, G, R).
- Azu Plus's START function 207 (a movie on its own: TOPMENU's `mv\STCODE_T.MPG`, not in the
  game, and the `MOVIE` command, which its scenario never uses) draws in a window without the
  display surface: an 800 x 600 movie with `05C1 n` (05C0, then the movie's DirectDraw surface
  Blt onto the primary surface at the record's rectangle +0x20, which is 0, 0, w, h), any other
  size with `05C0` and `0516 dst, l, t, r, b, src, sl, st, sr, sb, flags, fx` (FUN_0041DED0:
  IDirectDrawSurface::Blt of rectangles, stretched; dst -1 the window's client area, not scaled;
  surfaces from `0546 n | 0xC0000000`). Full screen it uses 05C0 and 0514 as Oreimo does. In
  OpenShiina both draw onto the window's picture (ScnVm.Paint `BltToWindow`), where the next
  WM_PAINT covers them only in its invalid rectangles.
- MovieMode 2 (and ERO-ON always) uses a DirectShow filter graph with a sample grabber (16
  records: 0x1B0 bytes at 0x4B9130 in v2.47, 0x220 at 0x4F14F0 in v2.49; states 0 closed, 1
  stopped, 2 paused, 3 running): `05C9 n, file, loop` open and play (v2.49: file 0 plays the one
  there; flags bit 0 loop, bit 31 frames into the surface of bits 16-23), `05D1 n, file, loop`
  open only (v2.49 ignores loop), `05CA` close (count -1), `05CB` pause, `05CC` play, `05CD n, v`
  status as 05B9, `05CE n, v` position in ms (-1 when not running or paused), `05CF n, s` go to s
  seconds, `05D0 n, v` loop count, `05D2 n, ..., -1` play each open one, `05D3 n, w, h` size,
  `05D4 n, surface, x, y` the latest frame into the surface at (x, y) (SetDIBitsToDevice, after
  waiting until it is due). At the end (EC_COMPLETE) a looping movie starts again and counts,
  any other is closed. v2.47: `05D5 n, v` / `05D6 n, v` get / set the volume. v2.49: `05D5 n,
  surface, x, y` only sets where the grabber draws every new frame from then on, `05D6 n` waits
  for the frame, `05D7` / `05D8` get / set the volume. In OpenShiina 05D4 / 05D6 end the host
  frame when no new frame is due (the executable sleeps there), and v2.49's grabber draws once a
  host frame. Oreimo with MovieMode=2 (ScnBoot SCNBOOT_INI) also plays sepa.mpg, which the other
  player skips.
- In OpenShiina frame n is due n / 30 s after the start and `05C0` draws the latest frame due.
- Movie sound: the MPEG-1 Layer II stream is decoded with NLayer (Formats/MpegAudio; the same
  samples as Windows' own decoder to 16-bit rounding) into a WAVE that plays through the music
  streams (IScnMusic), started, paused, stopped, looped and moved with the picture; the volume
  0-100 goes through the music streams' table. Only Maki Fes! and Re: Rem Plus have sound in
  their movies (ed.mpg, 104 s and 155 s); Oreimo's and Azu's sepa.mpg carry 0.5 s of silence.

### Hot embedded x86 in C# (ScnVm, 2026-10-06)

- Routines that run often get a C# version, found by the signature of the whole routine
  (Scripting/X86Routine: SHA-1 of every instruction reachable from the entry through jumps,
  branches and calls, with its offset from the entry; until 2026-10-07 it was the SHA-1 of the
  first 64 bytes, which a routine starting the same way but differing further on would match)
  (ScnVm.NativeKernels.cs). Written by hand: Oreimo START 79366 (blend of two 32-bit layers) and
  796E5 (the alpha of rule fades: the mask's top byte m, or 255 - m, against imin / imax; between
  them ((m - imin') * (0x8080 / (imax - imin + 1)) in signed 16 bits) * alpha >> 15).
- tools/X86Gen translates a routine mechanically into Scripting/Generated/*.g.cs (one C# method,
  registers as locals, jumps as gotos, flags and MMX through Scripting/X86Ops with the
  interpreter's semantics). It served as the first step and the reference for the hot routines
  below, which are now written out.
- **X86Jit** (2026-10-07) makes the same translation at run time for any routine of any game:
  the first call of a routine without a C# version starts its translation in the background
  (an expression tree compiled to IL: registers and flags as locals, branches as gotos, X86Ops
  for the operations; movs / stos through helpers, a block copy when a forward rep movs does not
  overlap itself); the interpreter runs the routine until it is ready. Routines with code it does
  not take stay on the interpreter: calls (Oreimo's CPUID routine), cpuid, SSE (79A20, never
  run), parity conditions, idiv, mul / div of bytes and words, cmps, segment overrides. Of
  Oreimo's 19 routines 17 are translated. Checked against the interpreter on 1,122 runs of 11
  of them with random pictures and arguments (subpixel, scale, enlarge 77108, blend, rule alpha,
  copy, negative, sepia, strnicmp, EFCLIB's two): the same bytes, 10 to 60 times faster (sepia of
  a full screen: 750 ms -> 12 ms). Only differences: pushfd stores parity and adjust as 0, and
  registers start at 0. Off where .NET cannot generate code at run time (iOS) and with
  OPENSHIINA_X86JIT=0 (ScnBoot: SCNBOOT_JIT=0, or =sync to translate before the first call);
  perf.log names translated routines "jit <offset>", interpreted ones "x86 <offset>".
- START 76388 (copies a rectangle of a 32-bit picture with positions in 1/16 pixels, no
  scaling: smooth scrolling) is written out in ScnVm.Subpixel32.cs: weights D0 = (b - a) mod 16
  and D4 = (a - b) mod 16 across (a / b the distances of the destination / source position to
  the next whole pixel), D8 / DC down; rows of six kinds (top / whole / bottom, from one source
  row when the fractions down line up, else two), each with a left edge pixel, the whole pixels
  and a right edge pixel whose alpha lane is scaled by the covered fraction. The formulas are the
  MMX ones in 16-bit lanes; quirks kept: on rows from two source rows the edge pixels whose
  source starts inside a pixel are stored as two unpacked 16-bit lanes, and the left one adds one
  of its products unshifted. The whole pixels use Vector128 (two pixels at a time) and rows run
  on all cores. Checked on 700 random rectangles against the x86 code and in the game.
- START 78380 (the scaling case of the same copy, only scaling down; 56 million instructions a
  call in the zoomed and panned pictures) is written out in ScnVm.Scale32.cs: tables per
  destination row and column (flags 0x80 / 0x40 for partly covered first / last source pixels,
  the whole pixels between, their weights x * 0x5ADC (rows) or 0x5ADD (columns) / source size by
  a rounded reciprocal), then per destination pixel the sum of byte * (pmaddwd(wy, wx) >>> 21)
  in 16 bits, >> 8, and byte 3 = FF when every source pixel has FF. Quirk: past the first pixel
  of a row the general loop takes the shared first column of partly covered source rows from a
  register and leaves it out of the FF test. Since the sums wrap at 16 bits, the whole source
  rows are added first (Vector<ushort>) and multiplied once; columns use prefix sums; rows run
  on all cores. Checked against the x86 code on 500 random rectangles (scratchpad scaletest)
  and in the game.
- ScnBoot with SCNBOOT_VERIFY_NATIVE=1 runs the interpreter as well and compares the bytes written;
  SCNBOOT_PROFILE=1 also times each C# routine; SCNBOOT_PROFILE_FRAME=n gives the opcodes and
  routines of frame n alone.
- **The v2.49 builds of the hot routines** (2026-10-07): Homu, Yuru, Nyaru, Rikka, Sena and
  Kuroneko Plus carry the same four routines (Sena START 787CE scale32, 767D4 subpixel32, 797B4
  blend32, 79B33 rule alpha, at other offsets in the other games) with the same instructions
  as Oreimo's, but ending with `ret` instead of `ret 4` and with other padding between blocks, so
  their signatures differ and they ran translated: Sena's scale32 took ~110 ms a call (478 calls
  in its first 2,500 frames; 399 frames over 100 ms in 20,000). Their signatures are now
  registered for the same C# versions (checked with SCNBOOT_VERIFY_NATIVE=1 on 479 calls). Maki
  Fes! and Re: Rem Plus have the v2.49 blend32 and a third scale32 (Re: Rem START 87920, Maki
  86DB0): v2.47's blocks ending with `ret`. It ran translated, 200-260 ms a call, in their zoomed
  scenes (`$A_CHR` 40 / 41: a 1600 x 900 picture scaled to 1280 x 720 every frame); as C# about
  5 ms (checked on 218 calls), the frame about 15 ms.
- **Ero-On!'s own zoom routines** (2026-10-07): the first v2.49 build has other code for the
  zoom and pan of 0x14A3B (case 0 START 5DCCA, the same size; case 1 5DFA5 when both sides grow;
  case 2 5E2D6 otherwise). 5E2D6 scales down with weight tables counted in steps of 0x100 and
  16 arguments (four work buffers), written out as `scale16` (ScnVm.Scale16.cs: ~90 ms a call
  translated, 12 ms); 5DFA5 enlarges bilinearly with a table of (i * 257) / sW in l[12], written
  out as `enlarge16` (ScnVm.Enlarge16.cs: 2.5 ms for 800 x 600). Both compared byte for byte
  with the x86 code on the interpreter (scale16 about 900 random cases and 138 calls in the game;
  enlarge16 250 cases, its table included). Quirk kept by result: when a destination row of
  scale16 has only a partial last source row, the x86 code steps the source 2^32 times by the
  row stride (the same address again, seconds of work); scale16 does not step. 5E878 (blend) is
  other code giving the same bytes as Oreimo's 79366 (300 random cases, both on the interpreter)
  and runs as blend32; 5EB4A (a rectangle copy) stays translated.
- **Engine v2.50** (Re:Rem Plus, Maki Fes!; 2026-10-07). REMPLUS.EXE is not packed: it is read
  as it is (its interpreter FUN_004297D0, getVar 4164D0, setVar 415F80, the invalid opcode
  430382; `ScnTools opscan` gives tools/ScnTools/tables/ops_remplus.tsv = Data/ScnOps/ops_v250.tsv).
  - The opcode table: below 0C30 v2.49's numbers, but `0C30` takes 3 operands and `009C 00CD
    0113 02C2 02FD 02FE 0410 077A 0A05 0A06 0A07` are new; v2.49's block from 0C31 is numbered
    from 1069 on. The old survey read v2.50's scripts with Sena's table, so `077A` looked like
    data; with their own table they use four opcodes v2.49 lacks: `077A w, h` (a size kept,
    1280 x 720), `02FD` / `02FE` (zlib 1.2.7 blocks [total size, size, stream] for the saves),
    `009C index, text, w, h, pixels` (a character of the task's text record drawn from w x h
    bytes, 0-64, at (x, y) off the baseline; the text drawing looks this table up before the
    font, lowest index first; the layout keeps the font's widths).
  - v2.49 opcodes they use (ScnVm.Menus.cs, read from Sena's executable): `06CC` Direct3D 9
    there (1; without it they stop with "Direct3D Error"), `07E5` / `07E6` Background on / off,
    `0A00 w, h, flags` the screen's size (they ask for the one RIO.INI gives), `09DD`
    GetSystemMetrics, the window menu `0898 08AC 08C0 08E8 08F2 08FC` and `0014` (kept as
    handles nothing shows), `030C` a word, `0395` not, `0BCC` strchr, `012D` MoveFile of saves,
    `0294 base, count, width, slot` the engine's qsort (VC6's, the comparison a slot run to its
    end with l[0], l[1] the two elements: START sorts its 81,301-entry montage table with the
    16-byte compare START 8BBD0, written out as `compare16`; that slot's non-zero end is its
    answer, not the end of the game).
  - 1025 picture slots (0-0x400; 0-0x100 before).
  - The screen is shown with Direct3D: a copy into the display surface shows without
    InvalidateRect. Its movie loop copies frames there with `0514` / `051E` and nothing else, so
    those repaint their rectangle in v2.50 (the GDI window picture of ScnVm.Paint stays for the
    rest: showing the surface every frame showed the backlog half drawn).
  - Its backlog draws its lines again one `0083` after another every frame: `0083` starting
    text counts as text going on, so a frame does not end between the lines (it flickered).
  - Sounds in memory (`06B1`) are measured by the container, as the engine does: an Ogg stream
    ends at its last page (the size of the last file loaded is not theirs). A Windows Media
    movie (Maki Fes!'s `mv\ed.wmv`) plays the MPEG-1 of the same name beside it.
  - The Windows players open the window a game pixel to a screen pixel (1280 x 720 at any DPI
    scaling), smaller only where it does not fit.
- A routine with loops (a branch backwards) whose translation is not ready at its first call
  waits for it instead of running on the interpreter (Sena's 75FAF: 250 ms there).
- **Every embedded routine as C#** (2026-10-09, ScnVm.NativeRoutines.cs). The 0276 targets of
  the eleven games' SCN files (282 calls) are 230 routines, 74 builds (signatures) of about 35
  different ones; the last 25 that ran translated or interpreted have C# versions now, so no
  routine of the eleven games is translated in play. Offsets of Oreimo / Sena / Re: Rem Plus's START: the CPU check
  (75A84 / 75EE4 / 85384: 0x11 into l[0] and the dword before the routine, as the interpreter's
  CPU reports), the save thumbnail (75B51 / 75FAF: 8 x 8 averages of 800 x 600 BGR; Ero-On!
  5D7A0: 16 x 12 from 50 x 50), copies of rows and blocks (75E25, 75EC3, Kuroneko Plus 7637C,
  Ero-On! 5DB11, Re: Rem Plus 85674, the 32-bit rectangle 7963C / 79A8A / 88BDC), BGR to 32 bits
  and back (85774 / 857DC), sepia (76042 / 76490 / 85920: y = (151 b0 + 77 b1 + 29 b2) >> 8, then
  y * 145, 200, 240 >> 8), NOT of a block (75FEE), strnicmp (7633C), the first byte / word in a
  string (75FAB / 75F62, into f[2]), EFCLIB's XOR fill and inversion (1E9E / 54A0), Ero-On!'s
  same-size zoom (5DCCA: bilinear at 1/16 pixel, weights (16 - fx)(16 - fy) ... in 16 bits),
  and Re: Rem Plus's own: half- to full-width Shift-JIS by the table inside the routine (88FEC),
  a saturation change by the tables inside the routine (8911E), swap (8BB90), the montage table's
  names in capitals and its bisection (8BC00 / 8BC4C), add / subtract a constant (8BD18 /
  8BD38), a checksum (8BD58: dword added, rotated right by 3), a 28 x 28 mask (8BD9C) and the
  matching of 48-byte records (904F4). Where the x86 code's result depends on how it goes
  through memory (blocks that overlap other than in place) or its count would wrap round to 4
  billion, a C# version leaves the call to the x86 code (`RunX86`: translated when X86Jit takes
  the routine, else interpreted). Each build was compared with its x86 code on the interpreter on
  random cases, the whole test memory after each (scratchpad nativetest: 54 builds and offsets, 3
  to 300 cases each, overlapping blocks among them), and in ScnBoot with SCNBOOT_VERIFY_NATIVE
  (which now checks every place a routine writes). Left to the x86 code: the SSE2 build of the
  zoom (Oreimo 79A20, Sena 79E70, Re: Rem Plus 8BDE0), which START takes only when the CPU
  check gives SSE2 (b[77]); the interpreter's CPU has none, so it never runs.
- `0158` (a file's size; START asks it before every `00CE`) reads the size from the archive's
  index instead of decoding the file (checked: the index's unpacked size is the decoded length
  for every entry of the 11 games). `00CE` itself is the engine's background loader (START
  polls `00CF` every 5 ms and goes on drawing), done at once here.
- YH1 (Huffman) entries, some of the music, are decoded with a 12-bit look-up table instead of
  a bit at a time: Sena's BGM25 (4.3 MB) 80 ms -> about 15 ms; the same bytes as before for the
  50 YH1 entries of the 11 games.
- `0562` draws whole rows of 1684 x 1261 pictures in Sena (zoomed characters); its runs are
  read and written once a run and blended a pixel (dword) at a time: 40 ms -> 17 ms.
- The compositor (04C4) gathers the shown entries once and sorts them by (priority, index), and
  draws rows straight into script memory; it costs about 1-2 ns a pixel, under 1 ms a frame in
  the opening.
- **Flat memory** (2026-10-09, branch flat-memory; Scripting/ScnAddressSpace.cs). The scripts'
  memory was 64 KB pages made on first use, every read and write looking its page up and the C#
  routines copying row by row across pages. It is now the whole 32-bit address space as one block
  of virtual memory, a script address being base + address: `mmap` with `MAP_NORESERVE` (Linux,
  Android, macOS: every address there at once, physical memory only for the pages written); on
  Windows the 4 GB are reserved and each 1 MB part committed on its first use (commit counts
  against RAM + page file, so not all 4 GB at once; 16 MB parts committed about twice the RAM
  used): every access tests a table of 4096 flags, and a part is committed with the first page
  of the next, so an access of up to 8 bytes needs only its first byte's test. Memory freed reads as zero again: whole pages go back to the system
  (`mmap` over them / decommit and commit again), the rest is cleared. `ScnVm.Bytes` gives a block
  as a span to work on in place: the compositor draws its rows into it and reads the sprites'
  runs from it (a frame row drawn over itself is copied first), BlendRows (`04F6` / `0500`) reads
  and writes rows in place with `Vector<T>` for the MMX blocks (600 random cases against the
  paged code, sources on the destination and a few rows off among them), copies are one memmove.
  The translated x86 code goes through the same reads and writes (now a pointer and, on Windows,
  the flag test). No heap block crosses a 16 MB boundary (one over 16 MB starts on one): a
  32-bit process (32-bit Android, ScnBoot built for win-x86) has no 4 GB to spare, so there the
  address space is 256 parts of 16 MB (`ScnAddressSpace.Segmented`, chosen by the process; the
  64-bit code does not see the test), each mapped on its first use next to the part before when
  that place is free: a script address is part[address >> 24] + (address & 0xFFFFFF), a heap
  block is one piece of memory for the C# routines (the parts of a block over 16 MB are mapped
  together when it is made), accesses of 2 and 4 bytes across a boundary go a byte at a time,
  blocks a part at a time. The games touch a few dozen parts. Checked in ScnBoot: the pictures of
  Re: Rem Plus's zoomed prologue (105, every 25 frames to 2600) and Oreimo's park route (61,
  every 100 frames to 6000) are the same as with the paged memory, and SCNBOOT_VERIFY_NATIVE
  agrees; whole runs (best of 3, Windows, 6 cores): Re: Rem Plus 6.9 -> 6.6 s, Oreimo 7.8 -> 6.7
  s, the compositor about 13% less (Oreimo 04C4 1.79 -> 1.55 s), 04F6 0.54 -> 0.31 s. Memory,
  after the two fixes below (paged -> flat, ScnBoot, working set): Oreimo's park route to frame
  3500 peak 186 -> 175 MB, mean 155 -> 147 MB; Re: Rem Plus through its first route (風呂掃除,
  frames 7000-14000, then its route menu) peak 807 -> 422 MB, mean over the route 535 -> 359 MB.
  The scripts' memory in RAM is about the same (Re: Rem Plus 237-277 MB paged, 229-259 MB flat);
  the paged memory's pages were on the GC heap, which kept garbage beside them. Commit with 1 MB
  parts: Re: Rem Plus 514 MB at frame 12000 of that route (753 MB with 16 MB parts).
  Not yet run on Linux, macOS or Android (the `mmap` path), nor in a 32-bit process.
- **The hot C# routines on script memory in place** (2026-10-10): scale32 reads its source rows
  and writes its destination rows in place; subpixel32, blend32 and rule alpha too, a source row
  being copied first when it shares bytes with the row written (as the x86 code reads a row
  before writing it) or is not one piece of memory (`ScnVm.ReadView`); 0562 draws a run in place
  (dwords read and written unaligned, so ARM 32-bit is safe); 0568 works in place where its
  inputs are the output or apart from it (in parts of 64 KB on all cores, as before). enlarge32
  keeps copying its rows (it works on them as dword arrays; one memcpy each now) in buffers its
  threads keep. The page-touching loops before rows are shared out between threads are gone
  (nothing to make first). Fewer allocations a frame: scale32 keeps its tables and work arrays
  from call to call, the scopes of named variables (`03CF`) are used again. Re: Rem Plus to frame
  15000 of its first route: about 1.7 -> 1.26 GB allocated by the engine. ScnBoot's pictures
  (Re: Rem Plus's zoom and first route, Oreimo's park route: 183) are the same as before, and
  SCNBOOT_VERIFY_NATIVE agrees on Oreimo's route (rule alpha, blend32, subpixel32) and Re: Rem
  Plus's zoom (scale32).
- **Garbage that made the players big** (2026-10-09, found measuring the flat memory). START
  asks whether the game's archives exist (`010E`; `0158` the same way): for a file that is not
  in the archives `ScriptFileSize` read the loose file to know its length, so every start read
  Re: Rem Plus's `REMPLUS_G.WAR` (678 MB) and `_V.WAR` (111 MB) into arrays; `IScnHost.LooseFileSize`
  now takes the length from the folder (both players and ScnBoot). And scale32 made its band
  buffers (rows, 16-bit sums, prefix sums) anew at every call, about 2 MB a call in zoomed
  scenes, tens of GB over a route; each thread keeps them now while the sizes stay. Re: Rem Plus
  through its first route allocated 69 GB before, 6.9 GB after (most of the rest is ScnBoot's own
  input lists); its working set had reached 1.7 GB with the flat memory (the GC ran a full
  collection four times in 30,000 frames) and 0.7 GB with the paged one.
- ScnBoot stall detection: frames over 1 s are logged ([slow]); a frame over 5 s counts the
  instructions per script address and reports the top ones every SCNBOOT_STALL_REPORT seconds
  ([stall]); over SCNBOOT_STALL seconds (default 60) the run stops with a picture.
