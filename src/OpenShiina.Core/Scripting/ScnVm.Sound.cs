// Sound effects: DirectSound buffers in a table of 0x101 (0x7DAC20). A file is loaded and turned
// into a RIFF WAVE in memory first (FUN_004059D0 / 00405900: PAD, OggS and OGV are decoded, WAVE
// is kept), then a buffer is made of it (FUN_004047A0). A value above 0xFFFF in place of a table
// index is a buffer itself. The platform plays the buffers (IScnSound); without one the buffers
// only keep their state, and they never report themselves playing.

namespace OpenShiina.Scripting;

/// <summary>DirectSound buffers of the platform.</summary>
public interface IScnSound
{
    /// <summary>A buffer of a RIFF WAVE file; returns its handle (above 0xFFFF), or 0.</summary>
    int CreateBuffer(byte[] wave);

    void Release(int buffer);

    /// <summary>Stop, SetCurrentPosition(0), Play with DSBPLAY flags (1 = looping).</summary>
    void Play(int buffer, int flags);

    void Stop(int buffer);

    /// <summary>SetVolume in hundredths of a decibel (-10000 silent to 0).</summary>
    void SetVolume(int buffer, int volume);

    /// <summary>GetStatus: DSBSTATUS bits (1 playing, 4 looping).</summary>
    int Status(int buffer);
}

public sealed partial class ScnVm
{
    public const int SoundSlots = 0x101;
    private readonly int[] m_sounds = new int[SoundSlots];
    private int m_nextSound = 0x10000;

    private IScnSound? Sound => m_host.Sound;

    /// <summary>FUN_00405900: the file as a RIFF WAVE, or null when it is none of the known kinds.</summary>
    public static byte[]? SoundToWave(byte[] data)
    {
        if (data.Length < 12)
            return null;
        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic == 0x46464952 && BitConverter.ToUInt32(data, 8) == 0x45564157)     // RIFF....WAVE
            return data;
        if ((magic & 0xFFFFFF) == 0x444150)     // PAD
            return AudioDecoder.DecodePad(data);
        if (magic == 0x5367674F)                // OggS
            return AudioDecoder.OggToWav(data);
        if ((magic & 0xFFFFFF) == 0x56474F)     // OGV
            return AudioDecoder.DecodeOgv(data) is { } ogg ? AudioDecoder.OggToWav(ogg) : null;
        return null;
    }

    /// <summary>SoundToWave; with no platform sound only the kind of file is checked.</summary>
    private byte[]? ToWave(byte[] data)
    {
        if (Sound != null)
            return SoundToWave(data);
        if (data.Length < 12)
            return null;
        uint magic = BitConverter.ToUInt32(data, 0);
        return magic is 0x46464952 or 0x5367674F || (magic & 0xFFFFFF) is 0x444150 or 0x56474F ? data : null;
    }

    /// <summary>A buffer by table index, or the value itself when it is a buffer (above 0xFFFF).</summary>
    private int SoundBuffer(int value) => (uint)value > 0xFFFF ? value : value < SoundSlots ? m_sounds[value] : 0;

    private bool MakeSound(int slot, byte[]? wave)
    {
        if (wave == null || (uint)slot >= SoundSlots)
            return false;
        if (m_sounds[slot] != 0)
            Sound?.Release(m_sounds[slot]);
        int buffer = Sound != null ? Sound.CreateBuffer(wave) : m_nextSound++;
        m_sounds[slot] = buffer;
        return buffer != 0;
    }

    private void RegisterSound()
    {
        // 06A5: release every buffer
        Register(0x06A5, (vm, c, i) =>
        {
            for (int k = 0; k < SoundSlots; k++)
                if (vm.m_sounds[k] != 0)
                {
                    vm.Sound?.Release(vm.m_sounds[k]);
                    vm.m_sounds[k] = 0;
                }
            return 0;
        });
        // 06A6 n, file: buffer n of a sound file
        Register(0x06A6, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            string file = vm.ReadString(vm.Value(c, i.Args[1]));
            if (vm.ReadScriptFile(file) is not { } data)
                return 2;
            return vm.MakeSound(slot, vm.ToWave(data)) ? 0 : 2;
        });
        // 06B1 n, address: buffer n of a sound in memory
        Register(0x06B1, (vm, c, i) =>
        {
            int slot = vm.Value(c, i.Args[0]);
            int address = vm.Value(c, i.Args[1]);
            int length = vm.EngineGlobals.GetValueOrDefault(0x4C4514);
            byte[] data = vm.ReadBytes(address, Math.Max(length, 12));
            return vm.MakeSound(slot, vm.ToWave(data)) ? 0 : 2;
        });
        // 06A7 n, flags: play from the start (1 = loop)
        Register(0x06A7, (vm, c, i) =>
        {
            int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
            int flags = vm.Value(c, i.Args[1]);
            if (buffer != 0)
                vm.Sound?.Play(buffer, flags);
            return 0;
        });
        // 06A8 n: stop
        Register(0x06A8, (vm, c, i) =>
        {
            int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
            if (buffer != 0)
                vm.Sound?.Stop(buffer);
            return 0;
        });
        // 06A9 n, volume: in 1/100 dB
        Register(0x06A9, (vm, c, i) =>
        {
            int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
            int volume = vm.Value(c, i.Args[1]);
            if (buffer != 0)
                vm.Sound?.SetVolume(buffer, volume);
            return 0;
        });
        // 06AF n, v: v = DSBSTATUS, or -1 for no buffer
        Register(0x06AF, (vm, c, i) =>
        {
            int buffer = vm.SoundBuffer(vm.Value(c, i.Args[0]));
            vm.Store(c, i.Args[1], buffer == 0 ? -1 : vm.Sound?.Status(buffer) ?? 0);
            return 0;
        });
        // 06B0 n: release buffer n
        Register(0x06B0, (vm, c, i) =>
        {
            int value = vm.Value(c, i.Args[0]);
            if ((uint)value > 0xFFFF)
            {
                vm.Sound?.Release(value);
                return 0;
            }
            if (value < SoundSlots && vm.m_sounds[value] != 0)
            {
                vm.Sound?.Release(vm.m_sounds[value]);
                vm.m_sounds[value] = 0;
            }
            return 0;
        });
    }
}
