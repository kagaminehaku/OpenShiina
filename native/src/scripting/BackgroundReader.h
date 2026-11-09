// The threads the interpreter reads files on while the scripts go on (C# Task.Run of the reads
// ahead and of the background loader, ScnVm.ReadAhead / ScnVm.Misc): a read posted is done on one
// of them, its result kept in a BackgroundRead the interpreter waits for or lets go. A read let go
// runs to its end on its thread; the interpreter never waits for one it no longer wants.
#pragma once

#include <condition_variable>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

#include "io/Binary.h"

namespace openshiina::scripting {

/// One read: done or not, and the file (none when it was not found or the read failed).
class BackgroundRead
{
public:
    /// Done: Wait would not wait.
    bool Done()
    {
        std::lock_guard lock(m_lock);
        return m_done;
    }

    /// Waits for the read: the file, or false (then it is read again on the interpreter's thread,
    /// where an error shows as it would have).
    bool Wait(Bytes& data)
    {
        std::unique_lock lock(m_lock);
        m_doneChanged.wait(lock, [this] { return m_done; });
        if (!m_found)
            return false;
        data = std::move(m_data);
        m_found = false;
        return true;
    }

    void Finish(bool found, Bytes data)
    {
        {
            std::lock_guard lock(m_lock);
            m_done = true;
            m_found = found;
            m_data = std::move(data);
        }
        m_doneChanged.notify_all();
    }

private:
    std::mutex m_lock;
    std::condition_variable m_doneChanged;
    bool m_done = false, m_found = false;
    Bytes m_data;
};

class BackgroundReader
{
public:
    explicit BackgroundReader(int threads)
    {
        for (int k = 0; k < threads; k++)
            m_threads.emplace_back([this] { Work(); });
    }

    /// Lets the reads under way end; those not started are dropped.
    ~BackgroundReader()
    {
        {
            std::lock_guard lock(m_lock);
            m_stopping = true;
            m_work.clear();
        }
        m_workAdded.notify_all();
        for (auto& t : m_threads)
            t.join();
    }

    BackgroundReader(const BackgroundReader&) = delete;
    BackgroundReader& operator=(const BackgroundReader&) = delete;

    /// Reads with read (on a reader thread); first: before the reads already waiting (the
    /// background loader, which the scripts wait for, before the reads ahead).
    std::shared_ptr<BackgroundRead> Post(std::function<bool(Bytes&)> read, bool first)
    {
        auto result = std::make_shared<BackgroundRead>();
        auto job = [read = std::move(read), result] {
            Bytes data;
            bool found = false;
            try
            {
                found = read(data);
            }
            catch (...)
            {
                found = false;
            }
            result->Finish(found, std::move(data));
        };
        {
            std::lock_guard lock(m_lock);
            if (first)
                m_work.push_front(std::move(job));
            else
                m_work.push_back(std::move(job));
        }
        m_workAdded.notify_one();
        return result;
    }

private:
    void Work()
    {
        for (;;)
        {
            std::function<void()> job;
            {
                std::unique_lock lock(m_lock);
                m_workAdded.wait(lock, [this] { return m_stopping || !m_work.empty(); });
                if (m_stopping)
                    return;
                job = std::move(m_work.front());
                m_work.pop_front();
            }
            job();
        }
    }

    std::mutex m_lock;
    std::condition_variable m_workAdded;
    std::deque<std::function<void()>> m_work;
    bool m_stopping = false;
    std::vector<std::thread> m_threads;
};

} // namespace openshiina::scripting
