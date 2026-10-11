// The SSE / SSE2 instructions of the routines of a START that will not run without SSE2 (Bitch
// Nee-chan's: its scaling copy and its blur, 2026-10-11): xmm registers as two 64-bit halves, the
// integer lanes as the MMX ones on each half, the floats as .NET's single precision (the default
// MXCSR: round to nearest even, no flush to zero). rcpps is exact here; a real CPU gives about 12
// bits of 1 / x, so a pixel computed from it may come out a step off the game's.

using Iced.Intel;

namespace OpenShiina.Scripting;

public sealed partial class X86Cpu
{
    // xmm0-7: the low half at 2n, the high half at 2n + 1
    public readonly ulong[] Xmm = new ulong[16];

    private static bool IsXmm(in Instruction ins, int i) => i < ins.OpCount && ins.GetOpKind(i) == OpKind.Register && ins.GetOpRegister(i).IsXMM();

    private (ulong Lo, ulong Hi) GetX(in Instruction ins, int i)
    {
        switch (ins.GetOpKind(i))
        {
            case OpKind.Register when ins.GetOpRegister(i).IsXMM():
            {
                int n = ins.GetOpRegister(i) - Register.XMM0;
                return (Xmm[2 * n], Xmm[2 * n + 1]);
            }
            case OpKind.Register:
                return (Reg(ins.GetOpRegister(i)), 0);
            case OpKind.Memory:
            {
                uint a = Address(ins);
                return ins.MemorySize.GetSize() switch
                {
                    4 => (Load(a, 4), 0),
                    8 => (Load64(a), 0),
                    _ => (Load64(a), Load64(a + 8)),
                };
            }
            default:
                return (ins.GetImmediate(i), 0);
        }
    }

    private void SetX(in Instruction ins, int i, (ulong Lo, ulong Hi) v)
    {
        if (ins.GetOpKind(i) == OpKind.Register)
        {
            int n = ins.GetOpRegister(i) - Register.XMM0;
            Xmm[2 * n] = v.Lo;
            Xmm[2 * n + 1] = v.Hi;
            return;
        }
        uint a = Address(ins);
        Store64(a, v.Lo);
        if (ins.MemorySize.GetSize() > 8)
            Store64(a + 8, v.Hi);
    }

    /// <summary>Runs an instruction with an xmm operand; false for one not written here.</summary>
    private bool Sse(in Instruction ins)
    {
        if (!IsXmm(ins, 0) && !IsXmm(ins, 1))
            return false;
        switch (ins.Mnemonic)
        {
            case Mnemonic.Movd:
                if (IsXmm(ins, 0))
                    SetX(ins, 0, ((uint)GetX(ins, 1).Lo, 0));
                else
                    Set(ins, 0, (uint)GetX(ins, 1).Lo);
                return true;
            case Mnemonic.Movq:
                if (IsXmm(ins, 0))
                    SetX(ins, 0, (GetX(ins, 1).Lo, 0));
                else
                    Store64(Address(ins), GetX(ins, 1).Lo);
                return true;
            case Mnemonic.Movaps or Mnemonic.Movups or Mnemonic.Movdqa or Mnemonic.Movdqu or Mnemonic.Movapd or Mnemonic.Movupd:
                SetX(ins, 0, GetX(ins, 1));
                return true;
        }
        var a = GetX(ins, 0);
        var b = ins.OpCount > 1 ? GetX(ins, 1) : default;
        int imm = ins.OpCount > 2 ? (int)ins.GetImmediate(2) : 0;
        if (!SseCompute(ins.Mnemonic, a, b, imm, out var r))
            return false;
        SetX(ins, 0, r);
        return true;
    }

