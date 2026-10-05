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
lines, 2,842 messages. Command use in Oreimo Plus, as the story player (`OpenShiina.Core/Story/`) reads them:

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

## 6. Play Story - status

Play Story plays Oreimo Plus from beginning to end (all 35 files, every choice), reading the
archives of the game folder. It is split in two projects:

- `OpenShiina.Core` (`net10.0`, no WPF or Windows API): archives, decoders, the SCN
  interpreter, the story engine and its formulas, the audio mixer. Pictures are `PixelImage`
  (plain BGRA / BGR pixels), sound goes out through `IAudioOutput`, and the screen and the
  window around it are the interfaces `IStage` and `IStoryView`, so every front end (Windows
  now; Android, iOS and macOS with .NET MAUI later) reuses all of it.
- `OpenShiina.Windows` (WPF): the player's screens on Windows.

The story runs from the game's own SRC_MAIN.SCN only. A hand-written Oreimo flow came before
it (`StoryFlow.cs`, kept in the GrandCrossExtractor repository).

| File | Role |
|---|---|
| Core `Story/StoryPlayer.cs` | the story engine: flow, scenario command interpreter, auto / skip / Ctrl, backlog, settings, saves and auto saves, save thumbnails, title sequence |
| Core `Scripting/ScnMachine.cs` | interpreter for SRC_MAIN.SCN (the story order of any game): variables, expressions, jumps, switch / case, local calls; gosub 240 and callmod 203 go to StoryPlayer |
| Core `Story/StoryOutline.cs` | the chapter list read from SRC_MAIN: files before the route menu = opening, the route menu's options and its switch targets = the routes (each ends at the goto back to the menu loop), the rest = ending |
| Core `Scripting/ScenarioScript.cs` | TXT parser |
| Core `Story/GameData.cs` | archives, lookup by stem, S25 cache, MONTBL / NWINTBL / SYSTEM.S25 |
| Core `Story/IStage.cs` | what the screen must do: planes, the action queue, DRAW / DRAW_EX, effects, scroll, movie |
| Core `Story/StageMath.cs` | the formulas of section 8: easings, A_CHR loops, rule fade / rule wipe levels, EFCLIB shake and zoom frames |
| Core `Story/MessageLayout.cs` | message text layout with the engine's font metrics and kinsoku |
| Core `Audio/AudioEngine.cs` | NAudio mixer (NAudio.Core): BGM, voice, SE channels, loops, fades |
| Core `Story/SaveData.cs`, `PlayerConfig.cs` | saves, settings and messages read, in `%AppData%\OpenShiina` (`OPENSHIINA_DATA` points them elsewhere, for tests) |
| Windows `Player/Stage.cs` | `IStage` with WPF: 800x600 planes, snapshot-based DRAW / DRAW_EX, plane animations, scroll, movie (WPF MediaElement plays the MPEG-1 files) |
| Windows `Player/MessageText.cs` | draws the message text and gaiji at `MessageLayout`'s positions |
| Windows `Player/WaveOutput.cs` | `IAudioOutput` on the Windows sound device (NAudio.WinMM) |
| Windows `Player/PlayerWindow.*` | `IStoryView`: title buttons, message window and its button bar, choices, chapter list, input (`.Save` save / load pages, `.Option` OPTION page and backlog page, `.Dialog` YES / NO dialog) |

Command behaviour and the screens follow section 8. Oreimo needs no ruby (its scripts have no
ruby text; START.SCN loads `d\ruby.s25`, 165 small kana, for games that do) and no automatic
lip sync / blinking (section 2). Other games: see section 9.

## 7. Tools

```
dotnet run --project tools/ScnTools -- opscan oreimoplus <OREIMOPLUS_dump_SCY.exe> ops.tsv
dotnet run --project tools/ScnTools -- dis tools/ScnTools/tables/ops_oreimoplus.tsv out.txt SRC_MAIN.SCN
```

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
| v2.50 | Maki Fes!, Re: Rem Plus | 53 commands: adds `WAITSE L_DELAY FADEVOICE L_SMOVIE L_ZBG L_ZBG2 LOOP DATE DELAYRUN DELAYRESET REGMSG L_PRIORITY` |

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
drawing. The player runs this subset with `ScnMachine`:

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

### Text, choices, L_MONT (used by the player)

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
