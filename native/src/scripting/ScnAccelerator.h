// C# Scripting/IScnAccelerator.cs: the GPU mode, pixel work of the interpreter that a graphics
// processor can do (the Switch player: deko3d compute shaders, player/ui/DekoAccelerator).
// ScnVm::Accelerator is null in the CPU mode; with one, the routines hand it their inner loops and
// keep everything else (the tables, the reads and writes of the scripts' memory), so both modes
// give the same bytes.
//
// A kernel is a compute shader by name; the routine fills its input buffers in place (memory the
// GPU reads), runs it and reads its output buffers in place. A kernel that is missing or a run
// that fails leaves the work to the CPU. A device that shares the CPU's memory (the Switch) can
// also have a binding on the scripts' memory itself (Memory): the shader reads the source and
// writes the destination where they are, with the scripts' row pitches, and nothing is copied.
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
    /// Storage buffer binding on the scripts' own memory: the bytes [at, at + bytes), which lie in
    /// [region, region + regionBytes) (whole parts of the scripts' memory, there now), read or (output)
    /// written by the shader in place until the run ends. The byte of `at` in the buffer goes to
    /// offset. False when the device cannot (the routine copies the bytes then).
    virtual bool Memory(int /*binding*/, uint8_t* /*region*/, int64_t /*regionBytes*/, uint8_t* /*at*/, int64_t /*bytes*/, bool /*output*/,
                        uint32_t& /*offset*/)
    {
        return false;
    }
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
    /// The CPU's memory [from, from + bytes) goes away (parts of the scripts' memory given back, or
    /// all of it): nothing the device made on it (Memory) may stay.
    virtual void Forget(uint8_t* /*from*/, int64_t /*bytes*/) {}
};

} // namespace openshiina::scripting
