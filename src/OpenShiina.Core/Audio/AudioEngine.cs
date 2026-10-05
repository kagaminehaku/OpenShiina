// Plays the story's sounds at the same time: BGM, voice and numbered sound-effect channels,
// each with its own loop flag and fades, mixed into one output. The mixed samples go to an
// IAudioOutput, the sound device of the front end.

using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenShiina.Audio;

/// <summary>A sound device that plays the mixer's output (44.1 kHz stereo floats) until disposed.</summary>
public interface IAudioOutput : IDisposable
{
    void Start(ISampleProvider source);
}

public sealed class AudioEngine : IDisposable
{
    private const int SampleRate = 44100;

    public const string Music = "bgm";
    public const string Voice = "voice";
    public static string Effect(int channel) => "se" + channel;

    private readonly IAudioOutput m_output;
    private readonly MixingSampleProvider m_mixer;
    private readonly Dictionary<string, Track> m_tracks = new();
    private readonly object m_lock = new();

    /// <summary>Volume of each kind of sound, 0-1.</summary>
    public float MusicVolume { get; set; } = 0.55f;
    public float VoiceVolume { get; set; } = 1.0f;
    public float EffectVolume { get; set; } = 0.8f;

    public AudioEngine(IAudioOutput output)
    {
        m_mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)) { ReadFully = true };
        m_output = output;
        m_output.Start(m_mixer);
    }

    private float VolumeOf(string channel) =>
        channel == Music ? MusicVolume : channel == Voice ? VoiceVolume : EffectVolume;

    /// <summary>
    /// Plays <paramref name="data"/> (Ogg Vorbis or WAV bytes) on a channel, replacing what it
    /// played before, <paramref name="plays"/> times (0 = until stopped).
    /// </summary>
    public void Play(string channel, byte[] data, int plays, double fadeInMs = 0)
    {
        Stop(channel);
        Track track;
        try
        {
            track = new Track(data, plays, VolumeOf(channel), fadeInMs);
        }
        catch
        {
            return; // A sound that cannot be decoded is skipped
        }
        lock (m_lock)
            m_tracks[channel] = track;
        m_mixer.AddMixerInput(track);
    }

    /// <summary>Stops a channel, fading out over <paramref name="fadeMs"/>.</summary>
    public void Stop(string channel, double fadeMs = 0)
    {
        Track? track;
        lock (m_lock)
        {
            if (!m_tracks.Remove(channel, out track))
                return;
        }
        if (fadeMs > 0)
            track.FadeOut(fadeMs);
        else
        {
            track.Finish();
            m_mixer.RemoveMixerInput(track);
        }
    }

    public void StopAll()
    {
        List<string> channels;
        lock (m_lock)
            channels = m_tracks.Keys.ToList();
        foreach (var channel in channels)
            Stop(channel);
    }

    public bool IsPlaying(string channel)
    {
        lock (m_lock)
            return m_tracks.TryGetValue(channel, out var track) && !track.IsFinished;
    }

    /// <summary>Applies changed volume settings to the sounds already playing.</summary>
    public void UpdateVolumes()
    {
        lock (m_lock)
        {
            foreach (var (channel, track) in m_tracks)
                track.Volume = VolumeOf(channel);
        }
    }

    public void Dispose()
    {
        m_output.Dispose();
        lock (m_lock)
        {
            foreach (var track in m_tracks.Values)
                track.Dispose();
            m_tracks.Clear();
        }
    }

    /// <summary>One sound, converted to 44.1 kHz stereo, with looping and fades.</summary>
    private sealed class Track : ISampleProvider, IDisposable
    {
        private readonly IDisposable m_reader;
        private readonly Action m_rewind;
        private readonly ISampleProvider m_source;
        private int m_playsLeft;        // 0 = loop until stopped

        private double m_gain;          // current fade level 0-1
        private double m_gainStep;      // change per sample frame
        private bool m_stopAfterFade;
        private volatile bool m_finished;

        public float Volume { get; set; }
        public bool IsFinished => m_finished;
        public WaveFormat WaveFormat { get; }

        public Track(byte[] data, int plays, float volume, double fadeInMs)
        {
            ISampleProvider source;
            if (data.Length >= 4 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
            {
                var vorbis = new VorbisProvider(new MemoryStream(data));
                m_reader = vorbis;
                m_rewind = vorbis.Rewind;
                source = vorbis;
            }
            else
            {
                var wav = new WaveFileReader(new MemoryStream(data));
                m_reader = wav;
                m_rewind = () => wav.Position = 0;
                source = wav.ToSampleProvider();
            }
            if (source.WaveFormat.Channels == 1)
                source = new MonoToStereoSampleProvider(source);
            else if (source.WaveFormat.Channels > 2)
                throw new NotSupportedException("Only mono and stereo sounds are played.");
            if (source.WaveFormat.SampleRate != SampleRate)
                source = new WdlResamplingSampleProvider(source, SampleRate);

            m_source = source;
            m_playsLeft = Math.Max(0, plays);
            Volume = volume;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
            if (fadeInMs > 0)
            {
                m_gain = 0;
                m_gainStep = 1.0 / (fadeInMs * SampleRate / 1000);
            }
            else
                m_gain = 1;
        }

        public void FadeOut(double ms)
        {
            m_gainStep = -Math.Max(m_gain, 0.0001) / Math.Max(1, ms * SampleRate / 1000);
            m_stopAfterFade = true;
        }

        public void Finish() => m_finished = true;

        public int Read(float[] buffer, int offset, int count)
        {
            if (m_finished)
                return 0;

            int total = 0;
            bool rewound = false;
            while (total < count)
            {
                int n = m_source.Read(buffer, offset + total, count - total);
                if (n > 0)
                {
                    total += n;
                    rewound = false;
                    continue;
                }
                // End of the sound: start again, unless the sound is empty
                if (rewound || m_playsLeft == 1)
                    break;
                if (m_playsLeft > 1)
                    m_playsLeft--;
                m_rewind();
                rewound = true;
            }

            float volume = Volume;
            for (int i = 0; i < total; i += 2)
            {
                if (m_gainStep != 0)
                {
                    m_gain += m_gainStep;
                    if (m_gain >= 1)
                    {
                        m_gain = 1;
                        m_gainStep = 0;
                    }
                    else if (m_gain <= 0)
                    {
                        m_gain = 0;
                        m_gainStep = 0;
                        if (m_stopAfterFade)
                            m_finished = true;
                    }
                }
                float g = (float)(m_gain * volume);
                buffer[offset + i] *= g;
                if (i + 1 < total)
                    buffer[offset + i + 1] *= g;
            }

            if (total < count)
                m_finished = true;
            // The mixer drops an input that returns less than it asked for
            return m_finished ? total : count;
        }

        public void Dispose()
        {
            m_finished = true;
            m_reader.Dispose();
        }
    }

    /// <summary>Ogg Vorbis decoded by NVorbis.</summary>
    private sealed class VorbisProvider : ISampleProvider, IDisposable
    {
        private readonly NVorbis.VorbisReader m_reader;

        public WaveFormat WaveFormat { get; }

        public VorbisProvider(Stream stream)
        {
            m_reader = new NVorbis.VorbisReader(stream, true);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(m_reader.SampleRate, m_reader.Channels);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Keep whole sample frames
            count -= count % WaveFormat.Channels;
            return m_reader.ReadSamples(buffer, offset, count);
        }

        public void Rewind() => m_reader.SeekTo(0);

        public void Dispose() => m_reader.Dispose();
    }
}
