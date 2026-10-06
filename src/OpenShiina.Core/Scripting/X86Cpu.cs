// An x86 (32-bit) interpreter for the machine code inside SCN modules (op_0276). The routines are
// part of the game's own scripts: picture kernels written with MMX, string helpers, CPUID. They
// run here on the interpreter's flat memory, so every byte they write is the byte the game's
// own code writes. Decoding is Iced's; execution covers the integer, string and MMX instructions
// the 11 games use. CPUID reports an Intel CPU with MMX and without SSE, so the scripts choose
// their MMX paths (their SSE paths are never run).

using Iced.Intel;

namespace OpenShiina.Scripting;

public sealed class X86Cpu
{
    private readonly ScnVm m_vm;
    private readonly Dictionary<uint, Instruction> m_code = new();
    private readonly VmCodeReader m_reader;

    // eax ecx edx ebx esp ebp esi edi
    public readonly uint[] R = new uint[8];
    public readonly ulong[] MM = new ulong[8];
    public uint Eip;
    private bool m_cf, m_pf, m_af, m_zf, m_sf, m_df, m_of;

    /// <summary>Instructions run in all, for the boot report.</summary>
    public long Executed { get; private set; }

    public X86Cpu(ScnVm vm)
    {
        m_vm = vm;
        m_reader = new VmCodeReader(vm);
    }

    /// <summary>Code was loaded again: forget decoded instructions.</summary>
    public void Forget() => m_code.Clear();

    private sealed class VmCodeReader(ScnVm vm) : CodeReader
    {
        public uint At;
        public override int ReadByte() => vm.ReadByte((int)At++);
    }

    private Instruction Fetch(uint ip)
    {
        if (m_code.TryGetValue(ip, out var ins))
            return ins;
        m_reader.At = ip;
        var decoder = Iced.Intel.Decoder.Create(32, m_reader, ip);
        decoder.Decode(out ins);
        if (ins.IsInvalid)
            throw new InvalidOperationException($"Invalid x86 code at {ip:X8}");
        m_code[ip] = ins;
        return ins;
    }

    /// <summary>
    /// Calls the routine at <paramref name="entry"/> with one stack argument, the way the engine
    /// does (stdcall), on a stack at <paramref name="stackTop"/>; returns when it returns.
    /// </summary>
    public void Call(uint entry, uint argument, uint stackTop, long limit = 2_000_000_000)
    {
        const uint Sentinel = 0xFFFFFFF0;
        R[4] = stackTop;
        Push(argument);
        Push(Sentinel);
        Eip = entry;
        m_df = false;
        for (long n = 0; Eip != Sentinel; n++)
        {
            if (n >= limit)
                throw new InvalidOperationException($"x86 routine at {entry:X8} did not return");
            Step();
        }
    }

    #region Registers and memory

    private uint Reg(Register r) => r switch
    {
        >= Register.EAX and <= Register.EDI => R[r - Register.EAX],
        >= Register.AX and <= Register.DI => R[r - Register.AX] & 0xFFFF,
        >= Register.AL and <= Register.BL => R[r - Register.AL] & 0xFF,
        >= Register.AH and <= Register.BH => (R[r - Register.AH] >> 8) & 0xFF,
        _ => throw new NotSupportedException($"x86 register {r}"),
    };

    private void SetReg(Register r, uint v)
    {
        switch (r)
        {
            case >= Register.EAX and <= Register.EDI:
                R[r - Register.EAX] = v;
                break;
            case >= Register.AX and <= Register.DI:
                R[r - Register.AX] = (R[r - Register.AX] & 0xFFFF0000) | (v & 0xFFFF);
                break;
            case >= Register.AL and <= Register.BL:
                R[r - Register.AL] = (R[r - Register.AL] & 0xFFFFFF00) | (v & 0xFF);
                break;
            case >= Register.AH and <= Register.BH:
                R[r - Register.AH] = (R[r - Register.AH] & 0xFFFF00FF) | ((v & 0xFF) << 8);
                break;
            default:
                throw new NotSupportedException($"x86 register {r}");
        }
    }

    private uint Address(in Instruction ins)
    {
        uint a = ins.MemoryDisplacement32;
        if (ins.MemoryBase != Register.None)
            a += Reg(ins.MemoryBase);
        if (ins.MemoryIndex != Register.None)
            a += Reg(ins.MemoryIndex) * (uint)ins.MemoryIndexScale;
        return a;
    }