    /// <summary>
    /// The result of an SSE instruction other than a move from its operands (<paramref name="imm"/>
    /// the third, for pshufd / shufps); false for one not written here. The interpreter and
    /// X86Jit both use it, so a translated routine writes the interpreter's bytes.
    /// </summary>
    public static bool SseCompute(Mnemonic mnemonic, (ulong Lo, ulong Hi) a, (ulong Lo, ulong Hi) b, int imm, out (ulong Lo, ulong Hi) r)
    {
        switch (mnemonic)
        {
            // Bits and integer lanes: the MMX ones on each half
            case Mnemonic.Pxor or Mnemonic.Xorps or Mnemonic.Xorpd: r = (a.Lo ^ b.Lo, a.Hi ^ b.Hi); break;
            case Mnemonic.Por or Mnemonic.Orps or Mnemonic.Orpd: r = (a.Lo | b.Lo, a.Hi | b.Hi); break;
            case Mnemonic.Pand or Mnemonic.Andps or Mnemonic.Andpd: r = (a.Lo & b.Lo, a.Hi & b.Hi); break;
            case Mnemonic.Pandn or Mnemonic.Andnps or Mnemonic.Andnpd: r = (~a.Lo & b.Lo, ~a.Hi & b.Hi); break;
            case Mnemonic.Paddb: r = Halves(a, b, 8, (x, y) => x + y, false); break;
            case Mnemonic.Paddw: r = Halves(a, b, 16, (x, y) => x + y, false); break;
            case Mnemonic.Paddd: r = Halves(a, b, 32, (x, y) => x + y, false); break;
            case Mnemonic.Paddq: r = (a.Lo + b.Lo, a.Hi + b.Hi); break;
            case Mnemonic.Psubb: r = Halves(a, b, 8, (x, y) => x - y, false); break;
            case Mnemonic.Psubw: r = Halves(a, b, 16, (x, y) => x - y, false); break;
            case Mnemonic.Psubd: r = Halves(a, b, 32, (x, y) => x - y, false); break;
            case Mnemonic.Psubq: r = (a.Lo - b.Lo, a.Hi - b.Hi); break;
            case Mnemonic.Paddusb: r = Halves(a, b, 8, (x, y) => Saturate(x + y, 0, 255), false); break;
            case Mnemonic.Paddusw: r = Halves(a, b, 16, (x, y) => Saturate(x + y, 0, 65535), false); break;
            case Mnemonic.Psubusb: r = Halves(a, b, 8, (x, y) => Saturate(x - y, 0, 255), false); break;
            case Mnemonic.Psubusw: r = Halves(a, b, 16, (x, y) => Saturate(x - y, 0, 65535), false); break;
            case Mnemonic.Paddsw: r = Halves(a, b, 16, (x, y) => Saturate(x + y, -32768, 32767), true); break;
            case Mnemonic.Psubsw: r = Halves(a, b, 16, (x, y) => Saturate(x - y, -32768, 32767), true); break;
            case Mnemonic.Pmullw: r = Halves(a, b, 16, (x, y) => x * y, true); break;
            case Mnemonic.Pmulhw: r = Halves(a, b, 16, (x, y) => (x * y) >> 16, true); break;
            case Mnemonic.Pmulhuw: r = Halves(a, b, 16, (x, y) => (x * y) >> 16, false); break;
            case Mnemonic.Psllw: r = ShiftHalves(a, b, 16, true, false); break;
            case Mnemonic.Pslld: r = ShiftHalves(a, b, 32, true, false); break;
            case Mnemonic.Psllq: r = ShiftHalves(a, b, 64, true, false); break;
            case Mnemonic.Psrlw: r = ShiftHalves(a, b, 16, false, false); break;
            case Mnemonic.Psrld: r = ShiftHalves(a, b, 32, false, false); break;
            case Mnemonic.Psrlq: r = ShiftHalves(a, b, 64, false, false); break;
            case Mnemonic.Psraw: r = ShiftHalves(a, b, 16, false, true); break;
            case Mnemonic.Psrad: r = ShiftHalves(a, b, 32, false, true); break;
            case Mnemonic.Pslldq: r = ShiftBytes(a, (int)Math.Min(b.Lo, 16), left: true); break;
            case Mnemonic.Psrldq: r = ShiftBytes(a, (int)Math.Min(b.Lo, 16), left: false); break;
            // Interleaving: the low (or high) halves of the two
            case Mnemonic.Punpcklbw: r = Unpack(a.Lo, b.Lo, 8); break;
            case Mnemonic.Punpcklwd: r = Unpack(a.Lo, b.Lo, 16); break;
            case Mnemonic.Punpckldq or Mnemonic.Unpcklps: r = Unpack(a.Lo, b.Lo, 32); break;
            case Mnemonic.Punpcklqdq: r = (a.Lo, b.Lo); break;
            case Mnemonic.Punpckhbw: r = Unpack(a.Hi, b.Hi, 8); break;
            case Mnemonic.Punpckhwd: r = Unpack(a.Hi, b.Hi, 16); break;
            case Mnemonic.Punpckhdq or Mnemonic.Unpckhps: r = Unpack(a.Hi, b.Hi, 32); break;
            case Mnemonic.Punpckhqdq: r = (a.Hi, b.Hi); break;
            // Packing: the first's lanes into the low half, the second's into the high one
            case Mnemonic.Packuswb: r = (Pack(a.Lo, a.Hi, 16, 0, 255), Pack(b.Lo, b.Hi, 16, 0, 255)); break;
            case Mnemonic.Packsswb: r = (Pack(a.Lo, a.Hi, 16, -128, 127), Pack(b.Lo, b.Hi, 16, -128, 127)); break;
            case Mnemonic.Packssdw: r = (Pack(a.Lo, a.Hi, 32, -32768, 32767), Pack(b.Lo, b.Hi, 32, -32768, 32767)); break;
            case Mnemonic.Pshufd:
            {
                int order = imm;
                r = Dwords(k => Dword(b, (order >> (2 * k)) & 3));
                break;
            }
            // Single-precision floats
            case Mnemonic.Cvtdq2ps: r = Dwords(k => Bits((float)(int)Dword(b, k))); break;
            case Mnemonic.Cvtps2dq: r = Dwords(k => ToInt(MathF.Round(Float(b, k), MidpointRounding.ToEven))); break;
            case Mnemonic.Cvttps2dq: r = Dwords(k => ToInt(MathF.Truncate(Float(b, k)))); break;
            case Mnemonic.Addps: r = Dwords(k => Bits(Float(a, k) + Float(b, k))); break;
            case Mnemonic.Subps: r = Dwords(k => Bits(Float(a, k) - Float(b, k))); break;
            case Mnemonic.Mulps: r = Dwords(k => Bits(Float(a, k) * Float(b, k))); break;
            case Mnemonic.Divps: r = Dwords(k => Bits(Float(a, k) / Float(b, k))); break;
            case Mnemonic.Minps: r = Dwords(k => Float(a, k) < Float(b, k) ? Dword(a, k) : Dword(b, k)); break;
            case Mnemonic.Maxps: r = Dwords(k => Float(a, k) > Float(b, k) ? Dword(a, k) : Dword(b, k)); break;
            case Mnemonic.Sqrtps: r = Dwords(k => Bits(MathF.Sqrt(Float(b, k)))); break;
            case Mnemonic.Rcpps: r = Dwords(k => Bits(1 / Float(b, k))); break;
            case Mnemonic.Rsqrtps: r = Dwords(k => Bits(1 / MathF.Sqrt(Float(b, k)))); break;
            case Mnemonic.Shufps:
            {
                int order = imm;
                r = Dwords(k => Dword(k < 2 ? a : b, (order >> (2 * k)) & 3));
                break;
            }
            default:
                r = default;
                return false;
        }
        return true;
    }

