// MONTBL.BIN: the expression codes of "$L_MONT,plane,,x,y,?,M,code" (docs/engine-notes.md,
// section 2). 100,000 entries of 8 bytes, then a string table. An entry is a u16 offset of the
// S25 name in the string table, a u16 offset of a face picture name, then six 5-bit slot values
// (base and layers 1-5, 31 = layer off); all FF = an unused code.


namespace OpenShiina.Formats;

/// <summary>A layered picture chosen by an expression code: the S25 file and its slots (base first).</summary>
public sealed record Montage(string File, int[] Slots);

public sealed class MontageTable
{
    private const int Entries = 100000, EntrySize = 8;

    private readonly byte[] m_table;

    public MontageTable(byte[] table)
    {
        m_table = table;
    }

    /// <summary>The picture of an expression code, or null when the code is not in the table.</summary>
    public Montage? Get(int code)
    {
        const int stringBase = Entries * EntrySize;
        var table = m_table;
        if (code < 0 || code >= Entries || table.Length <= stringBase)
            return null;

        int at = code * EntrySize;
        ushort nameOffset = BitConverter.ToUInt16(table, at);
        uint packed = BitConverter.ToUInt32(table, at + 4);
        if (nameOffset == 0xFFFF || stringBase + nameOffset >= table.Length)
            return null;

        int end = Array.IndexOf(table, (byte)0, stringBase + nameOffset);
        if (end < 0)
            end = table.Length;
        string file = Encodings.cp932.GetString(table, stringBase + nameOffset, end - stringBase - nameOffset);

        var slots = new List<int>();
        for (int k = 0; k < 6; k++)
        {
            int v = (int)(packed >> (k * 5)) & 31;
            if (v != 31)
                slots.Add(k * 100 + v);
        }
        return new Montage(file, slots.ToArray());
    }
}
