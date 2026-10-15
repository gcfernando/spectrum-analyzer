// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Checks the spectrum axis (frequency/level mapping) and its drawing.
public class SpectrumAxisPeakAcceptanceTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly double[] Frequencies =
        { 20, 30, 40, 60, 100, 200, 300, 400, 600, 1000, 2000, 3000, 4000, 6000, 10000, 20000 };
    private static readonly Color Indicator = Color.FromArgb(255, 255, 0, 255);

    public static IEnumerable<object[]> AxisCases()
    {
        foreach (var mode in new[] { "Spectrum", "Bricks", "LED", "Contour", "Peak Trace", "Threshold Monitor" })
        foreach (var size in new[] { new Size(900, 250), new Size(1184, 378), new Size(1920, 1080) })
        foreach (var scale in new[] { 1f, 1.5f, 2f })
            yield return new object[] { mode, size.Width, size.Height, scale };
    }

    [Theory]
    [MemberData(nameof(AxisCases))]
    public void NativeAxesRetainAllThirteenDbLabelsAndLogarithmicGridAtSupportedSizes(
        string mode, int width, int height, float fontScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: mode);
            Set(form, "_applyingRandomSelection", true);
            var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
            form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
            form.ShowInTaskbar = false;
            form.Show();
            var fonts = new List<Font>();
            try
            {
                foreach (var label in Descendants(form).OfType<Label>())
                {
                    label.UseCompatibleTextRendering = false;
                    var font = new Font(label.Font.FontFamily, label.Font.SizeInPoints * fontScale, label.Font.Style);
                    fonts.Add(font);
                    label.Font = font;
                }
                form.ClientSize = new Size(width, height);
                var expectedClientSize = form.ClientSize;
                for (var layout = 0; layout < 3; layout++)
                {
                    Call(form, "PositionSelectionPanel");
                    Call(form, "RecalculateBarLayout");
                    form.PerformLayout();
                }
                Assert.Equal(expectedClientSize, form.ClientSize);

                var bars = Field<VerticalProgressBar[]>(form, "_progressBars");
                var advanced = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
                var isMeter = mode == "Spectrum" || mode == "Bricks" || mode == "LED";
                var plot = isMeter
                    ? new RectangleF(bars[0].Left, bars[0].Top + bars[0].PlotBounds.Top,
                        bars[bars.Length - 1].Right - bars[0].Left, bars[0].PlotBounds.Height)
                    : advanced.SpectrumPlotBounds;
                if (!isMeter) plot.Offset(advanced.Left, advanced.Top);
                foreach (var side in new[] { "_dbAxisLabelsLeft", "_dbAxisLabelsRight" })
                {
                    var labels = Field<Label[]>(form, side);
                    Assert.Equal(13, labels.Length);
                    var previousBottom = int.MinValue;
                    for (var index = 0; index < labels.Length; index++)
                    {
                        var label = labels[index];
                        Assert.Equal((-6 * index).ToString(CultureInfo.InvariantCulture), label.Text);
                        if (!label.Visible) continue;
                        Assert.True(label.Parent.ClientRectangle.Contains(label.Bounds));
                        Assert.True(label.Top >= previousBottom + 2, $"{mode}: overlapping dB labels.");
                        Assert.InRange(Math.Abs(label.Top + label.Height / 2d -
                            (plot.Top + plot.Height * index / 12d)), 0, 1.1);
                        previousBottom = label.Bottom;
                    }
                    var canFitAllLabels = plot.Height >= 12 * (labels.Max(label => label.Height) + 4);
                    if (canFitAllLabels) Assert.All(labels, label => Assert.True(label.Visible));
                    else if (plot.Height >= 2 * labels[0].Height + 4)
                    {
                        Assert.True(labels[0].Visible);
                        Assert.True(labels[12].Visible);
                    }
                }
                var frequencyLabels = Field<Label[]>(form, "_axisLabels");
                Assert.Equal(16, frequencyLabels.Length);
                var visible = frequencyLabels.Where(label => label.Visible).ToArray();
                Assert.True(frequencyLabels[0].Visible);
                Assert.True(frequencyLabels[15].Visible);
                var previousRight = int.MinValue;
                for (var index = 0; index < frequencyLabels.Length; index++)
                {
                    var label = frequencyLabels[index];
                    Assert.Equal(FrequencyText(Frequencies[index]), label.Text);
                    if (!label.Visible) continue;
                    Assert.True(label.Left >= previousRight + 2, $"{mode}: overlapping frequency labels.");
                    Assert.True(label.Parent.ClientRectangle.Contains(label.Bounds));
                    var expected = plot.Left + plot.Width * Math.Log(Frequencies[index] / 20) / Math.Log(1000);
                    Assert.InRange(Math.Abs(label.Left + label.Width / 2d - expected), 0, 1.1);
                    previousRight = label.Right;
                }
                Assert.True(visible.Length >= 6, $"{mode}: density pruning removed too many frequency labels.");
                if (width == 1920 && fontScale == 1) Assert.Equal(16, visible.Length);
                if (isMeter) AssertMeterGridPixels(bars);
                else AssertAdvancedGridPixels(advanced);
            }
            finally
            {
                form.Dispose();
                foreach (var font in fonts) font.Dispose();
            }
        });
    }

    [Theory]
    [InlineData("Bricks")]
    [InlineData("LED")]
    public void EveryBrickBoundaryRetainsHysteresisWithoutAllowingPeakOverlap(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = NewBar(mode);
            bar.Size = new Size(24, 241);
            using (Paint(bar)) { }
            var centres = Field<List<int>>(bar, "_brickCentres").ToArray();
            Assert.True(centres.Length > 10);
            foreach (var centre in centres.Concat(centres.Reverse()))
            foreach (var delta in new[] { -3, -2, -1, 0, 1, 2, 3, 2, 1, 0, -1, -2, -3 })
            {
                var plot = bar.PlotBounds;
                var top = Math.Max(plot.Top, Math.Min(plot.Bottom, centre + delta));
                var level = 255f * (plot.Bottom - top) / plot.Height;
                var oldCount = Field<int>(bar, "_litBricks");
                var expected = centres.Where((row, index) =>
                    index < oldCount ? row + 2 >= top : row - 2 >= top).Count();
                ResetVisualMarker(bar);
                Set(bar, "_displayValue", level);
                Set(bar, "_peakValue", level);
                using var image = Paint(bar);
                Assert.Equal(expected, Field<int>(bar, "_litBricks"));
                AssertPeakPixels(bar, image, $"{mode}/brick centre {centre}/offset {delta}");
            }
        });
    }

    [Theory]
    [InlineData("Spectrum")]
    [InlineData("Bricks")]
    [InlineData("LED")]
    public void ActualPeakPixelsNeverCrossActualBarAcrossQuantizationResizeAndStyles(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = NewBar(mode);
            foreach (var style in VisualStyles.GetSupportedNames(mode))
            foreach (var size in new[] { new Size(2, 8), new Size(3, 13), new Size(9, 64), new Size(24, 241) })
            {
                bar.Size = size;
                bar.BrickPadding = size.Width <= 3 ? 0 : 1;
                bar.VisualStyle = VisualStyles.Parse(style);
                foreach (var level in new[] { 0f, 0.51f, 1f, 16f, 63.5f, 127.5f, 254f, 255f, 127.5f, 63.5f, 1f, 0f })
                {
                    ResetVisualMarker(bar);
                    Set(bar, "_displayValue", level);
                    Set(bar, "_peakValue", level);
                    using var image = Paint(bar);
                    AssertPeakPixels(bar, image, $"{mode}/{style}/{size}/{level}");
                }
            }
        });
    }

    [Theory]
    [InlineData("Spectrum")]
    [InlineData("Bricks")]
    [InlineData("LED")]
    public void MarkerCannotManufactureItsGapByErasingActualSignalPixels(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = NewBar(mode);
            foreach (var size in new[] { new Size(2, 13), new Size(12, 65), new Size(24, 241) })
            {
                bar.Size = size;
                bar.BrickPadding = size.Width == 2 ? 0 : 1;
                foreach (var value in new[] { 1f, 16f, 63f, 127f, 191f, 254f, 255f })
                {
                    Set(bar, "_displayValue", value);
                    Set(bar, "_peakValue", value);
                    bar.PeakHoldEnabled = false;
                    using var original = Paint(bar);
                    bar.PeakHoldEnabled = true;
                    using var marked = Paint(bar);
                    for (var y = 0; y < original.Height; y++)
                    for (var x = 0; x < original.Width; x++)
                        if (IsSignal(original.GetPixel(x, y)))
                            Assert.Equal(original.GetPixel(x, y).ToArgb(), marked.GetPixel(x, y).ToArgb());
                    AssertPeakPixels(bar, marked, $"{mode}/{size}/{value}/non-erasure");
                }
            }
        });
    }

    [Theory]
    [InlineData("Spectrum")]
    [InlineData("Bricks")]
    [InlineData("LED")]
    public void FullScaleAndQuantizedPlateausPreserveCalibratedMinimumGapInEveryTheme(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            foreach (var name in BarColorThemes.Names)
            {
                using var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Resolve(name), 255);
                bar.Tag = mode;
                bar.Size = new Size(24, 241);
                bar.PeakHoldEnabled = true;
                bar.PeakColorMatchesHeat = false;
                bar.PeakLineColor = Indicator;
                Set(bar, "_displayValue", 255f);
                Set(bar, "_peakValue", 255f);
                using (Paint(bar)) { }
                var fullScaleOffset = Math.Max(0, bar.RenderedActiveBounds.Top - bar.PlotBounds.Top);
                foreach (var value in new[] { 32f, 64f, 128f, 192f, 255f })
                {
                    ResetVisualMarker(bar);
                    Set(bar, "_displayValue", value);
                    Set(bar, "_peakValue", value);
                    using var image = Paint(bar);
                    AssertPeakPixels(bar, image, $"{mode}/{name}/{value}");
                    Assert.False(bar.RenderedActiveBounds.IsEmpty);
                    Assert.False(bar.RenderedPeakBounds.IsEmpty);
                    var mappedBoundary = bar.PlotBounds.Bottom -
                        (int)Math.Round(bar.PlotBounds.Height * value / 255f);
                    var expectedBottom = Math.Min(mappedBoundary + fullScaleOffset,
                        bar.RenderedActiveBounds.Top) - bar.PeakGapPixels;
                    Assert.Equal(expectedBottom, bar.RenderedPeakBounds.Bottom);
                    if (value == 255)
                        Assert.Equal(bar.PeakGapPixels, bar.RenderedActiveBounds.Top - bar.RenderedPeakBounds.Bottom);
                    var x = bar.RenderedPeakBounds.Left + bar.RenderedPeakBounds.Width / 2;
                    for (var y = bar.RenderedPeakBounds.Bottom; y < bar.RenderedActiveBounds.Top; y++)
                        Assert.Equal(bar.BackColor.ToArgb(), image.GetPixel(x, y).ToArgb());
                }
            }
        });
    }

    [Theory]
    [InlineData("Spectrum", 8.333333f)]
    [InlineData("Spectrum", 16.666667f)]
    [InlineData("Bricks", 33.333333f)]
    [InlineData("LED", 7f)]
    public void RealAnimationAndPainterPreservePeakSeparationForAttackHoldReleaseAndReattack(string mode, float cadenceMs)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = NewBar(mode);
            bar.Size = new Size(12, 241);
            var elapsed = 0f;
            var settleDeadlineMs = 1900 + bar.PeakHoldMilliseconds +
                255 / (bar.PeakDecayPerTick * 60) * 1000 + 500;
            for (var frame = 0; elapsed < settleDeadlineMs; frame++)
            {
                var dt = cadenceMs * (frame % 3 == 0 ? 1.4f : frame % 3 == 1 ? 0.6f : 1f);
                var target = elapsed < 350 ? 255 : elapsed < 900 ? 80 : elapsed < 1300 ? 190 :
                    elapsed < 1700 ? 40 : elapsed < 1900 ? 255 : 0;
                Set(bar, "_targetValue", target);
                Call(bar, "AnimateStep", dt / 1000f, dt);
                var level = Field<float>(bar, "_displayValue");
                var peak = Field<float>(bar, "_peakValue");
                Assert.InRange(level, 0, 255);
                Assert.InRange(peak, level, 255);
                using var image = Paint(bar);
                AssertPeakPixels(bar, image, $"{mode}/{cadenceMs}/frame {frame}");
                elapsed += dt;
            }
            Assert.Equal(0f, Field<float>(bar, "_displayValue"));
            Assert.Equal(0f, Field<float>(bar, "_peakValue"));
            using var silent = Paint(bar);
            Assert.True(FindPixels(silent, Indicator).IsEmpty);
        });
    }

    [Fact]
    public void PeakHoldConsumesOnlyTheElapsedHoldAndDecayIsCadenceIndependent()
    {
        foreach (var cadence in new[] { 7f, 1000f / 120, 1000f / 60, 1000f / 30, 83f })
        {
            var peak = 255f;
            var hold = 143f;
            var elapsed = 0f;
            for (var frame = 0; elapsed < 1800; frame++)
            {
                var dt = Math.Min(1800 - elapsed, cadence * (frame % 2 == 0 ? 0.7f : 1.3f));
                BarBallistics.StepPeak(ref peak, ref hold, 23, dt, 143, 72, 0);
                elapsed += dt;
                var expected = Math.Max(23, 255 - 72 * Math.Max(0, elapsed - 143) / 1000);
                Assert.InRange(Math.Abs(peak - expected), 0, 0.003);
                Assert.InRange(Math.Abs(hold - Math.Max(0, 143 - elapsed)), 0, 0.003);
            }
        }
    }

    private static void AssertPeakPixels(VerticalProgressBar bar, Bitmap image, string context)
    {
        var actualPeak = FindPixels(image, Indicator);
        Assert.True(actualPeak == bar.RenderedPeakBounds,
            $"{context}: bitmap marker {actualPeak} differs from actual painter bounds {bar.RenderedPeakBounds}.");
        var active = bar.RenderedActiveBounds;
        var plot = bar.PlotBounds;
        var peakValue = Field<float>(bar, "_peakValue");
        var mappedPeak = plot.Bottom - (int)Math.Round(plot.Height * peakValue / 255f);
        var mode = typeof(VerticalProgressBar).GetMethod("GetVisualizationModeCached", PrivateInstance).Invoke(bar, null);
        var segmentedOffset = (int)typeof(VerticalProgressBar).GetMethod("GetSegmentedPeakOffset", PrivateInstance)
            .Invoke(bar, new object[] { mode, plot });
        var thickness = Math.Max(1, (int)Math.Ceiling(bar.PeakLineThickness * bar.DeviceDpi / 96f));
        var visualValid = Field<bool>(bar, "_visualPeakValid");
        var visualLevel = Field<float>(bar, "_visualPeakLevel");
        var candidateAnchor = Math.Min(mappedPeak + segmentedOffset, active.IsEmpty ? plot.Bottom : active.Top);
        if (visualValid)
            candidateAnchor = Math.Min(candidateAnchor, plot.Bottom -
                (int)Math.Round(plot.Height * Math.Max(0, Math.Min(1, visualLevel / 255f))));
        var candidateTop = candidateAnchor - bar.PeakGapPixels - thickness;
        var shouldFit = (peakValue > 0.5f || visualValid && visualLevel > 0.5f) &&
            plot.Width > 0 && candidateTop >= bar.BrickPadding;
        Assert.True(shouldFit != actualPeak.IsEmpty,
            $"{context}: marker {(shouldFit ? "should fit but is absent" : "must be hidden when it cannot fit")}.");
        if (!bar.HeatmapEnabled)
        {
            var signal = FindPixels(image, IsSignal);
            Assert.True(signal.IsEmpty || active.Contains(signal),
                $"{context}: actual signal pixels {signal} exceed painter extent {active}.");
            Assert.True(signal.IsEmpty || actualPeak.IsEmpty || actualPeak.Bottom + bar.PeakGapPixels <= signal.Top,
                $"{context}: independently detected signal {signal} crosses peak pixels {actualPeak}.");
        }
        if (actualPeak.IsEmpty) return;
        Assert.Equal(candidateAnchor - bar.PeakGapPixels, actualPeak.Bottom);
        Assert.True(bar.ClientRectangle.Contains(actualPeak), $"{context}: clipped marker.");
        Assert.True(active.IsEmpty || actualPeak.Bottom + bar.PeakGapPixels <= active.Top,
            $"{context}: peak {actualPeak} crosses active bar {active}.");
        for (var y = actualPeak.Top; y < actualPeak.Bottom; y++)
        for (var x = actualPeak.Left; x < actualPeak.Right; x++)
            Assert.Equal(Indicator.ToArgb(), image.GetPixel(x, y).ToArgb());
        if (!active.IsEmpty)
        {
            var signalFound = false;
            for (var y = active.Top; y < active.Bottom && !signalFound; y++)
            for (var x = active.Left; x < active.Right; x++)
                if (image.GetPixel(x, y).ToArgb() != bar.BackColor.ToArgb()) { signalFound = true; break; }
            Assert.True(signalFound, $"{context}: reported active bounds contain no painted signal.");
        }
    }

    private static void AssertMeterGridPixels(VerticalProgressBar[] bars)
    {
        var left = bars[0].Left;
        var width = bars[bars.Length - 1].Right - left;
        foreach (var frequency in Frequencies.Skip(1).Take(Frequencies.Length - 2))
        {
            var x = (float)(width * Math.Log(frequency / 20) / Math.Log(1000));
            var bar = bars.First(item => item.Left <= left + x && item.Right > left + x);
            Assert.Contains(bar.VerticalGridlinePositions, position => Math.Abs(position - (left + x - bar.Left)) <= 0.001f);
        }
        using var probe = NewBar("Spectrum");
        probe.Size = new Size(120, 241);
        probe.PeakHoldEnabled = false;
        probe.GridlineColor = Color.Red;
        probe.GridlineLevels = Enumerable.Range(1, 11).Select(index => index / 12f).ToArray();
        probe.VerticalGridlinePositions = new float[] { 17, 37, 83 };
        using var image = Paint(probe);
        var plot = probe.PlotBounds;
        foreach (var index in Enumerable.Range(1, 11))
        {
            var y = plot.Bottom - (int)Math.Round(plot.Height * index / 12d);
            Assert.Equal(Color.Red.ToArgb(), image.GetPixel(60, y).ToArgb());
        }
        foreach (var x in new[] { 17, 37, 83 })
            Assert.Equal(Color.Red.ToArgb(), image.GetPixel(x, plot.Top + 7).ToArgb());
    }

    private static void AssertAdvancedGridPixels(AdvancedVisualizationControl control)
    {
        control.SetSpectrum(new byte[83]);
        var pen = Field<Pen>(control, "_gridPen");
        var originalColor = pen.Color;
        try
        {
            pen.Color = Color.Red;
            using var image = Paint(control);
            var plot = control.SpectrumPlotBounds;
            foreach (var index in Enumerable.Range(1, 11))
            {
                var y = (int)Math.Round(plot.Bottom - plot.Height * index / 12d);
                Assert.True(HasRedNear(image, (int)(plot.Left + plot.Width * 0.61), y),
                    $"{control.Mode}: missing rendered dB grid {index * -6}.");
            }
            foreach (var frequency in Frequencies.Skip(1).Take(Frequencies.Length - 2))
            {
                var x = (int)Math.Round(plot.Left + plot.Width * Math.Log(frequency / 20) / Math.Log(1000));
                Assert.True(HasRedNear(image, x, (int)(plot.Top + plot.Height * 0.43)),
                    $"{control.Mode}: missing rendered log grid {frequency}Hz.");
            }
            var betweenColumns = (int)Math.Round(plot.Left + plot.Width *
                Math.Log(Math.Sqrt(1000d * 2000) / 20) / Math.Log(1000));
            for (var interval = 0; interval < 12; interval++)
            {
                var betweenRows = (int)Math.Round(plot.Top + plot.Height * (interval + 0.5) / 12d);
                var nearestGuideDistance = Enumerable.Range(0, 13)
                    .Min(index => Math.Abs(betweenRows - (plot.Top + plot.Height * index / 12d)));
                Assert.True(nearestGuideDistance > 2,
                    $"{control.Mode}: plot too short to distinguish adjacent grid rows.");
                Assert.False(HasRedNear(image, betweenColumns, betweenRows),
                    $"{control.Mode}: grid occupies the interior of calibrated cell {interval}.");
            }
        }
        finally { pen.Color = originalColor; }
    }

    private static bool HasRedNear(Bitmap image, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            if (x + dx >= 0 && x + dx < image.Width && y + dy >= 0 && y + dy < image.Height)
            {
                var pixel = image.GetPixel(x + dx, y + dy);
                if (pixel.R > pixel.G + 30 && pixel.R > pixel.B + 30) return true;
            }
        return false;
    }

    private static VerticalProgressBar NewBar(string mode)
    {
        var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 0);
        bar.Tag = mode;
        bar.HeatmapEnabled = false;
        bar.ForeColor = Color.Lime;
        bar.BrickHighlight = false;
        bar.PeakHoldEnabled = true;
        bar.PeakColorMatchesHeat = false;
        bar.PeakLineColor = Indicator;
        bar.PeakLineThickness = 2;
        return bar;
    }

    private static void ResetVisualMarker(VerticalProgressBar bar)
    {
        bar.PeakHoldEnabled = false;
        bar.PeakHoldEnabled = true;
    }

    private static Bitmap Paint(Control control)
    {
        var image = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        using var args = new PaintEventArgs(graphics, control.ClientRectangle);
        control.GetType().GetMethod("OnPaint", PrivateInstance).Invoke(control, new object[] { args });
        return image;
    }

    private static Rectangle FindPixels(Bitmap image, Color color) =>
        FindPixels(image, pixel => pixel.ToArgb() == color.ToArgb());

    private static bool IsSignal(Color pixel) => pixel.G >= 160 && pixel.R < 128 && pixel.B < 128;

    private static Rectangle FindPixels(Bitmap image, Func<Color, bool> predicate)
    {
        var minX = image.Width;
        var minY = image.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (!predicate(image.GetPixel(x, y))) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static string FrequencyText(double value) =>
        value >= 1000 ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" :
            value.ToString("0", CultureInfo.InvariantCulture);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void Set(object target, string name, object value) => CombinedRandomizationTests.SetField(target, name, value);
    private static void Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, PrivateInstance).Invoke(target, arguments);
}
