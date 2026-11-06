#include "archives/WarcOpener.h"

#include <algorithm>
#include <cstring>
#include <unordered_set>

#include <miniz.h>

#include "archives/FormatManager.h"
#include "archives/WarcRangeDecoder.h"
#include "platform/Files.h"
#include "text/Text.h"

namespace openshiina::archives {

namespace {

uint8_t At(const Bytes& d, size_t i)
{
    if (i >= d.size())
        throw InvalidDataError("Unexpected end of data");
    return d[i];
}

/// A zlib stream (header, deflate data) inflated; as much as comes out when stopAt is reached or
/// the stream ends.
Bytes Inflate(const uint8_t* input, size_t length, size_t stopAt = SIZE_MAX)
{
    mz_stream stream{};
    if (mz_inflateInit(&stream) != MZ_OK)
        throw InvalidDataError("zlib cannot start");
    Bytes out;
    uint8_t chunk[65536];
    stream.next_in = input;
    stream.avail_in = unsigned(length);
    int status = MZ_OK;
    while (status == MZ_OK && out.size() < stopAt)
    {
        // No more than is wanted: the C# player reads the stream only that far
        size_t room = std::min(sizeof chunk, stopAt - out.size());
        stream.next_out = chunk;
        stream.avail_out = unsigned(room);
        status = mz_inflate(&stream, MZ_NO_FLUSH);
        out.insert(out.end(), chunk, chunk + (room - stream.avail_out));
        if (status == MZ_BUF_ERROR && stream.avail_in == 0)
            break;   // the input ends before the stream does: what came out is all there is
    }
    mz_inflateEnd(&stream);
    if (status != MZ_OK && status != MZ_STREAM_END && status != MZ_BUF_ERROR)
        throw InvalidDataError("Invalid zlib data");
    return out;
}

class YlzReader
{
public:
    YlzReader(const Bytes& input, size_t srcOffset, Bytes& output) : m_input(input), m_src(srcOffset), m_output(output) {}

    void Unpack()
    {
        GetCtlBit();
        size_t dst = 0;
        while (dst < m_output.size())
        {
            if (GetCtlBit())
            {
                m_output[dst++] = Take();
                continue;
            }
            bool nextBit = GetCtlBit();
            int offset = Take() | ~0xffff;
            int ah = 0xff;
            int count;
            if (nextBit)
            {
                if (GetCtlBit())
                    ah = (ah << 1) | GetBits(1);
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
                    count = 0x13 + Take();
            }
            else if (GetCtlBit())
            {
                ah <<= 3;
                ah |= GetBits(3);
                ah = (ah - 1) & 0xff;
                count = 2;
            }
            else if (0xff == (offset & 0xff))
                return;
            else
                count = 2;
            offset += (ah & 0xff) << 8;
            Binary::CopyOverlapped(m_output, ptrdiff_t(dst) + offset, ptrdiff_t(dst), count);
            dst += size_t(count);
        }
    }

private:
    uint8_t Take() { return At(m_input, m_src++); }

    bool GetCtlBit()
    {
        bool bit = 0 != (m_ctl & m_mask);
        m_mask >>= 1;
        if (0 == m_mask)
        {
            m_ctl = LittleEndian::ToUInt32(m_input, m_src);
            m_src += 4;
            m_mask = 0x80000000;
        }
        return bit;
    }

    int GetBits(int n)
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

    const Bytes& m_input;
    size_t m_src;
    Bytes& m_output;
    uint32_t m_ctl = 0;
    uint32_t m_mask = 0;
};

/// WARC 1.0's LZ (GARbro's Ylz16Reader): the input XORed with E6, 16-bit control words.
class Ylz16Reader
{
public:
    explicit Ylz16Reader(Bytes input) : m_input(std::move(input))
    {
        for (uint8_t& b : m_input)
            b ^= 0xE6;
    }

    Bytes Unpack(int unpackedSize)
    {
        if (unpackedSize < 0)
            throw InvalidDataError("Negative unpacked size");
        m_src = 0;
        m_bitCount = 0;
        GetCtlBit();
        Bytes output(static_cast<size_t>(unpackedSize));
        size_t dst = 0;
        while (dst < output.size())
        {
            if (GetCtlBit() != 0)
            {
                output[dst++] = Take();
                continue;
            }
            int offset, count;
            if (GetCtlBit() == 0)
            {
                count = GetCtlBit() << 1;
                count |= GetCtlBit();
                count += 2;
                offset = Take() | -0x100;
            }
            else
            {
                uint8_t lo = Take();
                uint8_t hi = Take();
                offset = lo | (hi & ~7) << 5 | -0x2000;
                count = hi & 7;
                if (count == 0)
                {
                    count = Take();
                    if (count == 0)
                        break;
                    count += 9;
                }
                else
                    count += 2;
            }
            Binary::CopyOverlapped(output, ptrdiff_t(dst) + offset, ptrdiff_t(dst), count);
            dst += size_t(count);
        }
        return output;
    }

private:
    uint8_t Take() { return At(m_input, m_src++); }

