// C# Audio/ScnMixer.cs (see ScnMixer.h)
#include "audio/ScnMixer.h"

#include <algorithm>

namespace openshiina::audio {

ScnVoice::ScnVoice(std::unique_ptr<SampleSource> source, float gain) : Gain(gain), m_source(std::move(source))
{
    m_step = double(m_source->Rate()) / ScnMixer::Rate;
    m_buffer.resize(size_t(1024) * size_t(std::max(1, m_source->Channels())));
}

bool ScnVoice::NextFrame(float& left, float& right)
{
    int channels = m_source->Channels();
    if (m_at >= m_buffered)
    {
        if (m_sourceEnded)
            return false;
        int want = int(m_buffer.size()) / channels * channels;
        m_buffered = m_source->Read(m_buffer.data(), want);
        m_at = 0;
        if (m_buffered < want)
            m_sourceEnded = true;
        if (m_buffered <= 0)
            return false;
    }
    left = m_buffer[size_t(m_at)];
    right = channels >= 2 ? m_buffer[size_t(m_at) + 1] : left;
    m_at += channels;
    return true;
}

void ScnVoice::Finish()
{
    if (m_ended)
        return;
    m_ended = true;
    m_source.reset();
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
    if (!m_started)
    {
        m_started = true;
        if (!NextFrame(m_l0, m_r0))
        {
            Finish();
            return false;
        }
        if (!NextFrame(m_l1, m_r1))
        {
            m_l1 = m_l0;
            m_r1 = m_r0;
        }
    }
    float gain = Gain;
    bool exact = m_source->Rate() == ScnMixer::Rate;
    for (int i = 0; i < frames; i++)
    {
        float left, right;
        if (exact)
        {
            left = m_l0;
            right = m_r0;
        }
        else
        {
            float t = float(m_position);
            left = m_l0 + (m_l1 - m_l0) * t;
            right = m_r0 + (m_r1 - m_r0) * t;
        }
        mix[2 * i] += left * gain;
        mix[2 * i + 1] += right * gain;
        m_position += exact ? 1.0 : m_step;
        while (m_position >= 1)
        {
            m_position -= 1;
            m_l0 = m_l1;
            m_r0 = m_r1;
            // the end of the source: the voice ends after this frame
            if (!NextFrame(m_l1, m_r1))
            {
                Finish();
                return false;
            }
        }
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
