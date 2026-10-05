// Plays the story's mixed sound (AudioEngine) on the default Windows sound device.

using NAudio.Wave;

namespace OpenShiina.Windows;

public sealed class WaveOutput : IAudioOutput
{
    private readonly WaveOutEvent m_output = new() { DesiredLatency = 120 };

    public void Start(ISampleProvider source)
    {
        m_output.Init(source);
        m_output.Play();
    }

    public void Dispose()
    {
        m_output.Stop();
        m_output.Dispose();
    }
}
