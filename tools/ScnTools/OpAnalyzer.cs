using System.IO;
using Iced.Intel;

// Derives the SCN opcode table from the engine: for every opcode value, follows the dispatcher of the
// script interpreter to the handler, then counts operand reads (GetVar / SetVar / GetVarAdr) along
// the handler's control flow, including through called functions.
static class OpAnalyzer
{
    static byte[] mem = null!; static uint ib;
    static uint GetVar, SetVar, GetVarAdr, Interp, LoopHead, DispStart, DispEnd, Invalid;
    static readonly Dictionary<uint, Instruction> insCache = new();

    static void LoadImage(string exe)
    {
        var b = File.ReadAllBytes(exe);
        int pe = BitConverter.ToInt32(b, 0x3C), n = BitConverter.ToUInt16(b, pe + 6), opt = BitConverter.ToUInt16(b, pe + 20);
        ib = BitConverter.ToUInt32(b, pe + 24 + 28);
        uint size = BitConverter.ToUInt32(b, pe + 24 + 56);
        mem = new byte[size + 0x1000];
        Array.Copy(b, 0, mem, 0, Math.Min(0x1000, b.Length));
        for (int i = 0; i < n; i++)
        {
            int h = pe + 24 + opt + 40 * i;
            uint va = BitConverter.ToUInt32(b, h + 12), raw = BitConverter.ToUInt32(b, h + 20), rs = BitConverter.ToUInt32(b, h + 16);
            if (raw + rs > b.Length) rs = (uint)(b.Length - raw);
            Array.Copy(b, raw, mem, va, Math.Min(rs, mem.Length - va));
        }
    }
    static uint U32(uint va) => BitConverter.ToUInt32(mem, (int)(va - ib));
    static byte U8(uint va) => mem[va - ib];
    static Instruction Ins(uint va)
    {
        if (insCache.TryGetValue(va, out var i)) return i;
        if (va < ib + 0x1000 || va - ib + 16 >= mem.Length) return default;
        var d =Iced.Intel.Decoder.Create(32, new ByteArrayCodeReader(mem, (int)(va - ib), 16), va);
        d.Decode(out i); insCache[va] = i; return i;
    }

