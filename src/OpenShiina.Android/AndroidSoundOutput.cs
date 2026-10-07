// The engine's mix (ScnMixer.Output: 44.1 kHz stereo float) on Android's AudioTrack, as
// SdlAudioOutput does on the desktop: a thread of its own reads the mix and writes it into the
// track, which keeps a short buffer (a little more than Android's least) so the sound follows the
// game closely. The writes do not block, so stopping never waits on the device.

using Android.Media;
using NAudio.Wave;

namespace OpenShiina.Android;

public sealed class AndroidSoundOutput : IDisposable
{
    private const int Rate = 44100, Channels = 2, ChunkFrames = 512;

    private readonly ISampleProvider m_source;
    private readonly AudioTrack m_track;
    private readonly Thread m_thread;
    private volatile bool m_stop;

    private AndroidSoundOutput(ISampleProvider source, AudioTrack track)
    {
        m_source = source;
        m_track = track;
        m_thread = new Thread(Feed) { IsBackground = true, Name = "OpenShiina sound", Priority = ThreadPriority.AboveNormal };
        m_thread.Start();
    }

    /// <summary>Plays <paramref name="source"/> (44.1 kHz stereo float); null when the device cannot.</summary>
    public static IDisposable? TryStart(ISampleProvider source)
    {
        if (source.WaveFormat.SampleRate != Rate || source.WaveFormat.Channels != Channels)
            throw new ArgumentException("The mix must be 44.1 kHz stereo.", nameof(source));
        try
        {
            int least = AudioTrack.GetMinBufferSize(Rate, ChannelOut.Stereo, Encoding.PcmFloat);
            if (least <= 0)
                return null;
            var track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Game)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!)!
                .SetAudioFormat(new AudioFormat.Builder()
                    .SetEncoding(Encoding.PcmFloat)!
                    .SetSampleRate(Rate)!
                    .SetChannelMask(ChannelOut.Stereo)!
                    .Build()!)!
                .SetBufferSizeInBytes(Math.Max(least * 2, ChunkFrames * Channels * sizeof(float) * 2))!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;
            if (track.State != AudioTrackState.Initialized)
            {
                track.Release();
                return null;
            }
            track.Play();
            return new AndroidSoundOutput(source, track);
        }
        catch (Exception e) when (e is Java.Lang.Exception or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private void Feed()
    {
        var buffer = new float[ChunkFrames * Channels];
        while (!m_stop)
        {
            int n = m_source.Read(buffer, 0, buffer.Length);
            if (n <= 0)
            {
                Array.Clear(buffer);
                n = buffer.Length;
            }
            // The track takes what fits; the rest waits for room
            for (int at = 0; at < n && !m_stop;)
            {
                int written = m_track.Write(buffer, at, n - at, WriteType.NonBlocking);
                if (written < 0)
                    return;
                at += written;
                if (at < n)
                    Thread.Sleep(2);
            }
        }
    }

    public void Dispose()
    {
        if (m_stop)
            return;
        m_stop = true;
        m_thread.Join(500);
        try
        {
            m_track.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
            // Not playing
        }
        m_track.Release();
        m_track.Dispose();
    }
}