    int GetCtlBit()
    {
        int bit = m_ctl & 1;
        m_ctl >>= 1;
        if (--m_bitCount <= 0)
        {
            m_ctl = LittleEndian::ToUInt16(m_input, m_src);
            m_src += 2;
            m_bitCount = 16;
        }
        return bit;
    }

    Bytes m_input;
    int m_ctl = 0;
    int m_bitCount = 0;
    size_t m_src = 0;
};

class HuffmanReader
{
public:
    HuffmanReader(const Bytes& src, size_t index, size_t length, Bytes& dst)
        : m_src(src), m_dst(dst), m_origin(index), m_total(length)
    {
    }

    void Unpack()
    {
        m_inputPos = m_origin;
        m_remaining = int64_t(m_total);
        m_curbits = 0;
        m_curindex = 256;
        uint16_t root = CreateTree();
        if (root < 256)
        {
            // A tree of one symbol: every byte is that symbol and reads no bits
            std::fill(m_dst.begin(), m_dst.end(), uint8_t(root));
            return;
        }
        auto table = BuildTable(root);
        // The bits that are left, the oldest at bit (bits - 1) of buffer; past the end of the
        // input, zeros stand in for the peek, but taking them is the error it always was
        uint64_t buffer = m_curbits == 0 ? 0 : m_cache & uint32_t((1ull << m_curbits) - 1);
        int bits = m_curbits;
        for (size_t i = 0; i < m_dst.size(); ++i)
        {
            if (bits < TableBits && m_remaining > 0)
            {
                buffer = (buffer << 32) | ReadUInt32();
                bits += 32;
            }
            uint32_t peek = uint32_t(bits >= TableBits ? buffer >> (bits - TableBits) : buffer << (TableBits - bits)) & TableMask;
            uint32_t item = table[peek];
            int length = int(item >> 16);
            int node = int(item & 0xFFFF);
            if (length > bits)
                throw InvalidDataError("Unexpected end of file");
            bits -= length;
            while (node >= 256)
            {
                if (bits == 0)
                {
                    if (m_remaining <= 0)
                        throw InvalidDataError("Unexpected end of file");
                    buffer = ReadUInt32();
                    bits = 32;
                }
                --bits;
                node = Tree(int(buffer >> bits) & 1, node);
            }
            m_dst[i] = uint8_t(node);
        }
    }

private:
    // Codes up to TableBits bits long are decoded by one look-up: (length << 16) | symbol; longer
    // ones give the node after TableBits bits, and the rest is read a bit at a time
    static constexpr int TableBits = 12;
    static constexpr uint32_t TableMask = (1u << TableBits) - 1;

    uint16_t& Tree(int side, int node)
    {
        if (node < 0 || node >= 511)
            throw InvalidDataError("Huffman tree node out of range");
        return m_tree[side][node];
    }

    std::vector<uint32_t> BuildTable(uint16_t root)
    {
        std::vector<uint32_t> table(1u << TableBits);
        struct Work { int Node, Code, Length; };
        std::vector<Work> work;
        work.push_back({root, 0, 0});
        while (!work.empty())
        {
            Work w = work.back();
            work.pop_back();
            if (w.Node < 256 || w.Length == TableBits)
            {
                int shift = TableBits - w.Length;
                std::fill_n(table.begin() + (ptrdiff_t(w.Code) << shift), size_t(1) << shift, uint32_t(w.Length << 16 | w.Node));
                continue;
            }
            work.push_back({Tree(0, w.Node), w.Code << 1, w.Length + 1});
            work.push_back({Tree(1, w.Node), w.Code << 1 | 1, w.Length + 1});
        }
        return table;
    }

