# OpenShiina

An unofficial, open-source player for visual novels made with the ShiinaRio (椎名里緒) engine.
It reads the game's own files and runs the game's own scripts (the engine's SCN bytecode, with the
x86 code embedded in it), so the title screen, effects, text, menus, saves and settings are the
game's own.

**Status:** early. The first target is the eleven GRAND†CROSS "Plus" games; all eleven play
through: the nine on engine v2.47 and v2.49 (Ero-On!, Azu Plus, Oreimo Plus, Homu☆Plus, Yuru Plus,
Sena Plus, Kuroneko Plus, Nyaru Plus, Rikka Plus) and the two on v2.50 (Maki Fes!, Re:Rem Plus).
The Android player runs them on phones too (checked on a Galaxy S7). Every game's and platform's
state is in [docs/compatibility.md](docs/compatibility.md); what is planned next is in
[docs/todo.md](docs/todo.md).

You need your own installed copy of the game. OpenShiina contains no game data.

## Platforms

| Project | Platform |
|---|---|
| `src/OpenShiina.Core` | The engine, no user interface (`net10.0`): archives and decryption, image and sound decoders, the SCN interpreter and its x86 translator |
| `src/OpenShiina.App` | The player on [Avalonia](https://avaloniaui.net), shared by every platform: the game view, the interpreter on a thread of its own, sound through SDL3, text through SkiaSharp |
| `src/OpenShiina.Desktop` | The player for Windows, Linux and macOS (Avalonia) |
| `src/OpenShiina.Android` | The player for Android phones and tablets (Avalonia), with touch controls; plays on a Galaxy S7 (Exynos 8890), heavy scenes are slow on phones that old |
| iOS | Planned, on `OpenShiina.App` |

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build -c Release
```

The player is written to `bin/Release/OpenShiina.Desktop/` (run `OpenShiina` or
`dotnet OpenShiina.dll`), with the scheme data (`Formats.Json`) next to it. The Windows player
on WPF (text drawn by Windows GDI) was retired after 0.2.0; its code is kept on the branch
`legacy-wpf`.

The Android player is not in the solution (it needs the .NET Android workload and the Android
SDK); build it on its own:

```
dotnet workload install android
dotnet build src/OpenShiina.Android -c Release
```

The APK is written to `bin/Release/OpenShiina.Android/`; a Release build compiles everything
ahead of time with LLVM, which takes several minutes (about 9 on a 6-core laptop). It reads the games from the device's
storage, so Android 11 and later ask for all files access the first time a game is added. On
the screen: a tap clicks, dragging holds the left button, two fingers moved up or down turn
the wheel (down opens the backlog); the bar on the right has Menu (a right click, as is Back),
Auto, Skip, Log and Exit.

Releases: pushing a tag `v*` (for instance `v0.2.0`) has GitHub Actions
(`.github/workflows/release.yml`) build the player for Windows and Linux and the APK, with the tag's version, and publish them as a release. The APK is
signed with the keystore in the repository's secrets (`ANDROID_KEYSTORE_BASE64`,
`ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`), or with a debug key when there is none.

## Usage

Run `OpenShiina`: its home screen lists your games with the icons of their `.exe`. **Add a
game…** takes the game's folder (the one with its `.exe` and `.WAR` files); then a click plays it,
and its menu (right click) opens its save folder or takes it off the list. Passing a folder on the
command line plays that game at once and adds it to the list. Quitting a game brings the list back;
closing the list ends the player. The game is recognised by its
`.exe`. The list is kept in `library.json` next to the saves. The player runs the
game's own SCN scripts, so the game looks and behaves as it does in its own engine.

Text: the games ask for MS Gothic. The player draws text with the system's fonts, with MS Gothic's measures: on
Windows MS Gothic itself, elsewhere a Japanese font that is installed (on Linux, for instance,
`fonts-noto-cjk` or `fonts-ipafont`), so text is close to the game's but not pixel for pixel.

A game whose .exe the player does not know (renamed, or a version it does not list) is found by
the keys that open its archives when it is added; when several games' keys fit, the player asks
which game it is.

Settings (the home screen's Settings button, saved in `settings.json` beside `library.json`;
each counts from the next game started): the graphics level (Potato: nearest pixels; Low: sharp;
Medium: FSR 1; High: FSR 1 sharpened; Max: Anime4K; Custom when the advanced settings match none
of them), and under Advanced graphics drawing on the CPU or on the GPU (the GPU mode runs
heavy picture work as Vulkan compute shaders, `src/OpenShiina.Gpu`: the scaling, enlarging and
rotating of zoomed scenes and subpixel moves, each on whichever of the CPU and the GPU was
faster on its first large calls, with the same pictures; without a Vulkan driver the games run on the CPU), the frame
rate (the game's own pace, up to 60 frames a second with the window drawing only new pictures,
as the original keeps it; or every refresh of the screen, smoother on a fast screen but the
scripts run as often: four times as much processor on a 240 Hz one), the scaling of the
picture to the window (sharp: whole game pixels as far as they fit and the rest smooth, so text
and lines stay even at any size; whole numbers only, with black around; smooth; nearest; or, on
the GPU that draws the window, the upscalers FSR 1, with its sharpening from none to 10, and
Anime4K, the heaviest), pausing the game while another window is in front (on by default; off, the game does as its
RIO.INI says: most go on, sound included), the game controller, showing the frame rate (in the title; on phones over the game), and for bug reports
writing `perf.log`, translating the games' x86 code (off: the interpreter only) and writing
`draw-trace.log` (both logs go to the game's save folder).

The environment variables of the settings still win over them, for tests: `OPENSHIINA_GPU=1` /
`=0`, `OPENSHIINA_JOYPAD=0` / `=ini` (below), `OPENSHIINA_PERF=0` (no frame rate) / `=log` (also
`perf.log`), `OPENSHIINA_X86JIT=0`, `OPENSHIINA_TRACE=draw`. Only as variables:
`OPENSHIINA_PAINT=surface` (shows the display surface every frame), `OPENSHIINA_THREADS=<n>` (the
most pieces a pixel routine is cut into; 1 runs them all on the interpreter's thread),
`OPENSHIINA_DATA=<folder>`
(the player's data instead of %AppData%\OpenShiina), `OPENSHIINA_WAYLAND=1` (Avalonia's own
Wayland backend on Linux).

Game controllers: the first joystick or gamepad works as the engine reads it: the stick (or
the first two axes) moves, button 1 decides, button 2 cancels. The games turn it off in their
RIO.INI (Joypad=0) but the players read it anyway; the setting "Game controller" (or
`OPENSHIINA_JOYPAD=0`) turns it off, `OPENSHIINA_JOYPAD=ini` follows RIO.INI.

Saves and settings are kept in `%AppData%\OpenShiina` (Linux: `~/.config/OpenShiina`); both
players share them.

## Translations

A scenario file (`.TXT`) saved as UTF-8 **with a byte order mark** is read as a translation: its
message lines may use any letters, Vietnamese included, drawn half-width with their own widths and
wrapped at the window's edge by words. Put translated files in a `translation` folder inside the
game's folder, at the path the game reads them by (for example `translation\p\pro01_01.txt`): they
are used in place of the archives' files of the same path, and the game's files stay as they are.
Keep the file's lines as they are: `;` comments and `$` commands; translate the other lines, one
message a line. A speaker's name in `【name】` may be translated where the game draws names as
text; a game whose name plates are pictures (Oreimo Plus) finds them by the original names.

Texts the scripts draw from their own code (choices, menus, names they keep) go in
`translation\strings.txt`, UTF-8, a line each: the original, a tab, the translation
(`土筆	Tsukushi`; lines starting with `#` are comments). A text drawn is replaced when the whole
of it is one of the originals.

## Documentation

- [`docs/compatibility.md`](docs/compatibility.md): which games play, and how far each was checked,
  on which platforms.
- [`docs/todo.md`](docs/todo.md): what is planned and not done yet.
- [`docs/engine-notes.md`](docs/engine-notes.md): what is known about the engine's archives,
  images, scenario commands, SCN bytecode and screens.
- `tools/ScnTools`: disassembler for SCN bytecode.
- `tools/GdiEllipseCheck`: compares the players' own ellipses with GDI's (run it on Windows).
- `tools/SchemeImport`: brings the ShiinaRio schemes of GARbro's `Formats.dat` into
  `Formats.Json` (keeps ours, adds the others; see docs/engine-notes.md section 1).
- `tests/OpenShiina.ScnBoot`: runs a game's scripts without a screen, presses keys and clicks
  at given frames, saves screenshots, and reports what the engine ran.

## Credits

- Archive formats and encryption schemes are based on [GARbro](https://github.com/morkt/GARbro) by morkt;
  the schemes of the other ShiinaRio games come from GARbro-Mod's scheme database.
- Started from [GrandCrossExtractor](https://github.com/kagaminehaku/GrandCrossExtractor).
- The sound of movies is decoded with [NLayer](https://github.com/naudio/NLayer) (MIT).
- The player uses [Avalonia](https://avaloniaui.net) (MIT), [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT)
  and [SDL3](https://libsdl.org) through [SDL3-CS](https://github.com/ppy/SDL3-CS) (zlib, MIT).
- Ellipses without Windows flatten their curves with GDI's Bézier flattener as kept in
  [WPF](https://github.com/dotnet/wpf)'s bezier.cpp (MIT, .NET Foundation).
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
