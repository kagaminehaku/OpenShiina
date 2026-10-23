// RIOSUB.DLL's colour functions (The Pillagers of Raillore, the DLL of 2018-12-03), written out from
// its x86 code: rsS25ChangeHSL (RVA 1210), rsS25ChangeColorBalance (18A0), rsS25ChangeCBHSL (1EE0)
// and their HSL to RGB step (1020). They change bytes 1-3 of each 4-byte pixel of a picture in
// place (pixels, width, height, row stride first); the arithmetic is the DLL's in doubles (SSE2:
// each operation rounded, in its order) and the truncation of cvttsd2si, so the bytes are the DLL's.
// CBHSL has three builds for three sets of its arguments; they compute what its general one does.

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private const double C333 = 0.333, Half = 0.5, Seven = 0.7;

    /// <summary>x below 0 is 0, above 1 is 1 (comisd then minsd: a NaN stays).</summary>
    private static double Unit(double x) => 0 > x ? 0 : 1 < x ? 1 : x;

    /// <summary>An argument in percent as the DLL takes it: v / 100 within -1 to 1.</summary>
    private static double Percent(int v)
    {
        double x = v / 100.0;
        return -1.0 > x ? -1.0 : 1.0 < x ? 1.0 : x;
    }

    /// <summary>The largest and smallest of the three as the DLL picks them (a / b / c: bytes 3 / 2 / 1).</summary>
    private static (double Max, double Min) MaxMin(double a, double b, double c)
    {
        double max = a > b ? a : b;
        max = max > c ? max : c;
        double min = b > a ? a : b;
        min = min < c ? min : c;
        return (max, min);
    }

    /// <summary>Hue and saturation of (a, b, c) (bytes 3 / 2 / 1, from 0 to 1); light from (max + min) / 2.</summary>
    private static (double Hue, double Saturation) HueSaturation(double a, double b, double c, double max, double min)
    {
        if (max == min)
            return (-1.0, 0.0);
        double d = max - min, s = min + max;
        if (Half < s * Half)
            s = 2.0 - max - min;
        double saturation = d / s;
        if (d == 0.0)
            d = 1.0;
        double hue = a == max ? (b - c) / d : b == max ? (c - a) / d + 2.0 : (a - b) / d + 4.0;
        hue /= 6.0;
        if (hue < 0.0)
            hue += 1.0;
        return (hue, saturation);
    }

    /// <summary>FUN_10001020: HSL to the three channels (bytes 3 / 2 / 1).</summary>
    private static (double A, double B, double C) FromHsl(double hue, double saturation, double light)
    {
        if (saturation == 0.0)
            return (light, light, light);
        double q = Half < light ? light + saturation - light * saturation : (saturation + 1.0) * light;
        double h6 = hue * 6.0, p = light + light - q;
        double Channel(double t)
        {
            if (t <= 6.0)
            {
                if (t < 0.0)
                    t += 6.0;
            }
            else
                t -= 6.0;
            if (1.0 <= t)
                return 3.0 <= t ? t < 4.0 ? (q - p) * (4.0 - t) + p : p : q;
            return (q - p) * t + p;
        }
        return (Channel(h6 + 2.0), Channel(h6), Channel(h6 - 2.0));
    }

    /// <summary>A double the DLL stores as a byte: cvttsd2si, then its low byte.</summary>
    private static byte ToByte(double x) => (byte)(x >= -2147483648.0 && x < 2147483648.0 ? (int)x : int.MinValue);

    /// <summary>Each pixel of a picture: bytes 1-3 given and set as (byte 3, byte 2, byte 1).</summary>
    private void EachPixel(int pixels, int width, int height, int stride, Func<byte, byte, byte, (byte, byte, byte)> f)
    {
        if (width <= 0 || height <= 0)
            return;
        for (int y = 0; y < height; y++)
        {
            var row = Bytes(pixels + y * stride, width * 4);
            for (int x = 0; x < width * 4; x += 4)
            {
                var (b3, b2, b1) = f(row[x + 3], row[x + 2], row[x + 1]);
                row[x + 1] = b1;
                row[x + 2] = b2;
                row[x + 3] = b3;
            }
        }
    }

    /// <summary>rsS25ChangeHSL(pixels, width, height, stride, hue (not used), saturation, light), in percent.</summary>
    private void RioChangeHsl(int pixels, int width, int height, int stride, int saturation, int light)
    {
        double s = saturation / 100.0, l = light / 100.0;
        if (saturation == 0)
        {
            // Light alone in integers: towards 255 or towards 0 by light * 256 / 100 / 256
            int k = light * 256 / 100;
            if (light < 1)
                EachPixel(pixels, width, height, stride, (b3, b2, b1) => ((byte)(b3 * k / 256 + b3), (byte)(b2 * k / 256 + b2), (byte)(b1 * k / 256 + b1)));
            else
                EachPixel(pixels, width, height, stride, (b3, b2, b1) =>
                    ((byte)((255 - b3) * k / 256 + b3), (byte)((255 - b2) * k / 256 + b2), (byte)((255 - b1) * k / 256 + b1)));
            return;
        }
        EachPixel(pixels, width, height, stride, (b3, b2, b1) =>
        {
            double a = b3 / 255.0, b = b2 / 255.0, c = b1 / 255.0;
            var (max, min) = MaxMin(a, b, c);
            double lightness = (min + max) * Half;
            var (hue, sat) = HueSaturation(a, b, c, max, min);
            sat = (0 < saturation ? 1.0 - sat : sat) * s + sat;
            var (oa, ob, oc) = FromHsl(hue, sat, lightness);
            if (light == 0)
                return (ToByte(oa * 255.0), ToByte(ob * 255.0), ToByte(oc * 255.0));
            return (ToByte(((0 < light ? 1.0 - oa : oa) * l + oa) * 255.0), ToByte(((0 < light ? 1.0 - ob : ob) * l + ob) * 255.0),
                    ToByte(((0 < light ? 1.0 - oc : oc) * l + oc) * 255.0));
        });
    }

    /// <summary>
    /// The colour balance of one pixel (bytes 3 / 2 / 1 from 0 to 1) by red, green, blue (from
    /// -1 to 1): each channel plus w2 w1 0.7 its amount, w1 / w2 the weights of its light.
    /// </summary>
    private static (double A, double B, double C, double Light) Balance(double a, double b, double c, double r, double g, double bl)
    {
        var (max, min) = MaxMin(a, b, c);
        double light = (min + max) * Half;
        double w1 = (light - C333) * 4.0 + Half, w2 = Half - (light + C333 - 1.0) * 4.0;
        double weight = Unit(w2) * Unit(w1) * Seven;
        return (Unit(weight * r + a), Unit(weight * g + b), Unit(weight * bl + c), light);
    }

    /// <summary>rsS25ChangeColorBalance(pixels, width, height, stride, red, green, blue, mode): mode 1 as it is, the others keeping each pixel's light, 0 not at all.</summary>
    private void RioChangeColorBalance(int pixels, int width, int height, int stride, int red, int green, int blue, int mode)
    {
        if (mode == 0)
            return;
        double r = Percent(red), g = Percent(green), bl = Percent(blue);
        EachPixel(pixels, width, height, stride, (b3, b2, b1) =>
        {
            var (a, b, c, light) = Balance(b3 / 255.0, b2 / 255.0, b1 / 255.0, r, g, bl);
            if (mode != 1)
            {
                var (max, min) = MaxMin(a, b, c);
                var (hue, sat) = HueSaturation(a, b, c, max, min);
                (a, b, c) = FromHsl(hue, sat, light);
            }
            return (ToByte(a * 255.0), ToByte(b * 255.0), ToByte(c * 255.0));
        });
    }

    /// <summary>
    /// rsS25ChangeCBHSL(pixels, width, height, stride, red, green, blue, mode, hue (not used),
    /// saturation, light): the colour balance (mode 1 with its new light, any other keeping the
    /// pixel's), then saturation and light as rsS25ChangeHSL's.
    /// </summary>
    private void RioChangeCbHsl(int pixels, int width, int height, int stride, int red, int green, int blue, int mode, int saturation, int light)
    {
        double r = Percent(red), g = Percent(green), bl = Percent(blue), s = saturation / 100.0, l = light / 100.0;
        EachPixel(pixels, width, height, stride, (b3, b2, b1) =>
        {
            var (a, b, c, original) = Balance(b3 / 255.0, b2 / 255.0, b1 / 255.0, r, g, bl);
            var (max, min) = MaxMin(a, b, c);
            double lightness = mode == 1 ? (min + max) * Half : original;
            var (hue, sat) = HueSaturation(a, b, c, max, min);
            sat = (0 < saturation ? 1.0 - sat : sat) * s + sat;
            var (oa, ob, oc) = FromHsl(hue, sat, lightness);
            return (ToByte(((0 < light ? 1.0 - oa : oa) * l + oa) * 255.0), ToByte(((0 < light ? 1.0 - ob : ob) * l + ob) * 255.0),
                    ToByte(((0 < light ? 1.0 - oc : oc) * l + oc) * 255.0));
        });
    }
}
