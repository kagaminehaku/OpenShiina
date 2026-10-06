// A just-in-time translator for the x86 routines embedded in SCN modules: a routine becomes a
// .NET method (an expression tree compiled to IL), with the registers and flags as locals,
// branches as gotos and every operation done by X86Ops, so it writes the bytes the interpreter
// (X86Cpu) and the game's own code write - tens of times faster than interpreting it. It is the
// same translation tools/X86Gen makes into C# source, done for any routine of any game when it
// is first called. Routines with instructions it does not translate (calls, cpuid, SSE, parity
// flags, division and multiplication of bytes and words, ...) stay on the interpreter.
//
// Differences with the interpreter that no routine can see: the parity and adjust flags are not
// kept (pushfd stores them as 0; no translated routine tests them), and the registers start at 0
// instead of keeping the values of the routine run before.

using System.Linq.Expressions;
using System.Reflection;
using Iced.Intel;
using static System.Linq.Expressions.Expression;

namespace OpenShiina.Scripting;

public static class X86Jit
{
    /// <summary>A translated routine: called like the engine calls it (stdcall, one argument) on a stack at esp.</summary>
    public delegate void Routine(ScnVm vm, uint argument, uint esp);

    /// <summary>Branches back a routine may take before it counts as one that does not end.</summary>
    public const long LoopLimit = 1_000_000_000;

