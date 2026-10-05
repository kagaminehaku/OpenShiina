// Entry models for archive file entries

namespace OpenShiina.IO;

/// <summary>
/// Basic filesystem entry within an archive.
/// </summary>
public class Entry
{
    public virtual string Name { get; set; } = "";
    public virtual string Type { get; set; } = "";
    public long Offset { get; set; } = -1;
    public uint Size { get; set; }
    public virtual uint UnpackedSize { get; set; }
    public virtual bool IsPacked { get; set; }

    public bool CheckPlacement(long maxOffset)
    {
        return Offset < maxOffset && Size <= maxOffset && Offset <= maxOffset - Size;
    }
}

/// <summary>
/// WARC-specific entry with additional metadata.
/// </summary>
public class WarcEntry : Entry
{
    public long FileTime { get; set; }
    public uint Flags { get; set; }
}
