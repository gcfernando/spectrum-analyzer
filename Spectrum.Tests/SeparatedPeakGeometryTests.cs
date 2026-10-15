// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Checks the geometry of separated peak markers.
public class SeparatedPeakGeometryTests
{
    [Theory]
    [InlineData("Spectrum", 96f)]
    [InlineData("Bricks", 96f)]
    [InlineData("LED", 96f)]
    [InlineData("Spectrum", 144f)]
    [InlineData("Bricks", 144f)]
    [InlineData("LED", 144f)]
    public void RealPainterKeepsEveryPeakPixelAboveUnmodifiedSignalPixels(string mode, float dpi)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create(mode);
            foreach (var quality in new[] { false, true })
            foreach (var style in new[] { VisualStyle.None, VisualStyle.Glow, VisualStyle.Trail, VisualStyle.Scanline })
            {
                bar.HighQualityRendering = quality;
                bar.VisualStyle = style;
                foreach (var value in new[] { 255f, 254f, 128f, 130f, 126f, 10f, 1f, 0f, 255f })
                {
                    Set(bar, "_displayValue", value);
                    Set(bar, "_peakValue", value);
                    bar.PeakHoldEnabled = false;
                    using var withoutPeak = Paint(bar, dpi);
                    var activeBounds = bar.RenderedActiveBounds;
                    bar.PeakHoldEnabled = true;
                    using var withPeak = Paint(bar, dpi);
                    var peak = bar.RenderedPeakBounds;
                    if (value <= 0.5f)
                    {
                        Assert.True(peak.IsEmpty);
                        continue;
                    }
                    Assert.False(peak.IsEmpty);
                    Assert.True(peak.Top >= bar.BrickPadding);
                    Assert.Equal((int)Math.Ceiling(2 * bar.DeviceDpi / 96d), peak.Height);
                    if (!activeBounds.IsEmpty)
                        Assert.True(peak.Bottom <= activeBounds.Top - bar.PeakGapPixels,
                            $"{mode}/{style}/{quality}/{dpi}/{value}: {peak} intersects {activeBounds}");
                    for (var y = 0; y < bar.Height; y++)
                    for (var x = 0; x < bar.Width; x++)
                    {
                        if (peak.Contains(x, y))
                            Assert.Equal(Color.Magenta.ToArgb(), withPeak.GetPixel(x, y).ToArgb());
                        else
                            Assert.Equal(withoutPeak.GetPixel(x, y).ToArgb(), withPeak.GetPixel(x, y).ToArgb());
                    }
                }
            }
        });
    }

    [Fact]
    public void MutableGeometryAndOffscreenDpiUseOneCalibratedExtent()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create("Bricks");
            foreach (var dpi in new[] { 96f, 144f, 192f, 96f })
            foreach (var height in new[] { 52, 177, 302 })
            {
                bar.Height = height;
                bar.BrickHeight = 9;
                bar.BrickGap = 0;
                bar.PeakLineThickness = 3;
                Set(bar, "_displayValue", 255f);
                Set(bar, "_peakValue", 255f);
                Set(bar, "_visualPeakValid", false);
                using var bitmap = Paint(bar, dpi);
                var plot = bar.PlotBounds;
                Assert.Equal(bar.Height - bar.BrickPadding, plot.Bottom);
                Assert.Equal(bar.BrickPadding + (int)Math.Ceiling(3 * bar.DeviceDpi / 96d) + bar.PeakGapPixels, plot.Top);
                foreach (var rect in Field<List<Rectangle>>(bar, "_brickRects"))
                    Assert.True(plot.Contains(rect));
                Assert.Equal(bar.RenderedActiveBounds.Top - bar.PeakGapPixels, bar.RenderedPeakBounds.Bottom);
                Assert.Equal(255f, Field<float>(bar, "_displayValue"));
                Assert.Equal(255f, Field<float>(bar, "_peakValue"));
            }
        });
    }

    [Fact]
    public void BitmapDpiTransitionsCannotChangeNativeLayoutOrPoisonTheNextPaint()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create("Spectrum");
            Set(bar, "_displayValue", 255f);
            Set(bar, "_peakValue", 255f);
            var beforeLayout = bar.PlotBounds;
            Rectangle marker = Rectangle.Empty;
            foreach (var dpi in new[] { 96f, 144f, 192f, 96f })
            {
                using var image = Paint(bar, dpi);
                Assert.Equal(beforeLayout, bar.PlotBounds);
                Assert.Equal((int)Math.Ceiling(2 * bar.DeviceDpi / 96d), bar.RenderedPeakBounds.Height);
                if (!marker.IsEmpty) Assert.Equal(marker, bar.RenderedPeakBounds);
                marker = bar.RenderedPeakBounds;
            }
        });
    }

    [Theory]
    [InlineData("Bricks")]
    [InlineData("LED")]
    public void FullScaleCellOffsetDoesNotChangeNumericPeakOrItsPixelDecayRate(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create(mode);
            bar.Height = 187;
            Set(bar, "_displayValue", 255f);
            Set(bar, "_peakValue", 255f);
            using (var full = Paint(bar, 144))
                Assert.Equal(bar.RenderedActiveBounds.Top - bar.PeakGapPixels, bar.RenderedPeakBounds.Bottom);
            Set(bar, "_displayValue", 32f);
            Set(bar, "_peakValue", 200f);
            Set(bar, "_visualPeakValid", false);
            Set(bar, "_targetValue", 32);
            using var before = Paint(bar, 144);
            var beforeTop = bar.RenderedPeakBounds.Top;
            Invoke(bar, "AnimateStep", 10f / 72, 10000f / 72);
            using var after = Paint(bar, 144);
            var expectedFall = (int)Math.Round(bar.PlotBounds.Height * 200f / 255f) -
                (int)Math.Round(bar.PlotBounds.Height * 190f / 255f);
            Assert.Equal(expectedFall, bar.RenderedPeakBounds.Top - beforeTop);
            Assert.InRange(Field<float>(bar, "_peakValue"), 189.999f, 190.001f);
            Assert.Equal(32f, Field<float>(bar, "_displayValue"));
        });
    }

    [Theory]
    [InlineData("Spectrum", 30)]
    [InlineData("Spectrum", 60)]
    [InlineData("Spectrum", 144)]
    [InlineData("Bricks", 30)]
    [InlineData("Bricks", 60)]
    [InlineData("Bricks", 144)]
    [InlineData("LED", 30)]
    [InlineData("LED", 60)]
    [InlineData("LED", 144)]
    public void RealTrailExpiryAndCellReleaseCannotDropMarkerFasterThanElapsedDecay(string mode, int fps)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create(mode);
            bar.Size = new Size(24, 241);
            bar.VisualStyle = VisualStyle.Trail;
            bar.UseAsymmetricBallistics = true;
            bar.ReleaseTimeMs = 380;
            Set(bar, "_targetValue", 255);
            Set(bar, "_displayValue", 255f);
            Set(bar, "_peakValue", 255f);
            Set(bar, "_peakHoldLeftMs", 140f);
            using (Paint(bar, 96)) { }
            var prior = bar.RenderedPeakBounds;
            var sawTrailAboveBar = false;
            var sawExpiredTrail = false;
            var sawVisualClearance = false;
            var previousLives = new float[8];
            var dt = 1f / fps;
            Set(bar, "_targetValue", 0);
            for (var frame = 0; frame < fps * 6; frame++)
            {
                Array.Copy(Field<float[]>(bar, "_trailLife"), previousLives, 8);
                Invoke(bar, "AnimateStep", dt, dt * 1000);
                using var image = Paint(bar, 96);
                var peak = bar.RenderedPeakBounds;
                var pixelTop = image.Height;
                var pixelBottom = 0;
                var markerX = bar.PlotBounds.Left + bar.PlotBounds.Width / 2;
                for (var row = 0; row < image.Height; row++)
                {
                    if (image.GetPixel(markerX, row).ToArgb() != Color.Magenta.ToArgb()) continue;
                    pixelTop = Math.Min(pixelTop, row);
                    pixelBottom = row + 1;
                }
                Assert.Equal(peak.IsEmpty, pixelBottom == 0);
                if (!peak.IsEmpty)
                {
                    Assert.Equal(pixelTop, peak.Top);
                    Assert.Equal(pixelBottom, peak.Bottom);
                }
                var lives = Field<float[]>(bar, "_trailLife");
                var levels = Field<float[]>(bar, "_trailLevels");
                var display = Field<float>(bar, "_displayValue");
                for (var i = 0; i < lives.Length; i++)
                {
                    sawTrailAboveBar |= lives[i] > 0 && levels[i] > display + 1;
                    sawExpiredTrail |= previousLives[i] > 0 && lives[i] == 0;
                }
                sawVisualClearance |= Field<bool>(bar, "_visualPeakReleasing");
                if (!peak.IsEmpty)
                {
                    if (!bar.RenderedActiveBounds.IsEmpty)
                        Assert.True(peak.Bottom + bar.PeakGapPixels <= bar.RenderedActiveBounds.Top);
                    if (!prior.IsEmpty)
                    {
                        var allowance = bar.PeakDecayPerTick * 60 * dt * bar.PlotBounds.Height / 255 + 1.01;
                        Assert.True(peak.Top - prior.Top <= allowance,
                            $"{mode}/{fps}/frame {frame}: marker fell {peak.Top - prior.Top}px, allowance {allowance:F3}.");
                    }
                    for (var y = peak.Top; y < peak.Bottom; y++)
                    for (var x = peak.Left; x < peak.Right; x++)
                        Assert.Equal(Color.Magenta.ToArgb(), image.GetPixel(x, y).ToArgb());
                }
                prior = peak;
            }
            Assert.True(sawTrailAboveBar);
            Assert.True(sawExpiredTrail);
            Assert.True(sawVisualClearance);
            Assert.True(bar.RenderedPeakBounds.IsEmpty);
            Assert.Equal(0f, Field<float>(bar, "_peakValue"));
            Assert.Equal(0f, Field<float>(bar, "_displayValue"));
            Assert.True((bool)Invoke(bar, "IsSettled"));
        });
    }

    [Theory]
    [InlineData("Bricks")]
    [InlineData("LED")]
    public void CellOnlyCollisionReleaseAlsoHonorsThePixelFallLimit(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create(mode);
            bar.BrickHeight = 15;
            bar.BrickGap = 3;
            bar.ResponseTimeMs = 1;
            bar.PeakHoldMilliseconds = 0;
            var dt = 1f / 60;
            Rectangle previous = Rectangle.Empty;
            var sawDelayedRelease = false;
            for (var frame = 0; frame < 500; frame++)
            {
                Set(bar, "_targetValue", Math.Max(0, 255 - frame));
                Invoke(bar, "AnimateStep", dt, dt * 1000);
                using var image = Paint(bar, 96);
                var peak = bar.RenderedPeakBounds;
                sawDelayedRelease |= Field<bool>(bar, "_visualPeakReleasing");
                if (!peak.IsEmpty && !previous.IsEmpty)
                    Assert.True(peak.Top - previous.Top <= 72 * dt * bar.PlotBounds.Height / 255 + 1.01);
                if (!peak.IsEmpty && !bar.RenderedActiveBounds.IsEmpty)
                    Assert.True(peak.Bottom + bar.PeakGapPixels <= bar.RenderedActiveBounds.Top);
                previous = peak;
            }
            Assert.True(sawDelayedRelease);
            Assert.True(bar.RenderedPeakBounds.IsEmpty);
            Assert.True((bool)Invoke(bar, "IsSettled"));
        });
    }

    [Fact]
    public void VisualClearanceMapsAcrossResizeAndResetsOnVisibilityAndPeakDisable()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create("Spectrum");
            bar.VisualStyle = VisualStyle.Trail;
            Set(bar, "_peakValue", 100f);
            var levels = Field<float[]>(bar, "_trailLevels");
            var lives = Field<float[]>(bar, "_trailLife");
            levels[0] = 255;
            lives[0] = 0.2f;
            using (Paint(bar, 96)) { }
            var clearance = Field<float>(bar, "_visualPeakLevel");
            lives[0] = 0;
            bar.Height = 360;
            using (Paint(bar, 96)) { }
            Assert.Equal(clearance, Field<float>(bar, "_visualPeakLevel"));
            Assert.True(Field<bool>(bar, "_visualPeakReleasing"));
            Assert.Equal(100f, Field<float>(bar, "_peakValue"));
            bar.Visible = false;
            bar.Visible = true;
            Assert.False(Field<bool>(bar, "_visualPeakValid"));
            Assert.False(Field<bool>(bar, "_visualPeakReleasing"));
            Set(bar, "_peakValue", 255f);
            using (Paint(bar, 96)) { }
            bar.PeakHoldEnabled = false;
            Assert.False(Field<bool>(bar, "_visualPeakValid"));
            Assert.False(Field<bool>(bar, "_visualPeakReleasing"));
            Invoke(bar, "AnimateStep", 10f, 10000f);
            using var silent = Paint(bar, 96);
            Assert.True(bar.RenderedPeakBounds.IsEmpty);
            Assert.True((bool)Invoke(bar, "IsSettled"));
        });
    }

    [Theory]
    [InlineData("Spectrum", 0)]
    [InlineData("Spectrum", 140)]
    [InlineData("Bricks", 0)]
    [InlineData("Bricks", 140)]
    [InlineData("LED", 0)]
    [InlineData("LED", 140)]
    public void StableNonzeroPlateausSettleWhenPaintingOnlyForChangedPresentation(string mode, int holdMs)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            foreach (var target in new[] { 17, 64, 127, 191, 255 })
            {
                using var bar = Create(mode);
                bar.Size = new Size(13, 193 + target % 7);
                bar.BrickHeight = 7 + target % 5;
                bar.BrickGap = 1 + target % 3;
                bar.UseAsymmetricBallistics = true;
                bar.ReleaseTimeMs = 380;
                bar.PeakHoldMilliseconds = holdMs;
                Set(bar, "_displayValue", 255f);
                Set(bar, "_peakValue", 255f);
                Set(bar, "_targetValue", target);
                var cadence = new[] { 1f / 30 * 1.3f, 1f / 144, 1f / 60 * 0.7f, 0.019f };
                var nativeMode = Invoke(bar, "GetVisualizationModeCached");
                if ((bool)Invoke(bar, "ComputeShouldInvalidate", nativeMode))
                    using (Paint(bar, 96)) { }
                var stableFrames = 0;
                for (var frame = 0; frame < 1500 && stableFrames < 150; frame++)
                {
                    var dt = cadence[frame % cadence.Length];
                    Invoke(bar, "AnimateStep", dt, dt * 1000);
                    var changed = (bool)Invoke(bar, "ComputeShouldInvalidate", nativeMode);
                    if (changed)
                        using (Paint(bar, 96)) { }
                    stableFrames = !changed && (bool)Invoke(bar, "IsSettled") ? stableFrames + 1 : 0;
                }
                Assert.True(stableFrames >= 150, $"{mode}/{holdMs}/{target}: never became paint-independent idle.");
                Assert.Equal((float)target, Field<float>(bar, "_displayValue"));
                Assert.Equal((float)target, Field<float>(bar, "_peakValue"));
                var peak = bar.RenderedPeakBounds;
                Assert.False(peak.IsEmpty);
                for (var frame = 0; frame < 150; frame++)
                {
                    var dt = cadence[frame % cadence.Length];
                    Invoke(bar, "AnimateStep", dt, dt * 1000);
                    Assert.False((bool)Invoke(bar, "ComputeShouldInvalidate", nativeMode),
                        $"{mode}/{holdMs}/{target}: redundant invalidation at stable frame {frame}.");
                    Assert.True((bool)Invoke(bar, "IsSettled"));
                    Assert.Equal(peak, bar.RenderedPeakBounds);
                }
            }
        });
    }

    [Fact]
    public void OversizedFallbackAndNarrowAndTinyControlsCannotOverlayTheirBars()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create("Spectrum");
            bar.BrickPadding = 0;
            foreach (var size in new[] { new Size(2, 120), new Size(1, 120), new Size(12, 3), new Size(1050, 1050) })
            {
                bar.Size = size;
                Set(bar, "_displayValue", 255f);
                Set(bar, "_peakValue", 255f);
                using var bitmap = Paint(bar, 144);
                var peak = bar.RenderedPeakBounds;
                if (size.Height <= 6) Assert.True(peak.IsEmpty);
                else
                {
                    Assert.False(peak.IsEmpty);
                    Assert.True(peak.Bottom <= bar.RenderedActiveBounds.Top - bar.PeakGapPixels);
                    Assert.Equal(Color.Magenta.ToArgb(), bitmap.GetPixel(0, peak.Top).ToArgb());
                }
            }
        });
    }

    [Fact]
    public void PeakHoldConsumesAllElapsedTimeAndIsIndependentPerControl()
    {
        float peak = 240, hold = 140;
        BarBallistics.StepPeak(ref peak, ref hold, 0, 1140, 140, 72, 0);
        Assert.Equal(168f, peak);
        Assert.Equal(0f, hold);
        float splitPeak = 240, splitHold = 140;
        foreach (var elapsed in new[] { 10f, 50f, 80f, 100f, 400f, 500f })
            BarBallistics.StepPeak(ref splitPeak, ref splitHold, 0, elapsed, 140, 72, 0);
        Assert.InRange(Math.Abs(peak - splitPeak), 0, 0.0001f);
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var first = Create("Spectrum");
            using var second = Create("Spectrum");
            Set(first, "_peakValue", 240f);
            Set(first, "_peakHoldLeftMs", 140f);
            Set(first, "_displayValue", 0f);
            Set(second, "_peakValue", 120f);
            Set(second, "_peakHoldLeftMs", 140f);
            Set(second, "_displayValue", 0f);
            Invoke(first, "AnimateStep", 1.14f, 1140f);
            Assert.InRange(Field<float>(first, "_peakValue"), 167.999f, 168.001f);
            Assert.Equal(120f, Field<float>(second, "_peakValue"));
            Invoke(first, "AnimateStep", 10f, 10000f);
            Assert.Equal(0f, Field<float>(first, "_peakValue"));
            Assert.Equal(0f, Field<float>(first, "_peakHoldLeftMs"));
        });
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void OneFrameMeasuredPulseIsCapturedBeforeSlowBarAttackFinishes(int fps)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = Create("Spectrum");
            bar.ResponseTimeMs = 1000;
            bar.PeakHoldMilliseconds = 300;
            bar.SetTargetValueUI(255);
            Invoke(bar, "AnimateStep", 1f / fps, 1000f / fps);
            Assert.InRange(Field<float>(bar, "_displayValue"), 0.1f, 9f);
            Assert.Equal(255f, Field<float>(bar, "_peakValue"));
            Assert.Equal(300f, Field<float>(bar, "_peakHoldLeftMs"));

            bar.SetTargetValueUI(0);
            Invoke(bar, "AnimateStep", 0.2f, 200f);
            Assert.Equal(255f, Field<float>(bar, "_peakValue"));
            Assert.Equal(100f, Field<float>(bar, "_peakHoldLeftMs"));
            Invoke(bar, "AnimateStep", 0.2f, 200f);
            Assert.InRange(Field<float>(bar, "_peakValue"), 247.799f, 247.801f);
            Assert.Equal(0f, Field<float>(bar, "_peakHoldLeftMs"));
            using var bitmap = Paint(bar, 96);
            Assert.False(bar.RenderedPeakBounds.IsEmpty);
            Assert.True(bar.RenderedPeakBounds.Bottom <= bar.RenderedActiveBounds.Top - bar.PeakGapPixels);
        });
    }

    private static VerticalProgressBar Create(string mode) => new()
    {
        Tag = mode,
        Size = new Size(24, 180),
        Maximum = 255,
        BrickPadding = 2,
        HeatmapEnabled = false,
        BrickHighlight = false,
        BackColor = Color.Black,
        ForeColor = Color.Lime,
        PeakColorMatchesHeat = false,
        PeakLineColor = Color.Magenta,
        PeakDecayPerTick = 1.2f
    };

    private static Bitmap Paint(VerticalProgressBar bar, float dpi)
    {
        var bitmap = new Bitmap(bar.Width, bar.Height);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        Invoke(bar, "OnPaint", new System.Windows.Forms.PaintEventArgs(graphics, bar.ClientRectangle));
        return bitmap;
    }

    private static void Set(object target, string name, object value) =>
        CombinedRandomizationTests.SetField(target, name, value);
    private static T Field<T>(object target, string name) =>
        CombinedRandomizationTests.GetField<T>(target, name);
    private static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
}
