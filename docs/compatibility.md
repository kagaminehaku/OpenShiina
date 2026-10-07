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
| Ero-On! | えろおん！ | 2010-04-29 | [v11473](https://vndb.org/v11473) | v2.49 | 🔴 Stops | 2026-10-07 |
| Azu Plus | アズプラス | 2010-08-15 | [v7579](https://vndb.org/v7579) | v2.47 | 🟢 Plays | 2026-10-07 |
| Oreimo Plus | 俺妹プラス | 2010-12-31 | [v6035](https://vndb.org/v6035) | v2.47 | ✅ Plays through | 2026-10-07 |
| Homu☆Plus | ほむ☆プラス | 2011-08-14 | [v8019](https://vndb.org/v8019) | v2.49 | 🟡 Starts | 2026-10-07 |
| Yuru Plus | ゆるプラス | 2011-11-25 | [v10125](https://vndb.org/v10125) | v2.49 | 🟡 Starts | 2026-10-07 |
| Sena Plus | 星奈プラス | 2011-12-31 | [v10126](https://vndb.org/v10126) | v2.49 | ✅ Plays through | 2026-10-07 |
| Kuroneko Plus | 黒猫プラス | 2012-05-18 | [v10586](https://vndb.org/v10586) | v2.49 | ✅ Plays through | 2026-10-07 |
| Nyaru Plus | ニャルプラス | 2012-08-12 | [v10779](https://vndb.org/v10779) | v2.49 | 🟡 Starts | 2026-10-07 |
| Rikka Plus | 六花プラス | 2012-12-31 | [v11902](https://vndb.org/v11902) | v2.49 | 🟡 Starts | 2026-10-07 |
| Maki Fes! | マキフェス！ | 2014-12-30 | [v16484](https://vndb.org/v16484) | v2.50 | ⚪ Not tried | — |
| Re:Rem Plus | Re:レムプラス | 2018-03-31 | [v22991](https://vndb.org/v22991) | v2.50 | ⚪ Not tried | — |

ShiinaRio is the engine version START.SCN checks (`03C0`, as in RIO.INI's section name). Games
of one version share most of their START.SCN; see
[engine-notes.md, section 9](engine-notes.md#9-the-other-games-survey-of-all-11-2026-10-03).

## Notes

**Ero-On!** Stops at the first frame on opcode `079F`, which no other game uses. Its scenario
commands are a subset of the others' (34, PRELOAD numbered differently), and it always plays
movies with the filter-graph player (MovieMode 2).

**Azu Plus** Needed `03C2` (a checksum of the executable, checked at start), `05C1` and `0516`
(movies drawn straight onto the window). Checked in ScnBoot: the title, the opening, the route
menu and one route with its movie, back to the menu.

**Oreimo Plus** The first game. Every route skipped through three times; title, choices, saves,
OPTION page, backlog, movies (MovieMode 0 / 1 and 2).

**Homu☆Plus, Yuru Plus, Nyaru Plus** Use no opcode OpenShiina lacks and boot to the title in
ScnBoot. Their scenario command table is the same as Sena's and Kuroneko's.

**Sena Plus** Every route skipped through. It was slow (frames over 100 ms) until the v2.49
builds of the hot embedded routines got their C# versions (engine-notes.md, section 10).

**Kuroneko Plus** Every route skipped through; nothing was added for it.

**Rikka Plus** Boots to the title; still lacks `0548`, which START uses later (also used by Maki
Fes! and Re:Rem Plus).

**Maki Fes!, Re:Rem Plus** Engine v2.50 with 53 scenario commands; START uses opcodes OpenShiina
lacks (Maki Fes! 19, Re:Rem Plus 18 of them): `0014 00DC 012D 0294 030C 0395 0548 06CC 07E5 07E6
0898 08AC 08C0 08E8 08F2 08FC 09DD 0A00 0BCC` (Re:Rem Plus all but `00DC`).

## Updating

When a game's state changes:

1. Set its **Status** to the row of the legend that fits and **Checked** to the date (YYYY-MM-DD).
2. Under **Notes**, say what was checked and how (played, skipped with Ctrl, ScnBoot), what was
   added for it, and what is known not to work.
3. If it is the headline of a change, the commit message can name the game; README links here
   and does not list the games itself.

To see which opcodes a game's scripts use that OpenShiina does not run yet, boot it in ScnBoot
(`tests/OpenShiina.ScnBoot`): it stops at the first one and prints it.
