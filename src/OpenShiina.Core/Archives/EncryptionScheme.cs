// Grand†CROSS / ShiinaRio archive encryption scheme definitions
// Ported from GARbro ArcFormats/ShiinaRio/WarcEncryption.cs


namespace OpenShiina.Archives;

public class EncryptionScheme
{
    public string Name { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public int Version { get; set; }
    public int EntryNameSize { get; set; } = 32;
    public byte[]? CryptKey { get; set; }
    public uint[]? HelperKey { get; set; }
    public byte[]? Region { get; set; }
    public byte[]? DecodeBin { get; set; }
    public IByteArray? ShiinaImage { get; set; }
    public IDecryptExtra? ExtraCrypt { get; set; }
}

public interface IDecryptExtra
{
    void Decrypt(byte[] data, int index, uint length, uint flags);
    void Encrypt(byte[] data, int index, uint length, uint flags);
}

public abstract class KeyDecryptBase : IDecryptExtra
{
    protected readonly uint Seed;
    protected readonly byte[] DecodeTable;
    protected uint MinLength = 0x400;
    protected int PostDataOffset = 0x200;

    public KeyDecryptBase(uint seed, byte[] decode_bin)
    {
        Seed = seed;
        DecodeTable = decode_bin;
    }

    public void Decrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < MinLength)
            return;
        if ((flags & 0x202) == 0x202)
            DecryptPre(data, index, length);
        if ((flags & 0x204) == 0x204)
            DecryptPost(data, index, length);
    }

    public void Encrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < MinLength)
            return;
        if ((flags & 0x104) == 0x104)
            DecryptPost(data, index, length);
        if ((flags & 0x102) == 0x102)
            DecryptPre(data, index, length);
    }

    protected abstract void DecryptPre(byte[] data, int index, uint length);

    protected virtual void DecryptPost(byte[] data, int index, uint length)
    {
        int pos = index + PostDataOffset;
        data[pos] ^= (byte)Seed;
        data[pos + 1] ^= (byte)(Seed >> 8);
        data[pos + 2] ^= (byte)(Seed >> 16);
        data[pos + 3] ^= (byte)(Seed >> 24);
    }
}

public abstract class KeyDecryptExtra : KeyDecryptBase
{
    protected int EncryptedSize = 0xFF;

    public KeyDecryptExtra(uint seed, byte[] decode_bin) : base(seed, decode_bin)
    {
    }

    protected override void DecryptPre(byte[] data, int index, uint length)
    {
        var k = new uint[4];
        InitKey(Seed, k);
        for (int i = 0; i < EncryptedSize; ++i)
        {
            uint j = k[3] ^ (k[3] << 11) ^ k[0] ^ ((k[3] ^ (k[3] << 11) ^ (k[0] >> 11)) >> 8);
            k[3] = k[2];
            k[2] = k[1];
            k[1] = k[0];
            k[0] = j;
            data[index + i] ^= DecodeTable[j % DecodeTable.Length];
        }
    }

    protected abstract void InitKey(uint key, uint[] k);
}

public class ShojoMamaCrypt : KeyDecryptExtra
{
    public ShojoMamaCrypt(uint key, byte[] bin) : base(key, bin) { }

    protected override void InitKey(uint key, uint[] k)
    {
        k[0] = key + 1;
        k[1] = key + 4;
        k[2] = key + 2;
        k[3] = key + 3;
    }
}

public class YuruPlusCrypt : KeyDecryptExtra
{
    public YuruPlusCrypt(uint key, byte[] bin) : base(key, bin)
    {
        EncryptedSize = 0x100;
        PostDataOffset = 0x204;
    }

    protected override void InitKey(uint key, uint[] k)
    {
        k[0] = key + 4;
        k[1] = key + 3;
        k[2] = key + 2;
        k[3] = key + 1;
    }
}

public class TestamentCrypt : KeyDecryptExtra
{
    public TestamentCrypt(uint key, byte[] bin) : base(key, bin) { }

