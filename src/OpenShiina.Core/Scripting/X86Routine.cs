// An x86 routine embedded in an SCN module, as far as its code can be followed from the entry:
// every reachable instruction (through jumps, conditional branches and direct calls), the jump
// targets, and a signature of the whole routine. The signature (SHA-1 of each instruction's
// offset from the entry and its bytes) is what C# versions of routines are found by, so a routine
// that starts like a known one but differs further on is not taken for it.

using System.Security.Cryptography;
using Iced.Intel;

namespace OpenShiina.Scripting;

public sealed class X86Routine
{
    /// <summary>The entry point (an address in VM memory).</summary>
    public uint Entry { get; }

    /// <summary>Reachable instructions by address.</summary>
    public SortedDictionary<uint, Instruction> Code { get; } = new();

    /// <summary>Addresses some branch jumps to.</summary>
    public HashSet<uint> Targets { get; } = new();

    /// <summary>The code could not be followed everywhere (an indirect jump or call, invalid bytes).</summary>
    public bool Open { get; private set; }

    /// <summary>The routine calls code (a direct call).</summary>
    public bool Calls { get; private set; }

    /// <summary>SHA-1 of the routine, in hex.</summary>
    public string Signature { get; }

    // A routine is a few thousand instructions at most; past this the code is not a routine
    private const int MaxInstructions = 20000;

    public X86Routine(ScnVm vm, uint entry)
    {
        Entry = entry;
        var reader = new Reader(vm);
        var work = new Stack<uint>();
        work.Push(entry);
        while (work.Count > 0 && Code.Count < MaxInstructions)
        {
            uint ip = work.Pop();
            while (!Code.ContainsKey(ip) && Code.Count < MaxInstructions)
            {
                reader.At = ip;
                var decoder = Iced.Intel.Decoder.Create(32, reader, ip);
                decoder.Decode(out var ins);
                if (ins.IsInvalid)
                {
                    Open = true;
                    break;
                }
                Code[ip] = ins;
                if (ins.FlowControl == FlowControl.Return)
                    break;
                if (ins.FlowControl == FlowControl.UnconditionalBranch)
                {
                    if (ins.Op0Kind == OpKind.NearBranch32)
                    {
                        Targets.Add(ins.NearBranch32);
                        work.Push(ins.NearBranch32);
                    }
                    else
                        Open = true;
                    break;
                }
                if (ins.FlowControl == FlowControl.ConditionalBranch)
                {
                    Targets.Add(ins.NearBranch32);
                    work.Push(ins.NearBranch32);
                }
                else if (ins.FlowControl == FlowControl.Call)
                {
                    Calls = true;
                    work.Push(ins.NearBranch32);
                }
                else if (ins.FlowControl is FlowControl.IndirectBranch or FlowControl.IndirectCall or FlowControl.Interrupt or FlowControl.Exception)
                {
                    Open = true;
                    if (ins.FlowControl is FlowControl.IndirectBranch or FlowControl.Exception)
                        break;
                }
                ip = ins.NextIP32;
            }
        }
        if (work.Count > 0)
            Open = true;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        Span<byte> offset = stackalloc byte[4];
        foreach (var (ip, ins) in Code)
        {
            BitConverter.TryWriteBytes(offset, ip - entry);
            sha.AppendData(offset);
            sha.AppendData(vm.ReadBytes((int)ip, ins.Length));
        }
        Signature = Convert.ToHexString(sha.GetHashAndReset());
    }

    private sealed class Reader(ScnVm vm) : CodeReader
    {
        public uint At;
        public override int ReadByte() => vm.ReadByte((int)At++);
    }
}
