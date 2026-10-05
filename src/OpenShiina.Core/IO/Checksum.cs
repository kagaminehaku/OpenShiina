// Ported from GARbro GameRes/Checksum.cs
// CRC32 and Adler32 checksum implementations

namespace OpenShiina.IO;

/// <summary>
/// Non-reflected (MSB-first) CRC-32 with polynomial 0x04C11DB7, as in GARbro ArcFormats/Crc32.cs.
/// This is NOT the common zip/PNG CRC-32; Decoder.Decrypt2 depends on this exact variant.
/// </summary>
public static class Crc32Normal
{
    private static readonly uint[] Table = InitTable();

    private static uint[] InitTable()
    {
        const uint polynomial = 0x04C11DB7;
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n << 24;
            for (int k = 0; k < 8; k++)
            {
                if (0 != (c & 0x80000000u))
                    c = polynomial ^ (c << 1);
                else
                    c <<= 1;
            }
            table[n] = c;
        }
        return table;
    }

    public static uint UpdateCrc(uint crc, byte[] data, int index, int length)
    {
        for (int i = 0; i < length; i++)
            crc = Table[(crc >> 24) ^ data[index + i]] ^ (crc << 8);
        return crc;
    }

    public static uint Compute(byte[] data, int index, int length)
    {
        return UpdateCrc(0xFFFFFFFF, data, index, length) ^ 0xFFFFFFFF;
    }
}

public static class Adler32
{
    public static uint Compute(byte[] data, int index, int length)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < length; i++)
        {
            a = (a + data[index + i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}

public sealed class Crc16
{
    private static readonly ushort[] Table = InitTable();
    private ushort m_crc = 0xFFFF;

    public ushort Value => m_crc;

    private static ushort[] InitTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (ushort)((crc >> 1) ^ 0x8408); // reflected CCITT, matches GARbro Crc16 table
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    public void Update(byte[] data, int index, int length)
    {
        for (int i = 0; i < length; i++)
            m_crc = (ushort)((m_crc >> 8) ^ Table[(m_crc ^ data[index + i]) & 0xFF]);
    }
}
