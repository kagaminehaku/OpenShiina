// The engine's animation and effect formulas, read from START.SCN, EFCLIB.SCN and the exe
// (docs/engine-notes.md, section 8). Any IStage implementation draws with these.

namespace OpenShiina.Story;

public static class StageMath
{
    /// <summary>The engine's three easing curves: 1 linear, 2 slow start (1-cos), 3 slow end (sin).</summary>
    public static double Ease(int type, double t) => type switch
    {
        2 => 1 - Math.Cos(Math.PI * t / 2),
        3 => Math.Sin(Math.PI * t / 2),
        _ => t,
    };

    /// <summary>
    /// A_CHR 1-6: where a looping plane is moved to, <paramref name="phase"/> ms into a cycle of
    /// <paramref name="period"/> ms. 1 hop up, 2 dip down, 3 / 4 triangle wave on y / x,
    /// 5 / 6 sine wave on y / x.
    /// </summary>
    public static (double X, double Y) LoopOffset(int mode, double phase, double period, double amplitude)
    {
        double ph = phase, a = amplitude;
        double dx = 0, dy = 0, k = a * ph * 2 / period;
        double tri = ph < period / 4 ? -k : ph < period * 3 / 4 ? k - a : -(k - a * 2);
        switch (mode)
        {
            case 1: dy = -Math.Sin(Math.PI * ph / period) * a; break;
            case 2: dy = Math.Sin(Math.PI * ph / period) * a; break;
            case 3: dy = tri; break;
            case 4: dx = tri; break;
            case 5: dy = -Math.Sin(2 * Math.PI * ph / period) * a / 2; break;
            case 6: dx = Math.Sin(2 * Math.PI * ph / period) * a / 2; break;
        }
        return (dx, dy);
    }

    /// <summary>
    /// A_CHR 60-63 at progress <paramref name="t"/> (0..1): for each rule mask level 0-255, how
    /// much of the plane shows (0-255); bright rule pixels show first and vanish last.
    /// START.SCN 13E8D: trs runs 511 -> 0 to appear, 0 -> 511 to vanish (0 = all shown). The MMX
    /// blend at 796E5 keeps rule values >= imax, ramps (imin, imax), drops the rest:
    /// trs &lt; 256: imin 0, imax trs; else imin trs - 256, imax 255.
    /// </summary>
    public static void RuleFadeLevels(double t, bool appear, bool reversed, byte[] levels)
    {
        int trs = (int)(511 * t);
        if (appear)
            trs = 511 - trs;
        int imin = trs < 256 ? 0 : trs - 256, imax = trs < 256 ? trs : 255;
        int step = 0x8080 / (imax - imin + 1), bias = imin == 0 ? 0 : imin - 1;
        for (int level = 0; level < 256; level++)
        {
            int m = reversed ? 255 - level : level;
            levels[level] = (byte)(trs == 0 || m >= imax ? 255 : m > imin ? (m - bias) * step * 255 >> 15 : 0);
        }
    }

    /// <summary>
    /// Rule wipe of $DRAW_EX at progress <paramref name="p"/> (0..1): for each rule level 0-255,
    /// how much of the OLD picture still shows (0-255). The engine's blend (exe 0x437B40): with t
    /// going 0..511, the new picture's weight at a pixel is clamp(t + rule - 256, 0, 256) / 256,
    /// so bright parts of the rule change first. Dark-first ($DRAW_EX 47) runs the same blend
    /// with the pictures swapped and t going back.
    /// </summary>
    public static void RuleWipeLevels(double p, bool brightFirst, byte[] levels)
    {
        double t = 511 * (brightFirst ? p : 1 - p);
        for (int level = 0; level < 256; level++)
        {
            double w = Math.Clamp(t + level - 256, 0, 256) / 256;
            double old = brightFirst ? 1 - w : w;
            levels[level] = (byte)(old * 255);
        }
    }

    /// <summary>EFCLIB runs its screen effects at 15 frames a second.</summary>
    public const double EffectFrameMs = 1000.0 / 15;

    /// <summary>
    /// EFCLIB 34 ($EFECT 0 / 1 / 2): the screen, enlarged by <paramref name="size"/> pixels,
    /// jumps between four offsets, twice.
    /// </summary>
    public static IReadOnlyList<StageTransform> ShakeFrames(int size)
    {
        double sx = (StageSize.Width + size) / (double)StageSize.Width, sy = (StageSize.Height + size) / (double)StageSize.Height;
        var offsets = new (double X, double Y)[] { (0, -size), (-size / 2.0, -size / 2.0), (-size, -size), (-size / 2.0, -size / 2.0) };
        var frames = new List<StageTransform>();
        for (int round = 0; round < 2; round++)
            foreach (var (x, y) in offsets)
                frames.Add(new StageTransform(sx, sy, x, y));
        return frames;
    }

    /// <summary>
    /// EFCLIB 35 ($EFECT 8-15): zooms in by (w, h) pixels a side per step, steps 1, 2, 3, 2, 1,
    /// <paramref name="rounds"/> times.
    /// </summary>
    public static IReadOnlyList<StageTransform> ZoomPulseFrames(int w, int h, int rounds)
    {
        var frames = new List<StageTransform>();
        for (int round = 0; round < rounds; round++)
        {
            foreach (int z in new[] { 1, 2, 3, 2, 1 })
            {
                double tx = w * z, ty = h * z;
                double sx = StageSize.Width / (StageSize.Width - 2 * tx), sy = StageSize.Height / (StageSize.Height - 2 * ty);
                frames.Add(new StageTransform(sx, sy, -tx * sx, -ty * sy));
            }
        }
        return frames;
    }
}
