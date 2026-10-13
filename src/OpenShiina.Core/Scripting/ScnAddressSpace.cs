// The scripts' memory (docs/engine-notes.md, section 10, "Flat memory"): the whole 32-bit
// address space as one block of virtual memory, a script address being Base + address, physical
// memory used only for the pages written. Linux, Android, macOS: mmap with MAP_NORESERVE, every
// address readable and writable at once; Windows: the 4 GB reserved, each 1 MB part committed on
// first use (commit counts against RAM + page file), which every access checks - a table of 4096
// flags. A 64-bit process only: a 32-bit one has no 4 GB to spare (the players are built 64-bit;
// the 32-bit way, 16 MB parts mapped one at a time, is kept on the branch legacy-32bit).
// Given back memory reads as zero again and gives its pages back to the system (Discard).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenShiina.Scripting;

public sealed unsafe partial class ScnAddressSpace : IDisposable
{
    /// <summary>The 4 GB of the 32-bit addresses.</summary>
    public const long Size = 1L << 32;

    /// <summary>Bytes past the 4 GB, so that an access of a few bytes at the top stays inside.</summary>
    private const long Guard = 1 << 16;

    /// <summary>The parts committed one at a time on Windows: 1 MB (16 MB committed about twice the RAM used).</summary>
    private const int CommitBits = 20;

    private const long CommitSize = 1L << CommitBits;

    private const int OsPage = 4096;

    /// <summary>Windows: parts are committed on first use (elsewhere everything is there at once).</summary>
    private static readonly bool LazyCommit = OperatingSystem.IsWindows();

    private byte* m_base;
    private readonly byte[] m_committed = new byte[1 << (32 - CommitBits)];
    private readonly Lock m_commitLock = new();
    private bool m_released;

    public ScnAddressSpace()
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("OpenShiina runs in a 64-bit process only: the games' 4 GB of memory do not fit in a 32-bit one.");
        if (LazyCommit)
        {
            m_base = (byte*)VirtualAlloc(null, unchecked((nuint)(Size + Guard)), MemReserve, PageNoAccess);
            if (m_base == null)
                throw new OutOfMemoryException($"Could not reserve 4 GB of address space (error {Marshal.GetLastPInvokeError()})");
            // The guard past the top, so that the last part's accesses of a few bytes need no test
            if (VirtualAlloc(m_base + Size, (nuint)Guard, MemCommit, PageReadWrite) == null)
                throw new OutOfMemoryException($"Could not commit script memory (error {Marshal.GetLastPInvokeError()})");
        }
        else
        {
            m_base = (byte*)mmap(null, unchecked((nuint)(Size + Guard)), ProtRead | ProtWrite, MapPrivate | MapAnonymous | MapNoReserve, -1, 0);
            if (m_base == MapFailed)
                throw new OutOfMemoryException($"Could not map 4 GB of address space (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    ~ScnAddressSpace() => Release();

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (m_released)
            return;
        m_released = true;
        if (m_base == null)
            return;
        if (LazyCommit)
            VirtualFree(m_base, 0, MemRelease);
        else
            munmap(m_base, unchecked((nuint)(Size + Guard)));
        m_base = null;
    }

    /// <summary>The memory of script address <paramref name="address"/>, for an access of up to 8 bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* At(uint address)
    {
        if (LazyCommit && Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(m_committed), (int)(address >> CommitBits)) == 0)
            CommitParts(address, 1);
        return m_base + address;
    }

    /// <summary>How many of the <paramref name="length"/> bytes from <paramref name="address"/> are one piece of memory: up to the top of the 4 GB (at least 1).</summary>
    public long Contiguous(uint address, long length) => Math.Min(length, Size - address);

    /// <summary>The memory of [address, address + length), which must not run past the top of the 4 GB (<see cref="Contiguous"/>).</summary>
    public byte* Block(uint address, long length)
    {
        if (length <= 0)
            return At(address);
        if (LazyCommit)
            CommitParts(address, length);
        return m_base + address;
    }