    // ---------------- dispatcher emulation ----------------
    static uint? Resolve(uint op, out string trace)
    {
        var regs = new Dictionary<Register, long> { [Register.EAX] = op, [Register.EDX] = 0 };
        long a = 0, bb = 0; bool haveCmp = false;
        uint ip = DispStart; trace = "";
        for (int steps = 0; steps < 400; steps++)
        {
            var ins = Ins(ip); uint next = (uint)ins.NextIP;
            long? R(Register r) { var full = r.GetFullRegister32(); if (!regs.TryGetValue(full, out var v)) return null; return r.GetSize() == 1 ? v & 0xFF : r.GetSize() == 2 ? v & 0xFFFF : v; }
            long? Op(int k) => ins.GetOpKind(k) switch { OpKind.Register => R(ins.GetOpRegister(k)), OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate16 or OpKind.Immediate32 => (long)(int)ins.GetImmediate(k) & 0xFFFFFFFF, _ => null };
            long? Mem() { long addr = ins.MemoryDisplacement32; if (ins.MemoryBase != Register.None) { var v = R(ins.MemoryBase); if (v == null) return null; addr += v.Value; } if (ins.MemoryIndex != Register.None) { var v = R(ins.MemoryIndex); if (v == null) return null; addr += v.Value * ins.MemoryIndexScale; } return addr & 0xFFFFFFFF; }
            switch (ins.Mnemonic)
            {
                case Mnemonic.And: case Mnemonic.Sub: case Mnemonic.Add: case Mnemonic.Cmp:
                    {
                        if (ins.Op0Kind != OpKind.Register) return ip;
                        var x = R(ins.Op0Register); var y = Op(1); if (x == null || y == null) return ip;
                        long r = ins.Mnemonic switch { Mnemonic.And => x.Value & y.Value, Mnemonic.Sub => x.Value - y.Value, Mnemonic.Add => x.Value + y.Value, _ => x.Value };
                        if (ins.Mnemonic != Mnemonic.And) { a = x.Value; bb = y.Value; haveCmp = true; }
                        if (ins.Mnemonic != Mnemonic.Cmp) regs[ins.Op0Register.GetFullRegister32()] = r & 0xFFFFFFFF;
                        break;
                    }
                case Mnemonic.Xor when ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Register && ins.Op0Register == ins.Op1Register:
                    regs[ins.Op0Register.GetFullRegister32()] = 0; break;
                case Mnemonic.Lea:
                    { var m = Mem(); if (m == null) return ip; regs[ins.Op0Register.GetFullRegister32()] = m.Value; break; }
                case Mnemonic.Mov or Mnemonic.Movzx when ins.Op0Kind == OpKind.Register:
                    {
                        long? v;
                        if (ins.Op1Kind == OpKind.Memory) { var m = Mem(); if (m == null) return ip; v = ins.MemorySize == MemorySize.UInt8 ? U8((uint)m) : ins.MemorySize == MemorySize.UInt16 ? BitConverter.ToUInt16(mem, (int)((uint)m - ib)) : U32((uint)m); }
                        else v = Op(1);
                        if (v == null) return ip;
                        var full = ins.Op0Register.GetFullRegister32();
                        if (ins.Op0Register.GetSize() == 1 && ins.Mnemonic == Mnemonic.Mov) { long old = regs.TryGetValue(full, out var o) ? o : 0; v = (old & ~0xFFL) | (v.Value & 0xFF); }
                        regs[full] = v.Value; break;
                    }
                case Mnemonic.Jmp:
                    if (ins.Op0Kind == OpKind.NearBranch32) { next = ins.NearBranch32; break; }
                    if (ins.Op0Kind == OpKind.Memory) { var m = Mem(); if (m == null) return ip; next = U32((uint)m); break; }
                    return ip;
                default:
                    if (ins.FlowControl == FlowControl.ConditionalBranch && haveCmp)
                    {
                        ulong ua = (ulong)(a & 0xFFFFFFFF), ub = (ulong)(bb & 0xFFFFFFFF); int sa = (int)a, sb = (int)bb;
                        bool t = ins.ConditionCode switch
                        {
                            ConditionCode.e => ua == ub, ConditionCode.ne => ua != ub,
                            ConditionCode.a => ua > ub, ConditionCode.ae => ua >= ub, ConditionCode.b => ua < ub, ConditionCode.be => ua <= ub,
                            ConditionCode.g => sa > sb, ConditionCode.ge => sa >= sb, ConditionCode.l => sa < sb, ConditionCode.le => sa <= sb,
                            _ => throw new Exception("cc " + ins.ConditionCode)
                        };
                        if (t) next = ins.NearBranch32;
                        break;
                    }
                    return ip;
            }
            ip = next;
        }
        return null;
    }

    // ---------------- operand signature ----------------
    // A signature is the sequence of items an instruction consumes after its opcode:
    // "V" = one operand (GetVar / SetVar / GetVarAdr), "bN" = N raw bytes read straight from the script.
    record Summary(List<string> Sig, bool Loop, bool PcWrite, bool Unknown);
    static readonly Dictionary<uint, Summary> funcMemo = new();
    static readonly HashSet<uint> funcActive = new();

    static int Weight(List<string> s) => s.Count * 1000 + s.Sum(x => x == "V" ? 0 : int.Parse(x[1..]));
    static List<string> Better(List<string> a, List<string> b) => Weight(a) >= Weight(b) ? a : b;
    static List<string> Concat(List<string> a, List<string> b) { var r = new List<string>(a); r.AddRange(b); return r; }

    // Script pointer = [ctx+10h]. The context register differs per engine build (Sena handlers use EBP,
    // Oreimo uses EBX and also keeps &ctx->pc in EDI); in called functions it is whatever register
    // is moved into ECX before an operand read.
    [ThreadStatic] static HashSet<Register>? ctxRegs;
    [ThreadStatic] static bool inHandlerNow;
    static Register HandlerCtx = Register.EBP, HandlerPcPtr = Register.None;

    static bool IsPcMem(in Instruction ins) =>
        ins.MemoryIndex == Register.None &&
        ((ins.MemoryDisplacement32 == 0x10 && ctxRegs != null && ctxRegs.Contains(ins.MemoryBase)) ||
         (inHandlerNow && HandlerPcPtr != Register.None && ins.MemoryBase == HandlerPcPtr && ins.MemoryDisplacement32 == 0));

