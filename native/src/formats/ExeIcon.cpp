// C# Formats/ExeIcon.cs (see ExeIcon.h)
#include "formats/ExeIcon.h"

#include <algorithm>

#include "platform/Files.h"

namespace openshiina::formats {

namespace {

constexpr int RT_ICON = 3, RT_GROUP_ICON = 14;

struct Pe
{
    const Bytes& Data;
    int SectionTable = 0, Sections = 0, Root = -1;

    bool In(int64_t at, int64_t size) const { return at >= 0 && size >= 0 && at + size <= int64_t(Data.size()); }
    int U16(int at) const { return In(at, 2) ? Data[size_t(at)] | Data[size_t(at) + 1] << 8 : 0; }
    int I32(int at) const { return In(at, 4) ? int(LittleEndian::ToUInt32(Data.data() + at)) : 0; }

    // RVA -> file offset through the section table
    int Offset(int rva) const
    {
        for (int i = 0; i < Sections; i++)
        {
            int s = SectionTable + 40 * i;
            int virtualAddress = I32(s + 12), virtualSize = std::max(I32(s + 8), I32(s + 16)), raw = I32(s + 20);
            if (rva >= virtualAddress && int64_t(rva) < int64_t(virtualAddress) + virtualSize)
                return rva - virtualAddress + raw;
        }
        return -1;
    }

    struct DirEntry
    {
        int Id;
        bool Directory;
        int At;
    };

    // The entries of a resource directory (named ones, bit 31, have id -1: not looked for)
    std::vector<DirEntry> Entries(int directory) const
    {
        std::vector<DirEntry> entries;
        int named = U16(directory + 12), ids = U16(directory + 14);
        for (int i = 0; i < named + ids && i < 4096; i++)
        {
            int e = directory + 16 + 8 * i;
            int name = I32(e), data = I32(e + 4);
            entries.push_back({name < 0 ? -1 : name, data < 0, Root + (data & 0x7FFFFFFF)});
        }
        return entries;
    }

