// Archive opener for ShiinaRio WAR/WARC archives
// Ported from GARbro ArcFormats/ShiinaRio/ArcWARC.cs

using System.IO;
using System.IO.Compression;

namespace OpenShiina.Archives;


public class WarcArchive : IDisposable
{
    public ArcView File { get; }
    public List<Entry> Entries { get; }
    public Decoder Decoder { get; }
    public string SchemeName { get; }

    public WarcArchive(ArcView file, List<Entry> entries, Decoder decoder, string schemeName)
    {
        File = file;
        Entries = entries;
        Decoder = decoder;
        SchemeName = schemeName;
    }

    public void Dispose()
    {
        File.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WarcOpener
{
    /// <summary>True if the file carries the ShiinaRio "WARC 1.x" signature (any version).</summary>
    public static bool IsWarc(ArcView file)
    {
        return file.MaxOffset >= 16 && file.View.AsciiEqual(4, " 1.");
    }

    public static WarcArchive? TryOpen(ArcView file, EncryptionScheme? selectedScheme = null)
    {
        if (!IsWarc(file))
            return null;
        int version = file.View.ReadByte(7) - 0x30;
        if (version < 1 || version > 7)
            return null;
        version = 100 + version * 10;
        // Only WARC 1.7 (zlib-compressed index) is supported: every GRAND†CROSS archive uses it.
        if (version != 170)
            throw new NotSupportedException($"WARC 1.{version / 10 % 10} archives are not supported; only WARC 1.7 (GRAND†CROSS games) can be opened.");
        uint index_offset = 0xF182AD82u ^ file.View.ReadUInt32(8);
        if (index_offset >= file.MaxOffset)
            return null;

        EncryptionScheme? scheme = selectedScheme;
        if (scheme == null)
        {
            string? detectedTitle = FormatManager.Instance.LookupGame(file.Name);
            if (!string.IsNullOrEmpty(detectedTitle))
                scheme = FormatManager.Instance.GetScheme(detectedTitle);

            // No silent fallback to a guessed scheme: the index does not depend on the ShiinaImage,
            // so a wrong guess can open fine and then fail on every entry. Let the user pick instead.
        }
        if (scheme == null)
            return null;

        var decoder = new Decoder(version, scheme);
        uint max_index_len = decoder.MaxIndexLength;
        uint index_length = (uint)Math.Min(max_index_len, file.MaxOffset - index_offset);
        if (index_length < 8) return null;

        var enc_index = new byte[max_index_len];
        if (index_length != file.View.Read(index_offset, enc_index, 0, index_length))
            return null;

        decoder.DecryptIndex(index_offset, enc_index);

        if (0x78 != enc_index[8]) // not a zlib stream: wrong scheme
            return null;
        // Inflate fully up front: ZLibStream.Read may return short counts, which the
        // fixed-size record loop below would mistake for the end of the index.
        var zindex = new MemoryStream(enc_index, 8, (int)index_length - 8);
        var index = new MemoryStream();
        using (var zlib = new ZLibStream(zindex, CompressionMode.Decompress))
            zlib.CopyTo(index);
        index.Position = 0;

        using (var header = new BinaryReader(index))
        {
            byte[] name_buf = new byte[decoder.EntryNameSize];
            var dir = new List<Entry>();
            var unique_names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (name_buf.Length == header.Read(name_buf, 0, name_buf.Length))
            {
                var name = Binary.GetCString(name_buf, 0, name_buf.Length);
                var entry = new WarcEntry { Name = name };
                entry.Offset = header.ReadUInt32();
                entry.Size = header.ReadUInt32();
                if (entry.Offset + entry.Size > file.MaxOffset)
                    return null;
                entry.UnpackedSize = header.ReadUInt32();
                entry.IsPacked = entry.Size != entry.UnpackedSize;
                entry.FileTime = header.ReadInt64();
                entry.Flags = header.ReadUInt32();

                // Guess entry type from extension
                entry.Type = GuessType(name);

                if (name.Length != 0 && name_buf[0] < 0x80 && unique_names.Add(name))
                    dir.Add(entry);
            }

            if (dir.Count == 0)
                return null;

            return new WarcArchive(file, dir, decoder, scheme.Name);
        }
    }

    public static byte[] OpenEntry(WarcArchive warc, Entry entry)
    {
        // Both ArcView.Frame (a single remappable window) and Decoder (keeps its PRNG state in
        // a field) are shared per archive, so background extraction and UI preview must not
        // run this stage concurrently.
        byte[] enc_data;
        uint sig = 0, unpacked_size = 0;
        var wentry = entry as WarcEntry;
        lock (warc)
        {
            enc_data = warc.File.View.ReadBytes(entry.Offset, entry.Size);
            if (wentry == null || entry.Size < 8 || enc_data.Length <= 8)
                return enc_data;

            sig = LittleEndian.ToUInt32(enc_data, 0);
            unpacked_size = LittleEndian.ToUInt32(enc_data, 4);

            if (warc.Decoder.WarcVersion > 110)
            {
                sig ^= (unpacked_size ^ 0x82AD82) & 0xFFFFFF;
                if (0 != (wentry.Flags & 0x80000000u))
                    warc.Decoder.Decrypt(enc_data, 8, entry.Size - 8);
                if (warc.Decoder.ExtraCrypt != null)
                    warc.Decoder.ExtraCrypt.Decrypt(enc_data, 8, entry.Size - 8, 0x202);
                if (0 != (wentry.Flags & 0x20000000u))
                    warc.Decoder.Decrypt2(enc_data, 8, entry.Size - 8);
            }
        }

        byte[] unpacked = enc_data;
        Action<byte[], byte[]>? unpack = null;

        switch (sig & 0xffffff)
        {
            case 0x314859: // 'YH1'
                unpack = UnpackYH1;
                break;
            case 0x4b5059: // 'YPK'
                unpack = UnpackYPK;
                break;
            case 0x5a4c59: // 'YLZ'
                unpack = UnpackYLZ;
                break;
        }

        if (unpack != null)
        {
            unpacked = new byte[unpacked_size];
            unpack(enc_data, unpacked);
            if (warc.Decoder.WarcVersion > 110)
            {
                if (0 != (wentry.Flags & 0x40000000))
                    warc.Decoder.Decrypt2(unpacked, 0, (uint)unpacked.Length);
                if (warc.Decoder.ExtraCrypt != null)
                    warc.Decoder.ExtraCrypt.Decrypt(unpacked, 0, (uint)unpacked.Length, 0x204);
            }
        }

        return unpacked;
    }

    private static void UnpackYH1(byte[] input, byte[] output)
    {
        if (0 != input[3])
        {
            uint key = 0x6393528e ^ 0x4b4du;
            unsafe
            {
                fixed (byte* buf_raw = input)
                {
                    uint* encoded = (uint*)buf_raw;
                    for (int i = 2; i < input.Length / 4; ++i)
                        encoded[i] ^= key;
                }
            }
        }
        var decoder = new HuffmanReader(input, 8, input.Length - 8, output);
        decoder.Unpack();
    }

    private static void UnpackYPK(byte[] input, byte[] output)
    {
        if (0 != input[3])
        {
            uint key = ~0x4b4d4b4du;
            unsafe
            {
                fixed (byte* buf_raw = input)
                {
                    uint* encoded = (uint*)buf_raw;
                    int i;
                    for (i = 2; i < input.Length / 4; ++i)
                        encoded[i] ^= key;
                    for (i *= 4; i < input.Length; ++i)
                        buf_raw[i] ^= (byte)key;
                }
            }
        }
        if (0x78 != input[8])
            throw new ApplicationException("Invalid decryption scheme");
        var src = new MemoryStream(input, 8, input.Length - 8);
        using var zlib = new ZLibStream(src, CompressionMode.Decompress);
        int totalRead = 0;
        while (totalRead < output.Length)
        {
            int r = zlib.Read(output, totalRead, output.Length - totalRead);
            if (r == 0) break;
            totalRead += r;
        }
    }

    private static void UnpackYLZ(byte[] input, byte[] output)
    {
        if (0 != input[3])
        {
            uint key = 0x4b4d4b4du;
            unsafe
            {
                fixed (byte* buf_raw = input)
                {
                    uint* encoded = (uint*)buf_raw;
                    int i;
                    for (i = 2; i < input.Length / 4; ++i)
                        encoded[i] ^= key;
                    for (i *= 4; i < input.Length; ++i)
                    {
                        buf_raw[i] ^= (byte)key;
                        key >>= 8;
                    }
                }
            }
        }
        var decoder = new YlzReader(input, 8, output);
        decoder.Unpack();
    }

    private static string GuessType(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".s25" or ".mi4" or ".bmp" or ".png" or ".jpg" or ".jpeg" or ".chd" => "image",
            ".wav" or ".ogg" or ".ogv" or ".pad" or ".mp3" => "audio",
            ".txt" or ".scr" or ".ini" or ".csv" or ".json" or ".lua" => "text",
            _ => "binary"
        };
    }
}

internal class YlzReader
{
    private readonly byte[] m_input;
    private readonly byte[] m_output;
    private int m_src;
    private uint m_ctl = 0;
    private uint m_mask = 0;

