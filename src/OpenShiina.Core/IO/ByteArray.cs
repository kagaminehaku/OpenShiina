// ByteArray interfaces and array models
// Adapted from GARbro GameRes/ByteArray.cs and WarcEncryption.cs

namespace OpenShiina.IO;

public interface IByteArray
{
    int Length { get; }
    byte this[int i] { get; }
}

/// <summary>
/// ShiinaImage as in GARbro: the first <c>commonLength</c> bytes of an array shared by many games
/// (the engine's built-in JPEG), followed by a per-game tail.
/// </summary>
public class ImageArray : IByteArray
{
    private readonly byte[] m_common;
    private readonly int m_commonLength;
    private readonly byte[] m_tail;

    public ImageArray(byte[] data) : this(data, data.Length, Array.Empty<byte>())
    {
    }

    public ImageArray(byte[] common, int commonLength, byte[] tail)
    {
        if (commonLength < 0 || commonLength > common.Length)
            throw new ArgumentOutOfRangeException(nameof(commonLength));
        m_common = common;
        m_commonLength = commonLength;
        m_tail = tail;
    }

    public int Length => m_commonLength + m_tail.Length;
    public byte this[int i] => i < m_commonLength ? m_common[i] : m_tail[i - m_commonLength];
}

public class ByteArrayView : IByteArray
{
    private readonly byte[] m_data;
    private readonly int m_offset;
    private readonly int m_length;

    public ByteArrayView(byte[] data, int offset, int length)
    {
        m_data = data;
        m_offset = offset;
        m_length = length;
    }

    public int Length => m_length;
    public byte this[int i] => m_data[m_offset + i];
}
