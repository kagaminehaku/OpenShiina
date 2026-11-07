// A movie's pictures decoded ahead on a thread of their own (no C# counterpart: the C# player
// decodes each frame when the script asks for it, ScnVm.Movie). The games' MPEG-1 movies have one
// slice a picture, so a picture cannot be shared out between cores; instead the next frames are
// decoded and made into BGR pictures while the scripts run, and the script's thread only takes
// them. The frames are the same and in the same order as the decoder gives them.
#pragma once

#include <condition_variable>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

#include "formats/MpegVideo.h"

namespace openshiina::formats {

class MovieAhead
{
public:
    explicit MovieAhead(std::unique_ptr<IMovieVideo> video);
    ~MovieAhead();

    int Width() const { return m_width; }
    int Height() const { return m_height; }
    double FrameRate() const { return m_rate; }

    /// The next frame as 24-bit BGR (rows of Width() * 3 bytes), or null at the end; it stays valid
    /// until the next call. Waits when the thread has not made it yet.
    const Bytes* Next();

    /// Back to the first frame.
    void Rewind();

private:
    static constexpr int Ahead = 3;

    void Start();
    void Stop();
    void Run();

    std::unique_ptr<IMovieVideo> m_video;
    int m_width, m_height;
    double m_rate;
    std::thread m_thread;
    std::mutex m_mutex;
    std::condition_variable m_changed;
    // a ring of pictures: [m_read, m_read + m_ready) made, the one before m_read in use
    std::vector<Bytes> m_pictures = std::vector<Bytes>(Ahead + 1);
    int m_read = 0, m_ready = 0;
    bool m_holding = false, m_ended = false, m_stopping = false;
};

} // namespace openshiina::formats
