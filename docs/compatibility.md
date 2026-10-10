# Game compatibility

The state of every game OpenShiina aims at. Update a game's row when something changes (see
[Updating](#updating) at the end).

| Status | Meaning |
|---|---|
| ✅ Plays through | Every route was played or skipped to its end |
| 🟢 Plays | Starts and its routes play, but not all of them were checked |
| 🟡 Starts | Boots to the title screen; the story was not tried yet |
| 🔴 Stops | Stops before the title screen |
| ⚪ Not tried | Not run yet |

## GRAND†CROSS "Plus" games

| Game | Japanese title | Release | VNDB | ShiinaRio | Status | Checked |
|---|---|---|---|---|---|---|
| Ero-On! | えろおん！ | 2010-04-29 | [v11473](https://vndb.org/v11473) | v2.49 | ✅ Plays through | 2026-10-07 |
| Azu Plus | アズプラス | 2010-08-15 | [v7579](https://vndb.org/v7579) | v2.47 | ✅ Plays through | 2026-10-07 |
| Oreimo Plus | 俺妹プラス | 2010-12-31 | [v6035](https://vndb.org/v6035) | v2.47 | ✅ Plays through | 2026-10-07 |
| Homu☆Plus | ほむ☆プラス | 2011-08-14 | [v8019](https://vndb.org/v8019) | v2.49 | ✅ Plays through | 2026-10-07 |
| Yuru Plus | ゆるプラス | 2011-11-25 | [v10125](https://vndb.org/v10125) | v2.49 | ✅ Plays through | 2026-10-07 |
| Sena Plus | 星奈プラス | 2011-12-31 | [v10126](https://vndb.org/v10126) | v2.49 | ✅ Plays through | 2026-10-07 |
| Kuroneko Plus | 黒猫プラス | 2012-05-18 | [v10586](https://vndb.org/v10586) | v2.49 | ✅ Plays through | 2026-10-07 |
| Nyaru Plus | ニャルプラス | 2012-08-12 | [v10779](https://vndb.org/v10779) | v2.49 | ✅ Plays through | 2026-10-07 |
| Rikka Plus | 六花プラス | 2012-12-31 | [v11902](https://vndb.org/v11902) | v2.49 | ✅ Plays through | 2026-10-07 |
| Maki Fes! | マキフェス！ | 2014-12-30 | [v16484](https://vndb.org/v16484) | v2.50 | ✅ Plays through | 2026-10-07 |
| Re:Rem Plus | Re:レムプラス | 2018-03-31 | [v22991](https://vndb.org/v22991) | v2.50 | ✅ Plays through | 2026-10-07 |

ShiinaRio is the engine version START.SCN checks (`03C0`, as in RIO.INI's section name). Games
of one version share most of their START.SCN; see
[engine-notes.md, section 9](engine-notes.md#9-the-other-games-survey-of-all-11-2026-10-03).

## Other ShiinaRio games

`Formats.Json` has the keys of 127 more ShiinaRio games (GARbro-Mod's scheme database; list:
its `KnownSchemes`). None was run yet. Their archives open (every WARC version, 1.0 to 1.7;
1.0 and 1.1 need no keys), but the interpreter knows engine versions 2.47, 2.49 and 2.50 only
(engine-notes.md section 1, "The schemes" and "WARC versions"). Try one with ScnBoot first: it
says the first opcode it does not run.

## Platforms

| Platform | Player | Status | Checked |
|---|---|---|---|
| Windows 10 / 11 | WPF (`OpenShiina.Windows`) and Avalonia (`OpenShiina.Desktop`) | ✅ The games above | 2026-10-08 |
| Android | Avalonia (`OpenShiina.Android`), Release built with LLVM | 🟢 Plays | 2026-10-08 |
| Linux, macOS | Avalonia (`OpenShiina.Desktop`) | ⚪ Builds; no game run recorded here | |
| iOS | not written yet | ⚪ Planned | |

**Windows** The games were played and checked on Windows 10 (6 cores, RTX 2070, a 240 Hz
screen). At the game's own pace a title screen costs about as much processor as the original
exe (Re:Rem Plus's: about 3% of the machine for both). The GPU mode needs a Vulkan driver.

**Android** Checked on a Galaxy S7 (Exynos 8890, Mali-T880, Vulkan 1.0) with Maki Fes!: it
plays, 18.4 frames a second over a session from its perf.log (6.2 before the Release build used
LLVM and the changes of 2026-10-08). Zooms and transitions still drop frames on a phone that old.
Touch controls are described in the README.

## Notes

**Ero-On!** Every route skipped through. It needed `079F` (the window loses its maximise box)
and C# versions of its own zoom routines (START 5E2D6 scaling down, 5DFA5 enlarging: an earlier
build than the other games', engine-notes.md section 10). It is shorter than the others, its
scenario commands are a subset of theirs (34, PRELOAD numbered differently), and the game itself
has no saves: its title offers only start and quit.

**Azu Plus** Needed `03C2` (a checksum of the executable, checked at start), `05C1` and `0516`
(movies drawn straight onto the window). Every route played through.

**Oreimo Plus** The first game. Every route skipped through three times; title, choices, saves,
OPTION page, backlog, movies (MovieMode 0 / 1 and 2).

**Homu☆Plus** Every route skipped through; nothing was added for it.

**Yuru Plus** Every route skipped through; nothing was added for it.

**Sena Plus** Every route skipped through. It was slow (frames over 100 ms) until the v2.49
builds of the hot embedded routines got their C# versions (engine-notes.md, section 10).

**Kuroneko Plus** Every route skipped through; nothing was added for it.

**Nyaru Plus** Every route skipped through; nothing was added for it.

**Rikka Plus** Every route skipped through. It is the only v2.49 game using `0548` (a surface
of a given size), in START's function 207, which plays a movie on its own with MovieMode 0 / 1
and is reached from TOPMENU's `mv\STCODE_T.MPG` and the `MOVIE` command; the scenario never
uses `MOVIE`, so play never reaches it. `0548` was added afterwards (Maki Fes! and Re:Rem Plus
make their pages with it).

**Maki Fes!** Every route skipped through. Its ending movie is a Windows Media file
(`mv\ed.wmv`), played as the MPEG-1 `mv\ed.mpg` the game ships beside it.

**Re:Rem Plus** Every route skipped through. Its zoomed scenes (`$A_CHR` 40 / 41, a 1600 x 900
picture scaled to the screen every frame) took 200 ms a frame until its scaling routine ran as
C# (a third build of scale32; engine-notes.md, section 10).

**Both (engine v2.50)** 1280 x 720, 53 scenario commands, shown through Direct3D. v2.50 has an
opcode table of its own (read from REMPLUS.EXE: Data/ScnOps/ops_v250.tsv) and 1025 picture
slots; they needed the v2.49 opcodes their START uses (ScnVm.Menus.cs) and four of v2.50
(ScnVm.Engine250.cs: zlib-packed blocks for the saves, characters drawn from the scripts' own
pictures); see engine-notes.md, section 10. The window menu the game puts on its window (exit,
window size) is not shown: the players' own window does both.

## Updating

When a game's state changes:

1. Set its **Status** to the row of the legend that fits and **Checked** to the date (YYYY-MM-DD).
2. Under **Notes**, say what was checked and how (played, skipped with Ctrl, ScnBoot), what was
   added for it, and what is known not to work.
3. When a platform was tried (a phone, Linux, macOS), add or update its row under **Platforms**
   with the device and what ran.
4. If it is the headline of a change, the commit message can name the game; README links here
   and does not list the games itself.

To see which opcodes a game's scripts use that OpenShiina does not run yet, boot it in ScnBoot
(`tests/OpenShiina.ScnBoot`): it stops at the first one and prints it.
