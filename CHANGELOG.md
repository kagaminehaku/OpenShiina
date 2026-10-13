# Changelog

What changed in each release of OpenShiina, the newest first. The downloads are on the
[Releases](https://github.com/kagaminehaku/OpenShiina/releases) page; the state of every game is in
[docs/compatibility.md](docs/compatibility.md).

## 0.5.0 (2026-10-13)

The player in your language.

- **The player's screens in other languages:** Settings → Appearance → Language picks the
  device's language (the default), English or Vietnamese, and changes the screens at once, on
  the desktop and on Android. OpenShiina Tools stays in English for now.
- **For translators:** the words live in `src/OpenShiina.Core/Localization/Strings.resx`
  (English); a translation is a `Strings.<language>.resx` beside it and shows in the list by
  itself. Words not translated yet show in English.
- Titles in the wordmark's font (Cinzel) only when it has all their letters, so a Vietnamese
  title is not drawn in two fonts.
- The README has screenshots, why OpenShiina exists, and this changelog.

## 0.4.1 (2026-10-13)

- The home screen's card stands level with the first row of tiles.

## 0.4.0 (2026-10-13)

A new look: the logo's colours, a home screen built around the game you are playing, and a
settings page in place of the long settings list.

### Look and home screen

- **Three themes:** Shiina (the default: the logo's wine red and gold, dark), Parchment (light)
  and Graphite (plain greys). The theme changes at once; OpenShiina Tools and the Windows title
  bar follow it.
- **The wordmark** "OpenShiina" in Cinzel, as on the logo.
- **The home screen:** a rail on the left (library, Add a game, settings), your games as tiles
  (the one played last first, each icon on its own colour) and the chosen game's card on the
  right: when it was played last, for how long, the size of the game and its saves, and **Play**
  under the right thumb on a phone.
- **Play time** is counted from this version on, while the game is in front.
- **Removing a game:** from the list only (with Undo), or its folder deleted too, and its saves if
  asked. Only a folder with the game's `.WAR` archives in it is ever deleted.

### Settings

- **A page of their own**, in sections: Appearance, Graphics, Game, Storage, Debug.
- **Phones and tablets:** two profiles first, Battery saver and Pretty; rows big enough for a
  finger.
- **Drawing uses the GPU by default** on new installs; the processor takes everything where
  there is no Vulkan.

### Android

- **Saves and settings go into Android's backup** (25 MB: the settings, then the saves of the
  games played last). Settings → Storage → Back up saves turns it off.
- Back goes from the settings to the library.

### Games

- **Can Fes! (きゃん☆フェス！)** starts. Its disc check is answered as if the disc were there, so it
  runs from a copy of its files on any platform; every routine it carries in x86 code runs as C#.

## 0.3.1 (2026-10-12)

Mostly Android: the app opens in a fraction of a second instead of six, and plays faster.

### Android

- **Opens about 6 seconds faster** (6.5 → 0.3 s on a Red Magic 9 Pro+, 7.6 → 1.4 s on a Galaxy
  S7), working around a .NET 10 Android runtime bug (dotnet/android#12826).
- **Runs on CoreCLR** instead of Mono: Bitch Nee-chan skipping through its scenes, 27 → 40 frames
  a second.
- **Scene changes wait less:** the pictures and sounds a scene names are read ahead (Bitch
  Nee-chan's event pictures, 12.3 → 3.4 s of loading over the same skipping).
- The pixel work uses NEON vector instructions.
- Updates install over the old app, downloaded or built by hand.

### All platforms

- **Settings → File cache:** Auto (by the machine's memory), or Potato to Max.
- The library opens without waiting for the GPU or the games' icons.
- `perf.log` names the slow file reads; `startup.log` times the app's start.

### Games

- **Chuuchuu Nurse** (engine v2.45) reaches its story.
- **Bitch Shimai** no longer crashes at its blur; **Bitch Gakuen** no longer crashes looking into
  its patch archives.
- `ScnTools opcheck`: checks a game's scripts against the player before it is played.

### Changed

- The Android APK is 64-bit only (arm64, x64).

## 0.3.0 (2026-10-11)

Translations, OpenShiina Tools, a logo, and one player on every platform.

### Translations

- A `translation` folder in the game's folder holds tables of the scenario lines, the speakers'
  names and the game's own texts. The player reads them as it plays; the game's files stay
  untouched.
- Any letters, Vietnamese and English included, drawn with their own widths and wrapped by words.
- Translate in **OpenShiina Tools → Translation**: search, "untranslated only", autosave, Check and
  Play. The tables are plain UTF-8 text with tabs, so a spreadsheet can fill them too.

### OpenShiina Tools (new)

- GrandCrossExtractor carried on in OpenShiina, on Avalonia, for every game the player knows:
  browse a game's `.WAR` archives, preview pictures, sounds and scripts, extract (S25 / MI4 → PNG,
  layered CGs composed, OGV → OGG, PAD → WAV) and repack (WARC 1.7).

### Games

- **Bitch Shimai ga Seijun na Hazu ga Nai!!** reaches its story; every embedded routine of the
  three Bitch games, Aneiro and Ao no Juuai runs as C#, and their menus no longer drop to one frame
  a second.
- **A game whose .exe is unknown** (renamed, or a version not listed) is found by its keys when it
  is added.
- `docs/compatibility.md` lists all 136 games with their release dates, engine versions and VNDB
  pages.

### Players

- **The WPF player is retired**: the Avalonia player runs on every platform (the WPF code is kept
  on the `legacy-wpf` branch).
- **A logo:** a locked book whose keyhole is a shii nut (椎).
- Movies open about five times faster; large pictures load in the background; memory goes back
  after a game is closed.

## 0.2.0 (2026-10-11)

ShiinaRio games beyond the GRAND†CROSS titles, a Graphics section with GPU upscalers, and players
much lighter on the processor.

### Games

- **Aneiro** (v2.49) plays through.
- **Bitch Nee-chan** and **Bitch Gakuen** (v2.50) reach their stories; **Ao no Juuai** (v2.34)
  reaches its story in the test runner.
- **Archive schemes of 138 ShiinaRio games** and every WARC version (1.0 to 1.7).

### Graphics

- **Graphics levels:** Potato, Low (sharp), Medium (FSR 1), High (FSR 1 sharpened), Max
  (Anime4K).
- **Scaling modes:** Sharp (the default: whole game pixels, smooth only where they meet), whole
  numbers, smooth, nearest, and the GPU upscalers FSR 1 and Anime4K.

### Speed

- **Every embedded x86 routine of the eleven GRAND†CROSS games runs as C#**, on vector
  instructions and several cores: Re:Rem Plus's held zoom went from 12 % of the machine to 2–3 %
  (the original game: 1 %).
- The scripts' memory is one flat block, pictures are drawn in place; only the changed part of
  the picture goes to the window; movies decode faster.

### Players

- Touch: taps counted in the game's frames, a finger held still holds the button.
- **Pause behind other windows** (on by default), and the engine's own `Background` setting.
- Android: the volume buttons work while a game runs.

## 0.1.1 (2026-10-08)

- The same code as 0.1.0, with the Android APK signed with the release key, so later versions
  install over it.

## 0.1.0 (2026-10-08)

The first release: the engine's own scripts running on Windows, Linux and Android.

### The engine

- **The SCN interpreter (ScnVm):** runs the games' own SCN bytecode, with the x86 code embedded in
  it translated to .NET at run time (X86Jit), and the hot routines written out in C#.
- Layers, blends, rule fades, the compositor, movies (both movie players, with sound), music and
  sound effects, window messages as the engine's window procedure gives them, keys, mouse,
  joystick and wheel.
- GDI's shapes drawn as Windows draws them, for the platforms without Windows.
- **GPU mode:** the heaviest pixel work as Vulkan compute shaders, each routine on whichever of the
  CPU and the GPU was faster.
- Diagnostics: the frame rate in the title, `perf.log`, `draw-trace.log`, a stall watch and crash
  reports.

### Games

- **All eleven GRAND†CROSS games play through:** Ero-On!, Azu Plus, Oreimo Plus, Homu☆Plus, Yuru
  Plus, Sena Plus, Kuroneko Plus, Nyaru Plus, Rikka Plus, Maki Fes! and Re:Rem Plus (engine
  versions 2.47, 2.49 and 2.50).

### Players

- **The Avalonia player** for Windows, Linux and macOS, and a WPF player for Windows.
- **The Android player**, with touch controls.
- **A home screen** with the games added and their icons (`library.json`); quitting a game comes
  back to it.
- **Settings:** CPU or GPU, the game's frame pace or the screen's, the game controller, the frame
  rate, and switches for bug reports.
- A release workflow: a tag builds every player and publishes them.
