#include "archives/EncryptionScheme.h"

#include <cstring>

#include "io/Checksum.h"

namespace openshiina::archives {

namespace {

uint8_t& At(Bytes& d, size_t i)
{
    if (i >= d.size())
        throw InvalidDataError("Index outside the data");
    return d[i];
}

/// Range checked once for the bytes a step reads or writes.
void Need(const Bytes& d, size_t index, size_t count)
{
    if (index + count > d.size())
        throw InvalidDataError("Index outside the data");
}

void XorDword(Bytes& d, size_t at, uint32_t key)
{
    Need(d, at, 4);
    d[at] ^= uint8_t(key);
    d[at + 1] ^= uint8_t(key >> 8);
    d[at + 2] ^= uint8_t(key >> 16);
    d[at + 3] ^= uint8_t(key >> 24);
}

class KeyDecryptBase : public IDecryptExtra
{
public:
    KeyDecryptBase(uint32_t seed, Bytes decodeBin) : Seed(seed), DecodeTable(std::move(decodeBin)) {}

    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < MinLength)
            return;
        if ((flags & 0x202) == 0x202)
            DecryptPre(data, index, length);
        if ((flags & 0x204) == 0x204)
            DecryptPost(data, index, length);
    }

protected:
    virtual void DecryptPre(Bytes& data, size_t index, uint32_t length) = 0;

    virtual void DecryptPost(Bytes& data, size_t index, uint32_t)
    {
        XorDword(data, index + size_t(PostDataOffset), Seed);
    }

    uint32_t Seed;
    Bytes DecodeTable;
    uint32_t MinLength = 0x400;
    int PostDataOffset = 0x200;
};

class KeyDecryptExtra : public KeyDecryptBase
{
public:
    using KeyDecryptBase::KeyDecryptBase;

protected:
    void DecryptPre(Bytes& data, size_t index, uint32_t) override
    {
        if (DecodeTable.empty())
            throw InvalidDataError("Empty DecodeTable");
        uint32_t k[4];
        InitKey(Seed, k);
        Need(data, index, size_t(EncryptedSize));
        for (int i = 0; i < EncryptedSize; ++i)
        {
            uint32_t j = k[3] ^ (k[3] << 11) ^ k[0] ^ ((k[3] ^ (k[3] << 11) ^ (k[0] >> 11)) >> 8);
            k[3] = k[2];
            k[2] = k[1];
            k[1] = k[0];
            k[0] = j;
            data[index + size_t(i)] ^= DecodeTable[j % DecodeTable.size()];
        }
    }

    virtual void InitKey(uint32_t key, uint32_t* k) = 0;

    int EncryptedSize = 0xFF;
};

class ShojoMamaCrypt final : public KeyDecryptExtra
{
public:
    using KeyDecryptExtra::KeyDecryptExtra;

protected:
    void InitKey(uint32_t key, uint32_t* k) override
    {
        k[0] = key + 1;
        k[1] = key + 4;
        k[2] = key + 2;
        k[3] = key + 3;
    }
};

class YuruPlusCrypt final : public KeyDecryptExtra
{
public:
    YuruPlusCrypt(uint32_t key, Bytes bin) : KeyDecryptExtra(key, std::move(bin))
    {
        EncryptedSize = 0x100;
        PostDataOffset = 0x204;
    }

protected:
    void InitKey(uint32_t key, uint32_t* k) override
    {
        k[0] = key + 4;
        k[1] = key + 3;
        k[2] = key + 2;
        k[3] = key + 1;
    }
};

class TestamentCrypt final : public KeyDecryptExtra
{
public:
    using KeyDecryptExtra::KeyDecryptExtra;

protected:
    void InitKey(uint32_t key, uint32_t* k) override
    {
        k[0] = key + 3;
        k[1] = key + 2;
        k[2] = key + 1;
        k[3] = key;
    }
};

class MakiFesCrypt final : public KeyDecryptBase
{
public:
    using KeyDecryptBase::KeyDecryptBase;

protected:
    void DecryptPre(Bytes& data, size_t index, uint32_t) override
    {
        if (DecodeTable.empty())
            throw InvalidDataError("Empty DecodeTable");
        uint32_t k = Seed;
        Need(data, index, 0x100);
        for (int i = 0; i < 0x100; ++i)
        {
            k = 0x343FD * k + 0x269EC3;
            data[index + size_t(i)] ^= DecodeTable[size_t(int32_t(k >> 16) & 0x7FFF) % DecodeTable.size()];
        }
    }
};

