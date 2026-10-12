// Opcodes only the v2.50 executable has (Re:Rem Plus, Maki Fes!), read from REMPLUS.EXE (the
// handlers named are its functions; the table is Data/ScnOps/ops_v250.tsv).

using System.IO.Compression;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>077A w, h (0xD09E30 / 0xD09E34): a size the v2.50 START sets at start (1280 x 720).</summary>
    public (int Width, int Height) Size250 { get; private set; }

    /// <summary>A character drawn from a picture of its own (009C): its code, size and bytes.</summary>
    private sealed record Glyph250(ushort Code, int Width, int Height, byte[] Pixels);

    // 009C's table: per text record (the task's, ctx+0x1004), by index (0x539D48 + record *
    // 0x9000 + index * 36; the engine keeps its font glyphs in the same table, after index 0x1FF)
    private readonly Dictionary<(int Record, int Index), Glyph250> m_glyphs250 = new();

    /// <summary>
    /// The character a text record draws for a code from 009C's pictures (FUN_00439xxx looks the
    /// table up before the font: the lowest index with the code), or null.
    /// </summary>
    private ScnGlyph? Glyph250For(int record, int code)
    {
        if (m_glyphs250.Count == 0)
            return null;
        Glyph250? found = null;
        int foundIndex = int.MaxValue;
        foreach (var ((r, index), glyph) in m_glyphs250)
            if (r == record && glyph.Code == (ushort)code && index < foundIndex)
            {
                found = glyph;
                foundIndex = index;
            }
        // Placed at (x, y) as it is: origin 0, and the 9999 of 009C keeps it off the baseline
        return found == null ? null
            : new ScnGlyph(found.Width, found.Height, 0, int.MinValue, (short)found.Width, 0, found.Width, found.Pixels);
    }

    /// <summary>
    /// 1072's blur (FUN_0041F440): a box blur of a rectangle of the source frame (at sx, sy) into
    /// the rectangle (x, y, w, h) of the destination frame, clipped to that frame, rx pixels wide
    /// along the rows and ry high down the columns (at least 1): the rows go into a buffer the
    /// size of the source frame, the columns from it into the destination. A sum of n bytes gives
    /// ((0xFFFFFFFF / n) * sum + 0x10000) >> 32. As the executable does: the edges repeat the
    /// rectangle's first pixel and the one 4 before its end, and after a line's first sum the
    /// pixels leaving and entering the window are read one rectangle's left (top) further on;
    /// 4 bytes a pixel whatever the frames have.
    /// </summary>
    private void BoxBlur250(int dstSlot, int dstFrame, int x, int y, int w, int h, int srcSlot, int srcFrame, int sx, int sy, int rx, int ry)
    {
        int dstPicture = Picture(dstSlot), srcPicture = Picture(srcSlot);
        if (dstPicture == 0 || srcPicture == 0)
            return;
        int FrameOf(int picture, int frame) => (uint)frame > (uint)Read32(picture + 4) ? 0 : Read32(picture + 8 + 4 * frame);
        int df = FrameOf(dstPicture, dstFrame), sf = FrameOf(srcPicture, srcFrame);
        if (df == 0 || sf == 0)
            return;
        Span<int> d = [x, y, x + w, y + h], s = [sx, sy, sx + w, sy + h];
        if (!ClipRect(d, s, Read32(df), Read32(df + 4)))
            return;
        int width = d[2] - d[0], height = d[3] - d[1];
        if (width <= 0 || height <= 0)
            return;
        int Stride(int f) => Read32(f) * (Read32(Read32(f + 0x14)) < 0 ? 4 : 3) + 8;
        int dstStride = Stride(df), srcStride = Stride(sf);
        int dstBase = Read32(df + 0x14) + 8, srcBase = Read32(sf + 0x14) + 8;
        int bufferBytes = Read32(sf + 4) * srcStride;
        rx = Math.Max(rx, 1);
        ry = Math.Max(ry, 1);
        int left = s[0], top = s[1], right = left + width, bottom = top + height;
        int[] edges = new int[Math.Max(rx + width, ry + height)];

        // A window's four sums written as a pixel, then the pixel at `leaving` out and `entering` in
        static uint Channel(uint scale, uint sum) => (uint)(((ulong)scale * sum + 0x10000) >> 32);
        void Line(int first, int cursor, int to, int toStep, int count, int window)
        {
            uint scale = 0xFFFFFFFF / (uint)window;
            uint b0 = 0, b1 = 0, b2 = 0, b3 = 0;
            for (int k = 0; k < window; k++)
            {
                uint p = (uint)Read32(first + edges[k]);
                b0 += p & 0xFF;
                b1 += (p >> 8) & 0xFF;
                b2 += (p >> 16) & 0xFF;
                b3 += p >> 24;
            }
            for (int k = 0; k < count; k++, to += toStep)
            {
                Write32(to, (int)(Channel(scale, b0) & 0xFF | (Channel(scale, b1) & 0xFF) << 8
                                  | (Channel(scale, b2) & 0xFF) << 16 | Channel(scale, b3) << 24));
                uint leaving = (uint)Read32(cursor + edges[k]), entering = (uint)Read32(cursor + edges[k + window]);
                b0 = b0 - (leaving & 0xFF) + (entering & 0xFF);
                b1 = b1 - ((leaving >> 8) & 0xFF) + ((entering >> 8) & 0xFF);
                b2 = b2 - ((leaving >> 16) & 0xFF) + ((entering >> 16) & 0xFF);
                b3 = b3 - (leaving >> 24) + (entering >> 24);
            }
        }

        // Along the rows: byte offsets of the pixels of the window, the edges repeated
        for (int k = 0, at = left - rx / 2; k < rx + width; k++, at++)
            edges[k] = at < left ? left * 4 : at >= right ? right * 4 - 16 : at * 4;
        if (ry == 1)
        {
            // Rows only: straight from the source into the destination
            for (int row = 0; row < height; row++)
                Line(srcBase + (top + row) * srcStride, srcBase + (top + row) * srcStride + left * 4,
                     dstBase + (d[1] + row) * dstStride + d[0] * 4, 4, width, rx);
            return;
        }
        int buffer = Allocate(bufferBytes);
        try
        {
            if (rx == 1)
                CopyMemory(buffer, srcBase, bufferBytes);
            else
                for (int row = top; row < bottom; row++)
                    Line(srcBase + row * srcStride, srcBase + row * srcStride + left * 4, buffer + row * srcStride + left * 4, 4, width, rx);
            // Down the columns, from the buffer
            for (int k = 0, at = top - ry / 2; k < ry + height; k++, at++)
                edges[k] = at < top ? top * srcStride : at >= bottom ? (bottom - 4) * srcStride : at * srcStride;
            for (int column = 0; column < width; column++)
                Line(buffer + (left + column) * 4, buffer + top * srcStride + (left + column) * 4,
                     dstBase + d[1] * dstStride + (d[0] + column) * 4, dstStride, height, ry);
        }
        finally
        {
            Free(buffer);
        }
    }

    private void RegisterEngine250()
    {
        // 1072 dst, frame, x, y, w, h, src, frame, sx, sy, rx, ry (REMPLUS.EXE 0x42F79A,
        // FUN_0041F440): a box blur of a picture's rectangle into another's (Bitch Shimai)
        Register(0x1072, (vm, c, i) =>
        {
            Span<int> a = stackalloc int[12];
            for (int k = 0; k < 12; k++)
                a[k] = vm.Value(c, i.Args[k]);
            vm.BoxBlur250(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11]);
            return 0;
        });
        // 0113 archive, file, v (REMPLUS.EXE 0x42ABE8): v = 1 when the file opens for reading
        // from that archive (FUN_00405640, then closed; archive 0: anywhere 0154 looks), else 0.
        // Bitch Gakuen asks its patch archives BITCHES3_P?.WAR for a picture's variant, ? a letter
        // from a setting, and keeps the plain picture when that archive is not there
        Register(0x0113, (vm, c, i) =>
        {
            int archive = vm.Value(c, i.Args[0]);
            string path = vm.ReadString(vm.Value(c, i.Args[1]));
            bool found = archive == 0 ? vm.ScriptFileSize(path) != null : vm.m_host.ArchiveFileSize(vm.ReadString(archive), path) != null;
            vm.Store(c, i.Args[2], found ? 1 : 0);
            return 0;
        });
        // 0410 v (REMPLUS.EXE 0x42B137): v = RIO.INI's GetKeyboardState (0xD09E68; also set when
        // DirectInput fails): the keys are read with GetKeyboardState, by virtual key, instead of
        // DirectInput's scan codes. 03EC takes scan codes here (0): Bitch Nee-chan's START reads
        // its keys with 03EC as they are, and turns them into virtual keys first otherwise
        Register(0x0410, (vm, c, i) => { vm.Store(c, i.Args[0], 0); return 0; });
        // 077A w, h
        Register(0x077A, (vm, c, i) =>
        {
            int w = vm.Value(c, i.Args[0]), h = vm.Value(c, i.Args[1]);
            vm.Size250 = (w, h);
            return 0;
        });
        // 009C index, text, w, h, pixels (FUN_00416E90): character index of the task's text
        // record becomes the first (double-byte, big-endian) character of text, drawn as the w x h
        // bytes at pixels (copied; one byte a pixel, as the font's glyphs: 0-64); text 0 removes it
        Register(0x009C, (vm, c, i) =>
        {
            int index = vm.Value(c, i.Args[0]), text = vm.Value(c, i.Args[1]);
            var key = (c.TextRecord, index);
            if (text == 0)
            {
                vm.m_glyphs250.Remove(key);
                return 0;
            }
            ushort code = (ushort)(vm.ReadByte(text) << 8 | vm.ReadByte(text + 1));
            int w = vm.Value(c, i.Args[2]), h = vm.Value(c, i.Args[3]);
            int pixels = vm.Value(c, i.Args[4]);
            long size = (long)w * h;
            if (w < 0 || h < 0 || size > 0x1000000)
                return 2;
            vm.m_glyphs250[key] = new Glyph250(code, w, h, vm.ReadBytes(pixels, (int)size));
            return 0;
        });
        // 02FD data, size, level, block, blockSize (FUN_00421F90 -> FUN_00441B60): data packed
        // with zlib 1.2.7 (deflate at level) into a new block: [total size, size, zlib stream]
        Register(0x02FD, (vm, c, i) =>
        {
            int data = vm.Value(c, i.Args[0]), size = vm.Value(c, i.Args[1]), level = vm.Value(c, i.Args[2]);
            if (size < 0 || level is < -1 or > 9)
                return 2;
            var packed = new MemoryStream();
            packed.Write(new byte[8]);
            using (var zlib = new ZLibStream(packed, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true))
                zlib.Write(vm.ReadBytes(data, size));
            var bytes = packed.ToArray();
            BitConverter.TryWriteBytes(bytes.AsSpan(0), bytes.Length);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), size);
            // The engine's block is as large as zlib's bound; what follows the stream is unused
            int block = vm.Allocate(Math.Max(bytes.Length, (size + 32) / 8 + size + 256));
            vm.WriteBytes(block, bytes);
            vm.Store(c, i.Args[3], block);
            vm.Store(c, i.Args[4], bytes.Length);
            return 0;
        });
        // 02FE block, data, size (FUN_00422030 -> FUN_00441C50): a block of 02FD unpacked into a
        // new one of the size it names
        Register(0x02FE, (vm, c, i) =>
        {
            int block = vm.Value(c, i.Args[0]);
            int total = vm.Read32(block), size = vm.Read32(block + 4);
            if (size < 0 || total < 8)
                return 2;
            var output = new byte[size];
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(vm.ReadBytes(block + 8, total - 8)), CompressionMode.Decompress);
                zlib.ReadExactly(output);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                return 2;
            }
            int data = vm.Allocate(Math.Max(size, 1));
            vm.WriteBytes(data, output);
            vm.Store(c, i.Args[1], data);
            vm.Store(c, i.Args[2], size);
            return 0;
        });
    }
}