    // Windows: commits the parts [address, address + length) touches, each with the first page
    // of the next, for accesses across the boundary
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CommitParts(uint address, long length)
    {
        long end = Math.Min((long)address + length, Size);
        for (long part = address >> CommitBits; part << CommitBits < end; part++)
        {
            if (Volatile.Read(ref m_committed[part]) != 0)
                continue;
            lock (m_commitLock)
            {
                if (m_committed[part] != 0)
                    continue;
                long start = part << CommitBits;
                long bytes = Math.Min(CommitSize + OsPage, Size - start);
                if (VirtualAlloc(m_base + start, (nuint)bytes, MemCommit, PageReadWrite) == null)
                    throw new OutOfMemoryException($"Could not commit script memory at {start:X8} (error {Marshal.GetLastPInvokeError()})");
                Volatile.Write(ref m_committed[part], 1);
            }
        }
    }

    /// <summary>
    /// [address, address + length) reads as zero again: whole pages go back to the system
    /// (their physical memory freed), the bytes of the pages at either end are cleared.
    /// </summary>
    public void Discard(uint address, long length)
    {
        long start = address, end = Math.Min(start + length, Size);
        if (end <= start)
            return;
        long first = (start + OsPage - 1) & ~(long)(OsPage - 1), last = end & ~(long)(OsPage - 1);
        // small blocks are only cleared: a system call costs more
        if (last - first < 16 * OsPage)
        {
            Clear(start, end - start);
            return;
        }
        Clear(start, first - start);
        Clear(last, end - last);
        if (LazyCommit)
        {
            // committed parts give their pages back; of the others only the first page can
            // hold anything (committed with the part before)
            for (long at = first; at < last; )
            {
                long part = at >> CommitBits, partEnd = Math.Min((part + 1) << CommitBits, last);
                if (m_committed[part] != 0)
                    GiveBack(m_base + at, partEnd - at);
                else
                    Clear(at, partEnd - at);
                at = partEnd;
            }
        }
        else
            GiveBack(m_base + first, last - first);
    }

    // Whole pages (committed, or mapped) go back to the system and read as zero again
    private static void GiveBack(byte* p, long length)
    {
        if (OperatingSystem.IsWindows())
        {
            VirtualFree(p, (nuint)length, MemDecommit);
            if (VirtualAlloc(p, (nuint)length, MemCommit, PageReadWrite) == null)
                throw new OutOfMemoryException($"Could not commit script memory (error {Marshal.GetLastPInvokeError()})");
        }
        else if (mmap(p, (nuint)length, ProtRead | ProtWrite, MapPrivate | MapAnonymous | MapNoReserve | MapFixed, -1, 0) == MapFailed)
            throw new OutOfMemoryException($"Could not map script memory (errno {Marshal.GetLastPInvokeError()})");
    }

    /// <summary>Zeroes [start, start + length) where memory is there (Windows: nothing is to clear in an uncommitted part).</summary>
    private void Clear(long start, long length)
    {
        long end = start + length;
        while (start < end)
        {
            long part = start >> CommitBits, partStart = part << CommitBits;
            long n = Math.Min(end, partStart + CommitSize) - start;
            long clear = n;
            if (LazyCommit && m_committed[part] == 0)
                // only the first page, when the part before is committed
                clear = part > 0 && m_committed[part - 1] != 0 ? Math.Max(0, Math.Min(n, partStart + OsPage - start)) : 0;
            if (clear > 0)
                new Span<byte>(m_base + start, (int)clear).Clear();
            start += n;
        }
    }

    #region System calls

    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemDecommit = 0x4000, MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01, PageReadWrite = 0x04;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* address, nuint size, uint type);

    private const int ProtRead = 1, ProtWrite = 2;
    private const int MapPrivate = 0x02;
    private static readonly int MapFixed = 0x10;
    private static readonly int MapAnonymous = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? 0x1000 : 0x20;
    private static readonly int MapNoReserve = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? 0x40 : 0x4000;
    private static readonly void* MapFailed = (void*)-1;

    [LibraryImport("libc", SetLastError = true)]
    private static partial void* mmap(void* address, nuint length, int protect, int flags, int fd, nint offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(void* address, nuint length);

    #endregion
}
