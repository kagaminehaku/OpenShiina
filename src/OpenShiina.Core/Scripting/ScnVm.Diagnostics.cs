// What to tell about a script error: which module the address is in, whether the code there is
// still the code that was loaded (scripts that stop on bytes no opcode starts with have usually
// run into code something overwrote), what overwrote it if a recent embedded routine call points
// near it, and the task's state.

using System.Text;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // Modules as they were loaded: address, file, bytes
    private readonly List<(int Base, string File, byte[] Original)> m_moduleImages = new();

    // The latest embedded routine calls: when, which, how it ran, its l[0..13]
    private readonly (long Round, int Slot, int Target, string How, int[] Args)[] m_recentCalls = new (long, int, int, string, int[])[64];
    private int m_recentCallCount;
    private long m_rounds;

    private void RememberModule(int at, string file, byte[] code) => m_moduleImages.Add((at, file, code));

    private void RememberCall(ScnContext c, int target, string how, ScnNativeArgs args)
    {
        var l = new int[14];
        for (int i = 0; i < l.Length; i++)
            l[i] = Read32(args.Stack + 4 * i);
        m_recentCalls[m_recentCallCount++ % m_recentCalls.Length] = (m_rounds, c.Slot, target, how, l);
    }

    /// <summary>Where an address is: "FILE.SCN+offset", or its hex value.</summary>
    public string DescribeAddress(int address)
    {
        for (int i = m_moduleImages.Count - 1; i >= 0; i--)
        {
            var (at, file, code) = m_moduleImages[i];
            if (address >= at && address < at + code.Length)
                return $"{file}+{address - at:X5}";
        }
        return $"{address:X8}";
    }

    /// <summary>A report on a script error for a log: the code at the address, overwritten or not, and recent routine calls.</summary>
    public string CrashReport(Exception error)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error.GetType().Name}: {error.Message}");
        if (error is ScnException se)
        {
            sb.AppendLine($"Address {se.Address:X8} = {DescribeAddress(se.Address)}, opcode {se.Op:X4}");
            if ((uint)se.Slot < Slots)
            {
                var c = m_slots[se.Slot];
                sb.AppendLine($"Slot {c.Slot}: module {DescribeAddress(c.CodeBase)}, base {DescribeAddress(c.Base)}, pc {DescribeAddress(c.Pc)}, " +
                              $"previous instruction {DescribeAddress(c.Current)}, sp {c.Sp}, frames {c.FrameTop}");
            }
            DescribeDamage(sb, se.Address);
        }
        sb.AppendLine("Latest embedded routine calls (oldest first):");
        int n = Math.Min(m_recentCallCount, m_recentCalls.Length);
        for (int k = m_recentCallCount - n; k < m_recentCallCount; k++)
        {
            var (round, slot, target, how, args) = m_recentCalls[k % m_recentCalls.Length];
            sb.AppendLine($"  round {round}, slot {slot}: {DescribeAddress(target)} ({how}) l = {string.Join(" ", args.Select(v => v.ToString("X")))}");
        }
        sb.AppendLine(error.StackTrace);
        return sb.ToString();
    }

    /// <summary>When the bytes at the address differ from the module as loaded: the changed run, and calls pointing near it.</summary>
    private void DescribeDamage(StringBuilder sb, int address)
    {
        foreach (var (at, file, code) in m_moduleImages)
        {
            if (address < at || address >= at + code.Length)
                continue;
            bool Changed(int a) => a >= at && a < at + code.Length && ReadByte(a) != code[a - at];
            if (!Changed(address) && !Changed(address + 1))
            {
                sb.AppendLine($"The code of {file} at the address is as it was loaded.");
                return;
            }
            // The changed run, allowing unchanged gaps of up to 16 bytes inside it
            int start = address, end = address + 2;
            for (int gap = 0; start > at && gap < 16; )
                gap = Changed(--start) ? 0 : gap + 1;
            while (!Changed(start))
                start++;
            for (int gap = 0; end < at + code.Length && gap < 16; end++)
                gap = Changed(end) ? 0 : gap + 1;
            while (end > start && !Changed(end - 1))
                end--;
            sb.AppendLine($"The code of {file} was overwritten: {file}+{start - at:X5} to +{end - at:X5} ({end - start} bytes).");
            int show = Math.Min(end - start, 256);
            byte[] now = ReadBytes(start, show);
            sb.AppendLine("  now:    " + Convert.ToHexString(now) + "  \"" + Printable(now) + "\"");
            sb.AppendLine("  loaded: " + Convert.ToHexString(code, start - at, show));
            int n = Math.Min(m_recentCallCount, m_recentCalls.Length);
            for (int k = m_recentCallCount - n; k < m_recentCallCount; k++)
            {
                var (round, slot, target, how, args) = m_recentCalls[k % m_recentCalls.Length];
                if (args.Any(v => v <= end && v > start - (8 << 20)))
                    sb.AppendLine($"  a call with a pointer before or into it: round {round}, {DescribeAddress(target)} ({how})");
            }
            return;
        }
        sb.AppendLine("The address is outside every loaded module.");
    }

    private static string Printable(byte[] bytes) =>
        new(bytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
}
