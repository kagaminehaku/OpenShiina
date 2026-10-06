// Streamed music (the engine's "Synthia PCM" streams, FUN_00453220 ...): a stream is opened on a
// file in memory (flag 1, from op_00C9) or on disk, kept in a table of 256 (0xBCDF44) and played
// through its own DirectSound buffer. Status bits (+0x1C4): 1 playing, 4 paused. Volume is an
// index 0-100 into a table of hundredths of a decibel, 1000 * log2(i / 100) (0x4A15E0). The
// platform decodes and plays (IScnMusic); without one a started stream counts as playing until
// it is stopped.

namespace OpenShiina.Scripting;

/// <summary>Music streams of the platform.</summary>
public interface IScnMusic
{
    /// <summary>A stream of a sound file (OGV, Ogg Vorbis, PAD or RIFF WAVE); 0 when it cannot be played.</summary>
    int Open(byte[] file);

    void Close(int stream);

    /// <summary>Plays from the start, looping when <paramref name="loop"/> is set.</summary>
    void Play(int stream, bool loop);

    void Stop(int stream);

    void Pause(int stream);

    void Resume(int stream);

    /// <summary>Volume in hundredths of a decibel (-10000 silent to 0).</summary>
    void SetVolume(int stream, int volume);

    /// <summary>The stream is still sounding (false once a stream without a loop has ended).</summary>
    bool IsPlaying(int stream);
}

public sealed partial class ScnVm
{
    private sealed class MusicStream
    {
        public int Handle;
        public int Status;
        public int Volume = 100;
        public int LoopCount;
        public bool Looping;
        public bool Compressed;
        public int StartMs, LoopMs = -1;
    }

    // 0x4A15E0: volume 0-100 -> DirectSound volume
    private static readonly int[] s_musicVolumes =
    [
        -10000, -6643, -5643, -5058, -4643, -4321, -4058, -3836, -3643, -3473, -3321, -3184, -3058, -2943,
        -2836, -2736, -2643, -2556, -2473, -2395, -2321, -2251, -2184, -2120, -2058, -2000, -1943, -1888,
        -1836, -1785, -1736, -1689, -1643, -1599, -1556, -1514, -1473, -1434, -1395, -1358, -1321, -1286,
        -1251, -1217, -1184, -1152, -1120, -1089, -1058, -1029, -1000, -971, -943, -915, -888, -862, -836,
        -810, -785, -761, -736, -713, -689, -666, -643, -621, -599, -577, -556, -535, -514, -494, -473,
        -454, -434, -415, -395, -377, -358, -340, -321, -304, -286, -268, -251, -234, -217, -200, -184,
        -168, -152, -136, -120, -104, -89, -74, -58, -43, -29, -14, 0,
    ];

    private const int MusicSlots = 256;
    private readonly int[] m_musicTable = new int[MusicSlots];
    private readonly Dictionary<int, MusicStream> m_music = new();
    private int m_nextMusic = 0x20000;

    private IScnMusic? Music => m_host.Music;

    private MusicStream? Stream(int handle) => m_music.GetValueOrDefault(handle);