/// XORs the dword at +0x204 with the Adler-32 of the first 0x100 bytes (before unpacking); the post
/// step is the base XOR of +0x200 with Seed. Used by Rikka Plus.
class KeyAdlerCrypt final : public KeyDecryptBase
{
public:
    explicit KeyAdlerCrypt(uint32_t key) : KeyDecryptBase(key, {}) {}

protected:
    void DecryptPre(Bytes& data, size_t index, uint32_t) override
    {
        Need(data, index, 0x100);
        XorDword(data, index + 0x204, Adler32::Compute(data.data() + index, 0x100));
    }
};

int RotateBytesRight(Bytes& data, size_t index, int length)
{
    Need(data, index, size_t(length));
    int sum = 0;
    int bit = 0;
    for (int i = 0; i < length; ++i)
    {
        uint8_t v = data[index + size_t(i)];
        sum += v >> 1;
        data[index + size_t(i)] = uint8_t(v >> 1 | bit);
        bit = v << 7;
    }
    data[index] |= uint8_t(bit);
    return sum;
}

class MajimeCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < 0x200)
            return;
        if ((flags & 0x202) == 0x202)
        {
            int sum = RotateBytesRight(data, index, 0x100);
            At(data, index + 0x104) ^= uint8_t(sum);
            At(data, index + 0x105) ^= uint8_t(sum >> 8);
        }
    }
};

class NyaruCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < 0x200)
            return;
        if ((flags & 0x204) == 0x204)
        {
            int sum = RotateBytesRight(data, index, 0x100);
            At(data, index + 0x100) ^= uint8_t(sum);
            At(data, index + 0x101) ^= uint8_t(sum >> 8);
        }
    }
};

class JokersCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < 0x400)
            return;
        if ((flags & 0x204) != 0x204)
            return;
        Need(data, index, 0x200);
        if (0x718E958D == LittleEndian::ToUInt32(data.data() + index))
        {
            Bytes input(data.begin() + ptrdiff_t(index), data.begin() + ptrdiff_t(index) + 0x200);
            int remaining = int32_t(LittleEndian::ToUInt32(input.data() + 8));
            size_t src = 12;
            uint32_t rangesHi[0x100];
            uint32_t rangesLo[0x101] = {};
            for (int i = 0; i < 0x100; ++i)
            {
                uint32_t v = At(input, src++);
                rangesHi[i] = v;
                rangesLo[i + 1] = v + rangesLo[i];
            }
            uint32_t denominator = rangesLo[0x100];
            Bytes symbolTable(denominator);
            uint32_t low, high;
            for (int i = 0; i < 0x100; ++i)
            {
                low = rangesLo[i];
                high = rangesLo[i + 1];
                for (uint32_t j = 0; j < high - low; ++j)
                    symbolTable[low + j] = uint8_t(i);
            }
            low = 0;
            high = 0xFFFFFFFF;
            Need(input, src, 4);
            uint32_t current = BigEndian::ToUInt32(input.data() + src);
            src += 4;
            for (int i = 0; i < remaining; ++i)
            {
                if (denominator == 0)
                    throw InvalidDataError("Division by zero");
                uint32_t range = high / denominator;
                if (range == 0)
                    throw InvalidDataError("Division by zero");
                uint8_t symbol = At(symbolTable, (current - low) / range);
                At(data, index + size_t(i)) = symbol;
                low += rangesLo[symbol] * range;
                high = rangesHi[symbol] * range;
                while (0 == ((low ^ (high + low)) & 0xFF000000u))
                {
                    low <<= 8;
                    high <<= 8;
                    current <<= 8;
                    current |= At(input, src++);
                }
                while (high < 0x10000)
                {
                    low <<= 8;
                    high = 0x1000000 - (low & 0xFFFF00);
                    current <<= 8;
                    current |= At(input, src++);
                }
            }
        }
        XorDword(data, index + 0x200, length);
    }
};

class AlcotCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < 0x400 || (flags & 0x204) != 0x204)
            return;
        size_t count = size_t((length & 0x7E) | 1);
        Need(data, index, count);
        Crc16 crc16;
        crc16.Update(data.data() + index, count);
        int sum = crc16.Value() ^ 0xFFFF;
        At(data, index + 0x104) ^= uint8_t(sum);
        At(data, index + 0x105) ^= uint8_t(sum >> 8);
    }
};

/// The length XORed into the dword at +0x200 as far as the data goes (Dodakure, Binbo).
void XorLengthAt200(Bytes& data, size_t index, uint32_t length)
{
    for (uint32_t i = 0; i < 4; i++)
        if (length > 0x200 + i)
            At(data, index + 0x200 + i) ^= uint8_t(length >> (8 * i));
}

class DodakureCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < 0x200 || (flags & 0x204) != 0x204)
            return;
        Need(data, index, 0x200);
        if (0x718E958D == LittleEndian::ToUInt32(data.data() + index))
        {
            Bytes input(data.begin() + ptrdiff_t(index), data.begin() + ptrdiff_t(index) + 0x200);
            int remaining = int32_t(LittleEndian::ToUInt32(input.data() + 8));
            size_t src = 12;
            size_t dst = index;
            bool rle = false;
            while (remaining > 0)
            {
                int count = At(input, src++);
                if (rle)
                {
                    if (dst == 0)
                        throw InvalidDataError("Index outside the data");
                    uint8_t v = data[dst - 1];
                    for (int i = 0; i < count; ++i)
                        At(data, dst++) = v;
                }
                else
                {
                    Need(input, src, size_t(count));
                    Need(data, dst, size_t(count));
                    std::memcpy(data.data() + dst, input.data() + src, size_t(count));
                    src += size_t(count);
                    dst += size_t(count);
                }
                remaining -= count;
                if (count < 0xFF)
                    rle = !rle;
            }
        }
        XorLengthAt200(data, index, length);
    }
};

/// XORs the dword at +0x200 with the Adler-32 of the first 0xFF bytes.
void AdlerTransform(Bytes& data, size_t index)
{
    Need(data, index, 0xFF);
    XorDword(data, index + 0x200, Adler32::Compute(data.data() + index, 0xFF));
}

/// AdlerCrypt after unpacking (flags 0x204).
class PostAdlerCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length >= 0x400 && (flags & 0x204) == 0x204)
            AdlerTransform(data, index);
    }
};

/// AdlerCrypt before unpacking (flags 0x202).
class PreAdlerCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length >= 0x400 && (flags & 0x202) == 0x202)
            AdlerTransform(data, index);
    }
};

/// Hin wa Bokura no Fuku no Kami: the first 0x200 bytes LZ-packed (signature 0x718E958D), then the
/// length XORed into the dword at +0x200.
class BinboCrypt final : public IDecryptExtra
{
public:
    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < 0x200 || (flags & 0x204) != 0x204)
            return;
        Need(data, index, 0x200);
        if (0x718E958D == LittleEndian::ToUInt32(data.data() + index))
        {
            Bytes input(data.begin() + ptrdiff_t(index), data.begin() + ptrdiff_t(index) + 0x200);
            LzComp(input, 8).Unpack(data, index);
        }
        XorLengthAt200(data, index, length);
    }

private:
    class LzComp
    {
    public:
        LzComp(const Bytes& input, size_t index) : m_input(input), m_src(index) {}

        void Unpack(Bytes& output, size_t dst)
        {
            FillBitCache();
            while (m_src < m_input.size())
            {
                if (GetBit() != 0)
                {
                    uint8_t literal = Take();
                    At(output, dst++) = literal;
                    continue;
                }
                int count, offset;
                if (GetBit() != 0)
                {
                    count = LittleEndian::ToUInt16(m_input, m_src);
                    m_src += 2;
                    offset = count >> 3 | -0x2000;
                    count &= 7;
                    if (count > 0)
                        count += 2;
                    else
                    {
                        count = Take();
                        if (count == 0)
                            break;
                        count += 9;
                    }
                }
                else
                {
                    count = GetBit() << 1;
                    count |= GetBit();
                    count += 2;
                    offset = Take() | -0x100;
                }
                Binary::CopyOverlapped(output, ptrdiff_t(dst) + offset, ptrdiff_t(dst), count);
                dst += size_t(count);
            }
        }

    private:
        uint8_t Take()
        {
            if (m_src >= m_input.size())
                throw InvalidDataError("Index outside the data");
            return m_input[m_src++];
        }

        int GetBit()
        {
            uint32_t v = m_bits >> --m_bitsCount;
            if (m_bitsCount <= 0)
                FillBitCache();
            return int(v & 1);
        }

        void FillBitCache()
        {
            m_bits = LittleEndian::ToUInt32(m_input, m_src);
            m_src += 4;
            m_bitsCount = 32;
        }

        const Bytes& m_input;
        size_t m_src;
        uint32_t m_bits = 0;
        int m_bitsCount = 0;
    };
};

/// The counts of 0x00 and of 0xFF bytes in the first (length and 0x7E) | 1 XORed at +0x100 and
/// +0x104 (from 0x200 bytes); AltCountCrypt: from 0x400 bytes, the two counts the other way round.
class CountCrypt final : public IDecryptExtra
{
public:
    explicit CountCrypt(bool alternative) : m_minLength(alternative ? 0x400 : 0x200), m_swapped(alternative) {}

    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if ((flags & 0x204) != 0x204 || int32_t(length) < m_minLength)
            return;
        size_t count = size_t((length & 0x7E) | 1);
        Need(data, index, count);
        uint8_t count00 = 0, countFF = 0;
        for (size_t i = 0; i < count; ++i)
        {
            if (data[index + i] == 0xFF)
                countFF++;
            else if (data[index + i] == 0)
                count00++;
        }
        At(data, index + 0x100) ^= m_swapped ? countFF : count00;
        At(data, index + 0x104) ^= m_swapped ? count00 : countFF;
    }

