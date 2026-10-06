// Machine code inside the SCN modules ("op_0276 label"): the engine calls x86 routines with a
// pointer to {b, a, s, f of the slot, the stack top}; they read their arguments from l[0], l[1]...
// and work on picture buffers in script memory. Each routine is recognised by the SHA-1 of its
// first 64 bytes and run by a C# version of it (docs/engine-notes.md, section 10).

using System.Security.Cryptography;

namespace OpenShiina.Scripting;

/// <summary>What an embedded routine receives: the variable areas and the stack top (l[0]).</summary>
public readonly record struct ScnNativeArgs(int B, int A, int S, int F, int Stack);

public sealed partial class ScnVm
{
    public delegate void NativeRoutine(ScnVm vm, ScnContext context, ScnNativeArgs args);

    private readonly Dictionary<string, (string Name, NativeRoutine Run)> m_natives = new(StringComparer.Ordinal);

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
            string signature = vm.NativeSignature(target);
            if (!vm.m_natives.TryGetValue(signature, out var routine))
                throw vm.Error(c, $"Embedded x86 routine at module offset {target - c.Base:X5} is not supported yet (signature {signature})");
            var args = new ScnNativeArgs(vm.BAddress(0), vm.AAddress(0), vm.SAddress(0), vm.FAddress(c.Slot, 0),
                                         vm.StackAddress(c.Slot, c.Sp));
            routine.Run(vm, c, args);
            return 0;
        });

        // CPUID (Oreimo START 75A84): bits 1 Intel, 2 AMD, 10h MMX, 20h SSE, 40h SSE2, 80h SSE3 ...
        // into l[0]. Reported as an Intel CPU with MMX and no SSE2, so the scripts take their MMX
        // paths (b[77] = 0) and one version of each picture routine is enough.
        RegisterNative("B47F0B95A2BAD93A810AFA338CDF7654B2544213", "cpuid", (vm, c, a) => vm.Write32(a.Stack, 0x11));
    }
}