    private void RegisterMusic()
    {
        // 06D6 source, flags, v: open a stream (flag 1: source is a file in memory, else a file
        // name); v = the stream
        Register(0x06D6, (vm, c, i) =>
        {
            int source = vm.Value(c, i.Args[0]), flags = vm.Value(c, i.Args[1]);
            byte[]? data;
            if ((flags & 1) != 0)
            {
                int size = vm.LoadedFileSize(source);
                data = size > 0 ? vm.ReadBytes(source, size) : null;
            }
            else
                data = vm.ReadScriptFile(vm.ReadString(source));
            if (data == null || data.Length < 12 || !IsMusicFile(data))
                return 2;
            int slot = Array.IndexOf(vm.m_musicTable, 0);
            int handle = vm.m_nextMusic++;
            int platform = 0;
            if (vm.Music != null && (platform = vm.Music.Open(data)) == 0)
                return 2;
            uint magic = BitConverter.ToUInt32(data, 0);
            vm.m_music[handle] = new MusicStream
            {
                Handle = platform,
                Compressed = magic == 0x5367674F || (magic & 0xFFFFFF) == 0x56474F,
            };
            if (slot >= 0)
                vm.m_musicTable[slot] = handle;
            vm.Store(c, i.Args[2], handle);
            return 0;
        });
        // 06D8 s: close the stream
        Register(0x06D8, (vm, c, i) =>
        {
            int handle = vm.Value(c, i.Args[0]);
            int slot = Array.IndexOf(vm.m_musicTable, handle);
            if (slot >= 0)
                vm.m_musicTable[slot] = 0;
            if (vm.m_music.Remove(handle, out var s) && s.Handle != 0)
                vm.Music?.Close(s.Handle);
            return 0;
        });
        // 06D9 s, flags: play from the start (flags 2: loop; bits 16-23 loop count)
        Register(0x06D9, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            int flags = vm.Value(c, i.Args[1]);
            if (s == null)
                return 0;
            s.Looping = (flags & 2) != 0;
            if (s.Handle != 0)
            {
                vm.Music!.Stop(s.Handle);
                vm.Music.SetVolume(s.Handle, s_musicVolumes[Math.Clamp(s.Volume, 0, 100)]);
                vm.Music.Play(s.Handle, s.Looping);
            }
            s.Status = 1;
            return 0;
        });
        // 06DA s: stop
        Register(0x06DA, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            if (s != null)
            {
                if (s.Handle != 0)
                    vm.Music!.Stop(s.Handle);
                s.Status = 0;
            }
            return 0;
        });
        // 06DB s: pause (the window lost the focus); 06DC s: go on
        Register(0x06DB, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            if (s != null && (s.Status & 5) == 1)
            {
                s.Status |= 4;
                if (s.Handle != 0)
                    vm.Music!.Pause(s.Handle);
            }
            return 0;
        });
        Register(0x06DC, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            if (s != null && (s.Status & 4) != 0)
            {
                s.Status &= ~4;
                if (s.Handle != 0)
                    vm.Music!.Resume(s.Handle);
            }
            return 0;
        });
        // 06DF s, volume: 0-100
        Register(0x06DF, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            int volume = vm.Value(c, i.Args[1]);
            if (s != null)
            {
                s.Volume = volume;
                if (s.Handle != 0)
                    vm.Music!.SetVolume(s.Handle, s_musicVolumes[Math.Clamp(volume, 0, 100)]);
            }
            return 0;
        });
        // 06E8 s, v: v = status (1 playing, 4 paused)
        Register(0x06E8, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            if (s != null && (s.Status & 5) == 1 && s.Handle != 0 && !vm.Music!.IsPlaying(s.Handle))
                s.Status = 0;
            vm.Store(c, i.Args[1], s?.Status ?? 0);
            return 0;
        });
        // 06EF s, start ms, loop ms, loop count (-1 each: unchanged); only compressed streams
        Register(0x06EF, (vm, c, i) =>
        {
            var s = vm.Stream(vm.Value(c, i.Args[0]));
            int start = vm.Value(c, i.Args[1]), loop = vm.Value(c, i.Args[2]), count = vm.Value(c, i.Args[3]);
            if (s == null || !s.Compressed)
                return 2;
            if (start != -1)
                s.StartMs = start;
            if (loop != -1)
                s.LoopMs = loop;
            if (count != -1)
                s.LoopCount = count;
            return 0;
        });
    }

    private static bool IsMusicFile(byte[] data)
    {
        uint magic = BitConverter.ToUInt32(data, 0);
        return magic is 0x46464952 or 0x5367674F || (magic & 0xFFFFFF) is 0x444150 or 0x56474F;
    }
}
