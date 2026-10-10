// Operand layouts of the SCN opcodes, from the opcode tables read out of the engine executables
// (tools/ScnTools opscan; Data/ScnOps). Opcode numbers are the same in every engine version; later
// versions only add opcodes (docs/engine-notes.md, section 10).

using System.Reflection;

namespace OpenShiina.Scripting;

public sealed class ScnOpcodes
{
    /// <summary>Operand items: "V" typed operand, "b1" / "b2" / "b4" raw bytes, and the special layouts below.</summary>
    private readonly Dictionary<int, string[]> m_layouts = new();

    // Variable-length instructions. N2V = u16 count, then that many operands; SW = switch table;
    // CASE = case entry; VARGS = operands up to an FF byte; M1V = operands up to a constant -1.
    private static readonly Dictionary<int, string[]> s_overrides = new()
    {
        [0x03CF] = ["N2V"],             // local declarations
        [0x03CE] = ["N2V"],             // global declarations
        [0x0283] = ["V", "V", "N2V"],   // callmod result, slot, arguments
        [0x0281] = ["V", "V", "N2V"],   // call a label with arguments
        [0x0280] = ["N2V"],             // a called slot takes its arguments
        [0x0259] = ["SW"],
        [0x0209] = ["CASE"],
        [0x02DB] = ["VARGS"],           // printf-like message
        [0x01A4] = ["V", "V", "V", "VARGS", "V"],   // DLL call: -, dll, function, arguments, result
        [0x05D2] = ["M1V"],             // movies to play, up to -1
        [0x0001] = ["V", "V"],          // loadmod slot, file
        [0x0002] = ["V", "V"],
        [0x0213] = ["b4", "b4"],        // loop: counter, target
        [0x039C] = ["V", "V"],          // rotate right n, v: the handler re-reads v to store it,
        [0x039D] = ["V", "V"],          // which the table took for a third operand
    };

    // Layouts of one engine version only. SPRITE234 = 04BD of v2.34: seven operands, then one more
    // (an alpha ORed into the flags) when the third has bits 0x60000000, three more (the tint's
    // red, green, blue) when it has 0x20000000
    private static readonly Dictionary<string, Dictionary<int, string[]>> s_versionOverrides = new()
    {
        ["ops_v234"] = new() { [0x04BD] = ["SPRITE234"] },
    };

    private ScnOpcodes(string table, string name)
    {
        foreach (var line in table.Split('\n').Skip(1))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 3 || f[0].Length != 4)
                continue;
            var sig = f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            // Runs of "b8" come from a file loader that walks its own structure, not from the script
            if (sig.Count(x => x == "b8") >= 3)
                sig.RemoveAll(x => x is "b8" or "b");
            m_layouts[Convert.ToInt32(f[0], 16)] = sig.ToArray();
        }
        foreach (var (op, layout) in s_overrides)
            m_layouts[op] = layout;
        foreach (var (op, layout) in s_versionOverrides.GetValueOrDefault(name) ?? [])
            m_layouts[op] = layout;
    }

    public bool TryGetLayout(int op, out string[] layout) => m_layouts.TryGetValue(op, out layout!);

    private static readonly Dictionary<string, ScnOpcodes> s_loaded = new();

    /// <summary>
    /// The table for an engine version as RIO.INI names it ("2.47"): v2.47 has its own table (a
    /// subset of v2.49's), v2.49 its own, and v2.50 its own (read from Re:Rem Plus's executable:
    /// the numbers below 0C30 are v2.49's but for 0C30's operands and 11 new ones, and the block
    /// v2.49 numbers from 0C31 comes from 1069 on). v2.34 has its own (read from Ao no Juuai's
    /// aoj.EXE: 543 opcodes, 534 of them numbered as in v2.47 and 523 of those with v2.47's
    /// operands; its music has one stream, so 0686-068B take no stream).
    /// </summary>
    public static ScnOpcodes ForVersion(string version)
    {
        string name = version.StartsWith("2.47") || version.StartsWith("2.36") ? "ops_v247"
            : version.StartsWith("2.34") ? "ops_v234"
            : version.StartsWith("2.50") ? "ops_v250" : "ops_v249";
        lock (s_loaded)
        {
            if (!s_loaded.TryGetValue(name, out var table))
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OpenShiina.ScnOps.{name}.tsv")
                    ?? throw new InvalidOperationException($"The opcode table {name} is missing.");
                using var reader = new StreamReader(stream);
                s_loaded[name] = table = new ScnOpcodes(reader.ReadToEnd(), name);
            }
            return table;
        }
    }
}
