// Variable-length codes read from a big-endian bit stream (the VC-1 and WMA decoders), looked
// up a few bits at a time as FFmpeg's get_vlc2 does, and the bit reader they are read with.

using System.Buffers.Binary;

namespace OpenShiina.Formats;

/// <summary>A table of variable-length codes and their symbols.</summary>
internal sealed class VlcTable
{
    /// <summary>Bits looked up at the first level.</summary>
    public readonly int Bits;
    // Per entry: length > 0 a code of that length and its symbol, 0 no code, < 0 a subtable of
    // -length bits starting at the entry the symbol gives
    private readonly int[] m_symbols;
    private readonly sbyte[] m_lengths;

    private VlcTable(int bits, List<(uint Code, int Length, int Symbol)> codes)
    {
        Bits = bits;
        var symbols = new List<int>();
        var lengths = new List<sbyte>();
        Build(symbols, lengths, bits, codes);
        m_symbols = symbols.ToArray();
        m_lengths = lengths.ToArray();
    }

    /// <summary>Codes and lengths per symbol; the symbol is the index unless <paramref name="symbols"/> are given.</summary>
    public static VlcTable FromCodes(int bits, ReadOnlySpan<uint> codes, ReadOnlySpan<byte> lengths, ReadOnlySpan<int> symbols = default)
    {
        var list = new List<(uint, int, int)>();
        for (int i = 0; i < codes.Length; i++)
            if (lengths[i] > 0)
                list.Add((codes[i], lengths[i], symbols.IsEmpty ? i : symbols[i]));
        return new VlcTable(bits, list);
    }

    public static VlcTable FromCodes(int bits, ReadOnlySpan<ushort> codes, ReadOnlySpan<byte> lengths)
    {
        var wide = new uint[codes.Length];
        for (int i = 0; i < codes.Length; i++)
            wide[i] = codes[i];
        return FromCodes(bits, wide, lengths);
    }

    /// <summary>Code / length pairs, the symbol being the pair's index.</summary>
    public static VlcTable FromPairs(int bits, ReadOnlySpan<uint> pairs)
    {
        var codes = new uint[pairs.Length / 2];
        var lengths = new byte[pairs.Length / 2];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = pairs[2 * i];
            lengths[i] = (byte)pairs[2 * i + 1];
        }
        return FromCodes(bits, codes, lengths);
    }

    /// <summary>
    /// ff_vlc_init_from_lengths: canonical codes given by their lengths in table order, each
    /// symbol plus <paramref name="offset"/>.
    /// </summary>
    public static VlcTable FromLengths(int bits, ReadOnlySpan<byte> lengths, ReadOnlySpan<int> symbols, int offset)
    {
        var list = new List<(uint, int, int)>();
        ulong code = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            if (len <= 0)
                continue;
            list.Add(((uint)(code >> (32 - len)), len, symbols[i] + offset));
            code += 1UL << (32 - len);
        }
        return new VlcTable(bits, list);
    }

    private static int Build(List<int> symbols, List<sbyte> lengths, int bits, List<(uint Code, int Length, int Symbol)> codes)
    {
        int start = symbols.Count;
        for (int i = 0; i < 1 << bits; i++)
        {
            symbols.Add(0);
            lengths.Add(0);
        }
        foreach (var (code, length, symbol) in codes)
        {
            if (length > bits)
                continue;
            int first = (int)(code << (bits - length)), count = 1 << (bits - length);
            for (int i = 0; i < count; i++)
            {
                symbols[start + first + i] = symbol;
                lengths[start + first + i] = (sbyte)length;
            }
        }
        foreach (var group in codes.Where(c => c.Length > bits).GroupBy(c => (int)(c.Code >> (c.Length - bits))))
        {
            var rest = group.Select(c => (c.Code & ((1u << (c.Length - bits)) - 1), c.Length - bits, c.Symbol)).ToList();
            int subBits = Math.Min(rest.Max(c => c.Item2), bits);
            int sub = Build(symbols, lengths, subBits, rest);
            symbols[start + group.Key] = sub;
            lengths[start + group.Key] = (sbyte)-subBits;
        }
        return start;
    }

    /// <summary>The symbol of the code at bit <paramref name="pos"/> of <paramref name="data"/> (moved past it), or -1 with no code there.</summary>
    public int Read(byte[] data, ref int pos)
    {
        int bits = Bits, index = 0;
        while (true)
        {
            int i = index + (int)BitReader.Show(data, pos, bits);
            int length = m_lengths[i];
            if (length > 0)
            {
                pos += length;
                return m_symbols[i];
            }
            if (length == 0)
                return -1;
            pos += bits;
            index = m_symbols[i];
            bits = -length;
        }
    }

    /// <summary>The symbol of the code at the reader's position (moved past it), or -1.</summary>
    public int Read(BitReader reader)
    {
        int pos = reader.Position;
        int v = Read(reader.Data, ref pos);
        reader.Position = pos;
        return v;
    }
}

/// <summary>A big-endian bit reader over an array with at least 8 bytes after its data.</summary>
internal sealed class BitReader
{
    public byte[] Data;
    public int Position;
    public int Size;

    public BitReader(byte[] data, int sizeBits, int position = 0)
    {
        Data = data;
        Size = sizeBits;
        Position = position;
    }

    public int Left => Size - Position;

    /// <summary>The next <paramref name="n"/> bits (0-32) at a bit position of a padded array, without moving.</summary>
    public static uint Show(byte[] data, int pos, int n)
    {
        int at = pos >> 3;
        if (n == 0 || pos < 0 || at + 8 > data.Length)
            return 0;
        ulong w = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at));
        return (uint)((w << (pos & 7)) >> (64 - n));
    }

    public uint Show(int n) => Show(Data, Position, n);

    public int Get(int n)
    {
        uint v = Show(Data, Position, n);
        Position += n;
        return (int)v;
    }

    public uint GetLong(int n)
    {
        if (n <= 24)
            return (uint)Get(n);
        uint hi = (uint)Get(n - 16);
        return hi << 16 | (uint)Get(16);
    }

    public int Bit() => Get(1);

    public void Skip(int n) => Position += n;

    public void Align() => Position = (Position + 7) & ~7;
}
