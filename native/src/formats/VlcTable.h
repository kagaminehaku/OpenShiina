// C# Formats/VlcTable.cs: variable-length codes read from a big-endian bit stream (the VC-1 and WMA
// decoders), looked up a few bits at a time as FFmpeg's get_vlc2 does, and the bit reader they are
// read with.
#pragma once

#include <cstdint>
#include <span>
#include <vector>

#include "io/Binary.h"

namespace openshiina::formats {

/// A big-endian bit reader over an array with at least 8 bytes after its data.
class BitReader
{
public:
    BitReader() = default;
    BitReader(const uint8_t* data, size_t length, int sizeBits, int position = 0) : Data(data), Length(length), Position(position), Size(sizeBits) {}
    BitReader(const Bytes& data, int sizeBits, int position = 0) : BitReader(data.data(), data.size(), sizeBits, position) {}

    /// The array and its length (with the padding).
    const uint8_t* Data = nullptr;
    size_t Length = 0;
    int Position = 0;
    int Size = 0;

    int Left() const { return Size - Position; }

    /// The next n bits (0-32) at a bit position of a padded array, without moving.
    static uint32_t Show(const uint8_t* data, size_t length, int pos, int n)
    {
        int at = pos >> 3;
        if (n == 0 || pos < 0 || size_t(at) + 8 > length)
            return 0;
        uint64_t w = BigEndian::ToUInt64(data + at);
        return uint32_t((w << (pos & 7)) >> (64 - n));
    }

    uint32_t Show(int n) const { return Show(Data, Length, Position, n); }

    int Get(int n)
    {
        uint32_t v = Show(Data, Length, Position, n);
        Position += n;
        return int(v);
    }

    uint32_t GetLong(int n)
    {
        if (n <= 24)
            return uint32_t(Get(n));
        uint32_t hi = uint32_t(Get(n - 16));
        return hi << 16 | uint32_t(Get(16));
    }

    int Bit() { return Get(1); }

    void Skip(int n) { Position += n; }

    void Align() { Position = (Position + 7) & ~7; }
};

/// A table of variable-length codes and their symbols.
class VlcTable
{
public:
    VlcTable() = default;

    /// Codes and lengths per symbol; the symbol is the index unless symbols are given.
    static VlcTable FromCodes(int bits, std::span<const uint32_t> codes, std::span<const uint8_t> lengths, std::span<const int32_t> symbols = {});
    static VlcTable FromCodes(int bits, std::span<const uint16_t> codes, std::span<const uint8_t> lengths);
    /// Code / length pairs, the symbol being the pair's index.
    static VlcTable FromPairs(int bits, std::span<const uint32_t> pairs);
    /// ff_vlc_init_from_lengths: canonical codes given by their lengths in table order, each symbol
    /// plus offset.
    static VlcTable FromLengths(int bits, std::span<const uint8_t> lengths, std::span<const int32_t> symbols, int offset);
    static VlcTable FromLengths(int bits, std::span<const uint8_t> lengths, std::span<const uint16_t> symbols, int offset);

    /// Bits looked up at the first level.
    int Bits = 0;

    /// The symbol of the code at bit pos of data (moved past it), or -1 with no code there.
    int Read(const uint8_t* data, size_t length, int& pos) const
    {
        int bits = Bits, index = 0;
        while (true)
        {
            int i = index + int(BitReader::Show(data, length, pos, bits));
            int codeLength = m_lengths[size_t(i)];
            if (codeLength > 0)
            {
                pos += codeLength;
                return m_symbols[size_t(i)];
            }
            if (codeLength == 0)
                return -1;
            pos += bits;
            index = m_symbols[size_t(i)];
            bits = -codeLength;
        }
    }

    /// The symbol of the code at the reader's position (moved past it), or -1.
    int Read(BitReader& reader) const { return Read(reader.Data, reader.Length, reader.Position); }

private:
    struct Code
    {
        uint32_t Value;
        int Length;
        int Symbol;
    };

    VlcTable(int bits, const std::vector<Code>& codes);
    static int Build(std::vector<int>& symbols, std::vector<int8_t>& lengths, int bits, const std::vector<Code>& codes);

    // Per entry: length > 0 a code of that length and its symbol, 0 no code, < 0 a subtable of
    // -length bits starting at the entry the symbol gives
    std::vector<int> m_symbols;
    std::vector<int8_t> m_lengths;
};

} // namespace openshiina::formats