    private uint Load(uint a, int size) => size switch
    {
        1 => m_vm.ReadByte((int)a),
        2 => m_vm.Read16((int)a),
        _ => (uint)m_vm.Read32((int)a),
    };

    private void Store(uint a, int size, uint v)
    {
        switch (size)
        {
            case 1: m_vm.WriteByte((int)a, (byte)v); break;
            case 2: m_vm.Write16((int)a, (int)v); break;
            default: m_vm.Write32((int)a, (int)v); break;
        }
    }

    private ulong Load64(uint a) => (uint)m_vm.Read32((int)a) | (ulong)(uint)m_vm.Read32((int)a + 4) << 32;

    private void Store64(uint a, ulong v)
    {
        m_vm.Write32((int)a, (int)v);
        m_vm.Write32((int)a + 4, (int)(v >> 32));
    }

    private void Push(uint v)
    {
        R[4] -= 4;
        Store(R[4], 4, v);
    }

    private uint Pop()
    {
        uint v = Load(R[4], 4);
        R[4] += 4;
        return v;
    }

    /// <summary>Size in bytes of operand <paramref name="i"/>.</summary>
    private static int Size(in Instruction ins, int i) => ins.GetOpKind(i) switch
    {
        OpKind.Register => ins.GetOpRegister(i).GetSize(),
        OpKind.Memory => ins.MemorySize.GetSize(),
        OpKind.Immediate8 => 1,
        OpKind.Immediate16 => 2,
        _ => 4,
    };

    private uint Get(in Instruction ins, int i) => ins.GetOpKind(i) switch
    {
        OpKind.Register => Reg(ins.GetOpRegister(i)),
        OpKind.Memory => Load(Address(ins), ins.MemorySize.GetSize()),
        _ => (uint)ins.GetImmediate(i),
    };

    private void Set(in Instruction ins, int i, uint v)
    {
        if (ins.GetOpKind(i) == OpKind.Register)
            SetReg(ins.GetOpRegister(i), v);
        else
            Store(Address(ins), ins.MemorySize.GetSize(), v);
    }

    private ulong GetMm(in Instruction ins, int i) => ins.GetOpKind(i) switch
    {
        OpKind.Register when ins.GetOpRegister(i).IsMM() => MM[ins.GetOpRegister(i) - Register.MM0],
        OpKind.Register => Reg(ins.GetOpRegister(i)),
        OpKind.Memory => ins.MemorySize.GetSize() == 8 ? Load64(Address(ins)) : Load(Address(ins), 4),
        _ => ins.GetImmediate(i),
    };

    private void SetMm(in Instruction ins, int i, ulong v)
    {
        if (ins.GetOpKind(i) == OpKind.Register)
            MM[ins.GetOpRegister(i) - Register.MM0] = v;
        else
            Store64(Address(ins), v);
    }

    #endregion

    #region Flags

    private static uint Mask(int size) => size == 4 ? 0xFFFFFFFF : (1u << (8 * size)) - 1;
    private static uint Sign(int size) => 1u << (8 * size - 1);

    private void Result(uint r, int size)
    {
        r &= Mask(size);
        m_zf = r == 0;
        m_sf = (r & Sign(size)) != 0;
        m_pf = (System.Numerics.BitOperations.PopCount(r & 0xFF) & 1) == 0;
    }

    private uint Add(uint a, uint b, uint carry, int size)
    {
        uint m = Mask(size);
        ulong wide = (ulong)(a & m) + (b & m) + carry;
        uint r = (uint)wide & m;
        m_cf = wide > m;
        m_of = ((a ^ r) & (b ^ r) & Sign(size)) != 0;
        m_af = ((a ^ b ^ r) & 0x10) != 0;
        Result(r, size);
        return r;
    }

    private uint Sub(uint a, uint b, uint borrow, int size)
    {
        uint m = Mask(size);
        a &= m;
        b &= m;
        uint r = (a - b - borrow) & m;
        m_cf = (ulong)a < (ulong)b + borrow;
        m_of = ((a ^ b) & (a ^ r) & Sign(size)) != 0;
        m_af = ((a ^ b ^ r) & 0x10) != 0;
        Result(r, size);
        return r;
    }

