# OpenShiina native

The player's core in C++, for the Nintendo Switch (homebrew, libnx). The C# player in `src` stays
the one for Windows, Linux, macOS and Android; this one follows it file by file (each C++ file names
the C# one it comes from), and every part is checked against it on the same games before it counts
as done. It is built for the PC too, only for that comparison.

## What is here so far

| Part | C# | State |
|---|---|---|
| Archives: WARC 1.0-1.7, every scheme and ExtraCrypt of Formats.Json | `Archives`, `IO` | Done: on PC the 20,878 files of 15 games (the 11 GRAND†CROSS games, Vanquish, Cleavage, Ao no Juuai, Shojo Mama) the same as the C# player's; on a Switch Lite, Vanquish's the same as on PC (1.1 s) |
| The scripts' 4 GB of memory, given in 1 MB parts on first use | `Scripting/ScnAddressSpace.cs` | Done on PC (`tools/spacetest`: 100,000 random operations against a model, discards, threads); on the Switch the parts are heap memory mapped with `svcMapProcessCodeMemory`, which works there, the whole not run on one yet |

`tools/arcdump` decodes every file of a game's archives and prints the SHA-256 of each; on the
Switch it is `arcdump.nro`.

## Build

PC (Windows, Visual Studio 2026 with the C++ tools; CMake and Ninja come with it):

```
native\build-pc.cmd
```

Switch (devkitPro with `switch-dev`, run in its MSYS2 shell):

```
native/build-switch.sh
```

The build turns off fused multiply-adds (`-ffp-contract=off`): the archive keys are worked out
with doubles, and each operation must round as in the C# player.

## Run on the Switch from the PC

`devhost.nro` runs each build nxlink sends, without going back to hbmenu: it takes uploads as
hbmenu's netloader does, on the same port, and the programs it starts come back to it when they
exit (`tools/devhost/DevHostReturn.h`, called from `userAppInit`). Send it once with hbmenu's
netloader on (Y), which starts it:

```
nxlink -a <Switch IP> -p openshiina/devhost.nro native/build-switch/devhost.nro
```

then each build, while devhost waits (`-s` shows the program's output on the PC):

```
nxlink -s -a <Switch IP> -p openshiina/arcdump.nro native/build-switch/arcdump.nro
```

Later, devhost is opened from hbmenu (`switch/openshiina/devhost.nro`); + goes back to hbmenu. It
keeps the Switch from dimming and sleeping while it waits.

## Check against the C# player

```
native\tools\arccompare.ps1 -Work <temp folder> <game folder>...
```

decodes each game with ScnTools (`tools\ScnTools`, built) and with `arcdump`, and compares every
file. On the Switch: copy game folders to `sdmc:/OpenShiina/Games/<folder>`, run `arcdump.nro`
(hbmenu, or from the PC with nxlink as above), and
compare the SHA-256 it prints for each game's dump with that of the file `arcdump` writes on the PC
for the same game: the C libraries' `acos`, `log`, `exp`, `pow`, `sin` and `cos` may round their
last bit differently there. The dumps are in `sdmc:/OpenShiina/arcdump/<folder>.txt`, its output in
`log.txt` there.

Game folders on the SD card need Latin names: a folder with a Japanese name, written there from
Windows, is not seen by the Switch at all.
