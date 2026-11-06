// C# Scripting/ScnVm.Calc.cs: expression operands ("expr(...)", eval) as the engine's calculator
// (rio/Calc.cpp) works them out: every value is a double; '+' and '-' bind loosest, every other
// binary operator has the same precedence and runs left to right; ',' separates expressions (the
// last one gives the result).
//
// Terms: numbers (decimal, 0x hex, with a fraction), _Xnnn variables (exactly three digits: D = a[],
// L = f[] of the slot, M = g[], S = b[], Z = l[] on the stack) with an optional suffix (f = float
// bits, i = signed, o = plus the module base), {name} variables, the calculator's own letter
// registers A-Z / a-z (double arrays, "dim(A[n])"), parentheses and the functions sin asin cos acos
// tan atan atan2 sqrt int rnd rand pow fabs abs log ceil floor shl shr sar RGB min max peekb peekw
// peek dim, -( !( ~(. Assignments (=, +=, -=, *=, /=, %=, &=, |=, ^=) store into the variable just
// read.
//
// The text is Shift-JIS bytes where the C# player has it decoded: only ASCII is looked at, but for
// a {name}, which goes to NamedAddress as it is.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <bit>
#include <cmath>
#include <format>
#include <stdexcept>

#include "io/Binary.h"

namespace openshiina::scripting {

namespace {

float AsFloat(int bits) { return std::bit_cast<float>(bits); }
int FloatBits(float value) { return std::bit_cast<int>(value); }

std::runtime_error Bad(const std::string& message) { return std::runtime_error("Calc: " + message); }

int CMod(int a, int b)
{
    if (a == int(0x80000000) && b == -1)
        throw std::overflow_error("Arithmetic operation resulted in an overflow.");
    return a % b;
}

} // namespace

int ScnVm::Rand()
{
    m_randSeed = m_randSeed * 214013u + 2531011u;
    return int((m_randSeed >> 16) & 0x7FFF);
}

int ScnVm::ToInt(double value)
{
    if (std::isnan(value) || std::isinf(value))
        return 0;
    // (int)(long)Math.Truncate(Math.Clamp(value, long.MinValue, long.MaxValue)): the clamp and the
    // conversion to long saturate as .NET 9+ does them
    return int(SaturateToInt64(std::trunc(value)));
}

std::vector<double>& ScnVm::RegisterArray(char letter)
{
    auto& array = m_registers[letter];
    if (array.empty())
        array.assign(1, 0.0);
    return array;
}

class ScnVm::Calculator
{
public:
    Calculator(ScnVm& vm, ScnContext& context, const std::string& text) : m_vm(vm), m_context(context), m_text(text) {}

    double Run()
    {
        while (true)
        {
            double value = Sum();
            Skip();
            if (Peek() != ',')
                return value;
            m_at++;
        }
    }

private:
    enum class TargetKind
    {
        None,
        Memory,
        Register,
    };

    struct Target
    {
        TargetKind Kind = TargetKind::None;
        int Address = 0;
        char Suffix = 0;
        char Letter = 0;
        int Index = 0;
    };

    char Peek() const { return m_at < m_text.size() ? m_text[m_at] : '\0'; }
    char PeekAt(size_t offset) const { return m_at + offset < m_text.size() ? m_text[m_at + offset] : '\0'; }

    void Skip()
    {
        while (m_at < m_text.size() && (m_text[m_at] == ' ' || m_text[m_at] == '\t'))
            m_at++;
    }

    void Expect(char c)
    {
        Skip();
        if (Peek() != c)
            throw Bad(std::format("'{}' expected at {} in \"{}\"", c, m_at, m_text));
        m_at++;
    }

