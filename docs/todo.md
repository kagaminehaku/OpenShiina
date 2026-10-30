# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

On a PC the heaviest scene measured takes 2-3% of the machine at 60 frames a second. What is left
is mostly for slow phones, and should follow what their perf.log shows.

- [ ] **Re:Rem Plus's held kiss zoom** is still two to three times the original (3.1-3.3% of the
  machine against 1.0%; engine-notes.md section 7): half the frame (2.4 ms), half showing it.
- [ ] **blur32 on arm64**: 87-93 ms a call on a phone against 5.5 on a PC, whatever the JIT does.
  Time its pieces there.
- [ ] **`00C9` on phones**: the first read of a large picture takes 7 ms a MB (2.4 on a PC);
  see whether decrypting or unpacking takes it.
- [ ] **The compositor: compose only what changed.** In a scene of text 04C4 redraws the whole
  list every frame where often only the message window changes. Tried (2026-10-17), right but no
  faster, so not kept: every write marked the 4 KB pages it wrote; for each surface 04C4 kept the
  rows before and after its last composition and the entries drawn, and drew again only the
  rows of entries that changed (their 32 bytes, or a write into their picture's heap block) and
  of the surface that changed (whole rows: a run cut at the sides can take another MMX path).
  Checked against whole compositions on all 28 releases. But the surfaces drawn into are
  written over before nearly every 04C4 (Bitch Nee-chan copies surface 3 into surface 0 with
  04E2, then composes over it): comparing their rows and copying the kept ones back cost as much
  as drawing them (04C4 in the first scenario swept: Re: Rem Plus 640-700 -> 840-870 ms, Sorcery
  Jokers 9.6-10.0 -> 14.1-14.8 s, Libra 4.2-4.3 -> 5.6-5.8 s; Oreimo Plus 180-226 -> 166-204);
  keeping rows only of surfaces nothing wrote since made it as fast as before, with 0-8% of the
  rows kept. What would pay: putting off a copy such as 04E2's for the rows the next composition
  draws over anyway, which needs to know when anything reads the surface. Bitch Nee-chan's
  prologue changes most of its rows anyway (a layer of its background is drawn every frame).
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
- [ ] **Edit boxes** (`0960`, Iyashinbo The Motion's save comments): the players show none yet,
  so the text cannot be typed; a box over the picture taking the keyboard (and on phones the
  on-screen keyboard), its text read back by `07A9`.
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

`Formats.Json` has the keys of 141 games; 43 releases play through (compatibility.md). For a new one:
`ScnTools opcheck`, then `tests/sweep.ps1`.

- [ ] **Keys for the 31 games without them** (compatibility.md, "No keys yet"), once their files
  are found. From Niflheim's WARC_unpack.c: the DecodeBin (0x2000 bytes) is read by the code
  near the constant 0x40000000; v2.50's Extra picture is unpacked at run time (follow
  `push 202` through GetProcAddress).
- [ ] Other v2.34 games: the 11 opcodes with other operands than v2.47's (0686-068B, 055A, 055B,
  04EC, 0B7D), the 9 new ones (007D-0080, 060E-0610, 0614, 077B) and 03BF. Ao no Juuai uses none.
- [ ] Opcode tables for 2.35-2.46 and 2.48 (opscan on the game's executable).
- [ ] The ExtraCrypts (PostAdler, PreAdler, Count, AltCount, Ushimitsu, Nukitashi2,
  Saimin) checked against their archives.
- [ ] **WMV9 P-picture loop filter**: Windows' decoder chooses some loop filter edges of WMV3
  P pictures otherwise than the standard (Sorcery Jokers' 2nd OP differs by 1-2 from frame 140;
  engine-notes.md, "Windows Media movies"). Find its rule.
- [ ] Interlaced VC-1 pictures, WMV3 range reduction / multiresolution / X8 intra pictures: no
  game movie has them yet (Vc1Video refuses such a stream).
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
