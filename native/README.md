# OpenShiina native

The player's core in C++, for the Nintendo Switch (homebrew, libnx) and other devices the .NET
player does not run on. The C# player in `src` stays the one for Windows, Linux, macOS and
Android; this one follows it file by file (each C++ file names the C# one it comes from), and every
part is checked against it on the same games before it counts as done.

## What is here so far

| Part | C# | State |
|---|---|---|
| Archives: WARC 1.0-1.7, every scheme and ExtraCrypt of Formats.Json | `Archives`, `IO` | Done on PC: the 20,878 files of 15 games (the 11 GRAND†CROSS games, Vanquish, Cleavage, Ao no Juuai, Shojo Mama) the same as the C# player's; not run on a Switch yet |

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

## Check against the C# player

```
native\tools\arccompare.ps1 -Work <temp folder> <game folder>...
```

decodes each game with ScnTools (`tools\ScnTools`, built) and with `arcdump`, and compares every
file. On the Switch: copy game folders to `sdmc:/OpenShiina/Games/<folder>`, run `arcdump.nro`
(hbmenu, or `nxlink -s arcdump.nro` from the PC), and compare the `sdmc:/OpenShiina/arcdump/<folder>.txt`
it writes with the lines `arcdump` writes on the PC for the same game (the same format): the C
libraries' `acos`, `log`, `exp`, `pow`, `sin` and `cos` may round their last bit differently there.
