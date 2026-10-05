// BinaryStream and auxiliary stream implementations
// Adapted from GARbro GameRes/BinaryStream.cs and ArcFormats/CommonStreams.cs

using System.IO;
using System.Text;

namespace OpenShiina.IO;

public interface IBinaryStream : IDisposable
{
    string Name { get; set; }
    uint Signature { get; }
    Stream AsStream { get; }
    bool CanSeek { get; }
    long Length { get; }
    long Position { get; set; }

    long Seek(long offset, SeekOrigin origin);
    int Read(byte[] buffer, int offset, int count);
    int ReadByte();
    byte ReadUInt8();
    ushort ReadUInt16();
    int ReadInt32();
    uint ReadUInt32();
    long ReadInt64();
    byte[] ReadBytes(int count);
    byte[] ReadHeader(int size);
    string ReadCString(int length, Encoding enc);
    string ReadCString(int length);
    string ReadCString(Encoding enc);
    string ReadCString();
}

public class BinaryStream : Stream, IBinaryStream
{
    private readonly Stream m_source;
    private readonly bool m_leaveOpen;

    public string Name { get; set; } = "";
    public Stream AsStream => this;
    public override bool CanRead => m_source.CanRead;
    public override bool CanSeek => m_source.CanSeek;
    public override bool CanWrite => m_source.CanWrite;
    public override long Length => m_source.Length;

    public override long Position
    {
        get => m_source.Position;
        set => m_source.Position = value;
    }

    public uint Signature
    {
        get
        {
            long pos = Position;
            byte[] buf = ReadHeader(4);
            Position = pos;
            return buf.Length >= 4 ? LittleEndian.ToUInt32(buf, 0) : 0;
        }
    }

    public BinaryStream(Stream source, string name = "", bool leaveOpen = false)
    {
        m_source = source;
        Name = name;
        m_leaveOpen = leaveOpen;
    }

    public override int Read(byte[] buffer, int offset, int count) => m_source.Read(buffer, offset, count);
    public override int ReadByte() => m_source.ReadByte();
    public override long Seek(long offset, SeekOrigin origin) => m_source.Seek(offset, origin);
    public override void Flush() => m_source.Flush();
    public override void SetLength(long value) => m_source.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => m_source.Write(buffer, offset, count);

    public byte ReadUInt8()
    {
        int b = ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public ushort ReadUInt16()
    {
        byte[] buf = ReadBytes(2);
        if (buf.Length < 2) throw new EndOfStreamException();
        return LittleEndian.ToUInt16(buf, 0);
    }

    public uint ReadUInt32()
    {
        byte[] buf = ReadBytes(4);
        if (buf.Length < 4) throw new EndOfStreamException();
        return LittleEndian.ToUInt32(buf, 0);
    }

    public int ReadInt32()
    {
        byte[] buf = ReadBytes(4);
        if (buf.Length < 4) throw new EndOfStreamException();
        return LittleEndian.ToInt32(buf, 0);
    }

    public long ReadInt64()
    {
        byte[] buf = ReadBytes(8);
        if (buf.Length < 8) throw new EndOfStreamException();
        return LittleEndian.ToInt64(buf, 0);
    }

    public byte[] ReadBytes(int count)
    {
        byte[] buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int r = Read(buf, read, count - read);
            if (r == 0) break;
            read += r;
        }
        if (read < count) Array.Resize(ref buf, read);
        return buf;
    }

    public byte[] ReadHeader(int size)
    {
        long pos = Position;
        byte[] buf = ReadBytes(size);
        Position = pos;
        return buf;
    }

    public string ReadCString(int length, Encoding enc)
    {
        byte[] buf = ReadBytes(length);
        int end = Array.IndexOf<byte>(buf, 0);
        if (end < 0) end = buf.Length;
        return enc.GetString(buf, 0, end);
    }

    public string ReadCString(int length) => ReadCString(length, Encodings.cp932);

    public string ReadCString(Encoding enc)
    {
        var ms = new MemoryStream();
        int b;
        while ((b = ReadByte()) > 0)
        {
            ms.WriteByte((byte)b);
        }
        return enc.GetString(ms.ToArray());
    }

    public string ReadCString() => ReadCString(Encodings.cp932);

    void IDisposable.Dispose()
    {
        base.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !m_leaveOpen)
        {
            m_source.Dispose();
        }
        base.Dispose(disposing);
    }
}

public class BinMemoryStream : Stream, IBinaryStream
{
    private readonly MemoryStream m_mem;
    public string Name { get; set; } = "";
    public Stream AsStream => this;
    public override bool CanRead => m_mem.CanRead;
    public override bool CanSeek => m_mem.CanSeek;
    public override bool CanWrite => m_mem.CanWrite;
    public override long Length => m_mem.Length;
    public override long Position { get => m_mem.Position; set => m_mem.Position = value; }

    public uint Signature
    {
        get
        {
            if (Length < 4) return 0;
            long pos = Position;
            Position = 0;
            byte[] buf = new byte[4];
            m_mem.Read(buf, 0, 4);
            Position = pos;
            return LittleEndian.ToUInt32(buf, 0);
        }
    }

    public BinMemoryStream(byte[] buffer, string name = "")
    {
        m_mem = new MemoryStream(buffer);
        Name = name;
    }

    public BinMemoryStream(byte[] buffer, int index, int count, string name = "")
    {
        m_mem = new MemoryStream(buffer, index, count);
        Name = name;
    }

