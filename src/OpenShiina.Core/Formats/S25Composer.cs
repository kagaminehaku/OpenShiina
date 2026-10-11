// Composes layered ShiinaRio S25 images (event CGs, standing sprites) into complete pictures.
//
// The engine groups an S25's frames by slot number:
//   slots   0-99   base picture (alternative versions, all covering the same area)
//   slots 100-199  layer 1 (e.g. mouth), 200-299 layer 2 (e.g. eyes), ... up to 900-999
//   slots 1000+    not part of the picture (hit-area masks and the like)
// A picture is one base plus at most one frame from each layer, every frame at its own screen
// offset. Which combinations the game shows is written in the scenario scripts; see
// S25ScriptIndex.

namespace OpenShiina.Formats;

public sealed class S25Layout
{
    private const int SlotsPerLayer = 100;
    private const int FirstNonImageSlot = 1000;

    /// <summary>Slots of the base pictures (0-99).</summary>
    public IReadOnlyList<int> BaseSlots { get; }

    /// <summary>Slots of each layer, bottom to top. Every layer has at least one frame.</summary>
    public IReadOnlyList<IReadOnlyList<int>> Layers { get; }

    /// <summary>Every slot that belongs to the picture (base and layers).</summary>
    public IReadOnlySet<int> ImageSlots { get; }

    private S25Layout(List<int> baseSlots, List<IReadOnlyList<int>> layers)
    {
        BaseSlots = baseSlots;
        Layers = layers;
        ImageSlots = baseSlots.Concat(layers.SelectMany(l => l)).ToHashSet();
    }

    public static S25Layout? Analyze(byte[] data) => Analyze(S25Decoder.ReadFrameInfos(data));

    /// <summary>
    /// Returns the layer layout, or null when the file is not a layered picture: a single
    /// image, a set of independent images, or a UI sprite sheet.
    /// </summary>
    public static S25Layout? Analyze(IReadOnlyList<S25FrameInfo> frames)
    {
        var used = new HashSet<int>(frames.Select(f => f.Slot));
        var bases = frames.Where(f => f.Slot < SlotsPerLayer).ToList();
        if (bases.Count == 0)
            return null;

        var layers = frames
            .Where(f => f.Slot >= SlotsPerLayer && f.Slot < FirstNonImageSlot)
            .GroupBy(f => f.Slot / SlotsPerLayer)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<int>)g.Select(f => f.Slot).OrderBy(s => s).ToList())
            .ToList();
        if (layers.Count == 0)
            return null;

        // Alternative bases replace each other, so they all cover the same area. Sprite sheets
        // (system graphics, save thumbnails) place their pieces anywhere.
        var first = bases[0];
        if (bases.Any(b => b.Width != first.Width || b.Height != first.Height ||
                           b.OffsetX != first.OffsetX || b.OffsetY != first.OffsetY))
            return null;

        // A numbered list running straight across a hundred boundary (…, 99, 100, …) is a
        // sprite sheet, not base + layers.
        for (int boundary = SlotsPerLayer; boundary < FirstNonImageSlot; boundary += SlotsPerLayer)
            if (used.Contains(boundary - 1) && used.Contains(boundary))
                return null;

        return new S25Layout(bases.Select(b => b.Slot).OrderBy(s => s).ToList(), layers);
    }

    /// <summary>True if <paramref name="slots"/> is one base followed by frames of this file's layers.</summary>
    public bool IsValidCombination(IReadOnlyList<int> slots) =>
        slots.Count > 0 && slots[0] < SlotsPerLayer && slots.All(ImageSlots.Contains);
}

public static class S25Composer
{
    /// <summary>
    /// Yields one picture per combination (a list of slots, base first), named
    /// "{baseName}@{slot}+{slot}+…" with 3-digit slot numbers.
    /// Pictures are produced one at a time, so large sets do not have to fit in memory.
    /// </summary>
    public static IEnumerable<(string Name, PixelImage Image)> Compose(
        string baseName, IReadOnlyList<S25Frame> frames, S25Layout layout, IEnumerable<IReadOnlyList<int>> combinations)
    {
        var bySlot = frames.ToDictionary(f => f.Slot);

        // One canvas for the whole file, so every picture of a set has the same size
        var all = layout.ImageSlots.Select(s => bySlot[s]).ToList();
        int left = all.Min(f => f.OffsetX), top = all.Min(f => f.OffsetY);
        int width = all.Max(f => f.OffsetX + (int)f.Width) - left;
        int height = all.Max(f => f.OffsetY + (int)f.Height) - top;
        var canvas = new byte[width * height * 4];

        foreach (var combination in combinations)
        {
            Array.Clear(canvas);
            foreach (int slot in combination)
                Draw(canvas, width, left, top, bySlot[slot]);

            // The canvas is reused for the next picture: hand out a copy
            var image = new PixelImage(width, height, PixelLayout.Bgra32, (byte[])canvas.Clone());
            yield return ($"{baseName}@{string.Join("+", combination.Select(s => s.ToString("D3")))}.png", image);
        }
    }

    /// <summary>
    /// Draws <paramref name="parts"/> over each other (first at the bottom) on a canvas just
    /// large enough for them. Returns the picture and the screen position of its top-left corner.
    /// Safe to call from any thread.
    /// </summary>
    public static (PixelImage Image, int Left, int Top) ComposeFrames(IReadOnlyList<S25Frame> parts)
    {
        int left = parts.Min(f => f.OffsetX), top = parts.Min(f => f.OffsetY);
        int width = parts.Max(f => f.OffsetX + (int)f.Width) - left;
        int height = parts.Max(f => f.OffsetY + (int)f.Height) - top;
        var canvas = new byte[width * height * 4];
        foreach (var frame in parts)
            Draw(canvas, width, left, top, frame);
        return (new PixelImage(width, height, PixelLayout.Bgra32, canvas), left, top);
    }

    /// <summary>Alpha-blends a frame onto the canvas at its own offset (straight alpha, "over").</summary>
    private static void Draw(byte[] canvas, int canvasWidth, int left, int top, S25Frame frame)
    {
        int fw = (int)frame.Width, fh = (int)frame.Height;
        byte[] src = frame.Pixels;
        for (int y = 0; y < fh; y++)
        {
            int s = y * fw * 4;
            int d = ((frame.OffsetY - top + y) * canvasWidth + (frame.OffsetX - left)) * 4;
            for (int x = 0; x < fw; x++, s += 4, d += 4)
                PixelImage.BlendPixel(src, s, canvas, d);
        }
    }
}
