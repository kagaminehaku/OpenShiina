// C# Formats/AsfFile.cs (see AsfFile.h)
#include "formats/AsfFile.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <map>

namespace openshiina::formats {

namespace {

using Guid = std::array<uint8_t, 16>;

/// A GUID as .NET lays it out in memory (its first three groups little-endian).
Guid MakeGuid(uint32_t a, uint16_t b, uint16_t c, uint64_t d)
{
    Guid g{};
    for (int i = 0; i < 4; i++)
        g[size_t(i)] = uint8_t(a >> (8 * i));
    g[4] = uint8_t(b);
    g[5] = uint8_t(b >> 8);
    g[6] = uint8_t(c);
    g[7] = uint8_t(c >> 8);
    for (int i = 0; i < 8; i++)
        g[size_t(8 + i)] = uint8_t(d >> (8 * (7 - i)));
    return g;
}

const Guid kHeader = MakeGuid(0x75B22630, 0x668E, 0x11CF, 0xA6D900AA0062CE6Cull);
const Guid kData = MakeGuid(0x75B22636, 0x668E, 0x11CF, 0xA6D900AA0062CE6Cull);
const Guid kFileProperties = MakeGuid(0x8CABDCA1, 0xA947, 0x11CF, 0x8EE400C00C205365ull);
const Guid kStreamProperties = MakeGuid(0xB7DC0791, 0xA9B7, 0x11CF, 0x8EE600C00C205365ull);
const Guid kHeaderExtension = MakeGuid(0x5FBF03B5, 0xA92E, 0x11CF, 0x8EE300C00C205365ull);
const Guid kExtendedStreamProperties = MakeGuid(0x14E6A5CB, 0xC672, 0x4332, 0x8399A96952065B5Aull);
const Guid kAudioMedia = MakeGuid(0xF8699E40, 0x5B4D, 0x11CF, 0xA8FD00805F5C442Bull);
const Guid kVideoMedia = MakeGuid(0xBC19EFC0, 0x5B4D, 0x11CF, 0xA8FD00805F5C442Bull);
const Guid kAudioSpread = MakeGuid(0xBFC3CD50, 0x618F, 0x11CF, 0x8BB200AA00B4E220ull);

/// Reads of the file that, past its end, are an error (C#: the index out of range).
struct Reader
{
    const Bytes& F;

    void Need(int64_t at, int64_t length) const
    {
        if (at < 0 || length < 0 || at + length > int64_t(F.size()))
            throw InvalidDataError("Read past the end of the ASF file");
    }
    int Byte(int at) const
    {
        Need(at, 1);
        return F[size_t(at)];
    }
    int Short(int at) const
    {
        Need(at, 2);
        return F[size_t(at)] | F[size_t(at) + 1] << 8;
    }
    int Int(int at) const
    {
        Need(at, 4);
        return int(LittleEndian::ToUInt32(F.data() + at));
    }
    int64_t Long(int at) const
    {
        Need(at, 8);
        return int64_t(LittleEndian::ToUInt64(F.data() + at));
    }
    bool Is(int at, const Guid& g) const
    {
        Need(at, 16);
        return std::memcmp(F.data() + at, g.data(), 16) == 0;
    }
    /// A field of 0, 1, 2 or 4 bytes (a two-bit length type).
    int Field(int& at, int type) const
    {
        switch (type & 3)
        {
        case 1:
            return Byte(at++);
        case 2:
            at += 2;
            return Short(at - 2);
        case 3:
            at += 4;
            return Int(at - 4);
        default:
            return 0;
        }
    }
};

/// The objects of a stream as they are put together from the payloads.
class Builder
{
public:
    Builder(AsfFile::Stream& stream, int preroll) : m_stream(stream), m_preroll(preroll), m_data(size_t(1) << 20) {}

    void Payload(const Bytes& file, int at, int length, int number, int offset, int size, int time, bool key)
    {
        if (offset == 0 || number != m_number)
        {
            // a new object (an unfinished one is dropped)
            if (offset != 0)
                return;
            m_number = number;
            m_start = m_length;
            m_size = size;
            m_filled = 0;
            m_time = time - m_preroll;
            m_key = key;
            Grow(int64_t(m_start) + size);
        }
        if (offset != m_filled || int64_t(offset) + length > m_size)
        {
            m_number = -1;
            m_length = m_start;
            return;
        }
        std::memcpy(m_data.data() + m_start + offset, file.data() + at, size_t(length));
        m_filled += length;
        if (m_filled == m_size)
        {
            Descramble(m_start, m_size);
            m_stream.Objects.push_back({m_start, m_size, m_time, m_key || !m_stream.Video});
            m_length = m_start + m_size;
            m_number = -1;
        }
    }