    private uint Logic(uint r, int size)
    {
        m_cf = m_of = m_af = false;
        Result(r, size);
        return r & Mask(size);
    }

    private bool Condition(ConditionCode cc) => cc switch
    {
        ConditionCode.o => m_of,
        ConditionCode.no => !m_of,
        ConditionCode.b => m_cf,
        ConditionCode.ae => !m_cf,
        ConditionCode.e => m_zf,
        ConditionCode.ne => !m_zf,
        ConditionCode.be => m_cf || m_zf,
        ConditionCode.a => !m_cf && !m_zf,
        ConditionCode.s => m_sf,
        ConditionCode.ns => !m_sf,
        ConditionCode.p => m_pf,
        ConditionCode.np => !m_pf,
        ConditionCode.l => m_sf != m_of,
        ConditionCode.ge => m_sf == m_of,
        ConditionCode.le => m_zf || m_sf != m_of,
        ConditionCode.g => !m_zf && m_sf == m_of,
        _ => throw new NotSupportedException($"x86 condition {cc}"),
    };

    private uint Flags() =>
        (m_cf ? 1u : 0) | 2 | (m_pf ? 4u : 0) | (m_af ? 0x10u : 0) | (m_zf ? 0x40u : 0) | (m_sf ? 0x80u : 0) |
        0x200 | (m_df ? 0x400u : 0) | (m_of ? 0x800u : 0);

    private void SetFlags(uint f)
    {
        m_cf = (f & 1) != 0;
        m_pf = (f & 4) != 0;
        m_af = (f & 0x10) != 0;
        m_zf = (f & 0x40) != 0;
        m_sf = (f & 0x80) != 0;
        m_df = (f & 0x400) != 0;
        m_of = (f & 0x800) != 0;
    }

    #endregion

