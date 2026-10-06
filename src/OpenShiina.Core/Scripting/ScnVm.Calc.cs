// Expression operands ("expr(...)", eval) as the engine's calculator (rio/Calc.cpp) works them out:
// every value is a double; '+' and '-' bind loosest, every other binary operator has the same
// precedence and runs left to right; ',' separates expressions (the last one gives the result).
//
// Terms: numbers (decimal, 0x hex, with a fraction), _Xnnn variables (exactly three digits:
// D = a[], L = f[] of the slot, M = g[], S = b[], Z = l[] on the stack) with an optional suffix
// (f = float bits, i = signed, o = plus the module base), {name} variables, the calculator's own
// letter registers A-Z / a-z (double arrays, "dim(A[n])"), parentheses and the functions sin asin
// cos acos tan atan atan2 sqrt int rnd rand pow fabs abs log ceil floor shl shr sar RGB min max
// peekb peekw peek dim, -( !( ~(. Assignments (=, +=, -=, *=, /=, %=, &=, |=, ^=) store into the
// variable just read.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // The calculator's letter registers: double arrays, one element until dim() makes more
    private readonly Dictionary<char, double[]> m_registers = new();

    // Microsoft C runtime rand(), as the engine uses it (op_03AC, op_03AE, rnd, rand)
    private uint m_randSeed = 1;

    public int Rand()
    {
        m_randSeed = m_randSeed * 214013 + 2531011;
        return (int)((m_randSeed >> 16) & 0x7FFF);
    }

    public void SeedRand(int seed) => m_randSeed = (uint)seed;

    /// <summary>The value of an expression operand; with <paramref name="integer"/> (the "." form) as an int.</summary>
    public int Calculate(ScnContext c, string text, bool integer = true) => ToInt(new Calculator(this, c, text).Run());

    public double CalculateDouble(ScnContext c, string text) => new Calculator(this, c, text).Run();

    /// <summary>The C conversion of a double to int (__ftol), kept to 32 bits.</summary>
    public static int ToInt(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return (int)(long)Math.Truncate(Math.Clamp(value, long.MinValue, long.MaxValue));
    }

    private double[] Register(char letter)
    {
        if (!m_registers.TryGetValue(letter, out var array))
            m_registers[letter] = array = new double[1];
        return array;
    }

    private sealed class Calculator(ScnVm vm, ScnContext context, string text)
    {
        private int m_at;

        // The variable read last, as the target of an assignment
        private enum TargetKind { None, Memory, Register }
        private TargetKind m_target;
        private int m_address;          // Memory: its address
        private char m_suffix;          // Memory: '\0' unsigned, 'i' int, 'f' float
        private char m_letter;          // Register
        private int m_index;

        public double Run()
        {
            double value;
            while (true)
            {
                value = Sum();
                Skip();
                if (Peek() != ',')
                    return value;
                m_at++;
            }
        }

        private char Peek() => m_at < text.Length ? text[m_at] : '\0';

        private char PeekAt(int offset) => m_at + offset < text.Length ? text[m_at + offset] : '\0';

        private void Skip()
        {
            while (m_at < text.Length && text[m_at] is ' ' or '\t')
                m_at++;
        }

        private void Expect(char c)
        {
            Skip();
            if (Peek() != c)
                throw new InvalidDataException($"Calc: '{c}' expected at {m_at} in \"{text}\"");
            m_at++;
        }

        /// <summary>FUN_00403120: terms joined by + and - (and +=, -=).</summary>
        private double Sum()
        {
            double v = Term();
            while (true)
            {
                Skip();
                char op = Peek();
                if (op is not ('+' or '-'))
                    return v;
                m_at++;
                if (Peek() == '=')
                {
                    m_at++;
                    var target = SaveTarget();
                    double r = Term();
                    v = Assign(target, op == '+' ? v + r : v - r);
                }
                else
                {
                    double r = Term();
                    v = op == '+' ? v + r : v - r;
                }
            }
        }

        /// <summary>FUN_00402620: primaries joined left to right by every other operator.</summary>
        private double Term()
        {
            double v = Primary();
            while (true)
            {
                Skip();
                char op = Peek(), next = PeekAt(1);
                switch (op)
                {
                    case '!' when next == '=':
                        m_at += 2;
                        v = v != Primary() ? 1 : 0;
                        break;
                    case '=' when next == '=':
                        m_at += 2;
                        v = v == Primary() ? 1 : 0;
                        break;
                    case '=':
                    {
                        m_at++;
                        var target = SaveTarget();
                        v = Assign(target, Sum());
                        break;
                    }
                    case '<' when next == '<':
                        m_at += 2;
                        { int left = ToInt(v), right = ToInt(Primary()); v = left << (right & 31); }
                        break;
                    case '<' when next == '=':
                        m_at += 2;
                        v = v <= Primary() ? 1 : 0;
                        break;
                    case '<':
                        m_at++;
                        v = v < Primary() ? 1 : 0;
                        break;
                    case '>' when next == '>':
                        m_at += 2;
                        { int left = ToInt(v), right = ToInt(Primary()); v = left >> (right & 31); }
                        break;
                    case '>' when next == '=':
                        m_at += 2;
                        v = v >= Primary() ? 1 : 0;
                        break;
                    case '>':
                        m_at++;
                        v = v > Primary() ? 1 : 0;
                        break;
                    case '&' when next == '&':
                        m_at += 2;
                        { double r = Primary(); v = v != 0 && r != 0 ? 1 : 0; }
                        break;
                    case '|' when next == '|':
                        m_at += 2;
                        { double r = Primary(); v = v != 0 || r != 0 ? 1 : 0; }
                        break;
                    case '*' or '/' or '%' or '&' or '|' or '^':
                    {
                        m_at++;
                        bool assign = Peek() == '=';
                        if (assign)
                            m_at++;
                        var target = assign ? SaveTarget() : default;
                        double r = Primary();
                        double result = op switch
                        {
                            '*' => v * r,
                            '/' => r == 0 ? 0 : v / r,
                            '%' => ToInt(r) == 0 ? 0 : ToInt(v) % ToInt(r),
                            '&' => ToInt(v) & ToInt(r),
                            '|' => ToInt(v) | ToInt(r),
                            _ => ToInt(v) ^ ToInt(r),
                        };
                        v = assign ? Assign(target, result) : result;
                        break;
                    }
                    case '?':
                    {
                        m_at++;
                        double yes = Primary();
                        Expect(':');
                        double no = Primary();
                        v = v != 0 ? yes : no;
                        break;
                    }
                    default:
                        return v;
                }
            }
        }

        private (TargetKind Kind, int Address, char Suffix, char Letter, int Index) SaveTarget() =>
            (m_target, m_address, m_suffix, m_letter, m_index);

        private double Assign((TargetKind Kind, int Address, char Suffix, char Letter, int Index) target, double value)
        {
            switch (target.Kind)
            {
                case TargetKind.Memory:
                    if (target.Suffix == 'f')
                    {
                        vm.Write32(target.Address, BitConverter.SingleToInt32Bits((float)value));
                        return value;
                    }
                    int n = ToInt(value);
                    vm.Write32(target.Address, n);
                    return target.Suffix == 'i' ? n : (uint)n;
                case TargetKind.Register:
                {
                    var array = vm.Register(target.Letter);
                    if (target.Index >= 0 && target.Index < array.Length)
                        array[target.Index] = value;
                    return value;
                }
                default:
                    throw new InvalidDataException($"Calc: nothing to assign to in \"{text}\"");
            }
        }

        /// <summary>FUN_00401C10: a function call, a parenthesis, or a variable / number.</summary>
        private double Primary()
        {
            Skip();
            char c = Peek();
            if (c == '~')
            {
                m_at++;
                return ~ToInt(Primary());
            }
            if (c == '(')
            {
                m_at++;
                double v = Sum();
                Expect(')');
                return Suffixed(v);
            }
            if (Function() is { } call)
                return call;
            return Variable();
        }

        private double Suffixed(double v)
        {
            if (Peek() == 'i')
            {
                m_at++;
                return ToInt(v);
            }
            if (Peek() == 'f')
                m_at++;
            return v;
        }

        private static readonly string[] s_functions =
        {
            "atan2(", "asin(", "acos(", "atan(", "sqrt(", "rand(", "fabs(", "ceil(", "floor(", "peekb(", "peekw(", "peek(",
            "sin(", "cos(", "tan(", "int(", "rnd(", "pow(", "abs(", "log(", "shl(", "shr(", "sar(", "RGB(", "min(", "max(",
            "dim(", "-(", "!(", "~(",
        };

        private double? Function()
        {
            string? name = null;
            foreach (var f in s_functions)
            {
                if (string.CompareOrdinal(text, m_at, f, 0, f.Length) == 0)
                {
                    name = f[..^1];
                    break;
                }
            }
            if (name == null)
                return null;
            m_at += name.Length + 1;

            double Arg() => Sum();
            void Comma() => Expect(',');
            double result;
            switch (name)
            {
                case "sin": result = Math.Sin(Arg()); break;
                case "asin": result = Math.Asin(Arg()); break;
                case "cos": result = Math.Cos(Arg()); break;
                case "acos": result = Math.Acos(Arg()); break;
                case "tan": result = Math.Tan(Arg()); break;
                case "atan": result = Math.Atan(Arg()); break;
                case "atan2": { double y = Arg(); Comma(); result = Math.Atan2(y, Arg()); break; }
                case "sqrt": result = Math.Sqrt(Arg()); break;
                case "int": result = ToInt(Arg()); break;
                case "rnd": { int n = ToInt(Arg()); int r = vm.Rand(); result = n == 0 ? 0 : r % n; break; }
                case "rand": result = vm.Rand(); break;
                case "pow": { double x = Arg(); Comma(); result = Math.Pow(x, Arg()); break; }
                case "fabs": result = Math.Abs(Arg()); break;
                case "abs": result = Math.Abs(ToInt(Arg())); break;
                case "log": result = Math.Log(Arg()); break;
                case "ceil": result = Math.Ceiling(Arg()); break;
                case "floor": result = Math.Floor(Arg()); break;
                case "shl": { int x = ToInt(Arg()); Comma(); result = (uint)(x << (ToInt(Arg()) & 31)); break; }
                case "shr": { int x = ToInt(Arg()); Comma(); result = (uint)x >> (ToInt(Arg()) & 31); break; }
                case "sar": { int x = ToInt(Arg()); Comma(); result = x >> (ToInt(Arg()) & 31); break; }
                case "RGB":
                {
                    int r = ToInt(Arg()); Comma();
                    int g = ToInt(Arg()); Comma();
                    int b = ToInt(Arg());
                    result = (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
                    break;
                }
                case "min": { double x = Arg(); Comma(); double y = Arg(); result = Math.Min(x, y); break; }
                case "max": { double x = Arg(); Comma(); double y = Arg(); result = Math.Max(x, y); break; }
                case "peekb": result = vm.ReadByte(ToInt(Arg())); break;
                case "peekw": result = vm.Read16(ToInt(Arg())); break;
                case "peek": result = (uint)vm.Read32(ToInt(Arg())); break;
                case "-": result = -Arg(); break;
                case "!": result = Arg() == 0 ? 1 : 0; break;
                case "~": result = ~ToInt(Arg()); break;
                case "dim":
                {
                    // dim(A[n], B[m], ...): new arrays for letter registers
                    char letter;
                    while (true)
                    {
                        Skip();
                        letter = Peek();
                        m_at++;
                        Expect('[');
                        int size = Math.Max(1, ToInt(Sum()));
                        Expect(']');
                        vm.m_registers[letter] = new double[size];
                        Skip();
                        if (Peek() != ',')
                            break;
                        m_at++;
                    }
                    result = 0;
                    break;
                }
                default:
                    throw new InvalidDataException($"Calc: {name}() is not supported");
            }
            Expect(')');
            return Suffixed(result);
        }

        /// <summary>FUN_004016A0: a number or a variable, with ! ~ + - in front.</summary>
        private double Variable()
        {
            Skip();
            bool not = false, invert = false, negate = false;
            if (Peek() == '!') { not = true; m_at++; Skip(); }
            if (Peek() == '~') { invert = true; m_at++; Skip(); }
            if (Peek() is '+' or '-') { negate = Peek() == '-'; m_at++; Skip(); }

            double v;
            char c = Peek();
            m_target = TargetKind.None;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            {
                // A letter register, optionally with an index
                m_at++;
                int index = 0;
                if (Peek() == '[')
                {
                    m_at++;
                    index = ToInt(Sum());
                    Expect(']');
                }
                var array = vm.Register(c);
                v = index >= 0 && index < array.Length ? array[index] : 0;
                m_target = TargetKind.Register;
                m_letter = c;
                m_index = index;
            }
            else if (c == '_' || c == '{')
            {
                int address;
                if (c == '_')
                {
                    char space = char.ToUpperInvariant(PeekAt(1));
                    if (m_at + 5 > text.Length || !int.TryParse(text.AsSpan(m_at + 2, 3), out int n))
                        throw new InvalidDataException($"Calc: bad variable at {m_at} in \"{text}\"");
                    m_at += 5;
                    address = space switch
                    {
                        'D' => vm.AAddress(n),
                        'L' => vm.FAddress(context.Slot, n),
                        'M' => vm.GAddress(n),
                        'S' => vm.BAddress(n),
                        'Z' => vm.StackAddress(context.Slot, context.Sp + n),
                        _ => throw new InvalidDataException($"Calc: variable _{space} in \"{text}\""),
                    };
                }
                else
                {
                    int end = text.IndexOf('}', m_at);
                    if (end < 0)
                        throw new InvalidDataException($"Calc: '}}' missing in \"{text}\"");
                    address = vm.NamedAddress(context, text[(m_at + 1)..end]);
                    m_at = end + 1;
                }
                m_target = TargetKind.Memory;
                m_address = address;
                int raw = vm.Read32(address);
                char suffix = Peek();
                if (suffix == 'f')
                {
                    m_at++;
                    m_suffix = 'f';
                    v = BitConverter.Int32BitsToSingle(raw);
                }
                else if (suffix == 'i')
                {
                    m_at++;
                    m_suffix = 'i';
                    v = raw;
                }
                else
                {
                    m_suffix = '\0';
                    if (suffix == 'o')
                    {
                        m_at++;
                        raw += vm.m_slots[context.Slot].CodeBase;
                    }
                    v = (uint)raw;
                }
            }
            else if (c is >= '0' and <= '9')
            {
                v = Number();
            }
            else
            {
                throw new InvalidDataException($"Calc: unexpected '{c}' at {m_at} in \"{text}\"");
            }

            if (negate)
                v = -v;
            if (invert)
                v = ~ToInt(v);
            if (not)
                v = v == 0 ? 1 : 0;
            return v;
        }

        private double Number()
        {
            double v = 0;
            if (Peek() == '0' && PeekAt(1) == 'x')
            {
                m_at += 2;
                while (true)
                {
                    char h = Peek();
                    int d = h is >= '0' and <= '9' ? h - '0' : h is >= 'a' and <= 'f' ? h - 'a' + 10 : h is >= 'A' and <= 'F' ? h - 'A' + 10 : -1;
                    if (d < 0)
                        return v;
                    v = v * 16 + d;
                    m_at++;
                }
            }
            while (Peek() is >= '0' and <= '9')
                v = v * 10 + (text[m_at++] - '0');
            if (Peek() == '.')
            {
                m_at++;
                double scale = 0.1;
                while (Peek() is >= '0' and <= '9')
                {
                    v += (text[m_at++] - '0') * scale;
                    scale *= 0.1;
                }
            }
            return v;
        }
    }
}
