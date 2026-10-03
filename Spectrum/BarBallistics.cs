using System;
using System.Collections.Generic;

namespace Spectrum;

/// Presentation ballistics for one bar. Timing is based on elapsed time so refresh rate does not change the result.
/// Values use display units from 0..fullScale and track the measured band level in dB.
/// Attack, release, and peak hold are handled independently.
internal static class BarBallistics
{
    /// Residual distance below which the release is considered settled.
    public const float SettleEpsilon = 0.05f;

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

    /// Returns the number of lit bricks for a level whose top is at the given pixel row.
    /// A brick turns on when the level reaches its centre and turns off only after hysteresis is crossed.
    /// <param name="centresBottomUp">Brick centres from the bottom up; rows decrease upward.</param>
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

    /// <param name="decayPerSecond">Marker fall rate after the hold time, in display units per second.</param>
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
            holdLeftMs -= dtMs;
            if (holdLeftMs < 0)
            {
                holdLeftMs = 0;
            }

            return;
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
