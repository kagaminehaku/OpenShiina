// The GPU mode on the Switch (scripting/ScnAccelerator.h): the C# player's compute shaders
// (src/OpenShiina.Gpu/Shaders, compiled with uam into the romfs: shaders/<name>.dksh) run on a
// compute queue of the player's deko3d device. Each kernel keeps a memory block a storage buffer,
// made larger when a call needs more: the inputs cached by the CPU (flushed before the run) and
// by the GPU (its L2 invalidated before the dispatch); the outputs not cached by the GPU, so what
// it writes is in memory when its fence is signalled (the CPU's cache dropped before reading).
// A run is waited for: the routines read its output at once.
#pragma once

#include <deko3d.hpp>

#include <map>
#include <memory>
#include <string>

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
};

} // namespace openshiina::player
