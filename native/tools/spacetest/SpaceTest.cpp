#include "SpaceTest.h"

#include <algorithm>
#include <chrono>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <memory>
#include <random>
#include <thread>
#include <unordered_map>
#include <vector>

#include "scripting/ScnAddressSpace.h"

namespace openshiina::tools {

namespace {

using scripting::ScnAddressSpace;
using Clock = std::chrono::steady_clock;

std::string Format(const char* format, ...)
{
    char text[512];
    va_list args;
    va_start(args, format);
    std::vsnprintf(text, sizeof text, format, args);
    va_end(args);
    return text;
}

double Ms(Clock::time_point since) { return std::chrono::duration<double, std::milli>(Clock::now() - since).count(); }

// The 4 GB as the 4 KB pages written to (the others are zero)
class Model
{
public:
    static constexpr uint32_t kPage = 4096;

    void Write(uint32_t address, const uint8_t* bytes, int64_t count)
    {
        Each(address, count, [&](uint8_t* page, uint32_t offset, uint32_t n, int64_t done) {
            std::memcpy(page + offset, bytes + done, n);
        });
    }

    void Fill(uint32_t address, uint8_t value, int64_t count)
    {
        Each(address, count, [&](uint8_t* page, uint32_t offset, uint32_t n, int64_t) { std::memset(page + offset, value, n); });
    }

    void Discard(uint32_t address, int64_t length)
    {
        int64_t end = std::min<int64_t>(int64_t(address) + length, ScnAddressSpace::Size);
        for (int64_t at = address; at < end;)
        {
            uint32_t number = uint32_t(at / kPage), offset = uint32_t(at % kPage);
            uint32_t n = uint32_t(std::min<int64_t>(kPage - offset, end - at));
            auto found = m_pages.find(number);
            if (found != m_pages.end())
            {
                if (n == kPage)
                    m_pages.erase(found);
                else
                    std::memset(found->second.get() + offset, 0, n);
            }
            at += n;
        }
    }

    uint8_t Read(uint32_t address) const
    {
        auto found = m_pages.find(address / kPage);
        return found == m_pages.end() ? 0 : found->second[address % kPage];
    }

    const std::unordered_map<uint32_t, std::unique_ptr<uint8_t[]>>& Pages() const { return m_pages; }

private:
    template <typename F> void Each(uint32_t address, int64_t count, F f)
    {
        for (int64_t done = 0; done < count;)
        {
            uint32_t at = address + uint32_t(done);
            uint32_t offset = at % kPage;
            uint32_t n = uint32_t(std::min<int64_t>(kPage - offset, count - done));
            auto& page = m_pages[at / kPage];
            if (!page)
                page = std::make_unique<uint8_t[]>(kPage);
            f(page.get(), offset, n, done);
            done += n;
        }
    }

