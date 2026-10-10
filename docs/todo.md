# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

- [ ] **The pixel routines on real vector instructions.** Measured against the original
  (engine-notes.md section 7, "Against the original in a heavy scene"): in Re:Rem Plus's held
  kiss zoom the players use 12% of the machine, the original 1%. enlarge32 does the MMX code on
  64-bit integers (X86Ops), about six times the MMX; the same goes for scale32, subpixel32,
  blend32 and the others built on X86Ops. Vector128 (SSE2 / AdvSimd: 16-bit lanes, pmullw,
  packuswb) gives the same numbers with an instruction for each step, all four channels at once.
  Then fewer, larger bands for Parallel.For (or none below a few milliseconds of work): it adds
  about 5 ms of processor a frame. Measure again with zoom.ps1 / cpuframe.ps1 (scratch
  sessions/2026-10-10-measure); check every routine against its x86 code (nativetest).
- [ ] **Flat memory: what is left** (engine-notes.md section 10, "Flat memory").
  - Run it on Linux, macOS and Android (the `mmap` path has only been built, not run).
  - The 32-bit way (16 MB parts, `ScnAddressSpace.Segmented`) builds but has not run: ScnBoot
    as a 32-bit program (`dotnet publish tests/OpenShiina.ScnBoot -c Release -r win-x86
    --self-contained`) against the 64-bit one's pictures (Re: Rem Plus's zoom, Oreimo's park
    route), then an `android-arm` APK on a 32-bit phone or TV box.
- [ ] **The x86 translation, for games beyond the eleven.** No routine of the eleven games runs
  translated any more (each has a C# version); X86Jit and the interpreter stay for builds no
  signature matches. Only worth it if other ShiinaRio games come: flags computed only where a
  later instruction reads them (each x86 instruction becomes several .NET ones now).
- [ ] **The SCN interpreter.** The engine's interpreter is compiled C++; ours dispatches each
  opcode in C# and works its operands out (variables, named ones by name) every time it runs
  (the instructions themselves are decoded once). Script logic is rarely where the
  time goes, but profile the dispatch (SCNBOOT_PROFILE) on a busy scene and trim what shows up.
- [ ] **Load files in the background.** Pictures and sounds are read and decoded on the
  interpreter's thread, so a scene change waits for them. `ArcView` is not thread-safe: a
  loader thread needs its own view of the archives.
- [ ] **Copy only what changed to the window.** Each new frame copies the whole picture; most
  frames change only the text box or a sprite.
- [ ] **`00A1`** (the incremental redraw of the message text, a character at a time as it
  fades in): still on the list of slow spots; profile it and make it cheaper.
- [ ] **Older phones.** Zooms and transitions still drop frames on a Galaxy S7 (Vulkan 1.0);
  read its perf.log again once the items above are done.

## The picture on large screens

The setting "Scaling" (sharp, whole numbers, smooth, nearest) is done (engine-notes.md section 6,
"Scaling"). Left:

- [ ] **Upscaler** on the GPU: FSR 1 (EASU + RCAS) or an anime-art upscaler (Anime4K, xBRZ);
  heavier, an option for desktop GPUs, maybe too slow for old phones. Avalonia could run it as
  SkSL like the sharp shader; WPF has no shader path yet (its sharp scaling is a CPU copy).
- [ ] WPF's sharp scaling on the GPU (a `ShaderEffect`, or the picture drawn through Direct3D),
  as the Avalonia player does it: now the picture multiplied by a whole number on the CPU, which
  uploads a picture up to the window's size each frame.
- [ ] Check on an unlocked screen that the WPF player's picture fills the window after a resize
  (captured while the PC was locked, the bottom 52 pixels stayed black in the stretched modes).

Text cannot be drawn at the screen's resolution: the engine draws it into the game's surfaces,
which the scripts then copy and blend.

## Touch

- [ ] **Taps count in the game's frames, not milliseconds.** A tap is now the button down when
  the finger lifts and up 70 ms later; on a phone whose frames take 55-100 ms the scripts (which
  read the buttons once a frame) can miss it, and the first tap on a button only lights it.
  Instead: the pointer over the place for 2 frames before the button goes down, the button down
  for 2 frames (GameThread running window messages at a given frame).
- [ ] **Holding a finger still holds the left button** (after about 0.3 s, until it lifts);
  now only a drag holds it.

## Translations

The scenario files are Shift-JIS and the engine makes half-width characters full-width, so a
translated script can only use what Shift-JIS has: no Vietnamese letters, and English comes out
spaced as full-width.

- [ ] Read a scenario `.TXT` as UTF-8 when it starts with a byte order mark.
- [ ] Keep Latin letters half-width in such files, and draw them with a font that has them
  (Vietnamese included) and the game's measures.
- [ ] Speaker names: `【name】` is looked up in `NWINTBL.BIN` for the name plate; translated
  names need the table translated too, or a map from the new names to the old.
- [ ] Choices written in the SCN code (Oreimo Plus's menus pass them from the scripts): a way
  to replace those strings without moving the code.
- [ ] Pictures with Japanese text (menus, buttons, titles): GrandCrossExtractor can repack an
  archive's files but has no S25 encoder yet.

## Platforms

- [ ] iOS player on `OpenShiina.App`.
- [ ] Run and record the games on Linux and macOS (compatibility.md, Platforms).
