# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

- [ ] **Measure against the original exe in heavy scenes.** So far only a title screen was
  compared (Re:Rem Plus: about 3% of the machine for both). Take one heavy scene, for instance
  Re:Rem Plus's prologue zoom (the original runs on Windows 10 through Locale Emulator), and
  record the time a frame and the processor used on both, before and after the items below, to
  know how far from the original the player still is.
- [ ] **Flat memory: what is left** (the memory itself is done on the branch flat-memory,
  engine-notes.md section 10, "Flat memory").
  - Run it on Linux, macOS and Android (the `mmap` path has only been built, not run), then
    merge the branch.
  - The 32-bit way (16 MB parts, `ScnAddressSpace.Segmented`) is written but not built or run:
    ScnBoot as a 32-bit program (`dotnet publish tests/OpenShiina.ScnBoot -c Release -r win-x86
    --self-contained`) against the 64-bit one's pictures (Re: Rem Plus's zoom, Oreimo's park
    route), then an `android-arm` APK on a 32-bit phone or TV box.
  - The other hot C# routines on whole blocks (`ScnVm.Bytes` instead of rows copied in and out):
    scale32, subpixel32, enlarge32, blend32, rule alpha, 0562, 0568.
  - Commit: whole 16 MB parts are committed on Windows (about 700 MB in Re: Rem Plus against
    350 MB of RAM); smaller parts (1 MB, a table of 4096 flags) would commit less.
- [ ] **The translated x86 code itself.** Each x86 instruction becomes several .NET ones
  (flags, the page lookup, bounds): flags computed only where a later instruction reads them,
  and direct memory access once memory is flat, for the routines that keep running translated.
- [ ] **The SCN interpreter.** The engine's interpreter is compiled C++; ours dispatches each
  opcode in C# and reads its operands through the paged memory. Script logic is rarely where the
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

Both players scale the game's picture (800 x 600, 1280 x 720 for v2.50) to the window with
nearest-neighbour (ScnWindow.cs, GameView.cs). At a scale that is not a whole number (x2.4 on a
1440p screen, x3.6 full screen on 4K) some game pixels become 3 screen pixels and some 4, so
text and outlines come out uneven; bilinear alone blurs instead. A setting "Scaling" with:

- [ ] **Sharp** (the default): the picture multiplied by the largest whole number that fits
  (nearest-neighbour), the rest of the way bilinear. One pass on the GPU where the picture is
  shown: the texture coordinate snapped within each game pixel so that only the last screen
  pixel of its edge mixes with the next (the "sharp bilinear" shader). Avalonia: a Skia runtime
  shader (SkSL) drawing the image; WPF: a pixel shader effect (`ShaderEffect`, HLSL) on the
  image. Not in the Vulkan mode's compute path: that works on the picture in the scripts' memory
  and would read the large picture back. Without a GPU: the whole-number copy on the CPU and the
  image drawn with linear filtering.
- [ ] **Whole numbers only**: x2, x3... and black around, sharp and exact.
- [ ] **Smooth**: bicubic (Catmull-Rom) or Lanczos, for those who like CG soft.
- [ ] **Upscaler** on the GPU: FSR 1 (EASU + RCAS) or an anime-art upscaler (Anime4K, xBRZ);
  heavier, an option for desktop GPUs, maybe too slow for old phones.
- [ ] **Nearest**: as now.
- [ ] Check the WPF player's DPI awareness (per-monitor): a window on a second screen with
  another scale could be stretched once more by Windows.

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
