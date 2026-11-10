// The GPU mode on the Switch (see DekoAccelerator.h)
#include "ui/DekoAccelerator.h"

#include <switch.h>

#include <cstdio>
#include <cstring>
#include <format>

#include "ui/Renderer.h"

namespace openshiina::player {

namespace {

constexpr uint32_t Align(uint32_t v, uint32_t a) { return (v + a - 1) & ~(a - 1); }

constexpr uint32_t CodeBytes = 64u << 10, CommandBytes = 64u << 10, UniformBytes = 256;
// A buffer grows by whole megabytes (a few sizes, not a new block every call)
constexpr uint32_t BufferStep = 1u << 20;

} // namespace

class DekoAccelerator::DekoKernel : public scripting::IScnGpuKernel
{
public:
    DekoKernel(DekoAccelerator& owner, uint32_t codeOffset) : m_owner(owner) { dk::ShaderMaker{owner.m_codeMem, codeOffset}.initialize(m_shader); }

    uint8_t* Input(int binding, int64_t bytes) override { return Buffer(binding, bytes, false); }
    uint8_t* Output(int binding, int64_t bytes) override { return Buffer(binding, bytes, true); }

    bool Run(const void* parameters, size_t size, int groupsX, int groupsY) override
    {
        if (size > UniformBytes || groupsX <= 0 || groupsY <= 0)
            return false;
        // What the CPU wrote in memory, and nothing of the outputs left in the CPU's cache
        for (auto& [binding, b] : m_buffers)
            b.Memory.flushCpuCache(0, b.Used);
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
            cmd.bindStorageBuffer(DkStage_Compute, uint32_t(binding), b.Memory.getGpuAddr(), b.Used);
        cmd.dispatchCompute(uint32_t(groupsX), uint32_t(groupsY), 1);
        cmd.barrier(DkBarrier_Full, DkInvalidateFlags_L2Cache);
        m_owner.m_queue.submitCommands(cmd.finishList());
        m_owner.m_queue.signalFence(m_owner.m_fence);
        m_owner.m_queue.flush();
        if (m_owner.m_fence.wait() != DkResult_Success)
            return false;
        // The outputs as the GPU wrote them in memory
        for (auto& [binding, b] : m_buffers)
            if (b.Output)
                b.Memory.flushCpuCache(0, b.Used);
        return true;
    }

private:
    struct StorageBuffer
    {
        dk::UniqueMemBlock Memory;
        uint32_t Size = 0, Used = 0;
        bool Output = false;
    };

    uint8_t* Buffer(int binding, int64_t bytes, bool output)
    {
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

std::string DekoAccelerator::ProbeScriptMemory(uint8_t* window, uint32_t bytes)
{
    auto since = [](uint64_t start) { return double(armTicksToNs(armGetSystemTick() - start)) / 1e6; };
    auto error = [] {
        std::string e = Renderer::LastDeviceError;
        Renderer::LastDeviceError.clear();
        return e.empty() ? std::string() : " (" + e + ")";
    };
    Renderer::LastDeviceError.clear();
    uint64_t start = armGetSystemTick();
    dk::UniqueMemBlock scripts = dk::MemBlockMaker{m_device, bytes}
                                     .setFlags(DkMemBlockFlags_CpuCached | DkMemBlockFlags_GpuUncached)
                                     .setStorage(window)
                                     .create();
    double made = since(start);
    if (!scripts)
        return std::format("no memory block on them{} ({:.2f} ms)", error(), made);
    dk::UniqueMemBlock other = dk::MemBlockMaker{m_device, bytes}.setFlags(DkMemBlockFlags_CpuCached | DkMemBlockFlags_GpuUncached).create();
    if (!other)
        return "no memory block to compare with" + error();
    auto* words = reinterpret_cast<uint32_t*>(window);
    auto* otherWords = static_cast<uint32_t*>(other.getCpuAddr());
    uint32_t count = bytes / 4;
    for (uint32_t k = 0; k < count; k++)
        words[k] = k * 2654435761u;
    std::memset(otherWords, 0, bytes);
    start = armGetSystemTick();
    scripts.flushCpuCache(0, bytes);
    double flushed = since(start);
    other.flushCpuCache(0, bytes);
    auto copy = [&](DkGpuAddr from, DkGpuAddr to) {
        m_cmd.clear();
        m_cmd.addMemory(m_dataMem, 0, CommandBytes);
        m_cmd.copyBuffer(from, to, bytes);
        m_cmd.barrier(DkBarrier_Full, DkInvalidateFlags_L2Cache);
        m_queue.submitCommands(m_cmd.finishList());
        m_queue.signalFence(m_fence);
        m_queue.flush();
        return m_fence.wait() == DkResult_Success;
    };
    // the GPU reads them
    start = armGetSystemTick();
    bool read = copy(scripts.getGpuAddr(), other.getGpuAddr());
    double copied = since(start);
    other.flushCpuCache(0, bytes);
    uint32_t readWrong = 0;
    for (uint32_t k = 0; k < count; k++)
        readWrong += otherWords[k] != k * 2654435761u;
    // the GPU writes them
    for (uint32_t k = 0; k < count; k++)
        otherWords[k] = ~(k * 2654435761u);
    other.flushCpuCache(0, bytes);
    bool wrote = copy(other.getGpuAddr(), scripts.getGpuAddr());
    scripts.flushCpuCache(0, bytes);
    uint32_t writeWrong = 0;
    for (uint32_t k = 0; k < count; k++)
        writeWrong += words[k] != ~(k * 2654435761u);
    // what the CPU takes to copy them (the GPU mode's way now)
    start = armGetSystemTick();
    std::memcpy(otherWords, words, bytes);
    double cpuCopy = since(start);
    start = armGetSystemTick();
    scripts = {};
    double letGo = since(start);
    return std::format("{} MB: made {:.2f} ms, CPU cache flushed {:.2f} ms, GPU read {} ({} words wrong, {:.2f} ms), GPU wrote {} ({} words wrong), "
                       "let go {:.2f} ms; the CPU copies them in {:.2f} ms{}",
                       bytes >> 20, made, flushed, read ? "done" : "failed", readWrong, copied, wrote ? "done" : "failed", writeWrong, letGo,
                       cpuCopy, error());
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
