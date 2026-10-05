using System.IO;
using System.Text;

// Disassembler for ShiinaRio .SCN bytecode. Opcode table (operand counts) comes from OpAnalyzer.
static class ScnDisasm
{
    public record OpInfo(string[] Sig, bool Jump, bool Special);
    public static Dictionary<int, OpInfo> LoadTable(string tsv)
    {
        var t = new Dictionary<int, OpInfo>();
        foreach (var line in File.ReadLines(tsv).Skip(1))
        {
            var f = line.Split('\t');
            var sig = f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            // runs of "b8" come from a file loader that walks its own +10h structure, not from the script
            if (sig.Count(x => x == "b8") >= 3) sig.RemoveAll(x => x == "b8" || x == "b");
            t[Convert.ToInt32(f[0], 16)] = new OpInfo(sig.ToArray(), f[4] == "1", f[3] == "1" || f[5] == "1");
        }
        return t;
    }

    static readonly Encoding Sjis = CreateSjis();
    static Encoding CreateSjis() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(932); }

    static string ReadZ(byte[] b, ref int p)
    {
        int s = p;
        while (p < b.Length && b[p] != 0) p++;
        var str = Sjis.GetString(b, s, p - s);
        p++; // NUL
        return str;
    }

    // One operand; returns text, advances p. Throws on unknown type.
    public static string Operand(byte[] b, ref int p, out int? target)
    {
        target = null;
        int at = p;
        byte t = b[p++];
        bool adr = (t & 0x40) != 0, rel = (t & 0x80) != 0;
        int k = adr ? t & 0x3F : t & 0x7F;
        string pre = (adr ? "&" : "") + (rel ? "@" : "");
        switch (k)
        {
            case 2: case 3: case 6: case 7: case 8: case 9: case 10: case 11: case 12: case 13: case 14: case 15:
                {
                    int v = BitConverter.ToUInt16(b, p); p += 2;
                    string space = (k & ~1) switch { 2 => "g", 6 => "s", 8 => "l", 10 => "a", 12 => "f", 14 => "b", _ => "?" };
                    return $"{pre}{space}{((k & 1) == 1 ? "*" : "")}[{v}]";
                }
            case 4: case 5:
                {
                    int v = BitConverter.ToInt32(b, p); p += 4;
                    if (rel && k == 4) { target = v; return $"L_{v:X5}"; }
                    return $"{pre}{(k == 5 ? "*" : "")}{v}";
                }
            case 0x10: return $"{pre}\"{ReadZ(b, ref p)}\"";
            case 0x11:
                {
                    bool fl = b[p] == 0x2E; if (fl) p++;
                    return $"{pre}expr{(fl ? "f" : "")}({ReadZ(b, ref p)})";
                }
            case 0x12: case 0x13:
                {
                    if (b[p] == 0x2E) throw new InvalidDataException($"sysvar operand at {at:X}");
                    int s = p;
                    while (p < b.Length && b[p] != 0 && b[p] != (byte)'}') p++;
                    var name = Sjis.GetString(b, s, p - s); p++;
                    return $"{pre}{(k == 0x13 ? "*" : "")}{name}";
                }
            default: throw new InvalidDataException($"bad operand type {t:X2} at {at:X}");
        }
    }

    public static readonly HashSet<int> NoFallthrough = new() { 0x258, 0x0000, 0x26C, 0x26D, 0x209 };   // goto, end of script, return, return value
    // Instructions whose address operands point at code although they do not jump there themselves
    public static readonly HashSet<int> CodeRefs = new() { 0x000C, 0x0281 };   // register a function (id, address), call with arguments
    // Variable-length instructions; "N2V" = u16 count followed by that many operands
    public static readonly Dictionary<int, string[]> Overrides = new()
    {
        [0x3CF] = new[] { "N2V" },   // local variable declarations
        [0x3CE] = new[] { "N2V" },   // global variable declarations
        [0x283] = new[] { "V", "V", "N2V" },
        [0x280] = new[] { "N2V" },   // a called function takes its arguments into locals
        [0x281] = new[] { "V", "V", "N2V" },   // call a function of this script with arguments
        [0x259] = new[] { "SW" },
        [0x2DB] = new[] { "VARGS" },  // message/printf: operands until an FF byte
        [0x209] = new[] { "CASE" }, // u32 index operand address, u32 target, values..., FF, u32 next case
        [0x0001] = new[] { "V", "V" },   // load module (slot, file)
        [0x0002] = new[] { "V", "V" },   // u32 table end, index, then targets up to the end
    };

    // Mnemonics for opcodes whose handler was read; everything else prints as op_XXXX
    public static readonly Dictionary<int, string> Names = new()
    {
        [0x0000] = "end", [0x0001] = "loadmod", [0x01F4] = "if", [0x0208] = "caseidx", [0x0209] = "case",
        [0x0258] = "goto", [0x0259] = "switch", [0x0267] = "gosub", [0x026C] = "ret", [0x026D] = "retv",
        [0x0283] = "callmod", [0x02D1] = "strcpy", [0x02D5] = "lea", [0x0302] = "load", [0x0303] = "store",
        [0x0316] = "salloc", [0x0317] = "sfree", [0x038E] = "mov", [0x038F] = "addr", [0x0393] = "add",
        [0x0394] = "sub", [0x0396] = "and", [0x0397] = "or", [0x0399] = "neg", [0x039A] = "shr", [0x039B] = "shl",
        [0x03CE] = "global", [0x03CF] = "local", [0x03DE] = "eval",
    };

    // Decodes one instruction; returns its text and literal branch targets.
    static (int len, string text, List<int> targets) Decode(byte[] b, int at, Dictionary<int, OpInfo> table)
    {
        int p = at; int op = BitConverter.ToUInt16(b, p); p += 2;
        if (!table.TryGetValue(op, out var info)) throw new InvalidDataException($"unknown opcode {op:X4} at {at:X}");
        var ops = new List<string>(); var tg = new List<int>();
        var sig = Overrides.TryGetValue(op, out var ov) ? ov : info.Sig;
        foreach (var item in sig)
        {
            if (item == "SW")
            {
                int end = BitConverter.ToInt32(b, p); p += 4;
                ops.Add($"end=L_{end:X5}"); ops.Add("idx=" + Operand(b, ref p, out _));
                var cases = new List<string>();
                while (p < end && p < b.Length) { cases.Add(Operand(b, ref p, out var ct)); if (ct.HasValue) tg.Add(ct.Value); }
                if (p != end) throw new InvalidDataException($"switch table overruns at {at:X}");
                ops.Add("[" + string.Join(" ", cases) + "]");
                continue;
            }
            if (item == "CASE")
            {
                int idxAt = BitConverter.ToInt32(b, p), target = BitConverter.ToInt32(b, p + 4); p += 8;
                var vals = new List<string>();
                while (p < b.Length && b[p] != 0xFF) vals.Add(Operand(b, ref p, out _));
                p++; // FF
                int next = BitConverter.ToInt32(b, p); p += 4;
                ops.Add($"idx@{idxAt:X}"); ops.Add("[" + string.Join(" ", vals) + "]"); ops.Add($"->L_{target:X5}"); ops.Add($"else ->L_{next:X5}");
                tg.Add(target); tg.Add(next);
                continue;
            }
            if (item == "VARGS")
            {
                // printf-like: operands until a 0xFF terminator byte
                var args = new List<string>();
                while (p < b.Length && b[p] != 0xFF) args.Add(Operand(b, ref p, out _));
                p++;
                ops.Add(string.Join(", ", args));
                continue;
            }
            if (item == "N2V")
            {
                int count = BitConverter.ToUInt16(b, p); p += 2; ops.Add($"#{count}");
                for (int i = 0; i < count; i++) ops.Add(Operand(b, ref p, out _));
                continue;
            }
            if (item == "V") { ops.Add(Operand(b, ref p, out var t)); if (t.HasValue && (info.Jump || CodeRefs.Contains(op))) tg.Add(t.Value); continue; }
            int n = int.Parse(item[1..]);
            if (n == 1) ops.Add($"<{b[p]}>");
            else if (n == 2) ops.Add($"<{BitConverter.ToUInt16(b, p)}>");
            else if (n == 4) { int v = BitConverter.ToInt32(b, p); ops.Add($"->L_{v:X5}"); if (info.Jump) tg.Add(v); }
            else ops.Add("<" + Convert.ToHexString(b, p, n) + ">");
            p += n;
        }
        string note = "";
        // "if (A op B) else goto L" with constant operands: an unconditional jump or a no-op
        if (op == 0x1F4 && ops.Count == 4 && int.TryParse(ops[0], out int ca) && int.TryParse(ops[2], out int cb))
        {
            uint ua = (uint)ca, ub = (uint)cb; int cmp = int.Parse(ops[1].Trim('<', '>'));
            bool falls = cmp switch { 0 => ua == ub, 1 => ua != ub, 2 => ua >= ub, 3 => ua > ub, 4 => ua <= ub, 5 => ua < ub, 6 => (ua & ub) != 0, _ => (ua | ub) != 0 };
            note = falls ? "   ; never jumps" : "   ; always jumps";
        }
        string mn = Names.TryGetValue(op, out var nm) ? nm : $"op_{op:X4}";
        if (op == 0x1F4 && ops.Count == 4)
        {
            string[] cmpOps = { "==", "!=", ">=", ">", "<=", "<", "&", "|" };
            int ci = int.Parse(ops[1].Trim('<', '>'));
            return (p - at, $"{op:X4}  if       {ops[0]} {(ci < 8 ? cmpOps[ci] : "?" + ci)} {ops[2]} else {ops[3]}{note}", tg);
        }
        return (p - at, $"{op:X4}  {mn,-8} {string.Join(", ", ops)}{note}", tg);
    }

    // Computed jumps "f = idx; f <<= 2; f += TABLE; f = *f; goto f": the table holds u32 code addresses
    static IEnumerable<int> FindAddressTables(byte[] b, SortedDictionary<int, (int len, string text, List<int> targets)> ins)
    {
        var list = ins.ToList();
        for (int i = 0; i + 2 < list.Count; i++)
        {
            var t = list[i].Value.text;
            if (!t.StartsWith("0393")) continue;
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^0393\s+add\s+(\d+),");
            if (!m.Success) continue;
            bool isJump = list.Skip(i + 1).Take(3).Any(x => x.Value.text.StartsWith("0258") && !x.Value.text.Contains("L_"));
            if (!isJump) continue;
            int tab = int.Parse(m.Groups[1].Value);
            for (int k = tab; k + 4 <= b.Length; k += 4)
            {
                int v = BitConverter.ToInt32(b, k);
                if (v <= 0 || v >= b.Length) { yield return k; break; }   // code may continue right after the table
                yield return v;
            }
        }
    }

    // u32 values inside not-yet-decoded bytes that point into the file (callback tables and the like)
    static IEnumerable<int> DataPointers(byte[] b, SortedDictionary<int, (int len, string text, List<int> targets)> ins)
    {
        var covered = new bool[b.Length];
        foreach (var (o, d) in ins) for (int i = o; i < o + d.len && i < b.Length; i++) covered[i] = true;
        for (int k = 0; k + 4 <= b.Length; k++)
        {
            if (covered[k] || covered[k + 3]) continue;
            int v = BitConverter.ToInt32(b, k);
            if (v > 0x10 && v < b.Length - 2 && !covered[v]) yield return v;
        }
    }

    // Recursive descent from offset 0: follows fall-through and literal branch targets; bytes never
    // reached are shown as data (they hold inline tables such as the choice texts).
    public static List<string> Disassemble(byte[] b, Dictionary<int, OpInfo> table, out int covered, out List<string> errors)
    {
        var ins = new SortedDictionary<int, (int len, string text, List<int> targets)>();
        var labels = new HashSet<int>(); errors = new List<string>();
        var work = new Stack<(int at, bool spec)>(); work.Push((0, false));
        var triedSpec = new HashSet<int>();
        while (work.Count > 0)
        {
            var (start, spec) = work.Pop();
            bool fromData = start < 0; if (fromData) start = -start - 1;
            // decode one path; speculative paths are kept only if they decode cleanly
            var path = new List<(int at, (int len, string text, List<int> targets) d)>();
            var occupied = new HashSet<int>();
            int p = start; bool ok = true, endsHard = false;
            while (p >= 0 && p + 1 < b.Length && !ins.ContainsKey(p))
            {
                (int len, string text, List<int> targets) d;
                try { d = Decode(b, p, table); }
                catch (Exception ex) { if (!fromData) errors.Add((spec ? $"spec@{start:X}: " : "") + ex.Message); ok = false; break; }
                // must not run into the middle of a known instruction
                var prev = ins.Keys.Where(k => k < p + d.len && k > p).ToList();
                if (prev.Count > 0) { ok = false; if (!spec) errors.Add($"overlap at {p:X}"); break; }
                path.Add((p, d));
                if (NoFallthrough.Contains(BitConverter.ToUInt16(b, p)) || d.text.EndsWith("; always jumps")) { endsHard = true; p += d.len; break; }
                p += d.len;
            }
            if (spec && (!ok || path.Count == 0)) continue;
            if (fromData && path.Count < 3) continue;   // pointer found in data: accept only a real-looking run
            if (fromData) labels.Add(start);
            foreach (var (at, d) in path)
            {
                ins[at] = d;
                foreach (var t in d.targets) { labels.Add(t); if (t >= 0 && t < b.Length) work.Push((t, false)); else errors.Add($"target {t:X} out of range at {at:X}"); }
            }
            if (endsHard && p < b.Length && triedSpec.Add(p)) work.Push((p, true));
            if (work.Count == 0)
                foreach (var t in FindAddressTables(b, ins).ToList())
                    if (!ins.ContainsKey(t) && triedSpec.Add(t)) { work.Push((t, true)); labels.Add(t); }
            if (work.Count == 0)
                foreach (var t in DataPointers(b, ins).ToList())
                    if (!ins.ContainsKey(t) && triedSpec.Add(t)) work.Push((-t - 1, true));   // negative = found in data
        }
        var outp = new List<string>(); covered = 0; int pos = 0;
        void Data(int from, int to)
        {
            if (to <= from) return;
            var sb = new StringBuilder();
            int s = from;
            for (int i = from; i <= to; i++)
            {
                if (i == to || b[i] == 0)
                {
                    if (i > s) sb.Append($" \"{Sjis.GetString(b, s, i - s)}\"");
                    if (i < to) sb.Append(" 00");
                    s = i + 1;
                }
            }
            var text = sb.ToString().Replace("\r", "\\r").Replace("\n", "\\n");
            outp.Add($"  {from:X5}  .data[{to - from}]{(text.Length > 300 ? text[..300] + " ..." : text)}");
        }
        foreach (var (off, d) in ins)
        {
            if (off < pos) { outp.Add($"  {off:X5}  !! overlapping instruction"); }
            else Data(pos, off);
            if (labels.Contains(off)) outp.Add($"L_{off:X5}:");
            outp.Add($"  {off:X5}  {d.text}");
            covered += d.len; pos = Math.Max(pos, off + d.len);
        }
        Data(pos, b.Length);
        foreach (var l in labels.Where(l => !ins.ContainsKey(l))) errors.Add($"label {l:X} is not an instruction");
        return outp;
    }

    // --scndis <table.tsv> <out.txt> <file.scn>...
    public static void Run(string[] a)
    {
        var table = LoadTable(a[0]);
        using var w = new StreamWriter(a[1]);
        foreach (var f in a.Skip(2))
        {
            var b = File.ReadAllBytes(f);
            var lines = Disassemble(b, table, out int covered, out var errs);
            Console.WriteLine($"{Path.GetFileName(f)}: {b.Length} bytes, code {covered} ({100.0 * covered / b.Length:F1}%), {lines.Count} lines, {errs.Count} problems{(errs.Count > 0 ? ": " + string.Join(" | ", errs.Take(5)) : "")}");
            w.WriteLine($"===== {f}");
            foreach (var l in lines) w.WriteLine(l);
            foreach (var e in errs) w.WriteLine("!!! " + e);
        }
    }
}