// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;

namespace Spectrum;

internal static class BarBallistics
{
    public const float SettleEpsilon = 0.05f;

    // Time-based smoothing so the bars look the same no matter the refresh rate.
    public static float StepLevel(float current, float target, float dtSeconds, float fullScale, int attackMs, int releaseMs, bool snapDown)
    {
        if (target > current)
        {
            var attackRate = fullScale / Math.Max(0.001f, attackMs / 1000f);
            return Math.Min(target, current + (attackRate * dtSeconds));
        }

        if (target < current)
        {
            if (snapDown)
            {
                return target;
            }

            var tau = Math.Max(0.001f, releaseMs / 1000f);
            var alpha = 1f - (float)Math.Exp(-dtSeconds / tau);
            var next = current + ((target - current) * alpha);

            return Math.Abs(next - target) < SettleEpsilon ? target : next;
        }

        return current;
    }

    // Hysteresis stops bricks from flickering when the level hovers right at a brick edge.
    public static int StepLitBricks(int currentLit, IReadOnlyList<int> centresBottomUp, int topLimit, int hysteresisPx)
    {
        var count = centresBottomUp.Count;
        var n = currentLit < 0 ? 0 : (currentLit > count ? count : currentLit);

        while (n < count && centresBottomUp[n] - hysteresisPx >= topLimit)
        {
            n++;
        }

        while (n > 0 && centresBottomUp[n - 1] + hysteresisPx < topLimit)
        {
            n--;
        }

        return n;
    }

    public static void StepPeak(ref float peak, ref float holdLeftMs, float level, float dtMs, int holdMs, float decayPerSecond, float minimum)
    {
        if (level >= peak)
        {
            peak = level;
            holdLeftMs = Math.Max(0, holdMs);
            return;
        }

        if (holdLeftMs > 0)
        {
            var heldMs = Math.Min(holdLeftMs, dtMs);
            holdLeftMs -= heldMs;
            dtMs -= heldMs;
        }

        peak -= decayPerSecond * (dtMs / 1000f);
        if (peak < level)
        {
            peak = level;
        }

        if (peak < minimum)
        {
            peak = minimum;
        }
    }
}
