# To do

What is planned and not done yet, roughly in the order it would be worth doing. When an item is
done, take it off here and describe it where it belongs (engine-notes.md, compatibility.md, the
README).

## Speed

- [ ] **C# for the remaining embedded x86 routines.** About 25 distinct routines of the eleven
  games still run translated (X86Jit) or interpreted. Each gets a C# version found by its
  signature, as the zoom, compositor and blend routines have (ScnVm.NativeKernels.cs), and is
  checked against the x86 code on random cases (SCNBOOT_VERIFY_NATIVE). X86Jit and the
  interpreter stay as the fallback for any build no signature matches.
- [ ] **Flat memory for the scripts.** The VM's memory is in 64 KB pages, so every read and
  write looks its page up and the C# routines copy row by row across pages. One flat block
  (or one block per surface) would make the C# routines and the translated code faster; the
  largest win left on phones.
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
