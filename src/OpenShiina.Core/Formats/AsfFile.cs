// Windows Media files (ASF: the .wmv movies of v2.50 games). The engine plays them through
// DirectShow like its MPEG-1 movies; Sorcery Jokers ships them alone (mv\*.wmv, WMV3 / VC-1
// video with WMA sound). AsfFile splits a file into the media objects (frames) of its first
// video and first audio stream, each stream copied once into an array of its own.

namespace OpenShiina.Formats;

/// <summary>The streams of an ASF file: their formats and their media objects in file order.</summary>
public sealed class AsfFile
{
    /// <summary>A stream: its format and its objects (start and length in <see cref="Data"/>, time in ms, key frame).</summary>
    public sealed class Stream
    {
        public int Number;
        public bool Video;
        /// <summary>Video: the BITMAPINFOHEADER's compression (WMV3, WVC1), size, and the bytes after the header.</summary>
        public uint FourCC;
        public int Width, Height;
        /// <summary>Audio: the WAVEFORMATEX fields, and the bytes after it (cbSize).</summary>
        public int FormatTag, Channels, SampleRate, BytesPerSecond, BlockAlign, BitsPerSample;
        public byte[] Extra = [];
        /// <summary>100 ns units from the extended stream properties (0 when not given).</summary>
        public long TimePerFrame;
        /// <summary>The objects one after another, followed by <see cref="Padding"/> zeros.</summary>
        public byte[] Data = [];
        public readonly List<(int Start, int Length, int Time, bool Key)> Objects = [];

        // Audio spread (descrambling): span, virtual packet and chunk lengths
        internal int Span, SpanPacket, SpanChunk;
    }

    /// <summary>Zeros after each stream's data, so that a bit reader may look past its end.</summary>
    public const int Padding = 16;

    public Stream? Video { get; }
    public Stream? Audio { get; }
    /// <summary>The play duration in ms (without the preroll).</summary>
    public long Duration { get; }

