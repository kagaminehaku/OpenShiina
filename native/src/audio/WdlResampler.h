// NAudio's WdlResampler (NAudio.Core 2.2.1, Dsp/WdlResampler.cs), which is Cockos WDL's resampler
// ported to C#, as its WdlResamplingSampleProvider uses it: output driven, linear interpolation and
// two IIR low-pass filters (SetMode(true, 2, false)), the C# player's resampling of a sound that is
// not at 44.1 kHz. The same arithmetic in the same order (doubles where the C# has them), so the
// samples are the C# player's. The sinc mode, which the provider never turns on, is left out.
//
// WDL's license: Copyright (C) 2005 and later Cockos Incorporated. This software is provided
// 'as-is', without any express or implied warranty. Permission is granted to anyone to use it for
// any purpose and to alter and redistribute it, provided that its origin is not misrepresented,
// altered versions are plainly marked as such, and this notice stays in any source distribution.
// Altered: C++ again, from NAudio's C#; only what WdlResamplingSampleProvider uses.
#pragma once

#include <vector>

namespace openshiina::audio {

class WdlResampler
{
public:
    /// The provider's settings: interp, filtercnt 2, no sinc; filterpos 0.693, Q 0.707; output driven.
    WdlResampler(double rateIn, double rateOut);

    /// How many input frames to put at the returned place for frames output frames.
    int ResamplePrepare(int frames, int channels, float*& input);

    /// Up to frames output frames into output from framesIn frames put in after ResamplePrepare
    /// (fewer than asked flushes what is left); how many came out.
    int ResampleOut(float* output, int framesIn, int frames, int channels);

private:
    class IirFilter
    {
    public:
        void SetParms(double fpos, double q);
        void Apply(float* in, int inIndex, float* out, int outIndex, int count, int span, int w);

    private:
        double m_fpos = -1;
        double m_a1 = 0, m_a2 = 0, m_b0 = 0, m_b1 = 0, m_b2 = 0;
        // 4 filters x 64 channels, 4 values of history each
        std::vector<double> m_hist = std::vector<double>(4 * 64 * 4);
    };

    double m_fracpos = 0, m_ratio = 1;
    float m_filterq = 0.707f, m_filterpos = 0.693f;
    std::vector<float> m_rsinbuf;
    IirFilter m_iirfilter;
    int m_lastRequested = 0, m_samplesInRsinbuf = 0;
    static constexpr int FilterCount = 2;
};

} // namespace openshiina::audio