    void Finish()
    {
        m_stream.Data.assign(m_data.begin(), m_data.begin() + m_length);
        m_stream.Data.resize(size_t(m_length) + AsfFile::Padding, 0);
        m_data = {};
    }

private:
    void Grow(int64_t length)
    {
        if (length + AsfFile::Padding <= int64_t(m_data.size()))
            return;
        if (length + AsfFile::Padding > 0x7FFFFFC7)
            throw InvalidDataError("ASF stream too large");
        m_data.resize(size_t(std::max<int64_t>(length + AsfFile::Padding, int64_t(m_data.size()) * 2)));
    }

    void Descramble(int start, int size)
    {
        int span = m_stream.Span, packet = m_stream.SpanPacket, chunk = m_stream.SpanChunk;
        if (span <= 1 || size != packet * span)
            return;
        Bytes copy(m_data.begin() + start, m_data.begin() + start + size);
        for (int offset = 0; offset < size; offset += chunk)
        {
            int off = offset / chunk, row = off / span, col = off % span;
            int index = row + col * packet / chunk;
            if (int64_t(index) * chunk + chunk > size)
                throw InvalidDataError("Bad ASF audio spread");
            std::memcpy(m_data.data() + start + offset, copy.data() + size_t(index) * size_t(chunk), size_t(chunk));
        }
    }

