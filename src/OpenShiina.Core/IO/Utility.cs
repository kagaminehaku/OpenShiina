// Ported from GARbro GameRes/Utility.cs
// Utility classes for binary data manipulation

using System.Text;

namespace OpenShiina.IO;

public static class Binary
{
    public static uint BigEndian(uint u)
    {
        return u << 24 | (u & 0xff00) << 8 | (u & 0xff0000) >> 8 | u >> 24;
    }

    public static ushort BigEndian(ushort u)
    {
        return (ushort)(u << 8 | u >> 8);
    }

    public static byte RotByteR(byte v, int count)
    {
        count &= 7;
        return (byte)(v >> count | v << (8 - count));
    }

    public static byte RotByteL(byte v, int count)
    {
        count &= 7;
        return (byte)(v << count | v >> (8 - count));
    }

    public static uint RotL(uint v, int count)
    {
        count &= 0x1f;
        return v << count | v >> (32 - count);
    }

    public static uint RotR(uint v, int count)
    {
        count &= 0x1f;
        return v >> count | v << (32 - count);
    }

    public static void CopyOverlapped(byte[] data, int src, int dst, int count)
    {
        if (dst > src)
        {
            while (count > 0)
            {
                int preceding = Math.Min(dst - src, count);
                Buffer.BlockCopy(data, src, data, dst, preceding);
                dst += preceding;
                count -= preceding;
            }
        }
        else
        {
            Buffer.BlockCopy(data, src, data, dst, count);
        }
    }

    public static bool AsciiEqual(byte[] name1, int offset, string name2)
    {
        if (name1.Length - offset < name2.Length)
            return false;
        for (int i = 0; i < name2.Length; ++i)
            if ((char)name1[offset + i] != name2[i])
                return false;
        return true;
    }

    public static string GetCString(byte[] data, int index, int length)
    {
        int end = Array.IndexOf<byte>(data, 0, index, length);
        if (end < 0) end = index + length;
        return Encodings.cp932.GetString(data, index, end - index); // Shift-JIS
    }
}

public static class LittleEndian
{
    public static ushort ToUInt16(byte[] data, int index)
    {
        return (ushort)(data[index] | data[index + 1] << 8);
    }

    public static uint ToUInt32(byte[] data, int index)
    {
        return (uint)(data[index] | data[index + 1] << 8 |
                      data[index + 2] << 16 | data[index + 3] << 24);
    }

    public static int ToInt32(byte[] data, int index)
    {
        return (int)ToUInt32(data, index);
    }

    public static void Pack(int value, byte[] data, int index)
    {
        data[index] = (byte)value;
        data[index + 1] = (byte)(value >> 8);
        data[index + 2] = (byte)(value >> 16);
        data[index + 3] = (byte)(value >> 24);
    }

    public static void Pack(short value, byte[] data, int index)
    {
        data[index] = (byte)value;
        data[index + 1] = (byte)(value >> 8);
    }

    public static long ToInt64(byte[] data, int index)
    {
        return (long)ToUInt64(data, index);
    }

    public static ulong ToUInt64(byte[] data, int index)
    {
        return (ulong)data[index] | (ulong)data[index + 1] << 8 |
               (ulong)data[index + 2] << 16 | (ulong)data[index + 3] << 24 |
               (ulong)data[index + 4] << 32 | (ulong)data[index + 5] << 40 |
               (ulong)data[index + 6] << 48 | (ulong)data[index + 7] << 56;
    }
}

public static class BigEndian
{
    public static uint ToUInt32(byte[] data, int index)
    {
        return (uint)(data[index] << 24 | data[index + 1] << 16 |
                      data[index + 2] << 8 | data[index + 3]);
    }

    public static ushort ToUInt16(byte[] data, int index)
    {
        return (ushort)(data[index] << 8 | data[index + 1]);
    }
}

public static class Encodings
{
    // Field initializers run before a static constructor body, so the provider
    // must be registered inside the initializer itself.
    public static readonly Encoding cp932 = CreateCp932();

    private static Encoding CreateCp932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }
}
