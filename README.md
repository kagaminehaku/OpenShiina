<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/logo/wordmark-dark.svg">
    <img src="docs/logo/wordmark-light.svg" alt="OpenShiina" width="480">
  </picture>
</p>

# OpenShiina

An unofficial, open-source player for visual novels made with the ShiinaRio (椎名里緒) engine.
It reads the game's own files and runs the game's own scripts (the engine's SCN bytecode, with the
x86 code embedded in it), so the title screen, effects, text, menus, saves and settings are the
game's own.

<p align="center">
  <img src="docs/images/home.png" alt="OpenShiina's home screen: the games as tiles, the chosen one's card with Play" width="860">
</p>

## Why OpenShiina

The ShiinaRio engine runs only on 32-bit Windows, through old Windows APIs, and some of its games
ask for their disc. OpenShiina keeps these games playable:

- **On today's machines and on phones:** Windows, Linux and Android, from the same game files,
  with no Windows layer or emulator in between.
- **As the games were made:** it runs their own scripts rather than retelling the story, so the
  effects, menus and saves are the original ones.
- **In other languages:** translations sit beside the game and are read as it plays, without
  touching its files.
- **For keeping:** the engine's behaviour is written down in
  [docs/engine-notes.md](docs/engine-notes.md), so these games do not depend on one old executable.

**Status:** early. The interpreter runs ShiinaRio engine versions 2.45, 2.47, 2.49 and 2.50,
and 2.34 in part. OpenShiina has the keys of 136 ShiinaRio games; 18 were tried and 12 play
through (the eleven GRAND†CROSS "Plus" games it started with, and Aneiro), the others tried start
and play their stories. The Android player runs them on phones too (checked on a Galaxy S7). Every game,
with its engine version and VNDB page, and every platform's state is in
[docs/compatibility.md](docs/compatibility.md); what changed in each version is in
[CHANGELOG.md](CHANGELOG.md), and what is planned next in [docs/todo.md](docs/todo.md).

You need your own installed copy of the game. OpenShiina contains no game data.

## Screenshots

