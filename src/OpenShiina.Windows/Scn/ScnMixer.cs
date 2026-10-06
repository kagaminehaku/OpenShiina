// One NAudio output for every sound of the scripts: the sound buffers (ScnSound) and the music
// streams (ScnMusic) are inputs of a mixer at 44.1 kHz stereo. Starting a sound only adds an
// input - opening a wave output for each one took 20-30 ms (100 ms the first time) on the window's
// thread, a dropped frame or two every time a button played its sound under the mouse.

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenShiina.Windows.Scn;

public sealed class ScnMixer : IDisposable
{
    public const int Rate = 44100;
    private readonly MixingSampleProvider m_mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)) { ReadFully = true };
    private readonly WaveOutEvent m_output = new() { DesiredLatency = 100 };

    public ScnMixer()
    {
        m_output.Init(m_mixer);
        m_output.Play();
    }

    /// <summary>Plays samples (any rate, mono or stereo); the voice ends at their end or when stopped.</summary>
    public ScnVoice Start(ISampleProvider source, float gain, IDisposable? owner = null)
    {
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        if (source.WaveFormat.SampleRate != Rate)
            source = new WdlResamplingSampleProvider(source, Rate);
        var voice = new ScnVoice(source, owner) { Gain = gain };
        if (source.WaveFormat.Channels == 2)
            m_mixer.AddMixerInput(voice);
        else
            voice.Stop();
        return voice;
    }

    public void Dispose()
    {
        m_output.Stop();
        m_output.Dispose();
    }
}

/// <summary>A sound playing in the mixer: volume, pause and stop from any thread.</summary>
public sealed class ScnVoice(ISampleProvider source, IDisposable? owner) : ISampleProvider
{
    private volatile bool m_stopped, m_ended;

    public WaveFormat WaveFormat => source.WaveFormat;
    public volatile float Gain;
    public volatile bool Paused;

    /// <summary>Finished: played to its end, or stopped.</summary>
    public bool Ended => m_ended;

    /// <summary>Ends the voice; its source is let go on the mixer's thread.</summary>
    public void Stop() => m_stopped = true;

    public int Read(float[] buffer, int offset, int count)
    {
        if (m_stopped || m_ended)
        {
            Finish();
            return 0;
        }
        if (Paused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        int n = source.Read(buffer, offset, count);
        float gain = Gain;
        if (gain != 1)
            for (int i = 0; i < n; i++)
                buffer[offset + i] *= gain;
        if (n < count)
            Finish();
        return n;
    }

    private void Finish()
    {
        if (m_ended)
            return;
        m_ended = true;
        owner?.Dispose();
    }
}
