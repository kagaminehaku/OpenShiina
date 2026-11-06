#include "archives/Decoder.h"

#include <array>
#include <bit>
#include <cmath>

#include "io/Checksum.h"

namespace openshiina::archives {

namespace {

constexpr double kPi = 3.14159265358979323846;

std::array<uint32_t, 256> MakeCustomCrcTable()
{
    std::array<uint32_t, 256> table{};
    for (uint32_t i = 0; i != 256; ++i)
    {
        uint32_t poly = i;
        for (int j = 0; j < 8; ++j)
        {
            uint32_t bit = poly & 1;
            poly = Binary::RotR(poly, 1);
            if (0 == bit)
                poly ^= 0x6DB88320;
        }
        table[i] = poly;
    }
    return table;
}

const std::array<uint32_t, 256> kCustomCrcTable = MakeCustomCrcTable();

struct SystemTime
{
    uint16_t Year = 0, Month = 0, DayOfWeek = 0, Day = 0, Hour = 0, Minute = 0, Second = 0, Milliseconds = 0;
};

/// FileTimeToSystemTime, worked out here so that it runs everywhere: 100 ns ticks since 1601-01-01
/// to the proleptic Gregorian date and time, the milliseconds truncated. FILETIME goes on to 30827,
/// and the keys are made from any 63-bit value.
SystemTime FileTimeToSystemTime(uint32_t low, uint32_t high)
{
    SystemTime t;
    uint64_t ticks = uint64_t(high) << 32 | low;
    if (ticks >= 0x8000000000000000ull)
        return t;   // FileTimeToSystemTime fails and leaves nothing
    uint64_t ms = ticks / 10000;
    t.Milliseconds = uint16_t(ms % 1000);
    uint64_t seconds = ms / 1000;
    t.Second = uint16_t(seconds % 60);
    t.Minute = uint16_t(seconds / 60 % 60);
    t.Hour = uint16_t(seconds / 3600 % 24);
    int64_t days = int64_t(seconds / 86400);
    t.DayOfWeek = uint16_t((days + 1) % 7);     // 1601-01-01 was a Monday
    // Days since 0000-03-01 in 400-year eras (Howard Hinnant's civil_from_days)
    int64_t z = days + 584694;                  // 1601-01-01 is day 584694 after 0000-03-01
    int64_t era = z / 146097;
    int64_t doe = z - era * 146097;
    int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    int64_t mp = (5 * doy + 2) / 153;
    int64_t d = doy - (153 * mp + 2) / 5 + 1;
    int64_t m = mp < 10 ? mp + 3 : mp - 9;
    int64_t y = yoe + era * 400 + (m <= 2 ? 1 : 0);
    t.Year = uint16_t(y);
    t.Month = uint16_t(m);
    t.Day = uint16_t(d);
    return t;
}

} // namespace

Decoder::Decoder(int version, std::shared_ptr<const EncryptionScheme> scheme)
    : m_scheme(std::move(scheme)), m_warcVersion(version)
{
    m_maxIndexLength = GetMaxIndexLength(version);
}

void Decoder::DecryptIndex(uint32_t indexOffset, Bytes& index)
{
    Decrypt(index, 0, uint32_t(index.size()));
    XorIndex(indexOffset, index);
}

void Decoder::DecryptContent(int x, Bytes& data, size_t index, uint32_t length)
{
    const Bytes& key = *m_scheme->CryptKey;
    if (key.empty())
        return;
    if (index + length > data.size())
        throw InvalidDataError("Index outside the data");
    size_t n = 0;
    for (uint32_t i = 2; i < length; ++i)
    {
        uint8_t d = data[index + i];
        if (m_warcVersion > 120)
            d ^= SaturateToByte(double(NextRand()) / 16777216.0);
        d = Binary::RotByteR(d, 1);
        d ^= uint8_t(key[n++] ^ key[size_t(x)]);
        data[index + i] = d;
        x = d % int(key.size());
        if (n >= key.size())
            n = 0;
    }
}

void Decoder::Decrypt(Bytes& data, size_t index, uint32_t dataLength)
{
    if (dataLength < 3 || m_warcVersion < 120 || !m_scheme->CryptKey)
        return;
    // An empty key divides by zero below: the DivideByZeroException of the C# player
    if (m_scheme->CryptKey->empty())
        throw InvalidDataError("Empty CryptKey");
    if (index + 2 > data.size())
        throw InvalidDataError("Index outside the data");
    uint32_t effectiveLength = std::min(dataLength, 1024u);
    int a, b;
    uint32_t fac = 0;
    m_rand = dataLength;
    if (m_warcVersion > 120)
    {
        a = int8_t(data[index]) ^ int8_t(dataLength);
        b = int8_t(data[index + 1]) ^ int8_t(dataLength / 2);
        if (dataLength != m_maxIndexLength && (m_warcVersion > 130 || m_scheme->Version > 2150))
        {
            const ImageArray* image = m_scheme->ShiinaImage.get();
            if (image && image->Length() > 0)
            {
                int idx = SaturateToInt32(double(NextRand()) * (double(image->Length()) / 4294967296.0));
                if (m_warcVersion >= 160)
                {
                    fac = m_rand + (*image)[size_t(idx)];
                    fac = DecryptHelper3(fac) & 0xfffffff;
                    if (effectiveLength > 0x80 && SchemeVersion() > 2350 && m_scheme->HelperKey)
                    {
                        DecryptHelper4(data, index + 4, *m_scheme->HelperKey);
                        index += 0x80;
                        effectiveLength -= 0x80;
                    }
                }
                else if (150 == m_warcVersion)
                {
                    fac = m_rand + (*image)[size_t(idx)];
                    fac ^= (fac & 0xfff) * (fac & 0xfff);
                    uint32_t v = 0;
                    for (int i = 0; i < 32; ++i)
                    {
                        uint32_t bit = fac & 1;
                        fac >>= 1;
                        if (0 != bit)
                            v += fac;
                    }
                    fac = v;
                }
                else if (140 == m_warcVersion)
                    fac = (*image)[size_t(idx)];
                else if (130 == m_warcVersion)
                    fac = (*image)[size_t(idx & 0xff)];
            }
        }
    }
    else
    {
        a = data[index];
        b = data[index + 1];
    }
    // DecryptHelper1 is negative for negative 'a': through a 64-bit integer, which keeps the wrap
    m_rand ^= uint32_t(SaturateToInt64(DecryptHelper1(a) * 100000000.0));

    double token = 0.0;
    if (0 != (a | b))
    {
        token = std::acos(double(a) / std::sqrt(double(a * a + b * b)));
        token = token / kPi * 180.0;
    }
    if (b < 0)
        token = 360.0 - token;

    int x = int((fac + uint8_t(DecryptHelper2(token))) % uint32_t(m_scheme->CryptKey->size()));
    DecryptContent(x, data, index, effectiveLength);
}

void Decoder::XorIndex(uint32_t indexOffset, Bytes& index)
{
    for (size_t i = 0; i + 4 <= index.size(); i += 4)
        LittleEndian::Xor(index.data() + i, indexOffset);
    if (m_warcVersion >= 170)
    {
        uint8_t key = uint8_t(~m_warcVersion);
        for (uint8_t& b : index)
            b ^= key;
    }
}

void Decoder::Decrypt2(Bytes& data, size_t index, uint32_t length)
{
    if (length < 0x400 || !m_scheme->DecodeBin)
        return;
    const Bytes& bin = *m_scheme->DecodeBin;
    if (index + 0x100 + 0x100 + 0x100 > data.size())
        throw InvalidDataError("Index outside the data");
    uint32_t crc = Crc32Normal::UpdateCrc(0xFFFFFFFF, data.data() + index, 0x100);
    index += 0x100;
    for (int i = 0; i < 0x40; ++i)
    {
        uint32_t src = LittleEndian::ToUInt32(data.data() + index) & 0x1ffcu;
        src = LittleEndian::ToUInt32(bin, src);
        uint32_t key = src ^ crc;
        data[index++ + 0x100] ^= uint8_t(key);
        data[index++ + 0x100] ^= uint8_t(key >> 8);
        data[index++ + 0x100] ^= uint8_t(key >> 16);
        data[index++ + 0x100] ^= uint8_t(key >> 24);
    }
}

double Decoder::DecryptHelper1(double a)
{
    if (a < 0)
        return -DecryptHelper1(-a);

    double v0;
    double v1;
    if (a < 18.0)
    {
        v0 = a;
        v1 = a;
        double v2 = -(a * a);

        for (int j = 3; j < 1000; j += 2)
        {
            v1 *= v2 / (j * (j - 1));
            v0 += v1 / j;
            if (v0 == v2)
                break;
        }
        return v0;
    }

    int flags = 0;
    double v0_l = 0;
    v1 = 0;
    double div = 1 / a;
    double v1_h = 2.0;
    double v0_h = 2.0;
    double v1_l = 0;
    v0 = 0;
    int i = 0;

    do
    {
        v0 += div;
        div *= ++i / a;
        if (v0 < v0_h)
            v0_h = v0;
        else
            flags |= 1;

        v1 += div;
        div *= ++i / a;
        if (v1 < v1_h)
            v1_h = v1;
        else
            flags |= 2;

        v0 -= div;
        div *= ++i / a;
        if (v0 > v0_l)
            v0_l = v0;
        else
            flags |= 4;

        v1 -= div;
        div *= ++i / a;
        if (v1 > v1_l)
            v1_l = v1;
        else
            flags |= 8;
    }
    while (flags != 0xf);

    return ((kPi - std::cos(a) * (v0_l + v0_h)) - (std::sin(a) * (v1_l + v1_h))) / 2.0;
}

uint32_t Decoder::DecryptHelper2(double a)
{
    double v0, v1, v2, v3;

    if (a > 1.0)
    {
        v0 = std::sqrt(a * 2 - 1);
        for (;;)
        {
            v1 = 1 - double(NextRand()) / 4294967296.0;
            v2 = 2.0 * double(NextRand()) / 4294967296.0 - 1.0;
            if (v1 * v1 + v2 * v2 > 1.0)
                continue;

            v2 /= v1;
            v3 = v2 * v0 + a - 1.0;
            if (v3 <= 0)
                continue;

            v1 = (a - 1.0) * std::log(v3 / (a - 1.0)) - v2 * v0;
            if (v1 < -50.0)
                continue;

            if ((double(NextRand()) / 4294967296.0) <= (std::exp(v1) * (v2 * v2 + 1.0)))
                break;
        }
    }
    else
    {
        v0 = std::exp(1.0) / (a + std::exp(1.0));
        do
        {
            v1 = double(NextRand()) / 4294967296.0;
            v2 = double(NextRand()) / 4294967296.0;
            if (v1 < v0)
            {
                v3 = std::pow(v2, 1.0 / a);
                v1 = std::exp(-v3);
            }
            else
            {
                v3 = 1.0 - std::log(v2);
                v1 = std::pow(v3, a - 1.0);
            }
        }
        while (double(NextRand()) / 4294967296.0 >= v1);
    }

    if (m_warcVersion > 120)
        return SaturateToUInt32(v3 * 256.0);
    else
        return SaturateToByte(double(NextRand()) / 4294967296.0);
}

uint32_t Decoder::DecryptHelper3(uint32_t key)
{
    auto floatOf = [](uint8_t b) { return float(1.5 * double(b) + 0.1); };
    uint32_t v0 = Binary::BigEndian(std::bit_cast<uint32_t>(floatOf(uint8_t(key))));
    uint32_t v1 = SaturateToUInt32(floatOf(uint8_t(key >> 8)));
    uint32_t v2 = uint32_t(-std::bit_cast<int32_t>(floatOf(uint8_t(key >> 16))));
    uint32_t v3 = ~std::bit_cast<uint32_t>(floatOf(uint8_t(key >> 24)));

    return ((v0 + v1) | (v2 - v3));
}

void Decoder::DecryptHelper4(Bytes& data, size_t index, const std::vector<uint32_t>& keySrc)
{
    if (!m_scheme->Region)
        return;
    if (index + 40 + 64 > data.size())
        throw InvalidDataError("Index outside the data");
    uint32_t buf[0x50];
    int i;
    for (i = 0; i < 0x10; ++i)
        buf[i] = BigEndian::ToUInt32(data.data() + index + 40 + 4 * size_t(i));
    for (; i < 0x50; ++i)
    {
        uint32_t v = buf[i - 16];
        v ^= buf[i - 14];
        v ^= buf[i - 8];
        v ^= buf[i - 3];
        buf[i] = Binary::RotL(v, 1);
    }
    uint32_t key[10] = {};
    for (size_t k = 0; k < keySrc.size() && k < 5; ++k)
        key[k] = keySrc[k];
    uint32_t k0 = key[0];
    uint32_t k1 = key[1];
    uint32_t k2 = key[2];
    uint32_t k3 = key[3];
    uint32_t k4 = key[4];

    for (int bufIdx = 0; bufIdx < 0x50; ++bufIdx)
    {
        uint32_t f, c;
        if (bufIdx < 0x10)
        {
            f = k1 ^ k2 ^ k3;
            c = 0;
        }
        else if (bufIdx < 0x20)
        {
            f = (k1 & k2) | (k3 & ~k1);
            c = 0x5A827999;
        }
        else if (bufIdx < 0x30)
        {
            f = k3 ^ (k1 | ~k2);
            c = 0x6ED9EBA1;
        }
        else if (bufIdx < 0x40)
        {
            f = (k1 & k3) | (k2 & ~k3);
            c = 0x8F1BBCDC;
        }
        else
        {
            f = k1 ^ (k2 | ~k3);
            c = 0xA953FD4E;
        }
        uint32_t newK0 = buf[bufIdx] + k4 + f + c + Binary::RotL(k0, 5);
        uint32_t newK2 = Binary::RotR(k1, 2);
        k1 = k0;
        k4 = k3;
        k3 = k2;
        k2 = newK2;
        k0 = newK0;
    }
    key[0] += k0;
    key[1] += k1;
    key[2] += k2;
    key[3] += k3;
    key[4] += k4;

    SystemTime sysTime = FileTimeToSystemTime(key[1], key[0] & 0x7FFFFFFF);
    key[5] = uint32_t(sysTime.Year | sysTime.Month << 16);
    key[7] = uint32_t(sysTime.Hour | sysTime.Minute << 16);
    key[8] = uint32_t(sysTime.Second | sysTime.Milliseconds << 16);

    uint32_t flags = LittleEndian::ToUInt32(data.data() + index + 40) | 0x80000000;
    uint32_t rgb = buf[1] >> 8;
    if (0 == (flags & 0x78000000))
        flags |= 0x98000000;
    key[6] = RegionCrc32(*m_scheme->Region, flags, rgb);
    key[9] = uint32_t((int64_t(int32_t(key[2])) * int64_t(int32_t(key[3]))) >> 8);
    if (m_scheme->Version >= 2390)
        key[6] += key[9];

    for (i = 0; i < 10; ++i)
        LittleEndian::Xor(data.data() + index + 4 * size_t(i), key[i]);
}

uint32_t Decoder::RegionCrc32(const Bytes& src, uint32_t flags, uint32_t rgb)
{
    int srcAlpha = int(flags) & 0x1ff;
    int dstAlpha = int(flags >> 12) & 0x1ff;
    flags >>= 24;
    if (0 == (flags & 0x10))
        dstAlpha = 0;
    if (0 == (flags & 8))
        srcAlpha = 0x100;
    int yStep = 0;
    int xStep = 4;
    int width = 48;
    int pos = 0;
    if (0 != (flags & 0x40))
    {
        yStep += width;
        pos += (width - 1) * 4;
        xStep = -xStep;
    }
    if (0 != (flags & 0x20))
    {
        yStep -= width;
        pos += width * 0x2f * 4;
    }
    yStep <<= 3;
    uint32_t checksum = 0;
    for (int y = 0; y < 48; ++y)
    {
        for (int x = 0; x < 48; ++x)
        {
            if (pos < 0 || size_t(pos) + 4 > src.size())
                throw InvalidDataError("Region shorter than its 48x48 pixels");
            int alpha = src[size_t(pos) + 3] * srcAlpha;
            alpha >>= 8;
            uint32_t color = rgb;
            for (int k = 0; k < 3; ++k)
            {
                int v = src[size_t(pos) + size_t(k)];
                int c = int(color & 0xff);
                c -= v;
                c = (c * dstAlpha) >> 8;
                c = (c + v) & 0xff;
                c = (c * alpha) >> 8;
                checksum = (checksum >> 8) ^ kCustomCrcTable[(uint32_t(c) ^ checksum) & 0xff];
                color >>= 8;
            }
            pos += xStep;
        }
        pos += yStep;
    }
    return checksum;
}

uint32_t Decoder::NextRand()
{
    m_rand = 1566083941u * m_rand + 1u;
    return m_rand;
}

uint32_t Decoder::GetMaxIndexLength(int version) const
{
    int maxIndexEntries = version < 150 || SchemeVersion() < 2310 ? 8192 : 16384;
    return uint32_t((m_scheme->EntryNameSize + 0x18) * maxIndexEntries);
}

} // namespace openshiina::archives
