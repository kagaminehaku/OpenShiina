// The engine's DirectSound buffers (IScnSound) with NAudio: a buffer the scripts start is a
// voice of the shared mixer (ScnMixer), decoded as it plays (ScnMusic.Source: an Ogg Vorbis
// sound is not decoded up front). Volumes are DirectSound's hundredths of a decibel.

using OpenShiina.Scripting;

namespace OpenShiina.Audio;

public sealed class ScnSound(ScnMixer mixer) : IScnSound, IDisposable
{
    private sealed class Buffer(byte[] file)
    {
        public readonly byte[] File = file;
        public ScnVoice? Voice;
        public float Gain = 1;
        public bool Looping;
    }

    private readonly Dictionary<int, Buffer> m_buffers = new();
    private int m_next = 0x10000;

    public int CreateBuffer(byte[] file)
    {
        int id = m_next++;
        m_buffers[id] = new Buffer(file);
        return id;
    }

    public void Release(int buffer)
    {
        if (m_buffers.Remove(buffer, out var b))
            Close(b);
    }

    public void Play(int buffer, int flags)
    {
        if (!m_buffers.TryGetValue(buffer, out var b))
            return;
        Close(b);
        try
        {
            b.Looping = (flags & 1) != 0;
            var source = new ScnMusic.Source(b.File, b.Looping);
            b.Voice = mixer.Start(source, b.Gain, source);
        }
        catch (Exception)
        {
            // A sound that cannot be played stays silent, as a failed DirectSound buffer does
            Close(b);
        }
    }

    public void Stop(int buffer)
    {
        if (m_buffers.TryGetValue(buffer, out var b))
            Close(b);
    }

    public void SetVolume(int buffer, int volume)
    {
        if (!m_buffers.TryGetValue(buffer, out var b))
            return;
        b.Gain = volume <= -10000 ? 0 : (float)Math.Pow(10, Math.Min(volume, 0) / 2000.0);
        if (b.Voice != null)
            b.Voice.Gain = b.Gain;
    }

    public int Status(int buffer) =>
        m_buffers.TryGetValue(buffer, out var b) && b.Voice is { Ended: false } ? 1 | (b.Looping ? 4 : 0) : 0;

    private static void Close(Buffer b)
    {
        b.Voice?.Stop();
        b.Voice = null;
    }

    public void Dispose()
    {
        foreach (var b in m_buffers.Values)
            Close(b);
        m_buffers.Clear();
    }
}
