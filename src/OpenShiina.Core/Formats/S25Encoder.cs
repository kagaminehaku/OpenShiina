// ShiinaRio S25 encoder: the reverse of S25Decoder, laid out as the games' own files are.
//
//   "S25\0", the number of slots, a file offset for each slot (0: empty)
//   a frame: width, height, x, y, flags (0x80000000: "incremental"), the file offset of each row
//   a row: its length (the bytes after this field) and then runs, each a 16-bit header at an even
//   offset (method << 13 | skip << 11 | count; count 0: a 32-bit count follows) and its data
//   `skip` bytes after the header:
//     0  `count` pixels left as they are (transparent)
//     2  `count` pixels B, G, R              3  one B, G, R for `count` pixels
//     4  `count` pixels A, B, G, R           5  one A, B, G, R for `count` pixels
// The data of methods 3, 4 and 5 starts at a multiple of 4 (as in the games' files). In an
// incremental frame the bytes of a run of method 2 or 4 after its first pixel are each the
// difference from the byte one pixel before: the engine adds them up in place when it loads the
// picture (FUN_00403430), once for each time a row is named. A row the same as another is
// written once and named again, as the games' files do; one with such runs, named n times by
// incremental frames, is written with the differences taken n times, so that it is right after
// the n sums (the games' packer did so too: their files decode only so).
//
// Every pixel the decoder gives comes back: all four bytes 0 is a transparent run, alpha 255
// opaque (methods 2 / 3), any other (alpha 0 with a colour too) methods 4 / 5; a run of one
// colour from 12 pixels, as in the games' files.

using System.IO;

namespace OpenShiina.Formats;

/// <summary>A frame to write: its slot, place and BGRA pixels (straight alpha).</summary>
public sealed record S25EncoderFrame(int Slot, int OffsetX, int OffsetY, PixelImage Image, bool Incremental = true);

public static class S25Encoder
{
    private const int MaxRun = 0x7FF;

    /// <summary>
    /// The fewest pixels of one colour written as a run of one colour (methods 3 / 5): the
    /// games' files have none shorter (the rest stay among the pixels of methods 2 / 4, whose
    /// differences pack better).
    /// </summary>
    private const int OneColour = 12;

    /// <summary>
    /// An S25 file of <paramref name="slots"/> slots with these frames (each in its own slot,
    /// the others empty).
    /// </summary>
    public static byte[] Encode(int slots, IReadOnlyList<S25EncoderFrame> frames)
    {
        if (slots <= 0 || frames.Any(f => f.Slot < 0 || f.Slot >= slots))
            throw new ArgumentException("A frame's slot is outside the slots.");
        if (frames.Select(f => f.Slot).Distinct().Count() != frames.Count)
            throw new ArgumentException("Two frames are in the same slot.");

        // The rows of every frame first: how many times each is named decides how it is written
        var ordered = frames.OrderBy(f => f.Slot).ToList();
        var rows = new EncodedRow[ordered.Count][];
        // Rows with runs of method 2 / 4 in incremental frames (summed once for each time they
        // are named), and the others (never changed): kept apart, as the engine sums only the former
        var summed = new Dictionary<RowKey, int>();
        for (int f = 0; f < ordered.Count; f++)
        {
            var frame = ordered[f];
            var image = frame.Image.Layout == PixelLayout.Bgra32 ? frame.Image : ToBgra(frame.Image);
            int width = image.Width, height = image.Height;
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                throw new ArgumentException($"Slot {frame.Slot}: a frame of {width} x {height} pixels.");
            rows[f] = new EncodedRow[height];
            for (int y = 0; y < height; y++)
            {
                var row = EncodeRow(image.Pixels.AsSpan(y * width * 4, width * 4), frame.Incremental);
                rows[f][y] = row;
                if (frame.Incremental && row.Literal.Count > 0)
                    summed[row.Key] = summed.GetValueOrDefault(row.Key) + 1;
            }
        }

        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write("S25\0"u8);
        writer.Write(slots);
        var slotOffsets = new uint[slots];
        output.Position = 8 + 4L * slots;
        // Rows written already: their bytes -> offset
        var writtenSummed = new Dictionary<RowKey, uint>();
        var writtenOther = new Dictionary<RowKey, uint>();

        for (int f = 0; f < ordered.Count; f++)
        {
            var frame = ordered[f];
            int height = rows[f].Length;
            Align(output, 4);
            slotOffsets[frame.Slot] = (uint)output.Position;
            writer.Write(frame.Image.Width);
            writer.Write(height);
            writer.Write(frame.OffsetX);
            writer.Write(frame.OffsetY);
            writer.Write(frame.Incremental ? 0x80000000u : 0u);
            long table = output.Position;
            output.Position = table + 4L * height;
            var rowOffsets = new uint[height];
            for (int y = 0; y < height; y++)
            {
                var row = rows[f][y];
                bool isSummed = frame.Incremental && row.Literal.Count > 0;
                var written = isSummed ? writtenSummed : writtenOther;
                if (written.TryGetValue(row.Key, out uint same))
                {
                    rowOffsets[y] = same;
                    continue;
                }
                byte[] bytes = row.Bytes;
                if (isSummed && summed[row.Key] > 1)
                {
                    // Named n times, summed n times: the differences taken n - 1 times more
                    bytes = (byte[])bytes.Clone();
                    foreach (var (at, length, step) in row.Literal)
                        for (int pass = 1; pass < summed[row.Key]; pass++)
                            for (int k = at + length - 1; k >= at + step; k--)
                                bytes[k] -= bytes[k - step];
                }
                Align(output, 4);
                uint rowAt = (uint)output.Position;
                writer.Write(bytes);
                rowOffsets[y] = rowAt;
                written[row.Key] = rowAt;
            }
            long end = output.Position;
            output.Position = table;
            foreach (uint r in rowOffsets)
                writer.Write(r);
            output.Position = end;
        }
        long fileLength = output.Length;
        output.Position = 8;
        foreach (uint o in slotOffsets)
            writer.Write(o);
        writer.Flush();
        return output.ToArray().AsSpan(0, (int)fileLength).ToArray();
    }

