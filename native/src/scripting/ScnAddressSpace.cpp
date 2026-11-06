// C# Scripting/ScnAddressSpace.cs
#include "scripting/ScnAddressSpace.h"

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <new>

#ifdef __SWITCH__
#include <switch.h>
#else
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

namespace openshiina::scripting {

#ifdef __SWITCH__

namespace {

// Heap memory at `place` of this process, writable there; the heap's own pages cannot be used
// until it is unmapped
bool MapAt(uint8_t* place, void* source, uint64_t size)
{
    Handle self = envGetOwnProcessHandle();
    if (R_FAILED(svcMapProcessCodeMemory(self, uint64_t(place), uint64_t(source), size)))
        return false;
    if (R_SUCCEEDED(svcSetProcessMemoryPermission(self, uint64_t(place), size, Perm_Rw)))
        return true;
    svcUnmapProcessCodeMemory(self, uint64_t(place), uint64_t(source), size);
    return false;
}

void UnmapAt(uint8_t* place, void* source, uint64_t size)
{
    svcUnmapProcessCodeMemory(envGetOwnProcessHandle(), uint64_t(place), uint64_t(source), size);
}

} // namespace

ScnAddressSpace::ScnAddressSpace()
{
    virtmemLock();
    m_base = static_cast<uint8_t*>(virtmemFindCodeMemory(uint64_t(Size + kGuard), 0));
    m_reservation = m_base ? virtmemAddReservation(m_base, uint64_t(Size + kGuard)) : nullptr;
    virtmemUnlock();
    if (!m_reservation)
        throw std::bad_alloc();
    m_source[kParts] = std::aligned_alloc(0x1000, kGuard);
    if (!m_source[kParts])
        throw std::bad_alloc();
    std::memset(m_source[kParts], 0, kGuard);
    if (!MapAt(m_base + Size, m_source[kParts], kGuard))
    {
        std::free(m_source[kParts]);
        throw std::bad_alloc();
    }
    m_committed[kParts] = 1;
}

ScnAddressSpace::~ScnAddressSpace()
{
    for (int part = 0; part <= kParts; part++)
        if (m_source[part])
        {
            UnmapAt(m_base + (int64_t(part) << kPartBits), m_source[part], part == kParts ? kGuard : kPartSize);
            std::free(m_source[part]);
        }
    virtmemLock();
    virtmemRemoveReservation(static_cast<VirtmemReservation*>(m_reservation));
    virtmemUnlock();
}

void ScnAddressSpace::CommitPart(int part)
{
    // Zeroed while it is still the heap's: mapped, it is only reached through the window
    void* source = std::aligned_alloc(0x1000, kPartSize);
    if (!source)
        throw std::bad_alloc();
    std::memset(source, 0, kPartSize);
    if (!MapAt(m_base + (int64_t(part) << kPartBits), source, kPartSize))
    {
        std::free(source);
        throw std::bad_alloc();
    }
    m_source[part] = source;
}

void ScnAddressSpace::ReleasePart(int part)
{
    UnmapAt(m_base + (int64_t(part) << kPartBits), m_source[part], kPartSize);
    std::free(m_source[part]);
    m_source[part] = nullptr;
}

#else

ScnAddressSpace::ScnAddressSpace()
{
    m_base = static_cast<uint8_t*>(VirtualAlloc(nullptr, SIZE_T(Size + kGuard), MEM_RESERVE, PAGE_NOACCESS));
    if (!m_base)
        throw std::bad_alloc();
    if (!VirtualAlloc(m_base + Size, SIZE_T(kGuard), MEM_COMMIT, PAGE_READWRITE))
    {
        VirtualFree(m_base, 0, MEM_RELEASE);
        throw std::bad_alloc();
    }
    m_committed[kParts] = 1;
}

ScnAddressSpace::~ScnAddressSpace()
{
    VirtualFree(m_base, 0, MEM_RELEASE);
}

void ScnAddressSpace::CommitPart(int part)
{
    if (!VirtualAlloc(m_base + (int64_t(part) << kPartBits), SIZE_T(kPartSize), MEM_COMMIT, PAGE_READWRITE))
        throw std::bad_alloc();
}

void ScnAddressSpace::ReleasePart(int part)
{
    VirtualFree(m_base + (int64_t(part) << kPartBits), SIZE_T(kPartSize), MEM_DECOMMIT);
}

#endif

void ScnAddressSpace::CommitParts(uint32_t address, int64_t length)
{
    int64_t end = std::min<int64_t>(int64_t(address) + length, Size);
    for (int64_t part = address >> kPartBits; part << kPartBits < end; part++)
    {
        if (m_committed[part].load(std::memory_order_acquire))
            continue;
        std::lock_guard lock(m_commitLock);
        if (m_committed[part].load(std::memory_order_relaxed))
            continue;
        CommitPart(int(part));
        m_committedParts++;
        m_committed[part].store(1, std::memory_order_release);
    }
}

void ScnAddressSpace::Discard(uint32_t address, int64_t length)
{
    int64_t start = address, end = std::min(start + length, Size);
    if (end <= start)
        return;
    int64_t first = (start + kPartSize - 1) & ~(kPartSize - 1), last = end & ~(kPartSize - 1);
    if (last <= first)
    {
        Clear(start, end - start);
        return;
    }
    Clear(start, first - start);
    Clear(last, end - last);
    std::lock_guard lock(m_commitLock);
    for (int64_t part = first >> kPartBits; part < last >> kPartBits; part++)
        if (m_committed[part].load(std::memory_order_relaxed))
        {
            m_committed[part].store(0, std::memory_order_release);
            ReleasePart(int(part));
            m_committedParts--;
        }
}

void ScnAddressSpace::Clear(int64_t start, int64_t length)
{
    // Only where memory is there: a part not given any reads as zero
    for (int64_t end = start + length; start < end;)
    {
        int64_t part = start >> kPartBits;
        int64_t n = std::min(end, (part + 1) << kPartBits) - start;
        if (m_committed[part].load(std::memory_order_acquire))
            std::memset(m_base + start, 0, size_t(n));
        start += n;
    }
}

} // namespace openshiina::scripting
