// C# Audio/ScnSound.cs: the engine's DirectSound buffers (IScnSound). A buffer the scripts start is a
// voice of the shared mixer (ScnMixer), decoded as it plays (OpenSource: an Ogg Vorbis sound is not
// decoded up front). Volumes are DirectSound's hundredths of a decibel.
#pragma once

#include <memory>
#include <unordered_map>

#include "audio/ScnMixer.h"
#include "scripting/ScnTypes.h"

namespace openshiina::audio {

class ScnSound final : public scripting::IScnSound
{
public:
    explicit ScnSound(ScnMixer& mixer) : m_mixer(mixer) {}
    ~ScnSound() override;

    int CreateBuffer(const Bytes& file) override;
    void Release(int buffer) override;
    void Play(int buffer, int flags) override;
    void Stop(int buffer) override;
    void SetVolume(int buffer, int volume) override;
    int Status(int buffer) override;

private:
    struct Buffer
    {
        Bytes File;
        std::shared_ptr<ScnVoice> Voice;
        float Gain = 1;
        bool Looping = false;
    };
    static void Close(Buffer& b);

    ScnMixer& m_mixer;
    std::unordered_map<int, Buffer> m_buffers;
    int m_next = 0x10000;
};

} // namespace openshiina::audio
