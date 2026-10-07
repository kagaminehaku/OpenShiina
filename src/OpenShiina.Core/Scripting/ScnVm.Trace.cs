// A drawing trace for finding problems (OPENSHIINA_TRACE=draw): text started with 0083 / 0084
// (record, surface, where, the text), where 0083 text ended and the rectangle its characters
// covered, the positions set with 0078, sprite lists composed into surfaces (04C4 / 04C5), 04C6,
// 04E2 and 04F6, the rectangles invalidated, WM_PAINT, the window messages and the answers of the
// message slot (07E4), each with the frame, the task and its code offset. The latest lines are
// kept and written out when the game closes.

using System.Text;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>The drawing trace, or null when off.</summary>
    public DrawTrace? Trace { get; set; }

    private int m_frameNumber;

    // The rectangle a record's 0083 text drew into, while it goes on
    private readonly Dictionary<int, (int L, int T, int R, int B)> m_traceExtent = new();

    private void TraceLine(ScnContext c, string what) =>
        Trace?.Add($"f{m_frameNumber} r{m_rounds} slot {c.Slot} {c.Pc - c.Base:X5}: {what}");

    private string TraceText(int address)
    {
        if (address == 0)
            return "";
        var bytes = new List<byte>();
        for (int a = address; bytes.Count < 160; a++)
        {
            byte b = ReadByte(a);
            if (b == 0)
                break;
            bytes.Add(b);
        }
        return OpenShiina.IO.Encodings.cp932.GetString(bytes.ToArray()).Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private void TraceTextStart(ScnContext c, int op, int surface, int rec, int text)
    {
        if (Trace == null)
            return;
        m_traceExtent.Remove(rec);
        TraceLine(c, $"{op:X4} surface {surface} record {G(rec, RIndex)} at {G(rec, RX)},{G(rec, RY)} \"{TraceText(text)}\"");
    }

    private void TraceTextStep(ScnContext c, int rec, bool drew)
    {
        if (Trace == null)
            return;
        if (drew)
        {
            var r = (G(rec, RRect), G(rec, RRect + 1), G(rec, RRect + 2), G(rec, RRect + 3));
            m_traceExtent[rec] = m_traceExtent.TryGetValue(rec, out var e)
                ? (Math.Min(e.L, r.Item1), Math.Min(e.T, r.Item2), Math.Max(e.R, r.Item3), Math.Max(e.B, r.Item4))
                : r;
        }
        if (G(rec, RState) == 0)
        {
            string extent = m_traceExtent.TryGetValue(rec, out var e) ? $"{e.L},{e.T}-{e.R},{e.B}" : "nothing";
            m_traceExtent.Remove(rec);
            TraceLine(c, $"0083 done record {G(rec, RIndex)} at {G(rec, RX)},{G(rec, RY)}, drew {extent}");
        }
    }
}

/// <summary>The latest lines of a trace, kept in memory.</summary>
public sealed class DrawTrace(int capacity = 200_000)
{
    private readonly Queue<string> m_lines = new();

    public void Add(string line)
    {
        lock (m_lines)
        {
            if (m_lines.Count == capacity)
                m_lines.Dequeue();
            m_lines.Enqueue(line);
        }
    }

    public void Save(string path)
    {
        lock (m_lines)
            File.WriteAllLines(path, m_lines, new UTF8Encoding(false));
    }
}
