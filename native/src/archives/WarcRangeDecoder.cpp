#include "archives/WarcRangeDecoder.h"

#include <algorithm>
#include <utility>

#include "io/Binary.h"

namespace openshiina::archives::WarcRangeDecoder {

namespace {

constexpr int TotalBits = 12;
constexpr uint32_t Total = 1u << TotalBits;
constexpr int Symbols = 257;
constexpr int EndMark = 256;
constexpr int MostSymbolsBetweenRescales = 2000;

// Range coder of 32 bits: the bits of each new byte that go below the code value
constexpr int ExtraBits = 7;
constexpr uint32_t Bottom = 1u << 23;

/// The decoder's state: the code value against the low end, the range, the scale of the last target.
class Coder
{
public:
    Coder(const uint8_t* input, size_t length) : m_input(input), m_length(length) {}

    bool InputUsedUp() const { return m_position >= m_length; }

    /// The first byte is the encoder's carry byte (unused); the next starts the code value.
    void Start()
    {
        uint8_t unused;
        Take(unused);
        Take(m_pending);
        m_value = uint32_t(m_pending) >> (8 - ExtraBits);
        m_range = 1u << ExtraBits;
    }

    /// Shifts in bytes while the range is small; stops where the input ends.
    void Renormalise()
    {
        while (m_range <= Bottom)
        {
            m_value = (m_value << 8) | uint8_t(m_pending << ExtraBits);
            if (!Take(m_pending))
                return;
            m_value |= uint32_t(m_pending) >> (8 - ExtraBits);
            m_range <<= 8;
        }
    }

    /// Where the code value falls among the model's totals (0 to Total - 1).
    uint32_t Target()
    {
        Renormalise();
        m_scale = m_range >> TotalBits;
        // A scale of 0 comes of bad input: the C# player's DivideByZeroException
        if (m_scale == 0)
            throw InvalidDataError("Range coder scale of 0");
        uint32_t at = m_value / m_scale;
        return (at >> TotalBits) != 0 ? Total - 1 : at;
    }

    /// The range becomes the symbol's part of it: totals from start, size of them.
    void Narrow(uint32_t start, uint32_t size)
    {
        uint32_t below = m_scale * start;
        m_value -= below;
        if (start + size < Total)
            m_range = m_scale * size;
        else
            m_range -= below;
    }

private:
    /// The next byte, or false and 0 past the end.
    bool Take(uint8_t& value)
    {
        if (m_position >= m_length)
        {
            value = 0;
            return false;
        }
        value = m_input[m_position++];
        return true;
    }

    const uint8_t* m_input;
    size_t m_length;
    size_t m_position = 0;
    uint32_t m_value = 0, m_range = 0, m_scale = 0;
    uint8_t m_pending = 0;
};

/// The quasistatic model: cumulative counts (fixed between rescales) and the counts that grow
/// meanwhile; at a rescale the grown counts become the cumulative ones and are halved.
class FrequencyModel
{
public:
    FrequencyModel()
    {
        m_from[Symbols] = Total;
        // As even as the total allows: the first (Total mod Symbols) one more
        uint32_t each = Total / Symbols, extra = Total % Symbols;
        for (int s = 0; s < Symbols; s++)
            m_growing[s] = each + (uint32_t(s) < extra ? 1u : 0u);
        Rescale();
    }

    int SymbolAt(uint32_t target) const
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

    std::pair<uint32_t, uint32_t> Interval(int symbol) const { return {m_from[symbol], m_from[symbol + 1] - m_from[symbol]}; }

    void Count(int symbol)
    {
        if (m_leftUntilRescale <= 0)
            Rescale();
        m_leftUntilRescale--;
        m_growing[symbol] += m_step;
    }

private:
    void Rescale()
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
            m_interval = std::min(m_interval * 2, MostSymbolsBetweenRescales);
        // The grown counts become the cumulative totals, then are halved (at least 1 each)
        uint32_t cumulative = Total, unused = Total;
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
        m_step = unused / uint32_t(m_interval);
        m_stillToSpread = int(unused % uint32_t(m_interval));
        m_leftUntilRescale = m_interval - m_stillToSpread;
    }

    // m_from[s] .. m_from[s + 1]: the totals of symbol s; m_growing[s]: its count meanwhile
    uint32_t m_from[Symbols + 1] = {};
    uint32_t m_growing[Symbols] = {};
    int m_interval = Symbols >> 4 | 2;
    int m_leftUntilRescale = 0, m_stillToSpread = 0;
    uint32_t m_step = 0;
};

} // namespace

size_t Decode(const uint8_t* input, size_t inputLength, uint8_t* output, size_t outputLength)
{
    Coder state(input, inputLength);
    FrequencyModel model;
    state.Start();
    size_t written = 0;
    while (!state.InputUsedUp())
    {
        uint32_t target = state.Target();
        int symbol = model.SymbolAt(target);
        if (symbol == EndMark)
            break;
        if (written >= outputLength)
            return 0;
        output[written++] = uint8_t(symbol);
        auto [start, size] = model.Interval(symbol);
        state.Narrow(start, size);
        model.Count(symbol);
    }
    auto [endStart, endSize] = model.Interval(EndMark);
    state.Narrow(endStart, endSize);
    state.Renormalise();
    return written;
}

} // namespace openshiina::archives::WarcRangeDecoder
