// moviecheck <movie> [frames] [--fast | --sound]: decodes an MPEG-1 movie (a system stream, or a video stream
// alone) with formats/MpegVideo, or a Windows Media one (ASF) with formats/Vc1Video, and prints, for each frame in display order, the SHA-256 of its
// picture as 24-bit BGR, and the frames' count and the decoding errors at the end. The C# player's
// decoder gives the same lines for the same movie (Formats/MpegVideo.cs, Vc1Video.cs): compare them. The times
// of the decoding and of the BGR pictures are given apart from that of the hashes.
//
// --sound: the sound of a Windows Media movie instead (formats/AsfAudio): its format, the SHA-256 of
// its 16-bit samples and their count; the C# player's AsfAudio.StartWave gives the same.
//
// --fast: the fast inverse DCT (the Switch's) against the C# player's, frame by frame: the largest
// difference of a byte, how many bytes differ, and the PSNR of each frame (the lowest and the mean).
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <filesystem>
#include <memory>
#include <string>
#include <vector>

#include "formats/AsfAudio.h"
#include "formats/AsfFile.h"
#include "formats/MpegVideo.h"
#include "formats/Vc1Video.h"
#include "platform/Files.h"
#include "util/Encoding.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#endif

using namespace openshiina;

namespace {

std::unique_ptr<formats::Mpeg1Video> Open(const Bytes& data)
{
    bool alone = data.size() >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0xB3;
    return alone ? std::make_unique<formats::Mpeg1Video>(data) : std::make_unique<formats::Mpeg1Video>(formats::MpegSystemStream(data));
}

int CompareFast(const Bytes& data, int limit)
{
    auto exact = Open(data), fast = Open(data);
    int w = exact->Width(), h = exact->Height();
    Bytes a(size_t(w) * 3 * size_t(h)), b(a.size());
    double fastSeconds = 0, lowest = 1e9, sum = 0;
    int largest = 0, n = 0;
    int64_t differing = 0;
    for (; n < limit; n++)
    {
        formats::Mpeg1Video::ExactIdct = true;
        const formats::MpegFrame* fa = exact->Next();
        auto before = std::chrono::steady_clock::now();
        formats::Mpeg1Video::ExactIdct = false;
        const formats::MpegFrame* fb = fast->Next();
        fastSeconds += std::chrono::duration<double>(std::chrono::steady_clock::now() - before).count();
        if (fa == nullptr || fb == nullptr)
        {
            if ((fa == nullptr) != (fb == nullptr))
                std::printf("frame %d: one decoder ended before the other\n", n);
            break;
        }
        fa->ToBgr24(a.data(), w * 3, w, h);
        fb->ToBgr24(b.data(), w * 3, w, h);
        double squares = 0;
        for (size_t i = 0; i < a.size(); i++)
        {
            int d = std::abs(int(a[i]) - int(b[i]));
            if (d != 0)
            {
                differing++;
                largest = std::max(largest, d);
                squares += double(d) * d;
            }
        }
        double psnr = squares == 0 ? 99 : 10 * std::log10(255.0 * 255.0 * double(a.size()) / squares);
        lowest = std::min(lowest, psnr);
        sum += psnr;
    }
    std::printf("%d frames: largest difference %d, %.4f%% of the bytes differ, PSNR lowest %.1f dB, mean %.1f dB (99: the same); fast decoding %.2f s\n",
                n, largest, n > 0 ? 100.0 * double(differing) / (double(a.size()) * n) : 0.0, n > 0 ? lowest : 0.0, n > 0 ? sum / n : 0.0, fastSeconds);
    return 0;
}

/// A movie of either kind, the ASF file kept while its video stream is read.
struct AnyMovie
{
    std::unique_ptr<formats::AsfFile> Asf;
    std::unique_ptr<formats::IMovieVideo> Video;
    int Errors() const
    {
        if (auto* vc1 = dynamic_cast<formats::Vc1Video*>(Video.get()))
            return vc1->Errors();
        return static_cast<formats::Mpeg1Video*>(Video.get())->Errors();
    }
};

AnyMovie OpenAny(const Bytes& data)
{
    AnyMovie movie;
    if (formats::AsfFile::IsAsf(data))
    {
        movie.Asf = std::make_unique<formats::AsfFile>(data);
        if (!movie.Asf->Video)
            throw InvalidDataError("No video stream");
        movie.Video = std::make_unique<formats::Vc1Video>(*movie.Asf->Video);
    }
    else
        movie.Video = Open(data);
    return movie;
}

int CheckSound(const Bytes& data)
{
    formats::AsfFile file(data);
    if (!file.Audio)
    {
        std::printf("no sound\n");
        return 0;
    }
    std::printf("sound: format %X, %d channels, %d Hz, block %d\n", file.Audio->FormatTag, file.Audio->Channels, file.Audio->SampleRate, file.Audio->BlockAlign);
    Bytes packed = formats::AsfAudio::Pack(file);
    if (packed.empty())
    {
        std::printf("not decoded\n");
        return 0;
    }
    auto start = std::chrono::steady_clock::now();
    formats::AsfAudio::Reader reader(packed);
    std::vector<float> buffer(4096);
    Bytes pcm;
    int n;
    while ((n = reader.Read(buffer.data(), int(buffer.size()))) > 0)
        for (int i = 0; i < n; i++)
        {
            int16_t s = int16_t(std::lround(buffer[size_t(i)] * 32768.0f));
            pcm.push_back(uint8_t(s));
            pcm.push_back(uint8_t(uint16_t(s) >> 8));
        }
    double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
    std::printf("%d channels, %d Hz, %zu samples, %s, %.2f s\n", reader.Channels(), reader.Rate(), pcm.size() / 2,
                Encoding::Sha256::HexOf(pcm.data(), pcm.size()).c_str(), seconds);
    return 0;
}

} // namespace

