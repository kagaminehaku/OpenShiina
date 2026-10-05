// ShiinaRio S25 multi-frame image decoder
// Ported from GARbro ArcFormats/ShiinaRio/ImageS25.cs

using System.IO;

namespace OpenShiina.Formats;

public class S25Frame
{
    public int Index { get; set; }
    /// <summary>Position in the file's slot table. The engine groups layers by it (see <see cref="S25Layout"/>).</summary>
    public int Slot { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    /// <summary>The decoded frame, BGRA with straight alpha.</summary>
    public PixelImage Image { get; set; } = null!;
    /// <summary>Decoded BGRA pixels (straight alpha), Width * Height * 4 bytes.</summary>
    public byte[] Pixels => Image.Pixels;
}

/// <summary>Frame header without pixel data: enough to work out the layer layout cheaply.</summary>
/// <param name="Index">Frame number used in output names: counts every non-empty slot, including frames that turn out to be invalid.</param>
public readonly record struct S25FrameInfo(int Index, int Slot, uint Offset, uint Width, uint Height, int OffsetX, int OffsetY);

public class S25Decoder
{
    public static bool IsS25(byte[] data)
    {
        return data.Length >= 8 && data[0] == 'S' && data[1] == '2' && data[2] == '5' && data[3] == 0;
    }

    /// <summary>Reads the slot table and frame headers. Empty slots and invalid frames are skipped.</summary>
    public static List<S25FrameInfo> ReadFrameInfos(byte[] data)
    {
        var infos = new List<S25FrameInfo>();
        if (!IsS25(data)) return infos;

        int count = LittleEndian.ToInt32(data, 4);
        if (count <= 0 || count > 0xfffff || 8 + (long)count * 4 > data.Length) return infos;

        int index = 0;
        for (int slot = 0; slot < count; ++slot)
        {
            uint off = LittleEndian.ToUInt32(data, 8 + slot * 4);
            if (off == 0 || off >= data.Length)
                continue;
            int frameIndex = index++;
            if (off + 0x14L > data.Length)
                continue;
            uint width = LittleEndian.ToUInt32(data, (int)off);
            uint height = LittleEndian.ToUInt32(data, (int)off + 4);
            if (width == 0 || height == 0 || width > 16384 || height > 16384)
                continue;
            infos.Add(new S25FrameInfo(frameIndex, slot, off, width, height,
                LittleEndian.ToInt32(data, (int)off + 8), LittleEndian.ToInt32(data, (int)off + 12)));
        }
        return infos;
    }

    public static List<S25Frame> DecodeAllFrames(byte[] data)
    {
        var frames = new List<S25Frame>();
        using var stream = new BinMemoryStream(data);

        foreach (var info in ReadFrameInfos(data))
        {
            stream.Position = info.Offset + 0x10;
            bool incremental = (stream.ReadUInt32() & 0x80000000u) != 0;

            var reader = new S25Reader(stream, info.Width, info.Height, info.Offset + 0x14, incremental);
            byte[] pixels = reader.Unpack();

            frames.Add(new S25Frame
            {
                Index = info.Index,
                Slot = info.Slot,
                Width = info.Width,
                Height = info.Height,
                OffsetX = info.OffsetX,
                OffsetY = info.OffsetY,
                Image = new PixelImage((int)info.Width, (int)info.Height, PixelLayout.Bgra32, pixels),
            });
        }

        return frames;
    }

    public static PixelImage? DecodeFirstFrame(byte[] data)
    {
        var frames = DecodeAllFrames(data);
        return frames.Count > 0 ? frames[0].Image : null;
    }

    private class S25Reader
    {
        private readonly IBinaryStream m_input;
        private readonly int m_width;
        private readonly int m_height;
        private readonly uint m_origin;
        private readonly bool m_incremental;
        private readonly byte[] m_output;

        public S25Reader(IBinaryStream file, uint width, uint height, uint origin, bool incremental)
        {
            m_input = file;
            m_width = (int)width;
            m_height = (int)height;
            m_origin = origin;
            m_incremental = incremental;
            m_output = new byte[m_width * m_height * 4];
        }

        public byte[] Unpack()
        {
            m_input.Position = m_origin;
            if (m_incremental)
                return UnpackIncremental();

            var rows = new uint[m_height];
            for (int i = 0; i < rows.Length; ++i)
                rows[i] = m_input.ReadUInt32();

            var row_buffer = new byte[m_width];
            int dst = 0;
            for (int y = 0; y < m_height && dst < m_output.Length; ++y)
            {
                uint row_pos = rows[y];
                m_input.Position = row_pos;
                int row_length = m_input.ReadUInt16();
                row_pos += 2;
                if (0 != (row_pos & 1))
                {
                    m_input.ReadByte();
                    --row_length;
                }
                if (row_buffer.Length < row_length)
                    row_buffer = new byte[row_length];
                m_input.Read(row_buffer, 0, row_length);
                dst = UnpackLine(row_buffer, dst);
            }
            return m_output;
        }

        private void UpdateRepeatCount(Dictionary<uint, int> rows_count)
        {
            m_input.Position = 4;
            int count = m_input.ReadInt32();
            var frames = new List<uint>(count);
            for (int i = 0; i < count; ++i)
            {
                var offset = m_input.ReadUInt32();
                if (0 != offset)
                    frames.Add(offset);
            }
            foreach (var offset in frames)
            {
                if (offset + 0x14 == m_origin)
                    continue;
                m_input.Position = offset + 4;
                int height = m_input.ReadInt32();
                m_input.Position = offset + 0x14;
                for (int i = 0; i < height; ++i)
                {
                    var row_offset = m_input.ReadUInt32();
                    if (rows_count.ContainsKey(row_offset))
                        ++rows_count[row_offset];
                }
            }
        }