    uint32_t ReadUInt32()
    {
        uint32_t v;
        if (m_remaining >= 4)
        {
            v = LittleEndian::ToUInt32(m_src, m_inputPos);
            m_inputPos += 4;
            m_remaining -= 4;
        }
        else if (m_remaining > 0)
        {
            v = At(m_src, m_inputPos++);
            int shift = 8;
            while (--m_remaining != 0)
            {
                v |= uint32_t(At(m_src, m_inputPos++)) << shift;
                shift += 8;
            }
        }
        else
            throw InvalidDataError("Unexpected end of file");
        return v;
    }

    uint32_t GetBits(int reqBits)
    {
        uint32_t retVal = 0;
        if (reqBits > m_curbits)
        {
            reqBits -= m_curbits;
            retVal |= (m_cache & ((1u << m_curbits) - 1u)) << reqBits;
            m_cache = ReadUInt32();
            m_curbits = 32;
        }
        m_curbits -= reqBits;
        return retVal | (((1u << reqBits) - 1u) & (m_cache >> m_curbits));
    }

    uint16_t CreateTree()
    {
        uint16_t i;
        if (0 != GetBits(1))
        {
            i = m_curindex++;
            // More than 511 nodes is the index error of the C# player; told before going deeper,
            // so that bad input cannot use up the stack (small on the Switch)
            if (i >= 511)
                throw InvalidDataError("Huffman tree node out of range");
            uint16_t left = CreateTree();
            Tree(0, i) = left;
            uint16_t right = CreateTree();
            Tree(1, i) = right;
        }
        else
            i = uint16_t(GetBits(8));
        return i;
    }