    /// <summary>Runs one instruction.</summary>
    public void Step()
    {
        var ins = Fetch(Eip);
        uint next = Eip + (uint)ins.Length;
        Eip = next;
        Executed++;
        switch (ins.Mnemonic)
        {
            // ---- data movement ----
            case Mnemonic.Mov:
                Set(ins, 0, Get(ins, 1));
                break;
            case Mnemonic.Movzx:
                Set(ins, 0, Get(ins, 1));
                break;
            case Mnemonic.Movsx:
            {
                uint v = Get(ins, 1);
                v = Size(ins, 1) == 1 ? (uint)(sbyte)v : (uint)(short)v;
                Set(ins, 0, v);
                break;
            }
            case Mnemonic.Lea:
                Set(ins, 0, Address(ins));
                break;
            case Mnemonic.Xchg:
            {
                uint a = Get(ins, 0), b = Get(ins, 1);
                Set(ins, 0, b);
                Set(ins, 1, a);
                break;
            }
            case Mnemonic.Push:
                Push(Size(ins, 0) == 1 ? (uint)(sbyte)Get(ins, 0) : Get(ins, 0));
                break;
            case Mnemonic.Pop:
            {
                uint v = Pop();
                Set(ins, 0, v);
                break;
            }
            case Mnemonic.Pushad:
            {
                uint esp = R[4];
                for (int k = 0; k < 8; k++)
                    Push(k == 4 ? esp : R[k]);
                break;
            }
            case Mnemonic.Popad:
                for (int k = 7; k >= 0; k--)
                {
                    uint v = Pop();
                    if (k != 4)
                        R[k] = v;
                }
                break;
            case Mnemonic.Pushfd:
                Push(Flags());
                break;
            case Mnemonic.Popfd:
                SetFlags(Pop());
                break;
            case Mnemonic.Cdq:
                R[2] = (int)R[0] < 0 ? 0xFFFFFFFF : 0;
                break;
            case Mnemonic.Bswap:
                Set(ins, 0, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Get(ins, 0)));
                break;
            case Mnemonic.Movnti:
                Set(ins, 0, Get(ins, 1));
                break;

            // ---- arithmetic ----
            case Mnemonic.Add:
                Set(ins, 0, Add(Get(ins, 0), Get(ins, 1), 0, Size(ins, 0)));
                break;
            case Mnemonic.Adc:
                Set(ins, 0, Add(Get(ins, 0), Get(ins, 1), m_cf ? 1u : 0, Size(ins, 0)));
                break;
            case Mnemonic.Sub:
                Set(ins, 0, Sub(Get(ins, 0), Get(ins, 1), 0, Size(ins, 0)));
                break;
            case Mnemonic.Sbb:
                Set(ins, 0, Sub(Get(ins, 0), Get(ins, 1), m_cf ? 1u : 0, Size(ins, 0)));
                break;
            case Mnemonic.Cmp:
                Sub(Get(ins, 0), Get(ins, 1), 0, Size(ins, 0));
                break;
            case Mnemonic.Inc:
            {
                bool cf = m_cf;
                Set(ins, 0, Add(Get(ins, 0), 1, 0, Size(ins, 0)));
                m_cf = cf;
                break;
            }
            case Mnemonic.Dec:
            {
                bool cf = m_cf;
                Set(ins, 0, Sub(Get(ins, 0), 1, 0, Size(ins, 0)));
                m_cf = cf;
                break;
            }
            case Mnemonic.Neg:
                Set(ins, 0, Sub(0, Get(ins, 0), 0, Size(ins, 0)));
                break;
            case Mnemonic.Not:
                Set(ins, 0, ~Get(ins, 0));
                break;
            case Mnemonic.And:
                Set(ins, 0, Logic(Get(ins, 0) & Get(ins, 1), Size(ins, 0)));
                break;
            case Mnemonic.Or:
                Set(ins, 0, Logic(Get(ins, 0) | Get(ins, 1), Size(ins, 0)));
                break;
            case Mnemonic.Xor:
                Set(ins, 0, Logic(Get(ins, 0) ^ Get(ins, 1), Size(ins, 0)));
                break;
            case Mnemonic.Test:
                Logic(Get(ins, 0) & Get(ins, 1), Size(ins, 0));
                break;
            case Mnemonic.Shl:
            case Mnemonic.Sal:
            case Mnemonic.Shr:
            case Mnemonic.Sar:
            case Mnemonic.Rol:
            case Mnemonic.Ror:
                Shift(ins);
                break;
            case Mnemonic.Mul:
                Multiply(ins, signed: false);
                break;
            case Mnemonic.Imul:
                if (ins.OpCount == 1)
                    Multiply(ins, signed: true);
                else
                {
                    long a = (int)Get(ins, ins.OpCount == 3 ? 1 : 0), b = (int)Get(ins, ins.OpCount == 3 ? 2 : 1);
                    if (Size(ins, 0) == 2)
                    {
                        a = (short)a;
                        b = (short)b;
                    }
                    long p = a * b;
                    uint r = (uint)p & Mask(Size(ins, 0));
                    m_cf = m_of = Size(ins, 0) == 2 ? p != (short)p : p != (int)p;
                    Result(r, Size(ins, 0));
                    Set(ins, 0, r);
                }
                break;
            case Mnemonic.Div:
            case Mnemonic.Idiv:
                Divide(ins, ins.Mnemonic == Mnemonic.Idiv);
                break;
            case Mnemonic.Bsr:
            {
                uint v = Get(ins, 1) & Mask(Size(ins, 1));
                m_zf = v == 0;
                if (v != 0)
                    Set(ins, 0, (uint)(31 - System.Numerics.BitOperations.LeadingZeroCount(v)));
                break;
            }

            // ---- flow ----
            case Mnemonic.Jmp:
                Eip = ins.GetOpKind(0) is OpKind.NearBranch32 or OpKind.NearBranch16 ? ins.NearBranch32 : Get(ins, 0);
                break;
            case Mnemonic.Call:
            {
                uint target = ins.GetOpKind(0) is OpKind.NearBranch32 or OpKind.NearBranch16 ? ins.NearBranch32 : Get(ins, 0);
                Push(next);
                Eip = target;
                break;
            }
            case Mnemonic.Ret:
                Eip = Pop();
                if (ins.OpCount > 0)
                    R[4] += (uint)ins.GetImmediate(0);
                break;
            case Mnemonic.Nop:
            case Mnemonic.Emms:
                break;
            case Mnemonic.Cld:
                m_df = false;
                break;
            case Mnemonic.Std:
                m_df = true;
                break;
            case Mnemonic.Cpuid:
                Cpuid();
                break;

            // ---- strings ----
            case Mnemonic.Movsb:
            case Mnemonic.Movsw:
            case Mnemonic.Movsd when ins.GetOpKind(0) == OpKind.MemoryESEDI:
            case Mnemonic.Stosb:
            case Mnemonic.Stosw:
            case Mnemonic.Stosd:
            case Mnemonic.Cmpsb:
            case Mnemonic.Cmpsd when ins.GetOpKind(0) is OpKind.MemorySegESI or OpKind.MemorySegSI:
                StringOp(ins);
                break;

            default:
                if (ins.ConditionCode != ConditionCode.None && ins.FlowControl == FlowControl.ConditionalBranch)
                {
                    if (Condition(ins.ConditionCode))
                        Eip = ins.NearBranch32;
                    break;
                }
                if (ins.ConditionCode != ConditionCode.None && ins.Mnemonic.ToString().StartsWith("Set", StringComparison.Ordinal))
                {
                    Set(ins, 0, Condition(ins.ConditionCode) ? 1u : 0);
                    break;
                }
                if (!Mmx(ins))
                    throw new NotSupportedException($"x86 instruction {ins} at {ins.IP32:X8} is not supported");
                break;
        }
    }

    private void Shift(in Instruction ins)
    {
        int size = Size(ins, 0), bits = 8 * size;
        int count = (int)(Get(ins, 1) & 31);
        if (count == 0)
            return;
        uint m = Mask(size), a = Get(ins, 0) & m, r;
        switch (ins.Mnemonic)
        {
            case Mnemonic.Shl:
            case Mnemonic.Sal:
                r = count >= 32 ? 0 : (a << count) & m;
                m_cf = count <= bits && ((a >> (bits - count)) & 1) != 0;
                m_of = ((r & Sign(size)) != 0) != m_cf;
                Result(r, size);
                break;
            case Mnemonic.Shr:
                r = a >> count;
                m_cf = ((a >> (count - 1)) & 1) != 0;
                m_of = (a & Sign(size)) != 0;
                Result(r, size);
                break;
            case Mnemonic.Sar:
            {
                int s = size == 4 ? (int)a : size == 2 ? (short)a : (sbyte)a;
                r = (uint)(s >> Math.Min(count, bits - 1)) & m;
                m_cf = ((s >> (count - 1)) & 1) != 0;
                m_of = false;
                Result(r, size);
                break;
            }
            case Mnemonic.Rol:
            {
                int c = count % bits;
                r = ((a << c) | (a >> ((bits - c) % bits))) & m;
                m_cf = (r & 1) != 0;
                m_of = ((r & Sign(size)) != 0) != m_cf;
                break;
            }
            default:
            {
                int c = count % bits;
                r = ((a >> c) | (a << ((bits - c) % bits))) & m;
                m_cf = (r & Sign(size)) != 0;
                m_of = m_cf != ((r & (Sign(size) >> 1)) != 0);
                break;
            }
        }
        Set(ins, 0, r);
    }

    private void Multiply(in Instruction ins, bool signed)
    {
        int size = Size(ins, 0);
        uint src = Get(ins, 0);
        switch (size)
        {
            case 1:
            {
                int p = signed ? (sbyte)R[0] * (sbyte)src : (byte)R[0] * (byte)src;
                R[0] = (R[0] & 0xFFFF0000) | ((uint)p & 0xFFFF);
                m_cf = m_of = signed ? p != (sbyte)p : (p & 0xFF00) != 0;
                break;
            }
            case 2:
            {
                int p = signed ? (short)R[0] * (short)src : (ushort)R[0] * (ushort)src;
                R[0] = (R[0] & 0xFFFF0000) | ((uint)p & 0xFFFF);
                R[2] = (R[2] & 0xFFFF0000) | (((uint)p >> 16) & 0xFFFF);
                m_cf = m_of = signed ? p != (short)p : ((uint)p >> 16) != 0;
                break;
            }
            default:
            {
                ulong p = signed ? (ulong)((long)(int)R[0] * (int)src) : (ulong)R[0] * src;
                R[0] = (uint)p;
                R[2] = (uint)(p >> 32);
                m_cf = m_of = signed ? (long)p != (int)p : (p >> 32) != 0;
                break;
            }
        }
    }

    private void Divide(in Instruction ins, bool signed)
    {
        int size = Size(ins, 0);
        uint src = Get(ins, 0) & Mask(size);
        if (src == 0)
            throw new DivideByZeroException($"x86 divide by zero at {ins.IP32:X8}");
        switch (size)
        {
            case 1:
            {
                uint ax = R[0] & 0xFFFF;
                uint q = signed ? (uint)((short)ax / (sbyte)src) : ax / src;
                uint rem = signed ? (uint)((short)ax % (sbyte)src) : ax % src;
                R[0] = (R[0] & 0xFFFF0000) | ((rem & 0xFF) << 8) | (q & 0xFF);
                break;
            }
            case 2:
            {
                uint d = ((R[2] & 0xFFFF) << 16) | (R[0] & 0xFFFF);
                uint q = signed ? (uint)((int)d / (short)src) : d / src;
                uint rem = signed ? (uint)((int)d % (short)src) : d % src;
                R[0] = (R[0] & 0xFFFF0000) | (q & 0xFFFF);
                R[2] = (R[2] & 0xFFFF0000) | (rem & 0xFFFF);
                break;
            }
            default:
            {
                ulong d = ((ulong)R[2] << 32) | R[0];
                if (signed)
                {
                    long q = (long)d / (int)src;
                    if (q != (int)q)
                        throw new OverflowException($"x86 divide overflow at {ins.IP32:X8}");
                    R[0] = (uint)q;
                    R[2] = (uint)((long)d % (int)src);
                }
                else
                {
                    ulong q = d / src;
                    if (q > uint.MaxValue)
                        throw new OverflowException($"x86 divide overflow at {ins.IP32:X8}");
                    R[0] = (uint)q;
                    R[2] = (uint)(d % src);
                }
                break;
            }
        }
    }

    private void StringOp(in Instruction ins)
    {
        int size = ins.Mnemonic switch
        {
            Mnemonic.Movsb or Mnemonic.Stosb or Mnemonic.Cmpsb => 1,
            Mnemonic.Movsw or Mnemonic.Stosw => 2,
            _ => 4,
        };
        int step = m_df ? -size : size;
        bool rep = ins.HasRepPrefix || ins.HasRepePrefix || ins.HasRepnePrefix;
        bool compare = ins.Mnemonic is Mnemonic.Cmpsb or Mnemonic.Cmpsd;
        while (!rep || R[1] != 0)
        {
            switch (ins.Mnemonic)
            {
                case Mnemonic.Movsb:
                case Mnemonic.Movsw:
                case Mnemonic.Movsd:
                    Store(R[7], size, Load(R[6], size));
                    R[6] += (uint)step;
                    R[7] += (uint)step;
                    break;
                case Mnemonic.Stosb:
                case Mnemonic.Stosw:
                case Mnemonic.Stosd:
                    Store(R[7], size, R[0]);
                    R[7] += (uint)step;
                    break;
                default:
                    Sub(Load(R[6], size), Load(R[7], size), 0, size);
                    R[6] += (uint)step;
                    R[7] += (uint)step;
                    break;
            }
            if (!rep)
                break;
            R[1]--;
            if (compare && (ins.HasRepePrefix && !m_zf || ins.HasRepnePrefix && m_zf))
                break;
        }
    }

    /// <summary>An Intel CPU ("GenuineIntel", family 6) with MMX and nothing newer.</summary>
    private void Cpuid()
    {
        switch (R[0])
        {
            case 0:
                R[0] = 1;
                R[3] = 0x756E6547;     // "Genu"
                R[2] = 0x49656E69;     // "ineI"
                R[1] = 0x6C65746E;     // "ntel"
                break;
            case 1:
                R[0] = 0x00000673;     // family 6
                R[3] = 0;
                R[1] = 0;
                R[2] = 0x00808001;     // edx: FPU | CMOV | MMX (no SSE, no SSE2)
                break;
            default:
                R[0] = R[1] = R[2] = R[3] = 0;
                break;
        }
    }

    #region MMX

    private static ulong Lanes(ulong a, ulong b, int bits, Func<long, long, long> f, bool signed)
    {
        ulong r = 0, mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        for (int s = 0; s < 64; s += bits)
        {
            ulong x = (a >> s) & mask, y = (b >> s) & mask;
            long lx = signed ? SignExtend(x, bits) : (long)x, ly = signed ? SignExtend(y, bits) : (long)y;
            r |= ((ulong)f(lx, ly) & mask) << s;
        }
        return r;
    }

    private static long SignExtend(ulong v, int bits) => bits == 64 ? (long)v : (long)(v << (64 - bits)) >> (64 - bits);

    private static long Saturate(long v, long min, long max) => Math.Clamp(v, min, max);

    private bool Mmx(in Instruction ins)
    {
        // Only MMX register forms; SSE (xmm) instructions are on paths the scripts do not take
        if (ins.OpCount == 0 || (ins.GetOpKind(0) == OpKind.Register && ins.GetOpRegister(0).IsXMM()) ||
            (ins.OpCount > 1 && ins.GetOpKind(1) == OpKind.Register && ins.GetOpRegister(1).IsXMM()))
            return false;
        switch (ins.Mnemonic)
        {
            case Mnemonic.Movd:
                if (ins.GetOpKind(0) == OpKind.Register && ins.GetOpRegister(0).IsMM())
                    MM[ins.GetOpRegister(0) - Register.MM0] = (uint)GetMm(ins, 1);
                else
                    Set(ins, 0, (uint)MM[ins.GetOpRegister(1) - Register.MM0]);
                return true;
            case Mnemonic.Movq:
                SetMm(ins, 0, GetMm(ins, 1));
                return true;
        }
        ulong a = GetMm(ins, 0), b = ins.OpCount > 1 ? GetMm(ins, 1) : 0, r;
        switch (ins.Mnemonic)
        {
            case Mnemonic.Paddb: r = Lanes(a, b, 8, (x, y) => x + y, false); break;
            case Mnemonic.Paddw: r = Lanes(a, b, 16, (x, y) => x + y, false); break;
            case Mnemonic.Paddd: r = Lanes(a, b, 32, (x, y) => x + y, false); break;
            case Mnemonic.Paddsb: r = Lanes(a, b, 8, (x, y) => Saturate(x + y, -128, 127), true); break;
            case Mnemonic.Paddsw: r = Lanes(a, b, 16, (x, y) => Saturate(x + y, -32768, 32767), true); break;
            case Mnemonic.Paddusb: r = Lanes(a, b, 8, (x, y) => Saturate(x + y, 0, 255), false); break;
            case Mnemonic.Paddusw: r = Lanes(a, b, 16, (x, y) => Saturate(x + y, 0, 65535), false); break;
            case Mnemonic.Psubb: r = Lanes(a, b, 8, (x, y) => x - y, false); break;
            case Mnemonic.Psubw: r = Lanes(a, b, 16, (x, y) => x - y, false); break;
            case Mnemonic.Psubd: r = Lanes(a, b, 32, (x, y) => x - y, false); break;
            case Mnemonic.Psubsb: r = Lanes(a, b, 8, (x, y) => Saturate(x - y, -128, 127), true); break;
            case Mnemonic.Psubsw: r = Lanes(a, b, 16, (x, y) => Saturate(x - y, -32768, 32767), true); break;
            case Mnemonic.Psubusb: r = Lanes(a, b, 8, (x, y) => Saturate(x - y, 0, 255), false); break;
            case Mnemonic.Psubusw: r = Lanes(a, b, 16, (x, y) => Saturate(x - y, 0, 65535), false); break;
            case Mnemonic.Pmullw: r = Lanes(a, b, 16, (x, y) => x * y, true); break;
            case Mnemonic.Pmulhw: r = Lanes(a, b, 16, (x, y) => (x * y) >> 16, true); break;
            case Mnemonic.Pmaddwd:
            {
                long w(ulong v, int k) => (short)(v >> (16 * k));
                long lo = w(a, 0) * w(b, 0) + w(a, 1) * w(b, 1), hi = w(a, 2) * w(b, 2) + w(a, 3) * w(b, 3);
                r = (uint)lo | (ulong)(uint)hi << 32;
                break;
            }
            case Mnemonic.Pand: r = a & b; break;
            case Mnemonic.Pandn: r = ~a & b; break;
            case Mnemonic.Por: r = a | b; break;
            case Mnemonic.Pxor: r = a ^ b; break;
            case Mnemonic.Pcmpeqb: r = Lanes(a, b, 8, (x, y) => x == y ? -1 : 0, false); break;
            case Mnemonic.Pcmpeqw: r = Lanes(a, b, 16, (x, y) => x == y ? -1 : 0, false); break;
            case Mnemonic.Pcmpeqd: r = Lanes(a, b, 32, (x, y) => x == y ? -1 : 0, false); break;
            case Mnemonic.Pcmpgtb: r = Lanes(a, b, 8, (x, y) => x > y ? -1 : 0, true); break;
            case Mnemonic.Pcmpgtw: r = Lanes(a, b, 16, (x, y) => x > y ? -1 : 0, true); break;
            case Mnemonic.Pcmpgtd: r = Lanes(a, b, 32, (x, y) => x > y ? -1 : 0, true); break;
            case Mnemonic.Psllw: r = ShiftLanes(a, b, 16, left: true, arithmetic: false); break;
            case Mnemonic.Pslld: r = ShiftLanes(a, b, 32, left: true, arithmetic: false); break;
            case Mnemonic.Psllq: r = ShiftLanes(a, b, 64, left: true, arithmetic: false); break;
            case Mnemonic.Psrlw: r = ShiftLanes(a, b, 16, left: false, arithmetic: false); break;
            case Mnemonic.Psrld: r = ShiftLanes(a, b, 32, left: false, arithmetic: false); break;
            case Mnemonic.Psrlq: r = ShiftLanes(a, b, 64, left: false, arithmetic: false); break;
            case Mnemonic.Psraw: r = ShiftLanes(a, b, 16, left: false, arithmetic: true); break;
            case Mnemonic.Psrad: r = ShiftLanes(a, b, 32, left: false, arithmetic: true); break;
            case Mnemonic.Punpcklbw: r = Interleave(a, b, 8, high: false); break;
            case Mnemonic.Punpcklwd: r = Interleave(a, b, 16, high: false); break;
            case Mnemonic.Punpckldq: r = Interleave(a, b, 32, high: false); break;
            case Mnemonic.Punpckhbw: r = Interleave(a, b, 8, high: true); break;
            case Mnemonic.Punpckhwd: r = Interleave(a, b, 16, high: true); break;
            case Mnemonic.Punpckhdq: r = Interleave(a, b, 32, high: true); break;
            case Mnemonic.Packuswb: r = Pack(a, b, 16, 0, 255); break;
            case Mnemonic.Packsswb: r = Pack(a, b, 16, -128, 127); break;
            case Mnemonic.Packssdw: r = Pack(a, b, 32, -32768, 32767); break;
            default:
                return false;
        }
        SetMm(ins, 0, r);
        return true;
    }

    private static ulong ShiftLanes(ulong a, ulong count, int bits, bool left, bool arithmetic)
    {
        if (count >= (ulong)bits)
        {
            if (!arithmetic)
                return 0;
            count = (ulong)bits - 1;
        }
        int c = (int)count;
        return Lanes(a, 0, bits, (x, _) => left ? x << c : arithmetic ? x >> c : (long)((ulong)x >> c), arithmetic);
    }

    private static ulong Interleave(ulong a, ulong b, int bits, bool high)
    {
        ulong mask = (1UL << bits) - 1, r = 0;
        int from = high ? 32 : 0;
        for (int k = 0, s = 0; s < 32; k++, s += bits)
        {
            r |= ((a >> (from + s)) & mask) << (2 * k * bits);
            r |= ((b >> (from + s)) & mask) << ((2 * k + 1) * bits);
        }
        return r;
    }

    private static ulong Pack(ulong a, ulong b, int bits, long min, long max)
    {
        int outBits = bits / 2;
        ulong outMask = (1UL << outBits) - 1, r = 0;
        int lanes = 64 / bits;
        for (int k = 0; k < lanes; k++)
        {
            long x = SignExtend((a >> (k * bits)) & ((1UL << bits) - 1), bits);
            long y = SignExtend((b >> (k * bits)) & ((1UL << bits) - 1), bits);
            r |= ((ulong)Saturate(x, min, max) & outMask) << (k * outBits);
            r |= ((ulong)Saturate(y, min, max) & outMask) << ((k + lanes) * outBits);
        }
        return r;
    }

    #endregion
}
