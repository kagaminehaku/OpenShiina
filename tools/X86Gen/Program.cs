// X86Gen <file> <hexEntry> <MethodName> <out.cs> [description]
// Translates an x86 routine embedded in an SCN module into a C# method with the semantics of
// OpenShiina's X86Cpu (registers are locals, jumps are gotos, flags cf/zf/sf/of). Follows the
// code from the entry; every reachable instruction must be supported, else it stops.
using System.Text;
using Iced.Intel;

var file = File.ReadAllBytes(args[0]);
uint entry = Convert.ToUInt32(args[1], 16);
string method = args[2], output = args[3];
string description = args.Length > 4 ? args[4] : "";

// Reachable instructions
var code = new SortedDictionary<uint, Instruction>();
var targets = new HashSet<uint>();
var work = new Stack<uint>();
work.Push(entry);
while (work.Count > 0)
{
    uint ip = work.Pop();
    while (!code.ContainsKey(ip))
    {
        var decoder = Iced.Intel.Decoder.Create(32, new ByteArrayCodeReader(file, (int)ip, Math.Min(16, file.Length - (int)ip)), ip);
        decoder.Decode(out var ins);
        if (ins.IsInvalid)
            throw new Exception($"invalid code at {ip:X}");
        code[ip] = ins;
        if (ins.Mnemonic == Mnemonic.Ret)
            break;
        if (ins.Mnemonic == Mnemonic.Jmp)
        {
            if (ins.Op0Kind != OpKind.NearBranch32)
                throw new Exception($"indirect jump at {ip:X}");
            targets.Add(ins.NearBranch32);
            work.Push(ins.NearBranch32);
            break;
        }
        if (ins.FlowControl == FlowControl.ConditionalBranch)
        {
            targets.Add(ins.NearBranch32);
            work.Push(ins.NearBranch32);
        }
        else if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.IndirectBranch or FlowControl.Interrupt)
            throw new Exception($"unsupported flow at {ip:X}: {ins}");
        ip = ins.NextIP32;
    }
}

var sb = new StringBuilder();
void E(string s) => sb.Append("        ").AppendLine(s);

string R32(Register r) => r.ToString().ToLowerInvariant();

string RegRead(Register r)
{
    if (r.IsMM())
        return R32(r);
    if (r is >= Register.EAX and <= Register.EDI)
        return R32(r);
    if (r is >= Register.AX and <= Register.DI)
        return $"({R32(r - Register.AX + Register.EAX)} & 0xFFFFu)";
    if (r is >= Register.AL and <= Register.BL)
        return $"({R32(r - Register.AL + Register.EAX)} & 0xFFu)";
    if (r is >= Register.AH and <= Register.BH)
        return $"(({R32(r - Register.AH + Register.EAX)} >> 8) & 0xFFu)";
    throw new Exception($"register {r}");
}

string RegWrite(Register r, string v)
{
    if (r.IsMM() || r is >= Register.EAX and <= Register.EDI)
        return $"{R32(r)} = {v};";
    if (r is >= Register.AX and <= Register.DI)
    {
        string n = R32(r - Register.AX + Register.EAX);
        return $"{n} = ({n} & 0xFFFF0000u) | (({v}) & 0xFFFFu);";
    }
    if (r is >= Register.AL and <= Register.BL)
    {
        string n = R32(r - Register.AL + Register.EAX);
        return $"{n} = ({n} & 0xFFFFFF00u) | (({v}) & 0xFFu);";
    }
    if (r is >= Register.AH and <= Register.BH)
    {
        string n = R32(r - Register.AH + Register.EAX);
        return $"{n} = ({n} & 0xFFFF00FFu) | ((({v}) & 0xFFu) << 8);";
    }
    throw new Exception($"register {r}");
}

string Addr(in Instruction ins)
{
    var parts = new List<string>();
    if (ins.MemoryBase != Register.None)
        parts.Add(RegRead(ins.MemoryBase));
    if (ins.MemoryIndex != Register.None)
        parts.Add(ins.MemoryIndexScale == 1 ? RegRead(ins.MemoryIndex) : $"{RegRead(ins.MemoryIndex)} * {ins.MemoryIndexScale}u");
    if (ins.MemoryDisplacement32 != 0 || parts.Count == 0)
        parts.Add($"0x{ins.MemoryDisplacement32:X}u");
    return parts.Count == 1 ? parts[0] : "(" + string.Join(" + ", parts) + ")";
}

