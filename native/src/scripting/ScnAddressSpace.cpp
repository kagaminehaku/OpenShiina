// C# Scripting/ScnAddressSpace.cs
#include "scripting/ScnAddressSpace.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <new>
#include <string>

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

// What could not be had of the system, and its result code (the player logs what())
class MapFailure : public std::bad_alloc
{
public:
    MapFailure(const char* step, Result rc) { std::snprintf(m_text, sizeof m_text, "the scripts' memory: %s failed (0x%X)", step, unsigned(rc)); }
    const char* what() const noexcept override { return m_text; }

private:
    char m_text[256];
};

Handle s_received = INVALID_HANDLE;

// This process's sessions, threads and memory against its limits, for a failure's message
std::string Limits()
{
    u64 limitHandle = 0;
    if (R_FAILED(svcGetInfo(&limitHandle, InfoType_ResourceLimit, INVALID_HANDLE, 0)) || limitHandle == 0)
        return "no resource limit";
    auto value = [&](LimitableResource which) {
        s64 current = -1, limit = -1;
        svcGetResourceLimitCurrentValue(&current, Handle(limitHandle), which);
        svcGetResourceLimitLimitValue(&limit, Handle(limitHandle), which);
        return std::to_string(current) + " of " + std::to_string(limit);
    };
    std::string text = "sessions " + value(LimitableResource_Sessions) + ", threads " + value(LimitableResource_Threads)
                       + ", memory " + value(LimitableResource_Memory);
    svcCloseHandle(Handle(limitHandle));
    return text;
}

// The other end of the session OwnProcess sends its handle over: takes the request's copied
// handle and closes the session, which ends the sender's request (as hbloader's
// procHandleReceiveThread)
void ReceiveProcessHandle(void* session)
{
    Handle s = Handle(uintptr_t(session));
    void* base = armGetTls();
    hipcMakeRequest(base, HipcMetadata{});
    s32 index = 0;
    if (R_SUCCEEDED(svcReplyAndReceive(&index, &s, 1, INVALID_HANDLE, UINT64_MAX)))
    {
        HipcParsedRequest r = hipcParseRequest(base);
        if (r.meta.num_copy_handles == 1)
            s_received = r.data.copy_handles[0];
    }
    svcCloseHandle(s);
}

// A real handle of this process (svcMapProcessCodeMemory takes no pseudo handle): the one a loader
// (hbloader) gave, else, installed as a title of its own, asked of the kernel as hbloader does: the
// current process's pseudo handle sent over a session of our own comes out a real one
Handle OwnProcess()
{
    static const Handle handle = [] {
        Handle given = envGetOwnProcessHandle();
        if (given != INVALID_HANDLE)
            return given;
        Handle server = INVALID_HANDLE, client = INVALID_HANDLE;
        Result rc = svcCreateSession(&server, &client, 0, 0);
        // An application may own one session it made itself, and libnx's sm: (kept open) is that
        // one: closed meanwhile, as hbloader has closed it by the time it does this
        struct SmClosed
        {
            bool closed = false;
            ~SmClosed()
            {
                if (closed)
                    smInitialize();
            }
        } sm;
        if (R_FAILED(rc))
        {
            smExit();
            sm.closed = true;
            rc = svcCreateSession(&server, &client, 0, 0);
        }
        if (R_FAILED(rc))
            throw MapFailure(("svcCreateSession, sm: closed, " + Limits()).c_str(), rc);
        Thread receiver;
        rc = threadCreate(&receiver, ReceiveProcessHandle, reinterpret_cast<void*>(uintptr_t(server)), nullptr, 0x1000, 0x20, -2);
        if (R_FAILED(rc))
        {
            svcCloseHandle(server);
            svcCloseHandle(client);
            throw MapFailure("threadCreate", rc);
        }
        rc = threadStart(&receiver);
        if (R_SUCCEEDED(rc))
        {
            HipcMetadata meta{};
            meta.num_copy_handles = 1;
            hipcMakeRequest(armGetTls(), meta).copy_handles[0] = CUR_PROCESS_HANDLE;
            svcSendSyncRequest(client);
            threadWaitForExit(&receiver);
        }
        else
            svcCloseHandle(server);
        threadClose(&receiver);
        svcCloseHandle(client);
        if (s_received == INVALID_HANDLE)
            throw MapFailure("getting the process handle", rc);
        return s_received;
    }();
    return handle;
}

// Heap memory at `place` of this process, writable there; the heap's own pages cannot be used
// until it is unmapped
void MapAt(uint8_t* place, void* source, uint64_t size)
{
    Handle self = OwnProcess();
    Result rc = svcMapProcessCodeMemory(self, uint64_t(place), uint64_t(source), size);
    if (R_FAILED(rc))
        throw MapFailure("svcMapProcessCodeMemory", rc);
    rc = svcSetProcessMemoryPermission(self, uint64_t(place), size, Perm_Rw);
    if (R_FAILED(rc))
    {
        svcUnmapProcessCodeMemory(self, uint64_t(place), uint64_t(source), size);
        throw MapFailure("svcSetProcessMemoryPermission", rc);
    }
}

void UnmapAt(uint8_t* place, void* source, uint64_t size)
{
    svcUnmapProcessCodeMemory(OwnProcess(), uint64_t(place), uint64_t(source), size);
}

} // namespace

void ScnAddressSpace::Prepare()
{
    OwnProcess();
}

ScnAddressSpace::ScnAddressSpace()
{
    virtmemLock();
    m_base = static_cast<uint8_t*>(virtmemFindCodeMemory(uint64_t(Size + kGuard), 0));
    m_reservation = m_base ? virtmemAddReservation(m_base, uint64_t(Size + kGuard)) : nullptr;
    virtmemUnlock();
    if (!m_reservation)
        throw MapFailure("virtmemFindCodeMemory (4 GB)", 0);
    m_source[kParts] = std::aligned_alloc(0x1000, kGuard);
    if (!m_source[kParts])
        throw std::bad_alloc();
    std::memset(m_source[kParts], 0, kGuard);
    try
    {
        MapAt(m_base + Size, m_source[kParts], kGuard);
    }
    catch (...)
    {
        std::free(m_source[kParts]);
        m_source[kParts] = nullptr;
        virtmemLock();
        virtmemRemoveReservation(static_cast<VirtmemReservation*>(m_reservation));
        virtmemUnlock();
        throw;
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
    try
    {
        MapAt(m_base + (int64_t(part) << kPartBits), source, kPartSize);
    }
    catch (...)
    {
        std::free(source);
        throw;
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

void ScnAddressSpace::Prepare() {}

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
