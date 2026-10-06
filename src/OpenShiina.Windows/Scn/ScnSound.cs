// The engine's DirectSound buffers (IScnSound) with NAudio: each buffer plays on its own wave
// output when the scripts start it. Volumes are DirectSound's hundredths of a decibel.

using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenShiina.Scripting;

namespace OpenShiina.Windows.Scn;

public sealed class ScnSound : IScnSound, IDisposable
{
    private sealed class Buffer(byte[] wave)
    {
        public readonly byte[] Wave = wave;
        public WaveOutEvent? Output;
        public VolumeSampleProvider? Volume;
        public float Gain = 1;
        public bool Looping;
    }

    private readonly Dictionary<int, Buffer> m_buffers = new();
    private int m_next = 0x10000;

    public int CreateBuffer(byte[] wave)
    {
        int id = m_next++;
        m_buffers[id] = new Buffer(wave);
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
            WaveStream reader = new WaveFileReader(new MemoryStream(b.Wave));
            b.Looping = (flags & 1) != 0;
            if (b.Looping)
                reader = new LoopStream(reader);
            b.Volume = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = b.Gain };
            b.Output = new WaveOutEvent { DesiredLatency = 100 };
            b.Output.Init(b.Volume);
            b.Output.Play();
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
        if (b.Volume != null)
            b.Volume.Volume = b.Gain;
    }

    public int Status(int buffer) =>
        m_buffers.TryGetValue(buffer, out var b) && b.Output?.PlaybackState == PlaybackState.Playing
            ? 1 | (b.Looping ? 4 : 0) : 0;

    private static void Close(Buffer b)
    {
        b.Output?.Stop();
        b.Output?.Dispose();
        b.Output = null;
        b.Volume = null;
    }

    public void Dispose()
    {
        foreach (var b in m_buffers.Values)
            Close(b);
        m_buffers.Clear();
    }

    /// <summary>A wave that starts again at its end (DSBPLAY_LOOPING).</summary>
    private sealed class LoopStream(WaveStream source) : WaveStream
    {
        public override WaveFormat WaveFormat => source.WaveFormat;
        public override long Length => long.MaxValue;
        public override long Position { get => source.Position; set => source.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = source.Read(buffer, offset + total, count - total);
                if (n == 0)
                {
                    if (source.Length == 0)
                        break;
                    source.Position = 0;
                }
                total += n;
            }
            return total;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                source.Dispose();
            base.Dispose(disposing);
        }
    }
}