    /// <summary>
    /// The S25 file <paramref name="original"/> with the frames numbered as the decoder numbers
    /// them (<see cref="S25FrameInfo.Index"/>, the number in an extracted frame's name) replaced by
    /// these pictures; every frame keeps its slot, place and kind, and the other frames their
    /// pixels.
    /// </summary>
    public static byte[] ReplaceFrames(byte[] original, IReadOnlyDictionary<int, PixelImage> pictures)
    {
        if (!S25Decoder.IsS25(original))
            throw new InvalidDataException("Not an S25 file.");
        int slots = LittleEndian.ToInt32(original, 4);
        var decoded = S25Decoder.DecodeAllFrames(original);
        foreach (int index in pictures.Keys)
            if (!decoded.Any(f => f.Index == index))
                throw new ArgumentException($"The picture has no frame {index}.");
        var frames = decoded.Select(f => new S25EncoderFrame(f.Slot, f.OffsetX, f.OffsetY,
            pictures.TryGetValue(f.Index, out var picture) ? picture : f.Image,
            (LittleEndian.ToUInt32(original, (int)LittleEndian.ToUInt32(original, 8 + f.Slot * 4) + 0x10) & 0x80000000u) != 0)).ToList();
        return Encode(slots, frames);
    }

    /// <summary>
    /// A row: its length and runs, laid out from an offset that is a multiple of 4, and where the
    /// data of its runs of method 2 or 4 is (index, bytes, bytes a pixel), which an incremental
    /// frame's load changes in place.
    /// </summary>
    private sealed record EncodedRow(byte[] Bytes, List<(int At, int Length, int Step)> Literal)
    {
        public RowKey Key { get; } = new(Bytes);
    }

