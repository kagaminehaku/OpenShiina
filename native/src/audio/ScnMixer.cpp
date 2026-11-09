// C# Audio/ScnMixer.cs (see ScnMixer.h)
#include "audio/ScnMixer.h"

#include <algorithm>

namespace openshiina::audio {

ScnVoice::ScnVoice(std::unique_ptr<SampleSource> source, float gain) : Gain(gain), m_source(std::move(source))
{
    if (m_source->Rate() != ScnMixer::Rate)
        m_resampler = std::make_unique<WdlResampler>(m_source->Rate(), ScnMixer::Rate);
}

int ScnVoice::ReadStereo(float* buffer, int count)
{
    if (m_source->Channels() == 2)
        return m_source->Read(buffer, count);
    // (more channels: the C# player's mixer takes none of them)
    if (m_source->Channels() != 1)
        return 0;
    m_mono.resize(size_t(count / 2));
    int read = m_source->Read(m_mono.data(), count / 2);
    for (int i = 0; i < read; i++)
    {
        buffer[2 * i] = m_mono[size_t(i)] * 1.0f;
        buffer[2 * i + 1] = m_mono[size_t(i)] * 1.0f;
    }
    return read * 2;
}

int ScnVoice::Read(float* buffer, int count)
{
    if (!m_resampler)
        return ReadStereo(buffer, count);
    int frames = count / 2;
    float* in = nullptr;
    int wanted = m_resampler->ResamplePrepare(frames, 2, in);
    int got = ReadStereo(in, wanted * 2) / 2;
    return m_resampler->ResampleOut(buffer, got, frames, 2) * 2;
}

void ScnVoice::Finish()
{
    if (m_ended)
        return;
    m_ended = true;
    m_source.reset();
    m_resampler.reset();
}

bool ScnVoice::MixInto(float* mix, int frames)
{
    if (m_stopped || m_ended)
    {
        Finish();
        return false;
    }
    if (Paused)
        return true;
    int count = frames * 2;
    m_samples.resize(size_t(count));
    int read = Read(m_samples.data(), count);
    float gain = Gain;
    if (gain != 1)
        for (int i = 0; i < read; i++)
            m_samples[size_t(i)] *= gain;
    for (int i = 0; i < read; i++)
        mix[i] += m_samples[size_t(i)];
    if (read < count)
    {
        Finish();
        return false;
    }
    return true;
}

std::shared_ptr<ScnVoice> ScnMixer::Start(std::unique_ptr<SampleSource> source, float gain)
{
    auto voice = std::make_shared<ScnVoice>(std::move(source), gain);
    std::lock_guard lock(m_mutex);
    m_voices.push_back(voice);
    return voice;
}

void ScnMixer::Read(float* output, int frames)
{
    std::fill(output, output + size_t(frames) * 2, 0.0f);
    if (Paused)
        return;
    std::vector<std::shared_ptr<ScnVoice>> voices;
    {
        std::lock_guard lock(m_mutex);
        voices = m_voices;
    }
    bool ended = false;
    for (auto& voice : voices)
        if (!voice->MixInto(output, frames))
            ended = true;
    if (ended)
    {
        std::lock_guard lock(m_mutex);
        std::erase_if(m_voices, [](const auto& v) { return v->Ended(); });
    }
}

void ScnMixer::StopAll()
{
    std::lock_guard lock(m_mutex);
    for (auto& voice : m_voices)
        voice->Stop();
}

} // namespace openshiina::audio
