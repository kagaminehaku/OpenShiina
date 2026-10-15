// The sound of a Windows Media movie (AsfFile) as a 16-bit RIFF WAVE, as MpegAudio gives that of an
// MPEG-1 one: the WAVE of the file's play length with its first seconds decoded at once and the
// rest decoded into it on another thread (WMA decodes about 250 times faster than it plays).

namespace OpenShiina.Formats;

/// <summary>A WMA decoder: packets of block_align bytes into float samples per channel.</summary>
internal interface IWmaDecoder
{
    int Channels { get; }
    int SampleRate { get; }
    /// <summary>Samples put out before the stream's first ones.</summary>
    int Delay { get; }
    void DecodePacket(ReadOnlySpan<byte> packet, List<float>[] output);
}

public static class AsfAudio
{
    /// <summary>The decoder of a WMA 1 / 2 / Pro stream, or null for another format.</summary>
    internal static IWmaDecoder? Decoder(AsfFile.Stream audio) => audio.FormatTag switch
    {
        0x160 or 0x161 => new WmaAudio(audio.FormatTag, audio.Channels, audio.SampleRate, audio.BytesPerSecond, audio.BlockAlign, audio.Extra),
        0x162 => new WmaProAudio(audio.Channels, audio.SampleRate, audio.BlockAlign, audio.Extra),
        _ => null,
    };

    /// <summary>
    /// The audio stream as a WAVE of the file's play length with the first
    /// <paramref name="seconds"/> decoded; <paramref name="rest"/> decodes the others into it.
    /// Null when there is no sound or it cannot be decoded.
    /// </summary>
    public static byte[]? StartWave(AsfFile file, double seconds, out Task rest)
    {
        rest = Task.CompletedTask;
        if (file.Audio is not { } audio || audio.Objects.Count == 0)
            return null;
        IWmaDecoder? decoder;
        try
        {
            decoder = Decoder(audio);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        if (decoder == null || decoder.Channels is < 1 or > 2)
            return null;
        int channels = decoder.Channels, rate = decoder.SampleRate;
        long frames = file.Duration * rate / 1000;
        if (frames <= 0 || frames > (int.MaxValue - 44) / (2 * channels))
            return null;
        var wave = MpegAudio.Header(new byte[44 + frames * channels * 2], channels, rate);
        var writer = new Writer(decoder, audio, wave);
        writer.Run(44 + (long)(seconds * rate) * channels * 2);
        if (!writer.Done)
            rest = Task.Run(() => writer.Run(wave.Length));
        return wave;
    }

    /// <summary>Decodes packets into the WAVE until a byte position is reached.</summary>
    private sealed class Writer(IWmaDecoder decoder, AsfFile.Stream audio, byte[] wave)
    {
        private readonly List<float>[] m_samples = Enumerable.Range(0, decoder.Channels).Select(_ => new List<float>()).ToArray();
        private int m_packet, m_at = 44, m_skip = decoder.Delay;

        public bool Done => m_at >= wave.Length || m_packet >= audio.Objects.Count;

        public void Run(long end)
        {
            end = Math.Min(end, wave.Length);
            int channels = decoder.Channels;
            while (m_at < end && m_packet < audio.Objects.Count)
            {
                var (start, length, _, _) = audio.Objects[m_packet++];
                foreach (var list in m_samples)
                    list.Clear();
                try
                {
                    decoder.DecodePacket(audio.Data.AsSpan(start, length), m_samples);
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
                {
                    // a broken packet: its frames stay out (FFmpeg goes on in the same way)
                }
                int count = m_samples[0].Count;
                int from = Math.Min(m_skip, count);
                m_skip -= from;
                for (int i = from; i < count && m_at < wave.Length; i++)
                    for (int c = 0; c < channels; c++)
                    {
                        short s = (short)Math.Clamp((int)MathF.Round(m_samples[c][i] * 32767f), short.MinValue, short.MaxValue);
                        wave[m_at] = (byte)s;
                        wave[m_at + 1] = (byte)(s >> 8);
                        m_at += 2;
                    }
            }
        }
    }
}
