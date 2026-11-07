// C# Scripting/ParallelRows.cs (see ParallelRows.h)
#include "scripting/ParallelRows.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdlib>
#include <exception>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

#ifdef __SWITCH__
#include <switch.h>
#endif

namespace openshiina::scripting::ParallelRows {

namespace {

int ReadMaxParts()
{
    if (const char* text = std::getenv("OPENSHIINA_THREADS"))
    {
        int n = std::atoi(text);
        if (n > 0)
            return n;
    }
#ifdef __SWITCH__
    return 3;
#else
    return int(std::clamp(std::thread::hardware_concurrency(), 1u, 4u));
#endif
}

/// Threads that run the pieces of one call at a time, with the calling thread.
class Pool
{
public:
    explicit Pool(int threads)
    {
        for (int k = 0; k < threads; k++)
            m_threads.emplace_back([this, k] { Work(k); });
    }

    ~Pool()
    {
        {
            std::lock_guard lock(m_mutex);
            m_stopping = true;
        }
        m_wake.notify_all();
        for (auto& t : m_threads)
            t.join();
    }

    void Run(int count, int parts, const std::function<void(int, int)>& range)
    {
        std::lock_guard calls(m_call);
        {
            // a thread still in the pieces of the call before leaves them first
            std::unique_lock lock(m_mutex);
            m_finished.wait(lock, [this] { return m_active == 0; });
            m_range = &range;
            m_count = count;
            m_parts = parts;
            m_next = 0;
            m_left = parts;
            m_job++;
        }
        m_wake.notify_all();
        RunPieces();
        std::unique_lock lock(m_mutex);
        m_finished.wait(lock, [this] { return m_left == 0; });
        // a piece that threw (a script error in a routine): thrown on the calling thread
        if (auto error = std::exchange(m_error, nullptr))
            std::rethrow_exception(error);
    }

private:
    void Work(int k)
    {
#ifdef __SWITCH__
        // Cores 1 and 2: the program's cores besides the interpreter's
        int core = 1 + k % 2;
        svcSetThreadCoreMask(CUR_THREAD_HANDLE, core, 1u << core);
#else
        (void)k;
#endif
        uint64_t seen = 0;
        for (;;)
        {
            {
                std::unique_lock lock(m_mutex);
                m_wake.wait(lock, [&] { return m_stopping || m_job != seen; });
                if (m_stopping)
                    return;
                seen = m_job;
                m_active++;
            }
            RunPieces();
            std::lock_guard lock(m_mutex);
            if (--m_active == 0)
                m_finished.notify_all();
        }
    }

    /// Takes pieces of the current call until none is left.
    void RunPieces()
    {
        for (;;)
        {
            int i = m_next.fetch_add(1);
            if (i >= m_parts)
                return;
            std::exception_ptr error;
            try
            {
                (*m_range)(int(int64_t(m_count) * i / m_parts), int(int64_t(m_count) * (i + 1) / m_parts));
            }
            catch (...)
            {
                error = std::current_exception();
            }
            std::lock_guard lock(m_mutex);
            if (error && !m_error)
                m_error = error;
            if (--m_left == 0)
                m_finished.notify_all();
        }
    }

    std::vector<std::thread> m_threads;
    std::mutex m_call, m_mutex;
    std::condition_variable m_wake, m_finished;
    bool m_stopping = false;
    uint64_t m_job = 0;
    // the call's pieces: the threads taking them (m_active) are counted, and a call starts only
    // when none is left in the one before
    const std::function<void(int, int)>* m_range = nullptr;
    int m_count = 0, m_parts = 0, m_left = 0, m_active = 0;
    std::exception_ptr m_error;
    std::atomic<int> m_next{0};
};

Pool& ThePool()
{
    static Pool pool(std::max(0, MaxParts() - 1));
    return pool;
}

} // namespace

int MaxParts()
{
    static const int parts = ReadMaxParts();
    return parts;
}

int Parts(int count, int64_t work)
{
    return int(std::clamp<int64_t>(std::min<int64_t>(work / PieceWork, count), 1, std::max(1, MaxParts())));
}

void For(int count, int64_t work, const std::function<void(int, int)>& range)
{
    if (count <= 0)
        return;
    int parts = Parts(count, work);
    if (parts == 1)
    {
        range(0, count);
        return;
    }
    ThePool().Run(count, parts, range);
}

} // namespace openshiina::scripting::ParallelRows
