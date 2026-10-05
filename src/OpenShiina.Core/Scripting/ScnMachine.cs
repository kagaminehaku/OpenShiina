// A small interpreter for the story flow script SRC_MAIN.SCN, so that every game plays its scenario
// files in its own order (docs/engine-notes.md, sections 4 and 9). It runs the part of the
// ShiinaRio bytecode these scripts use: variables, arithmetic, expressions, jumps, switches and
// local calls. Engine functions are handed to the host: "gosub 240" runs the scenario file named by
// the local "filename", "callmod 0,203" asks a choice. Drawing instructions (the staff roll) do
// nothing, and the clock they wait on runs fast.
//
// Bytecode: u16 opcode, then operands. A typed operand starts with a byte: bit 6 = address of,
// bit 7 = reference, low bits = kind: 2/6/8/10/12/14 = variable g / s / l / a / f / b [u16 index],
// 4 = u32 constant (a code address with bit 7), 0x10 = string, 0x11 = expression text,
// 0x12 = named variable. Variables live in a flat 32-bit memory; code is at address 0.

using System.IO;

namespace OpenShiina.Scripting;

public sealed class ScnMachine
{
    /// <summary>Engine functions the flow script calls.</summary>
    public interface IHost
    {
        /// <summary>gosub 240: plays a scenario file ("p\ore01.txt").</summary>
        Task RunScenarioAsync(string file);

        /// <summary>callmod: an engine function with arguments; the result goes to the first operand.</summary>
        Task<int> CallAsync(int function, int[] args);
    }

    // Operand layout of each instruction: V = typed operand, bN = N raw bytes,
    // N2V = u16 count then operands, SW = switch table, CASE = case entry
    private static readonly Dictionary<int, string[]> Layouts = new()
    {
        [0x0000] = ["V"], [0x0001] = ["V", "V"], [0x000D] = ["V"], [0x001E] = [], [0x002A] = ["V"], [0x0034] = [],
        [0x01F4] = ["V", "b1", "V", "b4"], [0x0208] = ["V"], [0x0209] = ["CASE"], [0x0258] = ["V"], [0x0259] = ["SW"],
        [0x0262] = ["V"], [0x0267] = ["V"], [0x026C] = [], [0x026D] = ["V"], [0x0280] = ["N2V"], [0x0283] = ["V", "V", "N2V"],
        [0x02C6] = ["V", "V", "V"], [0x02D1] = ["V", "V"], [0x02D5] = ["V", "V"], [0x02F8] = ["V"], [0x02F9] = ["V"],
        [0x0302] = ["V", "V"], [0x0303] = ["V", "V"], [0x0308] = ["V", "V", "V"], [0x0309] = ["V", "V", "V"],
        [0x0316] = ["V"], [0x0317] = ["V"], [0x038E] = ["V", "V"], [0x038F] = ["V", "V"], [0x0391] = ["V"], [0x0392] = ["V"],
        [0x0393] = ["V", "V"], [0x0394] = ["V", "V"], [0x0396] = ["V", "V"], [0x0397] = ["V", "V"], [0x0399] = ["V"],
        [0x039A] = ["V", "V"], [0x039B] = ["V", "V"], [0x039E] = ["V", "V"], [0x03A0] = ["V", "V", "V"], [0x03BD] = ["V"],
        [0x03CF] = ["N2V"], [0x03D0] = ["V", "V"], [0x03DE] = ["V", "b1", "V"], [0x03EA] = ["V"],
        [0x04B0] = ["V", "V"], [0x04B1] = ["V"], [0x04BA] = [], [0x04BD] = ["V", "V", "V", "V", "V", "V", "V", "V"],
        [0x04C4] = ["V"], [0x04CE] = ["V"], [0x04D8] = ["V", "V", "V", "V", "V", "V", "V", "V"],
        [0x04E2] = ["V", "V", "V", "V", "V", "V", "V", "V"], [0x0780] = ["V", "V", "V", "V"], [0x07D0] = ["V", "V", "V", "V"],
    };

    private enum Kind { Constant, Variable, String, Expression, Name }

    private readonly record struct Operand(Kind Kind, int Value, char Space, string? Text, bool AddressOf);

    private sealed record Instruction(int Op, int Length, Operand[] Args, int[] Raw, int[] Targets, int[] CaseValues);