    public YlzReader(byte[] input, int src_offset, byte[] output)
    {
        m_input = input;
        m_src = src_offset;
        m_output = output;
    }

    private bool GetCtlBit()
    {
        bool bit = 0 != (m_ctl & m_mask);
        m_mask >>= 1;
        if (0 == m_mask)
        {
            m_ctl = LittleEndian.ToUInt32(m_input, m_src);
            m_src += 4;
            m_mask = 0x80000000;
        }
        return bit;
    }

    private int GetBits(int n)
    {
        int v = 0;
        for (int i = 0; i < n; ++i)
        {
            v <<= 1;
            if (GetCtlBit())
                v |= 1;
        }
        return v;
    }

    public void Unpack()
    {
        GetCtlBit();
        int dst = 0;
        while (dst < m_output.Length)
        {
            if (GetCtlBit())
            {
                m_output[dst++] = m_input[m_src++];
                continue;
            }
            bool next_bit = GetCtlBit();
            int offset = m_input[m_src++] | ~0xffff;
            int ah = 0xff;
            int count;
            if (next_bit)
            {
                if (GetCtlBit())
                {
                    ah = (ah << 1) | GetBits(1);
                }
                else if (GetCtlBit())
                {
                    ah = (ah << 1) | GetBits(1);
                    offset -= 0x200;
                }
                else if (GetCtlBit())
                {
                    ah = (ah << 2) | GetBits(2);
                    offset -= 0x400;
                }
                else if (GetCtlBit())
                {
                    ah = (ah << 3) | GetBits(3);
                    offset -= 0x800;
                }
                else
                {
                    ah = (ah << 4) | GetBits(4);
                    offset -= 0x1000;
                }

                if (GetCtlBit())
                    count = 3;
                else if (GetCtlBit())
                    count = 4;
                else if (GetCtlBit())
                    count = 5 + GetBits(1);
                else if (GetCtlBit())
                    count = 7 + GetBits(2);
                else if (GetCtlBit())
                    count = 0x0b + GetBits(3);
                else
                    count = 0x13 + m_input[m_src++];
            }
            else if (GetCtlBit())
            {
                ah <<= 3;
                ah |= GetBits(3);
                ah = (ah - 1) & 0xff;
                count = 2;
            }
            else if (0xff == (offset & 0xff))
            {
                return;
            }
            else
            {
                count = 2;
            }
            offset += (ah & 0xff) << 8;
            Binary.CopyOverlapped(m_output, dst + offset, dst, count);
            dst += count;
        }
    }
}

internal class HuffmanReader
{
    private readonly byte[] m_src;
    private readonly byte[] m_dst;
    private readonly ushort[,] m_tree = new ushort[2, 511];
    private readonly int m_origin;
    private readonly int m_total;
    private int m_input_pos;
    private int m_remaining;
    private int m_curbits;
    private uint m_cache;
    private ushort m_curindex;

