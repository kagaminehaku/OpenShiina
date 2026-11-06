// The scripts' memory (C# Scripting/ScnAddressSpace.cs; docs/engine-notes.md, "Flat memory"): the
// whole 32-bit address space as one block of virtual memory, a script address being base + address,
// memory given to each 1 MB part on its first use, which every access checks - a table of 4096
// flags, as the C# player does on Windows.
//
// On the Switch the 4 GB are a window of the code-memory region (virtmemFindCodeMemory), and a part
// is heap memory mapped there with svcMapProcessCodeMemory on this process and made writable:
// svcMapMemory maps only into the stack region (2 GB), code memory objects are too few (about ten),
// and svcMapPhysicalMemory needs a system resource the homebrew loader's process has not. A part
// does not take the first page of the next one with it (one place is mapped only once), so an
// access checks the parts of its first and its last byte. On the PC (to compare with the C#
// player): Windows' reserved and committed memory.
//
// Given back memory reads as zero again; whole parts go back to the heap (on the PC, decommitted).
#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>

namespace openshiina::scripting {

class ScnAddressSpace
{
public:
    /// The 4 GB of the 32-bit addresses.
    static constexpr int64_t Size = int64_t(1) << 32;

    /// Reserves the 4 GB; throws std::bad_alloc when they cannot be.
    ScnAddressSpace();
    ~ScnAddressSpace();
    ScnAddressSpace(const ScnAddressSpace&) = delete;
    ScnAddressSpace& operator=(const ScnAddressSpace&) = delete;

    /// The memory of script address `address`, for an access of up to 16 bytes.
    uint8_t* At(uint32_t address)
    {
        if (!m_committed[address >> kPartBits].load(std::memory_order_acquire)
            || !m_committed[(uint64_t(address) + 15) >> kPartBits].load(std::memory_order_acquire))
            CommitParts(address, 16);
        return m_base + address;
    }

    /// How many of the `length` bytes from `address` are one piece of memory: up to the top of the
    /// 4 GB (at least 1).
    static int64_t Contiguous(uint32_t address, int64_t length) { return length < Size - address ? length : Size - address; }

    /// The memory of [address, address + length), which must not run past the top of the 4 GB
    /// (Contiguous).
    uint8_t* Block(uint32_t address, int64_t length)
    {
        CommitParts(address, length > 0 ? length : 1);
        return m_base + address;
    }

    /// [address, address + length) reads as zero again: whole parts go back, the bytes of the parts
    /// at either end are cleared.
    void Discard(uint32_t address, int64_t length);

    /// The memory given to the parts, in bytes.
    int64_t CommittedBytes() const { return int64_t(m_committedParts.load()) << kPartBits; }

private:
    static constexpr int kPartBits = 20;
    static constexpr int64_t kPartSize = int64_t(1) << kPartBits;
    static constexpr int kParts = 1 << (32 - kPartBits);
    /// Past the 4 GB, for an access of a few bytes at the top: part kParts, there from the start.
    static constexpr int64_t kGuard = 1 << 16;

    void CommitParts(uint32_t address, int64_t length);
    void CommitPart(int part);
    void ReleasePart(int part);
    void Clear(int64_t start, int64_t length);

    uint8_t* m_base = nullptr;
    std::atomic<uint8_t> m_committed[kParts + 1] = {};
    std::atomic<int> m_committedParts{0};
    std::mutex m_commitLock;
#ifdef __SWITCH__
    void* m_reservation = nullptr;
    // The heap memory mapped at each part, and at the guard
    void* m_source[kParts + 1] = {};
#endif
};

} // namespace openshiina::scripting
