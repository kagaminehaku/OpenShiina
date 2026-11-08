// C# Formats/VlcTable.cs
#include "formats/VlcTable.h"

#include <algorithm>

namespace openshiina::formats {

VlcTable::VlcTable(int bits, const std::vector<Code>& codes) : Bits(bits)
{
    Build(m_symbols, m_lengths, bits, codes);
}

VlcTable VlcTable::FromCodes(int bits, std::span<const uint32_t> codes, std::span<const uint8_t> lengths, std::span<const int32_t> symbols)
{
    std::vector<Code> list;
    for (size_t i = 0; i < codes.size(); i++)
        if (lengths[i] > 0)
            list.push_back({codes[i], lengths[i], symbols.empty() ? int(i) : symbols[i]});
    return VlcTable(bits, list);
}

VlcTable VlcTable::FromCodes(int bits, std::span<const uint16_t> codes, std::span<const uint8_t> lengths)
{
    std::vector<uint32_t> wide(codes.begin(), codes.end());
    return FromCodes(bits, wide, lengths);
}

VlcTable VlcTable::FromPairs(int bits, std::span<const uint32_t> pairs)
{
    std::vector<uint32_t> codes(pairs.size() / 2);
    std::vector<uint8_t> lengths(pairs.size() / 2);
    for (size_t i = 0; i < codes.size(); i++)
    {
        codes[i] = pairs[2 * i];
        lengths[i] = uint8_t(pairs[2 * i + 1]);
    }
    return FromCodes(bits, codes, lengths);
}

VlcTable VlcTable::FromLengths(int bits, std::span<const uint8_t> lengths, std::span<const int32_t> symbols, int offset)
{
    std::vector<Code> list;
    uint64_t code = 0;
    for (size_t i = 0; i < lengths.size(); i++)
    {
        int len = lengths[i];
        if (len <= 0)
            continue;
        list.push_back({uint32_t(code >> (32 - len)), len, symbols[i] + offset});
        code += uint64_t(1) << (32 - len);
    }
    return VlcTable(bits, list);
}

VlcTable VlcTable::FromLengths(int bits, std::span<const uint8_t> lengths, std::span<const uint16_t> symbols, int offset)
{
    std::vector<int32_t> wide(symbols.begin(), symbols.end());
    return FromLengths(bits, lengths, wide, offset);
}

int VlcTable::Build(std::vector<int>& symbols, std::vector<int8_t>& lengths, int bits, const std::vector<Code>& codes)
{
    int start = int(symbols.size());
    symbols.resize(symbols.size() + (size_t(1) << bits), 0);
    lengths.resize(lengths.size() + (size_t(1) << bits), 0);
    for (const Code& c : codes)
    {
        if (c.Length > bits)
            continue;
        int first = int(c.Value << (bits - c.Length)), count = 1 << (bits - c.Length);
        for (int i = 0; i < count; i++)
        {
            symbols[size_t(start + first + i)] = c.Symbol;
            lengths[size_t(start + first + i)] = int8_t(c.Length);
        }
    }
    // The longer codes in groups by their first bits, in the order the groups come (as LINQ's GroupBy)
    std::vector<int> keys;
    for (const Code& c : codes)
        if (c.Length > bits)
        {
            int key = int(c.Value >> (c.Length - bits));
            if (std::find(keys.begin(), keys.end(), key) == keys.end())
                keys.push_back(key);
        }
    for (int key : keys)
    {
        std::vector<Code> rest;
        int longest = 0;
        for (const Code& c : codes)
            if (c.Length > bits && int(c.Value >> (c.Length - bits)) == key)
            {
                rest.push_back({c.Value & ((1u << (c.Length - bits)) - 1), c.Length - bits, c.Symbol});
                longest = std::max(longest, c.Length - bits);
            }
        int subBits = std::min(longest, bits);
        int sub = Build(symbols, lengths, subBits, rest);
        symbols[size_t(start + key)] = sub;
        lengths[size_t(start + key)] = int8_t(-subBits);
    }
    return start;
}

} // namespace openshiina::formats
