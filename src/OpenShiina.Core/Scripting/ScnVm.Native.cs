// Machine code inside the SCN modules ("op_0276 label"): the engine calls x86 routines with a
// pointer to {b, a, s, f of the slot, the stack top}; they read their arguments from l[0], l[1]...
// and work on picture buffers in script memory. A routine runs, in order of preference:
//   - as a C# version written by hand (ScnVm.NativeKernels.cs), found by the signature of the
//     whole routine (X86Routine: every reachable instruction and its offset);
//   - translated to .NET by X86Jit when it is first called (the translation is made in the
//     background; the interpreter runs the routine until it is ready);
//   - on X86Cpu, an interpreter working on the VM's flat memory (routines X86Jit does not take,
//     and every routine where code cannot be generated at run time).
// All three write the same bytes (docs/engine-notes.md, section 10).

using System.Runtime.CompilerServices;

namespace OpenShiina.Scripting;

/// <summary>What an embedded routine receives: the variable areas and the stack top (l[0]).</summary>
public readonly record struct ScnNativeArgs(int B, int A, int S, int F, int Stack);

public sealed partial class ScnVm
{
    public delegate void NativeRoutine(ScnVm vm, ScnContext context, ScnNativeArgs args);

    private readonly Dictionary<string, (string Name, NativeRoutine Run)> m_natives = new(StringComparer.Ordinal);

    // The interpreter's stack and the argument block it gets
    private const int X86StackTop = 0x0A100000, X86Arguments = 0x0A200000;
    private X86Cpu? m_cpu;

    /// <summary>The x86 interpreter of embedded routines (created on first use).</summary>
    public X86Cpu Cpu => m_cpu ??= new X86Cpu(this);

    /// <summary>With OpTimes: time (Stopwatch ticks) and calls per C# routine and per routine run on the interpreter.</summary>
    public Dictionary<string, (long Ticks, int Calls)> NativeTimes { get; } = new();

    /// <summary>Adds a routine by the signature <see cref="NativeSignature"/> gives for its code.</summary>
    public void RegisterNative(string signature, string name, NativeRoutine run) => m_natives[signature] = (name, run);

    /// <summary>The signature of the routine at an address (X86Routine.Signature).</summary>
    public string NativeSignature(int address) => RoutineAt(address).Code.Signature;

    /// <summary>
    /// Translate routines to .NET (X86Jit). On by default where code can be generated at run time;
    /// off, every routine without a C# version runs on the interpreter.
    /// </summary>
    public bool JitX86 { get; set; } = RuntimeFeature.IsDynamicCodeCompiled;

    /// <summary>Translate in the background (the interpreter runs a routine until its translation is ready).</summary>
    public bool JitInBackground { get; set; } = true;

    /// <summary>What is known of the routine at an address: its code, its C# version, its translation.</summary>
    private sealed class RoutineInfo(X86Routine code)
    {
        public readonly X86Routine Code = code;
        public (string Name, NativeRoutine Run)? Native;
        public Task<X86Jit.Routine?>? Translation;
        public string? NotTranslated;

        public X86Jit.Routine? Translated => Translation is { IsCompletedSuccessfully: true } t ? t.Result : null;
    }

    private readonly Dictionary<int, RoutineInfo> m_routines = new();

    /// <summary>Routines known so far: their address, signature, C# version and translation (for reports).</summary>
    public IEnumerable<(int Address, string Signature, string? Native, bool Translated, string? NotTranslated)> RoutineReport() =>
        m_routines.Select(p => (p.Key, p.Value.Code.Signature, p.Value.Native?.Name, p.Value.Translated != null,
                                p.Value.NotTranslated ?? (p.Value.Translation is { IsFaulted: true } t ? t.Exception!.InnerException!.Message : null)));

    private RoutineInfo RoutineAt(int address)
    {
        if (m_routines.TryGetValue(address, out var info))
            return info;
        info = new RoutineInfo(new X86Routine(this, (uint)address));
        if (m_natives.TryGetValue(info.Code.Signature, out var native))
            info.Native = native;
        else if (JitX86)
        {
            var code = info.Code;
            info.Translation = JitInBackground
                ? Task.Run(() => Translate(code, info))
                : Task.FromResult(Translate(code, info));
        }
        m_routines[address] = info;
        return info;

        static X86Jit.Routine? Translate(X86Routine code, RoutineInfo info)
        {
            var routine = X86Jit.Compile(code, out string? reason);
            info.NotTranslated = reason;
            return routine;
        }
    }

