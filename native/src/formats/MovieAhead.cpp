// A movie decoded ahead (see MovieAhead.h)
#include "formats/MovieAhead.h"

#ifdef __SWITCH__
#include <switch.h>
#endif

namespace openshiina::formats {

MovieAhead::MovieAhead(std::unique_ptr<IMovieVideo> video)
    : m_video(std::move(video)), m_width(m_video->Width()), m_height(m_video->Height()), m_rate(m_video->FrameRate())
{
    for (auto& picture : m_pictures)
        picture.resize(size_t(m_width) * 3 * size_t(m_height));
    Start();
}

MovieAhead::~MovieAhead() { Stop(); }

void MovieAhead::Start()
{
    m_read = m_ready = 0;
    m_holding = m_ended = m_stopping = false;
    m_thread = std::thread([this] { Run(); });
}

void MovieAhead::Stop()
{
    {
        std::lock_guard lock(m_mutex);
        m_stopping = true;
    }
    m_changed.notify_all();
    if (m_thread.joinable())
        m_thread.join();
}

void MovieAhead::Rewind()
{
    Stop();
    m_video->Rewind();
    Start();
}

void MovieAhead::Run()
{
#ifdef __SWITCH__
    // Cores 1 and 2, beside the scripts' thread on core 0
    svcSetThreadCoreMask(CUR_THREAD_HANDLE, 2, 6);
#endif
    const int slots = int(m_pictures.size());
    for (;;)
    {
        int slot;
        {
            // room for one more: every slot but the one in use and the made ones
            std::unique_lock lock(m_mutex);
            m_changed.wait(lock, [&] { return m_stopping || m_ready + (m_holding ? 1 : 0) < slots; });
            if (m_stopping)
                return;
            slot = (m_read + m_ready) % slots;
        }
        const MpegFrame* frame = nullptr;
        try
        {
            frame = m_video->Next();
            if (frame != nullptr)
                frame->ToBgr24(m_pictures[size_t(slot)].data(), m_width * 3, m_width, m_height);
        }
        catch (const std::exception&)
        {
            // a stream that breaks off ends there (in the C# player its error would stop the scripts)
            frame = nullptr;
        }
        std::lock_guard lock(m_mutex);
        if (frame == nullptr)
            m_ended = true;
        else
            m_ready++;
        m_changed.notify_all();
        if (m_ended)
            return;
    }
}

const Bytes* MovieAhead::Next()
{
    std::unique_lock lock(m_mutex);
    const int slots = int(m_pictures.size());
    if (m_holding)
    {
        // the picture taken last is let go
        m_holding = false;
        m_changed.notify_all();
    }
    m_changed.wait(lock, [&] { return m_ready > 0 || m_ended; });
    if (m_ready == 0)
        return nullptr;
    const Bytes* picture = &m_pictures[size_t(m_read)];
    m_read = (m_read + 1) % slots;
    m_ready--;
    m_holding = true;
    return picture;
}

} // namespace openshiina::formats
