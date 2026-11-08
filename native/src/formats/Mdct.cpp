// C# Formats/Mdct.cs
#include "formats/Mdct.h"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace openshiina::formats {

Mdct::Mdct(int bits, double scale)
{
    int n = 1 << bits, n4 = n >> 2;
    m_n = n;
    m_cos.resize(size_t(n4));
    m_sin.resize(size_t(n4));
    double theta = 1.0 / 8 + (scale < 0 ? n4 : 0);
    scale = std::sqrt(std::abs(scale));
    for (int i = 0; i < n4; i++)
    {
        double alpha = 2 * std::numbers::pi * (i + theta) / n;
        m_cos[size_t(i)] = float(-std::cos(alpha) * scale);
        m_sin[size_t(i)] = float(-std::sin(alpha) * scale);
    }
    m_re.resize(size_t(n4));
    m_im.resize(size_t(n4));
    int fftBits = bits - 2;
    m_reverse.resize(size_t(n4));
    for (int i = 0; i < n4; i++)
    {
        int r = 0;
        for (int b = 0; b < fftBits; b++)
            if ((i & (1 << b)) != 0)
                r |= 1 << (fftBits - 1 - b);
        m_reverse[size_t(i)] = r;
    }
    m_twRe.resize(size_t(std::max(1, n4 / 2)));
    m_twIm.resize(size_t(std::max(1, n4 / 2)));
    for (int i = 0; i < n4 / 2; i++)
    {
        // the inverse transform: e^(+2 pi i k / n4)
        m_twRe[size_t(i)] = float(std::cos(2 * std::numbers::pi * i / n4));
        m_twIm[size_t(i)] = float(std::sin(2 * std::numbers::pi * i / n4));
    }
}

void Mdct::Inverse(const float* input, float* output)
{
    int n = m_n, n2 = n >> 1, n4 = n >> 2;
    InverseHalf(input, output + n4);
    for (int k = 0; k < n4; k++)
    {
        output[k] = -output[n2 - k - 1];
        output[n - k - 1] = output[n2 + k];
    }
}

void Mdct::InverseHalf(const float* input, float* output)
{
    int n = m_n, n2 = n >> 1, n4 = n >> 2, n8 = n >> 3;
    float* re = m_re.data();
    float* im = m_im.data();
    const float* cosine = m_cos.data();
    const float* sine = m_sin.data();
    // pre rotation (into bit-reversed order for the FFT)
    for (int k = 0; k < n4; k++)
    {
        float are = input[n2 - 1 - 2 * k], aim = input[2 * k];
        int j = m_reverse[size_t(k)];
        re[j] = are * cosine[k] - aim * sine[k];
        im[j] = are * sine[k] + aim * cosine[k];
    }
    // radix-2 inverse FFT
    for (int size = 2; size <= n4; size <<= 1)
    {
        int half = size >> 1, step = n4 / size;
        for (int start = 0; start < n4; start += size)
            for (int k = 0; k < half; k++)
            {
                float wr = m_twRe[size_t(k * step)], wi = m_twIm[size_t(k * step)];
                int a = start + k, b = a + half;
                float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                re[b] = re[a] - tr;
                im[b] = im[a] - ti;
                re[a] += tr;
                im[a] += ti;
            }
    }
    // post rotation and reordering
    for (int k = 0; k < n8; k++)
    {
        int a = n8 - k - 1, b = n8 + k;
        float r0 = im[a] * sine[a] - re[a] * cosine[a];
        float i1 = im[a] * cosine[a] + re[a] * sine[a];
        float r1 = im[b] * sine[b] - re[b] * cosine[b];
        float i0 = im[b] * cosine[b] + re[b] * sine[b];
        re[a] = r0;
        im[a] = i0;
        re[b] = r1;
        im[b] = i1;
    }
    for (int k = 0; k < n4; k++)
    {
        output[2 * k] = re[k];
        output[2 * k + 1] = im[k];
    }
}

} // namespace openshiina::formats
