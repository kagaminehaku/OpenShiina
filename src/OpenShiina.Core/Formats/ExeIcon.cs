// The icon of a Windows program, read from its resources without Windows: the first icon group
// (RT_GROUP_ICON) and, of its images (RT_ICON), the largest with the most colours, written out as
// a .ico file of that one image - which both players' image decoders read.

using System.IO;

namespace OpenShiina.Formats;

public static class ExeIcon
{
    private const int RT_ICON = 3, RT_GROUP_ICON = 14;

    /// <summary>The program's icon as a one-image .ico file, or null when it has none (or is not a PE file).</summary>
    public static byte[]? Read(string exePath)
    {
        try
        {
            return Extract(File.ReadAllBytes(exePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[]? Extract(byte[] pe)
    {
        int U16(int at) => BitConverter.ToUInt16(pe, at);
        int I32(int at) => BitConverter.ToInt32(pe, at);
        if (pe.Length < 0x40 || U16(0) != 0x5A4D)
            return null;
        int header = I32(0x3C);
        if (header < 0 || header + 24 > pe.Length || I32(header) != 0x00004550)
            return null;
        int sections = U16(header + 6), optionalSize = U16(header + 20);
        int optional = header + 24;
        int dataDirectories = U16(optional) switch { 0x10B => optional + 96, 0x20B => optional + 112, _ => -1 };
        if (dataDirectories < 0)
            return null;
        int resourceRva = I32(dataDirectories + 2 * 8);
        if (resourceRva == 0)
            return null;
        int sectionTable = optional + optionalSize;

        // RVA -> file offset through the section table
        int Offset(int rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = sectionTable + 40 * i;
                int virtualAddress = I32(s + 12), virtualSize = Math.Max(I32(s + 8), I32(s + 16)), raw = I32(s + 20);
                if (rva >= virtualAddress && rva < virtualAddress + virtualSize)
                    return rva - virtualAddress + raw;
            }
            return -1;
        }

        int root = Offset(resourceRva);
        if (root < 0)
            return null;

        // The entries of a resource directory: (id or name offset, is a directory, offset)
        IEnumerable<(int Id, bool Directory, int Offset)> Entries(int directory)
        {
            int named = U16(directory + 12), ids = U16(directory + 14);
            for (int i = 0; i < named + ids; i++)
            {
                int e = directory + 16 + 8 * i;
                int name = I32(e), data = I32(e + 4);
                // Named entries (bit 31) are not looked for: their id is -1
                yield return (name < 0 ? -1 : name, data < 0, root + (data & 0x7FFFFFFF));
            }
        }

        // The data of the first language of a resource (type, id: the first one when id is null)
        byte[]? Data(int type, int? id)
        {
            foreach (var (typeId, typeIsDirectory, typeAt) in Entries(root))
            {
                if (typeId != type || !typeIsDirectory)
                    continue;
                foreach (var (nameId, nameIsDirectory, nameAt) in Entries(typeAt))
                {
                    if (id != null && nameId != id || !nameIsDirectory)
                        continue;
                    foreach (var (_, languageIsDirectory, dataEntry) in Entries(nameAt))
                    {
                        if (languageIsDirectory)
                            continue;
                        int at = Offset(I32(dataEntry)), size = I32(dataEntry + 4);
                        if (at < 0 || size <= 0 || at + size > pe.Length)
                            return null;
                        return pe.AsSpan(at, size).ToArray();
                    }
                }
            }
            return null;
        }

        // GRPICONDIR: reserved, type, count, then 14-byte entries (width, height, colours,
        // reserved, planes, bit count, bytes, id)
        if (Data(RT_GROUP_ICON, null) is not { Length: >= 6 } group)
            return null;
        int count = BitConverter.ToUInt16(group, 4);
        int best = -1;
        (int Size, int Bits) bestKey = (-1, -1);
        for (int i = 0; i < count && 6 + 14 * i + 14 <= group.Length; i++)
        {
            int e = 6 + 14 * i;
            int size = group[e] == 0 ? 256 : group[e];
            int bits = BitConverter.ToUInt16(group, e + 6);
            if (size > bestKey.Size || size == bestKey.Size && bits > bestKey.Bits)
            {
                bestKey = (size, bits);
                best = e;
            }
        }
        if (best < 0 || Data(RT_ICON, BitConverter.ToUInt16(group, best + 12)) is not { } image)
            return null;

        // ICONDIR and one ICONDIRENTRY (the group's entry with the image's offset), then the image
        var ico = new byte[6 + 16 + image.Length];
        BitConverter.TryWriteBytes(ico.AsSpan(2), (ushort)1);
        BitConverter.TryWriteBytes(ico.AsSpan(4), (ushort)1);
        group.AsSpan(best, 8).CopyTo(ico.AsSpan(6));
        BitConverter.TryWriteBytes(ico.AsSpan(14), image.Length);
        BitConverter.TryWriteBytes(ico.AsSpan(18), 22);
        image.CopyTo(ico.AsSpan(22));
        return ico;
    }
}