int Size(in Instruction ins, int i) => ins.GetOpKind(i) switch
{
    OpKind.Register => ins.GetOpRegister(i).GetSize(),
    OpKind.Memory => ins.MemorySize.GetSize(),
    OpKind.Immediate8 => 1,
    OpKind.Immediate16 => 2,
    _ => 4,
};

string Mem(int size, string a) => size switch
{
    1 => $"X86Ops.R8(vm, {a})",
    2 => $"X86Ops.R16(vm, {a})",
    4 => $"X86Ops.R32(vm, {a})",
    8 => $"X86Ops.R64(vm, {a})",
    _ => throw new Exception($"size {size}"),
};

string MemWrite(int size, string a, string v) => size switch
{
    1 => $"X86Ops.W8(vm, {a}, {v});",
    2 => $"X86Ops.W16(vm, {a}, {v});",
    4 => $"X86Ops.W32(vm, {a}, {v});",
    8 => $"X86Ops.W64(vm, {a}, {v});",
    _ => throw new Exception($"size {size}"),
};

// Reads operand i; memory operands of the instruction use the address in "a_" when computed
string Get(in Instruction ins, int i, string? addr = null) => ins.GetOpKind(i) switch
{
    OpKind.Register => RegRead(ins.GetOpRegister(i)),
    OpKind.Memory => Mem(ins.MemorySize.GetSize(), addr ?? Addr(ins)),
    _ => $"0x{(uint)ins.GetImmediate(i):X}u",
};

string Set(in Instruction ins, int i, string v, string? addr = null) => ins.GetOpKind(i) == OpKind.Register
    ? RegWrite(ins.GetOpRegister(i), v)
    : MemWrite(ins.MemorySize.GetSize(), addr ?? Addr(ins), v);

string Cond(ConditionCode cc) => cc switch
{
    ConditionCode.o => "of",
    ConditionCode.no => "!of",
    ConditionCode.b => "cf",
    ConditionCode.ae => "!cf",
    ConditionCode.e => "zf",
    ConditionCode.ne => "!zf",
    ConditionCode.be => "cf || zf",
    ConditionCode.a => "!cf && !zf",
    ConditionCode.s => "sf",
    ConditionCode.ns => "!sf",
    ConditionCode.l => "sf != of",
    ConditionCode.ge => "sf == of",
    ConditionCode.le => "zf || sf != of",
    ConditionCode.g => "!zf && sf == of",
    _ => throw new Exception($"condition {cc}"),
};

const string Flags = "ref cf, ref zf, ref sf, ref of";
var mmxOps = new Dictionary<Mnemonic, string>
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

string MmGet(in Instruction ins, int i) => ins.GetOpKind(i) switch
{
    OpKind.Register => RegRead(ins.GetOpRegister(i)),
    OpKind.Memory => ins.MemorySize.GetSize() == 8 ? Mem(8, Addr(ins)) : Mem(4, Addr(ins)),
    _ => $"0x{ins.GetImmediate(i):X}ul",
};