    /// <summary>Code was loaded again: what was known of routines no longer applies.</summary>
    private void ForgetRoutines()
    {
        m_routines.Clear();
        m_cpu?.Forget();
    }

    /// <summary>The l[index] value a routine sees: the dword at the stack top + index.</summary>
    public int NativeArg(ScnNativeArgs args, int index) => Read32(args.Stack + 4 * index);

    // The routine being called (for VerifyNatives)
    private int m_nativeTarget;

    /// <summary>Runs an embedded routine on the interpreter.</summary>
    private void Interpret(ScnContext c, int target, ScnNativeArgs args)
    {
        Write32(X86Arguments, args.B);
        Write32(X86Arguments + 4, args.A);
        Write32(X86Arguments + 8, args.S);
        Write32(X86Arguments + 12, args.F);
        Write32(X86Arguments + 16, args.Stack);
        try
        {
            Cpu.Call((uint)target, X86Arguments, X86StackTop);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArithmeticException)
        {
            throw Error(c, $"Embedded x86 routine at module offset {target - c.Base:X5}: {ex.Message}");
        }
    }

    /// <summary>Runs a routine translated by X86Jit the way the interpreter runs it.</summary>
    private void RunTranslated(ScnContext c, int target, ScnNativeArgs args, X86Jit.Routine routine)
    {
        Write32(X86Arguments, args.B);
        Write32(X86Arguments + 4, args.A);
        Write32(X86Arguments + 8, args.S);
        Write32(X86Arguments + 12, args.F);
        Write32(X86Arguments + 16, args.Stack);
        try
        {
            routine(this, X86Arguments, X86StackTop);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArithmeticException)
        {
            throw Error(c, $"Embedded x86 routine at module offset {target - c.Base:X5}: {ex.Message}");
        }
    }

    /// <summary>
    /// With VerifyNatives: runs the routine on the interpreter, keeps what it wrote to
    /// [start, start + length), puts the old bytes back for the C# version, and afterwards
    /// compares. Call before the C# version; dispose after it.
    /// </summary>
    private IDisposable? VerifyRegion(ScnContext c, ScnNativeArgs args, int start, int length)
    {
        if (!VerifyNatives || length <= 0)
            return null;
        byte[] before = ReadBytes(start, length);
        Interpret(c, m_nativeTarget, args);
        byte[] expected = ReadBytes(start, length);
        WriteBytes(start, before);
        return new Check(() =>
        {
            byte[] actual = ReadBytes(start, length);
            int at = actual.AsSpan().CommonPrefixLength(expected);
            if (at < length)
                throw Error(c, $"Native routine differs from the x86 code at +{at:X} ({actual[at]:X2} instead of {expected[at]:X2})");
        });
    }

    private sealed class Check(Action check) : IDisposable
    {
        public void Dispose() => check();
    }

    private void RegisterNativeCall()
    {
        Register(0x0276, (vm, c, i) =>
        {
            int target = vm.Value(c, i.Args[0]);
            var args = new ScnNativeArgs(vm.BAddress(0), vm.AAddress(0), vm.SAddress(0), vm.FAddress(c.Slot, 0),
                                         vm.StackAddress(c.Slot, c.Sp));
            vm.m_nativeTarget = target;
            var info = vm.RoutineAt(target);
            long started = vm.OpTimes != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            string name;
            vm.RememberCall(c, target, info.Native?.Name ?? (info.Translated != null ? "jit" : "x86"), args);
            if (info.Native is { } native)
            {
                native.Run(vm, c, args);
                name = native.Name;
            }
            else if (info.Translated is { } translated)
            {
                vm.RunTranslated(c, target, args, translated);
                name = $"jit {target - c.CodeBase:X5}";
            }
            else
            {
                vm.Interpret(c, target, args);
                name = $"x86 {target - c.CodeBase:X5}";
            }
            if (vm.OpTimes != null)
            {
                // C# versions by name, translated and interpreted routines by their offset in the module
                var (ticks, calls) = vm.NativeTimes.GetValueOrDefault(name);
                vm.NativeTimes[name] = (ticks + System.Diagnostics.Stopwatch.GetTimestamp() - started, calls + 1);
            }
            return 0;
        });
        // (CPUID routines run on the interpreter too: it reports an Intel CPU with MMX and no
        // SSE, so the scripts take their MMX paths - Oreimo's START 75A84 gives l[0] = 0x11.)
    }
}
