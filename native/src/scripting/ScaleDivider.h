// What the zooms share (C# ScnVm.Scale32.cs): the reciprocal division of the x86 code and the entry
// of a row or column table.
#pragma once

#include <bit>
#include <cstdint>

namespace openshiina::scripting {

/// An entry of a scale table: flags (0x80 a partly covered first source pixel, 0x40 a partly
/// covered last one), the count of whole source pixels between, the weights of the two partial ones.
struct ScaleEntry
{
    int Flags, Count;
    uint32_t First, Last;
};

/// The reciprocal division of the x86 code: low32(x * K) / S, rounded as it rounds.
class ScaleDivider
{
public:
    ScaleDivider(uint32_t s, uint32_t k) : m_k(k)
    {
        m_shift = 31 - std::countl_zero(s);
        if (s == 1u << m_shift)
        {
            m_bias = -1;
            return;
        }
        uint64_t dividend = (uint64_t(1) << m_shift) << 32;
        uint64_t q = dividend / s, r = dividend % s;
        m_bias = r <= s >> 1 ? 1 : 0;
        m_multiplier = uint32_t(q) + uint32_t(m_bias ^ 1);
    }

    uint32_t Divide(uint32_t x) const
    {
        uint32_t t = x * m_k;
        if (m_bias >= 0)
        {
            uint32_t biased = t + uint32_t(m_bias);
            t = biased < t ? m_multiplier : uint32_t(uint64_t(biased) * m_multiplier >> 32);
        }
        return t >> m_shift;
    }

private:
    uint32_t m_k, m_multiplier = 0;
    int m_shift, m_bias = 0;
};

} // namespace openshiina::scripting