    /// <summary>The instructions <see cref="SseCompute"/> takes.</summary>
    public static bool SseComputes(Mnemonic mnemonic) => SseCompute(mnemonic, default, (1, 0), 0, out _);

    private static (ulong, ulong) Halves((ulong Lo, ulong Hi) a, (ulong Lo, ulong Hi) b, int bits, Func<long, long, long> f, bool signed) =>
        (Lanes(a.Lo, b.Lo, bits, f, signed), Lanes(a.Hi, b.Hi, bits, f, signed));

    // A count from an xmm register is its low 64 bits; one from an immediate is the byte
    private static (ulong, ulong) ShiftHalves((ulong Lo, ulong Hi) a, (ulong Lo, ulong Hi) count, int bits, bool left, bool arithmetic) =>
        (ShiftLanes(a.Lo, count.Lo, bits, left, arithmetic), ShiftLanes(a.Hi, count.Lo, bits, left, arithmetic));

    private static (ulong, ulong) ShiftBytes((ulong Lo, ulong Hi) a, int bytes, bool left)
    {
        var v = (UInt128)a.Hi << 64 | a.Lo;
        v = bytes >= 16 ? 0 : left ? v << (8 * bytes) : v >> (8 * bytes);
        return ((ulong)v, (ulong)(v >> 64));
    }

    // The low 32 bits of a and b interleaved make the low half, their high 32 bits the high one
    private static (ulong, ulong) Unpack(ulong a, ulong b, int bits) =>
        (Interleave(a, b, bits, high: false), Interleave(a, b, bits, high: true));

    private static uint Dword((ulong Lo, ulong Hi) v, int k) => (uint)((k < 2 ? v.Lo : v.Hi) >> (32 * (k & 1)));

    private static float Float((ulong Lo, ulong Hi) v, int k) => BitConverter.UInt32BitsToSingle(Dword(v, k));

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    // cvt(t)ps2dq: out of range or NaN gives the "integer indefinite" 0x80000000
    private static uint ToInt(float f) => f >= -2147483648f && f < 2147483648f ? (uint)(int)f : 0x80000000;

    private static (ulong, ulong) Dwords(Func<int, uint> lane) =>
        (lane(0) | (ulong)lane(1) << 32, lane(2) | (ulong)lane(3) << 32);
}
