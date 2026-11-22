// Rows of pixels as the window's picture moves them: the display surface's 32-bit rows into the
// picture's 24-bit ones (ScnVm.Paint), those into the hosts' BGRA (GameThread.CopyFrame), and the
// part of a row that differs from another (only that is written and counted as changed). Four
// pixels a step through a byte shuffle (SSSE3's pshufb, NEON's tbl); the rest one at a time.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace OpenShiina.Scripting;

public static class PixelRows
{
    /// <summary>
    /// BGRX (4 bytes a pixel) to BGR: <paramref name="dst"/> takes 3 bytes a pixel and needs 4 bytes
    /// more than that (each step writes 16 bytes, 12 of them kept).
    /// </summary>
    public static void BgrxToBgr(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int pixels = src.Length / 4, x = 0;
        if (dst.Length < pixels * 3)
            throw new ArgumentException("The destination is too short.", nameof(dst));
        if (CanShuffle && dst.Length >= pixels * 3 + 4)
        {
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            var mask = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
            for (; x + 4 <= pixels; x += 4)
                Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(x * 4)), mask).StoreUnsafe(ref d, (nuint)(x * 3));
        }
        for (; x < pixels; x++)
        {
            dst[x * 3] = src[x * 4];
            dst[x * 3 + 1] = src[x * 4 + 1];
            dst[x * 3 + 2] = src[x * 4 + 2];
        }
    }

    /// <summary>BGR to opaque BGRA (A = FF), <paramref name="dst"/> 4 bytes a pixel of <paramref name="src"/>'s.</summary>
    public static void BgrToBgra(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int pixels = src.Length / 3, x = 0;
        if (dst.Length < pixels * 4)
            throw new ArgumentException("The destination is too short.", nameof(dst));
        if (CanShuffle)
        {
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            var mask = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
            var opaque = Vector128.Create(0xFF000000u).AsByte();
            // (each step reads 16 bytes, 12 of them used: the last pixels go one at a time)
            for (; (x + 4) * 3 + 4 <= src.Length; x += 4)
                (Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(x * 3)), mask) | opaque).StoreUnsafe(ref d, (nuint)(x * 4));
        }
        for (; x < pixels; x++)
        {
            dst[x * 4] = src[x * 3];
            dst[x * 4 + 1] = src[x * 3 + 1];
            dst[x * 4 + 2] = src[x * 3 + 2];
            dst[x * 4 + 3] = 0xFF;
        }
    }

    private static bool CanShuffle => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;

    /// <summary>The bytes of <paramref name="v"/> the mask picks (0x80: a zero), one instruction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> v, Vector128<byte> mask) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(v, mask) : AdvSimd.Arm64.VectorTableLookup(v, mask);

    /// <summary>
    /// The bytes where two rows of the same length differ, from the first that differs to after the
    /// last; null when they are the same.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (int First, int Last)? Differs(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int first = a.CommonPrefixLength(b);
        if (first >= a.Length)
            return null;
        // from the end, 64 bytes at a time while they agree
        int last = a.Length;
        while (last - first > 64 && a[(last - 64)..last].SequenceEqual(b[(last - 64)..last]))
            last -= 64;
        while (last > first + 1 && a[last - 1] == b[last - 1])
            last--;
        return (first, last);
    }
}