| | |
|---|---|
| ![Maki Fes!'s title screen, run by OpenShiina at 60 frames a second](docs/images/game-maki-fes.png) | ![The settings page, its Graphics section](docs/images/settings.png) |
| Maki Fes! (GRAND†CROSS, 2014) running its own scripts | Settings, page by page |
| ![The home screen on an Android phone held sideways](docs/images/android-home.png) | ![Maki Fes!'s title screen on a Galaxy S7, at 60 frames a second](docs/images/android-maki-fes.png) |
| On Android: a Galaxy S7, from 2016 | Maki Fes! on the same phone, 60 frames a second |

| | | |
|---|---|---|
| ![The home screen in the Shiina theme](docs/images/home.png) | ![The home screen in the Parchment theme](docs/images/theme-parchment.png) | ![The home screen in the Graphite theme](docs/images/theme-graphite.png) |
| Shiina, the default | Parchment | Graphite |

## Platforms

| Project | Platform |
|---|---|
| `src/OpenShiina.Core` | The engine, no user interface (`net10.0`): archives and decryption, image and sound decoders, the SCN interpreter and its x86 translator |
| `src/OpenShiina.App` | The player on [Avalonia](https://avaloniaui.net), shared by every platform: the game view, the interpreter on a thread of its own, sound through SDL3, text through SkiaSharp |
| `src/OpenShiina.Desktop` | The player for Windows, Linux and macOS (Avalonia) |
| `src/OpenShiina.Tools` | OpenShiina Tools for Windows, Linux and macOS (Avalonia): browse, preview, extract and repack a game's archives; translate a game |
| `src/OpenShiina.Android` | The player for Android phones and tablets (Avalonia), with touch controls; plays on a Galaxy S7 (Exynos 8890), heavy scenes are slow on phones that old |
| iOS | Planned, on `OpenShiina.App` |

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build -c Release
```

The player is written to `bin/Release/OpenShiina.Desktop/` (run `OpenShiina` or
`dotnet OpenShiina.dll`), OpenShiina Tools to `bin/Release/OpenShiina.Tools/` (`OpenShiinaTools`),
each with the scheme data (`Formats.Json`) next to it. The Windows player
on WPF (text drawn by Windows GDI) was retired after 0.2.0; its code is kept on the branch
`legacy-wpf`.

The Android player is not in the solution (it needs the .NET Android workload and the Android
SDK); build it on its own:

```
dotnet workload install android
dotnet build src/OpenShiina.Android -c Release
```

The APK is written to `bin/Release/OpenShiina.Android/`. It runs on CoreCLR (experimental in
.NET 10; arm64 and x64), which played Bitch Nee-chan at 40 frames a second on a phone where Mono
gave 27. `-p:OpenShiinaMono=true` builds it on Mono instead, for 32-bit Android too, compiled
ahead of time with LLVM, which takes several minutes (about 9 on a 6-core laptop); that APK goes
to `bin/Release/OpenShiina.Android.Mono/`. It reads the games from the device's
storage, so Android 11 and later ask for all files access the first time a game is added. On
the screen: a tap clicks, dragging holds the left button, two fingers moved up or down turn
the wheel (down opens the backlog); the bar on the right has Menu (a right click, as is Back),
Auto, Skip, Log and Exit.

Releases: pushing a tag `v*` (for instance `v0.2.0`) has GitHub Actions
(`.github/workflows/release.yml`) build the player for Windows and Linux and the APK, with the tag's version, and publish them as a release. The APK is
signed with the keystore in the repository's secrets (`ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`), or with a debug key when there is none.

## Usage

Run `OpenShiina`: its home screen has a rail on the left (the library, **Add a game…** and
the settings), your games as tiles, the one played last first, each with its `.exe`'s icon on that
icon's own colour, and the chosen game's card on the right: when it was played last, for how
long (counted while the game is in front), the size of its folder and saves, and **Play** in the
corner. A tile chooses a game (a double click or tap plays it); the card plays it wherever it is
clicked. **Add a game…** takes the game's folder (the one with its `.exe` and `.WAR` files). The
card's **…** opens the save folder (and, on a desktop, the game's folder) or removes the game:
from the list only (its files and saves stay, with Undo for a few seconds), or its folder deleted
too, and its saves if asked; the player deletes only a folder with the game's `.WAR` archives in
it, never a drive, a system folder, its own data or a folder holding another game of the list.
Passing a folder on the command line plays that game at once and adds it to the list. Quitting a
game brings the list back; closing the list ends the player. The game is recognised by its
`.exe`. The list is kept in `library.json` next to the saves. The player runs the
game's own SCN scripts, so the game looks and behaves as it does in its own engine.

Text: the games ask for MS Gothic. The player draws text with the system's fonts, with MS Gothic's measures: on
Windows MS Gothic itself, elsewhere a Japanese font that is installed (on Linux, for instance,
`fonts-noto-cjk` or `fonts-ipafont`), so text is close to the game's but not pixel for pixel.

A game whose .exe the player does not know (renamed, or a version it does not list) is found by
the keys that open its archives when it is added; when several games' keys fit, the player asks
which game it is.

Settings (the home screen's Settings button opens their page, saved in `settings.json` beside
`library.json`; each counts from the next game started), page by page:

- **Appearance**: the theme, shown at once (OpenShiina Tools too): Shiina, the logo's wine red and
  gold, by default; Parchment, light; Graphite, plain greys. And the language of the player's own
  screens: the device's by default, English or Vietnamese (the words are in
  `src/OpenShiina.Core/Localization`: `Strings.resx` in English, a `Strings.<language>.resx` for
  each translation, which shows in the list by itself).
- **Graphics**: on phones and tablets first two profiles, Battery saver (sharp scaling at the
  game's own pace) and Pretty (FSR 1 sharpened, a frame every refresh of the screen); then the graphics level (Potato: nearest pixels; Low: sharp; Medium: FSR 1; High: FSR 1
  sharpened; Max: Anime4K; Custom when the details match none of them), and its details: the
  scaling of the picture to the window (sharp: whole game pixels as far as they fit and the rest
  smooth, so text and lines stay even at any size; whole numbers only, with black around; smooth;
  nearest; or, on the GPU that draws the window, the upscalers FSR 1, with its sharpening from
  none to 10, and Anime4K, the heaviest), drawing on the GPU (the default) or on the CPU (the GPU mode runs
  heavy picture work as Vulkan compute shaders, `src/OpenShiina.Gpu`: the scaling, enlarging and
  rotating of zoomed scenes and subpixel moves, each on whichever of the CPU and the GPU was
  faster on its first large calls, with the same pictures; without a Vulkan driver the games run
  on the CPU), the frame rate (the game's own pace, up to 60 frames a second with the window
  drawing only new pictures, as the original keeps it; or every refresh of the screen, smoother
  on a fast screen but the scripts run as often: four times as much processor on a 240 Hz one),
  and showing the frame rate (in the title; on phones over the game).
- **Game**: the game controller, and pausing the game while another window is in front (on by
  default; off, the game does as its RIO.INI says: most go on, sound included).
- **Storage**: the file cache, and on Android the backup of the saves (below).
- **Debug**, for bug reports: writing `perf.log` and `draw-trace.log` (both into the game's save
  folder), and translating the games' x86 code (off: the interpreter only).

The environment variables of the settings still win over them, for tests: `OPENSHIINA_GPU=1` /
`=0`, `OPENSHIINA_JOYPAD=0` / `=ini` (below), `OPENSHIINA_PERF=0` (no frame rate) / `=log` (also
`perf.log`), `OPENSHIINA_X86JIT=0`, `OPENSHIINA_TRACE=draw`. Only as variables:
`OPENSHIINA_PAINT=surface` (shows the display surface every frame), `OPENSHIINA_THREADS=<n>` (the
most pieces a pixel routine is cut into; 1 runs them all on the interpreter's thread),
`OPENSHIINA_DATA=<folder>`
(the player's data instead of %AppData%\OpenShiina), `OPENSHIINA_WAYLAND=1` (Avalonia's own
Wayland backend on Linux), `OPENSHIINA_TOUCH=1` (the desktop player laid out as on a phone or
tablet).

Game controllers: the first joystick or gamepad works as the engine reads it: the stick (or
the first two axes) moves, button 1 decides, button 2 cancels. The games turn it off in their
RIO.INI (Joypad=0) but the players read it anyway; the setting "Game controller" (or
`OPENSHIINA_JOYPAD=0`) turns it off, `OPENSHIINA_JOYPAD=ini` follows RIO.INI.

Saves and settings are kept in `%AppData%\OpenShiina` (Linux: `~/.config/OpenShiina`; Android:
`Android/data/com.kagaminehaku.openshiina/files`); both players share them. On Android they go
into Android's backup (Google's cloud, a new phone) and come back when the app is installed
again; the game library and the logs do not. Android keeps 25 MB of an app (a GRAND†CROSS game's
saves are about 3.5 MB): the settings go first, then the save folders of the games played last,
as many as fit. Settings > Back up saves turns that off.

## OpenShiina Tools

`OpenShiinaTools [archive or game folder]` (it was [GrandCrossExtractor](https://github.com/kagaminehaku/GrandCrossExtractor),
now on Avalonia and the player's engine, so it opens every game the player knows):

- **Archives**: opens a game's `.WAR` archive (the game found by its `.exe` next to it, else by
  its keys, or chosen under Scheme) or a standalone `.S25` picture; lists, filters and searches
  its files; previews pictures (S25 frames, MI4, PNG, JPEG, BMP), plays sounds (OGV, OGG, PAD,
  WAV) and shows scripts as text, anything else as bytes.
- **Extract** a file, the selection or everything, as stored or converted (on by default):
  S25 / MI4 to PNG (an S25 a file per frame), OGV to OGG, PAD to WAV. **Compose layers** saves
  each layered S25 (event CGs and sprites: a base and eyes, mouth, effect layers) as the whole
  pictures the scenario scripts show, read from the archives next to it.
- **Repack** (WARC 1.7 archives): the files of a folder named as entries of the open archive
  (extracted without converting, then edited) are packed and encrypted into a new archive, the
  other entries copied as they are. Every packed entry is read back and compared with its file
  before the archive is saved; the open archive itself cannot be written over. Pictures cannot
  be made from PNG yet (no S25 encoder).
- **Translation**: translate a game in the tab itself, see below.

## Translations

To translate a game, open the **Translation** tab of OpenShiina Tools (or run
`OpenShiinaTools <game folder>`) and choose the game's folder:

1. The first time, the tools read the game's scenarios and make the tables of its translation in
   the game folder's `translation` folder: `text.tsv` (every message line: id `FILE.TXT:line`,
   speaker, original, translation), `names.tsv` (the speakers' names) and `strings.tsv` (texts
   the scripts write themselves, such as choices and menus, added by hand: the text exactly as the
   game shows it and its translation).
2. Translate in the tab: the scenarios a file at a time (or every line at once), with a search
   and "untranslated only"; the names; the strings. Every change is saved a moment later.
   **Check** says how far the translation is, how many of the 2,820 codes for letters it uses
   (beyond them a letter shows as `?`) and which lines do not fit the game's (another version).
   **▶ Play** opens the game in OpenShiina with the translation as it is.
3. That is all: the player reads the tables as they are. To share the translation, share the
   `translation` folder; the game's own files stay as they are.

The tables are UTF-8 with tabs between the fields, so they can also be filled in a spreadsheet
(Excel, LibreOffice, Google Sheets) or by a translation tool; more columns after the
translation (notes, who translated a line) are kept. **Update from the game** reads the game
again (after an update of it) and keeps the translations of the lines that did not change. The
scenarios keep the original speakers' names, which games find voices and name plates by; the
player draws the translated names. Keep the game's own symbols such as `①` (pictures the game
draws in their place, a heart in the Bitch games).

What the player reads, all in the game folder's `translation` folder: `text.tsv`, `names.tsv` and
`strings.tsv`; a scenario file of its own (`.TXT`, UTF-8 **with a byte order mark**, found by the
path the game reads it by, `translation\p\pro01_01.txt`, or by its name alone,
`translation\PRO01_01.TXT`), which comes before `text.tsv` for that scenario; and
`strings.txt` (a line each: the original, a tab, the translation; `#` comments). Translated
lines may use any letters, Vietnamese included, drawn half-width with their own widths and
wrapped at the window's edge by words. A text the scripts draw is replaced when the whole of it
is one of the strings' or names' originals.

## Documentation

- [`docs/compatibility.md`](docs/compatibility.md): which games play, and how far each was checked,
  on which platforms.
- [`docs/todo.md`](docs/todo.md): what is planned and not done yet.
- [`docs/engine-notes.md`](docs/engine-notes.md): what is known about the engine's archives,
  images, scenario commands, SCN bytecode and screens.
- `tools/ScnTools`: disassembler for SCN bytecode; `opcheck` lists what of a game's scripts the
  player does not run yet.
- `tools/LogoGen`: draws the logo (a locked book whose keyhole is a shii nut, 椎 as in Shiina;
  OpenShiina Tools' icon adds a quill; the wordmark's red S, i, n spell the sin in the name, in
  Cinzel, or Playfair Display with `--font playfair`) into `docs/logo`, the players' and the
  tools' icons and the Android icons: `dotnet run --project tools/LogoGen`.
- `tools/GdiEllipseCheck`: compares the players' own ellipses with GDI's (run it on Windows).
- `tools/SchemeImport`: brings the ShiinaRio schemes of GARbro's `Formats.dat` into
  `Formats.Json` (keeps ours, adds the others; see docs/engine-notes.md section 1).
- `tests/OpenShiina.ScnBoot`: runs a game's scripts without a screen, presses keys and clicks
  at given frames, saves screenshots, and reports what the engine ran.

## Credits

- Archive formats and encryption schemes are based on [GARbro](https://github.com/morkt/GARbro) by morkt;
  the schemes of the other ShiinaRio games come from GARbro-Mod's scheme database.
- Started from [GrandCrossExtractor](https://github.com/kagaminehaku/GrandCrossExtractor), which OpenShiina Tools carries on.
- The sound of movies is decoded with [NLayer](https://github.com/naudio/NLayer) (MIT).
- The player uses [Avalonia](https://avaloniaui.net) (MIT), [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT)
  and [SDL3](https://libsdl.org) through [SDL3-CS](https://github.com/ppy/SDL3-CS) (zlib, MIT).
- Ellipses without Windows flatten their curves with GDI's Bézier flattener as kept in
  [WPF](https://github.com/dotnet/wpf)'s bezier.cpp (MIT, .NET Foundation).
- The wordmark's letters are [Cinzel](https://github.com/NDISCOVER/Cinzel) (the logo's fonts,
  Cinzel and [Playfair Display](https://github.com/clauseggers/Playfair-Display), are in
  `tools/LogoGen/Fonts` under the SIL Open Font License 1.1).
- The FSR 1 upscaler (EASU and RCAS) is ported from AMD's
  [FidelityFX FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR) `ffx_fsr1.h` (MIT, Advanced Micro
  Devices), the Anime4K one from [Anime4K](https://github.com/bloc97/Anime4K)'s Upscale CNN x2 (S)
  (MIT, bloc97).

## License

MIT; see [LICENSE](LICENSE).

## Disclaimer

OpenShiina is not affiliated with, endorsed or sponsored by the developers of the ShiinaRio
engine or by any game publisher. All trademarks belong to their owners. Use it only with games
you own.
