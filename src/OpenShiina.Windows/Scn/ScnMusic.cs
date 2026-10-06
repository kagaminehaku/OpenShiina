// The engine's music streams (IScnMusic): Ogg Vorbis (and the OGV wrapper) decoded as it plays
// with NVorbis, PAD and WAVE through a wave reader, each stream on its own NAudio output.

using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenShiina.Scripting;

namespace OpenShiina.Windows.Scn;

public sealed class ScnMusic : IScnMusic, IDisposable
{
    private sealed class Stream(byte[] file)
    {
        public readonly byte[] File = file;
        public WaveOutEvent? Output;
        public Source? Source;
        public VolumeSampleProvider? Volume;
        public float Gain = 1;
    }

    private readonly Dictionary<int, Stream> m_streams = new();
    private int m_next = 1;

    public int Open(byte[] file)
    {
        int id = m_next++;
        m_streams[id] = new Stream(file);
        return id;
    }

    public void Close(int stream)
    {
        if (m_streams.Remove(stream, out var s))
            Halt(s);
    }

    public void Play(int stream, bool loop)
    {
        if (!m_streams.TryGetValue(stream, out var s))
            return;
        Halt(s);
        try
        {
            s.Source = new Source(s.File, loop);
            s.Volume = new VolumeSampleProvider(s.Source) { Volume = s.Gain };
            s.Output = new WaveOutEvent { DesiredLatency = 150 };
            s.Output.Init(s.Volume);
            s.Output.Play();
        }
        catch (Exception)
        {
            // Music that cannot be decoded stays silent
            Halt(s);
        }
    }

    public void Stop(int stream)
    {
        if (m_streams.TryGetValue(stream, out var s))
            Halt(s);
    }

    public void Pause(int stream)
    {
        if (m_streams.TryGetValue(stream, out var s))
            s.Output?.Pause();
    }

    public void Resume(int stream)
    {
        if (m_streams.TryGetValue(stream, out var s) && s.Output?.PlaybackState == PlaybackState.Paused)
            s.Output.Play();
    }

    public void SetVolume(int stream, int volume)
    {
        if (!m_streams.TryGetValue(stream, out var s))
            return;
        s.Gain = volume <= -10000 ? 0 : (float)Math.Pow(10, Math.Min(volume, 0) / 2000.0);
        if (s.Volume != null)
            s.Volume.Volume = s.Gain;
    }

    public bool IsPlaying(int stream) =>
        m_streams.TryGetValue(stream, out var s) && s.Output != null && s.Source is { Ended: false };

    private static void Halt(Stream s)
    {
        s.Output?.Stop();
        s.Output?.Dispose();
        s.Output = null;
        s.Source?.Dispose();
        s.Source = null;
        s.Volume = null;
    }

    public void Dispose()
    {
        foreach (var s in m_streams.Values)
            Halt(s);
        m_streams.Clear();
    }

    /// <summary>Samples of a sound file, from the start again at its end when looping.</summary>
    private sealed class Source : ISampleProvider, IDisposable
    {
        private readonly NVorbis.VorbisReader? m_vorbis;
        private readonly WaveFileReader? m_wave;
        private readonly ISampleProvider? m_waveSamples;
        private readonly bool m_loop;

        public WaveFormat WaveFormat { get; }
        public bool Ended { get; private set; }

        public Source(byte[] file, bool loop)
        {
            m_loop = loop;
            uint magic = BitConverter.ToUInt32(file, 0);
            byte[]? ogg = (magic & 0xFFFFFF) == 0x56474F ? AudioDecoder.DecodeOgv(file) : magic == 0x5367674F ? file : null;
            if (ogg != null)
            {
                m_vorbis = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(m_vorbis.SampleRate, m_vorbis.Channels);
                return;
            }
            byte[] wave = (magic & 0xFFFFFF) == 0x444150 ? AudioDecoder.DecodePad(file) ?? file : file;
            m_wave = new WaveFileReader(new MemoryStream(wave));
            m_waveSamples = m_wave.ToSampleProvider();
            WaveFormat = m_waveSamples.WaveFormat;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            count -= count % WaveFormat.Channels;
            int total = 0;
            while (total < count)
            {
                int n = m_vorbis != null
                    ? m_vorbis.ReadSamples(buffer, offset + total, count - total)
                    : m_waveSamples!.Read(buffer, offset + total, count - total);
                if (n == 0)
                {
                    if (!m_loop)
                    {
                        Ended = true;
                        break;
                    }
                    if (m_vorbis != null)
                        m_vorbis.SeekTo(0);
                    else
                        m_wave!.Position = 0;
                    if (total == 0 && ++m_emptyRewinds > 2)
                        break;
                    continue;
                }
                m_emptyRewinds = 0;
                total += n;
            }
            return total;
        }

        // An empty file would loop for ever: stop instead
        private int m_emptyRewinds;

        public void Dispose()
        {
            m_vorbis?.Dispose();
            m_wave?.Dispose();
        }
    }
}
