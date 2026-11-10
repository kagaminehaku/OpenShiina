// The GPU mode on the Switch (see DekoAccelerator.h)
#include "ui/DekoAccelerator.h"

#include <algorithm>
#include <cstdio>

namespace openshiina::player {

namespace {

constexpr uint32_t Align(uint32_t v, uint32_t a) { return (v + a - 1) & ~(a - 1); }

constexpr uint32_t CodeBytes = 64u << 10, CommandBytes = 64u << 10, UniformBytes = 256;
// A buffer grows by whole megabytes (a few sizes, not a new block every call)
constexpr uint32_t BufferStep = 1u << 20;
// The blocks on the scripts' memory: how many are kept, and the largest made
constexpr size_t MaxScriptBlocks = 32;
constexpr int64_t MaxScriptBlock = int64_t(256) << 20;

} // namespace

class DekoAccelerator::DekoKernel : public scripting::IScnGpuKernel
{
public:
    DekoKernel(DekoAccelerator& owner, uint32_t codeOffset) : m_owner(owner) { dk::ShaderMaker{owner.m_codeMem, codeOffset}.initialize(m_shader); }

    uint8_t* Input(int binding, int64_t bytes) override { return Buffer(binding, bytes, false); }
    uint8_t* Output(int binding, int64_t bytes) override { return Buffer(binding, bytes, true); }

    bool Memory(int binding, uint8_t* region, int64_t regionBytes, uint8_t* at, int64_t bytes, bool output, uint32_t& offset) override
    {
        if (bytes <= 0 || at < region || at + bytes > region + regionBytes)
            return false;
        ScriptBlock* block = m_owner.ScriptBlockFor(region, regionBytes);
        if (!block)
            return false;
        offset = uint32_t(at - block->Start);
        m_script[binding] = {block->Memory, uint32_t(block->Bytes), offset, uint32_t(bytes), output};
        return true;
    }

    /// The bindings on the scripts' memory are for one run: none stays once their blocks may go.
    void ForgetScripts() { m_script.clear(); }

    bool Run(const void* parameters, size_t size, int groupsX, int groupsY) override
    {
        bool done = Dispatch(parameters, size, groupsX, groupsY);
        m_script.clear();
        return done;
    }

private:
    bool Dispatch(const void* parameters, size_t size, int groupsX, int groupsY)
    {
        if (size > UniformBytes || groupsX <= 0 || groupsY <= 0)
            return false;
        // What the CPU wrote in memory, and nothing of the outputs left in the CPU's cache
        for (auto& [binding, b] : m_buffers)
            if (!m_script.count(binding))
                b.Memory.flushCpuCache(0, b.Used);
        for (auto& [binding, s] : m_script)
            s.Block.flushCpuCache(s.Offset, s.Bytes);
        dk::CmdBuf& cmd = m_owner.m_cmd;
        cmd.clear();
        cmd.addMemory(m_owner.m_dataMem, 0, CommandBytes);
        DkGpuAddr uniforms = m_owner.m_dataMem.getGpuAddr() + CommandBytes;
        // The inputs as the CPU left them, not as the GPU's L2 may remember them from the last run
        cmd.barrier(DkBarrier_None, DkInvalidateFlags_L2Cache);
        cmd.bindShaders(DkStageFlag_Compute, {&m_shader});
        cmd.bindUniformBuffer(DkStage_Compute, 0, uniforms, UniformBytes);
        cmd.pushConstants(uniforms, UniformBytes, 0, uint32_t(size), parameters);
        for (auto& [binding, b] : m_buffers)
            if (!m_script.count(binding))
                cmd.bindStorageBuffer(DkStage_Compute, uint32_t(binding), b.Memory.getGpuAddr(), b.Used);
        for (auto& [binding, s] : m_script)
            cmd.bindStorageBuffer(DkStage_Compute, uint32_t(binding), s.Block.getGpuAddr(), s.Size);
        cmd.dispatchCompute(uint32_t(groupsX), uint32_t(groupsY), 1);
        cmd.barrier(DkBarrier_Full, DkInvalidateFlags_L2Cache);
        m_owner.m_queue.submitCommands(cmd.finishList());
        m_owner.m_queue.signalFence(m_owner.m_fence);
        m_owner.m_queue.flush();
        if (m_owner.m_fence.wait() != DkResult_Success)
            return false;
        // The outputs as the GPU wrote them in memory
        for (auto& [binding, b] : m_buffers)
            if (b.Output && !m_script.count(binding))
                b.Memory.flushCpuCache(0, b.Used);
        for (auto& [binding, s] : m_script)
            if (s.Output)
                s.Block.flushCpuCache(s.Offset, s.Bytes);
        return true;
    }

    struct StorageBuffer
    {
        dk::UniqueMemBlock Memory;
        uint32_t Size = 0, Used = 0;
        bool Output = false;
    };

    // A binding on the scripts' memory: its block (the accelerator's), the block's size, the bytes of the run in it
    struct ScriptBinding
    {
        dk::MemBlock Block;
        uint32_t Size = 0, Offset = 0, Bytes = 0;
        bool Output = false;
    };

