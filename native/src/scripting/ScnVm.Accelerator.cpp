// C# Scripting/ScnVm.Accelerator.cs: the GPU mode of the interpreter (ScnAccelerator.h). The
// routines that hand their pixel loops to it gather the scripts' memory straight into the kernel's
// buffers and write its output back.
//
// The C# player times the first calls of each routine on both and keeps the faster, as a PC's
// graphics card may be across PCIe or not much faster; every Switch has the same GPU sharing the
// CPU's memory, so here a call goes to the GPU when it draws enough pixels to be worth a run
// (GpuSmallest: below that the run's start and wait cost more than the work), measured once with
// GpuCheck. Both give the same bytes, so the choice cannot be seen.
//
// GpuCheck (the Switch player with sdmc:/OpenShiina/gpu-check) runs every call the GPU can take on
// both: the GPU's bytes are kept aside (GpuWrite), the CPU's are written as without a GPU, and the
// two are compared when the routine ends (GpuDone), each side timed.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <format>

namespace openshiina::scripting {

namespace {

constexpr int64_t GpuSmallest = 16384;

int64_t Now() { return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }

struct ScaleGpuParameters
{
    int32_t RowCount, ColumnCount, Width;
    uint32_t FullRow, FullColumn;
    int32_t Mode;
    // (the Switch's: where the source and the destination rows are, in words)
    int32_t SourceWord, SourcePitch, OutputWord, OutputPitch;
};

struct Enlarge16GpuParameters
{
    int32_t RowCount, ColumnCount, Width;
};

// The places of the Switch's own kernels: rows, columns, the source's first word and pitch, the destination's
struct PlaceGpuParameters
{
    int32_t RowCount, ColumnCount, SourceWord, SourcePitch, OutputWord, OutputPitch;
};

struct Scale32SseGpuParameters
{
    int32_t RowCount, ColumnCount, SourceWord, SourcePitch, OutputWord, OutputPitch;
    float WholeX, WholeY, WholeXY;
};

struct RotateZoomGpuParameters
{
    int32_t Width, Height, SX, SY, SXEnd, SYEnd, SW, Keep;
    uint32_t Colour, DuxLow, DuxHigh, DvxLow, DvxHigh;
};

} // namespace

bool ScnVm::UseGpu(const char* routine, int64_t pixels)
{
    if (!Accelerator || m_gpuFailed.count(routine))
        return false;
    if (GpuCheck)
    {
        m_gpuPixels = pixels;
        m_gpuStarted = Now();
        return true;
    }
    return GpuAlways || pixels >= GpuSmallest;
}

void ScnVm::GpuDone(const char* routine)
{
    // GpuCheck: the GPU's bytes against the CPU's, now in the scripts' memory
    if (!m_gpuPending.empty())
    {
        auto& counts = GpuCheckCounts[routine];
        bool first = counts.Calls == 0;
        counts.Calls++;
        counts.Pixels += m_gpuPixels;
        counts.GpuMs += double(m_gpuEnded - m_gpuStarted) / 1e6;
        counts.CpuMs += double(Now() - m_gpuEnded) / 1e6;
        std::string differs;
        for (const auto& [address, gpu] : m_gpuPending)
        {
            openshiina::Bytes cpu = ReadBytes(address, int(gpu.size()));
            auto at = std::mismatch(gpu.begin(), gpu.end(), cpu.begin());
            if (at.first != gpu.end())
            {
                size_t offset = size_t(at.first - gpu.begin());
                differs = std::format("at {:08X} + {} of {} rows (GPU {:02X}, CPU {:02X})", address, offset, m_gpuPending.size(), *at.first, *at.second);
                break;
            }
        }
        if (!differs.empty())
            counts.Differed++;
        if ((!differs.empty() && counts.Differed <= 20) || first)
            GpuChecks.push_back(std::format("{}: {}", routine, differs.empty() ? "the GPU's bytes are the CPU's" : "the GPU differs " + differs));
        m_gpuPending.clear();
    }
}

void ScnVm::GpuFailed(const char* routine)
{
    m_gpuPending.clear();
    m_gpuFailed.insert(routine);
    GpuChecks.push_back(std::format("{}: the GPU failed, the CPU draws it from now on", routine));
}

void ScnVm::GpuWrite(int address, const uint8_t* bytes, int length)
{
    if (GpuCheck)
    {
        m_gpuPending.emplace_back(address, openshiina::Bytes(bytes, bytes + length));
        m_gpuEnded = Now();
    }
    else
        WriteBytes(address, bytes, length);
}

bool ScnVm::GpuMemory(IScnGpuKernel* kernel, int binding, int address, int64_t bytes, bool output, uint32_t& word)
{
    if ((address & 3) != 0 || bytes <= 0 || ScnAddressSpace::Contiguous(uint32_t(address), bytes) < bytes)
        return false;
    auto [region, regionBytes] = m_memory->Parts(uint32_t(address), bytes);
    uint32_t offset = 0;
    if (!kernel->Memory(binding, region, regionBytes, m_memory->Base() + uint32_t(address), bytes, output, offset))
        return false;
    word = offset / 4;
    return true;
}

bool ScnVm::GpuSource(IScnGpuKernel* kernel, int binding, int src, int srcPitch, int width, int height, int32_t& word, int32_t& pitch, bool inPlace)
{
    uint32_t at = 0;
    if (inPlace && srcPitch > 0 && srcPitch % 4 == 0 && GpuMemory(kernel, binding, src, int64_t(height - 1) * srcPitch + int64_t(width) * 4, false, at))
    {
        word = int32_t(at), pitch = srcPitch / 4;
        return true;
    }
    uint8_t* source = kernel->Input(binding, int64_t(height) * width * 4);
    if (!source)
        return false;
    for (int r = 0; r < height; r++)
        ReadBytes(src + r * srcPitch, source + size_t(r) * size_t(width) * 4, width * 4);
    word = 0, pitch = width;
    return true;
}

bool ScnVm::GpuTarget(IScnGpuKernel* kernel, int binding, int dst, int dstPitch, int width, int height, int32_t& word, int32_t& pitch, uint8_t*& output)
{
    uint32_t at = 0;
    output = nullptr;
    if (dstPitch > 0 && dstPitch % 4 == 0 && GpuMemory(kernel, binding, dst, int64_t(height - 1) * dstPitch + int64_t(width) * 4, true, at))
    {
        word = int32_t(at), pitch = dstPitch / 4;
        return true;
    }
    output = kernel->Output(binding, int64_t(height) * width * 4);
    word = 0, pitch = width;
    return output != nullptr;
}

void ScnVm::GpuTargetDone(int dst, int dstPitch, int width, int height, const uint8_t* output)
{
    if (output || GpuCheck)
        for (int r = 0; r < height; r++)
            GpuWrite(dst + r * dstPitch, output ? output + size_t(r) * size_t(width) * 4 : Bytes(dst + r * dstPitch, width * 4), width * 4);
}

void ScnVm::GpuTimes(const char* routine, int64_t started, int64_t ran, int64_t back, const IScnGpuKernel& kernel)
{
    if (!RoutineTimes)
        return;
    auto add = [&](const char* part, double ms) {
        auto& time = (*RoutineTimes)[std::string(routine) + " gpu: " + part];
        time.Milliseconds += ms;
        time.Calls++;
    };
    IScnGpuKernel::RunTimes run = kernel.LastRun();
    add("prepare", double(ran - started) / 1e6);
    add("flush before", run.Before);
    add("GPU", run.Gpu);
    add("flush after", run.After);
    add("write back", double(Now() - back) / 1e6);
}

bool ScnVm::SubpixelOnGpu(const std::vector<std::array<int, 3>>& rows, int srcPitch, int inPixels, int outPixels, SubpixelGpuParameters parameters)
{
    int64_t started = Now();
    IScnGpuKernel* kernel = Accelerator->Kernel("subpixel32");
    if (rows.empty() || srcPitch <= 0 || !kernel)
        return false;
    int first = rows[0][1], sourceRows = 0;
    for (const auto& [kind, source, target] : rows)
    {
        if ((source - first) % srcPitch != 0)
            return false;
        sourceRows = std::max(sourceRows, (source - first) / srcPitch + (kind % 2 == 1 ? 2 : 1));
    }
    // The destination rows one pitch apart (as the routine makes them), the source in place when it
    // shares no bytes with them (else read before any is written, as a copy)
    int nr = int(rows.size()), dst = rows[0][2], dstPitch = nr > 1 ? rows[1][2] - dst : outPixels * 4;
    for (int r = 0; r < nr; r++)
        if (rows[size_t(r)][2] != dst + r * dstPitch)
            return false;
    int64_t sourceEnd = first + int64_t(sourceRows - 1) * srcPitch + int64_t(inPixels) * 4;
    int64_t targetEnd = dst + int64_t(nr - 1) * dstPitch + int64_t(outPixels) * 4;
    bool apart = dstPitch > 0 && (sourceEnd <= dst || targetEnd <= first);
    auto* rowTable = reinterpret_cast<int32_t*>(kernel->Input(1, int64_t(nr) * 8));
    uint8_t* output = nullptr;
    if (!rowTable || !GpuSource(kernel, 0, first, srcPitch, inPixels, sourceRows, parameters.SourceWord, parameters.SourcePitch, apart)
        || !GpuTarget(kernel, 2, dst, dstPitch, outPixels, nr, parameters.OutputWord, parameters.OutputPitch, output))
        return false;
    for (int r = 0; r < nr; r++)
    {
        rowTable[r * 2] = rows[size_t(r)][0];
        rowTable[r * 2 + 1] = (rows[size_t(r)][1] - first) / srcPitch;
    }
    parameters.RowCount = nr;
    parameters.OutPixels = outPixels;
    parameters.InPixels = inPixels;
    int64_t ran = Now();
    if (!kernel->Run(&parameters, sizeof parameters, (outPixels + 15) / 16, (nr + 15) / 16))
        return false;
    int64_t back = Now();
    GpuTargetDone(dst, dstPitch, outPixels, nr, output);
    GpuTimes("subpixel32", started, ran, back, *kernel);
    return true;
}

bool ScnVm::RotateZoomOnGpu(const std::vector<uint32_t>& source, int sw, int sh, int l, int t, int r, int b, int sx, int sy, int sxEnd, int syEnd,
                            bool keep, int colour, int64_t u0, int64_t v0, int64_t dux, int64_t dvx, int64_t duy, int64_t dvy, int dstBase,
                            int dstStride)
{
    IScnGpuKernel* kernel = Accelerator->Kernel("rotatezoom");
    if (!kernel)
        return false;
    int width = r - l, height = b - t, rowBytes = width * 4;
    uint8_t* sourceBytes = kernel->Input(0, int64_t(sw) * sh * 4);
    auto* rowTable = reinterpret_cast<int64_t*>(kernel->Input(1, int64_t(height) * 16));
    uint8_t* before = kernel->Input(2, keep ? int64_t(height) * rowBytes : 4);
    uint8_t* output = kernel->Output(3, int64_t(height) * rowBytes);
    if (!sourceBytes || !rowTable || !before || !output)
        return false;
    std::memcpy(sourceBytes, source.data(), size_t(sw) * size_t(sh) * 4);
    for (int row = 0; row < height; row++)
    {
        int yy = t + row;
        // (64-bit sums that wrap, as the C# player's unchecked ones)
        rowTable[row * 2] = int64_t(uint64_t(int64_t(yy)) * uint64_t(duy) + uint64_t(u0));
        rowTable[row * 2 + 1] = int64_t(uint64_t(int64_t(yy)) * uint64_t(dvy) + uint64_t(v0));
        if (keep)
            ReadBytes(dstBase + yy * dstStride + l * 4, before + size_t(row) * size_t(rowBytes), rowBytes);
    }
    RotateZoomGpuParameters parameters{width, height, sx, sy, sxEnd, syEnd, sw, keep ? 1 : 0, uint32_t(colour), uint32_t(dux), uint32_t(uint64_t(dux) >> 32),
                                       uint32_t(dvx), uint32_t(uint64_t(dvx) >> 32)};
    if (!kernel->Run(&parameters, sizeof parameters, (width + 15) / 16, (height + 15) / 16))
        return false;
    for (int row = 0; row < height; row++)
        GpuWrite(dstBase + (t + row) * dstStride + l * 4, output + size_t(row) * size_t(rowBytes), rowBytes);
    return true;
}

bool ScnVm::ScaleOnGpu(int mode, const std::vector<std::array<int, 4>>& rows, const std::vector<int>& rowFrom, const std::vector<std::array<int, 4>>& columns,
                       const std::vector<int>& columnFrom, int width, int src, int srcPitch, int dst, int dstPitch, uint32_t fullRow, uint32_t fullColumn)
{
    int64_t started = Now();
    IScnGpuKernel* kernel = Accelerator->Kernel("scale");
    if (!kernel)
        return false;
    int nr = int(rows.size()), nc = int(columns.size());
    int sourceRows = 0;
    for (int r = 0; r < nr; r++)
    {
        const auto& [flags, count, first, last] = rows[size_t(r)];
        sourceRows = std::max(sourceRows, rowFrom[size_t(r)] + ((flags & 0x80) != 0 ? 1 : 0) + count + ((flags & 0x40) != 0 ? 1 : 0));
    }
    // The source in place when it shares no bytes with the destination (else read before any is
    // written, as a copy), the destination in place
    ScaleGpuParameters parameters{nr, nc, width, fullRow, fullColumn, mode, 0, 0, 0, 0};
    int64_t sourceEnd = src + int64_t(sourceRows - 1) * srcPitch + int64_t(width) * 4;
    int64_t targetEnd = dst + int64_t(nr - 1) * dstPitch + int64_t(nc) * 4;
    bool apart = dstPitch > 0 && (sourceEnd <= dst || targetEnd <= src);
    auto* rowTable = reinterpret_cast<int32_t*>(kernel->Input(1, int64_t(nr) * 20));
    auto* columnTable = reinterpret_cast<int32_t*>(kernel->Input(2, int64_t(nc) * 20));
    uint8_t* output = nullptr;
    if (!rowTable || !columnTable || !GpuSource(kernel, 0, src, srcPitch, width, sourceRows, parameters.SourceWord, parameters.SourcePitch, apart)
        || !GpuTarget(kernel, 3, dst, dstPitch, nc, nr, parameters.OutputWord, parameters.OutputPitch, output))
        return false;
    for (int r = 0; r < nr; r++)
    {
        const auto& [flags, count, first, last] = rows[size_t(r)];
        int32_t* entry = rowTable + r * 5;
        entry[0] = rowFrom[size_t(r)], entry[1] = flags, entry[2] = count, entry[3] = first, entry[4] = last;
    }
    for (int c = 0; c < nc; c++)
    {
        const auto& [flags, count, first, last] = columns[size_t(c)];
        int32_t* entry = columnTable + c * 5;
        entry[0] = columnFrom[size_t(c)], entry[1] = flags & 0xC0, entry[2] = count, entry[3] = first, entry[4] = last;
    }
    int64_t ran = Now();
    if (!kernel->Run(&parameters, sizeof parameters, (nc + 15) / 16, (nr + 15) / 16))
        return false;
    int64_t back = Now();
    GpuTargetDone(dst, dstPitch, nc, nr, output);
    GpuTimes(mode == 0 ? "scale32" : "scale16", started, ran, back, *kernel);
    return true;
}

bool ScnVm::Enlarge16OnGpu(const std::vector<std::array<int, 4>>& rows, const std::vector<std::array<int, 10>>& columns, int width, int src, int srcPitch,
                           int dst, int dstPitch)
{
    IScnGpuKernel* kernel = Accelerator->Kernel("enlarge16");
    if (!kernel)
        return false;
    int nr = int(rows.size()), nc = int(columns.size());
    int sourceRows = 0;
    for (const auto& row : rows)
        sourceRows = std::max(sourceRows, row[0] + (row[1] != 0 ? 2 : 1));
    int rowBytes = width * 4;
    uint8_t* source = kernel->Input(0, int64_t(sourceRows) * rowBytes);
    auto* rowTable = reinterpret_cast<int32_t*>(kernel->Input(1, int64_t(nr) * 16));
    auto* columnTable = reinterpret_cast<int32_t*>(kernel->Input(2, int64_t(nc) * 40));
    uint8_t* output = kernel->Output(3, int64_t(nr) * nc * 4);
    if (!output || !source || !rowTable || !columnTable)
        return false;
    for (int r = 0; r < sourceRows; r++)
        ReadBytes(src + r * srcPitch, source + size_t(r) * size_t(rowBytes), rowBytes);
    std::memcpy(rowTable, rows.data(), size_t(nr) * 16);
    std::memcpy(columnTable, columns.data(), size_t(nc) * 40);
    Enlarge16GpuParameters parameters{nr, nc, width};
    if (!kernel->Run(&parameters, sizeof parameters, (nc + 15) / 16, (nr + 15) / 16))
        return false;
    for (int r = 0; r < nr; r++)
        GpuWrite(dst + r * dstPitch, output + size_t(r) * size_t(nc) * 4, nc * 4);
    return true;
}

bool ScnVm::Scale32SseOnGpu(const std::vector<std::array<uint32_t, 5>>& rows, const std::vector<std::array<uint32_t, 5>>& columns, int width, int height,
                            int src, int srcPitch, int dst, int dstPitch, float wholeX, float wholeY, float wholeXY)
{
    int64_t started = Now();
    // (the Switch's own kernels: the scripts' memory read and written in place where it can be)
    IScnGpuKernel* kernel = Accelerator->Kernel("scale32sse_csh");
    if (!kernel)
        return false;
    int nr = int(rows.size()), nc = int(columns.size());
    Scale32SseGpuParameters parameters{nr, nc, 0, 0, 0, 0, wholeX, wholeY, wholeXY};
    if (!GpuSource(kernel, 0, src, srcPitch, width, height, parameters.SourceWord, parameters.SourcePitch))
        return false;
    auto* rowTable = kernel->Input(1, int64_t(nr) * 20);
    auto* columnTable = kernel->Input(2, int64_t(nc) * 20);
    uint8_t* output = nullptr;
    if (!rowTable || !columnTable || !GpuTarget(kernel, 3, dst, dstPitch, nc, nr, parameters.OutputWord, parameters.OutputPitch, output))
        return false;
    std::memcpy(rowTable, rows.data(), size_t(nr) * 20);
    std::memcpy(columnTable, columns.data(), size_t(nc) * 20);
    int64_t ran = Now();
    if (!kernel->Run(&parameters, sizeof parameters, (nc + 15) / 16, (nr + 15) / 16))
        return false;
    int64_t back = Now();
    GpuTargetDone(dst, dstPitch, nc, nr, output);
    GpuTimes("scale32 (SSE2 build)", started, ran, back, *kernel);
    return true;
}

bool ScnVm::Enlarge32OnGpu(const std::vector<std::array<uint32_t, 8>>& rows, const std::vector<std::array<uint32_t, 8>>& columns, int width, int height,
                           int src, int srcPitch, int dst, int dstPitch)
{
    int64_t started = Now();
    IScnGpuKernel* kernel = Accelerator->Kernel("enlarge32_csh");
    if (!kernel)
        return false;
    int nr = int(rows.size()), nc = int(columns.size());
    PlaceGpuParameters parameters{nr, nc, 0, 0, 0, 0};
    if (!GpuSource(kernel, 0, src, srcPitch, width, height, parameters.SourceWord, parameters.SourcePitch))
        return false;
    auto* rowTable = kernel->Input(1, int64_t(nr) * 32);
    auto* columnTable = kernel->Input(2, int64_t(nc) * 32);
    uint8_t* output = nullptr;
    if (!rowTable || !columnTable || !GpuTarget(kernel, 3, dst, dstPitch, nc, nr, parameters.OutputWord, parameters.OutputPitch, output))
        return false;
    std::memcpy(rowTable, rows.data(), size_t(nr) * 32);
    std::memcpy(columnTable, columns.data(), size_t(nc) * 32);
    int64_t ran = Now();
    if (!kernel->Run(&parameters, sizeof parameters, (nc + 15) / 16, (nr + 15) / 16))
        return false;
    int64_t back = Now();
    GpuTargetDone(dst, dstPitch, nc, nr, output);
    GpuTimes("enlarge32", started, ran, back, *kernel);
    return true;
}

} // namespace openshiina::scripting
