// C# Formats/Mdct.cs: the inverse MDCT of the WMA decoders (FFmpeg's ff_imdct_calc / ff_imdct_half
// of mdct_template.c): N coefficients to 2N samples through an N/4-point complex inverse FFT.
#pragma once

#include <vector>

namespace openshiina::formats {

class Mdct
{
public:
    /// bits: log2 of the number of samples out (twice the coefficients).
    Mdct(int bits, double scale);

    /// ff_imdct_calc: N = 2^(bits-1) coefficients into 2N samples.
    void Inverse(const float* input, float* output);
    /// ff_imdct_half: the middle N samples.
    void InverseHalf(const float* input, float* output);

private:
    int m_n; // samples out (2N)
    std::vector<float> m_cos, m_sin;
    std::vector<float> m_re, m_im;
    std::vector<int> m_reverse;
    std::vector<float> m_twRe, m_twIm;
};

} // namespace openshiina::formats
