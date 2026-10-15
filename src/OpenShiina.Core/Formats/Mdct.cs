// The inverse MDCT of the WMA decoders (FFmpeg's ff_imdct_calc / ff_imdct_half of
// mdct_template.c): N coefficients to 2N samples through an N/4-point complex inverse FFT.

namespace OpenShiina.Formats;

internal sealed class Mdct
{
    private readonly int m_n; // samples out (2N)
    private readonly float[] m_cos, m_sin;
    private readonly float[] m_re, m_im;
    private readonly int[] m_reverse;
    private readonly float[] m_twRe, m_twIm;

    /// <param name="bits">log2 of the number of samples out (twice the coefficients).</param>
    public Mdct(int bits, double scale)
    {
        int n = 1 << bits, n4 = n >> 2;
        m_n = n;
        m_cos = new float[n4];
        m_sin = new float[n4];
        double theta = 1.0 / 8 + (scale < 0 ? n4 : 0);
        scale = Math.Sqrt(Math.Abs(scale));
        for (int i = 0; i < n4; i++)
        {
            double alpha = 2 * Math.PI * (i + theta) / n;
            m_cos[i] = (float)(-Math.Cos(alpha) * scale);
            m_sin[i] = (float)(-Math.Sin(alpha) * scale);
        }
        m_re = new float[n4];
        m_im = new float[n4];
        int fftBits = bits - 2;
        m_reverse = new int[n4];
        for (int i = 0; i < n4; i++)
        {
            int r = 0;
            for (int b = 0; b < fftBits; b++)
                if ((i & (1 << b)) != 0)
                    r |= 1 << (fftBits - 1 - b);
            m_reverse[i] = r;
        }
        m_twRe = new float[Math.Max(1, n4 / 2)];
        m_twIm = new float[Math.Max(1, n4 / 2)];
        for (int i = 0; i < n4 / 2; i++)
        {
            // the inverse transform: e^(+2 pi i k / n4)
            m_twRe[i] = (float)Math.Cos(2 * Math.PI * i / n4);
            m_twIm[i] = (float)Math.Sin(2 * Math.PI * i / n4);
        }
    }

    /// <summary>ff_imdct_calc: N = 2^(bits-1) coefficients into 2N samples.</summary>
    public void Inverse(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = m_n, n2 = n >> 1, n4 = n >> 2;
        InverseHalf(input, output.Slice(n4, n2));
        for (int k = 0; k < n4; k++)
        {
            output[k] = -output[n2 - k - 1];
            output[n - k - 1] = output[n2 + k];
        }
    }

    /// <summary>ff_imdct_half: the middle N samples.</summary>
    public void InverseHalf(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = m_n, n2 = n >> 1, n4 = n >> 2, n8 = n >> 3;
        var re = m_re;
        var im = m_im;
        // pre rotation (into bit-reversed order for the FFT)
        for (int k = 0; k < n4; k++)
        {
            float are = input[n2 - 1 - 2 * k], aim = input[2 * k];
            int j = m_reverse[k];
            re[j] = are * m_cos[k] - aim * m_sin[k];
            im[j] = are * m_sin[k] + aim * m_cos[k];
        }
        // radix-2 inverse FFT
        for (int size = 2; size <= n4; size <<= 1)
        {
            int half = size >> 1, step = n4 / size;
            for (int start = 0; start < n4; start += size)
                for (int k = 0; k < half; k++)
                {
                    float wr = m_twRe[k * step], wi = m_twIm[k * step];
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
            float r0 = im[a] * m_sin[a] - re[a] * m_cos[a];
            float i1 = im[a] * m_cos[a] + re[a] * m_sin[a];
            float r1 = im[b] * m_sin[b] - re[b] * m_cos[b];
            float i0 = im[b] * m_cos[b] + re[b] * m_sin[b];
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
}
