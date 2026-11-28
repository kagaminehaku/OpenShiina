# The player

How to use OpenShiina, in full. The README has the short version.

## The home screen

The rail on the left has the library, **Add games…** and the settings. Your games are tiles, the
one played last first, each with its `.exe`'s icon on that icon's own colour. The chosen game's
card on the right shows when it was played last, for how long (counted while the game is in
front), the size of its folder and saves, and **Play** in the corner.

- A tile chooses a game; a double click or a tap plays it. The card plays it wherever it is
  clicked.
- **Add games…** takes a game's folder (the one with its `.exe` and `.WAR` files), or a folder
  with games in it: the games up to four levels under it (`Games\Maker\Game`) are added at once,
  and the player says which it could not recognise.
- The card's **…** opens the save folder (and, on a desktop, the game's folder) or removes the
  game: from the list only (its files and saves stay, with Undo for a few seconds), or with its
  folder deleted too, and its saves if asked. The player deletes only a folder with the game's
  `.WAR` archives in it, never a drive, a system folder, its own data or a folder holding another
  game of the list.
- Passing a folder on the command line plays that game at once and adds it to the list.
- Quitting a game brings the list back; closing the list ends the player.

A game is recognised by its `.exe`, or by its archives' names. A game whose `.exe` the player
does not know (renamed, or a version it does not list) is found by the keys that open its
archives when it is added; when several games' keys fit, the player asks which game it is. The
list is kept in `library.json` next to the saves.

## Translations and new versions

Translations shared by players are in the repository's [translations](../translations) folder.
The first time you play a game that has some (online), the player asks how to play it: the
original, the translation in the game's own folder (a `translation` folder next to its files, when
it has one), or one of the shared translations, each with its language, whether people or a
machine translated it, how much of the game it covers, who made it and its size; any of them can
be downloaded besides. The answer is remembered for the game. Offline, or when the game has no
shared translation, nothing is asked; it is asked the next time online, if there are some then.
The card's **…** has **Translation…**, which shows the same choice again (it applies from the next
time the game starts), with a dot when a new translation of the game was shared or a downloaded
one has an update waiting.

Downloaded translations are kept in the player's folder (`translations`), never in the game's
folder, and updated in the background as the setting says, never while a game waits for them: a
game plays the new one the next time it starts. Only the files that changed are downloaded, each
checked against GitHub's own hash of it.

When a newer release of OpenShiina is out, the player says so once a run, with what changed:
**Update** downloads it and, on Windows and Linux, puts the new program in place of this one and
starts it (when the player's folder cannot be written in, the release's page opens instead); on
Android the system's installer takes it (Android asks once to allow installing from OpenShiina).
**Later** asks again the next time, **Skip this version** does not ask about it again.

## Text

The games ask for MS Gothic. The player draws text with the system's fonts, with MS Gothic's
measures: on Windows MS Gothic itself, elsewhere a Japanese font that is installed (on Linux, for
instance, `fonts-noto-cjk` or `fonts-ipafont`), so text is close to the game's but not pixel for
pixel.

## Controls

- **Keyboard and mouse:** as in the game.
- **Touch (Android):** a tap clicks, dragging holds the left button, two fingers moved up or down
  turn the wheel (down opens the backlog). The bar on the right has Menu (a right click, as is
  Back), Auto, Skip, Log and Exit.
- **Game controllers:** the first joystick or gamepad works as the engine reads it: the stick (or
  the first two axes) moves, button 1 decides, button 2 cancels. The games turn it off in their
  RIO.INI (Joypad=0) but the players read it anyway; the setting "Game controller" (or
  `OPENSHIINA_JOYPAD=0`) turns it off, `OPENSHIINA_JOYPAD=ini` follows RIO.INI.

## Settings

The home screen's Settings button opens their page. They are saved in `settings.json` beside
`library.json`, and each counts from the next game started.