    // The data of the first language of a resource (type, id: the first one when id < 0)
    std::optional<Bytes> Resource(int type, int id) const
    {
        for (const auto& t : Entries(Root))
        {
            if (t.Id != type || !t.Directory)
                continue;
            for (const auto& n : Entries(t.At))
            {
                if ((id >= 0 && n.Id != id) || !n.Directory)
                    continue;
                for (const auto& l : Entries(n.At))
                {
                    if (l.Directory)
                        continue;
                    int at = Offset(I32(l.At)), size = I32(l.At + 4);
                    if (at < 0 || size <= 0 || !In(at, size))
                        return std::nullopt;
                    return Bytes(Data.begin() + at, Data.begin() + at + size);
                }
            }
        }
        return std::nullopt;
    }
};

} // namespace

std::optional<IconImage> ExeIcon::Read(const std::string& exePath, int wanted)
{
    Bytes pe;
    if (!Files::ReadAll(exePath, pe))
        return std::nullopt;
    return Extract(pe, wanted);
}

std::optional<IconImage> ExeIcon::Extract(const Bytes& data, int wanted)
{
    Pe pe{data};
    if (data.size() < 0x40 || pe.U16(0) != 0x5A4D)
        return std::nullopt;
    int header = pe.I32(0x3C);
    if (header < 0 || !pe.In(header, 24) || pe.I32(header) != 0x00004550)
        return std::nullopt;
    pe.Sections = pe.U16(header + 6);
    int optionalSize = pe.U16(header + 20), optional = header + 24;
    int magic = pe.U16(optional);
    int dataDirectories = magic == 0x10B ? optional + 96 : magic == 0x20B ? optional + 112 : -1;
    if (dataDirectories < 0)
        return std::nullopt;
    int resourceRva = pe.I32(dataDirectories + 2 * 8);
    if (resourceRva == 0)
        return std::nullopt;
    pe.SectionTable = optional + optionalSize;
    pe.Root = pe.Offset(resourceRva);
    if (pe.Root < 0)
        return std::nullopt;

    // GRPICONDIR: reserved, type, count, then 14-byte entries (width, height, colours, reserved,
    // planes, bit count, bytes, id)
    auto group = pe.Resource(RT_GROUP_ICON, -1);
    if (!group || group->size() < 6)
        return std::nullopt;
    struct Choice
    {
        int Size, Bits, Id;
    };
    std::vector<Choice> choices;
    int count = (*group)[4] | (*group)[5] << 8;
    for (int i = 0; i < count && 6 + 14 * size_t(i) + 14 <= group->size(); i++)
    {
        const uint8_t* e = group->data() + 6 + 14 * i;
        choices.push_back({e[0] == 0 ? 256 : e[0], e[6] | e[7] << 8, e[12] | e[13] << 8});
    }
    // At least as large as wanted first, the smallest of those; then the largest; the most colours
    std::sort(choices.begin(), choices.end(), [wanted](const Choice& a, const Choice& b) {
        bool aBig = a.Size >= wanted, bBig = b.Size >= wanted;
        if (aBig != bBig)
            return aBig;
        if (a.Size != b.Size)
            return aBig ? a.Size < b.Size : a.Size > b.Size;
        return a.Bits > b.Bits;
    });
    for (const auto& c : choices)
        if (auto image = pe.Resource(RT_ICON, c.Id))
            if (auto decoded = DecodeBitmap(*image))
                return decoded;
    return std::nullopt;
}

std::optional<IconImage> ExeIcon::DecodeBitmap(const Bytes& image)
{
    // BITMAPINFOHEADER: size, width, height (the XOR image's and the AND mask's), planes, bit count,
    // compression, ..., colours used; a PNG starts with its own signature and is not read here
    if (image.size() < 40 || LittleEndian::ToUInt32(image.data()) < 40)
        return std::nullopt;
    auto i32 = [&](size_t at) { return int32_t(LittleEndian::ToUInt32(image.data() + at)); };
    int headerSize = i32(0), width = i32(4), height = i32(8) / 2;
    int bits = image[14] | image[15] << 8, compression = i32(16), used = i32(32);
    if (width <= 0 || width > 256 || height <= 0 || height > 256 || compression != 0)
        return std::nullopt;
    if (bits != 1 && bits != 4 && bits != 8 && bits != 16 && bits != 24 && bits != 32)
        return std::nullopt;
    size_t palette = size_t(headerSize), colours = bits <= 8 ? size_t(used > 0 ? used : 1 << bits) : 0;
    size_t pixels = palette + colours * 4;
    size_t stride = size_t((width * bits + 31) / 32) * 4, maskStride = size_t((width + 31) / 32) * 4;
    size_t mask = pixels + stride * size_t(height);
    bool hasMask = mask + maskStride * size_t(height) <= image.size();
    if (mask > image.size())
        return std::nullopt;

    IconImage out{width, height, std::vector<uint8_t>(size_t(width) * size_t(height) * 4)};
    bool anyAlpha = false;
    for (int y = 0; y < height; y++)
    {
        // rows bottom-up
        const uint8_t* row = image.data() + pixels + stride * size_t(height - 1 - y);
        uint8_t* dst = out.Rgba.data() + size_t(y) * size_t(width) * 4;
        for (int x = 0; x < width; x++, dst += 4)
        {
            uint8_t r = 0, g = 0, b = 0, a = 255;
            if (bits <= 8)
            {
                int bit = x * bits;
                int index = (row[bit / 8] >> (8 - bits - bit % 8)) & ((1 << bits) - 1);
                if (size_t(index) < colours)
                {
                    const uint8_t* c = image.data() + palette + size_t(index) * 4;
                    b = c[0], g = c[1], r = c[2];
                }
            }
            else if (bits == 16)
            {
                int v = row[x * 2] | row[x * 2 + 1] << 8;
                r = uint8_t((v >> 10 & 31) * 255 / 31), g = uint8_t((v >> 5 & 31) * 255 / 31), b = uint8_t((v & 31) * 255 / 31);
            }
            else
            {
                const uint8_t* p = row + size_t(x) * size_t(bits / 8);
                b = p[0], g = p[1], r = p[2];
                if (bits == 32)
                {
                    a = p[3];
                    anyAlpha = anyAlpha || a != 0;
                }
            }
            dst[0] = r, dst[1] = g, dst[2] = b, dst[3] = a;
        }
    }
    // The AND mask makes pixels clear, where the image has no alpha of its own
    if ((bits != 32 || !anyAlpha) && hasMask)
        for (int y = 0; y < height; y++)
        {
            const uint8_t* row = image.data() + mask + maskStride * size_t(height - 1 - y);
            uint8_t* dst = out.Rgba.data() + size_t(y) * size_t(width) * 4;
            for (int x = 0; x < width; x++)
                dst[size_t(x) * 4 + 3] = (row[x / 8] >> (7 - x % 8) & 1) ? 0 : 255;
        }
    return out;
}

} // namespace openshiina::formats