    public HuffmanReader(byte[] src, int index, int length, byte[] dst)
    {
        m_src = src;
        m_dst = dst;
        m_origin = index;
        m_total = length;
    }

    public byte[] Unpack()
    {
        m_input_pos = m_origin;
        m_remaining = m_total;
        m_curbits = 0;
        m_curindex = 256;
        ushort root = CreateTree();
        if (root < 256)
        {
            // A tree of one symbol: every byte is that symbol and reads no bits
            m_dst.AsSpan().Fill((byte)root);
            return m_dst;
        }
        var table = BuildTable(root);
        // The bits that are left, the oldest at bit (bits - 1) of buffer; past the end of the
        // input, zeros stand in for the peek, but taking them is the error it always was
        ulong buffer = m_curbits == 0 ? 0 : m_cache & (uint)((1ul << m_curbits) - 1);
        int bits = m_curbits;
        for (int i = 0; i < m_dst.Length; ++i)
        {
            if (bits < TableBits && m_remaining > 0)
            {
                buffer = (buffer << 32) | ReadUInt32();
                bits += 32;
            }
            uint peek = (uint)(bits >= TableBits ? buffer >> (bits - TableBits) : buffer << (TableBits - bits)) & TableMask;
            uint item = table[peek];
            int length = (int)(item >> 16);
            int node = (int)(item & 0xFFFF);
            if (length > bits)
                throw new InvalidDataException("Unexpected end of file");
            bits -= length;
            while (node >= 256)
            {
                if (bits == 0)
                {
                    if (m_remaining <= 0)
                        throw new InvalidDataException("Unexpected end of file");
                    buffer = ReadUInt32();
                    bits = 32;
                }
                --bits;
                node = m_tree[(int)(buffer >> bits) & 1, node];
            }
            m_dst[i] = (byte)node;
        }
        return m_dst;
    }

