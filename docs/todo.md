# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

On a PC the heaviest scene measured (Re:Rem Plus's held zoom) takes 2-3% of the machine at 60
frames a second: more work there is not worth it for now. What is left is for slow phones, and
should follow what their perf.log shows.

- [ ] **Still two to three times the original** in Re:Rem Plus's held kiss zoom (engine-notes.md
  section 7, "Vectors, fewer pieces, the changed part": WPF 2.2-2.4% of the machine, Avalonia
  3.1-3.3%, the original 1.0%); about half is the frame (2.4 ms), half showing it. Measure
  with zoom.ps1 / cpuframe.ps1 (scratch sessions/2026-10-10-measure).
- [ ] **The Avalonia player's picture**: each new frame is copied whole into a new SKImage (the
  render thread draws the one it holds while the next is made). Keep two or three pixel buffers
  and wrap them (SKImage.FromPixels with a release), or keep a texture and update only the
  changed part (GameSession.TakeFrame gives it).
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
- [ ] **`00A1`** (the incremental redraw of the message text, a character at a time as it
  fades in): still on the list of slow spots; profile it and make it cheaper.
- [ ] **Older phones, first.** Zooms and transitions dropped frames on a Galaxy S7 (Vulkan 1.0)
  before the vector routines and ParallelRows (2026-10-10, not built for Android yet). Build the
  APK, play Re:Rem Plus's prologue zoom, a scene change and a movie with perf.log on, and work
  on what it shows (the same vector code runs on NEON there; ParallelRows' piece size and
  MaxParts were measured on a PC only).

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
