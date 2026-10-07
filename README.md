# OpenShiina

An unofficial, open-source player for visual novels made with the ShiinaRio (椎名里緒) engine.
It reads the game's own files and runs the game's own scripts (the engine's SCN bytecode, with the
x86 code embedded in it), so the title screen, effects, text, menus, saves and settings are the
game's own.

**Status:** early. The first target is the eleven GRAND†CROSS "Plus" games; all nine on
engine v2.47 and v2.49 play through (Ero-On!, Azu Plus, Oreimo Plus, Homu☆Plus, Yuru Plus, Sena
Plus, Kuroneko Plus, Nyaru Plus, Rikka Plus); Maki Fes! and Re:Rem Plus (v2.50) not yet. Every
game's state is in [docs/compatibility.md](docs/compatibility.md).

You need your own installed copy of the game. OpenShiina contains no game data.

## Platforms

| Project | Platform |
|---|---|
| `src/OpenShiina.Core` | The engine, no user interface (`net10.0`): archives and decryption, image and sound decoders, the SCN interpreter and its x86 translator |
| `src/OpenShiina.App` | The player on [Avalonia](https://avaloniaui.net), shared by every platform: the game view, the interpreter on a thread of its own, sound through SDL3, text through SkiaSharp |
| `src/OpenShiina.Desktop` | The player for Windows, Linux and macOS (Avalonia) |
| `src/OpenShiina.Windows` | The Windows player on WPF, whose text is drawn by Windows GDI as the games draw it |
| Android, iOS | Planned, on `OpenShiina.App` |

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build -c Release
```

The players are written to `bin/Release/OpenShiina.Desktop/` (run `OpenShiina` or
`dotnet OpenShiina.dll`) and `bin/Release/OpenShiina.Windows/`, with the scheme data
(`Formats.Json` and `ShiinaImage/`) next to them. The WPF player builds on Windows only.

## Usage

Run `OpenShiina`: its home screen lists your games with the icons of their `.exe`. **Add a
game…** takes the game's folder (the one with its `.exe` and `.WAR` files); then a click plays it,
and its menu (right click) opens its save folder or takes it off the list. Passing a folder on the
command line plays that game at once and adds it to the list. The game is recognised by its
`.exe`. Both players share the list (`library.json` next to the saves). The player runs the
game's own SCN scripts, so the game looks and behaves as it does in its own engine.

Text: the games ask for MS Gothic. The WPF player draws it with Windows GDI, exactly as the game
does. The Avalonia player draws text with the system's fonts, with MS Gothic's measures: on
Windows MS Gothic itself, elsewhere a Japanese font that is installed (on Linux, for instance,
`fonts-noto-cjk` or `fonts-ipafont`), so text is close to the game's but not pixel for pixel.

Settings for testing, as environment variables: `OPENSHIINA_PERF=0` hides the frame rate in the
title (`=log` also writes `perf.log` to the save folder), `OPENSHIINA_X86JIT=0` runs embedded x86
code on the interpreter only, `OPENSHIINA_WAYLAND=1` uses Avalonia's own Wayland backend on Linux.

Game controllers: the first joystick or gamepad works as the engine reads it: the stick (or
the first two axes) moves, button 1 decides, button 2 cancels. The games turn it off in their
RIO.INI (Joypad=0) but the players read it anyway; `OPENSHIINA_JOYPAD=0` turns it off,
`OPENSHIINA_JOYPAD=ini` follows RIO.INI.

Saves and settings are kept in `%AppData%\OpenShiina` (Linux: `~/.config/OpenShiina`); both
players share them.

## Documentation

- [`docs/compatibility.md`](docs/compatibility.md): which games play, and how far each was checked.
- [`docs/engine-notes.md`](docs/engine-notes.md): what is known about the engine's archives,
  images, scenario commands, SCN bytecode and screens.
- `tools/ScnTools`: disassembler for SCN bytecode.
- `tools/GdiEllipseCheck`: compares the players' own ellipses with GDI's (run it on Windows).
- `tests/OpenShiina.ScnBoot`: runs a game's scripts without a screen, presses keys and clicks
  at given frames, saves screenshots, and reports what the engine ran.

## Credits

- Archive formats and encryption schemes are based on [GARbro](https://github.com/morkt/GARbro) by morkt.
- Started from [GrandCrossExtractor](https://github.com/kagaminehaku/GrandCrossExtractor).
- The sound of movies is decoded with [NLayer](https://github.com/naudio/NLayer) (MIT).
- The Avalonia player uses [Avalonia](https://avaloniaui.net) (MIT), [SkiaSharp](https://github.com/mono/SkiaSharp) (MIT)
  and [SDL3](https://libsdl.org) through [SDL3-CS](https://github.com/ppy/SDL3-CS) (zlib, MIT).
- Ellipses without Windows flatten their curves with GDI's Bézier flattener as kept in
  [WPF](https://github.com/dotnet/wpf)'s bezier.cpp (MIT, .NET Foundation).

## License

MIT; see [LICENSE](LICENSE).

## Disclaimer

OpenShiina is not affiliated with, endorsed or sponsored by the developers of the ShiinaRio
engine or by any game publisher. All trademarks belong to their owners. Use it only with games
you own.