    protected override void InitKey(uint key, uint[] k)
    {
        k[0] = key + 3;
        k[1] = key + 2;
        k[2] = key + 1;
        k[3] = key;
    }
}

public class MakiFesCrypt : KeyDecryptBase
{
    public MakiFesCrypt(uint seed, byte[] key) : base(seed, key) { }

    protected override void DecryptPre(byte[] data, int index, uint length)
    {
        uint k = Seed;
        for (int i = 0; i < 0x100; ++i)
        {
            k = 0x343FD * k + 0x269EC3;
            data[index + i] ^= DecodeTable[((int)(k >> 16) & 0x7FFF) % DecodeTable.Length];
        }
    }
}

/// <summary>
/// XORs the dword at +0x204 with the Adler-32 of the first 0x100 bytes (before unpacking);
/// the post step is the base XOR of +0x200 with Seed. Used by Rikka Plus.
/// </summary>
public class KeyAdlerCrypt : KeyDecryptBase
{
    public KeyAdlerCrypt(uint key) : base(key, Array.Empty<byte>()) { }

    protected override void DecryptPre(byte[] data, int index, uint length)
    {
        uint key = Adler32.Compute(data, index, 0x100);
        data[index + 0x204] ^= (byte)key;
        data[index + 0x205] ^= (byte)(key >> 8);
        data[index + 0x206] ^= (byte)(key >> 16);
        data[index + 0x207] ^= (byte)(key >> 24);
    }
}

public class MajimeCrypt : IDecryptExtra
{
    public void Decrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < 0x200)
            return;
        if ((flags & 0x202) == 0x202)
        {
            int sum = RotateBytesRight(data, index, 0x100);
            data[index + 0x104] ^= (byte)sum;
            data[index + 0x105] ^= (byte)(sum >> 8);
        }
    }

    public void Encrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < 0x200)
            return;
        if ((flags & 0x102) == 0x102)
        {
            int sum = RotateBytesLeft(data, index, 0x100);
            data[index + 0x104] ^= (byte)sum;
            data[index + 0x105] ^= (byte)(sum >> 8);
        }
    }

    internal int RotateBytesRight(byte[] data, int index, int length)
    {
        int sum = 0;
        int bit = 0;
        for (int i = 0; i < length; ++i)
        {
            byte v = data[index + i];
            sum += v >> 1;
            data[index + i] = (byte)(v >> 1 | bit);
            bit = v << 7;
        }
        data[index] |= (byte)bit;
        return sum;
    }

    internal int RotateBytesLeft(byte[] data, int index, int length)
    {
        int sum = 0;
        int bit = 0;
        for (int i = length - 1; i >= 0; --i)
        {
            byte v = data[index + i];
            sum += v & 0x7F;
            data[index + i] = (byte)(v << 1 | bit);
            bit = v >> 7;
        }
        data[index + length - 1] |= (byte)bit;
        return sum;
    }
}

public class NyaruCrypt : MajimeCrypt, IDecryptExtra
{
    new public void Decrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < 0x200)
            return;
        if ((flags & 0x204) == 0x204)
        {
            int sum = RotateBytesRight(data, index, 0x100);
            data[index + 0x100] ^= (byte)sum;
            data[index + 0x101] ^= (byte)(sum >> 8);
        }
    }

    new public void Encrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < 0x200)
            return;
        if ((flags & 0x104) == 0x104)
        {
            int sum = RotateBytesLeft(data, index, 0x100);
            data[index + 0x100] ^= (byte)sum;
            data[index + 0x101] ^= (byte)(sum >> 8);
        }
    }
}

