// C# Audio/ScnMusic.cs: the engine's music streams (IScnMusic). Ogg Vorbis (and the OGV wrapper) is
// decoded as it plays with stb_vorbis (the C# player: NVorbis), PAD and WAVE read as PCM; each
// stream is a voice of the shared mixer.
#pragma once

#include <memory>
#include <unordered_map>

#include "audio/ScnMixer.h"
#include "scripting/ScnTypes.h"

namespace openshiina::audio {

/// A file played as it is decoded (Ogg Vorbis, OGV, PAD or a RIFF WAVE), from the start again at its
/// end when it loops: music and the sound buffers (ScnSound). Throws InvalidDataError for a file it
/// cannot play.
std::unique_ptr<SampleSource> OpenSource(const Bytes& file, bool loop);

/// The gain of a DirectSound volume (hundredths of a decibel, -10000 silent to 0).
float VolumeGain(int volume);

class ScnMusic final : public scripting::IScnMusic
{
public:
    explicit ScnMusic(ScnMixer& mixer) : m_mixer(mixer) {}
    ~ScnMusic() override;

    int Open(const Bytes& file) override;
    void Close(int stream) override;
    void Play(int stream, bool loop) override;
    void Stop(int stream) override;
    void Pause(int stream) override;
    void Resume(int stream) override;
    void SetVolume(int stream, int volume) override;
    bool IsPlaying(int stream) override;

private:
    struct Stream
    {
        Bytes File;
        std::shared_ptr<ScnVoice> Voice;
        float Gain = 1;
    };
    static void Halt(Stream& s);

    ScnMixer& m_mixer;
    std::unordered_map<int, Stream> m_streams;
    int m_next = 1;
};

} // namespace openshiina::audio
