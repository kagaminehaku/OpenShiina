// C# Scripting/ScnVm.Sound.cs: sound effects - DirectSound buffers in a table of 0x101 (0x7DAC20).
// A file is loaded and turned into a RIFF WAVE in memory first (FUN_004059D0 / 00405900: PAD, OggS
// and OGV are decoded, WAVE is kept), then a buffer is made of it (FUN_004047A0). A value above
// 0xFFFF in place of a table index is a buffer itself. The platform plays the buffers (IScnSound);
// without one the buffers only keep their state, and they never report themselves playing.
//
// Not here yet: the decoders of PAD, Ogg Vorbis and OGV (C# Formats/AudioDecoder.cs). With a
// platform's sound a RIFF WAVE is played and the others are left silent.
#include "scripting/ScnVm.h"

#include <algorithm>

namespace openshiina::scripting {

std::optional<openshiina::Bytes> ScnVm::SoundToWave(const openshiina::Bytes& data)
{
    // FUN_00405900: the file as a RIFF WAVE, or none when it is none of the known kinds
    if (data.size() < 12)
        return std::nullopt;
    uint32_t magic = LittleEndian::ToUInt32(data.data());
    if (magic == 0x46464952 && LittleEndian::ToUInt32(&data[8]) == 0x45564157)     // RIFF....WAVE
        return data;
    return std::nullopt;
}

int ScnVm::OggLength(int address)
{
    // The length of an Ogg stream in memory: its pages up to the one that ends the stream (header
    // type bit 2), or the last "OggS"
    int at = address;
    while (Read32(at) == 0x5367674F && at - address < 0x4000000)
    {
        int type = ReadByte(at + 5), segments = ReadByte(at + 26);
        int size = 27 + segments;
        for (int k = 0; k < segments; k++)
            size += ReadByte(at + 27 + k);
        at += size;
        if ((type & 4) != 0)
            break;
    }
    return at - address;
}

std::optional<openshiina::Bytes> ScnVm::ToWave(const openshiina::Bytes& data)
{
    // SoundToWave; with no platform sound only the kind of file is checked
    if (Sound())
        return SoundToWave(data);
    if (data.size() < 12)
        return std::nullopt;
    uint32_t magic = LittleEndian::ToUInt32(data.data());
    if (magic == 0x46464952 || magic == 0x5367674F || (magic & 0xFFFFFF) == 0x444150 || (magic & 0xFFFFFF) == 0x56474F)
        return data;
    return std::nullopt;
}

int ScnVm::SoundBuffer(int value) const
{
    // A buffer by table index, or the value itself when it is a buffer (above 0xFFFF)
    return uint32_t(value) > 0xFFFF ? value : value < SoundSlots ? m_sounds[size_t(value)] : 0;
}

int ScnVm::MakeSoundOrSilence(int slot, const std::optional<openshiina::Bytes>& wave)
{
    // MakeSound; a sound that does not decode leaves the slot empty instead of stopping the scripts
    if (uint32_t(slot) >= SoundSlots)
        return 2;
    if (wave)
        return MakeSound(slot, *wave) ? 0 : 2;
    if (m_sounds[size_t(slot)] != 0 && Sound())
        Sound()->Release(m_sounds[size_t(slot)]);
    m_sounds[size_t(slot)] = 0;
    return 0;
}

bool ScnVm::MakeSound(int slot, const openshiina::Bytes& wave)
{
    if (uint32_t(slot) >= SoundSlots)
        return false;
    if (m_sounds[size_t(slot)] != 0 && Sound())
        Sound()->Release(m_sounds[size_t(slot)]);
    int buffer = Sound() ? Sound()->CreateBuffer(wave) : m_nextSound++;
    m_sounds[size_t(slot)] = buffer;
    return buffer != 0;
}

void ScnVm::RegisterSound()
{
    // 06A5: release every buffer
    Register(0x06A5, [](ScnVm& vm, ScnContext&, const ScnInstruction&) {
        for (int k = 0; k < SoundSlots; k++)
            if (vm.m_sounds[size_t(k)] != 0)
            {
                if (vm.Sound())
                    vm.Sound()->Release(vm.m_sounds[size_t(k)]);
                vm.m_sounds[size_t(k)] = 0;
            }
        return 0;
    });
    // 06A6 n, file: buffer n of a sound file
    Register(0x06A6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        std::string file = vm.ReadString(vm.Value(c, i.Args[1]));
        openshiina::Bytes data;
        if (!vm.ReadScriptFile(file, data))
            return 2;
        return vm.MakeSoundOrSilence(slot, vm.ToWave(data));
    });
    // 06B1 n, address: buffer n of a sound in memory (an Ogg stream ends at its last page, whatever
    // was loaded last)
    Register(0x06B1, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int slot = vm.Value(c, i.Args[0]);
        int address = vm.Value(c, i.Args[1]);
        int length = vm.Read32(address) == 0x5367674F ? vm.OggLength(address) : vm.EngineGlobal(0x4C4514);
        auto data = vm.ReadBytes(address, std::max(length, 12));
        return vm.MakeSoundOrSilence(slot, vm.ToWave(data));
    });
    // 06A7 n, flags: play from the start (1 = loop)
    Register(0x06A7, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
        int flags = vm.Value(c, i.Args[1]);
        if (buffer != 0 && vm.Sound())
            vm.Sound()->Play(buffer, flags);
        return 0;
    });
    // 06A8 n: stop
    Register(0x06A8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
        if (buffer != 0 && vm.Sound())
            vm.Sound()->Stop(buffer);
        return 0;
    });
    // 06A9 n, volume: in 1/100 dB
    Register(0x06A9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
        int volume = vm.Value(c, i.Args[1]);
        if (buffer != 0)
        {
            if (vm.Sound())
                vm.Sound()->SetVolume(buffer, volume);
            vm.m_soundVolumes[buffer] = volume;
        }
        return 0;
    });
    // 06AC n, v: v = the buffer's volume (1/100 dB: 0 until 06A9 sets it), or -1 for no buffer
    Register(0x06AC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
        auto volume = vm.m_soundVolumes.find(buffer);
        vm.Store(c, i.Args[1], buffer == 0 ? -1 : volume == vm.m_soundVolumes.end() ? 0 : volume->second);
        return 0;
    });
    // 06AF n, v: v = DSBSTATUS, or -1 for no buffer
    Register(0x06AF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
        vm.Store(c, i.Args[1], buffer == 0 ? -1 : vm.Sound() ? vm.Sound()->Status(buffer) : 0);
        return 0;
    });
    // 06B0 n: release buffer n
    Register(0x06B0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int value = vm.Value(c, i.Args[0]);
        if (uint32_t(value) > 0xFFFF)
        {
            if (vm.Sound())
                vm.Sound()->Release(value);
            return 0;
        }
        if (value < SoundSlots && vm.m_sounds[size_t(value)] != 0)
        {
            if (vm.Sound())
                vm.Sound()->Release(vm.m_sounds[size_t(value)]);
            vm.m_sounds[size_t(value)] = 0;
        }
        return 0;
    });
}

} // namespace openshiina::scripting