public class JokersCrypt : IDecryptExtra
{
    public void Decrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < 0x400)
            return;
        if ((flags & 0x204) == 0x204)
        {
            if (0x718E958D == LittleEndian.ToUInt32(data, index))
            {
                var input = new byte[0x200];
                Buffer.BlockCopy(data, index, input, 0, 0x200);
                int remaining = LittleEndian.ToInt32(input, 8);
                int src = 12;
                int dst = index;
                var ranges_hi = new uint[0x100];
                var ranges_lo = new uint[0x101];
                for (int i = 0; i < 0x100; ++i)
                {
                    uint v = input[src++];
                    ranges_hi[i] = v;
                    ranges_lo[i + 1] = v + ranges_lo[i];
                }
                uint denominator = ranges_lo[0x100];
                var symbol_table = new byte[denominator];
                uint low, high;
                for (int i = 0; i < 0x100; ++i)
                {
                    low = ranges_lo[i];
                    high = ranges_lo[i + 1];
                    int count = (int)(high - low);
                    for (int j = 0; j < count; ++j)
                        symbol_table[low + j] = (byte)i;
                }
                low = 0;
                high = 0xFFFFFFFF;
                uint current = BigEndian.ToUInt32(input, src);
                src += 4;
                for (int i = 0; i < remaining; ++i)
                {
                    uint range = high / denominator;
                    byte symbol = symbol_table[(current - low) / range];
                    data[index + i] = symbol;
                    low += ranges_lo[symbol] * range;
                    high = ranges_hi[symbol] * range;
                    while (0 == ((low ^ (high + low)) & 0xFF000000u))
                    {
                        low <<= 8;
                        high <<= 8;
                        current <<= 8;
                        current |= input[src++];
                    }
                    while (high < 0x10000)
                    {
                        low <<= 8;
                        high = 0x1000000 - (low & 0xFFFF00);
                        current <<= 8;
                        current |= input[src++];
                    }
                }
            }
            data[index + 0x200] ^= (byte)length;
            data[index + 0x201] ^= (byte)(length >> 8);
            data[index + 0x202] ^= (byte)(length >> 16);
            data[index + 0x203] ^= (byte)(length >> 24);
        }
    }

    public void Encrypt(byte[] data, int index, uint length, uint flags)
    {
        // Not used for extraction
    }
}

public class AlcotCrypt : IDecryptExtra
{
    public void Decrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length >= 0x400 && (flags & 0x204) == 0x204)
            Crc16Crypt(data, index, (int)length);
    }

    public void Encrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length >= 0x400 && (flags & 0x104) == 0x104)
            Crc16Crypt(data, index, (int)length);
    }

    private void Crc16Crypt(byte[] data, int index, int length)
    {
        var crc16 = new Crc16();
        crc16.Update(data, index, length & 0x7E | 1);
        var sum = crc16.Value ^ 0xFFFF;
        data[index + 0x104] ^= (byte)sum;
        data[index + 0x105] ^= (byte)(sum >> 8);
    }
}

public class DodakureCrypt : IDecryptExtra
{
    public void Decrypt(byte[] data, int index, uint length, uint flags)
    {
        if (length < 0x200)
            return;
        if ((flags & 0x204) == 0x204)
        {
            if (0x718E958D == LittleEndian.ToUInt32(data, index))
            {
                var input = new byte[0x200];
                Buffer.BlockCopy(data, index, input, 0, 0x200);
                int remaining = LittleEndian.ToInt32(input, 8);
                int src = 12;
                int dst = index;
                bool rle = false;
                while (remaining > 0)
                {
                    int count = input[src++];
                    if (rle)
                    {
                        byte v = data[dst - 1];
                        for (int i = 0; i < count; ++i)
                            data[dst++] = v;
                    }
                    else
                    {
                        Buffer.BlockCopy(input, src, data, dst, count);
                        src += count;
                        dst += count;
                    }
                    remaining -= count;
                    if (count < 0xFF)
                        rle = !rle;
                }
            }
            if (length > 0x200)
                data[index + 0x200] ^= (byte)length;
            if (length > 0x201)
                data[index + 0x201] ^= (byte)(length >> 8);
            if (length > 0x202)
                data[index + 0x202] ^= (byte)(length >> 16);
            if (length > 0x203)
                data[index + 0x203] ^= (byte)(length >> 24);
        }
    }

    public void Encrypt(byte[] data, int index, uint length, uint flags)
    {
        throw new NotImplementedException();
    }
}