    /// FUN_00403120: terms joined by + and - (and +=, -=).
    double Sum()
    {
        double v = Term();
        while (true)
        {
            Skip();
            char op = Peek();
            if (op != '+' && op != '-')
                return v;
            m_at++;
            if (Peek() == '=')
            {
                m_at++;
                Target target = m_target;
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

    /// FUN_00402620: primaries joined left to right by every other operator.
    double Term()
    {
        double v = Primary();
        while (true)
        {
            Skip();
            char op = Peek(), next = PeekAt(1);
            if (op == '!' && next == '=')
            {
                m_at += 2;
                v = v != Primary() ? 1 : 0;
            }
            else if (op == '=' && next == '=')
            {
                m_at += 2;
                v = v == Primary() ? 1 : 0;
            }
            else if (op == '=')
            {
                m_at++;
                Target target = m_target;
                v = Assign(target, Sum());
            }
            else if (op == '<' && next == '<')
            {
                m_at += 2;
                int left = ToInt(v), right = ToInt(Primary());
                v = int(uint32_t(left) << (right & 31));
            }
            else if (op == '<' && next == '=')
            {
                m_at += 2;
                v = v <= Primary() ? 1 : 0;
            }
            else if (op == '<')
            {
                m_at++;
                v = v < Primary() ? 1 : 0;
            }
            else if (op == '>' && next == '>')
            {
                m_at += 2;
                int left = ToInt(v), right = ToInt(Primary());
                v = left >> (right & 31);
            }
            else if (op == '>' && next == '=')
            {
                m_at += 2;
                v = v >= Primary() ? 1 : 0;
            }
            else if (op == '>')
            {
                m_at++;
                v = v > Primary() ? 1 : 0;
            }
            else if (op == '&' && next == '&')
            {
                m_at += 2;
                double r = Primary();
                v = v != 0 && r != 0 ? 1 : 0;
            }
            else if (op == '|' && next == '|')
            {
                m_at += 2;
                double r = Primary();
                v = v != 0 || r != 0 ? 1 : 0;
            }
            else if (op == '*' || op == '/' || op == '%' || op == '&' || op == '|' || op == '^')
            {
                m_at++;
                bool assign = Peek() == '=';
                if (assign)
                    m_at++;
                Target target = assign ? m_target : Target{};
                double r = Primary();
                double result;
                switch (op)
                {
                case '*': result = v * r; break;
                case '/': result = r == 0 ? 0 : v / r; break;
                case '%': result = ToInt(r) == 0 ? 0 : CMod(ToInt(v), ToInt(r)); break;
                case '&': result = ToInt(v) & ToInt(r); break;
                case '|': result = ToInt(v) | ToInt(r); break;
                default: result = ToInt(v) ^ ToInt(r); break;
                }
                v = assign ? Assign(target, result) : result;
            }
            else if (op == '?')
            {
                m_at++;
                double yes = Primary();
                Expect(':');
                double no = Primary();
                v = v != 0 ? yes : no;
            }
            else
                return v;
        }
    }

    double Assign(const Target& target, double value)
    {
        switch (target.Kind)
        {
        case TargetKind::Memory:
        {
            if (target.Suffix == 'f')
            {
                m_vm.Write32(target.Address, FloatBits(float(value)));
                return value;
            }
            int n = ToInt(value);
            m_vm.Write32(target.Address, n);
            return target.Suffix == 'i' ? double(n) : double(uint32_t(n));
        }
        case TargetKind::Register:
        {
            auto& array = m_vm.RegisterArray(target.Letter);
            if (target.Index >= 0 && size_t(target.Index) < array.size())
                array[size_t(target.Index)] = value;
            return value;
        }
        default:
            throw Bad(std::format("nothing to assign to in \"{}\"", m_text));
        }
    }

    /// FUN_00401C10: a function call, a parenthesis, or a variable / number.
    double Primary()
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
        double call;
        if (Function(call))
            return call;
        return Variable();
    }

    double Suffixed(double v)
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

    bool Function(double& result)
    {
        static const char* const functions[] = {
            "atan2(", "asin(", "acos(", "atan(", "sqrt(", "rand(", "fabs(", "ceil(", "floor(", "peekb(", "peekw(", "peek(",
            "sin(", "cos(", "tan(", "int(", "rnd(", "pow(", "abs(", "log(", "shl(", "shr(", "sar(", "RGB(", "min(", "max(",
            "dim(", "-(", "!(", "~(",
        };
        std::string name;
        for (const char* f : functions)
        {
            size_t length = std::char_traits<char>::length(f);
            if (m_text.compare(m_at, length, f) == 0)
            {
                name.assign(f, length - 1);
                break;
            }
        }
        if (name.empty())
            return false;
        m_at += name.size() + 1;

        auto arg = [this] { return Sum(); };
        auto comma = [this] { Expect(','); };
        if (name == "sin") result = std::sin(arg());
        else if (name == "asin") result = std::asin(arg());
        else if (name == "cos") result = std::cos(arg());
        else if (name == "acos") result = std::acos(arg());
        else if (name == "tan") result = std::tan(arg());
        else if (name == "atan") result = std::atan(arg());
        else if (name == "atan2")
        {
            double y = arg();
            comma();
            result = std::atan2(y, arg());
        }
        else if (name == "sqrt") result = std::sqrt(arg());
        else if (name == "int") result = ToInt(arg());
        else if (name == "rnd")
        {
            int n = ToInt(arg());
            int r = m_vm.Rand();
            result = n == 0 ? 0 : CMod(r, n);
        }
        else if (name == "rand") result = m_vm.Rand();
        else if (name == "pow")
        {
            double x = arg();
            comma();
            result = std::pow(x, arg());
        }
        else if (name == "fabs") result = std::fabs(arg());
        else if (name == "abs")
        {
            int n = ToInt(arg());
            if (n == int(0x80000000))
                throw std::overflow_error("Negating the minimum value of a twos complement number is invalid.");
            result = n < 0 ? -n : n;
        }
        else if (name == "log") result = std::log(arg());
        else if (name == "ceil") result = std::ceil(arg());
        else if (name == "floor") result = std::floor(arg());
        else if (name == "shl")
        {
            int x = ToInt(arg());
            comma();
            result = uint32_t(x) << (ToInt(arg()) & 31);
        }
        else if (name == "shr")
        {
            int x = ToInt(arg());
            comma();
            result = uint32_t(x) >> (ToInt(arg()) & 31);
        }
        else if (name == "sar")
        {
            int x = ToInt(arg());
            comma();
            result = x >> (ToInt(arg()) & 31);
        }
        else if (name == "RGB")
        {
            int r = ToInt(arg());
            comma();
            int g = ToInt(arg());
            comma();
            int b = ToInt(arg());
            result = uint32_t((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
        }
        else if (name == "min")
        {
            double x = arg();
            comma();
            double y = arg();
            result = NetMin(x, y);
        }
        else if (name == "max")
        {
            double x = arg();
            comma();
            double y = arg();
            result = NetMax(x, y);
        }
        else if (name == "peekb") result = m_vm.ReadByte(ToInt(arg()));
        else if (name == "peekw") result = m_vm.Read16(ToInt(arg()));
        else if (name == "peek") result = uint32_t(m_vm.Read32(ToInt(arg())));
        else if (name == "-") result = -arg();
        else if (name == "!") result = arg() == 0 ? 1 : 0;
        else if (name == "~") result = ~ToInt(arg());
        else if (name == "dim")
        {
            // dim(A[n], B[m], ...): new arrays for letter registers
            while (true)
            {
                Skip();
                char letter = Peek();
                m_at++;
                Expect('[');
                int size = std::max(1, ToInt(Sum()));
                Expect(']');
                m_vm.m_registers[letter].assign(size_t(size), 0.0);
                Skip();
                if (Peek() != ',')
                    break;
                m_at++;
            }
            result = 0;
        }
        else
            throw Bad(name + "() is not supported");
        Expect(')');
        result = Suffixed(result);
        return true;
    }

    // Math.Min / Math.Max of doubles: NaN when either is, -0 below +0
    static double NetMin(double x, double y)
    {
        if (std::isnan(x) || std::isnan(y))
            return std::nan("");
        if (x == y)
            return std::signbit(x) ? x : y;
        return x < y ? x : y;
    }

    static double NetMax(double x, double y)
    {
        if (std::isnan(x) || std::isnan(y))
            return std::nan("");
        if (x == y)
            return std::signbit(x) ? y : x;
        return x > y ? x : y;
    }

    /// FUN_004016A0: a number or a variable, with ! ~ + - in front.
    double Variable()
    {
        Skip();
        bool notFlag = false, invert = false, negate = false;
        if (Peek() == '!')
        {
            notFlag = true;
            m_at++;
            Skip();
        }
        if (Peek() == '~')
        {
            invert = true;
            m_at++;
            Skip();
        }
        if (Peek() == '+' || Peek() == '-')
        {
            negate = Peek() == '-';
            m_at++;
            Skip();
        }

        double v;
        char c = Peek();
        m_target.Kind = TargetKind::None;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
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
            auto& array = m_vm.RegisterArray(c);
            v = index >= 0 && size_t(index) < array.size() ? array[size_t(index)] : 0;
            m_target.Kind = TargetKind::Register;
            m_target.Letter = c;
            m_target.Index = index;
        }
        else if (c == '_' || c == '{')
        {
            int address;
            if (c == '_')
            {
                char space = PeekAt(1);
                if (space >= 'a' && space <= 'z')
                    space = char(space - 32);
                int n;
                if (m_at + 5 > m_text.size() || !NetTryParseInt(std::string_view(m_text).substr(m_at + 2, 3), n))
                    throw Bad(std::format("bad variable at {} in \"{}\"", m_at, m_text));
                m_at += 5;
                switch (space)
                {
                case 'D': address = m_vm.AAddress(n); break;
                case 'L': address = m_vm.FAddress(m_context.Slot, n); break;
                case 'M': address = m_vm.GAddress(n); break;
                case 'S': address = m_vm.BAddress(n); break;
                case 'Z': address = m_vm.StackAddress(m_context.Slot, m_context.Sp + n); break;
                default: throw Bad(std::format("variable _{} in \"{}\"", space, m_text));
                }
            }
            else
            {
                size_t end = SjisFind(m_text, '}', m_at);
                if (end == std::string::npos)
                    throw Bad(std::format("'}}' missing in \"{}\"", m_text));
                address = m_vm.NamedAddress(m_context, m_text.substr(m_at + 1, end - m_at - 1));
                m_at = end + 1;
            }
            m_target.Kind = TargetKind::Memory;
            m_target.Address = address;
            int raw = m_vm.Read32(address);
            char suffix = Peek();
            if (suffix == 'f')
            {
                m_at++;
                m_target.Suffix = 'f';
                v = AsFloat(raw);
            }
            else if (suffix == 'i')
            {
                m_at++;
                m_target.Suffix = 'i';
                v = raw;
            }
            else
            {
                m_target.Suffix = 0;
                if (suffix == 'o')
                {
                    m_at++;
                    raw += m_vm.m_slots[size_t(m_context.Slot)].CodeBase;
                }
                v = uint32_t(raw);
            }
        }
        else if (c >= '0' && c <= '9')
            v = Number();
        else
            throw Bad(std::format("unexpected '{}' at {} in \"{}\"", c, m_at, m_text));

        if (negate)
            v = -v;
        if (invert)
            v = ~ToInt(v);
        if (notFlag)
            v = v == 0 ? 1 : 0;
        return v;
    }

    double Number()
    {
        double v = 0;
        if (Peek() == '0' && PeekAt(1) == 'x')
        {
            m_at += 2;
            while (true)
            {
                char h = Peek();
                int d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                if (d < 0)
                    return v;
                v = v * 16 + d;
                m_at++;
            }
        }
        while (Peek() >= '0' && Peek() <= '9')
            v = v * 10 + (m_text[m_at++] - '0');
        if (Peek() == '.')
        {
            m_at++;
            double scale = 0.1;
            while (Peek() >= '0' && Peek() <= '9')
            {
                v += (m_text[m_at++] - '0') * scale;
                scale *= 0.1;
            }
        }
        return v;
    }

    ScnVm& m_vm;
    ScnContext& m_context;
    const std::string& m_text;
    size_t m_at = 0;
    // The variable read last, as the target of an assignment
    Target m_target;
};

int ScnVm::Calculate(ScnContext& c, const std::string& text, bool)
{
    return ToInt(Calculator(*this, c, text).Run());
}

double ScnVm::CalculateDouble(ScnContext& c, const std::string& text)
{
    return Calculator(*this, c, text).Run();
}

} // namespace openshiina::scripting
