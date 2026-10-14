# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

On a PC the heaviest scene measured takes 2-3% of the machine at 60 frames a second. What is left
is mostly for slow phones, and should follow what their perf.log shows.

- [ ] **Re:Rem Plus's held kiss zoom** is still two to three times the original (3.1-3.3% of the
  machine against 1.0%; engine-notes.md section 7): half the frame (2.4 ms), half showing it.
- [ ] **The Avalonia player's picture**: a GPU texture that takes only the changed part
  (GameSession.TakeFrame gives it) instead of the whole picture each frame.
- [ ] **blur32 on arm64**: 87-93 ms a call on a phone against 5.5 on a PC, whatever the JIT does.
  Time its pieces there.
- [ ] **`00C9` on phones**: the first read of a large picture takes 7 ms a MB (2.4 on a PC);
  see whether decrypting or unpacking takes it.
- [ ] **The compositor: compose only what changed.** In a scene of text 04C4 redraws the whole
  1280 x 720 list every frame where only the message window changes. Needs to know when a
  picture's pixels change.
- [ ] **Load in the background**: the system pictures and sounds START loads at the start (no
  scenario names them), and pictures' decoding, still on the interpreter's thread.
- [ ] **`00A1`**: every shown character is drawn again at each call.
- [ ] **The SCN interpreter**: profile the dispatch (SCNBOOT_PROFILE) on a busy scene.
- [ ] **The x86 translation**, only if a game runs a routine no signature matches: flags
  computed only where a later instruction reads them.
- [ ] **Older phones (the S7)**: Re:Rem Plus's zoom, a scene change and a movie with perf.log on;
  ParallelRows' piece size and MaxParts were measured on a PC only. FSR and Anime4K there too.

## Translations

- [ ] A translated scene on a phone (tried on Windows only).
- [ ] Name plates that are pictures found by name (Oreimo Plus's `NWINTBL.BIN`): the original
  names have to stay for the lookup, and the plates are pictures (below).
- [ ] Texts the scripts build from parts or draw another way (Oreimo Plus's menus):
  `strings.txt` replaces only a text that is a whole original.
- [ ] **Pictures with Japanese text** (menus, buttons, titles): an S25 encoder, compressed, and
  in the tools a frame replaced by a PNG / a picture's frames exported and imported back.
  ([Niflheim](https://github.com/Yggdrasill-Moe/Niflheim/tree/master/RioShiina)'s S25_pack
  writes the layout with every pixel uncompressed.)
- [ ] In OpenShiina Tools: list the texts the scripts write themselves (the Shift-JIS strings
  passed to 0083 / 0084 / 00A0); a whole game translated (are 2,820 letter codes enough?);
  Enter to the next line; machine translation of the empty lines (the user's own key).

## Other ShiinaRio games

`Formats.Json` has the keys of 138 games; 19 play through (compatibility.md). For a new one:
`ScnTools opcheck`, then `tests/sweep.ps1`.

- [ ] **Keys for the 35 games without them** (compatibility.md, "No keys yet"), once their files
  are found. From Niflheim's WARC_unpack.c: the DecodeBin (0x2000 bytes) is read by the code
  near the constant 0x40000000; v2.50's Extra picture is unpacked at run time (follow
  `push 202` through GetProcAddress).
- [ ] Other v2.34 games: the 11 opcodes with other operands than v2.47's (0686-068B, 055A, 055B,
  04EC, 0B7D), the 9 new ones (007D-0080, 060E-0610, 0614, 077B) and 03BF. Ao no Juuai uses none.
- [ ] Opcode tables for 2.35-2.46 and 2.48 (opscan on the game's executable).
- [ ] The ExtraCrypts (PostAdler, PreAdler, Binbo, Count, AltCount, Ushimitsu, Nukitashi2,
  Saimin) checked against their archives.
- [ ] **Known builds**: a game's build told by the SHA-256 (and size) of its .exe, for the
  scheme and keys (Re:Rem Plus's re-release has another DecodeBin date under the same .exe name),
  `03C2`'s value, and in the library whether that build was played through; and START.SCN's
  hash (or the archives' indexes) as the mark of the scripts a sweep ran (an update can leave the
  .exe as it is). An unknown hash only says so: the .exe name and SchemeDetector as now, never
  refused (patched, disc-free or translated .exes all differ).
- [ ] A real WARC 1.0-1.6 game (1.0 / 1.1 tried only on archives made for the test, 1.2-1.6
  only against GARbro's on random input).

## Platforms

- [ ] iOS player on `OpenShiina.App`.
- [ ] The games on Linux and macOS (compatibility.md, Platforms), and the flat memory's `mmap`
  path there.
