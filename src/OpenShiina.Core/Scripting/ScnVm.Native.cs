// Machine code inside the SCN modules ("op_0276 label"): the engine calls x86 routines with a
// pointer to {b, a, s, f of the slot, the stack top}; they read their arguments from l[0], l[1]...
// and work on picture buffers in script memory. They run on X86Cpu, an interpreter working on
// the VM's flat memory. A routine can also be replaced by a C# version, found by the SHA-1 of its
// first 64 bytes (docs/engine-notes.md, section 10).

using System.Security.Cryptography;

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

    /// <summary>Adds a routine by the signature <see cref="NativeSignature"/> gives for its code.</summary>
    public void RegisterNative(string signature, string name, NativeRoutine run) => m_natives[signature] = (name, run);

    /// <summary>SHA-1 of the first 64 bytes of a routine, in hex.</summary>
    public string NativeSignature(int address) => Convert.ToHexString(SHA1.HashData(ReadBytes(address, 64)));

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

    /// <summary>Runs a routine translated to C# (Scripting/Generated) the way the interpreter runs it.</summary>
    private void RunTranslated(ScnContext c, ScnNativeArgs args, Action<ScnVm, uint, uint> routine)
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
        catch (ArithmeticException ex)
        {
            throw Error(c, $"Embedded x86 routine at module offset {m_nativeTarget - c.Base:X5}: {ex.Message}");
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
            if (vm.m_natives.Count > 0 && vm.m_natives.TryGetValue(vm.NativeSignature(target), out var routine))
            {
                routine.Run(vm, c, args);
                return 0;
            }
            vm.Interpret(c, target, args);
            return 0;
        });
        // (CPUID routines run on the interpreter too: it reports an Intel CPU with MMX and no
        // SSE, so the scripts take their MMX paths - Oreimo's START 75A84 gives l[0] = 0x11.)
    }
}