    // Codes up to TableBits bits long are decoded by one look-up: (length << 16) | symbol; longer
    // ones give the node after TableBits bits, and the rest is read a bit at a time
    private const int TableBits = 12;
    private const uint TableMask = (1u << TableBits) - 1;

    private uint[] BuildTable(ushort root)
    {
        var table = new uint[1 << TableBits];
        var work = new Stack<(int Node, int Code, int Length)>();
        work.Push((root, 0, 0));
        while (work.Count > 0)
        {
            var (node, code, length) = work.Pop();
            if (node < 256 || length == TableBits)
            {
                int shift = TableBits - length;
                table.AsSpan(code << shift, 1 << shift).Fill((uint)(length << 16 | node));
                continue;
            }
            work.Push((m_tree[0, node], code << 1, length + 1));
            work.Push((m_tree[1, node], code << 1 | 1, length + 1));
        }
        return table;
    }

    private uint ReadUInt32()
    {
        uint v;
        if (m_remaining >= 4)
        {
            v = LittleEndian.ToUInt32(m_src, m_input_pos);
            m_input_pos += 4;
            m_remaining -= 4;
        }
        else if (m_remaining > 0)
        {
            v = m_src[m_input_pos++];
            int shift = 8;
            while (--m_remaining != 0)
            {
                v |= (uint)(m_src[m_input_pos++] << shift);
                shift += 8;
            }
        }
        else
            throw new InvalidDataException("Unexpected end of file");
        return v;
    }

    private uint GetBits(int req_bits)
    {
        uint ret_val = 0;
        if (req_bits > m_curbits)
        {
            req_bits -= m_curbits;
            ret_val |= (m_cache & ((1u << m_curbits) - 1u)) << req_bits;
            m_cache = ReadUInt32();
            m_curbits = 32;
        }
        m_curbits -= req_bits;
        return ret_val | ((1u << req_bits) - 1u) & (m_cache >> m_curbits);
    }

    private ushort CreateTree()
    {
        ushort i;
        if (0 != GetBits(1))
        {
            i = m_curindex++;
            m_tree[0, i] = CreateTree();
            m_tree[1, i] = CreateTree();
        }
        else
            i = (ushort)GetBits(8);
        return i;
    }
}
