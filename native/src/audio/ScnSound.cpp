// C# Audio/ScnSound.cs (see ScnSound.h)
#include "audio/ScnSound.h"

#include "audio/ScnMusic.h"

namespace openshiina::audio {

ScnSound::~ScnSound()
{
    for (auto& [id, b] : m_buffers)
        Close(b);
}

int ScnSound::CreateBuffer(const Bytes& file)
{
    int id = m_next++;
    m_buffers[id] = Buffer{file, nullptr, 1, false};
    return id;
}

void ScnSound::Release(int buffer)
{
    if (auto found = m_buffers.find(buffer); found != m_buffers.end())
    {
        Close(found->second);
        m_buffers.erase(found);
    }
}

void ScnSound::Play(int buffer, int flags)
{
    auto found = m_buffers.find(buffer);
    if (found == m_buffers.end())
        return;
    Buffer& b = found->second;
    Close(b);
    try
    {
        b.Looping = (flags & 1) != 0;
        b.Voice = m_mixer.Start(OpenSource(b.File, b.Looping), b.Gain);
    }
    catch (const std::exception&)
    {
        // A sound that cannot be played stays silent, as a failed DirectSound buffer does
        Close(b);
    }
}

void ScnSound::Stop(int buffer)
{
    if (auto found = m_buffers.find(buffer); found != m_buffers.end())
        Close(found->second);
}

void ScnSound::SetVolume(int buffer, int volume)
{
    auto found = m_buffers.find(buffer);
    if (found == m_buffers.end())
        return;
    found->second.Gain = VolumeGain(volume);
    if (found->second.Voice)
        found->second.Voice->Gain = found->second.Gain;
}

int ScnSound::Status(int buffer)
{
    auto found = m_buffers.find(buffer);
    if (found == m_buffers.end() || !found->second.Voice || found->second.Voice->Ended())
        return 0;
    return 1 | (found->second.Looping ? 4 : 0);
}

void ScnSound::Close(Buffer& b)
{
    if (b.Voice)
        b.Voice->Stop();
    b.Voice = nullptr;
}

} // namespace openshiina::audio
