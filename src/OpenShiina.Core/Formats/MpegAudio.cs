// The sound of a movie: the MPEG-1 audio stream of an MPEG-1 system stream (Layer II in the
// games' movies), decoded with NLayer into a 16-bit RIFF WAVE. StartWave gives the WAVE at once
// with its first seconds decoded and the rest decoded into it on another thread (an opening
// movie's 100 s took 0.6 s at its start; the decoding runs about 150 times faster than the sound
// plays, so the part being played is always there).

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

    /// <summary>
    /// The audio stream as a RIFF WAVE of its full length (NLayer's sample count, from the frame
    /// headers) with the first <paramref name="seconds"/> decoded; <paramref name="rest"/> decodes
    /// the others into it (the same bytes as <see cref="ToWave"/>). Null when there is no sound;
    /// a stream whose length is not known is decoded at once (<paramref name="rest"/> done).
    /// </summary>
    public static byte[]? StartWave(byte[] audio, double seconds, out Task rest)
    {
        rest = Task.CompletedTask;
        if (audio.Length == 0)
            return null;
        MpegFile? file = null;
        try
        {
            file = new MpegFile(new MemoryStream(audio, writable: false));
            int channels = file.Channels, rate = file.SampleRate;
            long total = file.Length / sizeof(float);
            if (channels is < 1 or > 2 || rate <= 0 || total <= 0 || total > int.MaxValue / 2 - 44)
            {
                file.Dispose();
                return ToWave(audio);
            }
            var wave = Header(new byte[44 + total * 2], channels, rate);
            int at = 44;
            Decode(file, wave, ref at, 44 + (long)(seconds * rate) * channels * 2);
            if (at >= wave.Length)
            {
                file.Dispose();
                return wave;
            }
            var reader = file;
            file = null;
            rest = Task.Run(() =>
            {
                using (reader)
                {
                    try
                    {
                        Decode(reader, wave, ref at, wave.Length);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
                    {
                        // the rest stays silent
                    }
                }
            });
            return wave;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            file?.Dispose();
            return null;
        }
    }

    /// <summary>Samples of <paramref name="file"/> into <paramref name="wave"/> from byte <paramref name="at"/> until byte <paramref name="end"/> (or the file's end).</summary>
    private static void Decode(MpegFile file, byte[] wave, ref int at, long end)
    {
        end = Math.Min(end, wave.Length);
        var samples = new float[4096 * file.Channels];
        while (at < end)
        {
            int n = file.ReadSamples(samples, 0, (int)Math.Min(samples.Length, (end - at) / 2));
            if (n <= 0)
                break;
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
                wave[at] = (byte)s;
                wave[at + 1] = (byte)(s >> 8);
                at += 2;
            }
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
        var wave = Header(new byte[44 + pcm.Length], channels, rate);
        pcm.CopyTo(wave, 44);
        return wave;
    }

    /// <summary>The 44-byte header of a 16-bit PCM WAVE into <paramref name="wave"/> (its data the rest of the array).</summary>
    private static byte[] Header(byte[] wave, int channels, int rate)
    {
        int pcmLength = wave.Length - 44;
        void Text(int at, string s) { for (int i = 0; i < 4; i++) wave[at + i] = (byte)s[i]; }
        Text(0, "RIFF");
        BitConverter.TryWriteBytes(wave.AsSpan(4), 36 + pcmLength);
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
        BitConverter.TryWriteBytes(wave.AsSpan(40), pcmLength);
        return wave;
    }
}
