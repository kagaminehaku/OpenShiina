// The interpreter's GPU mode on Vulkan (IScnAccelerator): the kernels of Shaders/, each made when
// first asked for. A kernel that cannot be made, or a device that failed, gives null: the CPU
// does the work.

using OpenShiina.Scripting;

namespace OpenShiina.Gpu;

public sealed class VulkanAccelerator : IScnAccelerator
{
    // Each shader's storage buffers and bytes of push constants (its layout in Shaders/name.comp)
    private static readonly Dictionary<string, (int Buffers, int PushBytes)> s_kernels = new()
    {
        ["scale"] = (4, 24),
        ["enlarge16"] = (4, 12),
        ["subpixel32"] = (3, 68),
        ["rotatezoom"] = (4, 52),
    };

    private readonly VulkanCompute m_gpu;
    private readonly Dictionary<string, VulkanKernel?> m_kernels = [];

    private VulkanAccelerator(VulkanCompute gpu) => m_gpu = gpu;

    /// <summary>The accelerator on the best GPU, or null with the reason (no Vulkan driver, no GPU).</summary>
    public static VulkanAccelerator? TryCreate(out string? error) =>
        VulkanCompute.TryCreate(out error) is { } gpu ? new VulkanAccelerator(gpu) : null;

    public string Name => m_gpu.Name;

    public IScnGpuKernel? Kernel(string name)
    {
        if (m_gpu.Broken)
            return null;
        if (!m_kernels.TryGetValue(name, out var kernel))
        {
            try
            {
                kernel = s_kernels.TryGetValue(name, out var layout) ? m_gpu.Kernel(name, layout.Buffers, layout.PushBytes) : null;
            }
            catch (VulkanException)
            {
                kernel = null;
            }
            m_kernels[name] = kernel;
        }
        return kernel;
    }

    public void Dispose() => m_gpu.Dispose();
}
