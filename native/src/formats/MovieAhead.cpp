// A movie decoded ahead (see MovieAhead.h)
#include "formats/MovieAhead.h"

#include <chrono>

#ifdef __SWITCH__
#include <switch.h>
#endif

namespace openshiina::formats {

MovieAhead::MovieAhead(std::unique_ptr<IMovieVideo> video)
    : m_video(std::move(video)), m_width(m_video->Width()), m_height(m_video->Height()), m_rate(m_video->FrameRate())
{
    m_picture.resize(size_t(m_width) * 3 * size_t(m_height));
    Start();
}

MovieAhead::~MovieAhead() { Stop(); }

void MovieAhead::Start()
{
    m_read = m_ready = 0;
    m_holding = m_ended = m_stopping = false;
    m_taken = nullptr;
    m_pictureMade = false;
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
    const int slots = int(m_frames.size());
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
        auto start = std::chrono::steady_clock::now();
        try
        {
            // the decoder's frame copied: the decoder writes the next ones into its own
            frame = m_video->Next();
            if (frame != nullptr)
            {
                auto& kept = m_frames[size_t(slot)];
                if (!kept)
                    kept = std::make_unique<MpegFrame>(*frame);
                else
                    *kept = *frame;
            }
        }
        catch (const std::exception&)
        {
            // a stream that breaks off ends there (in the C# player its error would stop the scripts)
            frame = nullptr;
        }
        auto decoded = std::chrono::steady_clock::now();
        std::lock_guard lock(m_mutex);
        m_decodingNs += std::chrono::duration_cast<std::chrono::nanoseconds>(decoded - start).count();
        if (frame == nullptr)
            m_ended = true;
        else
        {
            m_ready++;
            m_decoded++;
        }
        m_changed.notify_all();
        if (m_ended)
            return;
    }
}

void MovieAhead::TakeTimes(double& decoding, int& frames, double& pictures, int& made)
{
    std::lock_guard lock(m_mutex);
    decoding = double(m_decodingNs) / 1e6;
    pictures = double(m_picturesNs) / 1e6;
    frames = m_decoded;
    made = m_made;
    m_decodingNs = m_picturesNs = 0;
    m_decoded = m_made = 0;
}

const Bytes& MovieAhead::Picture()
{
    if (m_taken != nullptr && !m_pictureMade)
    {
        auto start = std::chrono::steady_clock::now();
        m_taken->ToBgr24(m_picture.data(), m_width * 3, m_width, m_height);
        m_pictureMade = true;
        auto made = std::chrono::steady_clock::now();
        std::lock_guard lock(m_mutex);
        m_picturesNs += std::chrono::duration_cast<std::chrono::nanoseconds>(made - start).count();
        m_made++;
    }
    return m_picture;
}

const MpegFrame* MovieAhead::Next()
{
    std::unique_lock lock(m_mutex);
    const int slots = int(m_frames.size());
    if (m_holding)
    {
        // the picture taken last is let go
        m_holding = false;
        m_changed.notify_all();
    }
    m_taken = nullptr;
    m_pictureMade = false;
    m_changed.wait(lock, [&] { return m_ready > 0 || m_ended; });
    if (m_ready == 0)
        return nullptr;
    m_taken = m_frames[size_t(m_read)].get();
    m_read = (m_read + 1) % slots;
    m_ready--;
    m_holding = true;
    return m_taken;
}

} // namespace openshiina::formats