    private static readonly Guid s_header = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid s_data = new("75B22636-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid s_fileProperties = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    private static readonly Guid s_streamProperties = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
    private static readonly Guid s_headerExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    private static readonly Guid s_extendedStreamProperties = new("14E6A5CB-C672-4332-8399-A96952065B5A");
    private static readonly Guid s_audioMedia = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");
    private static readonly Guid s_videoMedia = new("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B");
    private static readonly Guid s_audioSpread = new("BFC3CD50-618F-11CF-8BB2-00AA00B4E220");

    /// <summary>The file starts with the ASF header object.</summary>
    public static bool IsAsf(ReadOnlySpan<byte> file) => file.Length >= 16 && new Guid(file[..16]) == s_header;

    public AsfFile(byte[] file)
    {
        if (!IsAsf(file) || file.Length < 30)
            throw new InvalidDataException("Not an ASF file");
        var streams = new Dictionary<int, Stream>();
        var frameTimes = new Dictionary<int, long>();
        long headerSize = Long(file, 16);
        int count = Int(file, 24);
        int pos = 30, packetSize = 0;
        long preroll = 0, playDuration = 0;
        for (int k = 0; k < count && pos + 24 <= Math.Min(headerSize, file.Length); k++)
        {
            var id = new Guid(file.AsSpan(pos, 16));
            long size = Long(file, pos + 16);
            if (size < 24 || pos + size > file.Length)
                throw new InvalidDataException("Bad ASF header object");
            int body = pos + 24;
            if (id == s_fileProperties)
            {
                playDuration = Long(file, body + 40);
                preroll = Long(file, body + 56);
                packetSize = Int(file, body + 68);
            }
            else if (id == s_streamProperties)
                ReadStreamProperties(file, body, streams);
            else if (id == s_headerExtension)
            {
                // reserved GUID and word, then the size of the objects inside
                int at = body + 22, end = at + Int(file, body + 18);
                while (at + 24 <= end)
                {
                    long inner = Long(file, at + 16);
                    if (inner < 24 || at + inner > end)
                        break;
                    if (new Guid(file.AsSpan(at, 16)) == s_extendedStreamProperties)
                    {
                        int number = Short(file, at + 24 + 48);
                        frameTimes[number] = Long(file, at + 24 + 52);
                        // an embedded stream properties object follows the names and payload extensions
                        int names = Short(file, at + 24 + 60), systems = Short(file, at + 24 + 62);
                        int p = at + 24 + 64;
                        for (int n = 0; n < names && p + 4 <= at + inner; n++)
                            p += 4 + Short(file, p + 2);
                        for (int n = 0; n < systems && p + 22 <= at + inner; n++)
                            p += 22 + Int(file, p + 18);
                        if (p + 24 <= at + inner && new Guid(file.AsSpan(p, 16)) == s_streamProperties)
                            ReadStreamProperties(file, p + 24, streams);
                    }
                    at += (int)inner;
                }
            }
            pos += (int)size;
        }
        foreach (var (number, time) in frameTimes)
            if (streams.TryGetValue(number, out var s))
                s.TimePerFrame = time;
        Duration = Math.Max(0, playDuration / 10000 - preroll);
        Video = streams.Values.Where(s => s.Video).MinBy(s => s.Number);
        Audio = streams.Values.Where(s => !s.Video).MinBy(s => s.Number);
        pos = (int)Math.Min(headerSize, file.Length);
        if (pos + 50 > file.Length || new Guid(file.AsSpan(pos, 16)) != s_data || packetSize <= 0)
            throw new InvalidDataException("No ASF data object");
        long dataEnd = Math.Min(file.Length, pos + Long(file, pos + 16));
        if (dataEnd <= pos + 50)
            dataEnd = file.Length;
        var builders = new Dictionary<int, Builder>();
        if (Video != null)
            builders[Video.Number] = new Builder(Video, (int)preroll);
        if (Audio != null)
            builders[Audio.Number] = new Builder(Audio, (int)preroll);
        for (long p = pos + 50; p + packetSize <= dataEnd; p += packetSize)
            ReadPacket(file, (int)p, packetSize, builders);
        foreach (var b in builders.Values)
            b.Finish();
    }

    private static void ReadStreamProperties(byte[] file, int body, Dictionary<int, Stream> streams)
    {
        var type = new Guid(file.AsSpan(body, 16));
        var correction = new Guid(file.AsSpan(body + 16, 16));
        int specific = Int(file, body + 40), correctionLength = Int(file, body + 44);
        int number = Short(file, body + 48) & 0x7F;
        int data = body + 54;
        if (data + specific + correctionLength > file.Length || streams.ContainsKey(number))
            return;
        if (type == s_videoMedia && specific >= 11 + 40)
        {
            int header = data + 11, size = Int(file, header);
            var s = new Stream
            {
                Number = number,
                Video = true,
                Width = Int(file, header + 4),
                Height = Int(file, header + 8),
                FourCC = (uint)Int(file, header + 16),
            };
            int extra = Math.Clamp(Math.Min(size, specific - 11) - 40, 0, specific - 51);
            s.Extra = file.AsSpan(header + 40, extra).ToArray();
            streams[number] = s;
        }
        else if (type == s_audioMedia && specific >= 16)
        {
            var s = new Stream
            {
                Number = number,
                FormatTag = Short(file, data),
                Channels = Short(file, data + 2),
                SampleRate = Int(file, data + 4),
                BytesPerSecond = Int(file, data + 8),
                BlockAlign = Short(file, data + 12),
                BitsPerSample = Short(file, data + 14),
            };
            if (specific >= 18)
                s.Extra = file.AsSpan(data + 18, Math.Clamp(Short(file, data + 16), 0, specific - 18)).ToArray();
            if (correction == s_audioSpread && correctionLength >= 5)
            {
                int c = data + specific;
                s.Span = file[c];
                s.SpanPacket = Short(file, c + 1);
                s.SpanChunk = Short(file, c + 3);
                if (s.Span > 1 && (s.SpanChunk == 0 || s.SpanPacket / s.SpanChunk <= 1 || s.SpanPacket % s.SpanChunk != 0))
                    s.Span = 0;
            }
            streams[number] = s;
        }
    }

    /// <summary>The objects of a stream as they are put together from the payloads.</summary>
    private sealed class Builder(Stream stream, int preroll)
    {
        private byte[] m_data = new byte[1 << 20];
        private int m_length;
        // the object being filled: its number, start, size and bytes so far
        private int m_number = -1, m_start, m_size, m_filled, m_time;
        private bool m_key;

        public void Payload(byte[] file, int at, int length, int number, int offset, int size, int time, bool key)
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
                m_time = time - preroll;
                m_key = key;
                Grow(m_start + size);
            }
            if (offset != m_filled || offset + length > m_size)
            {
                m_number = -1;
                m_length = m_start;
                return;
            }
            Buffer.BlockCopy(file, at, m_data, m_start + offset, length);
            m_filled += length;
            if (m_filled == m_size)
            {
                Descramble(m_start, m_size);
                stream.Objects.Add((m_start, m_size, m_time, m_key || !stream.Video));
                m_length = m_start + m_size;
                m_number = -1;
            }
        }