    // Memory: code at 0, then one region per variable space; named variables and frames on top
    private const int PageBits = 12, PageSize = 1 << PageBits;
    private static readonly Dictionary<char, int> SpaceBase = new()
    {
        ['a'] = 0x01000000, ['b'] = 0x02000000, ['f'] = 0x03000000, ['g'] = 0x04000000, ['s'] = 0x05000000,
    };
    private const int NamedBase = 0x06000000, StackBase = 0x07000000;

    private readonly byte[] m_code;
    private readonly Dictionary<int, byte[]> m_pages = new();
    private readonly Dictionary<int, Instruction> m_decoded = new();
    private readonly Dictionary<string, int> m_globals = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, int>> m_scopes = new();
    private readonly List<int> m_frames = new();
    private readonly Stack<int> m_returns = new();
    private readonly Stack<int> m_values = new();
    private int m_stackTop = StackBase, m_namedTop = NamedBase;
    private int m_caseValue;
    private int m_clock;
    private List<(int Address, Instruction Ins)>? m_walked;

    public ScnMachine(byte[] code)
    {
        m_code = code;
        for (int at = 0; at < code.Length; at += PageSize)
            Array.Copy(code, at, Page(at), 0, Math.Min(PageSize, code.Length - at));
    }

    #region Memory

    private byte[] Page(int address)
    {
        int page = address >>> PageBits;
        if (!m_pages.TryGetValue(page, out var bytes))
            m_pages[page] = bytes = new byte[PageSize];
        return bytes;
    }

    private byte ReadByte(int address) => Page(address)[address & (PageSize - 1)];

    private void WriteByte(int address, byte value) => Page(address)[address & (PageSize - 1)] = value;

    private int Read32(int address) =>
        ReadByte(address) | ReadByte(address + 1) << 8 | ReadByte(address + 2) << 16 | ReadByte(address + 3) << 24;

    private void Write32(int address, int value)
    {
        for (int i = 0; i < 4; i++)
            WriteByte(address + i, (byte)(value >> (8 * i)));
    }

    private string ReadString(int address)
    {
        var bytes = new List<byte>();
        for (byte c; (c = ReadByte(address)) != 0 && bytes.Count < 4096; address++)
            bytes.Add(c);
        return Encodings.cp932.GetString(bytes.ToArray());
    }

    private int Allocate(int bytes)
    {
        int at = m_stackTop;
        m_stackTop += (bytes + 3) & ~3;
        for (int i = at; i < m_stackTop; i++)
            WriteByte(i, 0);
        return at;
    }

    /// <summary>a[index]: the variables the scenario files share (_D710 ...).</summary>
    public int GetA(int index) => Read32(SpaceBase['a'] + 4 * index);

    public void SetA(int index, int value) => Write32(SpaceBase['a'] + 4 * index, value);

    public int GetB(int index) => Read32(SpaceBase['b'] + 4 * index);

    public void SetB(int index, int value) => Write32(SpaceBase['b'] + 4 * index, value);

    /// <summary>The global variables (a[], b[] and named ones) that are not zero, for a save.</summary>
    public Dictionary<string, int> Snapshot()
    {
        var vars = new Dictionary<string, int>();
        foreach (char space in "ab")
        {
            int start = SpaceBase[space];
            foreach (var (page, bytes) in m_pages)
            {
                int address = page << PageBits;
                if (address < start || address >= start + 0x01000000)
                    continue;
                for (int i = 0; i < PageSize; i += 4)
                    if (BitConverter.ToInt32(bytes, i) is int v and not 0)
                        vars[$"{space}{(address + i - start) / 4}"] = v;
            }
        }
        foreach (var (name, address) in m_globals)
            if (Read32(address) is int v and not 0)
                vars["$" + name] = v;
        return vars;
    }

    public void Restore(IReadOnlyDictionary<string, int> vars)
    {
        foreach (var (key, value) in vars)
        {
            if (key.StartsWith('$'))
                Write32(Global(key[1..]), value);
            else if (key.Length > 1 && SpaceBase.TryGetValue(key[0], out int start) && int.TryParse(key[1..], out int index))
                Write32(start + 4 * index, value);
        }
    }

