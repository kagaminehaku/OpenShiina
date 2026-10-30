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

**Status:** early. What changed in each version is in [CHANGELOG.md](CHANGELOG.md), and what is
planned next in [docs/todo.md](docs/todo.md).

You need your own installed copy of the game. OpenShiina contains no game data.

## Games

42 games (43 releases) play through on Windows, Linux and Android, among them the GRAND†CROSS
"Plus" games and Maki Fes!, the Bitch games, Kyuuketsuki no Libra, and the English releases of
Libra of the Vampire Princess, Sorcery Jokers and The Pillagers of Raillore. Engine versions 2.45
and 2.47 to 2.50 run, and 2.34 (two games tried). OpenShiina has the keys of 97 more ShiinaRio
games, not tried yet.

**Every game, with its engine version and how far it was checked: [docs/compatibility.md](docs/compatibility.md).**

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

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build -c Release
```

- The player goes to `bin/Release/OpenShiina.Desktop/` (`OpenShiina`), OpenShiina Tools to
  `bin/Release/OpenShiina.Tools/` (`OpenShiinaTools`).
- The Android player is built on its own (it needs the .NET Android workload):
  `dotnet workload install android`, then `dotnet build src/OpenShiina.Android -c Release`; the
  APK goes to `bin/Release/OpenShiina.Android/`.
- Pushing a tag `v*` has GitHub Actions build the player for Windows and Linux and the APK, and
  publish them as a release.

The projects: `src/OpenShiina.Core` (the engine: archives, decoders, the SCN interpreter and its
x86 translator), `src/OpenShiina.App` (the player on [Avalonia](https://avaloniaui.net), shared by
every platform), `src/OpenShiina.Desktop` (Windows, Linux, macOS), `src/OpenShiina.Android`,
`src/OpenShiina.Tools`. iOS is planned.

## Usage

1. Run `OpenShiina` and click **Add games…**: choose a game's folder (the one with its `.exe` and
   `.WAR` files), or a folder with several games in it.
2. Click a game's tile, then **Play**. The game runs its own scripts: its menus, saves and
   settings are the original ones.
3. Quit the game to come back to the list.

- **Controls:** keyboard, mouse and game controllers as in the game. On Android a tap clicks, two
  fingers moved up or down turn the wheel (down opens the backlog), and the bar on the right has
  Menu, Auto, Skip, Log and Exit.
- **Settings:** the theme and language, the graphics (sharp scaling, FSR 1 or Anime4K upscaling,
  the frame rate), the game controller, the file cache.
- **Saves** are kept in `%AppData%\OpenShiina` (Linux: `~/.config/OpenShiina`). On Android they
  are backed up with the phone.
- **Fonts:** on Linux, install a Japanese font (`fonts-noto-cjk` or `fonts-ipafont`).

Every setting, the controls, where the logs go and the environment variables for tests are in
[docs/player.md](docs/player.md).

## OpenShiina Tools

OpenShiina Tools opens a game's archives and translates games. **The full guide, with pictures,
is [docs/tools.md](docs/tools.md).** It was [GrandCrossExtractor](https://github.com/kagaminehaku/GrandCrossExtractor);
it now runs on Avalonia and the player's engine, so it opens every game the player knows.

<p align="center">
  <img src="docs/images/tools-archives.png" alt="OpenShiina Tools, Archives tab: Maki Fes!'s pictures, a character's sprite previewed" width="860">
</p>

- **Browse** a game's `.WAR` archives (or a single `.S25` picture): search the files, see the
  pictures frame by frame, play the sounds, read the scripts.
- **Extract** files as they are stored or converted: S25 / MI4 to PNG, OGV to OGG, PAD to WAV.
  **Compose layers** saves event pictures and sprites whole, as the story shows them.
- **Repack** edited files into a new archive (WARC 1.7). Each one is checked before it is saved.
- **Translate** a game in the Translation tab, below.

## Translations

To translate a game, open the **Translation** tab of OpenShiina Tools and choose the game's
folder:

1. The tools read the game's scenarios and make the translation's tables in a `translation`
   folder inside the game folder.
2. Translate line by line in the tab, then the speakers' names and the texts the scripts draw
   themselves (choices, menus). Every change is saved as you type.
3. **▶ Play** plays the game in a window of the tools, as OpenShiina does, with the translation
   as it is. While it plays, what you save reaches it from its next scenario, and a button goes
   to the line it shows. Right-click a line, **▶ Play from here** (experimental): the game opens straight at that line.

| | |
|---|---|
| ![The Translation tab: Maki Fes!'s first lines translated into English](docs/images/tools-translation.png) | ![Maki Fes! played from the tools with the translation: Maki's name and line in English](docs/images/tools-translation-play.png) |
| The Translation tab | The same lines in the game |

To share a translation, share the `translation` folder: it goes into the game's folder, and the
game's own files stay as they are. The tables are plain text (UTF-8, tabs between the fields), so
they can also be filled in a spreadsheet or a translation tool. Every step, and the files the
player reads, are in [docs/tools.md](docs/tools.md#translation-translate-a-game).

## Documentation

- [`docs/compatibility.md`](docs/compatibility.md): which games play, and how far each was checked,
  on which platforms.
- [`docs/player.md`](docs/player.md): the player in full: the home screen, controls, settings,
  saves, environment variables.
- [`docs/tools.md`](docs/tools.md): how to use OpenShiina Tools, with pictures: archives,
  extracting, repacking, translating a game.
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
- `tests/sweep.ps1 -Folder <game folder>`: runs every scenario of a game through ScnBoot and
  says which stopped or stood still, with the slowest frames
  ([docs/compatibility.md](docs/compatibility.md#sweeping)).

## Credits

- Archive formats and encryption schemes are based on [GARbro](https://github.com/morkt/GARbro) by morkt;
  the schemes of the other ShiinaRio games come from GARbro-Mod's scheme database.
- Started from [GrandCrossExtractor](https://github.com/kagaminehaku/GrandCrossExtractor), which OpenShiina Tools carries on.
- The sound of movies is decoded with [NLayer](https://github.com/naudio/NLayer) (MIT).
- Windows Media movies (ASF, WMV3 / VC-1 video, WMA and WMA Pro sound) are decoded by C# ports of
  [FFmpeg](https://ffmpeg.org)'s decoders (LGPL 2.1 or later).
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

Copyright (C) 2026 kagaminehaku

OpenShiina is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version. It is distributed in the hope that it will be
useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
FOR A PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the GNU General Public License.

Versions up to 0.6.0 were released under the MIT License, and stay under it. The code ported
from GARbro, WPF, FidelityFX FSR and Anime4K keeps its MIT notices, and that ported from FFmpeg
its LGPL one, in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

OpenShiina is not affiliated with, endorsed or sponsored by the developers of the ShiinaRio
engine or by any game publisher. All trademarks belong to their owners. Use it only with games
you own.
