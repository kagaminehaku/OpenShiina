// The operations of x86 instructions for C# translated from the x86 routines embedded in SCN
// modules (Scripting/Generated, made by tools/X86Gen): the same semantics as X86Cpu, so a
// translated routine writes the bytes the interpreter (and the game's own code) writes.
// Flags kept: carry, zero, sign, overflow (parity and adjust are not used by the routines).

using System.Runtime.CompilerServices;

namespace OpenShiina.Scripting;

public static class X86Ops
{
    #region Memory

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint R8(ScnVm vm, uint a) => vm.ReadByte((int)a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint R16(ScnVm vm, uint a) => vm.Read16((int)a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint R32(ScnVm vm, uint a) => (uint)vm.Read32((int)a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong R64(ScnVm vm, uint a) => (uint)vm.Read32((int)a) | (ulong)(uint)vm.Read32((int)a + 4) << 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void W8(ScnVm vm, uint a, uint v) => vm.WriteByte((int)a, (byte)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void W16(ScnVm vm, uint a, uint v) => vm.Write16((int)a, (int)(v & 0xFFFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void W32(ScnVm vm, uint a, uint v) => vm.Write32((int)a, (int)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void W64(ScnVm vm, uint a, ulong v)
    {
        vm.Write32((int)a, (int)v);
        vm.Write32((int)a + 4, (int)(v >> 32));
    }

    #endregion

    #region Integer

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Mask(int size) => size == 4 ? 0xFFFFFFFF : (1u << (8 * size)) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sign(int size) => 1u << (8 * size - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Result(uint r, int size, ref bool zf, ref bool sf)
    {
        zf = (r & Mask(size)) == 0;
        sf = (r & Sign(size)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Add(uint a, uint b, uint carry, int size, ref bool cf, ref bool zf, ref bool sf, ref bool of)
    {
        uint m = Mask(size);
        ulong wide = (ulong)(a & m) + (b & m) + carry;
        uint r = (uint)wide & m;
        cf = wide > m;
        of = ((a ^ r) & (b ^ r) & Sign(size)) != 0;
        Result(r, size, ref zf, ref sf);
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Sub(uint a, uint b, uint borrow, int size, ref bool cf, ref bool zf, ref bool sf, ref bool of)
    {
        uint m = Mask(size);
        a &= m;
        b &= m;
        uint r = (a - b - borrow) & m;
        cf = (ulong)a < (ulong)b + borrow;
        of = ((a ^ b) & (a ^ r) & Sign(size)) != 0;
        Result(r, size, ref zf, ref sf);
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Logic(uint r, int size, ref bool cf, ref bool zf, ref bool sf, ref bool of)
    {
        cf = of = false;
        Result(r, size, ref zf, ref sf);
        return r & Mask(size);
    }

    /// <summary>shl / shr / sar / rol / ror (kind 0-4) by a count already masked to 0-31.</summary>
    public static uint Shift(int kind, uint a, int count, int size, ref bool cf, ref bool zf, ref bool sf, ref bool of)
    {
        if (count == 0)
            return a;
        int bits = 8 * size;
        uint m = Mask(size), r;
        a &= m;
        switch (kind)
        {
            case 0:
                r = count >= 32 ? 0 : (a << count) & m;
                cf = count <= bits && ((a >> (bits - count)) & 1) != 0;
                of = ((r & Sign(size)) != 0) != cf;
                Result(r, size, ref zf, ref sf);
                return r;
            case 1:
                r = a >> count;
                cf = ((a >> (count - 1)) & 1) != 0;
                of = (a & Sign(size)) != 0;
                Result(r, size, ref zf, ref sf);
                return r;
            case 2:
            {
                int s = size == 4 ? (int)a : size == 2 ? (short)a : (sbyte)a;
                r = (uint)(s >> Math.Min(count, bits - 1)) & m;
                cf = ((s >> (count - 1)) & 1) != 0;
                of = false;
                Result(r, size, ref zf, ref sf);
                return r;
            }
            case 3:
            {
                int c = count % bits;
                r = ((a << c) | (a >> ((bits - c) % bits))) & m;
                cf = (r & 1) != 0;
                of = ((r & Sign(size)) != 0) != cf;
                return r;
            }
            default:
            {
                int c = count % bits;
                r = ((a >> c) | (a << ((bits - c) % bits))) & m;
                cf = (r & Sign(size)) != 0;
                of = cf != ((r & (Sign(size) >> 1)) != 0);
                return r;
            }
        }
    }

    /// <summary>mul r/m32: edx:eax = eax * v.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mul32(ref uint eax, ref uint edx, uint v, ref bool cf, ref bool of)
    {
        ulong p = (ulong)eax * v;
        eax = (uint)p;
        edx = (uint)(p >> 32);
        cf = of = edx != 0;
    }

    /// <summary>imul r/m32 (one operand): edx:eax = eax * v, signed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Imul32(ref uint eax, ref uint edx, uint v, ref bool cf, ref bool of)
    {
        long p = (long)(int)eax * (int)v;
        eax = (uint)p;
        edx = (uint)((ulong)p >> 32);
        cf = of = p != (int)p;
    }

    /// <summary>imul r, r/m[, imm] (32 or 16 bits): the low part of the signed product.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Imul(uint a, uint b, int size, ref bool cf, ref bool zf, ref bool sf, ref bool of)
    {
        long x = (int)a, y = (int)b;
        if (size == 2)
        {
            x = (short)x;
            y = (short)y;
        }
        long p = x * y;
        uint r = (uint)p & Mask(size);
        cf = of = size == 2 ? p != (short)p : p != (int)p;
        Result(r, size, ref zf, ref sf);
        return r;
    }

    /// <summary>div r/m32: eax = edx:eax / v, edx = the remainder.</summary>
    public static void Div32(ref uint eax, ref uint edx, uint v)
    {
        if (v == 0)
            throw new DivideByZeroException("x86 divide by zero");
        ulong d = (ulong)edx << 32 | eax;
        ulong q = d / v;
        if (q > uint.MaxValue)
            throw new OverflowException("x86 divide overflow");
        eax = (uint)q;
        edx = (uint)(d % v);
    }

    #endregion

    #region MMX

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort W(ulong v, int k) => (ushort)(v >> (16 * k));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Words(int w0, int w1, int w2, int w3) =>
        (ushort)w0 | (ulong)(ushort)w1 << 16 | (ulong)(ushort)w2 << 32 | (ulong)(ushort)w3 << 48;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Paddw(ulong a, ulong b) =>
        Words(W(a, 0) + W(b, 0), W(a, 1) + W(b, 1), W(a, 2) + W(b, 2), W(a, 3) + W(b, 3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Psubw(ulong a, ulong b) =>
        Words(W(a, 0) - W(b, 0), W(a, 1) - W(b, 1), W(a, 2) - W(b, 2), W(a, 3) - W(b, 3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pmullw(ulong a, ulong b) =>
        Words(W(a, 0) * W(b, 0), W(a, 1) * W(b, 1), W(a, 2) * W(b, 2), W(a, 3) * W(b, 3));

    public static ulong Pmulhw(ulong a, ulong b) =>
        Words((short)W(a, 0) * (short)W(b, 0) >> 16, (short)W(a, 1) * (short)W(b, 1) >> 16,
              (short)W(a, 2) * (short)W(b, 2) >> 16, (short)W(a, 3) * (short)W(b, 3) >> 16);

    public static ulong Pmaddwd(ulong a, ulong b)
    {
        int lo = (short)W(a, 0) * (short)W(b, 0) + (short)W(a, 1) * (short)W(b, 1);
        int hi = (short)W(a, 2) * (short)W(b, 2) + (short)W(a, 3) * (short)W(b, 3);
        return (uint)lo | (ulong)(uint)hi << 32;
    }

    public static ulong Paddb(ulong a, ulong b)
    {
        // Byte lanes without carries between them
        ulong low = (a & 0x7F7F7F7F7F7F7F7F) + (b & 0x7F7F7F7F7F7F7F7F);
        return low ^ ((a ^ b) & 0x8080808080808080);
    }

    public static ulong Psubb(ulong a, ulong b)
    {
        ulong r = 0;
        for (int s = 0; s < 64; s += 8)
            r |= (ulong)(byte)((byte)(a >> s) - (byte)(b >> s)) << s;
        return r;
    }

    public static ulong Paddd(ulong a, ulong b) => (uint)((uint)a + (uint)b) | (ulong)((uint)(a >> 32) + (uint)(b >> 32)) << 32;

    public static ulong Psubd(ulong a, ulong b) => (uint)((uint)a - (uint)b) | (ulong)((uint)(a >> 32) - (uint)(b >> 32)) << 32;

    private static ulong ByteLanes(ulong a, ulong b, Func<int, int, int> f)
    {
        ulong r = 0;
        for (int s = 0; s < 64; s += 8)
            r |= (ulong)(byte)f((byte)(a >> s), (byte)(b >> s)) << s;
        return r;
    }

    private static ulong SignedByteLanes(ulong a, ulong b, Func<int, int, int> f)
    {
        ulong r = 0;
        for (int s = 0; s < 64; s += 8)
            r |= (ulong)(byte)f((sbyte)(a >> s), (sbyte)(b >> s)) << s;
        return r;
    }

    public static ulong Paddusb(ulong a, ulong b) => ByteLanes(a, b, (x, y) => Math.Min(x + y, 255));
    public static ulong Psubusb(ulong a, ulong b) => ByteLanes(a, b, (x, y) => Math.Max(x - y, 0));
    public static ulong Paddsb(ulong a, ulong b) => SignedByteLanes(a, b, (x, y) => Math.Clamp(x + y, -128, 127));
    public static ulong Psubsb(ulong a, ulong b) => SignedByteLanes(a, b, (x, y) => Math.Clamp(x - y, -128, 127));

    public static ulong Paddusw(ulong a, ulong b) =>
        Words(Math.Min(W(a, 0) + W(b, 0), 0xFFFF), Math.Min(W(a, 1) + W(b, 1), 0xFFFF),
              Math.Min(W(a, 2) + W(b, 2), 0xFFFF), Math.Min(W(a, 3) + W(b, 3), 0xFFFF));

    public static ulong Psubusw(ulong a, ulong b) =>
        Words(Math.Max(W(a, 0) - W(b, 0), 0), Math.Max(W(a, 1) - W(b, 1), 0),
              Math.Max(W(a, 2) - W(b, 2), 0), Math.Max(W(a, 3) - W(b, 3), 0));

    public static ulong Paddsw(ulong a, ulong b) =>
        Words(Math.Clamp((short)W(a, 0) + (short)W(b, 0), -32768, 32767), Math.Clamp((short)W(a, 1) + (short)W(b, 1), -32768, 32767),
              Math.Clamp((short)W(a, 2) + (short)W(b, 2), -32768, 32767), Math.Clamp((short)W(a, 3) + (short)W(b, 3), -32768, 32767));

    public static ulong Psubsw(ulong a, ulong b) =>
        Words(Math.Clamp((short)W(a, 0) - (short)W(b, 0), -32768, 32767), Math.Clamp((short)W(a, 1) - (short)W(b, 1), -32768, 32767),
              Math.Clamp((short)W(a, 2) - (short)W(b, 2), -32768, 32767), Math.Clamp((short)W(a, 3) - (short)W(b, 3), -32768, 32767));

    public static ulong Pcmpeqb(ulong a, ulong b) => ByteLanes(a, b, (x, y) => x == y ? 0xFF : 0);
    public static ulong Pcmpgtb(ulong a, ulong b) => SignedByteLanes(a, b, (x, y) => x > y ? 0xFF : 0);

    public static ulong Pcmpeqw(ulong a, ulong b) =>
        Words(W(a, 0) == W(b, 0) ? -1 : 0, W(a, 1) == W(b, 1) ? -1 : 0, W(a, 2) == W(b, 2) ? -1 : 0, W(a, 3) == W(b, 3) ? -1 : 0);

    public static ulong Pcmpgtw(ulong a, ulong b) =>
        Words((short)W(a, 0) > (short)W(b, 0) ? -1 : 0, (short)W(a, 1) > (short)W(b, 1) ? -1 : 0,
              (short)W(a, 2) > (short)W(b, 2) ? -1 : 0, (short)W(a, 3) > (short)W(b, 3) ? -1 : 0);

    public static ulong Pcmpeqd(ulong a, ulong b) =>
        ((uint)a == (uint)b ? 0xFFFFFFFFul : 0) | ((uint)(a >> 32) == (uint)(b >> 32) ? 0xFFFFFFFF00000000ul : 0);

    public static ulong Pcmpgtd(ulong a, ulong b) =>
        ((int)a > (int)b ? 0xFFFFFFFFul : 0) | ((int)(a >> 32) > (int)(b >> 32) ? 0xFFFFFFFF00000000ul : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Psrlw(ulong a, ulong count) => count > 15 ? 0 : (a >> (int)count) & (0xFFFFul >> (int)count) * 0x0001000100010001ul;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Psllw(ulong a, ulong count) => count > 15 ? 0 : (a << (int)count) & ((0xFFFFul << (int)count) & 0xFFFF) * 0x0001000100010001ul;

    public static ulong Psraw(ulong a, ulong count)
    {
        int c = count > 15 ? 15 : (int)count;
        return Words((short)W(a, 0) >> c, (short)W(a, 1) >> c, (short)W(a, 2) >> c, (short)W(a, 3) >> c);
    }

    public static ulong Psrld(ulong a, ulong count) => count > 31 ? 0 : ((uint)a >> (int)count) | (ulong)((uint)(a >> 32) >> (int)count) << 32;

    public static ulong Pslld(ulong a, ulong count) => count > 31 ? 0 : ((uint)a << (int)count) | (ulong)((uint)(a >> 32) << (int)count) << 32;

    public static ulong Psrad(ulong a, ulong count)
    {
        int c = count > 31 ? 31 : (int)count;
        return (uint)((int)a >> c) | (ulong)(uint)((int)(a >> 32) >> c) << 32;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Psrlq(ulong a, ulong count) => count > 63 ? 0 : a >> (int)count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Psllq(ulong a, ulong count) => count > 63 ? 0 : a << (int)count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Punpcklbw(ulong a, ulong b) => SpreadBytes(a) | SpreadBytes(b) << 8;

    /// <summary>The low 4 bytes of v, one in the low byte of each 16-bit lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SpreadBytes(ulong v)
    {
        v &= 0xFFFFFFFF;
        v = (v | v << 16) & 0x0000FFFF0000FFFF;
        return (v | v << 8) & 0x00FF00FF00FF00FF;
    }

    public static ulong Punpckhbw(ulong a, ulong b) => Punpcklbw(a >> 32, b >> 32);

    public static ulong Punpcklwd(ulong a, ulong b) =>
        (a & 0xFFFF) | (b & 0xFFFF) << 16 | ((a >> 16) & 0xFFFF) << 32 | ((b >> 16) & 0xFFFF) << 48;

    public static ulong Punpckhwd(ulong a, ulong b) => Punpcklwd(a >> 32, b >> 32);

    public static ulong Punpckldq(ulong a, ulong b) => (uint)a | (ulong)(uint)b << 32;

    public static ulong Punpckhdq(ulong a, ulong b) => (a >> 32) | (b >> 32) << 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Packuswb(ulong a, ulong b) => PackUnsigned(a) | PackUnsigned(b) << 32;

    /// <summary>The four signed 16-bit lanes of v saturated to unsigned bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackUnsigned(ulong v)
    {
        // Fast path: every lane already 0-255
        if ((v & 0xFF00FF00FF00FF00) == 0)
        {
            v = (v | v >> 8) & 0x0000FFFF0000FFFF;
            return (v | v >> 16) & 0xFFFFFFFF;
        }
        return Saturate((short)v) | Saturate((short)(v >> 16)) << 8 | Saturate((short)(v >> 32)) << 16 | Saturate((short)(v >> 48)) << 24;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Saturate(short w) => w < 0 ? 0ul : w > 255 ? 255ul : (ulong)w;

    public static ulong Packsswb(ulong a, ulong b)
    {
        ulong r = 0;
        for (int k = 0; k < 4; k++)
        {
            r |= (ulong)(byte)(sbyte)Math.Clamp((int)(short)W(a, k), -128, 127) << (8 * k);
            r |= (ulong)(byte)(sbyte)Math.Clamp((int)(short)W(b, k), -128, 127) << (8 * k + 32);
        }
        return r;
    }

    public static ulong Packssdw(ulong a, ulong b) =>
        Words(Math.Clamp((int)a, -32768, 32767), Math.Clamp((int)(a >> 32), -32768, 32767),
              Math.Clamp((int)b, -32768, 32767), Math.Clamp((int)(b >> 32), -32768, 32767));

    #endregion
}