    /// <summary>The routine as a .NET method; null (and why) when it has code the translator does not take.</summary>
    public static Routine? Compile(X86Routine routine, out string? reason)
    {
        try
        {
            var lambda = new Builder(routine).Build();
            reason = null;
            return lambda.Compile();
        }
        catch (NotSupportedException ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    #region Helpers the translated code calls

    /// <summary>movs (byte, word, dword), with rep: the interpreter's element by element copy.</summary>
    public static void Movs(ScnVm vm, int size, bool df, bool rep, ref uint esi, ref uint edi, ref uint ecx)
    {
        if (rep && !df && ecx > 0)
        {
            // Forwards, the element by element copy is a block copy unless the destination
            // starts inside the source (then it repeats the source's start)
            long bytes = (long)ecx * size;
            if (bytes <= int.MaxValue && (edi <= esi || edi >= esi + bytes))
            {
                vm.CopyMemory((int)edi, (int)esi, (int)bytes);
                esi += (uint)bytes;
                edi += (uint)bytes;
                ecx = 0;
                return;
            }
        }
        uint step = df ? unchecked((uint)-size) : (uint)size;
        while (!rep || ecx != 0)
        {
            switch (size)
            {
                case 1: vm.WriteByte((int)edi, vm.ReadByte((int)esi)); break;
                case 2: vm.Write16((int)edi, vm.Read16((int)esi)); break;
                default: vm.Write32((int)edi, vm.Read32((int)esi)); break;
            }
            esi += step;
            edi += step;
            if (!rep)
                break;
            ecx--;
        }
    }

    /// <summary>stos (byte, word, dword), with rep.</summary>
    public static void Stos(ScnVm vm, int size, bool df, bool rep, uint eax, ref uint edi, ref uint ecx)
    {
        uint step = df ? unchecked((uint)-size) : (uint)size;
        while (!rep || ecx != 0)
        {
            switch (size)
            {
                case 1: vm.WriteByte((int)edi, (byte)eax); break;
                case 2: vm.Write16((int)edi, (int)(eax & 0xFFFF)); break;
                default: vm.Write32((int)edi, (int)eax); break;
            }
            edi += step;
            if (!rep)
                break;
            ecx--;
        }
    }

    /// <summary>bsr: the destination's new value (unchanged when the source is 0).</summary>
    public static uint Bsr(uint v, uint old, ref bool zf)
    {
        zf = v == 0;
        return v == 0 ? old : (uint)(31 - System.Numerics.BitOperations.LeadingZeroCount(v));
    }

    public static Exception Endless(uint entry) => new InvalidOperationException($"x86 routine at {entry:X8} did not return");

    #endregion

    private sealed class Builder
    {
        private readonly X86Routine m_routine;
        private readonly ParameterExpression m_vm = Parameter(typeof(ScnVm), "vm");
        private readonly ParameterExpression m_argument = Parameter(typeof(uint), "argument");
        private readonly ParameterExpression[] m_r = new ParameterExpression[8];
        private readonly ParameterExpression[] m_mm = new ParameterExpression[8];
        private readonly ParameterExpression m_cf = Variable(typeof(bool), "cf"), m_zf = Variable(typeof(bool), "zf");
        private readonly ParameterExpression m_sf = Variable(typeof(bool), "sf"), m_of = Variable(typeof(bool), "of");
        private readonly ParameterExpression m_df = Variable(typeof(bool), "df"), m_keep = Variable(typeof(bool), "keep");
        private readonly ParameterExpression m_addr = Variable(typeof(uint), "addr");
        private readonly ParameterExpression m_t1 = Variable(typeof(uint), "t1"), m_t2 = Variable(typeof(uint), "t2");
        private readonly ParameterExpression m_loops = Variable(typeof(long), "loops");
        private readonly Dictionary<uint, LabelTarget> m_labels = new();
        private readonly LabelTarget m_return = Label("return");
        private readonly List<Expression> m_body = new();

        private static readonly string[] s_names = { "eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi" };

        public Builder(X86Routine routine)
        {
            m_routine = routine;
            for (int i = 0; i < 8; i++)
            {
                m_r[i] = i == 4 ? Parameter(typeof(uint), "esp") : Variable(typeof(uint), s_names[i]);
                m_mm[i] = Variable(typeof(ulong), $"mm{i}");
            }
        }

        public Expression<Routine> Build()
        {
            if (m_routine.Open)
                throw new NotSupportedException("code that cannot be followed (indirect jumps or calls)");
            if (m_routine.Calls)
                throw new NotSupportedException("calls");
            foreach (uint target in m_routine.Targets)
            {
                if (!m_routine.Code.ContainsKey(target))
                    throw new NotSupportedException($"a jump into an instruction at {target:X8}");
                m_labels[target] = Label($"L_{target:X}");
            }
            var esp = m_r[4];
            // The argument and the return address the engine pushes
            Emit(Assign(esp, Subtract(esp, U(4))));
            Emit(Call(Op("W32"), m_vm, esp, m_argument));
            Emit(Assign(esp, Subtract(esp, U(4))));
            Emit(Call(Op("W32"), m_vm, esp, U(0xFFFFFFF0)));
            Emit(Assign(m_df, Constant(false)));
            // Straight-line code runs on from one instruction to the next; where the code the
            // routine was followed through has a gap, the instruction before it ends a path
            foreach (var (ip, ins) in m_routine.Code)
            {
                if (m_labels.TryGetValue(ip, out var label))
                    Emit(Label(label));
                Translate(ins);
            }
            Emit(Label(m_return));
            var variables = m_r.Where((_, i) => i != 4).Concat(m_mm)
                .Concat(new[] { m_cf, m_zf, m_sf, m_of, m_df, m_keep, m_addr, m_t1, m_t2, m_loops });
            return Lambda<Routine>(Block(variables, m_body), $"x86_{m_routine.Entry:X}", new[] { m_vm, m_argument, m_r[4] });
        }

        private void Emit(Expression e) => m_body.Add(e);

        private static ConstantExpression U(uint v) => Constant(v, typeof(uint));

        private static MethodInfo Op(string name) =>
            typeof(X86Ops).GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(nameof(X86Ops), name);

        private static MethodInfo Helper(string name) =>
            typeof(X86Jit).GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(nameof(X86Jit), name);

        private Expression[] Flags => new Expression[] { m_cf, m_zf, m_sf, m_of };

        #region Operands

        private Expression Reg(Register r) => r switch
        {
            >= Register.EAX and <= Register.EDI => m_r[r - Register.EAX],
            >= Register.AX and <= Register.DI => And(m_r[r - Register.AX], U(0xFFFF)),
            >= Register.AL and <= Register.BL => And(m_r[r - Register.AL], U(0xFF)),
            >= Register.AH and <= Register.BH => And(RightShift(m_r[r - Register.AH], Constant(8)), U(0xFF)),
            _ => throw new NotSupportedException($"register {r}"),
        };

        private Expression SetReg(Register r, Expression v)
        {
            switch (r)
            {
                case >= Register.EAX and <= Register.EDI:
                    return Assign(m_r[r - Register.EAX], v);
                case >= Register.AX and <= Register.DI:
                {
                    var n = m_r[r - Register.AX];
                    return Assign(n, Or(And(n, U(0xFFFF0000)), And(v, U(0xFFFF))));
                }
                case >= Register.AL and <= Register.BL:
                {
                    var n = m_r[r - Register.AL];
                    return Assign(n, Or(And(n, U(0xFFFFFF00)), And(v, U(0xFF))));
                }
                case >= Register.AH and <= Register.BH:
                {
                    var n = m_r[r - Register.AH];
                    return Assign(n, Or(And(n, U(0xFFFF00FF)), LeftShift(And(v, U(0xFF)), Constant(8))));
                }
                default:
                    throw new NotSupportedException($"register {r}");
            }
        }

        private Expression Address(in Instruction ins)
        {
            if (ins.MemorySegment is not (Register.DS or Register.SS or Register.ES or Register.None))
                throw new NotSupportedException($"segment {ins.MemorySegment}");
            Expression a = U(ins.MemoryDisplacement32);
            if (ins.MemoryBase != Register.None)
                a = Add(a, Reg(ins.MemoryBase));
            if (ins.MemoryIndex != Register.None)
                a = Add(a, ins.MemoryIndexScale == 1 ? Reg(ins.MemoryIndex) : Multiply(Reg(ins.MemoryIndex), U((uint)ins.MemoryIndexScale)));
            return a;
        }

        private static bool HasMemory(in Instruction ins)
        {
            for (int i = 0; i < ins.OpCount; i++)
                if (ins.GetOpKind(i) == OpKind.Memory)
                    return true;
            return false;
        }

        private static int Size(in Instruction ins, int i) => ins.GetOpKind(i) switch
        {
            OpKind.Register => ins.GetOpRegister(i).GetSize(),
            OpKind.Memory => ins.MemorySize.GetSize(),
            OpKind.Immediate8 => 1,
            OpKind.Immediate16 => 2,
            _ => 4,
        };

        private Expression Load(int size, Expression a) => size switch
        {
            1 => Call(Op("R8"), m_vm, a),
            2 => Call(Op("R16"), m_vm, a),
            4 => Call(Op("R32"), m_vm, a),
            _ => throw new NotSupportedException($"{size}-byte memory operand"),
        };

        private Expression StoreMem(int size, Expression a, Expression v) => size switch
        {
            1 => Call(Op("W8"), m_vm, a, v),
            2 => Call(Op("W16"), m_vm, a, v),
            4 => Call(Op("W32"), m_vm, a, v),
            _ => throw new NotSupportedException($"{size}-byte memory operand"),
        };

        /// <summary>Operand i as a uint; memory operands use the address computed into m_addr.</summary>
        private Expression Get(in Instruction ins, int i) => ins.GetOpKind(i) switch
        {
            OpKind.Register => Reg(ins.GetOpRegister(i)),
            OpKind.Memory => Load(ins.MemorySize.GetSize(), m_addr),
            OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8_2nd
                => U((uint)ins.GetImmediate(i)),
            var k => throw new NotSupportedException($"operand {k}"),
        };

        private Expression Set(in Instruction ins, int i, Expression v) => ins.GetOpKind(i) switch
        {
            OpKind.Register => SetReg(ins.GetOpRegister(i), v),
            OpKind.Memory => StoreMem(ins.MemorySize.GetSize(), m_addr, v),
            var k => throw new NotSupportedException($"destination {k}"),
        };

        private Expression GetMm(in Instruction ins, int i)
        {
            switch (ins.GetOpKind(i))
            {
                case OpKind.Register when ins.GetOpRegister(i).IsMM():
                    return m_mm[ins.GetOpRegister(i) - Register.MM0];
                case OpKind.Register when ins.GetOpRegister(i).IsXMM():
                    throw new NotSupportedException("SSE");
                case OpKind.Register:
                    return Convert(Reg(ins.GetOpRegister(i)), typeof(ulong));
                case OpKind.Memory:
                    return ins.MemorySize.GetSize() == 8 ? Call(Op("R64"), m_vm, m_addr) : Convert(Load(4, m_addr), typeof(ulong));
                default:
                    return Constant(ins.GetImmediate(i), typeof(ulong));
            }
        }

        private Expression SetMm(in Instruction ins, int i, Expression v)
        {
            if (ins.GetOpKind(i) == OpKind.Register)
            {
                var r = ins.GetOpRegister(i);
                if (!r.IsMM())
                    throw new NotSupportedException($"register {r}");
                return Assign(m_mm[r - Register.MM0], v);
            }
            return Call(Op("W64"), m_vm, m_addr, v);
        }

        private Expression Cond(ConditionCode cc) => cc switch
        {
            ConditionCode.o => m_of,
            ConditionCode.no => Not(m_of),
            ConditionCode.b => m_cf,
            ConditionCode.ae => Not(m_cf),
            ConditionCode.e => m_zf,
            ConditionCode.ne => Not(m_zf),
            ConditionCode.be => OrElse(m_cf, m_zf),
            ConditionCode.a => AndAlso(Not(m_cf), Not(m_zf)),
            ConditionCode.s => m_sf,
            ConditionCode.ns => Not(m_sf),
            ConditionCode.l => NotEqual(m_sf, m_of),
            ConditionCode.ge => Equal(m_sf, m_of),
            ConditionCode.le => OrElse(m_zf, NotEqual(m_sf, m_of)),
            ConditionCode.g => AndAlso(Not(m_zf), Equal(m_sf, m_of)),
            _ => throw new NotSupportedException($"condition {cc}"),
        };

        private Expression Push(Expression v) => Block(
            Assign(m_r[4], Subtract(m_r[4], U(4))),
            Call(Op("W32"), m_vm, m_r[4], v));

        private Expression PopInto(ParameterExpression target) => Block(
            Assign(target, Call(Op("R32"), m_vm, m_r[4])),
            Assign(m_r[4], Add(m_r[4], U(4))));

        #endregion

        private static readonly Dictionary<Mnemonic, string> s_mmx = new()
        {
            [Mnemonic.Paddb] = "Paddb", [Mnemonic.Paddw] = "Paddw", [Mnemonic.Paddd] = "Paddd",
            [Mnemonic.Paddsb] = "Paddsb", [Mnemonic.Paddsw] = "Paddsw", [Mnemonic.Paddusb] = "Paddusb", [Mnemonic.Paddusw] = "Paddusw",
            [Mnemonic.Psubb] = "Psubb", [Mnemonic.Psubw] = "Psubw", [Mnemonic.Psubd] = "Psubd",
            [Mnemonic.Psubsb] = "Psubsb", [Mnemonic.Psubsw] = "Psubsw", [Mnemonic.Psubusb] = "Psubusb", [Mnemonic.Psubusw] = "Psubusw",
            [Mnemonic.Pmullw] = "Pmullw", [Mnemonic.Pmulhw] = "Pmulhw", [Mnemonic.Pmaddwd] = "Pmaddwd",
            [Mnemonic.Pcmpeqb] = "Pcmpeqb", [Mnemonic.Pcmpeqw] = "Pcmpeqw", [Mnemonic.Pcmpeqd] = "Pcmpeqd",
            [Mnemonic.Pcmpgtb] = "Pcmpgtb", [Mnemonic.Pcmpgtw] = "Pcmpgtw", [Mnemonic.Pcmpgtd] = "Pcmpgtd",
            [Mnemonic.Psllw] = "Psllw", [Mnemonic.Pslld] = "Pslld", [Mnemonic.Psllq] = "Psllq",
            [Mnemonic.Psrlw] = "Psrlw", [Mnemonic.Psrld] = "Psrld", [Mnemonic.Psrlq] = "Psrlq",
            [Mnemonic.Psraw] = "Psraw", [Mnemonic.Psrad] = "Psrad",
            [Mnemonic.Punpcklbw] = "Punpcklbw", [Mnemonic.Punpcklwd] = "Punpcklwd", [Mnemonic.Punpckldq] = "Punpckldq",
            [Mnemonic.Punpckhbw] = "Punpckhbw", [Mnemonic.Punpckhwd] = "Punpckhwd", [Mnemonic.Punpckhdq] = "Punpckhdq",
            [Mnemonic.Packuswb] = "Packuswb", [Mnemonic.Packsswb] = "Packsswb", [Mnemonic.Packssdw] = "Packssdw",
        };

        private void Translate(in Instruction ins)
        {
            for (int i = 0; i < ins.OpCount; i++)
                if (ins.GetOpKind(i) == OpKind.Register && ins.GetOpRegister(i).IsXMM())
                    throw new NotSupportedException($"SSE ({ins.Mnemonic} at {ins.IP32:X8})");
            if (ins.HasLockPrefix)
                throw new NotSupportedException($"lock prefix at {ins.IP32:X8}");
            bool memory = HasMemory(ins) && ins.Mnemonic is not (Mnemonic.Lea or Mnemonic.Pop);
            if (memory)
                Emit(Assign(m_addr, Address(ins)));
            int size0 = ins.OpCount > 0 ? Size(ins, 0) : 4;
            var esp = m_r[4];
            switch (ins.Mnemonic)
            {
                // ---- data movement ----
                case Mnemonic.Mov:
                case Mnemonic.Movzx:
                case Mnemonic.Movnti:
                    Emit(Set(ins, 0, Get(ins, 1)));
                    break;
                case Mnemonic.Movsx:
                    Emit(Set(ins, 0, Size(ins, 1) == 1
                        ? Convert(Convert(Convert(Get(ins, 1), typeof(byte)), typeof(sbyte)), typeof(uint))
                        : Convert(Convert(Convert(Get(ins, 1), typeof(ushort)), typeof(short)), typeof(uint))));
                    break;
                case Mnemonic.Lea:
                    Emit(SetReg(ins.Op0Register, Address(ins)));
                    break;
                case Mnemonic.Xchg:
                    Emit(Assign(m_t1, Get(ins, 0)));
                    Emit(Assign(m_t2, Get(ins, 1)));
                    Emit(Set(ins, 0, m_t2));
                    Emit(Set(ins, 1, m_t1));
                    break;
                case Mnemonic.Push:
                    Emit(Assign(m_t1, Size(ins, 0) == 1 ? Convert(Convert(Convert(Get(ins, 0), typeof(byte)), typeof(sbyte)), typeof(uint)) : Get(ins, 0)));
                    Emit(Push(m_t1));
                    break;
                case Mnemonic.Pop:
                    Emit(PopInto(m_t1));
                    if (ins.Op0Kind == OpKind.Memory)
                        Emit(Assign(m_addr, Address(ins)));
                    Emit(Set(ins, 0, m_t1));
                    break;
                case Mnemonic.Pushad:
                    Emit(Assign(m_t1, esp));
                    for (int k = 0; k < 8; k++)
                        Emit(Push(k == 4 ? m_t1 : m_r[k]));
                    break;
                case Mnemonic.Popad:
                    for (int k = 7; k >= 0; k--)
                        Emit(k == 4 ? Assign(esp, Add(esp, U(4))) : PopInto(m_r[k]));
                    break;
                case Mnemonic.Pushfd:
                    Emit(Push(Or(Or(Or(FlagBit(m_cf, 1), U(0x202)), Or(FlagBit(m_zf, 0x40), FlagBit(m_sf, 0x80))),
                                 Or(FlagBit(m_df, 0x400), FlagBit(m_of, 0x800)))));
                    break;
                case Mnemonic.Popfd:
                    Emit(PopInto(m_t1));
                    Emit(Assign(m_cf, NotEqual(And(m_t1, U(1)), U(0))));
                    Emit(Assign(m_zf, NotEqual(And(m_t1, U(0x40)), U(0))));
                    Emit(Assign(m_sf, NotEqual(And(m_t1, U(0x80)), U(0))));
                    Emit(Assign(m_df, NotEqual(And(m_t1, U(0x400)), U(0))));
                    Emit(Assign(m_of, NotEqual(And(m_t1, U(0x800)), U(0))));
                    break;
                case Mnemonic.Cdq:
                    Emit(Assign(m_r[2], Expression.Condition(LessThan(Convert(m_r[0], typeof(int)), Constant(0)), U(0xFFFFFFFF), U(0))));
                    break;
                case Mnemonic.Bswap:
                    Emit(Set(ins, 0, Call(typeof(System.Buffers.Binary.BinaryPrimitives).GetMethod("ReverseEndianness", new[] { typeof(uint) })!, Get(ins, 0))));
                    break;

                // ---- arithmetic ----
                case Mnemonic.Add:
                case Mnemonic.Adc:
                case Mnemonic.Sub:
                case Mnemonic.Sbb:
                case Mnemonic.Cmp:
                {
                    var method = Op(ins.Mnemonic is Mnemonic.Add or Mnemonic.Adc ? "Add" : "Sub");
                    Expression carry = ins.Mnemonic is Mnemonic.Adc or Mnemonic.Sbb ? Expression.Condition(m_cf, U(1), U(0)) : U(0);
                    var calc = Call(method, new[] { Get(ins, 0), Get(ins, 1), carry, Constant(size0) }.Concat(Flags));
                    Emit(ins.Mnemonic == Mnemonic.Cmp ? calc : Set(ins, 0, calc));
                    break;
                }
                case Mnemonic.Inc:
                case Mnemonic.Dec:
                    Emit(Assign(m_keep, m_cf));
                    Emit(Set(ins, 0, Call(Op(ins.Mnemonic == Mnemonic.Inc ? "Add" : "Sub"),
                        new[] { Get(ins, 0), U(1), U(0), Constant(size0) }.Concat(Flags))));
                    Emit(Assign(m_cf, m_keep));
                    break;
                case Mnemonic.Neg:
                    Emit(Set(ins, 0, Call(Op("Sub"), new[] { U(0), Get(ins, 0), U(0), Constant(size0) }.Concat(Flags))));
                    break;
                case Mnemonic.Not:
                    Emit(Set(ins, 0, OnesComplement(Get(ins, 0))));
                    break;
                case Mnemonic.And:
                case Mnemonic.Or:
                case Mnemonic.Xor:
                case Mnemonic.Test:
                {
                    Expression a = Get(ins, 0), b = Get(ins, 1);
                    Expression r = ins.Mnemonic switch { Mnemonic.Or => Or(a, b), Mnemonic.Xor => ExclusiveOr(a, b), _ => And(a, b) };
                    var calc = Call(Op("Logic"), new[] { r, Constant(size0) }.Concat(Flags));
                    Emit(ins.Mnemonic == Mnemonic.Test ? calc : Set(ins, 0, calc));
                    break;
                }
                case Mnemonic.Shl:
                case Mnemonic.Sal:
                case Mnemonic.Shr:
                case Mnemonic.Sar:
                case Mnemonic.Rol:
                case Mnemonic.Ror:
                {
                    int kind = ins.Mnemonic switch { Mnemonic.Shr => 1, Mnemonic.Sar => 2, Mnemonic.Rol => 3, Mnemonic.Ror => 4, _ => 0 };
                    Expression count = ins.OpCount < 2 ? Constant(1)
                        : ins.Op1Kind == OpKind.Register ? Convert(And(Reg(ins.Op1Register), U(31)), typeof(int))
                        : Constant((int)(ins.GetImmediate(1) & 31));
                    Emit(Set(ins, 0, Call(Op("Shift"), new[] { Constant(kind), Get(ins, 0), count, Constant(size0) }.Concat(Flags))));
                    break;
                }
                case Mnemonic.Mul when size0 == 4:
                    Emit(Call(Op("Mul32"), m_r[0], m_r[2], Get(ins, 0), m_cf, m_of));
                    break;
                case Mnemonic.Imul when ins.OpCount == 1 && size0 == 4:
                    Emit(Call(Op("Imul32"), m_r[0], m_r[2], Get(ins, 0), m_cf, m_of));
                    break;
                case Mnemonic.Imul when ins.OpCount >= 2 && size0 is 2 or 4:
                {
                    Expression a = ins.OpCount == 3 ? Get(ins, 1) : Get(ins, 0), b = ins.OpCount == 3 ? Get(ins, 2) : Get(ins, 1);
                    Emit(Set(ins, 0, Call(Op("Imul"), new[] { a, b, Constant(size0) }.Concat(Flags))));
                    break;
                }
                case Mnemonic.Div when size0 == 4:
                    Emit(Call(Op("Div32"), m_r[0], m_r[2], Get(ins, 0)));
                    break;
                case Mnemonic.Bsr when ins.Op0Kind == OpKind.Register && ins.Op0Register is >= Register.EAX and <= Register.EDI:
                {
                    uint mask = Size(ins, 1) == 2 ? 0xFFFF : 0xFFFFFFFF;
                    Emit(SetReg(ins.Op0Register, Call(Helper("Bsr"), And(Get(ins, 1), U(mask)), Reg(ins.Op0Register), m_zf)));
                    break;
                }

                // ---- flow ----
                case Mnemonic.Jmp:
                    EmitBranch(ins, null);
                    break;
                case Mnemonic.Ret:
                    Emit(Return(m_return));
                    break;
                case Mnemonic.Nop:
                case Mnemonic.Emms:
                    break;
                case Mnemonic.Cld:
                    Emit(Assign(m_df, Constant(false)));
                    break;
                case Mnemonic.Std:
                    Emit(Assign(m_df, Constant(true)));
                    break;
                case Mnemonic.Cmc:
                    Emit(Assign(m_cf, Not(m_cf)));
                    break;
                case Mnemonic.Stc:
                    Emit(Assign(m_cf, Constant(true)));
                    break;
                case Mnemonic.Clc:
                    Emit(Assign(m_cf, Constant(false)));
                    break;

                // ---- strings ----
                case Mnemonic.Movsb:
                case Mnemonic.Movsw:
                case Mnemonic.Movsd when ins.Op0Kind == OpKind.MemoryESEDI:
                {
                    int n = ins.Mnemonic == Mnemonic.Movsb ? 1 : ins.Mnemonic == Mnemonic.Movsw ? 2 : 4;
                    Emit(Call(Helper("Movs"), m_vm, Constant(n), m_df, Constant(ins.HasRepPrefix), m_r[6], m_r[7], m_r[1]));
                    break;
                }
                case Mnemonic.Stosb:
                case Mnemonic.Stosw:
                case Mnemonic.Stosd:
                {
                    int n = ins.Mnemonic == Mnemonic.Stosb ? 1 : ins.Mnemonic == Mnemonic.Stosw ? 2 : 4;
                    Emit(Call(Helper("Stos"), m_vm, Constant(n), m_df, Constant(ins.HasRepPrefix), m_r[0], m_r[7], m_r[1]));
                    break;
                }

                // ---- MMX ----
                case Mnemonic.Movd:
                    if (ins.Op0Kind == OpKind.Register && ins.Op0Register.IsMM())
                        Emit(Assign(m_mm[ins.Op0Register - Register.MM0], Convert(Convert(GetMm(ins, 1), typeof(uint)), typeof(ulong))));
                    else
                        Emit(Set(ins, 0, Convert(m_mm[ins.Op1Register - Register.MM0], typeof(uint))));
                    break;
                case Mnemonic.Movq:
                    Emit(SetMm(ins, 0, GetMm(ins, 1)));
                    break;
                case Mnemonic.Pand:
                    Emit(SetMm(ins, 0, And(GetMm(ins, 0), GetMm(ins, 1))));
                    break;
                case Mnemonic.Pandn:
                    Emit(SetMm(ins, 0, And(OnesComplement(GetMm(ins, 0)), GetMm(ins, 1))));
                    break;
                case Mnemonic.Por:
                    Emit(SetMm(ins, 0, Or(GetMm(ins, 0), GetMm(ins, 1))));
                    break;
                case Mnemonic.Pxor:
                    Emit(SetMm(ins, 0, ExclusiveOr(GetMm(ins, 0), GetMm(ins, 1))));
                    break;

                default:
                    if (ins.FlowControl == FlowControl.ConditionalBranch && ins.ConditionCode != ConditionCode.None &&
                        ins.Mnemonic is not (Mnemonic.Jcxz or Mnemonic.Jecxz or Mnemonic.Loop or Mnemonic.Loope or Mnemonic.Loopne))
                    {
                        EmitBranch(ins, Cond(ins.ConditionCode));
                        break;
                    }
                    if (ins.ConditionCode != ConditionCode.None && ins.Mnemonic.ToString().StartsWith("Set", StringComparison.Ordinal))
                    {
                        Emit(Set(ins, 0, Expression.Condition(Cond(ins.ConditionCode), U(1), U(0))));
                        break;
                    }
                    if (s_mmx.TryGetValue(ins.Mnemonic, out var name) && ins.Op0Kind == OpKind.Register && ins.Op0Register.IsMM())
                    {
                        Emit(SetMm(ins, 0, Call(Op(name), GetMm(ins, 0), GetMm(ins, 1))));
                        break;
                    }
                    throw new NotSupportedException($"{ins.Mnemonic} at {ins.IP32:X8}");
            }
        }

        private static Expression FlagBit(ParameterExpression flag, uint bit) => Expression.Condition(flag, U(bit), U(0));

        /// <summary>A jump (conditional when <paramref name="condition"/> is set); jumps back count towards the loop limit.</summary>
        private void EmitBranch(in Instruction ins, Expression? condition)
        {
            var target = m_labels[ins.NearBranch32];
            Expression jump = Goto(target);
            if (ins.NearBranch32 <= ins.IP32)
                jump = Block(
                    IfThen(GreaterThan(PreIncrementAssign(m_loops), Constant(LoopLimit)),
                        Throw(Call(Helper("Endless"), Constant(m_routine.Entry)))),
                    jump);
            Emit(condition == null ? jump : IfThen(condition, jump));
        }
    }
}
