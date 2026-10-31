using System.Buffers.Binary;
using System.Text;

// Checks that a decoded file of the archives came out whole, by what its kind allows: an S25's
// index and row tables point inside the file, a scenario TXT is strict Shift-JIS, an Ogg stream
// (OGV, OGG) has right page CRCs. A wrong key spoils bytes these see (DecodeBin's date spoils 4
// bytes at 0x200-0x2FF: engine-notes.md, "DecodeBin's date").
static class FileChecks
{
    static readonly Encoding s_sjis;
    static readonly uint[] s_crc = new uint[256];

    static FileChecks()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        s_sjis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        for (uint n = 0; n < 256; n++)
        {
            uint r = n << 24;
            for (int k = 0; k < 8; k++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            s_crc[n] = r;
        }
    }

    /// <summary>The kinds checked: S25, TXT and the Ogg ones.</summary>
    public static bool Checked(string name) => Path.GetExtension(name).ToUpperInvariant() is ".S25" or ".TXT" or ".OGV" or ".OGG";

    /// <summary>Why a file is not whole, or null (also for kinds not checked).</summary>
    public static string? Problem(string name, byte[] data) => Path.GetExtension(name).ToUpperInvariant() switch
    {
        ".S25" => S25(data),
        ".TXT" => Text(data),
        ".OGV" or ".OGG" => Ogg(data),
        _ => null,
    };

    public static string? S25(byte[] d)
    {
        if (d.Length < 8 || d[0] != 'S' || d[1] != '2' || d[2] != '5')
            return "no S25 header";
        long frames = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        if (8 + 4 * frames > d.Length)
            return $"{frames} frames do not fit";
        for (int i = 0; i < frames; i++)
        {
            long at = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8 + 4 * i));
            if (at == 0)
                continue;
            if (at + 0x14 > d.Length)
                return $"frame {i} at {at:X} outside the file";
            long height = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at + 4));
            if (at + 0x14 + 4 * height > d.Length)
                return $"frame {i} at {at:X}: its {height} rows do not fit";
            for (int y = 0; y < height; y++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)(at + 0x14 + 4 * y))) >= d.Length)
                    return $"frame {i} at {at:X}: row {y} outside the file";
        }
        return null;
    }

    public static string? Text(byte[] d)
    {
        try
        {
            s_sjis.GetString(d);
            return null;
        }
        catch (DecoderFallbackException e)
        {
            return $"not Shift-JIS at {e.Index:X}";
        }
    }

    /// <summary>The Ogg pages from the first "OggS" (OGV has a header of its own before it), each with a right CRC.</summary>
    public static string? Ogg(byte[] d)
    {
        int at = d.AsSpan(0, Math.Min(d.Length, 256)).IndexOf("OggS"u8);
        if (at < 0)
            return "no Ogg page";
        int pages = 0;
        while (at + 27 <= d.Length && d.AsSpan(at, 4).SequenceEqual("OggS"u8))
        {
            int segments = d[at + 26], size = 27 + segments;
            if (at + size > d.Length)
                break;
            for (int k = 0; k < segments; k++)
                size += d[at + 27 + k];
            if (at + size > d.Length)
                break;
            if (OggCrc(d, at, size) != BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 22)))
                return $"Ogg page {pages} at {at:X}: wrong CRC";
            at += size;
            pages++;
        }
        return pages == 0 ? "no Ogg page" : null;
    }

    /// <summary>The CRC of the Ogg page at <paramref name="at"/> (its CRC field taken as 0).</summary>
    public static uint OggCrc(byte[] d, int at, int size)
    {
        uint crc = 0;
        for (int k = 0; k < size; k++)
        {
            byte b = k is >= 22 and < 26 ? (byte)0 : d[at + k];
            crc = (crc << 8) ^ s_crc[(crc >> 24) ^ b];
        }
        return crc;
    }

    /// <summary>
    /// The dwords (0-63) of 0x200-0x2FF that Decrypt2 XORs with DecodeBin's date at 0x1020: those
    /// whose dword 0x100 bytes before, masked with 0x1FFC, is 0x1020. Decrypt2 leaves those bytes
    /// as they are, so a decoded file tells them. None for a file under 0x400 bytes.
    /// </summary>
    public static List<int> DateDwords(byte[] d)
    {
        var result = new List<int>();
        if (d.Length < 0x400)
            return result;
        for (int i = 0; i < 0x40; i++)
            if ((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x100 + 4 * i)) & 0x1FFC) == 0x1020)
                result.Add(i);
        return result;
    }
}