- **Appearance**: the theme, shown at once (OpenShiina Tools too): Shiina, the logo's wine red and
  gold, by default; Parchment, light; Graphite, plain greys; White, plain white and light greys;
  As the system, White or Graphite as the system's own colours are light or dark (it changes with
  them). And the language of the player's own
  screens: the device's by default, English or Vietnamese (the words are in
  `src/OpenShiina.Core/Localization`: `Strings.resx` in English, a `Strings.<language>.resx` for
  each translation, which shows in the list by itself).
- **Graphics**:
  - On phones and tablets, first two profiles: Battery saver (sharp scaling at the game's own
    pace) and Pretty (FSR 1 sharpened, a frame every refresh of the screen).
  - The graphics level: Potato (nearest pixels), Low (sharp), Medium (FSR 1), High (FSR 1
    sharpened), Max (Anime4K); Custom when the details match none of them.
  - Scaling: sharp (whole game pixels as far as they fit and the rest smooth, so text and lines
    stay even at any size), whole numbers only (black around), smooth, nearest; or, on the GPU
    that draws the window, the upscalers FSR 1 (its sharpening from none to 10) and Anime4K, the
    heaviest.
  - Drawing on the GPU (the default) or on the CPU. The GPU mode runs heavy picture work as
    Vulkan compute shaders (`src/OpenShiina.Gpu`): the scaling, enlarging and rotating of zoomed
    scenes and subpixel moves, each on whichever of the CPU and the GPU was faster on its first
    large calls, with the same pictures. Without a Vulkan driver the games run on the CPU.
  - The frame rate: the game's own pace (up to 60 frames a second, the window drawing only new
    pictures, as the original keeps it), or every refresh of the screen (smoother on a fast
    screen, but the scripts run as often: four times as much processor on a 240 Hz one).
  - Showing the frame rate (in the title; on phones over the game).
- **Game**: the game controller, and pausing the game while another window is in front (on by
  default; off, the game does as its RIO.INI says: most go on, sound included). When downloaded
  translations are updated (below): automatically (the default on a desktop), on Wi-Fi only (the
  default on phones), or when you choose. And whether the player looks on GitHub for new
  versions and translations at all (on by default; off, it never goes online).
- **Storage**: the file cache, and on Android the backup of the saves (below).
- **Debug**, for bug reports: writing `perf.log` and `draw-trace.log` (both into the game's save
  folder), and translating the games' x86 code (off: the interpreter only).

## Saves

Saves and settings are kept in `%AppData%\OpenShiina` (Linux: `~/.config/OpenShiina`; Android:
`OpenShiina` on the device's storage once the player may read the device's files, where any file
manager and a computer over USB see them, and which uninstalling the app leaves; before that
`Android/data/com.kagaminehaku.openshiina/files`, whose files are copied to `OpenShiina` once).
Each game's saves are in a folder of their own (the card's **…** opens it, in Files on Android);
`crash.log`, `stall.log` and `perf.log` are written there too.

On Android the saves go into Android's backup (Google's cloud, a new phone) and come back when the
app is installed again; the game library and the logs do not. Android keeps 25 MB of an app (a
GRAND†CROSS game's saves are about 3.5 MB): the settings go first, then the save folders of the
games played last, as many as fit. Settings > Back up saves turns that off.

Android 11 and later ask for all files access the first time a game is added: the player reads the
games from the device's storage.

## Environment variables

The settings' variables win over the settings, for tests: `OPENSHIINA_GPU=1` / `=0`,
`OPENSHIINA_JOYPAD=0` / `=ini`, `OPENSHIINA_PERF=0` (no frame rate) / `=log` (also `perf.log`),
`OPENSHIINA_X86JIT=0`, `OPENSHIINA_TRACE=draw`.

Only as variables:

| Variable | What it does |
|---|---|
| `OPENSHIINA_PAINT=surface` | shows the display surface every frame |
| `OPENSHIINA_THREADS=<n>` | the most pieces a pixel routine is cut into, and the most threads for a movie picture's slices; 1 runs them all on the interpreter's thread |
| `OPENSHIINA_DATA=<folder>` | the player's data instead of %AppData%\OpenShiina |
| `OPENSHIINA_WAYLAND=1` | Avalonia's own Wayland backend on Linux |
| `OPENSHIINA_TOUCH=1` | the desktop player laid out as on a phone or tablet |
