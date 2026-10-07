// A compute shader ready to run: its pipeline, one descriptor set of storage buffers (binding 0, 1,
// ...) and push constants. The buffers grow as the work needs (they are only touched between
// runs, which the device has finished). A buffer that cannot be made gives an empty span and
// makes the next run fail, so the caller goes back to the CPU.

using System.Runtime.InteropServices;
using OpenShiina.Scripting;
using Silk.NET.Vulkan;

namespace OpenShiina.Gpu;

public sealed unsafe class VulkanKernel : IScnGpuKernel
{
    private readonly VulkanCompute m_owner;
    private readonly Vk m_vk;
    private readonly int m_pushBytes;
    private ShaderModule m_module;
    private DescriptorSetLayout m_setLayout;
    private PipelineLayout m_layout;
    private Pipeline m_pipeline;
    private DescriptorSet m_set;
    private readonly VulkanBuffer?[] m_buffers;
    private bool m_failed;

    internal VulkanKernel(VulkanCompute owner, byte[] code, int buffers, int pushBytes, DescriptorPool pool)
    {
        m_owner = owner;
        m_vk = owner.Api;
        m_pushBytes = pushBytes;
        m_buffers = new VulkanBuffer?[buffers];
        var device = owner.Device;

        fixed (byte* p = code)
        {
            var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)p };
            ShaderModule module;
            VulkanCompute.Check(m_vk.CreateShaderModule(device, &moduleInfo, null, &module), "vkCreateShaderModule");
            m_module = module;
        }

        var bindings = new DescriptorSetLayoutBinding[buffers];
        for (int i = 0; i < buffers; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        fixed (DescriptorSetLayoutBinding* b = bindings)
        {
            var setInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)buffers, PBindings = b };
            DescriptorSetLayout setLayout;
            VulkanCompute.Check(m_vk.CreateDescriptorSetLayout(device, &setInfo, null, &setLayout), "vkCreateDescriptorSetLayout");
            m_setLayout = setLayout;
        }

        var range = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Offset = 0, Size = (uint)pushBytes };
        var setLayoutHandle = m_setLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayoutHandle,
            PushConstantRangeCount = pushBytes > 0 ? 1u : 0u,
            PPushConstantRanges = pushBytes > 0 ? &range : null,
        };
        PipelineLayout layout;
        VulkanCompute.Check(m_vk.CreatePipelineLayout(device, &layoutInfo, null, &layout), "vkCreatePipelineLayout");
        m_layout = layout;

        var entry = Marshal.StringToHGlobalAnsi("main");
        try
        {
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = m_module,
                    PName = (byte*)entry,
                },
                Layout = m_layout,
            };
            Pipeline pipeline;
            VulkanCompute.Check(m_vk.CreateComputePipelines(device, default, 1, &pipelineInfo, null, &pipeline), "vkCreateComputePipelines");
            m_pipeline = pipeline;
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
        }

        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayoutHandle,
        };
        DescriptorSet set;
        VulkanCompute.Check(m_vk.AllocateDescriptorSets(device, &allocate, &set), "vkAllocateDescriptorSets");
        m_set = set;
    }

    /// <summary>Buffer <paramref name="binding"/> for the CPU to fill: at least <paramref name="bytes"/> long.</summary>
    public Span<byte> Input(int binding, long bytes) => Room(binding, bytes) is { } b ? b.Bytes[..(int)bytes] : [];

    /// <summary>Buffer <paramref name="binding"/> for the shader to fill and the CPU to read after <see cref="Run"/>.</summary>
    public Span<byte> Output(int binding, long bytes) => Room(binding, bytes) is { } b ? b.Bytes[..(int)bytes] : [];

    private VulkanBuffer? Room(int binding, long bytes)
    {
        if (m_buffers[binding] is { } buffer && buffer.Size >= bytes)
            return buffer;
        long size = Math.Max(Math.Max(bytes, 65536), (m_buffers[binding]?.Size ?? 0) * 3 / 2);
        if (m_buffers[binding] is { } old)
            m_owner.Free(old);
        m_buffers[binding] = null;
        try
        {
            buffer = m_owner.Buffer(size);
        }
        catch (VulkanException)
        {
            m_failed = true;
            return null;
        }
        m_buffers[binding] = buffer;
        var info = new DescriptorBufferInfo { Buffer = buffer.Handle, Offset = 0, Range = Vk.WholeSize };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = m_set,
            DstBinding = (uint)binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &info,
        };
        m_vk.UpdateDescriptorSets(m_owner.Device, 1, &write, 0, null);
        return buffer;
    }

    /// <summary>
    /// Runs the shader over groupsX x groupsY work groups with <paramref name="push"/> as its push
    /// constants and waits for it; false when the device failed (the CPU then does the work).
    /// </summary>
    public bool Run<T>(in T push, int groupsX, int groupsY) where T : unmanaged
    {
        if (m_failed || m_buffers.Any(b => b == null))
        {
            m_failed = false;
            return false;
        }
        fixed (T* p = &push)
            return m_owner.Run(this, new ReadOnlySpan<byte>(p, sizeof(T)), (uint)groupsX, (uint)groupsY);
    }

    internal void Record(CommandBuffer commands, ReadOnlySpan<byte> push)
    {
        m_vk.CmdBindPipeline(commands, PipelineBindPoint.Compute, m_pipeline);
        var set = m_set;
        m_vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Compute, m_layout, 0, 1, &set, 0, null);
        if (m_pushBytes > 0)
            fixed (byte* p = push)
                m_vk.CmdPushConstants(commands, m_layout, ShaderStageFlags.ComputeBit, 0, (uint)Math.Min(push.Length, m_pushBytes), p);
    }

    internal void Destroy()
    {
        var device = m_owner.Device;
        for (int i = 0; i < m_buffers.Length; i++)
            if (m_buffers[i] is { } buffer)
            {
                m_owner.Free(buffer);
                m_buffers[i] = null;
            }
        m_vk.DestroyPipeline(device, m_pipeline, null);
        m_vk.DestroyPipelineLayout(device, m_layout, null);
        m_vk.DestroyDescriptorSetLayout(device, m_setLayout, null);
        m_vk.DestroyShaderModule(device, m_module, null);
    }
}
