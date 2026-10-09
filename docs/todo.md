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
- [ ] **Flat memory for the scripts.** The VM's memory is in 64 KB pages, so every read and
  write looks its page up and the C# routines copy row by row across pages. Decided
  (2026-10-09): the whole 32-bit address space as one block of virtual memory, a script address
  being `base + address`, so no page lookup, null or bounds check anywhere (every 32-bit address
  is inside), the translated code reading and writing through the pointer, and pictures one
  contiguous block for the C# routines.
  - Reserve 4 GB of address space, physical memory only for the pages written: `mmap` with
    `MAP_NORESERVE` (Linux, Android, macOS); on Windows reserve, then commit each 16 MB part on
    first use (commit counts against RAM + page file). `Free` gives pages back and keeps them
    zero (`madvise(MADV_DONTNEED)`, `VirtualFree(MEM_DECOMMIT)`).
  - 64-bit only: Android drops `android-arm` (see the last item for bringing it back).
  - Rule from the start: no heap block crosses a 16 MB boundary (blocks over 16 MB start on
    one), so a table of 16 MB segments can be added later for 32-bit without touching the C#
    routines.
  - Steps: measure first (how much of a heavy scene's time is memory access); the backing
    swapped under the same API (Read32, Write32, CopyMemory, TryDirect...), ScnBoot pictures the
    same; then the translated code through the pointer (SCNBOOT_VERIFY_NATIVE); then the hot C#
    routines on whole blocks; measure again. On a branch of its own.
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
- [ ] **Android 32-bit (last).** Once memory is flat the player is 64-bit only. 32-bit Android is
  left on cheap Android Go phones, TV boxes and smart TVs (64-bit chips with a 32-bit system),
  mostly too slow for the games anyway. To bring `android-arm` back: a table of 256 segments of
  16 MB (`pointer = segment[address >> 24] + (address & 0xFFFFFF)`), each segment mapped on its
  first use; the translated code looks the segment up (and maps it when missing) instead of
  adding the base. The C# routines stay as they are (blocks never cross a segment). It can be
  tried on a PC with ScnBoot built for win-x86.
