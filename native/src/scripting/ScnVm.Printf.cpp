// C# Scripting/ScnVm.Printf.cs: op_02DA dest, format, list: wvsprintfA. op_02DB dest, format,
// args...: the engine calls user32's wsprintfA with the operands as they are (FUN_0041DA80, its
// "call a DLL function" path). wsprintfA is reproduced here: integers and strings only (no floating
// point), flags - 0 #, width, precision, h / l, at most 1024 bytes.
#include "scripting/ScnVm.h"

#include <cstdio>
#include <stdexcept>

namespace openshiina::scripting {

namespace {

std::string PadLeft(std::string text, int width, char pad)
{
    if (int(text.size()) < width)
        text.insert(0, size_t(width) - text.size(), pad);
    return text;
}

} // namespace

int ScnVm::Wsprintf(int dest, int format, const std::vector<int>& args)
{
    size_t next = 0;
    return Wvsprintf(dest, format, [&] { return next < args.size() ? args[next++] : 0; });
}

int ScnVm::Wvsprintf(int dest, int format, const std::function<int()>& arg)
{
    std::string output;
    for (int p = format; output.size() < 1024;)
    {
        uint8_t b = ReadByte(p++);
        if (b == 0)
            break;
        if (b != '%')
        {
            output += char(b);
            continue;
        }
        bool left = false, zero = false, alternate = false;
        for (;; p++)
        {
            uint8_t f = ReadByte(p);
            if (f == '-')
                left = true;
            else if (f == '0')
                zero = true;
            else if (f == '#')
                alternate = true;
            else
                break;
        }
        int width = 0;
        while (ReadByte(p) >= '0' && ReadByte(p) <= '9')
            width = width * 10 + ReadByte(p++) - '0';
        int precision = -1;
        if (ReadByte(p) == '.')
        {
            p++;
            precision = 0;
            while (ReadByte(p) >= '0' && ReadByte(p) <= '9')
                precision = precision * 10 + ReadByte(p++) - '0';
        }
        bool wide = false, small = false;
        if (ReadByte(p) == 'l' || ReadByte(p) == 'L')
        {
            wide = true;
            p++;
        }
        else if (ReadByte(p) == 'h' || ReadByte(p) == 'H')
        {
            small = true;
            p++;
        }
        uint8_t type = ReadByte(p++);
        std::string body, prefix;
        switch (type)
        {
        case 'd':
        case 'i':
        {
            int v = arg();
            if (small)
                v = int16_t(v);
            body = std::to_string(v < 0 ? -int64_t(v) : int64_t(v));
            if (precision >= 0)
                body = PadLeft(body, precision, '0');
            prefix = v < 0 ? "-" : "";
            break;
        }
        case 'u':
        {
            uint32_t v = uint32_t(arg());
            if (small)
                v = uint16_t(v);
            body = std::to_string(v);
            if (precision >= 0)
                body = PadLeft(body, precision, '0');
            break;
        }
        case 'x':
        case 'X':
        {
            uint32_t v = uint32_t(arg());
            if (small)
                v = uint16_t(v);
            char digits[16];
            std::snprintf(digits, sizeof digits, type == 'x' ? "%x" : "%X", v);
            body = digits;
            if (precision >= 0)
                body = PadLeft(body, precision, '0');
            if (alternate && v != 0)
                prefix = type == 'x' ? "0x" : "0X";
            break;
        }
        case 'c':
        case 'C':
            body = std::string(1, char(uint8_t(arg())));
            break;
        case 's':
        case 'S':
        {
            int s = arg();
            if (wide)
                throw std::runtime_error("wsprintfA %ls is not supported");
            for (int q = s; s != 0 && (precision < 0 || int(body.size()) < precision); q++)
            {
                uint8_t c = ReadByte(q);
                if (c == 0)
                    break;
                body += char(c);
            }
            if (s == 0)
                body += "(null)";
            break;
        }
        case 0:
            p--;
            break;
        default:
            // an unknown type is copied as it is
            body = std::string(1, char(type));
            break;
        }
        int pad = width - int(body.size()) - int(prefix.size());
        if (!left && !zero)
            for (int k = 0; k < pad; k++)
                output += ' ';
        output += prefix;
        if (!left && zero)
            for (int k = 0; k < pad; k++)
                output += '0';
        output += body;
        if (left)
            for (int k = 0; k < pad; k++)
                output += ' ';
    }
    if (output.size() > 1024)
        output.resize(1024);
    WriteString(dest, output);
    return int(output.size());
}

void ScnVm::RegisterPrintf()
{
    Register(0x02DB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        if (i.Args.size() < 2)
            return 2;
        std::vector<int> values(i.Args.size());
        for (size_t k = 0; k < values.size(); k++)
            values[k] = vm.Value(c, i.Args[k]);
        vm.Wsprintf(values[0], values[1], std::vector<int>(values.begin() + 2, values.end()));
        return 0;
    });
    // 02DA dest, format, list: wvsprintfA with the arguments as dwords at list
    Register(0x02DA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int dest = vm.Value(c, i.Args[0]), format = vm.Value(c, i.Args[1]), list = vm.Value(c, i.Args[2]);
        vm.Wvsprintf(dest, format, [&] {
            int v = vm.Read32(list);
            list += 4;
            return v;
        });
        return 0;
    });
}

} // namespace openshiina::scripting
