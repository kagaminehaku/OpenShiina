// op_02DA dest, format, list: wvsprintfA. op_02DB dest, format, args...: the engine calls user32's wsprintfA with the operands as they
// are (FUN_0041DA80, its "call a DLL function" path). wsprintfA is reproduced here: integers and
// strings only (no floating point), flags - 0 #, width, precision, h / l, at most 1024 bytes.

using System.Text;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>wsprintfA into VM memory; returns the length written (without the NUL).</summary>
    public int Wsprintf(int dest, int format, IReadOnlyList<int> args)
    {
        int next = 0;
        return Wvsprintf(dest, format, () => next < args.Count ? args[next++] : 0);
    }

    /// <summary>wvsprintfA: the arguments come one by one from <paramref name="arg"/>.</summary>
    public int Wvsprintf(int dest, int format, Func<int> arg)
    {
        var output = new List<byte>();
        int Arg() => arg();
        for (int p = format; output.Count < 1024;)
        {
            byte b = ReadByte(p++);
            if (b == 0)
                break;
            if (b != '%')
            {
                output.Add(b);
                continue;
            }
            bool left = false, zero = false, alternate = false;
            for (; ; p++)
            {
                byte f = ReadByte(p);
                if (f == '-') left = true;
                else if (f == '0') zero = true;
                else if (f == '#') alternate = true;
                else break;
            }
            int width = 0;
            while (ReadByte(p) is >= (byte)'0' and <= (byte)'9')
                width = width * 10 + ReadByte(p++) - '0';
            int precision = -1;
            if (ReadByte(p) == '.')
            {
                p++;
                precision = 0;
                while (ReadByte(p) is >= (byte)'0' and <= (byte)'9')
                    precision = precision * 10 + ReadByte(p++) - '0';
            }
            bool wide = false, small = false;
            if (ReadByte(p) is (byte)'l' or (byte)'L') { wide = true; p++; }
            else if (ReadByte(p) is (byte)'h' or (byte)'H') { small = true; p++; }
            byte type = ReadByte(p++);
            byte[] body;
            string prefix = "";
            switch (type)
            {
                case (byte)'d':
                case (byte)'i':
                {
                    int v = Arg();
                    if (small)
                        v = (short)v;
                    string digits = Math.Abs((long)v).ToString();
                    if (precision >= 0)
                        digits = digits.PadLeft(precision, '0');
                    prefix = v < 0 ? "-" : "";
                    body = Encoding.ASCII.GetBytes(digits);
                    break;
                }
                case (byte)'u':
                {
                    uint v = (uint)Arg();
                    if (small)
                        v = (ushort)v;
                    string digits = v.ToString();
                    if (precision >= 0)
                        digits = digits.PadLeft(precision, '0');
                    body = Encoding.ASCII.GetBytes(digits);
                    break;
                }
                case (byte)'x':
                case (byte)'X':
                {
                    uint v = (uint)Arg();
                    if (small)
                        v = (ushort)v;
                    string digits = v.ToString(type == 'x' ? "x" : "X");
                    if (precision >= 0)
                        digits = digits.PadLeft(precision, '0');
                    if (alternate && v != 0)
                        prefix = type == 'x' ? "0x" : "0X";
                    body = Encoding.ASCII.GetBytes(digits);
                    break;
                }
                case (byte)'c':
                case (byte)'C':
                    body = [(byte)Arg()];
                    break;
                case (byte)'s':
                case (byte)'S':
                {
                    int s = Arg();
                    if (wide)
                        throw new NotSupportedException("wsprintfA %ls is not supported");
                    var text = new List<byte>();
                    for (int q = s; s != 0 && (precision < 0 || text.Count < precision); q++)
                    {
                        byte c = ReadByte(q);
                        if (c == 0)
                            break;
                        text.Add(c);
                    }
                    if (s == 0)
                        text.AddRange("(null)"u8.ToArray());
                    body = text.ToArray();
                    break;
                }
                case 0:
                    p--;
                    body = [];
                    break;
                default:
                    // an unknown type is copied as it is
                    body = [type];
                    break;
            }
            int pad = width - body.Length - prefix.Length;
            if (!left && !zero)
                for (int k = 0; k < pad; k++) output.Add((byte)' ');
            output.AddRange(Encoding.ASCII.GetBytes(prefix));
            if (!left && zero)
                for (int k = 0; k < pad; k++) output.Add((byte)'0');
            output.AddRange(body);
            if (left)
                for (int k = 0; k < pad; k++) output.Add((byte)' ');
        }
        if (output.Count > 1024)
            output.RemoveRange(1024, output.Count - 1024);
        WriteBytes(dest, output.ToArray());
        WriteByte(dest + output.Count, 0);
        return output.Count;
    }

    private void RegisterPrintf()
    {
        Register(0x02DB, (vm, c, i) =>
        {
            if (i.Args.Length < 2)
                return 2;
            var values = new int[i.Args.Length];
            for (int k = 0; k < values.Length; k++)
                values[k] = vm.Value(c, i.Args[k]);
            vm.Wsprintf(values[0], values[1], values[2..]);
            return 0;
        });
        // 02DA dest, format, list: wvsprintfA with the arguments as dwords at list
        Register(0x02DA, (vm, c, i) =>
        {
            int dest = vm.Value(c, i.Args[0]), format = vm.Value(c, i.Args[1]), list = vm.Value(c, i.Args[2]);
            vm.Wvsprintf(dest, format, () => { int v = vm.Read32(list); list += 4; return v; });
            return 0;
        });
    }
}