    const Bytes& m_src;
    Bytes& m_dst;
    uint16_t m_tree[2][511] = {};
    size_t m_origin;
    size_t m_total;
    size_t m_inputPos = 0;
    int64_t m_remaining = 0;
    int m_curbits = 0;
    uint32_t m_cache = 0;
    uint16_t m_curindex = 0;
};

void UnpackYH1(Bytes& input, Bytes& output)
{
    if (0 != At(input, 3))
    {
        uint32_t key = 0x6393528e ^ 0x4b4du;
        for (size_t i = 2; i < input.size() / 4; ++i)
            LittleEndian::Xor(input.data() + 4 * i, key);
    }
    HuffmanReader(input, 8, input.size() - 8, output).Unpack();
}

void UnpackYPK(Bytes& input, Bytes& output)
{
    if (0 != At(input, 3))
    {
        uint32_t key = ~0x4b4d4b4du;
        size_t i;
        for (i = 2; i < input.size() / 4; ++i)
            LittleEndian::Xor(input.data() + 4 * i, key);
        for (i *= 4; i < input.size(); ++i)
            input[i] ^= uint8_t(key);
    }
    if (0x78 != At(input, 8))
        throw InvalidDataError("Invalid decryption scheme");
    Bytes data = Inflate(input.data() + 8, input.size() - 8, output.size());
    std::copy(data.begin(), data.end(), output.begin());
}

void UnpackYLZ(Bytes& input, Bytes& output)
{
    if (0 != At(input, 3))
    {
        uint32_t key = 0x4b4d4b4du;
        size_t i;
        for (i = 2; i < input.size() / 4; ++i)
            LittleEndian::Xor(input.data() + 4 * i, key);
        for (i *= 4; i < input.size(); ++i)
        {
            input[i] ^= uint8_t(key);
            key >>= 8;
        }
    }
    YlzReader(input, 8, output).Unpack();
}

std::string GuessType(const std::string& name)
{
    std::string ext = Text::FoldCase(Files::Extension(name));
    for (const char* e : {".S25", ".MI4", ".BMP", ".PNG", ".JPG", ".JPEG", ".CHD"})
        if (ext == e) return "image";
    for (const char* e : {".WAV", ".OGG", ".OGV", ".PAD", ".MP3"})
        if (ext == e) return "audio";
    for (const char* e : {".TXT", ".SCR", ".INI", ".CSV", ".JSON", ".LUA"})
        if (ext == e) return "text";
    return "binary";
}

} // namespace

bool WarcOpener::IsWarc(ArcView& file)
{
    return file.MaxOffset() >= 16 && file.AsciiEqual(4, " 1.");
}

std::optional<int> WarcOpener::ArchiveVersion(const std::string& path)
{
    std::FILE* file = Files::Open(path, "rb");
    if (!file)
        return std::nullopt;
    uint8_t head[8];
    size_t got = std::fread(head, 1, 8, file);
    std::fclose(file);
    if (got != 8 || std::memcmp(head, "WARC 1.", 7) != 0)
        return std::nullopt;
    int minor = head[7] - '0';
    if (minor >= 0 && minor <= 7)
        return 100 + minor * 10;
    return std::nullopt;
}

std::unique_ptr<WarcArchive> WarcOpener::TryOpen(std::unique_ptr<ArcView> file, std::shared_ptr<const EncryptionScheme> selectedScheme)
{
    if (!IsWarc(*file))
        return nullptr;
    int version = file->ReadByte(7) - 0x30;
    if (version < 0 || version > 7)
        return nullptr;
    version = 100 + version * 10;
    if (version == 100)
        return TryOpenForest(std::move(file), selectedScheme ? selectedScheme->Name : std::string());
    uint32_t indexOffset = 0xF182AD82u ^ file->ReadUInt32(8);
    if (indexOffset >= file->MaxOffset())
        return nullptr;

    std::shared_ptr<const EncryptionScheme> scheme =
        version == 110 ? EncryptionScheme::Warc110(selectedScheme ? selectedScheme->Name : std::string()) : selectedScheme;
    if (!scheme)
    {
        // No silent fallback to a guessed scheme: the index does not depend on the ShiinaImage, so
        // a wrong guess can open fine and then fail on every entry. Let the user pick instead.
        if (auto* manager = FormatManager::Instance())
            if (auto title = manager->LookupGame(file->Name()); title && !title->empty())
                scheme = manager->GetScheme(*title);
    }
    if (!scheme)
        return nullptr;

    auto decoder = std::make_unique<Decoder>(version, scheme);
    uint32_t maxIndexLen = decoder->MaxIndexLength();
    uint32_t indexLength = uint32_t(std::min<int64_t>(maxIndexLen, file->MaxOffset() - indexOffset));
    if (indexLength < 8)
        return nullptr;

    Bytes encIndex(maxIndexLen);
    if (indexLength != file->Read(indexOffset, encIndex.data(), indexLength))
        return nullptr;

    decoder->DecryptIndex(indexOffset, encIndex);

    Bytes index;
    if (version == 110)
        index.assign(encIndex.begin(), encIndex.begin() + indexLength);
    else if (version < 170)
    {
        Bytes unpacked(maxIndexLen);
        size_t length = WarcRangeDecoder::Decode(encIndex.data(), indexLength, unpacked.data(), unpacked.size());
        if (length == 0)
            return nullptr;
        unpacked.resize(length);
        index = std::move(unpacked);
    }
    else
    {
        if (0x78 != encIndex[8])   // not a zlib stream: wrong scheme
            return nullptr;
        index = Inflate(encIndex.data() + 8, indexLength - 8);
    }

    const size_t nameSize = size_t(decoder->EntryNameSize());
    const size_t recordSize = nameSize + 24;
    std::vector<Entry> dir;
    std::unordered_set<std::string> uniqueNames;
    for (size_t pos = 0; pos + nameSize <= index.size(); pos += recordSize)
    {
        // A record cut short after its name: the C# player's BinaryReader throws there
        if (pos + recordSize > index.size())
            throw InvalidDataError("Unexpected end of the index");
        const uint8_t* record = index.data() + pos;
        Entry entry;
        entry.Name = Text::CString(record, nameSize);
        entry.IsWarc = true;
        entry.Offset = LittleEndian::ToUInt32(record + nameSize);
        entry.Size = LittleEndian::ToUInt32(record + nameSize + 4);
        if (entry.Offset + entry.Size > file->MaxOffset())
            return nullptr;
        entry.UnpackedSize = LittleEndian::ToUInt32(record + nameSize + 8);
        entry.IsPacked = entry.Size != entry.UnpackedSize;
        entry.FileTime = int64_t(LittleEndian::ToUInt64(record + nameSize + 12));
        entry.Flags = LittleEndian::ToUInt32(record + nameSize + 20);

        // Guess entry type from extension
        entry.Type = GuessType(entry.Name);

        if (!entry.Name.empty() && record[0] < 0x80 && uniqueNames.insert(Text::FoldCase(entry.Name)).second)
            dir.push_back(std::move(entry));
    }

    if (dir.empty())
        return nullptr;

    std::string schemeName = scheme->Name;
    return std::make_unique<WarcArchive>(std::move(file), std::move(dir), std::move(decoder), schemeName);
}

/// WARC 1.0 (GARbro's War0Opener): the index at the dword at 8, up to 0xC000 bytes XORed with FE
/// E5, records of a 16-byte name, offset and size.
std::unique_ptr<WarcArchive> WarcOpener::TryOpenForest(std::unique_ptr<ArcView> file, const std::string& name)
{
    uint32_t indexOffset = file->ReadUInt32(8);
    if (indexOffset >= file->MaxOffset())
        return nullptr;
    Bytes index = file->ReadBytes(indexOffset, uint32_t(std::min<int64_t>(0xC000, file->MaxOffset() - indexOffset)));
    size_t count = index.size() / 0x18;
    if (count == 0 || count > 0x800)
        return nullptr;
    for (size_t i = 0; i + 1 < index.size(); i += 2)
    {
        index[i] ^= 0xFE;
        index[i + 1] ^= 0xE5;
    }
    std::vector<Entry> dir;
    dir.reserve(count);
    std::unordered_set<std::string> uniqueNames;
    for (size_t i = 0, pos = 0; i < count; ++i, pos += 0x18)
    {
        Entry entry;
        entry.Name = Text::CString(index.data() + pos, 0x10);
        entry.Offset = LittleEndian::ToUInt32(index.data() + pos + 0x10);
        entry.Size = LittleEndian::ToUInt32(index.data() + pos + 0x14);
        if (!entry.CheckPlacement(file->MaxOffset()))
            return nullptr;
        entry.UnpackedSize = entry.Size;
        entry.Type = GuessType(entry.Name);
        if (!entry.Name.empty() && uniqueNames.insert(Text::FoldCase(entry.Name)).second)
            dir.push_back(std::move(entry));
    }
    if (dir.empty())
        return nullptr;
    auto decoder = std::make_unique<Decoder>(100, EncryptionScheme::Warc110(name));
    return std::make_unique<WarcArchive>(std::move(file), std::move(dir), std::move(decoder), name);
}

Bytes WarcOpener::OpenEntry(WarcArchive& warc, const Entry& entry)
{
    if (warc.m_decoder->WarcVersion() == 100)
        return OpenForestEntry(warc, entry);

    Bytes encData;
    uint32_t sig = 0, unpackedSize = 0;
    {
        std::lock_guard<std::mutex> lock(warc.m_lock);
        encData = warc.File->ReadBytes(entry.Offset, entry.Size);
        if (!entry.IsWarc || entry.Size < 8 || encData.size() <= 8)
            return encData;

        sig = LittleEndian::ToUInt32(encData.data());
        unpackedSize = LittleEndian::ToUInt32(encData.data() + 4);

        Decoder& decoder = *warc.m_decoder;
        if (decoder.WarcVersion() > 110)
        {
            sig ^= (unpackedSize ^ 0x82AD82) & 0xFFFFFF;
            if (0 != (entry.Flags & 0x80000000u))
                decoder.Decrypt(encData, 8, entry.Size - 8);
            if (decoder.ExtraCrypt())
                decoder.ExtraCrypt()->Decrypt(encData, 8, entry.Size - 8, 0x202);
            if (0 != (entry.Flags & 0x20000000u))
                decoder.Decrypt2(encData, 8, entry.Size - 8);
        }
    }

    void (*unpack)(Bytes&, Bytes&) = nullptr;
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

    if (!unpack)
        return encData;

    Bytes unpacked(unpackedSize);
    unpack(encData, unpacked);
    Decoder& decoder = *warc.m_decoder;
    if (decoder.WarcVersion() > 110)
    {
        if (0 != (entry.Flags & 0x40000000))
            decoder.Decrypt2(unpacked, 0, uint32_t(unpacked.size()));
        if (decoder.ExtraCrypt())
            decoder.ExtraCrypt()->Decrypt(unpacked, 0, uint32_t(unpacked.size()), 0x204);
    }
    return unpacked;
}

/// A WARC 1.0 entry: as it is, or "Ylz" + its unpacked size + Ylz16 data.
Bytes WarcOpener::OpenForestEntry(WarcArchive& warc, const Entry& entry)
{
    Bytes data;
    {
        std::lock_guard<std::mutex> lock(warc.m_lock);
        data = warc.File->ReadBytes(entry.Offset, entry.Size);
    }
    if (data.size() < 8 || data[0] != 'Y' || data[1] != 'l' || data[2] != 'z')
        return data;
    int unpacked = int32_t(LittleEndian::ToUInt32(data.data() + 4));
    return Ylz16Reader(Bytes(data.begin() + 8, data.end())).Unpack(unpacked);
}

} // namespace openshiina::archives