    static HashSet<Register> FindCtxRegs(uint start, bool inHandler)
    {
        var set = new HashSet<Register>(); if (inHandler) set.Add(HandlerCtx);
        var seen = new HashSet<uint>(); var q = new Queue<uint>(); q.Enqueue(start);
        Register lastEcxSrc = Register.None;
        while (q.Count > 0 && seen.Count < 20000)
        {
            uint ip = q.Dequeue();
            while (seen.Add(ip))
            {
                if (inHandler && ((ip >= LoopHead && ip < DispEnd) || ip == Invalid)) break;
                var ins = Ins(ip);
                if (ins.Code == Code.INVALID) break;
                if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op0Register == Register.ECX && ins.Op1Kind == OpKind.Register)
                    lastEcxSrc = ins.Op1Register;
                if (ins.FlowControl == FlowControl.Call && ins.Op0Kind == OpKind.NearBranch32 &&
                    (ins.NearBranch32 == GetVar || ins.NearBranch32 == SetVar || ins.NearBranch32 == GetVarAdr) && lastEcxSrc != Register.None)
                    set.Add(lastEcxSrc);
                if (ins.FlowControl == FlowControl.Return) break;
                if (ins.FlowControl == FlowControl.ConditionalBranch) q.Enqueue(ins.NearBranch32);
                if (ins.FlowControl == FlowControl.UnconditionalBranch) { if (ins.Op0Kind == OpKind.NearBranch32) { ip = ins.NearBranch32; continue; } break; }
                ip = (uint)ins.NextIP;
            }
        }
        return set;
    }

    // Registers loaded from the first stack argument in a function's prologue
    static IEnumerable<Register> FirstArgRegs(uint start)
    {
        int pushes = 0; bool frame = false; uint ip = start;
        for (int i = 0; i < 14; i++)
        {
            var ins = Ins(ip);
            if (ins.Code == Code.INVALID || ins.FlowControl != FlowControl.Next) yield break;
            if (ins.Mnemonic == Mnemonic.Push) pushes++;
            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op0Register == Register.EBP && ins.Op1Kind == OpKind.Register && ins.Op1Register == Register.ESP) frame = true;
            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Memory && ins.MemoryIndex == Register.None)
            {
                if (ins.MemoryBase == Register.ESP && ins.MemoryDisplacement32 == 4 + 4 * pushes) yield return ins.Op0Register;
                if (frame && ins.MemoryBase == Register.EBP && ins.MemoryDisplacement32 == 8) yield return ins.Op0Register;
            }
            ip = (uint)ins.NextIP;
        }
    }

    static Summary Analyze(uint start, bool inHandler, bool ctxArg = false)
    {
        var outerCtx = ctxRegs; var outerH = inHandlerNow;
        ctxRegs = FindCtxRegs(start, inHandler); inHandlerNow = inHandler;
        if (ctxArg) foreach (var r in FirstArgRegs(start)) ctxRegs.Add(r);
        try { return AnalyzeInner(start, inHandler); } finally { ctxRegs = outerCtx; inHandlerNow = outerH; }
    }

    static Summary AnalyzeInner(uint start, bool inHandler)
    {
        var memo = new Dictionary<uint, List<string>>(); var onStack = new HashSet<uint>();
        bool loop = false, pcWrite = false, unknown = false;
        List<string> Walk(uint ip, int depth)
        {
            if (depth > 3000) { unknown = true; return new(); }
            if (memo.TryGetValue(ip, out var m)) return m;
            if (onStack.Contains(ip)) { loop = true; return new(); }
            onStack.Add(ip);
            var acc = new List<string>(); List<string> result; uint cur = ip;
            var pcRegs = new Dictionary<Register, (int snap, int off)>();   // register = script pointer + off
            bool ctxPushed = false;
            var spills = new Dictionary<(Register, uint), (int snap, int off)>();
            while (true)
            {
                if (inHandler && ((cur >= LoopHead && cur < DispEnd) || cur == Invalid)) { result = acc; break; }
                var ins = Ins(cur);
                if (ins.Code == Code.INVALID) { unknown = true; result = acc; break; }

                // ---- script pointer tracking ----
                Register r0 = ins.Op0Kind == OpKind.Register ? ins.Op0Register.GetFullRegister32() : Register.None;
                if (ins.Mnemonic == Mnemonic.Mov && r0 != Register.None && ins.Op1Kind == OpKind.Memory && IsPcMem(ins))
                    pcRegs[r0] = (acc.Count, 0);
                // script pointer spilled to a stack slot and reloaded later (Oreimo build)
                else if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Memory && !IsPcMem(ins) && ins.MemoryIndex == Register.None &&
                         ins.MemoryBase is Register.EBP or Register.ESP && ins.Op1Kind == OpKind.Register &&
                         pcRegs.TryGetValue(ins.Op1Register.GetFullRegister32(), out var sp))
                    spills[(ins.MemoryBase, ins.MemoryDisplacement32)] = sp;
                else if (ins.Mnemonic == Mnemonic.Mov && r0 != Register.None && ins.Op1Kind == OpKind.Memory && ins.MemoryIndex == Register.None &&
                         spills.TryGetValue((ins.MemoryBase, ins.MemoryDisplacement32), out var rl))
                    pcRegs[r0] = rl;
                else if (ins.Mnemonic == Mnemonic.Mov && r0 != Register.None && ins.Op1Kind == OpKind.Register && pcRegs.TryGetValue(ins.Op1Register.GetFullRegister32(), out var cp))
                    pcRegs[r0] = cp;
                else if (r0 != Register.None && pcRegs.TryGetValue(r0, out var pr) && ins.OpCount >= 1 &&
                         (ins.Mnemonic == Mnemonic.Inc || (ins.Mnemonic == Mnemonic.Add && ins.Op1Kind is OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate32)))
                    pcRegs[r0] = (pr.snap, pr.off + (ins.Mnemonic == Mnemonic.Inc ? 1 : (int)ins.GetImmediate(1)));
                else if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Memory && IsPcMem(ins) && ins.Op1Kind == OpKind.Register)
                {
                    if (pcRegs.TryGetValue(ins.Op1Register.GetFullRegister32(), out var st))
                    {
                        if (acc.Count == st.snap) { if (st.off > 0) acc.Add("b" + st.off); else if (st.off < 0) unknown = true; }
                        else if (st.off == 0) acc.RemoveRange(st.snap, acc.Count - st.snap);   // save / read / restore
                        else unknown = true;
                        pcRegs.Clear();
                    }
                    else pcWrite = true;   // jump: pointer replaced by a computed address
                }
                else if (ins.Op0Kind == OpKind.Memory && IsPcMem(ins) && ins.Mnemonic is Mnemonic.Add or Mnemonic.Inc)
                {
                    if (ins.Mnemonic == Mnemonic.Inc) acc.Add("b1");
                    else if (ins.Op1Kind is OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate32) acc.Add("b" + (int)ins.GetImmediate(1));
                    else unknown = true;
                }
                else if (ins.Op0Kind == OpKind.Memory && IsPcMem(ins) && ins.Mnemonic is Mnemonic.Sub or Mnemonic.Dec)
                    unknown = true;
                else if (r0 != Register.None && ins.Mnemonic != Mnemonic.Cmp && ins.Mnemonic != Mnemonic.Test && ins.Mnemonic != Mnemonic.Push)
                    pcRegs.Remove(r0);
                // "push ctx; call f": f receives the context as its first argument
                if (ins.Mnemonic == Mnemonic.Push && ins.Op0Kind == OpKind.Register && ctxRegs!.Contains(ins.Op0Register))
                    ctxPushed = true;

                if (ins.FlowControl == FlowControl.Call)
                {
                    if (ins.Op0Kind == OpKind.NearBranch32)
                    {
                        uint t = ins.NearBranch32;
                        if (t == GetVar || t == SetVar || t == GetVarAdr) acc.Add("V");
                        else if (t != Interp) { var s = Func(t, ctxPushed); acc.AddRange(s.Sig); loop |= s.Loop; unknown |= s.Unknown; pcWrite |= s.PcWrite; }
                    }
                    ctxPushed = false;
                    pcRegs.Remove(Register.EAX); pcRegs.Remove(Register.ECX); pcRegs.Remove(Register.EDX);
                    cur = (uint)ins.NextIP; continue;
                }
                if (ins.FlowControl == FlowControl.Return) { result = acc; break; }
                if (ins.FlowControl == FlowControl.UnconditionalBranch)
                {
                    if (ins.Op0Kind == OpKind.NearBranch32) { result = Concat(acc, Walk(ins.NearBranch32, depth + 1)); break; }
                    if (ins.Op0Kind == OpKind.Memory && ins.MemoryIndex != Register.None && ins.MemoryIndexScale == 4 && ins.MemoryBase == Register.None)
                    {
                        var best = new List<string>(); uint tab = ins.MemoryDisplacement32;
                        for (int e = 0; e < 512; e++)
                        {
                            uint tgt = U32(tab + (uint)e * 4);
                            if (tgt < ib + 0x1000 || tgt >= ib + (uint)mem.Length - 0x1000 || Math.Abs((long)tgt - cur) > 0x20000) break;
                            best = Better(best, Walk(tgt, depth + 1));
                        }
                        result = Concat(acc, best); break;
                    }
                    unknown = true; result = acc; break;
                }
                if (ins.FlowControl == FlowControl.ConditionalBranch)
                {
                    var x = Walk(ins.NearBranch32, depth + 1); var y = Walk((uint)ins.NextIP, depth + 1);
                    result = Concat(acc, Better(x, y)); break;
                }
                if (ins.FlowControl == FlowControl.Interrupt || ins.Mnemonic == Mnemonic.Int3) { result = acc; break; }
                cur = (uint)ins.NextIP;
            }
            onStack.Remove(ip);
            memo[ip] = result;
            return result;
        }
        var sig = Walk(start, 0);
        return new Summary(sig, loop, pcWrite, unknown);
    }

    static readonly Dictionary<(uint, bool), Summary> funcMemo2 = new();
    static Summary Func(uint va, bool ctxArg)
    {
        if (funcMemo2.TryGetValue((va, ctxArg), out var s)) return s;
        if (!funcActive.Add(va)) return new Summary(new(), true, false, false); // recursion
        s = Analyze(va, false, ctxArg);
        funcActive.Remove(va);
        if (s.Sig.Count == 0) s = s with { Loop = false };
        funcMemo2[(va, ctxArg)] = s;
        return s;
    }

    // --opscan <exe> <interp> <dispStart> <loopHead> <invalid> <getvar> <setvar> <getvaradr> <out.tsv> <dispEnd>
    public static void Run(string[] a)
    {
        LoadImage(a[0]);
        Interp = Convert.ToUInt32(a[1], 16); DispStart = Convert.ToUInt32(a[2], 16); LoopHead = Convert.ToUInt32(a[3], 16);
        Invalid = Convert.ToUInt32(a[4], 16); GetVar = Convert.ToUInt32(a[5], 16); SetVar = Convert.ToUInt32(a[6], 16); GetVarAdr = Convert.ToUInt32(a[7], 16); DispEnd = Convert.ToUInt32(a[9], 16);
        if (a.Length > 10) HandlerCtx = Enum.Parse<Register>(a[10], true);
        if (a.Length > 11) HandlerPcPtr = Enum.Parse<Register>(a[11], true);
        var handlers = new Dictionary<uint, List<uint>>();
        for (uint op = 0; op <= 0xFFFF; op++)
        {
            var h = Resolve(op, out _);
            if (h == null || h == Invalid) continue;
            if (!handlers.TryGetValue(h.Value, out var l)) handlers[h.Value] = l = new();
            l.Add(op);
        }
        var t = new Thread(() =>
        {
            using var w = new StreamWriter(a[8]);
            w.WriteLine("op\thandler\tsig\tloop\tpcWrite\tunknown");
            foreach (var (h, ops) in handlers.OrderBy(kv => kv.Value[0]))
            {
                if (ops.Count > 2000) { Console.WriteLine($"handler {h:X} serves {ops.Count} opcodes (fallback?) first {ops[0]:X}"); continue; }
                var s = Analyze(h, true); var jw = s.PcWrite;
                foreach (var op in ops) w.WriteLine($"{op:X4}\t{h:X8}\t{string.Join(" ", s.Sig)}\t{(s.Loop ? 1 : 0)}\t{(jw ? 1 : 0)}\t{(s.Unknown ? 1 : 0)}");
            }
        }, 256 * 1024 * 1024);
        t.Start(); t.Join();
        Console.WriteLine($"valid opcodes: {handlers.Values.Sum(l => l.Count)}, handlers: {handlers.Count}");
    }
}