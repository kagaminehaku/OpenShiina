// A Vulkan device for compute work only: one queue, one command buffer used again and again, and
// kernels (a compute shader with its storage buffers and push constants) run one at a time, the
// caller waiting for each (the interpreter needs the pixels before its next instruction).
//
// Buffers are host-visible and coherent, mapped for good, so the CPU writes the inputs and reads
// the outputs in place, with no copy commands; where possible in cached memory. On phones and
// integrated GPUs that is the GPU's own memory; a desktop card reads and writes it over PCIe,
// which costs less than the CPU writing into the card's memory (resizable BAR, write-combined:
// gathering scale32's 5.8 MB of source there took 3-5 ms, into cached memory 0.9 ms).

using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace OpenShiina.Gpu;

public sealed unsafe class VulkanCompute : IDisposable
{
    private readonly Vk m_vk;
    private Instance m_instance;
    private PhysicalDevice m_physical;
    private Device m_device;
    private Queue m_queue;
    private CommandPool m_pool;
    private CommandBuffer m_commands;
    private Fence m_fence;
    private DescriptorPool m_descriptors;
    private PhysicalDeviceMemoryProperties m_memory;
    private readonly List<VulkanKernel> m_kernels = [];

    /// <summary>The device and the Vulkan version it has.</summary>
    public string Name { get; }

    /// <summary>Set when the device failed (lost, out of memory): every later run returns false.</summary>
    public bool Broken { get; private set; }

    internal Vk Api => m_vk;
    internal Device Device => m_device;

    private VulkanCompute(Vk vk)
    {
        m_vk = vk;
        Name = "";
        var appName = Marshal.StringToHGlobalAnsi("OpenShiina");
        try
        {
            var app = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                PEngineName = (byte*)appName,
                ApiVersion = Vk.Version10,
            };
            var instanceInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
            Instance instance;
            Check(vk.CreateInstance(&instanceInfo, null, &instance), "vkCreateInstance");
            m_instance = instance;
        }
        finally
        {
            Marshal.FreeHGlobal(appName);
        }

