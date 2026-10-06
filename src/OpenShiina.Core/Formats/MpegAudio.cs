// The sound of a movie: the MPEG-1 audio stream of an MPEG-1 system stream (Layer II in the
// games' movies), decoded with NLayer into a 16-bit RIFF WAVE.

using NLayer;

namespace OpenShiina.Formats;

public static class MpegAudio
{
    /// <summary>The audio stream (MpegSystemStream.Audio) as a RIFF WAVE, or null when there is none or it cannot be decoded.</summary>
    public static byte[]? ToWave(byte[] audio)
    {
        if (audio.Length == 0)
            return null;
        try
        {
            using var file = new MpegFile(new MemoryStream(audio, writable: false));
            int channels = file.Channels, rate = file.SampleRate;
            if (channels is < 1 or > 2 || rate <= 0)
                return null;
            using var pcm = new MemoryStream();
            var samples = new float[4096 * channels];
            var bytes = new byte[samples.Length * 2];
            int n;
            while ((n = file.ReadSamples(samples, 0, samples.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    short s = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
                    bytes[2 * i] = (byte)s;
                    bytes[2 * i + 1] = (byte)(s >> 8);
                }
                pcm.Write(bytes, 0, 2 * n);
            }
            return pcm.Length == 0 ? null : Wave(pcm.ToArray(), channels, rate);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The part of a 16-bit WAVE made by <see cref="ToWave"/> from <paramref name="milliseconds"/> on.</summary>
    public static byte[] From(byte[] wave, long milliseconds)
    {
        int channels = BitConverter.ToInt16(wave, 22), rate = BitConverter.ToInt32(wave, 24);
        long frame = milliseconds * rate / 1000;
        int dataLength = wave.Length - 44;
        int skip = (int)Math.Clamp(frame * channels * 2, 0, dataLength);
        return Wave(wave.AsSpan(44 + skip).ToArray(), channels, rate);
    }

    private static byte[] Wave(byte[] pcm, int channels, int rate)
    {
        var wave = new byte[44 + pcm.Length];
        void Text(int at, string s) { for (int i = 0; i < 4; i++) wave[at + i] = (byte)s[i]; }
        Text(0, "RIFF");
        BitConverter.TryWriteBytes(wave.AsSpan(4), 36 + pcm.Length);
        Text(8, "WAVE");
        Text(12, "fmt ");
        BitConverter.TryWriteBytes(wave.AsSpan(16), 16);
        BitConverter.TryWriteBytes(wave.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(wave.AsSpan(22), (short)channels);
        BitConverter.TryWriteBytes(wave.AsSpan(24), rate);
        BitConverter.TryWriteBytes(wave.AsSpan(28), rate * channels * 2);
        BitConverter.TryWriteBytes(wave.AsSpan(32), (short)(channels * 2));
        BitConverter.TryWriteBytes(wave.AsSpan(34), (short)16);
        Text(36, "data");
        BitConverter.TryWriteBytes(wave.AsSpan(40), pcm.Length);
        pcm.CopyTo(wave, 44);
        return wave;
    }
}
