// The engine's mix (ScnMixer.Output: 44.1 kHz stereo float) on the platform's sound device through
// SDL3. A thread of its own keeps about 60 ms of samples queued in an SDL audio stream, the latency
// of the Windows player's wave output. Without a sound device the game runs silent.

using NAudio.Wave;
using SDL;

namespace OpenShiina.App;

public sealed unsafe class SdlAudioOutput : IDisposable
{
    private const int Rate = 44100, Channels = 2, ChunkFrames = 512;
    private const int TargetBytes = Rate * Channels * sizeof(float) * 60 / 1000;

    private readonly ISampleProvider m_source;
    private readonly SDL_AudioStream* m_stream;
    private readonly Thread m_thread;
    private volatile bool m_stop;

    private SdlAudioOutput(ISampleProvider source, SDL_AudioStream* stream)
    {
        m_source = source;
        m_stream = stream;
        m_thread = new Thread(Feed) { IsBackground = true, Name = "OpenShiina sound", Priority = ThreadPriority.AboveNormal };
        m_thread.Start();
    }

    /// <summary>Plays <paramref name="source"/> (44.1 kHz stereo float) on the default device; null when there is none.</summary>
    public static SdlAudioOutput? TryStart(ISampleProvider source)
    {
        if (source.WaveFormat.SampleRate != Rate || source.WaveFormat.Channels != Channels)
            throw new ArgumentException("The mix must be 44.1 kHz stereo.", nameof(source));
        if (!SDL3.SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
            return null;
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = Channels, freq = Rate };
        var stream = SDL3.SDL_OpenAudioDeviceStream(SDL3.SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
        if (stream == null)
        {
            SDL3.SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
            return null;
        }
        SDL3.SDL_ResumeAudioStreamDevice(stream);
        return new SdlAudioOutput(source, stream);
    }

    private void Feed()
    {
        var buffer = new float[ChunkFrames * Channels];
        while (!m_stop)
        {
            if (SDL3.SDL_GetAudioStreamQueued(m_stream) >= TargetBytes)
            {
                Thread.Sleep(5);
                continue;
            }
            int n = m_source.Read(buffer, 0, buffer.Length);
            if (n <= 0)
            {
                Array.Clear(buffer);
                n = buffer.Length;
            }
            fixed (float* p = buffer)
                SDL3.SDL_PutAudioStreamData(m_stream, (IntPtr)p, n * sizeof(float));
        }
    }

    public void Dispose()
    {
        if (m_stop)
            return;
        m_stop = true;
        m_thread.Join(500);
        SDL3.SDL_DestroyAudioStream(m_stream);
        SDL3.SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
    }
}