int Run(int argc, std::string* argv)
{
    if (argc < 2)
    {
        std::fprintf(stderr, "moviecheck <movie> [frames] [--fast | --sound]\n");
        return 2;
    }
    bool compareFast = false, sound = false;
    int limit = 1 << 30;
    for (int k = 2; k < argc; k++)
        if (argv[k] == "--fast")
            compareFast = true;
        else if (argv[k] == "--sound")
            sound = true;
        else
            limit = std::atoi(argv[k].c_str());
    Bytes data;
    if (!Files::ReadAll(argv[1], data))
    {
        std::fprintf(stderr, "Cannot read %s\n", argv[1].c_str());
        return 1;
    }
    try
    {
        if (compareFast)
            return CompareFast(data, limit);
        if (sound)
            return CheckSound(data);
        AnyMovie movie = OpenAny(data);
        formats::IMovieVideo* video = movie.Video.get();
        std::printf("%dx%d, %.3f frames a second\n", video->Width(), video->Height(), video->FrameRate());
        Bytes bgr(size_t(video->Width()) * 3 * size_t(video->Height()));
        auto start = std::chrono::steady_clock::now();
        double decoding = 0, pictures = 0;
        int n = 0;
        for (; n < limit; n++)
        {
            auto before = std::chrono::steady_clock::now();
            const formats::MpegFrame* frame = video->Next();
            if (frame == nullptr)
                break;
            auto decoded = std::chrono::steady_clock::now();
            frame->ToBgr24(bgr.data(), video->Width() * 3, video->Width(), video->Height());
            decoding += std::chrono::duration<double>(decoded - before).count();
            pictures += std::chrono::duration<double>(std::chrono::steady_clock::now() - decoded).count();
            std::printf("%d %s\n", n, Encoding::Sha256::HexOf(bgr.data(), bgr.size()).c_str());
        }
        double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        std::printf("%d frames, %d errors, %.2f s (%.2f s decoding, %.2f s making the pictures)\n", n, movie.Errors(), seconds, decoding, pictures);
    }
    catch (const std::exception& e)
    {
        std::fprintf(stderr, "%s\n", e.what());
        return 1;
    }
    return 0;
}

// The arguments as UTF-8: on Windows from the UTF-16 command line (main's are in the ANSI code
// page, which has no Japanese on most PCs)
#ifdef _WIN32
int wmain(int argc, wchar_t** argv)
{
    SetConsoleOutputCP(CP_UTF8);
    std::vector<std::string> args;
    for (int i = 0; i < argc; i++)
        args.push_back(Files::ToUtf8(std::filesystem::path(argv[i])));
    return Run(argc, args.data());
}
#else
int main(int argc, char** argv)
{
    std::vector<std::string> args(argv, argv + argc);
    return Run(argc, args.data());
}
#endif
