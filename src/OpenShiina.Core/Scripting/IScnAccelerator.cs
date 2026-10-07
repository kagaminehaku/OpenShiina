// The GPU mode: pixel work of the interpreter that a graphics card can do (OpenShiina.Gpu does it
// with Vulkan compute shaders). ScnVm.Accelerator is null in the CPU mode; with one, the C#
// routines hand it their inner loops and keep everything else (the tables, the reads and writes
// of the scripts' memory), so both modes give the same bytes.
//
// A kernel is a shader of OpenShiina.Gpu/Shaders by name; the routine fills its input buffers in
// place (they are the GPU's own memory, mapped), runs it and reads its output buffers in place.
// A kernel that is missing or a run that fails (the device was lost, a buffer could not be made)
// leaves the work to the CPU.

namespace OpenShiina.Scripting;

public interface IScnAccelerator : IDisposable
{
    /// <summary>The device, for the settings and the logs (e.g. "NVIDIA GeForce RTX 2070 (Vulkan 1.4)").</summary>
    string Name { get; }

    /// <summary>The shader <paramref name="name"/>, or null when there is none or the device failed.</summary>
    IScnGpuKernel? Kernel(string name);
}

public interface IScnGpuKernel
{
    /// <summary>Storage buffer <paramref name="binding"/> for the CPU to fill, <paramref name="bytes"/> long (kept until the next run).</summary>
    Span<byte> Input(int binding, long bytes);

    /// <summary>Storage buffer <paramref name="binding"/> for the shader to fill; read it after <see cref="Run"/>.</summary>
    Span<byte> Output(int binding, long bytes);

    /// <summary>Runs groupsX x groupsY work groups with <paramref name="parameters"/> as push constants and waits; false when it failed.</summary>
    bool Run<T>(in T parameters, int groupsX, int groupsY) where T : unmanaged;
}