var formatter = new MasmFormatter();
var text = new StringOutput();
int count = 0;
foreach (var (ip, ins) in code)
{
    if (targets.Contains(ip))
        sb.AppendLine($"    L_{ip:X}:");
    formatter.Format(ins, text);
    string asm = text.ToStringAndReset();
    int size0 = ins.OpCount > 0 ? Size(ins, 0) : 4;
    string body;
    switch (ins.Mnemonic)
    {
        case Mnemonic.Mov:
        case Mnemonic.Movzx:
            body = Set(ins, 0, Get(ins, 1));
            break;
        case Mnemonic.Movsx:
            body = Set(ins, 0, Size(ins, 1) == 1 ? $"(uint)(sbyte){Get(ins, 1)}" : $"(uint)(short){Get(ins, 1)}");
            break;
        case Mnemonic.Lea:
            // lea r, [r] is a no-op (alignment padding)
            body = Addr(ins) == RegRead(ins.Op0Register) ? "" : RegWrite(ins.Op0Register, Addr(ins));
            break;
        case Mnemonic.Xchg:
            body = $"{{ uint x_ = {Get(ins, 0)}, y_ = {Get(ins, 1)}; {Set(ins, 0, "y_")} {Set(ins, 1, "x_")} }}";
            break;
        case Mnemonic.Push:
            body = $"{{ uint v_ = {Get(ins, 0)}; esp -= 4; X86Ops.W32(vm, esp, v_); }}";
            break;
        case Mnemonic.Pop:
            body = $"{{ uint v_ = X86Ops.R32(vm, esp); esp += 4; {Set(ins, 0, "v_")} }}";
            break;
        case Mnemonic.Pushad:
            body = "{ uint t_ = esp; foreach (uint v_ in new[] { eax, ecx, edx, ebx, t_, ebp, esi, edi }) { esp -= 4; X86Ops.W32(vm, esp, v_); } }";
            break;
        case Mnemonic.Popad:
            body = "edi = X86Ops.R32(vm, esp); esi = X86Ops.R32(vm, esp + 4); ebp = X86Ops.R32(vm, esp + 8); ebx = X86Ops.R32(vm, esp + 16); " +
                   "edx = X86Ops.R32(vm, esp + 20); ecx = X86Ops.R32(vm, esp + 24); eax = X86Ops.R32(vm, esp + 28); esp += 32;";
            break;
        case Mnemonic.Pushfd:
            body = "esp -= 4; X86Ops.W32(vm, esp, (cf ? 1u : 0) | 2 | (zf ? 0x40u : 0) | (sf ? 0x80u : 0) | 0x200 | (df ? 0x400u : 0) | (of ? 0x800u : 0));";
            break;
        case Mnemonic.Popfd:
            body = "{ uint f_ = X86Ops.R32(vm, esp); esp += 4; cf = (f_ & 1) != 0; zf = (f_ & 0x40) != 0; sf = (f_ & 0x80) != 0; df = (f_ & 0x400) != 0; of = (f_ & 0x800) != 0; }";
            break;
        case Mnemonic.Cdq:
            body = "edx = (int)eax < 0 ? 0xFFFFFFFFu : 0;";
            break;
        case Mnemonic.Add:
        case Mnemonic.Adc:
        case Mnemonic.Sub:
        case Mnemonic.Sbb:
        case Mnemonic.Cmp:
        {
            string f = ins.Mnemonic is Mnemonic.Add or Mnemonic.Adc ? "Add" : "Sub";
            string carry = ins.Mnemonic is Mnemonic.Adc or Mnemonic.Sbb ? "(cf ? 1u : 0)" : "0";
            bool mem = ins.Op0Kind == OpKind.Memory;
            string a = mem ? "a_" : null!;
            string calc = $"X86Ops.{f}({Get(ins, 0, a)}, {Get(ins, 1, a)}, {carry}, {size0}, {Flags})";
            string st = ins.Mnemonic == Mnemonic.Cmp ? $"{calc};" : Set(ins, 0, calc, a);
            body = mem ? $"{{ uint a_ = {Addr(ins)}; {st} }}" : st;
            break;
        }
        case Mnemonic.Inc:
        case Mnemonic.Dec:
        {
            string f = ins.Mnemonic == Mnemonic.Inc ? "Add" : "Sub";
            bool mem = ins.Op0Kind == OpKind.Memory;
            string a = mem ? "a_" : null!;
            string st = Set(ins, 0, $"X86Ops.{f}({Get(ins, 0, a)}, 1, 0, {size0}, ref c_, ref zf, ref sf, ref of)", a);
            body = $"{{ bool c_ = cf; {(mem ? $"uint a_ = {Addr(ins)}; " : "")}{st} }}";
            break;
        }
        case Mnemonic.Neg:
        {
            bool mem = ins.Op0Kind == OpKind.Memory;
            string a = mem ? "a_" : null!;
            string st = Set(ins, 0, $"X86Ops.Sub(0, {Get(ins, 0, a)}, 0, {size0}, {Flags})", a);
            body = mem ? $"{{ uint a_ = {Addr(ins)}; {st} }}" : st;
            break;
        }
        case Mnemonic.Not:
        {
            bool mem = ins.Op0Kind == OpKind.Memory;
            string a = mem ? "a_" : null!;
            string st = Set(ins, 0, $"~{Get(ins, 0, a)}", a);
            body = mem ? $"{{ uint a_ = {Addr(ins)}; {st} }}" : st;
            break;
        }
        case Mnemonic.And:
        case Mnemonic.Or:
        case Mnemonic.Xor:
        case Mnemonic.Test:
        {
            string op = ins.Mnemonic switch { Mnemonic.Or => "|", Mnemonic.Xor => "^", _ => "&" };
            bool mem = ins.Op0Kind == OpKind.Memory;
            string a = mem ? "a_" : null!;
            string calc = $"X86Ops.Logic({Get(ins, 0, a)} {op} {Get(ins, 1, a)}, {size0}, {Flags})";
            string st = ins.Mnemonic == Mnemonic.Test ? $"{calc};" : Set(ins, 0, calc, a);
            body = mem ? $"{{ uint a_ = {Addr(ins)}; {st} }}" : st;
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
            string countExpr = ins.Op1Kind == OpKind.Register ? $"(int)({RegRead(ins.Op1Register)} & 31)" : $"{(int)(ins.GetImmediate(1) & 31)}";
            bool mem = ins.Op0Kind == OpKind.Memory;
            string a = mem ? "a_" : null!;
            string st = Set(ins, 0, $"X86Ops.Shift({kind}, {Get(ins, 0, a)}, {countExpr}, {size0}, {Flags})", a);
            body = mem ? $"{{ uint a_ = {Addr(ins)}; {st} }}" : st;
            break;
        }
        case Mnemonic.Bsr when size0 == 4:
            // zf = source is 0 (the destination then keeps its value)
            body = $"{{ uint v_ = {Get(ins, 1)}; zf = v_ == 0; if (v_ != 0) {RegWrite(ins.Op0Register, "(uint)(31 - System.Numerics.BitOperations.LeadingZeroCount(v_))")} }}";
            break;
        case Mnemonic.Mul when size0 == 4:
            body = $"X86Ops.Mul32(ref eax, ref edx, {Get(ins, 0)}, ref cf, ref of);";
            break;
        case Mnemonic.Imul when ins.OpCount == 1 && size0 == 4:
            body = $"X86Ops.Imul32(ref eax, ref edx, {Get(ins, 0)}, ref cf, ref of);";
            break;
        case Mnemonic.Imul when ins.OpCount >= 2 && size0 is 2 or 4:
            body = RegWrite(ins.Op0Register, ins.OpCount == 3
                ? $"X86Ops.Imul({Get(ins, 1)}, {Get(ins, 2)}, {size0}, {Flags})"
                : $"X86Ops.Imul({Get(ins, 0)}, {Get(ins, 1)}, {size0}, {Flags})");
            break;
        case Mnemonic.Div when size0 == 4:
            body = $"X86Ops.Div32(ref eax, ref edx, {Get(ins, 0)});";
            break;
        case Mnemonic.Jmp:
            body = $"goto L_{ins.NearBranch32:X};";
            break;
        case Mnemonic.Ret:
            body = "return;";
            break;
        case Mnemonic.Nop:
        case Mnemonic.Emms:
            body = "";
            break;
        case Mnemonic.Cld:
            body = "df = false;";
            break;
        case Mnemonic.Std:
            body = "df = true;";
            break;
        case Mnemonic.Movsb:
        case Mnemonic.Movsd when ins.Op0Kind == OpKind.MemoryESEDI:
        case Mnemonic.Stosb:
        case Mnemonic.Stosd:
        {
            int n = ins.Mnemonic is Mnemonic.Movsb or Mnemonic.Stosb ? 1 : 4;
            bool move = ins.Mnemonic is Mnemonic.Movsb or Mnemonic.Movsd;
            string one = move
                ? $"{MemWrite(n, "edi", Mem(n, "esi"))} esi += step_; edi += step_;"
                : $"{MemWrite(n, "edi", n == 1 ? "eax & 0xFFu" : "eax")} edi += step_;";
            string step = $"uint step_ = df ? unchecked((uint)-{n}) : {n}u;";
            body = ins.HasRepPrefix
                ? $"{{ {step} while (ecx != 0) {{ {one} ecx--; }} }}"
                : $"{{ {step} {one} }}";
            break;
        }
        case Mnemonic.Movd:
            body = ins.Op0Kind == OpKind.Register && ins.Op0Register.IsMM()
                ? $"{R32(ins.Op0Register)} = (ulong){Get(ins, 1)};"
                : Set(ins, 0, $"(uint){R32(ins.Op1Register)}");
            break;
        case Mnemonic.Movq:
            body = ins.Op0Kind == OpKind.Register
                ? $"{R32(ins.Op0Register)} = {MmGet(ins, 1)};"
                : MemWrite(8, Addr(ins), R32(ins.Op1Register));
            break;
        case Mnemonic.Pand:
            body = $"{R32(ins.Op0Register)} &= {MmGet(ins, 1)};";
            break;
        case Mnemonic.Pandn:
            body = $"{R32(ins.Op0Register)} = ~{R32(ins.Op0Register)} & {MmGet(ins, 1)};";
            break;
        case Mnemonic.Por:
            body = $"{R32(ins.Op0Register)} |= {MmGet(ins, 1)};";
            break;
        case Mnemonic.Pxor:
            body = $"{R32(ins.Op0Register)} ^= {MmGet(ins, 1)};";
            break;
        default:
            if (ins.FlowControl == FlowControl.ConditionalBranch && ins.ConditionCode != ConditionCode.None)
            {
                body = $"if ({Cond(ins.ConditionCode)}) goto L_{ins.NearBranch32:X};";
                break;
            }
            if (ins.ConditionCode != ConditionCode.None && ins.Mnemonic.ToString().StartsWith("Set", StringComparison.Ordinal))
            {
                body = Set(ins, 0, $"(({Cond(ins.ConditionCode)}) ? 1u : 0u)");
                break;
            }
            if (mmxOps.TryGetValue(ins.Mnemonic, out var mmxName) && ins.Op0Kind == OpKind.Register && ins.Op0Register.IsMM())
            {
                body = $"{R32(ins.Op0Register)} = X86Ops.{mmxName}({R32(ins.Op0Register)}, {MmGet(ins, 1)});";
                break;
            }
            throw new Exception($"unsupported at {ip:X}: {asm}");
    }
    if (body == $"{RegRead(ins.OpCount > 0 && ins.Op0Kind == OpKind.Register ? ins.Op0Register : Register.EAX)} = {RegRead(ins.OpCount > 0 && ins.Op0Kind == OpKind.Register ? ins.Op0Register : Register.EAX)};")
        body = "";   // mov r, r (padding)
    if (body.Length > 0)
        E($"{body,-100} // {ip:X5} {asm}");
    else
        E($"// {ip:X5} {asm}");
    count++;
}

