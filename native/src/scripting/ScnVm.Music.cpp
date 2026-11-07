// C# Scripting/ScnVm.Music.cs: music streams (06D6 open ... 06F0 length), a table of 256 handles,
// each stream's status, volume, loop points and 06EC's fades stepped before each frame. The
// platform plays them (IScnMusic); the lengths and sample rates the scripts read come from the
// files' headers.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <cstring>

namespace openshiina::scripting {

namespace {

// 0x4A15E0: volume 0-100 -> DirectSound volume
constexpr int MusicVolumes[101] = {
    -10000, -6643, -5643, -5058, -4643, -4321, -4058, -3836, -3643, -3473, -3321, -3184, -3058, -2943, -2836, -2736, -2643,
    -2556,  -2473, -2395, -2321, -2251, -2184, -2120, -2058, -2000, -1943, -1888, -1836, -1785, -1736, -1689, -1643, -1599,
    -1556,  -1514, -1473, -1434, -1395, -1358, -1321, -1286, -1251, -1217, -1184, -1152, -1120, -1089, -1058, -1029, -1000,
    -971,   -943,  -915,  -888,  -862,  -836,  -810,  -785,  -761,  -736,  -713,  -689,  -666,  -643,  -621,  -599,  -577,
    -556,   -535,  -514,  -494,  -473,  -454,  -434,  -415,  -395,  -377,  -358,  -340,  -321,  -304,  -286,  -268,  -251,
    -234,   -217,  -200,  -184,  -168,  -152,  -136,  -120,  -104,  -89,   -74,   -58,   -43,   -29,   -14,   0,
};

} // namespace

int MusicVolume(int volume) { return MusicVolumes[std::clamp(volume, 0, 100)]; }

namespace {

bool IsMusicFile(const openshiina::Bytes& data)
{
    uint32_t magic = LittleEndian::ToUInt32(data.data());
    return magic == 0x46464952 || magic == 0x5367674F || (magic & 0xFFFFFF) == 0x444150 || (magic & 0xFFFFFF) == 0x56474F;
}

/// A music file's sample rate: a RIFF WAVE's "fmt " chunk, an Ogg stream's Vorbis identification
/// header; 44100 for the others (OGV, PAD).
int MusicSampleRate(const openshiina::Bytes& data)
{
    uint32_t magic = LittleEndian::ToUInt32(data.data());
    if (magic == 0x46464952)
    {
        for (size_t p = 12; p + 16 <= data.size();)
        {
            int size = int(LittleEndian::ToUInt32(&data[p + 4]));
            if (std::memcmp(&data[p], "fmt ", 4) == 0)
                return int(LittleEndian::ToUInt32(&data[p + 12]));
            if (size < 0)
                break;
            p += 8 + size_t(size) + size_t(size & 1);
        }
    }
    else if (magic == 0x5367674F)
    {
        static const uint8_t vorbis[] = {1, 'v', 'o', 'r', 'b', 'i', 's'};
        auto end = data.begin() + std::min<ptrdiff_t>(ptrdiff_t(data.size()), 4096);
        auto found = std::search(data.begin(), end, std::begin(vorbis), std::end(vorbis));
        size_t at = size_t(found - data.begin());
        if (found != end && at + 16 <= data.size())
            return int(LittleEndian::ToUInt32(&data[at + 12]));
    }
    return 44100;
}

/// A compressed music file's length in ms as the engine has it (06F0: its PCM bytes * 1000 / its
/// bytes a second, truncated): an OGV's from its header, an Ogg stream's from the granule position
/// of its last page and its sample rate; 0 for others.
int MusicLengthMs(const openshiina::Bytes& data)
{
    uint32_t magic = LittleEndian::ToUInt32(data.data());
    if ((magic & 0xFFFFFF) == 0x56474F && data.size() >= 44 && std::memcmp(&data[12], "fmt ", 4) == 0)
    {
        int perSecond = int(LittleEndian::ToUInt32(&data[28])), bytes = int(LittleEndian::ToUInt32(&data[40]));
        return perSecond > 0 ? int(bytes * 1000.0 / perSecond) : 0;
    }
    if (magic != 0x5367674F)
        return 0;
    int rate = MusicSampleRate(data);
    static const uint8_t ogg[] = {'O', 'g', 'g', 'S'};
    auto found = std::find_end(data.begin(), data.end(), std::begin(ogg), std::end(ogg));
    if (found == data.end() || size_t(found - data.begin()) + 14 > data.size() || rate <= 0)
        return 0;
    int64_t samples = int64_t(LittleEndian::ToUInt64(&*(found + 6)));
    return samples > 0 ? int(samples * 1000.0 / rate) : 0;
}

} // namespace

void ScnVm::FadeTo(MusicStream* s, int ms, int delta, int target)
{
    // 06EC's fade: the volume goes by delta (0: -1) every ms (counted in 5 ms ticks) until it
    // reaches target (0-100); going down to a target without bit 31 the stream stops there. Nothing
    // for a stream not playing or already at the target
    if (!s || (s->Status & 1) == 0 || s->Volume == (target & 0x7FFFFFFF))
        return;
    s->FadeStepMs = std::max(int(uint32_t(ms) / 5), 1) * 5;
    s->FadeDelta = delta == 0 ? -1 : delta;
    s->FadeTarget = target & 0x7FFFFFFF;
    s->FadeStopAtEnd = target >= 0;
    s->FadeOld = false;
    s->FadeDue = Clock() + uint32_t(s->FadeStepMs);
}

void ScnVm::StepMusicFades()
{
    // The steps of 06EC's fades due by now (before each frame)
    m_music.ForEach([&](int, MusicStream& s) {
        for (int steps = 0; s.FadeStepMs > 0 && Clock() - s.FadeDue < 0x80000000u && steps < 1000; steps++)
        {
            s.FadeDue += uint32_t(s.FadeStepMs);
            // v2.33's timer counts no ticks while the stream is paused
            if (s.FadeOld && (s.Status & 4) != 0)
                continue;
            s.Volume += s.FadeDelta;
            if (s.FadeOld)
            {
                if (s.Volume > 100)
                {
                    s.Volume = 100;
                    s.FadeStepMs = 0;
                }
                else if (s.Volume < 0)
                {
                    s.FadeStepMs = 0;
                    if (s.Handle != 0 && Music())
                        Music()->Stop(s.Handle);
                    s.Status = 0;
                    continue;
                }
                if (s.Handle != 0 && Music())
                    Music()->SetVolume(s.Handle, MusicVolume(s.Volume));
                continue;
            }
            bool reached = s.FadeDelta > 0 ? s.Volume >= s.FadeTarget : s.Volume <= s.FadeTarget;
            if (reached)
            {
                s.Volume = s.FadeTarget;
                s.FadeStepMs = 0;
                if (s.FadeDelta < 0 && s.FadeStopAtEnd)
                {
                    if (s.Handle != 0 && Music())
                        Music()->Stop(s.Handle);
                    s.Status = 0;
                }
            }
            if (s.Handle != 0 && Music())
                Music()->SetVolume(s.Handle, MusicVolume(s.Volume));
        }
    });
}

int ScnVm::MusicLength(int address)
{
    // The length of the music file at address from its header: a RIFF's size, an OGV's, or an Ogg
    // stream's pages up to the one that ends it; 0 for anything else
    uint32_t magic = uint32_t(Read32(address));
    if (magic == 0x46464952)
        return Read32(address + 4) + 8;
    // OGV: "OGV", the PCM's length + 52, the file's - 52
    if ((magic & 0xFFFFFF) == 0x56474F)
        return Read32(address + 8) + 52;
    if (magic != 0x5367674F)
        return 0;
    int at = address;
    for (int pages = 0; pages < 1 << 20 && uint32_t(Read32(at)) == 0x5367674F; pages++)
    {
        // The page header (27 bytes), its segment table, then the segments
        int type = ReadByte(at + 5), segments = ReadByte(at + 26), length = 27 + segments;
        for (int k = 0; k < segments; k++)
            length += ReadByte(at + 27 + k);
        at += length;
        if ((type & 4) != 0)
            return at - address;
    }
    return at - address;
}

void ScnVm::RegisterMusic()
{
    // 06D6 source, flags, v: open a stream (flag 1: source is a file in memory, else a file name);
    // v = the stream
    Register(0x06D6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int source = vm.Value(c, i.Args[0]), flags = vm.Value(c, i.Args[1]);
        openshiina::Bytes data;
        bool found;
        if ((flags & 1) != 0)
        {
            // A file loaded there, or one copied: the length from its header
            int size = vm.LoadedFileSize(source);
            if (size <= 0)
                size = vm.MusicLength(source);
            found = size > 0;
            if (found)
                data = vm.ReadBytes(source, size);
        }
        else
            found = vm.ReadScriptFile(vm.ReadString(source), data);
        if (!found || data.size() < 12 || !IsMusicFile(data))
            return 2;
        auto slot = std::find(vm.m_musicTable.begin(), vm.m_musicTable.end(), 0);
        int handle = vm.m_nextMusic++;
        int platform = 0;
        if (vm.Music() && (platform = vm.Music()->Open(data)) == 0)
            return 2;
        uint32_t magic = LittleEndian::ToUInt32(data.data());
        MusicStream stream;
        stream.Handle = platform;
        stream.Compressed = magic == 0x5367674F || (magic & 0xFFFFFF) == 0x56474F;
        stream.SampleRate = MusicSampleRate(data);
        stream.LengthMs = MusicLengthMs(data);
        vm.m_music[handle] = stream;
        if (slot != vm.m_musicTable.end())
            *slot = handle;
        vm.Store(c, i.Args[2], handle);
        return 0;
    });
    // 06D8 s: close the stream
    Register(0x06D8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int handle = vm.Value(c, i.Args[0]);
        auto slot = std::find(vm.m_musicTable.begin(), vm.m_musicTable.end(), handle);
        if (slot != vm.m_musicTable.end())
            *slot = 0;
        if (auto* s = vm.Stream(handle))
        {
            int platform = s->Handle;
            vm.m_music.Remove(handle);
            if (platform != 0 && vm.Music())
                vm.Music()->Close(platform);
        }
        return 0;
    });
    // 06D9 s, flags: play from the start (flags 2: loop; bits 16-23 loop count)
    Register(0x06D9, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        int flags = vm.Value(c, i.Args[1]);
        if (!s)
            return 0;
        s->Looping = (flags & 2) != 0;
        if (s->Handle != 0 && vm.Music())
        {
            vm.Music()->Stop(s->Handle);
            vm.Music()->SetVolume(s->Handle, MusicVolume(s->Volume));
            vm.Music()->Play(s->Handle, s->Looping);
        }
        s->Status = 1;
        return 0;
    });
    // 06DA s: stop
    Register(0x06DA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        if (auto* s = vm.Stream(vm.Value(c, i.Args[0])))
        {
            if (s->Handle != 0 && vm.Music())
                vm.Music()->Stop(s->Handle);
            s->Status = 0;
        }
        return 0;
    });
    // 06DB s: pause (the window lost the focus); 06DC s: go on
    Register(0x06DB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        if (s && (s->Status & 5) == 1)
        {
            s->Status |= 4;
            if (s->Handle != 0 && vm.Music())
                vm.Music()->Pause(s->Handle);
        }
        return 0;
    });
    Register(0x06DC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        if (s && (s->Status & 4) != 0)
        {
            s->Status &= ~4;
            if (s->Handle != 0 && vm.Music())
                vm.Music()->Resume(s->Handle);
        }
        return 0;
    });
    // 06DF s, volume: 0-100
    Register(0x06DF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        int volume = vm.Value(c, i.Args[1]);
        if (s)
        {
            s->Volume = volume;
            if (s->Handle != 0 && vm.Music())
                vm.Music()->SetVolume(s->Handle, MusicVolume(volume));
        }
        return 0;
    });
    // 06EC s, ms, delta, target: a fade (see FadeTo)
    Register(0x06EC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        int ms = vm.Value(c, i.Args[1]), delta = vm.Value(c, i.Args[2]), target = vm.Value(c, i.Args[3]);
        vm.FadeTo(s, ms, delta, target);
        return 0;
    });
    // 06DD s, ms, d / 06DE s, ms, d (v2.37): 06EC with d and the target 100 / with -d and the target 0
    Register(0x06DD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        int ms = vm.Value(c, i.Args[1]), delta = vm.Value(c, i.Args[2]);
        vm.FadeTo(s, ms, delta, 100);
        return 0;
    });
    Register(0x06DE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        int ms = vm.Value(c, i.Args[1]), delta = vm.Value(c, i.Args[2]);
        vm.FadeTo(s, ms, int(0u - uint32_t(delta)), 0);
        return 0;
    });
    // 06E3 s, v: v = the volume (0-100; -1 without a stream)
    Register(0x06E3, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        vm.Store(c, i.Args[1], s ? s->Volume : -1);
        return 0;
    });
    // 06E6 s, v: v = the stream's sample rate (0 without a stream)
    Register(0x06E6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        vm.Store(c, i.Args[1], s ? s->SampleRate : 0);
        return 0;
    });
    // 06E8 s, v: v = status (1 playing, 4 paused)
    Register(0x06E8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        if (s && (s->Status & 5) == 1 && s->Handle != 0 && vm.Music() && !vm.Music()->IsPlaying(s->Handle))
            s->Status = 0;
        vm.Store(c, i.Args[1], s ? s->Status : 0);
        return 0;
    });
    // 06F0 s, v: v = the stream's length in ms; only compressed streams
    Register(0x06F0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        if (!s || !s->Compressed)
            return 2;
        vm.Store(c, i.Args[1], s->LengthMs);
        return 0;
    });
    // 06EF s, start ms, loop ms, loop count (-1 each: unchanged); only compressed streams
    Register(0x06EF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        auto* s = vm.Stream(vm.Value(c, i.Args[0]));
        int start = vm.Value(c, i.Args[1]), loop = vm.Value(c, i.Args[2]), count = vm.Value(c, i.Args[3]);
        if (!s || !s->Compressed)
            return 2;
        if (start != -1)
            s->StartMs = start;
        if (loop != -1)
            s->LoopMs = loop;
        if (count != -1)
            s->LoopCount = count;
        return 0;
    });
}

} // namespace openshiina::scripting
