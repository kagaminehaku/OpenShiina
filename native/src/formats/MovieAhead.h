// A movie's pictures decoded ahead on a thread of their own (no C# counterpart: the C# player
// decodes each frame when the script asks for it, ScnVm.Movie). The games' MPEG-1 movies have one
// slice a picture, so a picture cannot be shared out between cores; instead the next frames are
// decoded (and their planes kept) while the scripts run, and the script's thread only takes them.
// Only the frame drawn is made into a BGR picture, on the script's thread (Picture): a movie behind
// its time takes several frames for one drawn, and making each picture on the decoding thread
// (9 ms a 1280 x 720 frame on the Switch) left it further behind. The frames are the same and in the
// same order as the decoder gives them.
#pragma once

#include <condition_variable>
#include <cstdint>
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

    /// The next frame, or null at the end; it stays valid until the next call. Waits when the thread
    /// has not decoded it yet.
    const MpegFrame* Next();

    /// The frame Next gave last as 24-bit BGR (rows of Width() * 3 bytes), made the first time it is
    /// asked for; valid until the next call of Next.
    const Bytes& Picture();

    /// Back to the first frame.
    void Rewind();

    /// The time since the last call, for the slow log: milliseconds decoding and the frames decoded
    /// (the thread's), milliseconds making pictures and the pictures made (Picture's).
    void TakeTimes(double& decoding, int& frames, double& pictures, int& made);

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
    // a ring of frames: [m_read, m_read + m_ready) decoded, the one before m_read in use
    std::vector<std::unique_ptr<MpegFrame>> m_frames = std::vector<std::unique_ptr<MpegFrame>>(Ahead + 1);
    int m_read = 0, m_ready = 0;
    // The frame in use and its picture (made for it or not yet)
    const MpegFrame* m_taken = nullptr;
    Bytes m_picture;
    bool m_pictureMade = false;
    // (TakeTimes, under m_mutex)
    int64_t m_decodingNs = 0, m_picturesNs = 0;
    int m_decoded = 0, m_made = 0;
    bool m_holding = false, m_ended = false, m_stopping = false;
};

} // namespace openshiina::formats
