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

    private void RegisterNativeCall()
    {
        Register(0x0276, (vm, c, i) =>
        {
            int target = vm.Value(c, i.Args[0]);
            var args = new ScnNativeArgs(vm.BAddress(0), vm.AAddress(0), vm.SAddress(0), vm.FAddress(c.Slot, 0),
                                         vm.StackAddress(c.Slot, c.Sp));
            if (vm.m_natives.Count > 0 && vm.m_natives.TryGetValue(vm.NativeSignature(target), out var routine))
            {
                routine.Run(vm, c, args);
                return 0;
            }
            vm.Write32(X86Arguments, args.B);
            vm.Write32(X86Arguments + 4, args.A);
            vm.Write32(X86Arguments + 8, args.S);
            vm.Write32(X86Arguments + 12, args.F);
            vm.Write32(X86Arguments + 16, args.Stack);
            try
            {
                vm.Cpu.Call((uint)target, X86Arguments, X86StackTop);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArithmeticException)
            {
                throw vm.Error(c, $"Embedded x86 routine at module offset {target - c.Base:X5}: {ex.Message}");
            }
            return 0;
        });
        // (CPUID routines run on the interpreter too: it reports an Intel CPU with MMX and no
        // SSE, so the scripts take their MMX paths - Oreimo's START 75A84 gives l[0] = 0x11.)
    }
}