    private int Global(string name)
    {
        if (!m_globals.TryGetValue(name, out int address))
        {
            address = m_namedTop;
            m_namedTop += 4;
            m_globals[name] = address;
        }
        return address;
    }

    private int NameAddress(string name)
    {
        for (int i = m_scopes.Count - 1; i >= 0; i--)
            if (m_scopes[i].TryGetValue(name, out int address))
                return address;
        return Global(name);
    }

    #endregion

    #region Decoding

    private Instruction Decode(int at)
    {
        if (m_decoded.TryGetValue(at, out var cached))
            return cached;
        int p = at;
        int op = m_code[p] | m_code[p + 1] << 8;
        p += 2;
        if (!Layouts.TryGetValue(op, out var layout))
            throw new InvalidDataException($"SRC_MAIN: instruction {op:X4} at {at:X} is not supported.");
        var args = new List<Operand>();
        var raw = new List<int>();
        var targets = new List<int>();
        var caseValues = new List<int>();
        foreach (var item in layout)
        {
            switch (item)
            {
                case "V":
                    args.Add(ReadOperand(ref p));
                    break;
                case "b1":
                    raw.Add(m_code[p]);
                    p += 1;
                    break;
                case "b4":
                    raw.Add(BitConverter.ToInt32(m_code, p));
                    p += 4;
                    break;
                case "N2V":
                {
                    int count = BitConverter.ToUInt16(m_code, p);
                    p += 2;
                    raw.Add(count);
                    for (int i = 0; i < count; i++)
                        args.Add(ReadOperand(ref p));
                    break;
                }
                case "SW":
                {
                    int end = BitConverter.ToInt32(m_code, p);
                    p += 4;
                    raw.Add(end);
                    args.Add(ReadOperand(ref p));
                    while (p < end)
                        targets.Add(ReadOperand(ref p).Value);
                    break;
                }
                case "CASE":
                {
                    // u32 address of the index operand, u32 target, values, FF, u32 next case
                    int target = BitConverter.ToInt32(m_code, p + 4);
                    p += 8;
                    while (m_code[p] != 0xFF)
                        caseValues.Add(ReadOperand(ref p).Value);
                    p++;
                    raw.Add(target);
                    raw.Add(BitConverter.ToInt32(m_code, p));
                    p += 4;
                    break;
                }
            }
        }
        var instruction = new Instruction(op, p - at, args.ToArray(), raw.ToArray(), targets.ToArray(), caseValues.ToArray());
        m_decoded[at] = instruction;
        return instruction;
    }

    private Operand ReadOperand(ref int p)
    {
        int at = p;
        byte type = m_code[p++];
        bool addressOf = (type & 0x40) != 0;
        int kind = addressOf ? type & 0x3F : type & 0x7F;
        switch (kind)
        {
            case 2 or 6 or 8 or 10 or 12 or 14:
            {
                int index = BitConverter.ToUInt16(m_code, p);
                p += 2;
                char space = kind switch { 2 => 'g', 6 => 's', 8 => 'l', 10 => 'a', 12 => 'f', _ => 'b' };
                return new Operand(Kind.Variable, index, space, null, addressOf);
            }
            case 4:
            {
                int value = BitConverter.ToInt32(m_code, p);
                p += 4;
                return new Operand(Kind.Constant, value, '\0', null, false);
            }
            case 0x10:
            {
                int start = p;
                while (m_code[p] != 0)
                    p++;
                p++;
                return new Operand(Kind.String, start, '\0', null, false);
            }
            case 0x11:
            {
                if (m_code[p] == 0x2E)
                    p++;
                int start = p;
                while (m_code[p] != 0)
                    p++;
                string text = Encodings.cp932.GetString(m_code, start, p - start);
                p++;
                return new Operand(Kind.Expression, 0, '\0', text, false);
            }
            case 0x12:
            {
                int start = p;
                while (m_code[p] != 0 && m_code[p] != (byte)'}')
                    p++;
                string name = Encodings.cp932.GetString(m_code, start, p - start);
                p++;
                return new Operand(Kind.Name, 0, '\0', name, addressOf);
            }
            default:
                throw new InvalidDataException($"SRC_MAIN: operand type {type:X2} at {at:X} is not supported.");
        }
    }

    #endregion

    #region Values