        private void Grow(int length)
        {
            if (length + Padding <= m_data.Length)
                return;
            Array.Resize(ref m_data, (int)Math.Min(Array.MaxLength, Math.Max(length + Padding, (long)m_data.Length * 2)));
        }

        private void Descramble(int start, int size)
        {
            int span = stream.Span, packet = stream.SpanPacket, chunk = stream.SpanChunk;
            if (span <= 1 || size != packet * span)
                return;
            var copy = m_data.AsSpan(start, size).ToArray();
            for (int offset = 0; offset < size; offset += chunk)
            {
                int off = offset / chunk, row = off / span, col = off % span;
                int index = row + col * packet / chunk;
                copy.AsSpan(index * chunk, chunk).CopyTo(m_data.AsSpan(start + offset));
            }
        }

        public void Finish()
        {
            var data = new byte[m_length + Padding];
            Buffer.BlockCopy(m_data, 0, data, 0, m_length);
            stream.Data = data;
            m_data = [];
        }
    }

    /// <summary>A field of 0, 1, 2 or 4 bytes (a two-bit length type).</summary>
    private static int Field(byte[] file, ref int at, int type)
    {
        switch (type & 3)
        {
            case 1: return file[at++];
            case 2: at += 2; return Short(file, at - 2);
            case 3: at += 4; return Int(file, at - 4);
            default: return 0;
        }
    }

    private static void ReadPacket(byte[] file, int start, int packetSize, Dictionary<int, Builder> builders)
    {
        int at = start, end = start + packetSize;
        int b = file[at];
        if ((b & 0x80) != 0)
        {
            // error correction data
            at += 1 + ((b & 0x60) == 0 ? b & 0x0F : 0);
        }
        int flags = file[at], property = file[at + 1];
        at += 2;
        int length = Field(file, ref at, flags >> 5);
        Field(file, ref at, flags >> 1);
        int padding = Field(file, ref at, flags >> 3);
        at += 6; // send time, duration
        if (length == 0 || length > packetSize)
            length = packetSize;
        end = start + length - padding;
        int payloads = 1, lengthType = 2;
        bool multiple = (flags & 1) != 0;
        if (multiple)
        {
            payloads = file[at] & 0x3F;
            lengthType = file[at] >> 6;
            at++;
        }
        for (int n = 0; n < payloads && at < end; n++)
        {
            int streamByte = file[at++];
            int number = Field(file, ref at, property >> 4);
            int offset = Field(file, ref at, property >> 2);
            int replicated = Field(file, ref at, property);
            int size = 0, time = 0;
            int replicatedAt = at;
            at += replicated;
            int payload = multiple ? Field(file, ref at, lengthType) : end - at;
            if (at + payload > end || payload < 0)
                return;
            builders.TryGetValue(streamByte & 0x7F, out var builder);
            bool key = (streamByte & 0x80) != 0;
            if (replicated == 1)
            {
                // compressed payload: offset is the time, then a time delta and sub-payloads of a byte's length
                int delta = file[replicatedAt];
                int p = at, t = offset;
                while (p < at + payload)
                {
                    int sub = file[p++];
                    if (p + sub > at + payload)
                        break;
                    builder?.Payload(file, p, sub, -2 - n, 0, sub, t, key);
                    p += sub;
                    t += delta;
                }
            }
            else if (replicated >= 8)
            {
                size = Int(file, replicatedAt);
                time = Int(file, replicatedAt + 4);
                if (size > 0)
                    builder?.Payload(file, at, payload, number, offset, size, time, key);
            }
            at += payload;
        }
    }

    private static int Short(byte[] d, int at) => d[at] | d[at + 1] << 8;
    private static int Int(byte[] d, int at) => BitConverter.ToInt32(d, at);
    private static long Long(byte[] d, int at) => BitConverter.ToInt64(d, at);
}
