// Where the time of a running game goes, for the players' title and perf.log (OPENSHIINA_PERF):
// unset or anything but 0, frames a second and the slowest frame for the title; "log" also
// writes, every second, the slowest engine and picture times, the most main-loop rounds in a
// frame and the heap in use, and the opcodes, C# routines and embedded x86 routines (translated:
// "jit <offset>", interpreted: "x86 <offset>") that took the most time, to perf.log in the save
// folder. Timing every opcode makes the interpreter about half as fast, so only "log" does it.
// Called on the thread that runs the interpreter.

using System.Diagnostics;
using System.IO;

namespace OpenShiina.Game;

public sealed class PerfMeter : IDisposable
{
    private readonly ScnVm m_vm;
    private readonly string m_name;
    private readonly StreamWriter? m_log;
    private readonly Stopwatch m_clock = Stopwatch.StartNew(), m_run = Stopwatch.StartNew();
    private double m_worst, m_worstEngine, m_worstPicture;
    private int m_frames, m_rounds;

    private PerfMeter(ScnVm vm, string name, StreamWriter? log)
    {
        m_vm = vm;
        m_name = name;
        m_log = log;
        if (log != null)
            vm.OpTimes = new();
    }

    /// <summary>A meter as OPENSHIINA_PERF asks for, or null when it is "0".</summary>
    public static PerfMeter? Create(ScnVm vm, string gameName, string saveFolder)
    {
        string? setting = Environment.GetEnvironmentVariable("OPENSHIINA_PERF");
        if (setting == "0")
            return null;
        var log = setting == "log" ? new StreamWriter(Path.Combine(saveFolder, "perf.log"), append: false) : null;
        return new PerfMeter(vm, gameName, log);
    }

    /// <summary>
    /// A frame: it began at <paramref name="started"/>, the engine was done at
    /// <paramref name="engine"/> and the picture at <paramref name="done"/> (Stopwatch
    /// timestamps). Every second, the title to show; else null.
    /// </summary>
    public string? Frame(long started, long engine, long done)
    {
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        m_worst = Math.Max(m_worst, Ms(done - started));
        m_worstEngine = Math.Max(m_worstEngine, Ms(engine - started));
        m_worstPicture = Math.Max(m_worstPicture, Ms(done - engine));
        m_rounds = Math.Max(m_rounds, m_vm.FrameRounds);
        m_frames++;
        if (m_clock.ElapsedMilliseconds < 1000)
            return null;
        double fps = m_frames * 1000.0 / m_clock.ElapsedMilliseconds;
        if (m_log != null && m_vm.OpTimes is { } ops)
        {
            long instructions = m_vm.OpCounts.Values.Sum();
            m_log.WriteLine($"{m_run.Elapsed.TotalSeconds:F0} s: {fps:F0} fps, slowest {m_worst:F1} ms " +
                $"(engine {m_worstEngine:F1}, picture {m_worstPicture:F1}), up to {m_rounds} rounds a frame, " +
                $"{instructions} instructions, engine total {Ms(ops.Values.Sum()):F0} ms, heap {m_vm.HeapInUse >> 20} MB");
            m_log.WriteLine("  ops: " + string.Join(", ", ops.OrderByDescending(t => t.Value).Take(8)
                .Select(t => $"{t.Key:X4} {Ms(t.Value):F1} ms x{m_vm.OpCounts.GetValueOrDefault(t.Key)}")));
            if (m_vm.NativeTimes.Count > 0)
                m_log.WriteLine("  routines: " + string.Join(", ", m_vm.NativeTimes.OrderByDescending(t => t.Value.Ticks).Take(6)
                    .Select(t => $"{t.Key} {Ms(t.Value.Ticks):F1} ms x{t.Value.Calls}")));
            m_log.Flush();
            ops.Clear();
            m_vm.OpCounts.Clear();
            m_vm.NativeTimes.Clear();
        }
        string title = $"{m_name} - {fps:F0} fps, slowest {m_worst:F1} ms";
        m_clock.Restart();
        m_worst = m_worstEngine = m_worstPicture = 0;
        m_frames = m_rounds = 0;
        return title;
    }

    public void Dispose() => m_log?.Dispose();
}