    public override int Read(byte[] buffer, int offset, int count) => m_mem.Read(buffer, offset, count);
    public override int ReadByte() => m_mem.ReadByte();
    public override long Seek(long offset, SeekOrigin origin) => m_mem.Seek(offset, origin);
    public override void Flush() => m_mem.Flush();
    public override void SetLength(long value) => m_mem.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => m_mem.Write(buffer, offset, count);

    public byte ReadUInt8()
    {
        int b = ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public ushort ReadUInt16()
    {
        byte[] buf = ReadBytes(2);
        if (buf.Length < 2) throw new EndOfStreamException();
        return LittleEndian.ToUInt16(buf, 0);
    }

    public uint ReadUInt32()
    {
        byte[] buf = ReadBytes(4);
        if (buf.Length < 4) throw new EndOfStreamException();
        return LittleEndian.ToUInt32(buf, 0);
    }

    public int ReadInt32()
    {
        byte[] buf = ReadBytes(4);
        if (buf.Length < 4) throw new EndOfStreamException();
        return LittleEndian.ToInt32(buf, 0);
    }

    public long ReadInt64()
    {
        byte[] buf = ReadBytes(8);
        if (buf.Length < 8) throw new EndOfStreamException();
        return LittleEndian.ToInt64(buf, 0);
    }

    public byte[] ReadBytes(int count)
    {
        byte[] buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int r = m_mem.Read(buf, read, count - read);
            if (r == 0) break;
            read += r;
        }
        if (read < count) Array.Resize(ref buf, read);
        return buf;
    }

    public byte[] ReadHeader(int size)
    {
        long pos = Position;
        byte[] buf = ReadBytes(size);
        Position = pos;
        return buf;
    }

    public string ReadCString(int length, Encoding enc)
    {
        byte[] buf = ReadBytes(length);
        int end = Array.IndexOf<byte>(buf, 0);
        if (end < 0) end = buf.Length;
        return enc.GetString(buf, 0, end);
    }

    public string ReadCString(int length) => ReadCString(length, Encodings.cp932);

    public string ReadCString(Encoding enc)
    {
        var ms = new MemoryStream();
        int b;
        while ((b = ReadByte()) > 0)
        {
            ms.WriteByte((byte)b);
        }
        return enc.GetString(ms.ToArray());
    }

    public string ReadCString() => ReadCString(Encodings.cp932);

    void IDisposable.Dispose()
    {
        base.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            m_mem.Dispose();
        }
        base.Dispose(disposing);
    }
}

public class StreamRegion : Stream
{
    private readonly Stream m_main;
    private readonly long m_begin;
    private readonly long m_end;
    private readonly bool m_leaveOpen;

    public override bool CanRead => m_main.CanRead;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => m_end - m_begin;

    public override long Position
    {
        get => m_main.Position - m_begin;
        set => m_main.Position = Math.Max(m_begin + value, m_begin);
    }

    public StreamRegion(Stream main, long offset, long length, bool leaveOpen = false)
    {
        m_main = main;
        m_begin = offset;
        m_end = Math.Min(offset + length, main.Length);
        m_leaveOpen = leaveOpen;
        m_main.Position = m_begin;
    }

    public StreamRegion(Stream main, long offset, bool leaveOpen = false)
        : this(main, offset, main.Length - offset, leaveOpen)
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long available = m_end - m_main.Position;
        if (available <= 0) return 0;
        return m_main.Read(buffer, offset, (int)Math.Min(count, available));
    }

    public override int ReadByte()
    {
        if (m_main.Position < m_end)
            return m_main.ReadByte();
        return -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => m_begin + offset,
            SeekOrigin.Current => m_main.Position + offset,
            SeekOrigin.End => m_end + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        target = Math.Clamp(target, m_begin, m_end);
        m_main.Position = target;
        return target - m_begin;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !m_leaveOpen)
        {
            m_main.Dispose();
        }
        base.Dispose(disposing);
    }
}

public class PrefixStream : Stream
{
    private readonly byte[] m_header;
    private readonly Stream m_baseStream;
    private readonly bool m_leaveOpen;
    private long m_position;

    public override bool CanRead => true;
    public override bool CanSeek => m_baseStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => m_header.Length + m_baseStream.Length;

    public override long Position
    {
        get => m_position;
        set
        {
            if (!CanSeek) throw new NotSupportedException();
            m_position = Math.Max(value, 0);
            if (m_position > m_header.Length)
            {
                long streamPos = m_baseStream.Seek(m_position - m_header.Length, SeekOrigin.Begin);
                m_position = m_header.Length + streamPos;
            }
        }
    }

    public PrefixStream(byte[] header, Stream baseStream, bool leaveOpen = false)
    {
        m_header = header;
        m_baseStream = baseStream;
        m_leaveOpen = leaveOpen;
        m_position = 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = 0;
        if (m_position < m_header.Length)
        {
            int headerCount = Math.Min(count, m_header.Length - (int)m_position);
            Buffer.BlockCopy(m_header, (int)m_position, buffer, offset, headerCount);
            m_position += headerCount;
            read += headerCount;
            offset += headerCount;
            count -= headerCount;
        }
        if (count > 0)
        {
            if (m_header.Length == m_position && m_baseStream.CanSeek)
                m_baseStream.Position = 0;
            int streamRead = m_baseStream.Read(buffer, offset, count);
            m_position += streamRead;
            read += streamRead;
        }
        return read;
    }

    public override int ReadByte()
    {
        if (m_position < m_header.Length)
            return m_header[m_position++];
        if (m_position == m_header.Length && m_baseStream.CanSeek)
            m_baseStream.Position = 0;
        int b = m_baseStream.ReadByte();
        if (-1 != b) m_position++;
        return b;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => m_position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = target;
        return m_position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !m_leaveOpen)
        {
            m_baseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
