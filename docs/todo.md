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
- [ ] **The games whose story is in SCN modules, not scenario TXT** (Cleavage, Reika, Puri-Puri,
  Death☆Meta, the Hana no Kioku releases, Nagagutsu wo Haita Deco and the others of
  compatibility.md's notes "SCN modules"): their lines are Shift-JIS strings in the modules' code
  (Reika's SCA01.SCN: `"「……くん」_r"`), which the player already replaces when a whole one is in
  `strings.tsv`; the Translation tab lists the lines of scenario TXT only. It should read each
  module's strings (as `ScnTools dis` finds them: the operands of the text calls, with their
  codes such as `_r`) into the tables, a module as a "scenario", and play a module from its
  first line as Play from here does a scenario (SCNBOOT_MODULE's swap). Texts built at run time
  (names put in) still cannot be matched whole.
- [ ] **Fonts of one's own** (asked by players):
  - a translation's own font: a `.ttf` / `.otf` in its `translation` folder draws its letters (the
    gaiji, SkiaFonts.Latin, now the first of Segoe UI, Arial, ... the system has; FreeTypeFonts on
    the Switch), so a translation looks the same on every platform;
  - the player's choice in Settings, for each game and a default: the face that stands in for
    ＭＳ ゴシック / ＭＳ 明朝 (a file chosen on a PC or a phone, `OpenShiina/Fonts` on the Switch's
    card). Measured as MS Gothic as the stand-ins are, so a fixed-pitch face suits the games'
    layouts and a wider one can overlap: say so where it is chosen.
- [ ] In OpenShiina Tools: list the texts the scripts write themselves (the Shift-JIS strings
  passed to 0083 / 0084 / 00A0); a whole game translated (are 2,820 letter codes enough?);
  Enter to the next line; machine translation of the empty lines (the user's own key).

## Other ShiinaRio games

`Formats.Json` has the keys of 164 games; 167 releases play through (compatibility.md). For a new one:
`ScnTools opcheck`, then `tests/sweep.ps1`.

- [ ] **Keys for the 2 games without them** (Delicious Way, Mahou no Shippo na Dai Ni wa;
  compatibility.md, "No keys yet"), once their files are found. From Niflheim's WARC_unpack.c:
  the DecodeBin (0x2000 bytes) is read by the code near the constant 0x40000000; v2.50's Extra
  picture is unpacked at run time (follow `push 202` through GetProcAddress).
- [ ] **Routines still without C#** (`ScnTools routines <games> --like <every game>`, 2026-11-23;
  the xor streams found by their bytes are not among them). The same shape as a C# version, its
  numbers to check with `routine --like` and `routinecheck`: thumbnails of Stellula Eques 2E006,
  Tsui☆Teru 2CB9A, Toriko no Chigiri 77D0F, Majikan 1 7A8BF and 2 7EB0F,
  Mekyou Choukyou 70C7F, Baby BE 1BF35 and ID 21674 (1 instruction from Kateinai Choukyou's);
  blocks of Stellula Eques 2E373, Tsui☆Teru 2CE93, You∽Gaku 73740, Draculius 2D4C3; rows of
  Toriko no Chigiri 77FA3 and Mekyou Choukyou 70F13 (Bloody Rondo's 244 x 183), Foster's HK456
  09DB8 (HK7, STAGE, Shikaeshi: rows600); the rule wipe of EDEN IV 053B8 (EDEN V, FINAL, SPN1,
  SPN3); copy32 of Niwaka Ane 5E03F; enlarge16 of Najimi no Oba-chan 9A243. Love Revo's scan did
  not end.
- [ ] **Known builds**: a game's build told by the SHA-256 (and size) of its .exe, for the
  scheme and keys (Re:Rem Plus's re-release has another DecodeBin date under the same .exe name),
  `03C2`'s value, and in the library whether that build was played through; and START.SCN's
  hash (or the archives' indexes) as the mark of the scripts a sweep ran (an update can leave the
  .exe as it is). An unknown hash only says so: the .exe name and SchemeDetector as now, never
  refused (patched, disc-free or translated .exes all differ).

## Platforms

- [ ] **Switch: the shared translations and new versions** (the desktop's and Android's are
  TranslationService and AppUpdater): HTTPS on the console (devkitPro's switch-curl with
  mbedtls), the tree of translations/ read and the files checked by their git SHA-1 as
  SharedTranslations does, kept in `sdmc:/OpenShiina/Translations`, the question before a game's
  first start on the console's own screens, and a new `OpenShiina.nro` downloaded in place of the
  one the Loader title starts.

- [ ] **Switch: devhost runs and times a routine by itself**: the player started from the
  OpenShiina Loader title (it runs sdmc:/switch/openshiina/OpenShiina.nro, so no NSP to install
  again) with arguments over nxlink: a bench mode that loads the files a routine needs from one
  game and times it, without anyone opening a game on the console.
- [ ] **Switch: `00C9`'s reads on the scripts' thread**: Re:Rem Plus's corridor loads its pictures
  at once (615 ms for 8 files; the slow log names each read over 50 ms as "read: <file>"). Read
  them ahead as `00CE` does.
- [ ] **Switch: a movie's start** (1.4 s for Sorcery Jokers' opening: the whole file read, then
  the ASF parsed): read its header only, the rest as it plays.
- [ ] **Switch: Sorcery Jokers' first scenes** (4-12 frames a second: effects of 1920 x 1080
  stacked); scale32's first column with its last pixel on the GPU (db3caab) still to be checked
  on the console with gpu-check. Measured 2026-11-23 (sjslow, frames 1500-2160, handheld clocks:
  CPU 1020, GPU 307, memory 1331 MHz): 54.7 ms a frame; scale32's SSE2 build 13.3 ms a call (its
  shader 10.2: the whole screen from about 2,500 x 1,400, 660 calls the same bytes as the CPU's),
  subpixel32 8.2 (shader 5.5), the CPU's cache flushed before a run 1.6-2.0, a run queued 0.26
  and woken 0.04; 04C4 13.8 ms a frame, 00D2 7.0, 04E2 + 04C5 7.2, the picture copied 1.7, the
  GPU's drawing 0.6. Tried on scale32sse_csh, both slower or no faster, not kept: the work
  group's source pixels read once into shared memory as floats (29-39 ms a call), the bytes made
  floats without I2F (11.3). What is slow in the shader is still to find (a bench of the routine
  alone, below, would make each try seconds instead of two minutes).

- [ ] iOS player on `OpenShiina.App`.
- [ ] The games on Linux and macOS (compatibility.md, Platforms), and the flat memory's `mmap`
  path there.
