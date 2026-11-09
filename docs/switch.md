# OpenShiina on the Nintendo Switch

OpenShiina for the Switch is homebrew: the same engine as the PC and Android players, written
again in C++ (the `native/` folder), drawing with deko3d. It comes two ways:
`OpenShiina-<version>-switch.nsp`, installed once as a title of its own on the HOME menu, and
`OpenShiina-<version>-switch.nro`, started from the Homebrew Menu. It is
new: Azu Plus and Bitch Gakuen ga Seijun na Hazu ga Nai!!? were played on a Switch Lite; the other
games run the same way on PC but were not tried on the console yet.

## What you need

- A Switch that runs homebrew: custom firmware (Atmosphère) with the Homebrew Menu (hbmenu) on its
  microSD card.
- Your own installed copy of the game, from a Windows PC. OpenShiina contains no game data.

## Installing the title (.nsp)

One install, and OpenShiina on the HOME menu opens the list of games at once, with all of the
console's memory. It needs signature patches (for example sys-patch) on the system it is installed
on, as every homebrew title does, and DBI to install it.

1. Copy `OpenShiina-<version>-switch.nsp` to the microSD card and install it with DBI ("Browse SD
   card", the file, Install). A newer version installs over it.
2. Copy the games to `OpenShiina/Games/` as in step 2 below.
3. Start OpenShiina from the HOME menu.

There − does nothing in the list of games (there is no Homebrew Menu to go back to); + quits.

## Installing the homebrew (.nro)

1. Copy `OpenShiina-<version>-switch.nro` to the microSD card as
   `switch/openshiina/OpenShiina.nro` (any folder under `switch/` works).
2. Copy each game's installed folder (the one with its `.exe` and its `.war` archives) to
   `OpenShiina/Games/` on the card, under a name in Latin letters, for example
   `OpenShiina/Games/BitchGakuen`. The card then looks like this:

   ```
   switch/openshiina/OpenShiina.nro
   OpenShiina/Games/BitchGakuen/...
   OpenShiina/Games/AzuPlus/...
   ```

   A file manager on the console (DBI's MTP, over USB) or a card reader both do.
3. Start hbmenu **from a game, not from the Album**: hold R while you start any game on the HOME
   menu, keep holding until hbmenu shows, then start OpenShiina. Started from the Album, homebrew
   gets only about 370 MB of memory and the games run out of it; started from a game it has all of
   the console's.

OpenShiina shows the games of `OpenShiina/Games` to pick from, each with its name and its icon
(games copied to the console's own storage, DBI's "Nand USER", in `OpenShiina/Games` there, are
listed too). A plays the chosen one, X opens the settings (how the picture is scaled: Sharp, Whole
numbers, Smooth, Nearest or FSR 1, and FSR's sharpness), Y looks at the game folders again, − goes
back to hbmenu, + quits.

## Playing

| Switch | The game sees |
|---|---|
| Touch | the mouse (a tap is a click) |
| Left stick | moves the pointer |
| A | Return |
| B | the right mouse button (the game's menu, back) |
| X | Escape |
| Y | Space |
| R / ZR (held) | Control: skip |
| L | Page Up (the text history) |
| D-pad | the arrow keys |
| + | the menu: back to the game, the scaling, leave the game |
| HOME | pauses the game |

The saves go to `OpenShiina/Saves/<game>` on the card, and what the player prints to
`OpenShiina/player.log` (send it with a bug report).