private:
    int m_minLength;
    bool m_swapped;
};

/// The first 0x40 dwords XORed with a key (flags 0x204).
class UshimitsuCrypt final : public IDecryptExtra
{
public:
    explicit UshimitsuCrypt(uint32_t key) : m_key(key) {}

    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if ((flags & 0x204) != 0x204 || length < 0x100)
            return;
        Need(data, index, 0x100);
        for (size_t i = 0; i < 0x40; ++i)
            LittleEndian::Xor(data.data() + index + 4 * i, m_key);
    }

private:
    uint32_t m_key;
};

/// The first (length and 0x7E) | 1 bytes XORed with a 0x40-byte key, and the length XORed into the
/// dword at lengthAt (flags 0x202): Nukige Mitai na Shima ni Sunderu... 2 (+0x100); SaiminCrypt,
/// Chou Saiminjutsu Gakuen (+0x104).
class Nukitashi2Crypt final : public IDecryptExtra
{
public:
    Nukitashi2Crypt(Bytes key, size_t lengthAt) : m_key(std::move(key)), m_lengthAt(lengthAt) {}

    void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) override
    {
        if (length < 0x200 || (flags & 0x202) != 0x202)
            return;
        size_t count = size_t((length & 0x7E) | 1);
        Need(data, index, count);
        if (m_key.size() < std::min<size_t>(count, 0x40))
            throw InvalidDataError("Key shorter than the bytes it is for");
        for (size_t i = 0; i < count; i++)
            data[index + i] ^= m_key[i % 0x40];
        XorDword(data, index + m_lengthAt, length);
    }

private:
    Bytes m_key;
    size_t m_lengthAt;
};

} // namespace

ImageArray::ImageArray(std::shared_ptr<const Bytes> common, size_t commonLength, Bytes tail)
    : m_common(std::move(common)), m_commonLength(commonLength), m_tail(std::move(tail))
{
    if (commonLength > m_common->size())
        throw InvalidDataError("CommonLength longer than the common part");
}

std::shared_ptr<EncryptionScheme> EncryptionScheme::Warc110(const std::string& name)
{
    auto scheme = std::make_shared<EncryptionScheme>();
    scheme->Name = name;
    scheme->EntryNameSize = 0x10;
    return scheme;
}

std::shared_ptr<IDecryptExtra> CreateExtraCrypt(const ExtraCryptParameters& p)
{
    const std::string& t = p.Type;
    auto table = [&]() -> const Bytes& {
        if (p.Table.empty()) throw InvalidDataError(t + " has no DecodeTable");
        return p.Table;
    };
    auto keyBytes = [&]() -> const Bytes& {
        if (p.KeyBytes.empty()) throw InvalidDataError(t + " has no Key");
        return p.KeyBytes;
    };
    if (t == "YuruPlusCrypt") return std::make_shared<YuruPlusCrypt>(p.Seed, table());
    if (t == "MakiFesCrypt") return std::make_shared<MakiFesCrypt>(p.Seed, table());
    if (t == "KeyAdlerCrypt") return std::make_shared<KeyAdlerCrypt>(p.Seed);
    if (t == "ShojoMamaCrypt") return std::make_shared<ShojoMamaCrypt>(p.Seed, table());
    if (t == "TestamentCrypt") return std::make_shared<TestamentCrypt>(p.Seed, table());
    if (t == "NyaruCrypt") return std::make_shared<NyaruCrypt>();
    if (t == "MajimeCrypt") return std::make_shared<MajimeCrypt>();
    if (t == "JokersCrypt") return std::make_shared<JokersCrypt>();
    if (t == "AlcotCrypt") return std::make_shared<AlcotCrypt>();
    if (t == "DodakureCrypt") return std::make_shared<DodakureCrypt>();
    if (t == "PostAdlerCrypt") return std::make_shared<PostAdlerCrypt>();
    if (t == "PreAdlerCrypt") return std::make_shared<PreAdlerCrypt>();
    if (t == "BinboCrypt") return std::make_shared<BinboCrypt>();
    if (t == "CountCrypt") return std::make_shared<CountCrypt>(false);
    if (t == "AltCountCrypt") return std::make_shared<CountCrypt>(true);
    if (t == "UshimitsuCrypt") return std::make_shared<UshimitsuCrypt>(p.Key);
    if (t == "Nukitashi2Crypt") return std::make_shared<Nukitashi2Crypt>(keyBytes(), 0x100);
    if (t == "SaiminCrypt") return std::make_shared<Nukitashi2Crypt>(keyBytes(), 0x104);
    throw InvalidDataError("ExtraCrypt type '" + t + "' is not implemented");
}

} // namespace openshiina::archives
