// Core memory-mapped file access for archive reading
// Adapted from GARbro GameRes/ArcView.cs

using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenShiina.IO;

public class ArcView : IDisposable
{
    private MemoryMappedFile m_map;
    private FileStream m_fileStream;
    public long MaxOffset { get; private set; }
    public Frame View { get; private set; }
    public string Name { get; private set; }
    private bool disposed;

    public ArcView(string path)
    {
        Name = Path.GetFullPath(path);
        m_fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        MaxOffset = m_fileStream.Length;
        m_map = MemoryMappedFile.CreateFromFile(m_fileStream, null, 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        View = new Frame(this);
    }

    public ArcViewStream CreateStream()
    {
        return CreateStream(0, (uint)Math.Min(MaxOffset, uint.MaxValue), Name);
    }

    public ArcViewStream CreateStream(long offset)
    {
        long size = MaxOffset - offset;
        return CreateStream(offset, (uint)Math.Min(size, uint.MaxValue), Name);
    }

    public ArcViewStream CreateStream(long offset, uint size, string? name = null)
    {
        return new ArcViewStream(this, offset, size, name ?? Name);
    }

    public MemoryMappedViewAccessor CreateViewAccessor(long offset, uint size)
    {
        return m_map.CreateViewAccessor(offset, size, MemoryMappedFileAccess.Read);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                View?.Dispose();
                m_map?.Dispose();
                m_fileStream?.Dispose();
            }
            disposed = true;
        }
    }

    public class Frame : IDisposable
    {
        private readonly ArcView m_arc;
        private MemoryMappedViewAccessor? m_view;
        private unsafe byte* m_ptr;
        private long m_offset;
        private uint m_size;
        private bool m_disposed;

        public Frame(ArcView arc)
        {
            m_arc = arc;
            m_offset = 0;
            m_size = (uint)Math.Min(65536, m_arc.MaxOffset);
            Remap(m_offset, m_size);
        }

        private void Remap(long offset, uint size)
        {
            Release();
            m_offset = offset;
            m_size = (uint)Math.Min(size, m_arc.MaxOffset - offset);
            m_view = m_arc.CreateViewAccessor(m_offset, m_size);
            unsafe
            {
                byte* ptr = null;
                m_view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                m_ptr = ptr + m_view.PointerOffset;
            }
        }

        private void Release()
        {
            if (m_view != null)
            {
                unsafe
                {
                    if (m_ptr != null)
                    {
                        m_view.SafeMemoryMappedViewHandle.ReleasePointer();
                        m_ptr = null;
                    }
                }
                m_view.Dispose();
                m_view = null;
            }
        }

        public byte ReadByte(long offset)
        {
            Ensure(offset, 1);
            unsafe { return m_ptr[offset - m_offset]; }
        }

        public ushort ReadUInt16(long offset)
        {
            Ensure(offset, 2);
            unsafe { return *(ushort*)(m_ptr + (offset - m_offset)); }
        }

        public uint ReadUInt32(long offset)
        {
            Ensure(offset, 4);
            unsafe { return *(uint*)(m_ptr + (offset - m_offset)); }
        }

        public int ReadInt32(long offset)
        {
            Ensure(offset, 4);
            unsafe { return *(int*)(m_ptr + (offset - m_offset)); }
        }

        public long ReadInt64(long offset)
        {
            Ensure(offset, 8);
            unsafe { return *(long*)(m_ptr + (offset - m_offset)); }
        }

        public byte[] ReadBytes(long offset, uint count)
        {
            byte[] buf = new byte[count];
            Read(offset, buf, 0, count);
            return buf;
        }

        public uint Read(long offset, byte[] buffer, int bufOffset, uint count)
        {
            uint totalRead = 0;
            while (count > 0 && offset < m_arc.MaxOffset)
            {
                Ensure(offset, 1);
                uint available = m_size - (uint)(offset - m_offset);
                uint toRead = Math.Min(count, available);
                unsafe
                {
                    Marshal.Copy((IntPtr)(m_ptr + (offset - m_offset)), buffer, bufOffset, (int)toRead);
                }
                totalRead += toRead;
                bufOffset += (int)toRead;
                offset += toRead;
                count -= toRead;
            }
            return totalRead;
        }

        public bool AsciiEqual(long offset, string text)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(text);
            if (offset + ascii.Length > m_arc.MaxOffset)
                return false;
            Ensure(offset, (uint)ascii.Length);
            unsafe
            {
                for (int i = 0; i < ascii.Length; i++)
                {
                    if (m_ptr[offset - m_offset + i] != ascii[i])
                        return false;
                }
            }
            return true;
        }

        private void Ensure(long offset, uint size)
        {
            if (offset < m_offset || offset + size > m_offset + m_size)
            {
                long newOffset = Math.Max(0, offset);
                uint newSize = Math.Max(size, 65536u);
                if (newOffset + newSize > m_arc.MaxOffset)
                    newSize = (uint)(m_arc.MaxOffset - newOffset);
                Remap(newOffset, newSize);
            }
        }

        public void Dispose()
        {
            if (!m_disposed)
            {
                Release();
                m_disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}

public class ArcViewStream : Stream, IBinaryStream
{
    private readonly ArcView m_arc;
    private readonly long m_begin;
    private readonly long m_size;
    private long m_position;

    public string Name { get; set; }
    public Stream AsStream => this;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => m_size;

    public override long Position
    {
        get => m_position;
        set => m_position = Math.Clamp(value, 0, m_size);
    }

    public uint Signature
    {
        get
        {
            if (m_size < 4) return 0;
            return m_arc.View.ReadUInt32(m_begin);
        }
    }

    public ArcViewStream(ArcView arc, long offset, uint size, string name)
    {
        m_arc = arc;
        m_begin = offset;
        m_size = Math.Min(size, arc.MaxOffset - offset);
        m_position = 0;
        Name = name;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (m_position >= m_size) return 0;
        uint toRead = (uint)Math.Min(count, m_size - m_position);
        uint read = m_arc.View.Read(m_begin + m_position, buffer, offset, toRead);
        m_position += read;
        return (int)read;
    }

    public override int ReadByte()
    {
        if (m_position >= m_size) return -1;
        byte b = m_arc.View.ReadByte(m_begin + m_position);
        m_position++;
        return b;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => m_position + offset,
            SeekOrigin.End => m_size + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = target;
        return m_position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public byte[] ReadBytes(int count)
    {
        int available = (int)Math.Min(count, m_size - m_position);
        byte[] buf = new byte[available];
        int read = 0;
        while (read < available)
        {
            int r = Read(buf, read, available - read);
            if (r == 0) break;
            read += r;
        }
        return buf;
    }

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

    public byte[] ReadHeader(int size)
    {
        long pos = Position;
        byte[] data = ReadBytes(size);
        Position = pos;
        return data;
    }

    public string ReadCString(int length, Encoding enc)
    {
        byte[] buf = ReadBytes(length);
        int end = Array.IndexOf<byte>(buf, 0);
        if (end < 0) end = buf.Length;
        return enc.GetString(buf, 0, end);
    }

    public string ReadCString(int length) => ReadCString(length, Encodings.cp932);

    public string ReadCString() => ReadCString(Encodings.cp932);

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

    void IDisposable.Dispose()
    {
        base.Dispose();
    }
}