    std::unordered_map<uint32_t, std::unique_ptr<uint8_t[]>> m_pages;
};

} // namespace

int RunSpaceTest(const std::function<void(const std::string&)>& log)
{
    int failed = 0;
    auto check = [&](bool ok, const std::string& what) {
        if (!ok)
        {
            failed++;
            log("FAILED: " + what);
        }
    };

    auto space = std::make_unique<ScnAddressSpace>();
    Model model;

    // Fresh memory reads zero, the top of the 4 GB too (the guard past it)
    for (uint32_t address : {0u, 0x10000000u, 0x7FFFFFF8u, 0xFFFFFFF0u, 0xFFFFFFFFu})
        check(*space->At(address) == 0, Format("fresh byte at %08X is 0", address));

    // An 8-byte access across two parts, and one at the top
    {
        uint64_t value = 0x1122334455667788ull, back = 0;
        std::memcpy(space->At(0x000FFFFC), &value, 8);
        model.Write(0x000FFFFC, reinterpret_cast<uint8_t*>(&value), 8);
        std::memcpy(&back, space->At(0x000FFFFC), 8);
        check(back == value, "8 bytes across parts 0 and 1");
        uint32_t top = 0xA1B2C3D4, topBack = 0;
        std::memcpy(space->At(0xFFFFFFFC), &top, 4);
        model.Write(0xFFFFFFFC, reinterpret_cast<uint8_t*>(&top), 4);
        std::memcpy(&topBack, space->At(0xFFFFFFFC), 4);
        check(topBack == top, "4 bytes at the top of the 4 GB");
    }

    // Random writes in the VM's regions (40 MB of them), read back through the model; discards in
    // between
    std::mt19937 random(12345);
    const uint32_t regions[][2] = {
        {0x00400000, 0x00800000}, // the executable's image
        {0x01000000, 0x02000000}, // the fixed regions
        {0x10000000, 0x11000000}, // the heap
        {0x70000000, 0x70200000}, // modules' code
        {0x90000000, 0x90200000}, // the high heap
        {0xFFF00000, 0xFFFFFFF0}, // the top
    };
    auto randomAddress = [&] {
        auto& region = regions[random() % std::size(regions)];
        return region[0] + uint32_t(random() % (region[1] - region[0]));
    };
    auto start = Clock::now();
    constexpr int kOperations = 100000;
    for (int op = 0; op < kOperations; op++)
    {
        int kind = int(random() % 100);
        uint32_t address = randomAddress();
        if (kind < 60)
        {
            uint8_t bytes[16];
            int count = 1 << (random() % 5); // 1, 2, 4, 8 or 16 bytes
            for (int i = 0; i < count; i++)
                bytes[i] = uint8_t(random());
            std::memcpy(space->At(address), bytes, size_t(count));
            model.Write(address, bytes, count);
        }
        else if (kind < 63)
        {
            // A block of up to 2 MB, written in place
            int64_t length = ScnAddressSpace::Contiguous(address, 1 + random() % (2 << 20));
            uint8_t fill = uint8_t(random() | 1);
            std::memset(space->Block(address, length), fill, size_t(length));
            model.Fill(address, fill, length);
        }
        else if (kind < 66)
        {
            int64_t length = 1 + random() % (3 << 20);
            space->Discard(address, length);
            model.Discard(address, length);
        }
        else
        {
            uint8_t byte = *space->At(address);
            if (byte != model.Read(address))
            {
                check(false, Format("byte at %08X: %02X, should be %02X (op %d)", address, byte, model.Read(address), op));
                if (failed > 20)
                    return failed;
            }
        }
    }
    // Every page the model has is the same in the address space
    int wrong = 0;
    for (const auto& [number, page] : model.Pages())
        if (std::memcmp(space->Block(number * Model::kPage, Model::kPage), page.get(), Model::kPage) != 0)
            wrong++;
    check(wrong == 0, Format("%d pages not as written, of %zu", wrong, model.Pages().size()));
    log(Format("%d random operations in %.0f ms: %zu pages written to, %lld MB given to parts", kOperations, Ms(start),
               model.Pages().size(), (long long)(space->CommittedBytes() >> 20)));
    space.reset();

    // A discard over whole parts gives them back, keeps the bytes outside and reads zero after
    {
        auto fresh = std::make_unique<ScnAddressSpace>();
        uint8_t* block = fresh->Block(0x20000000 - 0x100, (5 << 20) + 0x200);
        std::memset(block, 0x77, (5 << 20) + 0x200);
        fresh->Discard(0x20000000 - 0x80, (5 << 20) + 0x100);
        check(fresh->CommittedBytes() == (2 << 20), Format("parts given back: %lld MB kept, should be 2",
                                                           (long long)(fresh->CommittedBytes() >> 20)));
        check(*fresh->At(0x20000000 - 0x81) == 0x77 && *fresh->At(0x20000000 - 0x80) == 0, "bytes before the discarded range");
        check(*fresh->At(0x20000000 + (5 << 20) + 0x7F) == 0 && *fresh->At(0x20000000 + (5 << 20) + 0x80) == 0x77, "bytes after it");
        bool zero = true;
        for (uint32_t at = 0x20000000; at < 0x20000000 + (5 << 20); at += 4093)
            zero = zero && *fresh->At(at) == 0;
        check(zero, "discarded parts read zero again");
    }

    // Parts made by several threads at once
    {
        auto shared = std::make_unique<ScnAddressSpace>();
        std::vector<std::thread> threads;
        for (int t = 0; t < 3; t++)
            threads.emplace_back([&shared, t] {
                for (uint32_t part = 0; part < 64; part++)
                    std::memset(shared->At(0x30000000 + (part << 20) + uint32_t(t) * 64), t + 1, 16);
            });
        for (auto& thread : threads)
            thread.join();
        int bad = 0;
        for (uint32_t part = 0; part < 64; part++)
            for (int t = 0; t < 3; t++)
                if (*shared->At(0x30000000 + (part << 20) + uint32_t(t) * 64 + 15) != t + 1)
                    bad++;
        check(bad == 0 && shared->CommittedBytes() == (64 << 20), Format("3 threads over 64 parts: %d wrong, %lld MB",
                                                                        bad, (long long)(shared->CommittedBytes() >> 20)));
    }

    // Speed: parts made, 64 MB written and moved, reads that check their part
    {
        auto timed = std::make_unique<ScnAddressSpace>();
        start = Clock::now();
        uint8_t* block = timed->Block(0x10000000, 64 << 20);
        double commit = Ms(start);
        start = Clock::now();
        std::memset(block, 1, 64 << 20);
        double fill = Ms(start);
        start = Clock::now();
        std::memmove(block + (1 << 20), block, 63 << 20);
        double move = Ms(start);
        start = Clock::now();
        uint64_t sum = 0;
        for (uint32_t at = 0x10000000; at < 0x10000000 + (64 << 20); at += 4)
            sum += *timed->At(at);
        double reads = Ms(start);
        check(sum == (16u << 20), "sum of the bytes read");
        log(Format("64 parts made in %.1f ms; 64 MB set in %.1f ms, moved in %.1f ms; 16M reads in %.1f ms",
                   commit, fill, move, reads));
    }
    log(failed == 0 ? "All checks passed." : Format("%d checks failed.", failed));
    return failed;
}

} // namespace openshiina::tools
