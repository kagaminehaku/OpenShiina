// C# Scripting/IScnAccelerator.cs: the GPU mode, pixel work of the interpreter that a graphics
// processor can do (the Switch player: deko3d compute shaders, player/ui/DekoAccelerator).
// ScnVm::Accelerator is null in the CPU mode; with one, the routines hand it their inner loops and
// keep everything else (the tables, the reads and writes of the scripts' memory), so both modes
// give the same bytes.
//
// A kernel is a compute shader by name; the routine fills its input buffers in place (memory the
// GPU reads), runs it and reads its output buffers in place. A kernel that is missing or a run
// that fails leaves the work to the CPU.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace openshiina::scripting {

class IScnGpuKernel
{
public:
    virtual ~IScnGpuKernel() = default;
    /// Storage buffer binding for the CPU to fill, bytes long (kept until the next run); null when
    /// it cannot be had.
    virtual uint8_t* Input(int binding, int64_t bytes) = 0;
    /// Storage buffer binding for the shader to fill; read it after Run.
    virtual uint8_t* Output(int binding, int64_t bytes) = 0;
    /// Runs groupsX x groupsY work groups with the parameters (the shader's uniform block) and
    /// waits; false when it failed.
    virtual bool Run(const void* parameters, size_t size, int groupsX, int groupsY) = 0;
};

class IScnAccelerator
{
public:
    virtual ~IScnAccelerator() = default;
    /// The device, for the logs.
    virtual std::string Name() const = 0;
    /// The shader by name, or null when there is none or the device failed.
    virtual IScnGpuKernel* Kernel(const std::string& name) = 0;
};

} // namespace openshiina::scripting
