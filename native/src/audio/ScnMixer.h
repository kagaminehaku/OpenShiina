// C# Audio/ScnMixer.cs: one mix of every sound of the scripts. The sound buffers (ScnSound) and the
// music streams (ScnMusic) are inputs of a mixer at 44.1 kHz stereo, float; starting a sound only
// adds an input. The platform plays Read's samples on its sound device (the Switch player: SDL),
// calling it on its own thread.
//
// The C# player's mixer is NAudio's (MixingSampleProvider, MonoToStereoSampleProvider,
// WdlResamplingSampleProvider); here a sound of another rate is resampled linearly.
#pragma once

#include <atomic>
#include <memory>
#include <mutex>
#include <vector>

namespace openshiina::audio {

/// Samples of a sound: interleaved floats of Channels() channels at Rate() a second.
class SampleSource
{
public:
    virtual ~SampleSource() = default;
    virtual int Channels() const = 0;
    virtual int Rate() const = 0;
    /// Up to count samples (whole frames) into buffer; fewer only at the end.
    virtual int Read(float* buffer, int count) = 0;
};

/// A sound playing in the mixer: volume, pause and stop from any thread.
class ScnVoice
{
public:
    ScnVoice(std::unique_ptr<SampleSource> source, float gain);

    std::atomic<float> Gain;
    std::atomic<bool> Paused{false};

    /// Finished: played to its end, or stopped.
    bool Ended() const { return m_ended; }

    /// Ends the voice; its source is let go on the mixer's thread.
    void Stop() { m_stopped = true; }

    /// Adds up to frames stereo frames at 44.1 kHz into mix; false once the voice has ended.
    bool MixInto(float* mix, int frames);

private:
    /// The next frame of the source as stereo; false at its end.
    bool NextFrame(float& left, float& right);
    void Finish();

    std::unique_ptr<SampleSource> m_source;
    std::atomic<bool> m_stopped{false}, m_ended{false};
    std::vector<float> m_buffer;
    int m_buffered = 0, m_at = 0;
    // the linear resampling: the two source frames around the position, and the position in between
    float m_l0 = 0, m_r0 = 0, m_l1 = 0, m_r1 = 0;
    double m_position = 0, m_step = 1;
    bool m_started = false, m_sourceEnded = false;
};

class ScnMixer
{
public:
    static constexpr int Rate = 44100;

    /// Every voice stands still (silence out) and goes on where it was when this is cleared.
    std::atomic<bool> Paused{false};

    /// Plays samples (any rate, mono or stereo); the voice ends at their end or when stopped.
    std::shared_ptr<ScnVoice> Start(std::unique_ptr<SampleSource> source, float gain);

    /// The mix: frames stereo frames (2 * frames floats), silence when nothing plays.
    void Read(float* output, int frames);

    void StopAll();

private:
    std::mutex m_mutex;
    std::vector<std::shared_ptr<ScnVoice>> m_voices;
};

} // namespace openshiina::audio
