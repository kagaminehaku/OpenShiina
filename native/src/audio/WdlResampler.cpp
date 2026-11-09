// NAudio's Dsp/WdlResampler.cs (see WdlResampler.h)
#include "audio/WdlResampler.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace openshiina::audio {

namespace {

constexpr double Pi = 3.1415926535897932384626433832795;

} // namespace

WdlResampler::WdlResampler(double rateIn, double rateOut)
{
    rateIn = std::max(rateIn, 1.0);
    rateOut = std::max(rateOut, 1.0);
    m_ratio = rateIn / rateOut;
}

int WdlResampler::ResamplePrepare(int frames, int channels, float*& input)
{
    int wanted = std::max(int(m_ratio * frames) + 4 - m_samplesInRsinbuf, 0);
    m_rsinbuf.resize(size_t(m_samplesInRsinbuf + wanted) * size_t(channels));
    input = m_rsinbuf.data() + size_t(m_samplesInRsinbuf) * size_t(channels);
    m_lastRequested = wanted;
    return wanted;
}

int WdlResampler::ResampleOut(float* output, int framesIn, int frames, int channels)
{
    // Downsampling: the new input low-passed first (twice per channel)
    if (m_ratio > 1.0 && framesIn > 0)
    {
        m_iirfilter.SetParms((1.0 / m_ratio) * m_filterpos, m_filterq);
        int at = m_samplesInRsinbuf * channels, w = 0;
        for (int x = 0; x < channels; x++)
            for (int a = 0; a < FilterCount; a++)
                m_iirfilter.Apply(m_rsinbuf.data(), at + x, m_rsinbuf.data(), at + x, framesIn, channels, w++);
    }
    m_samplesInRsinbuf += std::min(framesIn, m_lastRequested);
    int available = m_samplesInRsinbuf;
    // Less input than asked for: the end, flushed with silence
    if (framesIn < m_lastRequested)
    {
        int pad = (m_lastRequested - framesIn) * 2;
        m_rsinbuf.resize(size_t(m_samplesInRsinbuf + pad) * size_t(channels));
        std::fill(m_rsinbuf.begin() + ptrdiff_t(m_samplesInRsinbuf) * channels, m_rsinbuf.end(), 0.0f);
        available = m_samplesInRsinbuf + pad;
    }

    int made = 0;
    double srcpos = m_fracpos, step = m_ratio;
    float* out = output;
    for (int n = frames; n-- != 0;)
    {
        int ipos = int(srcpos);
        double fracpos = srcpos - ipos;
        if (ipos >= available - 1)
            break;
        double ifracpos = 1.0 - fracpos;
        const float* in = m_rsinbuf.data() + size_t(ipos) * size_t(channels);
        for (int c = 0; c < channels; c++)
            *out++ = float(double(in[c]) * ifracpos + double(in[c + channels]) * fracpos);
        srcpos += step;
        made++;
    }

    // Upsampling: the output low-passed after
    if (m_ratio < 1.0 && made > 0)
    {
        m_iirfilter.SetParms(m_ratio * m_filterpos, m_filterq);
        int w = 0;
        for (int x = 0; x < channels; x++)
            for (int a = 0; a < FilterCount; a++)
                m_iirfilter.Apply(output, x, output, x, made, channels, w++);
    }

    // What came from the silence padded on is not given
    if (made > 0 && available > m_samplesInRsinbuf)
    {
        double adj = (srcpos - m_samplesInRsinbuf) / step;
        if (adj > 0)
            made = std::max(made - int(adj + 0.5), 0);
    }

    int isrcpos = int(srcpos);
    m_fracpos = srcpos - isrcpos;
    m_samplesInRsinbuf -= isrcpos;
    if (m_samplesInRsinbuf <= 0)
        m_samplesInRsinbuf = 0;
    else
        std::memmove(m_rsinbuf.data(), m_rsinbuf.data() + size_t(isrcpos) * size_t(channels),
                     sizeof(float) * size_t(m_samplesInRsinbuf) * size_t(channels));
    return made;
}

void WdlResampler::IirFilter::SetParms(double fpos, double q)
{
    if (std::abs(fpos - m_fpos) < 0.000001)
        return;
    m_fpos = fpos;
    double pos = fpos * Pi;
    double cpos = std::cos(pos);
    double spos = std::sin(pos);
    double alpha = spos / (2.0 * q);
    double sc = 1.0 / (1 + alpha);
    m_b1 = (1 - cpos) * sc;
    m_b2 = m_b0 = m_b1 * 0.5;
    m_a1 = -2 * cpos * sc;
    m_a2 = (1 - alpha) * sc;
}

void WdlResampler::IirFilter::Apply(float* in, int inIndex, float* out, int outIndex, int count, int span, int w)
{
    double b0 = m_b0, b1 = m_b1, b2 = m_b2, a1 = m_a1, a2 = m_a2;
    double* h = m_hist.data() + size_t(w) * 4;
    while (count-- != 0)
    {
        double inx = in[inIndex];
        inIndex += span;
        double outx = inx * b0 + h[0] * b1 + h[1] * b2 - h[2] * a1 - h[3] * a2;
        h[1] = h[0];
        h[0] = inx;
        h[3] = h[2];
        h[2] = outx;
        out[outIndex] = float(h[2]);
        outIndex += span;
    }
}

} // namespace openshiina::audio