        private byte[] UnpackIncremental()
        {
            var rows = new uint[m_height];
            var rows_count = new Dictionary<uint, int>(m_height);
            for (int i = 0; i < rows.Length; ++i)
            {
                uint offset = m_input.ReadUInt32();
                rows[i] = offset;
                if (rows_count.ContainsKey(offset))
                    ++rows_count[offset];
                else
                    rows_count[offset] = 1;
            }
            UpdateRepeatCount(rows_count);
            var input_rows = new Dictionary<uint, byte[]>(m_height);
            var input_lines = new byte[m_height][];
            for (int y = 0; y < m_height; ++y)
            {
                uint row_pos = rows[y];
                if (input_rows.TryGetValue(row_pos, out var cachedRow))
                {
                    input_lines[y] = cachedRow;
                    continue;
                }
                var row = ReadLine(row_pos, rows_count[row_pos]);
                input_rows[row_pos] = row;
                input_lines[y] = row;
            }
            int dst = 0;
            foreach (var line in input_lines)
            {
                dst = UnpackLine(line, dst);
            }
            return m_output;
        }

        private int UnpackLine(byte[] line, int dst)
        {
            int row_pos = 0;
            for (int x = m_width; x > 0 && dst < m_output.Length && row_pos < line.Length;)
            {
                if (0 != (row_pos & 1))
                {
                    ++row_pos;
                }
                int count = LittleEndian.ToUInt16(line, row_pos);
                row_pos += 2;
                int method = count >> 13;
                int skip = (count >> 11) & 3;
                if (0 != skip)
                {
                    row_pos += skip;
                }
                count &= 0x7ff;
                if (0 == count)
                {
                    count = LittleEndian.ToInt32(line, row_pos);
                    row_pos += 4;
                }
                if (count > x) count = x;
                x -= count;
                byte b, g, r, a;

                switch (method)
                {
                    case 2:
                        for (int i = 0; i < count && row_pos < line.Length; ++i)
                        {
                            m_output[dst++] = line[row_pos++];
                            m_output[dst++] = line[row_pos++];
                            m_output[dst++] = line[row_pos++];
                            m_output[dst++] = 0xff;
                        }
                        break;
                    case 3:
                        b = line[row_pos++];
                        g = line[row_pos++];
                        r = line[row_pos++];
                        for (int i = 0; i < count; ++i)
                        {
                            m_output[dst++] = b;
                            m_output[dst++] = g;
                            m_output[dst++] = r;
                            m_output[dst++] = 0xff;
                        }
                        break;
                    case 4:
                        for (int i = 0; i < count && row_pos < line.Length; ++i)
                        {
                            a = line[row_pos++];
                            m_output[dst++] = line[row_pos++];
                            m_output[dst++] = line[row_pos++];
                            m_output[dst++] = line[row_pos++];
                            m_output[dst++] = a;
                        }
                        break;
                    case 5:
                        a = line[row_pos++];
                        b = line[row_pos++];
                        g = line[row_pos++];
                        r = line[row_pos++];
                        for (int i = 0; i < count; ++i)
                        {
                            m_output[dst++] = b;
                            m_output[dst++] = g;
                            m_output[dst++] = r;
                            m_output[dst++] = a;
                        }
                        break;
                    default:
                        dst += count * 4;
                        break;
                }
            }
            return dst;
        }

        private byte[] ReadLine(uint offset, int repeat)
        {
            m_input.Position = offset;
            int row_length = m_input.ReadUInt16();
            if (0 != (offset & 1))
            {
                m_input.ReadByte();
                --row_length;
            }
            var row = new byte[row_length];
            m_input.Read(row, 0, row.Length);
            int row_pos = 0;
            for (int x = m_width; x > 0;)
            {
                if (0 != (row_pos & 1))
                {
                    ++row_pos;
                }
                int count = LittleEndian.ToUInt16(row, row_pos);
                row_pos += 2;
                int method = count >> 13;
                int skip = (count >> 11) & 3;
                if (0 != skip)
                {
                    row_pos += skip;
                }
                count &= 0x7ff;
                if (0 == count)
                {
                    count = LittleEndian.ToInt32(row, row_pos);
                    row_pos += 4;
                }
                if (count < 0 || count > x) count = x;
                x -= count;

                switch (method)
                {
                    case 2:
                        for (int j = 0; j < repeat; ++j)
                        {
                            for (int i = 3; i < count * 3 && row_pos + i < row.Length; ++i)
                            {
                                row[row_pos + i] += row[row_pos + i - 3];
                            }
                        }
                        row_pos += count * 3;
                        break;
                    case 3:
                        row_pos += 3;
                        break;
                    case 4:
                        for (int j = 0; j < repeat; ++j)
                        {
                            for (int i = 4; i < count * 4 && row_pos + i < row.Length; ++i)
                            {
                                row[row_pos + i] += row[row_pos + i - 4];
                            }
                        }
                        row_pos += count * 4;
                        break;
                    case 5:
                        row_pos += 4;
                        break;
                    default:
                        break;
                }
            }
            return row;
        }
    }
}