    private static EncodedRow EncodeRow(ReadOnlySpan<byte> pixels, bool incremental)
    {
        int width = pixels.Length / 4;
        var row = new List<byte>(pixels.Length + 16) { 0, 0 };
        var literal = new List<(int, int, int)>();
        int x = 0;
        while (x < width)
        {
            int kind = Kind(pixels, x);
            int same = 1;
            while (x + same < width && Same(pixels, x, x + same))
                same++;
            if (kind == 0)
            {
                Run(row, 0, same, ReadOnlySpan<byte>.Empty);
                x += same;
                continue;
            }
            if (same >= OneColour)
            {
                // One colour for the run
                int p = x * 4;
                if (kind == 2)
                    Run(row, 3, same, [pixels[p], pixels[p + 1], pixels[p + 2]]);
                else
                    Run(row, 5, same, [pixels[p + 3], pixels[p], pixels[p + 1], pixels[p + 2]]);
                x += same;
                continue;
            }
            // Pixels one by one, until the kind changes or a run of one colour starts (at most
            // 2,047, a header's count: each run is coded from its own first pixel)
            int end = x + 1;
            while (end < width && end - x < MaxRun && Kind(pixels, end) == kind && !OneColourAt(pixels, end, width))
                end++;
            int count = end - x, bytes = kind == 2 ? 3 : 4;
            var data = new byte[count * bytes];
            for (int i = 0; i < count; i++)
            {
                int p = (x + i) * 4, d = i * bytes;
                if (kind == 2)
                {
                    data[d] = pixels[p];
                    data[d + 1] = pixels[p + 1];
                    data[d + 2] = pixels[p + 2];
                }
                else
                {
                    data[d] = pixels[p + 3];
                    data[d + 1] = pixels[p];
                    data[d + 2] = pixels[p + 1];
                    data[d + 3] = pixels[p + 2];
                }
            }
            if (incremental)
                for (int k = data.Length - 1; k >= bytes; k--)
                    data[k] -= data[k - bytes];
            Run(row, kind == 2 ? 2 : 4, count, data);
            literal.Add((row.Count - data.Length, data.Length, bytes));
            x = end;
        }
        int length = row.Count - 2;
        if (length > 0xFFFF)
            throw new ArgumentException($"A row of {width} pixels takes {length} bytes, more than an S25 row holds (65,535).");
        row[0] = (byte)length;
        row[1] = (byte)(length >> 8);
        return new EncodedRow(row.ToArray(), literal);
    }

    /// <summary>
    /// A run of <paramref name="count"/> pixels: its header at an even offset, its data at a
    /// multiple of 4 for methods 3, 4 and 5. A run of one colour (or none) longer than a header
    /// counts (2,047) is cut into several.
    /// </summary>
    private static void Run(List<byte> row, int method, int count, ReadOnlySpan<byte> data)
    {
        for (int done = 0; done < count;)
        {
            int n = Math.Min(MaxRun, count - done);
            if ((row.Count & 1) != 0)
                row.Add(0);
            // The row starts at a multiple of 4, so an index of it is its offset's remainder
            int skip = method is 3 or 4 or 5 ? (4 - (row.Count + 2) % 4) % 4 : 0;
            int header = method << 13 | skip << 11 | n;
            row.Add((byte)header);
            row.Add((byte)(header >> 8));
            for (int i = 0; i < skip; i++)
                row.Add(0);
            foreach (byte b in data)
                row.Add(b);
            done += n;
        }
    }

    /// <summary>0: transparent (all four bytes 0), 2: opaque, 4: any other.</summary>
    private static int Kind(ReadOnlySpan<byte> pixels, int x)
    {
        int p = x * 4;
        byte a = pixels[p + 3];
        if (a == 255)
            return 2;
        return a == 0 && pixels[p] == 0 && pixels[p + 1] == 0 && pixels[p + 2] == 0 ? 0 : 4;
    }

    /// <summary>Whether <see cref="OneColour"/> pixels of one colour start at <paramref name="x"/>.</summary>
    private static bool OneColourAt(ReadOnlySpan<byte> pixels, int x, int width)
    {
        if (x + OneColour > width)
            return false;
        for (int i = 1; i < OneColour; i++)
            if (!Same(pixels, x, x + i))
                return false;
        return true;
    }

    private static bool Same(ReadOnlySpan<byte> pixels, int a, int b) =>
        pixels.Slice(a * 4, 4).SequenceEqual(pixels.Slice(b * 4, 4));

    private static void Align(Stream output, int to)
    {
        while (output.Position % to != 0)
            output.WriteByte(0);
    }

    private static PixelImage ToBgra(PixelImage image)
    {
        var pixels = new byte[image.Width * image.Height * 4];
        for (int i = 0, s = 0; i < pixels.Length; i += 4, s += 3)
        {
            pixels[i] = image.Pixels[s];
            pixels[i + 1] = image.Pixels[s + 1];
            pixels[i + 2] = image.Pixels[s + 2];
            pixels[i + 3] = 255;
        }
        return new PixelImage(image.Width, image.Height, PixelLayout.Bgra32, pixels);
    }

    /// <summary>A row's bytes as a dictionary key.</summary>
    private readonly struct RowKey(byte[] bytes) : IEquatable<RowKey>
    {
        private readonly byte[] m_bytes = bytes;
        private readonly int m_hash = Hash(bytes);

        private static int Hash(byte[] bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        public bool Equals(RowKey other) => m_hash == other.m_hash && m_bytes.AsSpan().SequenceEqual(other.m_bytes);
        public override bool Equals(object? obj) => obj is RowKey other && Equals(other);
        public override int GetHashCode() => m_hash;
    }
}