    uint8_t* Buffer(int binding, int64_t bytes, bool output)
    {
        m_script.erase(binding);
        if (bytes <= 0 || bytes > (int64_t(1) << 30))
            return nullptr;
        StorageBuffer& b = m_buffers[binding];
        if (!b.Memory || b.Size < uint32_t(bytes) || b.Output != output)
        {
            b.Size = Align(uint32_t(bytes), BufferStep);
            b.Output = output;
            b.Memory = dk::MemBlockMaker{m_owner.m_device, b.Size}
                           .setFlags(DkMemBlockFlags_CpuCached | (output ? DkMemBlockFlags_GpuUncached : DkMemBlockFlags_GpuCached))
                           .create();
            if (!b.Memory)
                return nullptr;
        }
        b.Used = uint32_t(bytes);
        return static_cast<uint8_t*>(b.Memory.getCpuAddr());
    }

    DekoAccelerator& m_owner;
    dk::Shader m_shader;
    std::map<int, StorageBuffer> m_buffers;
    std::map<int, ScriptBinding> m_script;
};

DekoAccelerator::DekoAccelerator(dk::Device device) : m_device(device)
{
    m_queue = dk::QueueMaker{m_device}.setFlags(DkQueueFlags_Compute).create();
    m_codeMem = dk::MemBlockMaker{m_device, Align(CodeBytes + DK_SHADER_CODE_UNUSABLE_SIZE, DK_MEMBLOCK_ALIGNMENT)}
                    .setFlags(DkMemBlockFlags_CpuUncached | DkMemBlockFlags_GpuCached | DkMemBlockFlags_Code)
                    .create();
    m_dataMem = dk::MemBlockMaker{m_device, Align(CommandBytes + UniformBytes, DK_MEMBLOCK_ALIGNMENT)}
                    .setFlags(DkMemBlockFlags_CpuUncached | DkMemBlockFlags_GpuCached)
                    .create();
    m_cmd = dk::CmdBufMaker{m_device}.create();
}

DekoAccelerator::~DekoAccelerator()
{
    m_queue.waitIdle();
}

DekoAccelerator::ScriptBlock* DekoAccelerator::ScriptBlockFor(uint8_t* region, int64_t bytes)
{
    if (bytes <= 0 || bytes > MaxScriptBlock || (uintptr_t(region) | uintptr_t(bytes)) % DK_MEMBLOCK_ALIGNMENT != 0)
        return nullptr;
    for (auto& block : m_scriptBlocks)
        if (block.Start <= region && region + bytes <= block.Start + block.Bytes)
        {
            block.Used = ++m_scriptUses;
            return &block;
        }
    if (m_scriptBlocks.size() >= MaxScriptBlocks)
        m_scriptBlocks.erase(std::min_element(m_scriptBlocks.begin(), m_scriptBlocks.end(),
                                              [](const ScriptBlock& a, const ScriptBlock& b) { return a.Used < b.Used; }));
    dk::UniqueMemBlock memory = dk::MemBlockMaker{m_device, uint32_t(bytes)}
                                    .setFlags(DkMemBlockFlags_CpuCached | DkMemBlockFlags_GpuUncached)
                                    .setStorage(region)
                                    .create();
    if (!memory)
        return nullptr;
    m_scriptBlocks.push_back({region, bytes, std::move(memory), ++m_scriptUses});
    return &m_scriptBlocks.back();
}

void DekoAccelerator::Forget(uint8_t* from, int64_t bytes)
{
    for (auto& [name, kernel] : m_kernels)
        if (kernel)
            kernel->ForgetScripts();
    std::erase_if(m_scriptBlocks, [&](const ScriptBlock& block) { return block.Start < from + bytes && from < block.Start + block.Bytes; });
}

scripting::IScnGpuKernel* DekoAccelerator::Kernel(const std::string& name)
{
    auto found = m_kernels.find(name);
    if (found != m_kernels.end())
        return found->second.get();
    // The shader from the romfs into the code memory
    std::unique_ptr<DekoKernel> kernel;
    std::string path = "romfs:/shaders/" + name + ".dksh";
    if (std::FILE* f = std::fopen(path.c_str(), "rb"))
    {
        std::fseek(f, 0, SEEK_END);
        long length = std::ftell(f);
        std::fseek(f, 0, SEEK_SET);
        uint32_t at = Align(m_codeUsed, DK_SHADER_CODE_ALIGNMENT);
        if (length > 0 && at + uint32_t(length) <= CodeBytes
            && std::fread(static_cast<uint8_t*>(m_codeMem.getCpuAddr()) + at, 1, size_t(length), f) == size_t(length))
        {
            m_codeUsed = at + uint32_t(length);
            kernel = std::make_unique<DekoKernel>(*this, at);
        }
        std::fclose(f);
    }
    auto* result = kernel.get();
    m_kernels[name] = std::move(kernel);
    return result;
}

} // namespace openshiina::player