        // A real GPU with a compute queue: a discrete one before an integrated one, never a
        // software renderer (llvmpipe, SwiftShader: the CPU code is faster)
        uint count = 0;
        Check(vk.EnumeratePhysicalDevices(m_instance, &count, null), "vkEnumeratePhysicalDevices");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            Check(vk.EnumeratePhysicalDevices(m_instance, &count, p), "vkEnumeratePhysicalDevices");
        (PhysicalDevice Device, uint Family, int Rank, string Name, uint Version)? best = null;
        foreach (var device in devices)
        {
            PhysicalDeviceProperties properties;
            vk.GetPhysicalDeviceProperties(device, &properties);
            int rank = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };
            if (rank == 0 || ComputeFamily(device) is not { } family || (best is { } b && b.Rank >= rank))
                continue;
            best = (device, family, rank, Marshal.PtrToStringUTF8((nint)properties.DeviceName) ?? "GPU", properties.ApiVersion);
        }
        if (best is not { } chosen)
            throw new NotSupportedException("No GPU with Vulkan compute.");
        m_physical = chosen.Device;
        Name = $"{chosen.Name} (Vulkan {chosen.Version >> 22}.{(chosen.Version >> 12) & 0x3FF})";
        PhysicalDeviceMemoryProperties memory;
        vk.GetPhysicalDeviceMemoryProperties(m_physical, &memory);
        m_memory = memory;

        float priority = 1;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = chosen.Family,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var deviceInfo = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo };
        Device logical;
        Check(vk.CreateDevice(m_physical, &deviceInfo, null, &logical), "vkCreateDevice");
        m_device = logical;
        Queue queue;
        vk.GetDeviceQueue(m_device, chosen.Family, 0, &queue);
        m_queue = queue;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = chosen.Family,
        };
        CommandPool pool;
        Check(vk.CreateCommandPool(m_device, &poolInfo, null, &pool), "vkCreateCommandPool");
        m_pool = pool;
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = m_pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer commands;
        Check(vk.AllocateCommandBuffers(m_device, &allocateInfo, &commands), "vkAllocateCommandBuffers");
        m_commands = commands;
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Fence fence;
        Check(vk.CreateFence(m_device, &fenceInfo, null, &fence), "vkCreateFence");
        m_fence = fence;
        var size = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 256 };
        var descriptorInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 64,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        DescriptorPool descriptors;
        Check(vk.CreateDescriptorPool(m_device, &descriptorInfo, null, &descriptors), "vkCreateDescriptorPool");
        m_descriptors = descriptors;
    }

    /// <summary>The device, or null with the reason when there is none (no driver, no GPU).</summary>
    public static VulkanCompute? TryCreate(out string? error)
    {
        Vk? vk = null;
        try
        {
            vk = Vk.GetApi();
            error = null;
            return new VulkanCompute(vk);
        }
        catch (Exception e)
        {
            error = e.Message;
            vk?.Dispose();
            return null;
        }
    }

    private uint? ComputeFamily(PhysicalDevice device)
    {
        uint count = 0;
        m_vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            m_vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        for (uint i = 0; i < count; i++)
            if ((families[i].QueueFlags & QueueFlags.ComputeBit) != 0)
                return i;
        return null;
    }

    internal static void Check(Result result, string call)
    {
        if (result != Result.Success)
            throw new VulkanException(call, result);
    }

    /// <summary>A kernel from an embedded shader (Shaders/name.spv) with that many storage buffers and bytes of push constants.</summary>
    public VulkanKernel Kernel(string name, int buffers, int pushBytes)
    {
        using var stream = typeof(VulkanCompute).Assembly.GetManifestResourceStream($"OpenShiina.Gpu.{name}.spv")
            ?? throw new InvalidOperationException($"No shader {name}.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        var kernel = new VulkanKernel(this, code, buffers, pushBytes, m_descriptors);
        m_kernels.Add(kernel);
        return kernel;
    }

    /// <summary>A mapped buffer of at least <paramref name="size"/> bytes.</summary>
    internal VulkanBuffer Buffer(long size)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)size,
            Usage = BufferUsageFlags.StorageBufferBit,
            SharingMode = SharingMode.Exclusive,
        };
        VkBuffer buffer;
        Check(m_vk.CreateBuffer(m_device, &info, null, &buffer), "vkCreateBuffer");
        MemoryRequirements requirements;
        m_vk.GetBufferMemoryRequirements(m_device, buffer, &requirements);
        var needed = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        var wanted = needed | MemoryPropertyFlags.HostCachedBit;
        DeviceMemory memory = default;
        Result result = Result.ErrorOutOfDeviceMemory;
        foreach (var flags in new[] { wanted, needed })
        {
            if (MemoryType(requirements.MemoryTypeBits, flags) is not { } type)
                continue;
            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = type,
            };
            result = m_vk.AllocateMemory(m_device, &allocate, null, &memory);
            if (result == Result.Success)
                break;
        }
        if (result != Result.Success)
        {
            m_vk.DestroyBuffer(m_device, buffer, null);
            throw new VulkanException("vkAllocateMemory", result);
        }
        Check(m_vk.BindBufferMemory(m_device, buffer, memory, 0), "vkBindBufferMemory");
        void* mapped;
        Check(m_vk.MapMemory(m_device, memory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
        return new VulkanBuffer(buffer, memory, size, mapped);
    }

    private uint? MemoryType(uint bits, MemoryPropertyFlags flags)
    {
        for (int i = 0; i < m_memory.MemoryTypeCount; i++)
            if ((bits & (1u << i)) != 0 && (m_memory.MemoryTypes[i].PropertyFlags & flags) == flags)
                return (uint)i;
        return null;
    }

    internal void Free(VulkanBuffer buffer)
    {
        m_vk.UnmapMemory(m_device, buffer.Memory);
        m_vk.DestroyBuffer(m_device, buffer.Handle, null);
        m_vk.FreeMemory(m_device, buffer.Memory, null);
    }

    /// <summary>Records the kernel's dispatch, submits it and waits; false when the device failed.</summary>
    internal bool Run(VulkanKernel kernel, ReadOnlySpan<byte> push, uint groupsX, uint groupsY)
    {
        if (Broken)
            return false;
        try
        {
            var commands = m_commands;
            Check(m_vk.ResetCommandBuffer(commands, 0), "vkResetCommandBuffer");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(m_vk.BeginCommandBuffer(commands, &begin), "vkBeginCommandBuffer");
            kernel.Record(commands, push);
            m_vk.CmdDispatch(commands, groupsX, groupsY, 1);
            // The shader's writes, seen by the CPU reading the mapped output
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
            };
            m_vk.CmdPipelineBarrier(commands, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
            Check(m_vk.EndCommandBuffer(commands), "vkEndCommandBuffer");
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commands };
            var fence = m_fence;
            Check(m_vk.QueueSubmit(m_queue, 1, &submit, fence), "vkQueueSubmit");
            Check(m_vk.WaitForFences(m_device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            Check(m_vk.ResetFences(m_device, 1, &fence), "vkResetFences");
            return true;
        }
        catch (VulkanException)
        {
            Broken = true;
            return false;
        }
    }

    public void Dispose()
    {
        if (m_device.Handle != 0)
        {
            m_vk.DeviceWaitIdle(m_device);
            foreach (var kernel in m_kernels)
                kernel.Destroy();
            m_kernels.Clear();
            m_vk.DestroyDescriptorPool(m_device, m_descriptors, null);
            m_vk.DestroyFence(m_device, m_fence, null);
            m_vk.DestroyCommandPool(m_device, m_pool, null);
            m_vk.DestroyDevice(m_device, null);
            m_device = default;
        }
        if (m_instance.Handle != 0)
        {
            m_vk.DestroyInstance(m_instance, null);
            m_instance = default;
        }
        m_vk.Dispose();
    }
}

/// <summary>A failed Vulkan call.</summary>
public sealed class VulkanException(string call, Result result) : Exception($"{call}: {result}")
{
    public Result Result { get; } = result;
}

/// <summary>A storage buffer mapped into the CPU's memory for good.</summary>
internal sealed unsafe class VulkanBuffer(VkBuffer handle, DeviceMemory memory, long size, void* mapped)
{
    public VkBuffer Handle { get; } = handle;
    public DeviceMemory Memory { get; } = memory;
    public long Size { get; } = size;
    public Span<byte> Bytes => new(mapped, (int)Size);
}
