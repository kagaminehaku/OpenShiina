// C# Audio/ScnMixer.cs: one mix of every sound of the scripts. The sound buffers (ScnSound) and the
// music streams (ScnMusic) are inputs of a mixer at 44.1 kHz stereo, float; starting a sound only
// adds an input. The platform plays Read's samples on its sound device (the Switch player: SDL),
// calling it on its own thread.
//
// The C# player's mixer is NAudio's (MixingSampleProvider, MonoToStereoSampleProvider,
// WdlResamplingSampleProvider), and so is this one's: a mono sound made stereo, then one of another
// rate resampled by WDL's resampler (WdlResampler), gain applied, the voices added.
#pragma once

#include <atomic>
#include <memory>
#include <mutex>
#include <vector>

#include "audio/WdlResampler.h"

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
    /// Up to count samples (whole stereo frames) of the source as stereo (MonoToStereoSampleProvider).
    int ReadStereo(float* buffer, int count);
    /// Up to count samples at 44.1 kHz stereo (WdlResamplingSampleProvider for another rate).
    int Read(float* buffer, int count);
    void Finish();

    std::unique_ptr<SampleSource> m_source;
    std::unique_ptr<WdlResampler> m_resampler;
    std::atomic<bool> m_stopped{false}, m_ended{false};
    std::vector<float> m_mono, m_samples;
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