    private int Address(Operand o) => o.Kind switch
    {
        Kind.Variable when o.Space == 'l' => (m_frames.Count > 0 ? m_frames[^1] : Frame0()) + 4 * o.Value,
        Kind.Variable => SpaceBase[o.Space] + 4 * o.Value,
        Kind.Name => NameAddress(o.Text!),
        _ => throw new InvalidDataException("SRC_MAIN: an operand without an address was written."),
    };

    private int Frame0()
    {
        m_frames.Add(Allocate(64 * 4));
        return m_frames[0];
    }

    private int Value(Operand o) => o.Kind switch
    {
        Kind.Constant or Kind.String => o.Value,
        Kind.Expression => Evaluate(o.Text!),
        _ when o.AddressOf => Address(o),
        _ => Read32(Address(o)),
    };

    private void Write(Operand o, int value)
    {
        if (o.Kind is Kind.Variable or Kind.Name)
            Write32(Address(o), value);
    }

    /// <summary>
    /// Expression text of eval / expr(): integers, _Dn = a[n], _Sn = b[n], _Ln = f[n], _Zn = l[n],
    /// {name}, shl(x,y), C operators.
    /// </summary>
    private int Evaluate(string text)
    {
        int at = 0;
        int result = Or();
        return result;

        void Skip()
        {
            while (at < text.Length && text[at] == ' ')
                at++;
        }
        bool Take(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, at, token, 0, token.Length) != 0)
                return false;
            at += token.Length;
            return true;
        }
        int Or()
        {
            int v = And();
            while (true)
            {
                if (Take("||")) v = (v != 0) | (And() != 0) ? 1 : 0;
                else if (Peek('|')) { at++; v |= And(); }
                else return v;
            }
        }
        int And()
        {
            int v = Compare();
            while (true)
            {
                if (Take("&&")) v = (v != 0) & (Compare() != 0) ? 1 : 0;
                else if (Peek('&')) { at++; v &= Compare(); }
                else return v;
            }
        }
        bool Peek(char c)
        {
            Skip();
            return at < text.Length && text[at] == c && (at + 1 >= text.Length || text[at + 1] != c);
        }
        int Compare()
        {
            int v = Shift();
            while (true)
            {
                if (Take("==")) v = v == Shift() ? 1 : 0;
                else if (Take("!=")) v = v != Shift() ? 1 : 0;
                else if (Take(">=")) v = v >= Shift() ? 1 : 0;
                else if (Take("<=")) v = v <= Shift() ? 1 : 0;
                else if (!LookShift() && Take(">")) v = v > Shift() ? 1 : 0;
                else if (!LookShift() && Take("<")) v = v < Shift() ? 1 : 0;
                else return v;
            }
        }
        bool LookShift()
        {
            Skip();
            return at + 1 < text.Length && (text[at] == '<' && text[at + 1] == '<' || text[at] == '>' && text[at + 1] == '>');
        }
        int Shift()
        {
            int v = Sum();
            while (true)
            {
                if (Take("<<")) v <<= Sum();
                else if (Take(">>")) v >>= Sum();
                else return v;
            }
        }
        int Sum()
        {
            int v = Product();
            while (true)
            {
                if (Take("+")) v += Product();
                else if (Take("-")) v -= Product();
                else return v;
            }
        }
        int Product()
        {
            int v = Unary();
            while (true)
            {
                if (Take("*")) v *= Unary();
                else if (Take("/")) { int d = Unary(); v = d == 0 ? 0 : v / d; }
                else if (Take("%")) { int d = Unary(); v = d == 0 ? 0 : v % d; }
                else return v;
            }
        }
        int Unary()
        {
            if (Take("-")) return -Unary();
            if (Take("!")) return Unary() == 0 ? 1 : 0;
            if (Take("~")) return ~Unary();
            return Primary();
        }
        int Primary()
        {
            Skip();
            if (Take("("))
            {
                int v = Or();
                Take(")");
                return v;
            }
            if (Take("{"))
            {
                int end = text.IndexOf('}', at);
                string name = text[at..end];
                at = end + 1;
                return Read32(NameAddress(name));
            }
            if (Take("shl("))
            {
                int x = Or();
                Take(",");
                int y = Or();
                Take(")");
                return x << y;
            }
            if (at < text.Length && text[at] == '_' && at + 1 < text.Length)
            {
                char space = text[at + 1];
                at += 2;
                int index = Number();
                return space switch
                {
                    'D' => Read32(SpaceBase['a'] + 4 * index),
                    'S' => Read32(SpaceBase['b'] + 4 * index),
                    'L' => Read32(SpaceBase['f'] + 4 * index),
                    'Z' => Read32((m_frames.Count > 0 ? m_frames[^1] : Frame0()) + 4 * index),
                    _ => throw new InvalidDataException($"SRC_MAIN: expression \"{text}\" is not supported."),
                };
            }
            return Number();
        }
        int Number()
        {
            Skip();
            int start = at;
            while (at < text.Length && char.IsDigit(text[at]))
                at++;
            if (start == at)
                throw new InvalidDataException($"SRC_MAIN: expression \"{text}\" is not supported.");
            return int.Parse(text[start..at]);
        }
    }

    #endregion

    #region Running

    /// <summary>Runs from <paramref name="start"/> until the script ends or loads another module.</summary>
    public async Task RunAsync(IHost host, CancellationToken token, int start = 0)
    {
        int pc = start;
        int budget = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // A loop that never reaches the host is a script this interpreter does not understand
            if (++budget > 2_000_000)
                throw new InvalidOperationException($"SRC_MAIN: no progress near {pc:X}.");

            var ins = Decode(pc);
            var a = ins.Args;
            int next = pc + ins.Length;
            switch (ins.Op)
            {
                case 0x0000:    // end
                case 0x0001:    // load another module (topmenu.scn after the ending)
                case 0x000D:    // quit
                case 0x0780:    // error message
                    return;

                case 0x01F4:    // if A cmp B else goto L
                {
                    int x = Value(a[0]), y = Value(a[1]);
                    bool holds = ins.Raw[0] switch
                    {
                        0 => x == y, 1 => x != y, 2 => x >= y, 3 => x > y, 4 => x <= y, 5 => x < y,
                        6 => (x & y) != 0, _ => (x | y) != 0,
                    };
                    if (!holds)
                        next = ins.Raw[1];
                    break;
                }
                case 0x0258:    // goto
                    next = Value(a[0]);
                    break;
                case 0x0259:    // switch: targets[idx], else the end of the table
                {
                    int index = Value(a[0]);
                    next = index >= 0 && index < ins.Targets.Length ? ins.Targets[index] : ins.Raw[0];
                    break;
                }
                case 0x0208:    // caseidx
                    m_caseValue = Value(a[0]);
                    break;
                case 0x0209:    // case values -> target, else the next case
                    next = ins.CaseValues.Contains(m_caseValue) ? ins.Raw[0] : ins.Raw[1];
                    break;
                case 0x0262:    // call a label of this script
                    m_returns.Push(next);
                    next = Value(a[0]);
                    break;
                case 0x026C:    // ret
                case 0x026D:    // retv
                    if (m_returns.Count == 0)
                        return;
                    next = m_returns.Pop();
                    break;

                case 0x0267:    // gosub: an engine function
                    budget = 0;
                    if (Value(a[0]) == 240)
                        await host.RunScenarioAsync(ReadString(Read32(NameAddress("filename"))));
                    break;
                case 0x0283:    // callmod result, function, #n args
                {
                    budget = 0;
                    var args = a.Skip(2).Select(Value).ToArray();
                    int function = Value(a[1]);
                    int result = await host.CallAsync(function, args);
                    if (function == 203)
                        Write32(Global("g$sel"), result);
                    Write(a[0], result);
                    break;
                }

                case 0x03CF:    // local #n names: a new scope; #0 leaves it
                    if (ins.Raw[0] == 0)
                    {
                        if (m_scopes.Count > 0)
                            m_scopes.RemoveAt(m_scopes.Count - 1);
                    }
                    else
                    {
                        var scope = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var name in a)
                            scope[name.Text ?? ""] = Allocate(4);
                        m_scopes.Add(scope);
                    }
                    break;
                case 0x0280:    // a called label takes its arguments
                    for (int i = a.Length - 1; i >= 0; i--)
                        Write(a[i], m_values.Count > 0 ? m_values.Pop() : 0);
                    break;
                case 0x0316:    // salloc n
                    m_frames.Add(Allocate(Math.Max(1, Value(a[0])) * 4));
                    break;
                case 0x0317:    // sfree
                    if (m_frames.Count > 0)
                        m_frames.RemoveAt(m_frames.Count - 1);
                    break;
                case 0x02F8:    // push
                    m_values.Push(Value(a[0]));
                    break;
                case 0x02F9:    // pop
                    Write(a[0], m_values.Count > 0 ? m_values.Pop() : 0);
                    break;

                case 0x038E: Write(a[1], Value(a[0])); break;                  // mov
                case 0x038F: Write(a[1], Address(a[0])); break;                // addr
                case 0x02D5: Write(a[1], Value(a[0])); break;                  // lea
                case 0x0393: Write(a[1], Value(a[1]) + Value(a[0])); break;    // add
                case 0x0394: Write(a[1], Value(a[1]) - Value(a[0])); break;    // sub
                case 0x0396: Write(a[1], Value(a[1]) & Value(a[0])); break;    // and
                case 0x0397: Write(a[1], Value(a[1]) | Value(a[0])); break;    // or
                case 0x039A: Write(a[1], Value(a[1]) >> Value(a[0])); break;   // shr
                case 0x039B: Write(a[1], Value(a[1]) << Value(a[0])); break;   // shl
                case 0x039E: Write(a[1], Value(a[1]) * Value(a[0])); break;    // mul
                case 0x0399: Write(a[0], -Value(a[0])); break;                 // neg
                case 0x0391: Write(a[0], Value(a[0]) + 1); break;              // inc
                case 0x0392: Write(a[0], Value(a[0]) - 1); break;              // dec
                case 0x03A0:    // div: divisor, value (quotient), remainder
                {
                    int d = Value(a[0]), v = Value(a[1]);
                    Write(a[1], d == 0 ? 0 : v / d);
                    Write(a[2], d == 0 ? 0 : v % d);
                    break;
                }
                case 0x03DE:    // eval "expression", mode, result
                    Write(a[1], Evaluate(ReadString(Value(a[0]))));
                    break;
                case 0x0302: Write(a[1], Read32(Value(a[0]))); break;          // load
                case 0x0303: Write32(Value(a[0]), Value(a[1])); break;         // store
                case 0x0308: Write(a[2], Read32(Value(a[0]) + Value(a[1]))); break;
                case 0x0309: Write32(Value(a[0]) + Value(a[1]), Value(a[2])); break;
                case 0x02D1:    // strcpy dst, src
                {
                    int to = Value(a[0]), from = Value(a[1]);
                    for (byte c; ; to++, from++)
                    {
                        WriteByte(to, c = ReadByte(from));
                        if (c == 0)
                            break;
                    }
                    break;
                }
                case 0x02C6:    // memcpy dst, src, bytes
                {
                    int to = Value(a[0]), from = Value(a[1]), count = Value(a[2]);
                    for (int i = 0; i < count && i < 0x100000; i++)
                        WriteByte(to + i, ReadByte(from + i));
                    break;
                }
                case 0x03BD:    // the time in ms; runs fast so that waiting loops end at once
                    m_clock += 100;
                    Write(a[0], m_clock);
                    break;

                default:        // drawing, sound and frame waits of the staff roll
                    break;
            }
            pc = next;
        }
    }

    /// <summary>
    /// Every instruction that can be decoded by following the code from the start, in address
    /// order. Code after a jump or an end is tried too: it is reached through tables (the b[250]
    /// scene table at the start) or is data, where an undecodable byte stops that path.
    /// </summary>
    private List<(int Address, Instruction Ins)> Walk()
    {
        if (m_walked != null)
            return m_walked;
        var found = m_walked = new List<(int, Instruction)>();
        var seen = new HashSet<int>();
        var work = new Stack<int>();
        work.Push(0);
        while (work.Count > 0)
        {
            int pc = work.Pop();
            while (pc >= 0 && pc + 1 < m_code.Length && seen.Add(pc))
            {
                Instruction ins;
                try
                {
                    ins = Decode(pc);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
                {
                    break;
                }
                found.Add((pc, ins));
                var a = ins.Args;
                foreach (int t in ins.Targets)
                    work.Push(t);
                if (ins.Op == 0x01F4)
                    work.Push(ins.Raw[1]);
                if (ins.Op == 0x0209)
                {
                    work.Push(ins.Raw[0]);
                    work.Push(ins.Raw[1]);
                }
                if (ins.Op == 0x0259)
                    work.Push(ins.Raw[0]);
                if (ins.Op == 0x0262 && a[0].Kind == Kind.Constant)
                    work.Push(a[0].Value);
                if (ins.Op == 0x0258 && a[0].Kind == Kind.Constant)
                    work.Push(a[0].Value);
                if (ins.Op is 0x0000 or 0x0001 or 0x000D or 0x0209 or 0x0259 or 0x026C or 0x026D or 0x0258)
                {
                    work.Push(pc + ins.Length);
                    break;
                }
                pc += ins.Length;
            }
        }
        found.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return found;
    }

    /// <summary>
    /// The scenario files in code order with the scene number set before each ("mov n, @b[250]");
    /// Address is that of the "gosub 240" that plays the file.
    /// </summary>
    public List<(int Address, string File, int Scene)> FindScenes()
    {
        var found = new List<(int, string, int)>();
        int scene = -1;
        string? file = null;
        foreach (var (pc, ins) in Walk())
        {
            var a = ins.Args;
            if (ins.Op == 0x038E && a[1].Kind == Kind.Variable && a[1].Space == 'b' && a[1].Value == 250 && a[0].Kind == Kind.Constant)
                scene = a[0].Value;
            else if (ins.Op == 0x02D5 && a[0].Kind == Kind.String && a[1].Kind == Kind.Name && a[1].Text == "filename")
                file = ReadString(a[0].Value);
            else if (ins.Op == 0x0267 && a[0].Kind == Kind.Constant && a[0].Value == 240 && file != null)
            {
                found.Add((pc, file, scene));
                file = null;
            }
        }
        return found;
    }

    /// <summary>The route menu: its options, and where each route's code starts and ends.</summary>
    public sealed record RouteMenu(int Address, string[] Options, int[] Starts, int[] Ends)
    {
        /// <summary>The route whose code holds <paramref name="address"/>, or -1.</summary>
        public int RouteAt(int address)
        {
            for (int r = 0; r < Starts.Length; r++)
                if (address >= Starts[r] && address < Ends[r])
                    return r;
            return -1;
        }
    }

    /// <summary>
    /// The route menu of the flow (Oreimo: 0x24D): the choice "callmod 0,203,#5, mode, ?, table,
    /// kind, mask" whose mask of played options is a variable (a[780]). Its table comes from the
    /// "mov table, @p" before it, its routes from the switch after it (option i jumps to target
    /// i), and each route ends with the goto back to the loop before the menu. Null when the flow
    /// has no such menu.
    /// </summary>
    public RouteMenu? FindRouteMenu()
    {
        var code = Walk();
        for (int i = 0; i < code.Count; i++)
        {
            var (at, ins) = code[i];
            var a = ins.Args;
            if (ins.Op != 0x0283 || a.Length < 7 || a[1].Kind != Kind.Constant || a[1].Value != 203 || a[6].Kind != Kind.Variable)
                continue;

            int table = -1;
            for (int j = i - 1; j >= Math.Max(0, i - 4) && table < 0; j--)
            {
                var set = code[j].Ins;
                if (set.Op == 0x038E && set.Args[0].Kind == Kind.Constant && set.Args[1].Kind == Kind.Name && set.Args[1].Text == a[4].Text)
                    table = set.Args[0].Value;
            }
            var routes = code.Skip(i + 1).Select(c => c.Ins).FirstOrDefault(c => c.Op == 0x0259);
            if (table < 0 || routes == null)
                continue;

            var starts = routes.Targets;
            var ends = starts.Select(start => code
                .Where(c => c.Address > start && c.Ins.Op == 0x0258 && c.Ins.Args[0].Kind == Kind.Constant && c.Ins.Args[0].Value < at)
                .Select(c => c.Address).DefaultIfEmpty(start).First()).ToArray();
            return new RouteMenu(at, ReadChoices(table), starts, ends);
        }
        return null;
    }

    /// <summary>The options of a choice table (callmod 203): a count byte, then zero-terminated texts.</summary>
    public string[] ReadChoices(int address)
    {
        int count = ReadByte(address++);
        var list = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string s = ReadString(address);
            list.Add(s);
            address += Encodings.cp932.GetByteCount(s) + 1;
        }
        return list.ToArray();
    }

    #endregion
}
