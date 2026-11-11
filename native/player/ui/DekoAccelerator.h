// The GPU mode on the Switch (scripting/ScnAccelerator.h): the C# player's compute shaders
// (src/OpenShiina.Gpu/Shaders, compiled with uam into the romfs: shaders/<name>.dksh) run on a
// compute queue of the player's deko3d device. Each kernel keeps a memory block a storage buffer,
// made larger when a call needs more: the inputs cached by the CPU (flushed before the run) and
// by the GPU (its L2 invalidated before the dispatch); the outputs not cached by the GPU, so what
// it writes is in memory when its fence is signalled (the CPU's cache dropped before reading).
// A run is waited for: the routines read its output at once.
//
// A binding can also be on the scripts' memory itself (IScnGpuKernel::Memory, no copies): a memory
// block made on the parts of the scripts' memory around it (heap pages mapped into the code-memory
// window, which nvmap maps for the GPU as any other), cached by the CPU and by the GPU (its L2:
// invalidated before the dispatch, flushed by the full barrier after it; uncached by the GPU, as
// before, with SetScriptCaching(false)), and kept for the next calls (a picture's rows are where
// they were: making one takes about 1 ms for 8 MB, letting it go 1.7). The bytes of the run are
// flushed from the CPU's cache before it (the GPU then reads what the CPU wrote, and no dirty line
// can later land on what it writes) and after (the CPU reads what the GPU wrote). Blocks go when
// the scripts give their parts back (Forget): unmapping memory the GPU still has mapped would fail.
#pragma once

#include <deko3d.hpp>

#include <map>
#include <memory>
#include <string>
#include <vector>

#include "scripting/ScnAccelerator.h"

namespace openshiina::player {

class DekoAccelerator : public scripting::IScnAccelerator
{
public:
    explicit DekoAccelerator(dk::Device device);
    ~DekoAccelerator() override;
    DekoAccelerator(const DekoAccelerator&) = delete;
    DekoAccelerator& operator=(const DekoAccelerator&) = delete;

    std::string Name() const override { return "Tegra X1 (deko3d)"; }
    scripting::IScnGpuKernel* Kernel(const std::string& name) override;
    void Forget(uint8_t* from, int64_t bytes) override;
    /// Whether the GPU caches the blocks on the scripts' memory (the blocks made so far go when it changes).
    void SetScriptCaching(bool gpuCached);
    bool ScriptCaching() const { return m_scriptGpuCached; }

private:
    class DekoKernel;
    friend class DekoKernel;

    dk::Device m_device;
    dk::UniqueQueue m_queue;
    dk::UniqueMemBlock m_codeMem, m_dataMem;
    dk::UniqueCmdBuf m_cmd;
    dk::Fence m_fence;
    uint32_t m_codeUsed = 0;
    // By name; null for a shader the romfs does not have
    std::map<std::string, std::unique_ptr<DekoKernel>> m_kernels;

    // The memory blocks on the scripts' memory, the one used longest ago dropped when there are too many
    struct ScriptBlock
    {
        uint8_t* Start = nullptr;
        int64_t Bytes = 0;
        dk::UniqueMemBlock Memory;
        uint64_t Used = 0;
    };
    /// The block holding [region, region + bytes), made when none does; null when it cannot be.
    ScriptBlock* ScriptBlockFor(uint8_t* region, int64_t bytes);
    std::vector<ScriptBlock> m_scriptBlocks;
    uint64_t m_scriptUses = 0;
    bool m_scriptGpuCached = true;
};

} // namespace openshiina::player
