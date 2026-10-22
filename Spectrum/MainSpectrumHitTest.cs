using System;
using System.Collections.Generic;
using System.Drawing;
using Spectrum.Dsp;

namespace Spectrum;

internal readonly struct MainSpectrumHit
{
    internal MainSpectrumHit(int bandIndex, string channel = "Combined")
    { BandIndex = bandIndex; Channel = channel; }
    public int BandIndex { get; }
    public string Channel { get; }
    public bool HasBand => BandIndex >= 0;
}

// All rectangles use coordinates local to the displayed control. Obtain them from
// that renderer's actual paint/layout; do not substitute the surrounding panel bounds.
internal static class MainSpectrumHitTest
{
    internal static MainSpectrumHit HitTest(string mode, BandPlan plan, PointF point, RectangleF plotBounds,
        IReadOnlyList<RectangleF> bandBounds = null, IReadOnlyList<RectangleF> stereoPlotBounds = null,
        PointF radialCenter = default, float radialRadius = 0)
    {
        var none = new MainSpectrumHit(-1);
        if (plan == null || !Finite(point.X) || !Finite(point.Y)) return none;
        switch (MainSpectrumInspector.GetGeometry(mode))
        {
            case MainSpectrumGeometry.LegacyBars:
            case MainSpectrumGeometry.MatrixCards:
                // Actual cards/bars can wrap, have gaps, or become hidden at tiny sizes.
                if (bandBounds == null || bandBounds.Count != plan.Count) return none;
                for (var i = 0; i < bandBounds.Count; i++)
                    if (Contains(bandBounds[i], point, false)) return new MainSpectrumHit(i);
                return none;
            case MainSpectrumGeometry.SevenBandCards:
                if (plotBounds.Height < 8 || plotBounds.Width / 7 < 4 || !Contains(plotBounds, point)) return none;
                return new MainSpectrumHit(Math.Min(6, (int)((point.X - plotBounds.Left) / plotBounds.Width * 7)));
            case MainSpectrumGeometry.StereoPanels:
                if (stereoPlotBounds == null || stereoPlotBounds.Count != 2) return none;
                for (var channel = 0; channel < 2; channel++)
                    if (Contains(stereoPlotBounds[channel], point))
                        return new MainSpectrumHit(LogBand(plan, point, stereoPlotBounds[channel]), channel == 0 ? "Left" : "Right");
                return none;
            case MainSpectrumGeometry.LogFrequency:
                return Contains(plotBounds, point) ? new MainSpectrumHit(LogBand(plan, point, plotBounds)) : none;
            case MainSpectrumGeometry.RadialBands:
                if (!(radialRadius > 0) || !Finite(radialRadius) || !Finite(radialCenter.X) || !Finite(radialCenter.Y)) return none;
                var dx = point.X - radialCenter.X; var dy = point.Y - radialCenter.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                // Inner 20% is intentionally empty, and side legends are not frequency spokes.
                if (distance < radialRadius * .2 || distance > radialRadius) return none;
                var angle = Math.Atan2(dx, -dy); // First spoke at twelve o'clock; clockwise.
                if (angle < 0) angle += Math.PI * 2;
                return new MainSpectrumHit((int)Math.Floor(angle * plan.Count / (2 * Math.PI) + .5) % plan.Count);
            default: return none;
        }
    }

    private static int LogBand(BandPlan plan, PointF point, RectangleF bounds)
    {
        // SpectrumAxis.Position is a fixed 20 Hz-20 kHz logarithmic axis, even when
        // the measured plan ends earlier at a low-rate Nyquist boundary.
        var fraction = Math.Max(0, Math.Min(1, (point.X - bounds.Left) / bounds.Width));
        var frequency = fraction == 1 ? 20000 : 20 * Math.Pow(1000, fraction);
        return MainSpectrumInspector.FindBandAtFrequency(plan, frequency);
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Contains(RectangleF bounds, PointF point, bool includeEnd = true) =>
        Finite(bounds.X) && Finite(bounds.Y) && Finite(bounds.Width) && Finite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0 &&
        point.X >= bounds.Left && point.Y >= bounds.Top &&
        (includeEnd ? point.X <= bounds.Right && point.Y <= bounds.Bottom : point.X < bounds.Right && point.Y < bounds.Bottom);
}
