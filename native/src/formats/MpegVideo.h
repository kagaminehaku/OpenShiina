// C# Formats/MpegVideo.cs and MovieVideo.cs: MPEG-1 movies (ISO/IEC 11172), the format of the
// movies of the GRAND†CROSS games and their kin (mv\*.mpg: MPEG-1 system streams, 800 x 600 or
// 1280 x 720 at 30 frames a second, some with MPEG-1 Layer II sound). The engine plays them through
// DirectShow into a DirectDraw surface; this is a decoder of the player's own:
//   MpegSystemStream - splits a system stream into its first video and first audio stream
//   Mpeg1Video       - decodes the video stream into YCbCr frames in display order
#pragma once

#include <cstdint>
#include <memory>
#include <vector>

#include "io/Binary.h"

namespace openshiina::formats {

/// The elementary streams of an MPEG-1 (or MPEG-2 program) system stream. The video stream has
/// Mpeg1Video::Padding zero bytes after it, which Mpeg1Video takes as it is.
class MpegSystemStream
{
public:
    explicit MpegSystemStream(const Bytes& file);

    /// The first video stream (0xE0-0xEF) followed by Padding zeros, and its length.
    Bytes VideoPadded;
    int VideoLength = 0;
    /// The first audio stream (0xC0-0xDF), or empty.
    Bytes Audio;
};

/// A decoded picture: Y at full size, Cb and Cr at half size, macroblock-aligned planes.
class MpegFrame
{
public:
    MpegFrame(int width, int height, int mbWidth, int mbHeight);

    int Width, Height, Stride, ChromaStride;
    Bytes Y, Cb, Cr;

    /// The picture as 24-bit BGR (ITU-R BT.601, video range, each chroma sample covering 2 x 2
    /// pixels), width x height pixels from the top left, rows pitch bytes apart.
    void ToBgr24(uint8_t* dst, int pitch, int width, int height) const;
};

/// The picture of a movie: frames in display order (Mpeg1Video; the C# player also has Vc1Video).
class IMovieVideo
{
public:
    virtual ~IMovieVideo() = default;
    virtual int Width() const = 0;
    virtual int Height() const = 0;
    /// Frames a second.
    virtual double FrameRate() const = 0;
    /// The next frame in display order, or null at the end; it stays valid until the next call.
    virtual const MpegFrame* Next() = 0;
    /// Back to the first frame.
    virtual void Rewind() = 0;
};

/// An MPEG-1 video stream decoder (ISO/IEC 11172-2); frames come out in display order.
class Mpeg1Video final : public IMovieVideo
{
public:
    /// Bytes of zeros after the stream, so that peeking past its end reads zeros.
    static constexpr int Padding = 8;

    /// A video stream alone (copied, with the padding after it).
    explicit Mpeg1Video(const Bytes& stream);
    /// The video stream of a system stream (taken from it).
    explicit Mpeg1Video(MpegSystemStream&& system);

    int Width() const override { return m_width; }
    int Height() const override { return m_height; }
    double FrameRate() const override { return m_frameRate; }
    const MpegFrame* Next() override;
    void Rewind() override;

    /// Slices that did not decode cleanly so far.
    int Errors() const { return m_errors; }

private:
    friend class SliceDecoder;

    Mpeg1Video(Bytes data, int length);
    int Peek(int count) const;
    int Read(int count);
    bool ReadBit() { return Read(1) != 0; }
    bool NextStartCode(int code);
    void ReadSequenceHeader();
    std::unique_ptr<MpegFrame> NewFrame() const;
    const MpegFrame* DecodePicture();

    Bytes m_data;
    int m_bit = 0, m_end = 0;
    int m_width = 0, m_height = 0;
    double m_frameRate = 30;
    int m_errors = 0;
    int m_mbWidth = 0, m_mbHeight = 0;
    int m_intraMatrix[64] = {}, m_nonIntraMatrix[64] = {};
    std::vector<std::pair<int, int>> m_slices;

    // Anchors (I / P pictures): the older one and the newer one; B pictures go to a third frame
    std::unique_ptr<MpegFrame> m_anchor0, m_anchor1, m_bFrame;
    MpegFrame *m_older = nullptr, *m_newer = nullptr;
    bool m_flushed = false;

    // The picture being decoded
    int m_pictureType = 0;
    bool m_fullPelForward = false, m_fullPelBackward = false;
    int m_forwardRSize = 0, m_backwardRSize = 0;
    MpegFrame *m_current = nullptr, *m_forwardRef = nullptr, *m_backwardRef = nullptr;
};

} // namespace openshiina::formats
