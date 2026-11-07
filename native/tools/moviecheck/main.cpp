// moviecheck <movie> [frames]: decodes an MPEG-1 movie (a system stream, or a video stream alone)
// with formats/MpegVideo and prints, for each frame in display order, the SHA-256 of its picture as
// 24-bit BGR, and the frames' count and the decoding errors at the end. The C# player's decoder
// gives the same lines for the same movie (Formats/MpegVideo.cs): compare them.
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <exception>

#include "formats/MpegVideo.h"
#include "platform/Files.h"
#include "util/Encoding.h"

using namespace openshiina;

int main(int argc, char** argv)
{
    if (argc < 2)
    {
        std::fprintf(stderr, "moviecheck <movie> [frames]\n");
        return 2;
    }
    int limit = argc > 2 ? std::atoi(argv[2]) : 1 << 30;
    Bytes data;
    if (!Files::ReadAll(argv[1], data))
    {
        std::fprintf(stderr, "Cannot read %s\n", argv[1]);
        return 1;
    }
    try
    {
        bool alone = data.size() >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0xB3;
        auto video = alone ? formats::Mpeg1Video(data) : formats::Mpeg1Video(formats::MpegSystemStream(data));
        std::printf("%dx%d, %.3f frames a second\n", video.Width(), video.Height(), video.FrameRate());
        Bytes bgr(size_t(video.Width()) * 3 * size_t(video.Height()));
        auto start = std::chrono::steady_clock::now();
        int n = 0;
        for (; n < limit; n++)
        {
            const formats::MpegFrame* frame = video.Next();
            if (frame == nullptr)
                break;
            frame->ToBgr24(bgr.data(), video.Width() * 3, video.Width(), video.Height());
            std::printf("%d %s\n", n, Encoding::Sha256::HexOf(bgr.data(), bgr.size()).c_str());
        }
        double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        std::printf("%d frames, %d errors, %.2f s\n", n, video.Errors(), seconds);
    }
    catch (const std::exception& e)
    {
        std::fprintf(stderr, "%s\n", e.what());
        return 1;
    }
    return 0;
}
