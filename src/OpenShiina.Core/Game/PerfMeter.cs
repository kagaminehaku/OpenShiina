// Where the time of a running game goes, for the players' title and perf.log (the settings
// "Show the frame rate" and "Write perf.log", or OPENSHIINA_PERF): frames a second and the
// slowest frame for the title; the log gets, every second, the slowest engine and picture times, the most main-loop rounds in a
// frame and the heap in use, and the opcodes, C# routines and embedded x86 routines (translated:
// "jit <offset>", interpreted: "x86 <offset>") that took the most time, the file reads that held
// the interpreter 30 ms or more and how many files were read ahead, to perf.log in the save
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
    private readonly bool m_title;
    private readonly Stopwatch m_clock = Stopwatch.StartNew(), m_run = Stopwatch.StartNew();
    private double m_worst, m_worstEngine, m_worstPicture;
    private int m_frames, m_rounds;
    private bool m_alphaLogged;
    private (int Read, int Used) m_readAhead;

    private PerfMeter(ScnVm vm, string name, StreamWriter? log, bool title)
    {
        m_title = title;
        m_vm = vm;
        m_name = name;
        m_log = log;
        if (log != null)
        {
            vm.OpTimes = new();
            log.WriteLine($"Drawing on {(vm.Accelerator is { } gpu ? $"the GPU: {gpu.Name}" : "the CPU")}");
            log.WriteLine(Machine() + $"; file cache {vm.FileCacheBytes >> 20} MB (memory {FileCachePresets.TotalMemory() >> 20} MB)");
        }
    }

    /// <summary>
    /// The runtime and what its vectors are on this processor: the pixel routines are written
    /// with Vector128 / Vector&lt;T&gt;, which only some runtimes make the processor's own (NEON, SSE).
    /// </summary>
    private static string Machine()
    {
        bool mono = Type.GetType("Mono.RuntimeStructs") != null;
        return $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}{(mono ? " (Mono)" : "")}, " +
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} processors " +
            $"(pieces of a call: up to {ParallelRows.MaxParts}); vectors: Vector128 {(System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated ? "on" : "off")}, " +
            $"Vector<T> {(System.Numerics.Vector.IsHardwareAccelerated ? "on" : "off")} ({System.Numerics.Vector<byte>.Count} bytes), " +
            $"NEON {(System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported ? "yes" : "no")}, SSSE3 {(System.Runtime.Intrinsics.X86.Ssse3.IsSupported ? "yes" : "no")}";
    }

    /// <summary>A meter as the settings ask for, or null when they want neither the title nor the log.</summary>
    public static PerfMeter? Create(ScnVm vm, string gameName, string saveFolder, PlayerSettings settings)
    {
        var (title, writeLog) = settings.PerfForGame();
        if (!title && !writeLog)
            return null;
        var log = writeLog ? new StreamWriter(Path.Combine(saveFolder, "perf.log"), append: false) : null;
        return new PerfMeter(vm, gameName, log, title);
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
            lock (m_vm.GpuChoices)
            {
                foreach (string choice in m_vm.GpuChoices)
                    m_log.WriteLine("  chose " + choice);
                m_vm.GpuChoices.Clear();
            }
            if (m_vm.SlowReads.Count > 0)
            {
                m_log.WriteLine("  slow file reads: " + string.Join("; ", m_vm.SlowReads));
                m_vm.SlowReads.Clear();
            }
            if (m_vm.ReadAheadCounts != m_readAhead)
            {
                m_readAhead = m_vm.ReadAheadCounts;
                m_log.WriteLine($"  read ahead so far: {m_readAhead.Read} files, {m_readAhead.Used} of them used");
            }
            if (!m_alphaLogged && ScnVm.AlphaVectors is { Length: > 0 } alpha)
            {
                m_log.WriteLine("  chose " + alpha);
                m_alphaLogged = true;
            }
            m_log.Flush();
            ops.Clear();
            m_vm.OpCounts.Clear();
            m_vm.NativeTimes.Clear();
        }
        string title = $"{m_name} - {fps:F0} fps, slowest {m_worst:F1} ms";
        m_clock.Restart();
        m_worst = m_worstEngine = m_worstPicture = 0;
        m_frames = m_rounds = 0;
        return m_title ? title : null;
    }

    public void Dispose() => m_log?.Dispose();
}