var o = new StringBuilder();
o.AppendLine($"// <auto-generated> by tools/X86Gen from {Path.GetFileName(args[0])} at {entry:X5}: do not edit by hand.");
if (description.Length > 0)
    o.AppendLine($"// {description}");
o.AppendLine("#pragma warning disable CS0162, CS0164, CS0168, CS0219");
o.AppendLine();
o.AppendLine("namespace OpenShiina.Scripting.Generated;");
o.AppendLine();
o.AppendLine("internal static partial class X86Routines");
o.AppendLine("{");
o.AppendLine($"    /// <summary>{count} instructions; called like the engine does (stdcall, one argument).</summary>");
o.AppendLine($"    public static void {method}(ScnVm vm, uint argument, uint esp)");
o.AppendLine("    {");
o.AppendLine("        uint eax = 0, ecx = 0, edx = 0, ebx = 0, ebp = 0, esi = 0, edi = 0;");
o.AppendLine("        ulong mm0 = 0, mm1 = 0, mm2 = 0, mm3 = 0, mm4 = 0, mm5 = 0, mm6 = 0, mm7 = 0;");
o.AppendLine("        bool cf = false, zf = false, sf = false, of = false, df = false;");
o.AppendLine("        esp -= 4; X86Ops.W32(vm, esp, argument);");
o.AppendLine("        esp -= 4; X86Ops.W32(vm, esp, 0xFFFFFFF0u);");
o.Append(sb);
o.AppendLine("    }");
o.AppendLine("}");
File.WriteAllText(output, o.ToString());
Console.WriteLine($"{method}: {count} instructions, {targets.Count} labels -> {output}");