    AsfFile::Stream& m_stream;
    int m_preroll;
    Bytes m_data;
    int m_length = 0;
    // the object being filled: its number, start, size and bytes so far
    int m_number = -1, m_start = 0, m_size = 0, m_filled = 0, m_time = 0;
    bool m_key = false;
};

void ReadStreamProperties(const Reader& r, int body, std::map<int, AsfFile::Stream>& streams)
{
    bool video = r.Is(body, kVideoMedia), audio = r.Is(body, kAudioMedia);
    bool spread = r.Is(body + 16, kAudioSpread);
    int specific = r.Int(body + 40), correctionLength = r.Int(body + 44);
    int number = r.Short(body + 48) & 0x7F;
    int data = body + 54;
    if (int64_t(data) + specific + correctionLength > int64_t(r.F.size()) || streams.count(number) != 0)
        return;
    if (video && specific >= 11 + 40)
    {
        int header = data + 11, size = r.Int(header);
        AsfFile::Stream s;
        s.Number = number;
        s.Video = true;
        s.Width = r.Int(header + 4);
        s.Height = r.Int(header + 8);
        s.FourCC = uint32_t(r.Int(header + 16));
        int extra = std::clamp(std::min(size, specific - 11) - 40, 0, specific - 51);
        r.Need(header + 40, extra);
        s.Extra.assign(r.F.begin() + header + 40, r.F.begin() + header + 40 + extra);
        streams[number] = std::move(s);
    }
    else if (audio && specific >= 16)
    {
        AsfFile::Stream s;
        s.Number = number;
        s.FormatTag = r.Short(data);
        s.Channels = r.Short(data + 2);
        s.SampleRate = r.Int(data + 4);
        s.BytesPerSecond = r.Int(data + 8);
        s.BlockAlign = r.Short(data + 12);
        s.BitsPerSample = r.Short(data + 14);
        if (specific >= 18)
        {
            int extra = std::clamp(r.Short(data + 16), 0, specific - 18);
            r.Need(data + 18, extra);
            s.Extra.assign(r.F.begin() + data + 18, r.F.begin() + data + 18 + extra);
        }
        if (spread && correctionLength >= 5)
        {
            int c = data + specific;
            s.Span = r.Byte(c);
            s.SpanPacket = r.Short(c + 1);
            s.SpanChunk = r.Short(c + 3);
            if (s.Span > 1 && (s.SpanChunk == 0 || s.SpanPacket / s.SpanChunk <= 1 || s.SpanPacket % s.SpanChunk != 0))
                s.Span = 0;
        }
        streams[number] = std::move(s);
    }
}

void ReadPacket(const Reader& r, int start, int packetSize, std::map<int, Builder*>& builders)
{
    int at = start, end;
    int b = r.Byte(at);
    if ((b & 0x80) != 0)
    {
        // error correction data
        at += 1 + ((b & 0x60) == 0 ? b & 0x0F : 0);
    }
    int flags = r.Byte(at), property = r.Byte(at + 1);
    at += 2;
    int length = r.Field(at, flags >> 5);
    r.Field(at, flags >> 1);
    int padding = r.Field(at, flags >> 3);
    at += 6;    // send time, duration
    if (length == 0 || length > packetSize)
        length = packetSize;
    end = start + length - padding;
    int payloads = 1, lengthType = 2;
    bool multiple = (flags & 1) != 0;
    if (multiple)
    {
        payloads = r.Byte(at) & 0x3F;
        lengthType = r.Byte(at) >> 6;
        at++;
    }
    for (int n = 0; n < payloads && at < end; n++)
    {
        int streamByte = r.Byte(at++);
        int number = r.Field(at, property >> 4);
        int offset = r.Field(at, property >> 2);
        int replicated = r.Field(at, property);
        int replicatedAt = at;
        at += replicated;
        int payload = multiple ? r.Field(at, lengthType) : end - at;
        if (at + payload > end || payload < 0)
            return;
        auto found = builders.find(streamByte & 0x7F);
        Builder* builder = found != builders.end() ? found->second : nullptr;
        bool key = (streamByte & 0x80) != 0;
        r.Need(at, payload);
        if (replicated == 1)
        {
            // compressed payload: offset is the time, then a time delta and sub-payloads of a byte's length
            int delta = r.Byte(replicatedAt);
            int p = at, t = offset;
            while (p < at + payload)
            {
                int sub = r.Byte(p++);
                if (p + sub > at + payload)
                    break;
                if (builder)
                    builder->Payload(r.F, p, sub, -2 - n, 0, sub, t, key);
                p += sub;
                t += delta;
            }
        }
        else if (replicated >= 8)
        {
            int size = r.Int(replicatedAt), time = r.Int(replicatedAt + 4);
            if (size > 0 && builder)
                builder->Payload(r.F, at, payload, number, offset, size, time, key);
        }
        at += payload;
    }
}

} // namespace

bool AsfFile::IsAsf(const Bytes& file)
{
    return file.size() >= 16 && std::memcmp(file.data(), kHeader.data(), 16) == 0;
}

AsfFile::AsfFile(const Bytes& file)
{
    if (!IsAsf(file) || file.size() < 30)
        throw InvalidDataError("Not an ASF file");
    if (file.size() > 0x7FFFFFFF)
        throw InvalidDataError("ASF file too large");
    Reader r{file};
    std::map<int, Stream> streams;
    std::map<int, int64_t> frameTimes;
    int64_t headerSize = r.Long(16);
    int count = r.Int(24);
    int pos = 30, packetSize = 0;
    int64_t preroll = 0, playDuration = 0;
    for (int k = 0; k < count && pos + 24 <= std::min<int64_t>(headerSize, int64_t(file.size())); k++)
    {
        int64_t size = r.Long(pos + 16);
        if (size < 24 || pos + size > int64_t(file.size()))
            throw InvalidDataError("Bad ASF header object");
        int body = pos + 24;
        if (r.Is(pos, kFileProperties))
        {
            playDuration = r.Long(body + 40);
            preroll = r.Long(body + 56);
            packetSize = r.Int(body + 68);
        }
        else if (r.Is(pos, kStreamProperties))
            ReadStreamProperties(r, body, streams);
        else if (r.Is(pos, kHeaderExtension))
        {
            // reserved GUID and word, then the size of the objects inside
            int at = body + 22, end = at + r.Int(body + 18);
            while (at + 24 <= end)
            {
                int64_t inner = r.Long(at + 16);
                if (inner < 24 || at + inner > end)
                    break;
                if (r.Is(at, kExtendedStreamProperties))
                {
                    int number = r.Short(at + 24 + 48);
                    frameTimes[number] = r.Long(at + 24 + 52);
                    // an embedded stream properties object follows the names and payload extensions
                    int names = r.Short(at + 24 + 60), systems = r.Short(at + 24 + 62);
                    int p = at + 24 + 64;
                    for (int n = 0; n < names && p + 4 <= at + inner; n++)
                        p += 4 + r.Short(p + 2);
                    for (int n = 0; n < systems && p + 22 <= at + inner; n++)
                        p += 22 + r.Int(p + 18);
                    if (p + 24 <= at + inner && r.Is(p, kStreamProperties))
                        ReadStreamProperties(r, p + 24, streams);
                }
                at += int(inner);
            }
        }
        pos += int(size);
    }
    for (const auto& [number, time] : frameTimes)
        if (auto found = streams.find(number); found != streams.end())
            found->second.TimePerFrame = time;
    Duration = std::max<int64_t>(0, playDuration / 10000 - preroll);
    // the first video and the first audio stream by number (std::map is in number order)
    for (auto& [number, s] : streams)
    {
        if (s.Video && !Video)
            Video = s;
        else if (!s.Video && !Audio)
            Audio = s;
    }
    pos = int(std::min<int64_t>(headerSize, int64_t(file.size())));
    if (pos + 50 > int64_t(file.size()) || !r.Is(pos, kData) || packetSize <= 0)
        throw InvalidDataError("No ASF data object");
    int64_t dataEnd = std::min<int64_t>(int64_t(file.size()), pos + r.Long(pos + 16));
    if (dataEnd <= pos + 50)
        dataEnd = int64_t(file.size());
    std::optional<Builder> video, audio;
    std::map<int, Builder*> builders;
    if (Video)
        builders[Video->Number] = &video.emplace(*Video, int(preroll));
    if (Audio)
        builders[Audio->Number] = &audio.emplace(*Audio, int(preroll));
    for (int64_t p = pos + 50; p + packetSize <= dataEnd; p += packetSize)
        ReadPacket(r, int(p), packetSize, builders);
    if (video)
        video->Finish();
    if (audio)
        audio->Finish();
}

} // namespace openshiina::formats
