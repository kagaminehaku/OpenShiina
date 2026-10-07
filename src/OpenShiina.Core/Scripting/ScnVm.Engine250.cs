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

    private void RegisterEngine250()
    {
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
