// The index of WARC 1.2-1.6 archives is packed with a range coder: Michael Schindler's (32-bit
// arithmetic, renormalised a byte at a time, seven bits of the first byte to start with) over
// 257 symbols - the 256 byte values and an end mark - whose frequencies a quasistatic model
// adapts as the data goes: totals of 4096, each symbol's count raised by a step after it is
// coded, and every so often (36 symbols at first, twice as many each time, up to every 2000) the
// counts halved and the steps worked out again. Written for OpenShiina from that description of
// the method; checked against GARbro's decoder run as a program on random input (the outputs of
// every case the same).

namespace OpenShiina.Archives;

public static class WarcRangeDecoder
{
    private const int TotalBits = 12;
    private const uint Total = 1u << TotalBits;
    private const int Symbols = 257;
    private const int EndMark = 256;
    private const int MostSymbolsBetweenRescales = 2000;

    // Range coder of 32 bits: the bits of each new byte that go below the code value
    private const int ExtraBits = 7;
    private const uint Bottom = 1u << 23;

    /// <summary>
    /// Unpacks <paramref name="input"/> into <paramref name="output"/> up to the end mark (or the
    /// end of the input); the bytes written, 0 when they do not fit.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var state = new Coder(input);
        var model = new FrequencyModel();
        state.Start();
        int written = 0;
        while (!state.InputUsedUp)
        {
            uint target = state.Target();
            int symbol = model.SymbolAt(target);
            if (symbol == EndMark)
                break;
            if (written >= output.Length)
                return 0;
            output[written++] = (byte)symbol;
            var (start, size) = model.Interval(symbol);
            state.Narrow(start, size);
            model.Count(symbol);
        }
        var (endStart, endSize) = model.Interval(EndMark);
        state.Narrow(endStart, endSize);
        state.Renormalise();
        return written;
    }

    /// <summary>The decoder's state: the code value against the low end, the range, the scale of the last target.</summary>
    private ref struct Coder(ReadOnlySpan<byte> input)
    {
        private readonly ReadOnlySpan<byte> m_input = input;
        private int m_position;
        private uint m_value, m_range, m_scale;
        private byte m_pending;

        public readonly bool InputUsedUp => m_position >= m_input.Length;

        /// <summary>The next byte, or false and 0 past the end.</summary>
        private bool Take(out byte value)
        {
            if (m_position >= m_input.Length)
            {
                value = 0;
                return false;
            }
            value = m_input[m_position++];
            return true;
        }

        /// <summary>The first byte is the encoder's carry byte (unused); the next starts the code value.</summary>
        public void Start()
        {
            Take(out _);
            Take(out m_pending);
            m_value = (uint)m_pending >> (8 - ExtraBits);
            m_range = 1u << ExtraBits;
        }

        /// <summary>Shifts in bytes while the range is small; stops where the input ends.</summary>
        public void Renormalise()
        {
            while (m_range <= Bottom)
            {
                m_value = (m_value << 8) | (byte)(m_pending << ExtraBits);
                if (!Take(out m_pending))
                    return;
                m_value |= (uint)m_pending >> (8 - ExtraBits);
                m_range <<= 8;
            }
        }

        /// <summary>Where the code value falls among the model's totals (0 to Total - 1).</summary>
        public uint Target()
        {
            Renormalise();
            m_scale = m_range >> TotalBits;
            uint at = m_value / m_scale;
            return (at >> TotalBits) != 0 ? Total - 1 : at;
        }

        /// <summary>The range becomes the symbol's part of it: totals from <paramref name="start"/>, <paramref name="size"/> of them.</summary>
        public void Narrow(uint start, uint size)
        {
            uint below = m_scale * start;
            m_value -= below;
            if (start + size < Total)
                m_range = m_scale * size;
            else
                m_range -= below;
        }
    }

    /// <summary>
    /// The quasistatic model: cumulative counts (fixed between rescales) and the counts that grow
    /// meanwhile; at a rescale the grown counts become the cumulative ones and are halved.
    /// </summary>
    private sealed class FrequencyModel
    {
        // m_from[s] .. m_from[s + 1]: the totals of symbol s; m_growing[s]: its count meanwhile
        private readonly uint[] m_from = new uint[Symbols + 1];
        private readonly uint[] m_growing = new uint[Symbols];
        private int m_interval = Symbols >> 4 | 2;
        private int m_leftUntilRescale, m_stillToSpread;
        private uint m_step;

        public FrequencyModel()
        {
            m_from[Symbols] = Total;
            // As even as the total allows: the first (Total mod Symbols) one more
            uint each = Total / Symbols, extra = Total % Symbols;
            for (int s = 0; s < Symbols; s++)
                m_growing[s] = each + (s < extra ? 1u : 0u);
            Rescale();
        }

        public int SymbolAt(uint target)
        {
            // The last symbol whose totals start at or below the target
            int low = 0, high = Symbols;
            while (high - low > 1)
            {
                int middle = (low + high) >> 1;
                if (target < m_from[middle])
                    high = middle;
                else
                    low = middle;
            }
            return low;
        }

        public (uint Start, uint Size) Interval(int symbol) => (m_from[symbol], m_from[symbol + 1] - m_from[symbol]);

        public void Count(int symbol)
        {
            if (m_leftUntilRescale <= 0)
                Rescale();
            m_leftUntilRescale--;
            m_growing[symbol] += m_step;
        }

        private void Rescale()
        {
            // What the step could not share out evenly is spread over a last stretch, one more each
            if (m_stillToSpread != 0)
            {
                m_step++;
                m_leftUntilRescale = m_stillToSpread;
                m_stillToSpread = 0;
                return;
            }
            if (m_interval < MostSymbolsBetweenRescales)
                m_interval = Math.Min(m_interval * 2, MostSymbolsBetweenRescales);
            // The grown counts become the cumulative totals, then are halved (at least 1 each)
            uint cumulative = Total, unused = Total;
            for (int s = Symbols - 1; s > 0; s--)
            {
                cumulative -= m_growing[s];
                m_from[s] = cumulative;
                m_growing[s] = m_growing[s] >> 1 | 1;
                unused -= m_growing[s];
            }
            m_growing[0] = m_growing[0] >> 1 | 1;
            unused -= m_growing[0];
            // The totals the halved counts leave are shared out over the next interval
            m_step = unused / (uint)m_interval;
            m_stillToSpread = (int)(unused % (uint)m_interval);
            m_leftUntilRescale = m_interval - m_stillToSpread;
        }
    }
}
