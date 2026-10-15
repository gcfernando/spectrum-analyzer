// Developer :: > Gehan Fernando
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

// Tests for the spectrum axis labels and layout.
public class SpectrumAxisTests
{
    [Fact]
    public void ReferenceTicksUseFixedLogViewportAndSixDecibelIntervals()
    {
        Assert.Equal(new double[] { 20, 30, 40, 60, 100, 200, 300, 400, 600, 1000, 2000, 3000, 4000, 6000, 10000, 20000 },
            SpectrumAxis.Frequencies);
        Assert.Equal(Enumerable.Range(0, 13).Select(index => -6d * index), SpectrumAxis.Decibels);
        foreach (var hz in SpectrumAxis.Frequencies)
            Assert.Equal(Math.Log10(hz / 20) / 3, SpectrumAxis.Position(hz), 12);
        Assert.Equal(0, SpectrumAxis.Position(19));
        Assert.Equal(1, SpectrumAxis.Position(21000));
    }

    [Theory]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void ActualBandEdgesTileViewportWithoutStretchingNyquistToTwentyKilohertz(int sampleRate)
    {
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, sampleRate);
        Assert.Equal(0, SpectrumAxis.Position(plan[0].LowerHz));
        for (var band = 1; band < plan.Count; band++)
            Assert.Equal(SpectrumAxis.Position(plan[band - 1].UpperHz),
                SpectrumAxis.Position(plan[band].LowerHz), 12);
        if (sampleRate == 32000)
            Assert.Equal(Math.Log10(16000d / 20) / 3, SpectrumAxis.Position(plan[82].UpperHz), 12);
        else
            Assert.Equal(1, SpectrumAxis.Position(plan[82].UpperHz));
    }

    [Fact]
    public void LocalFrequencyGridRetainsExactViewportCoordinatesIncludingBothEnds()
    {
        const int width = 1103;
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        var actual = Enumerable.Range(0, 83).SelectMany(band =>
        {
            var left = (int)Math.Round(SpectrumAxis.Position(plan[band].LowerHz) * width);
            var right = (int)Math.Round(SpectrumAxis.Position(plan[band].UpperHz) * width);
            return SpectrumAxis.LocalGridPositions(left, right, width).Select(x => x + left);
        }).ToArray();
        Assert.Equal(16, actual.Length);
        for (var tick = 0; tick < actual.Length; tick++)
            Assert.InRange(Math.Abs(actual[tick] - Math.Log10(SpectrumAxis.Frequencies[tick] / 20) / 3 * width), 0, 0.0001);
    }

    [Theory]
    [InlineData("Spectrum", 1f)]
    [InlineData("Bricks", 1f)]
    [InlineData("LED", 1f)]
    [InlineData("Contour", 1f)]
    [InlineData("Peak Trace", 1f)]
    [InlineData("Threshold Monitor", 1f)]
    [InlineData("Spectrum", 2f)]
    [InlineData("Peak Trace", 2f)]
    public void MinimumWindowKeepsAllThirteenDbLabelsAlignedAndUncrowded(string mode, float scale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: mode);
            ShowWithoutCapture(form);
            var labels = CombinedRandomizationTests.GetField<Label[]>(form, "_dbAxisLabelsLeft");
            using var font = new Font(labels[0].Font.FontFamily, labels[0].Font.SizeInPoints * scale);
            foreach (var label in labels) label.Font = font;
            foreach (var label in CombinedRandomizationTests.GetField<Label[]>(form, "_dbAxisLabelsRight")) label.Font = font;
            form.ClientSize = new Size(900, scale > 1 ? 650 : 420);
            var expectedSize = form.ClientSize;
            Reflow(form);
            Assert.Equal(expectedSize, form.ClientSize);
            var bars = CombinedRandomizationTests.GetField<VerticalProgressBar[]>(form, "_progressBars");
            var advanced = CombinedRandomizationTests.GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            var plot = bars[0].Visible
                ? new RectangleF(bars[0].Left, bars[0].Top + bars[0].PlotBounds.Top, bars[0].Width, bars[0].PlotBounds.Height)
                : new RectangleF(advanced.Left, advanced.Top + advanced.SpectrumPlotBounds.Top, advanced.Width, advanced.SpectrumPlotBounds.Height);
            for (var tick = 0; tick < 13; tick++)
            {
                Assert.True(labels[tick].Visible,
                    $"{mode}/{scale}: {labels[tick].Text} hidden at {form.ClientSize}, minimum {form.MinimumSize}.");
                var expected = plot.Top + tick * plot.Height / 12;
                Assert.InRange(Math.Abs(labels[tick].Top + labels[tick].Height / 2 - expected), 0, 1);
                if (tick > 0) Assert.True(labels[tick].Top >= labels[tick - 1].Bottom + 4);
            }
        });
    }

    [Fact]
    public void NonSpectrumModesKeepTheirOriginalTwelveDecibelAxisContract()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: "Center");
            typeof(FormAudioSpectrum).GetMethod("RecalculateBarLayout", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            var labels = CombinedRandomizationTests.GetField<Label[]>(form, "_dbAxisLabelsLeft");
            for (var index = 1; index < labels.Length; index += 2)
                Assert.False(labels[index].Visible);
        });
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void StereoMinimumWindowRendersAllThirteenDbLabelsInBothPanels(float fontScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: "Stereo Spectrum");
            ShowWithoutCapture(form);
            var control = CombinedRandomizationTests.GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            using var font = new Font(control.Font.FontFamily, control.Font.SizeInPoints * fontScale);
            control.Font = font;
            form.ClientSize = new Size(900, 350);
            var expectedSize = form.ClientSize;
            Reflow(form);
            Assert.Equal(expectedSize, form.ClientSize);
            using var image = VisualizationReadabilityTests.Render(control);
            var gridPen = CombinedRandomizationTests.GetField<Pen>(control, "_gridPen");
            var gridColor = gridPen.Color;
            gridPen.Color = Color.Transparent;
            using var withoutGrid = VisualizationReadabilityTests.Render(control);
            gridPen.Color = gridColor;
            var bodyFont = CombinedRandomizationTests.GetField<Font>(control, "_analysisBodyFont");
            var bodyHeight = bodyFont.GetHeight(control.DeviceDpi);
            for (var channel = 0; channel < 2; channel++)
            {
                var plot = control.GetStereoSpectrumPlotBounds(channel);
                Assert.True(plot.Height / 12 >= bodyHeight + 2,
                    $"Panel {channel} gives {plot.Height / 12}px per label requiring {bodyHeight}px.");
                for (var tick = 0; tick < 13; tick++)
                {
                    var y = plot.Top + plot.Height * tick / 12;
                    var textBounds = Rectangle.FromLTRB(Math.Max(0, (int)(plot.Left - 48 * control.DeviceDpi / 96f)),
                        Math.Max(0, (int)(y - bodyHeight / 2)),
                        (int)plot.Left - 3, Math.Min(image.Height, (int)Math.Ceiling(y + bodyHeight / 2)));
                    Assert.True(CountNonBackground(image, textBounds, control.BackColor) > 2,
                        $"Panel {channel} has no rendered {tick * -6} dB text at font scale {fontScale}.");
                    var row = new Rectangle((int)(plot.Left + plot.Width * 0.87), (int)Math.Round(y) - 1, 4, 3);
                    Assert.True(CountDifferences(image, withoutGrid, row) > 0,
                        $"Panel {channel} has no rendered {tick * -6} dB grid.");
                }
                foreach (var hz in SpectrumAxis.Frequencies)
                {
                    var x = plot.Left + plot.Width * Math.Log10(hz / 20) / 3;
                    var column = new Rectangle((int)Math.Round(x) - 1,
                        (int)(plot.Top + plot.Height * 5.5 / 12), 3, 4);
                    Assert.True(CountDifferences(image, withoutGrid, column) > 0,
                        $"Panel {channel} has no rendered {hz} Hz grid.");
                }
            }
        });
    }

    [Theory]
    [InlineData("Spectrum")]
    [InlineData("Bricks")]
    [InlineData("LED")]
    public void RenderedMeterDataAtThirtyTwoKilohertzLeavesTheUpperViewportUnmeasured(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: mode);
            ShowWithoutCapture(form);
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 32000);
            CombinedRandomizationTests.SetField(form, "_appliedPlan", plan);
            form.ClientSize = new Size(1184, 500);
            Reflow(form);
            var bars = CombinedRandomizationTests.GetField<VerticalProgressBar[]>(form, "_progressBars");
            foreach (var bar in bars)
            {
                bar.PeakHoldEnabled = false;
                CombinedRandomizationTests.SetField(bar, "_displayValue", 0f);
            }
            using var silent = VisualizationReadabilityTests.Render(form);
            foreach (var bar in bars) CombinedRandomizationTests.SetField(bar, "_displayValue", 255f);
            using var active = VisualizationReadabilityTests.Render(form);
            var container = bars[0].Parent;
            var origin = form.PointToClient(container.PointToScreen(Point.Empty));
            var left = origin.X + bars[0].Left;
            var viewportWidth = container.ClientSize.Width - 2 * bars[0].Left;
            var nyquist = left + viewportWidth * Math.Log10(16000d / 20) / 3;
            var plot = bars[0].PlotBounds;
            var tail = Rectangle.FromLTRB((int)Math.Ceiling(nyquist) + 3,
                origin.Y + bars[0].Top + plot.Top + 2, left + viewportWidth - 2,
                origin.Y + bars[0].Top + plot.Bottom - 2);
            Assert.True(tail.Width > 10);
            Assert.Equal(0, CountDifferences(silent, active, tail));
            var measured = new Rectangle(left + viewportWidth / 2, tail.Top, 20, tail.Height);
            Assert.True(CountDifferences(silent, active, measured) > 100);
            Assert.InRange(Math.Abs(origin.X + bars[82].Right - nyquist), 0, 0.6);
        });
    }

    [Theory]
    [InlineData(AdvancedVisualizationMode.Contour)]
    [InlineData(AdvancedVisualizationMode.PeakTrace)]
    [InlineData(AdvancedVisualizationMode.ThresholdMonitor)]
    [InlineData(AdvancedVisualizationMode.StereoSpectrum)]
    public void RenderedAnalyticSpectraAtThirtyTwoKilohertzDoNotStretchDataToTwentyKilohertz(AdvancedVisualizationMode mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Size = new Size(1184, 650), Mode = mode, ThemeName = "Studio"
            };
            control.SetBandPlan(BandPlan.CreateLogarithmic(83, 20, 20000, 32000));
            var audio = new AudioVisualizationData();
            audio.Update(new float[] { 0, 0 }, 1, 2, 32000);
            audio.SetChannelSpectrum(new double[83], new double[83]);
            control.SetAudioData(audio);
            control.SetSpectrum(new byte[83]);
            using var silent = VisualizationReadabilityTests.Render(control);
            audio.SetChannelSpectrum(Enumerable.Repeat(1d, 83).ToArray(), Enumerable.Repeat(1d, 83).ToArray());
            control.SetAudioData(audio);
            control.SetSpectrum(Enumerable.Repeat((byte)255, 83).ToArray());
            using var active = VisualizationReadabilityTests.Render(control);
            for (var channel = 0; channel < (mode == AdvancedVisualizationMode.StereoSpectrum ? 2 : 1); channel++)
            {
                var plot = mode == AdvancedVisualizationMode.StereoSpectrum
                    ? control.GetStereoSpectrumPlotBounds(channel) : control.SpectrumPlotBounds;
                var nyquistX = plot.Left + plot.Width * Math.Log10(16000d / 20) / 3;
                var tail = Rectangle.FromLTRB((int)Math.Ceiling(nyquistX) + 4, (int)Math.Ceiling(plot.Top) + 4,
                    (int)plot.Right - 2, (int)plot.Bottom - 2);
                Assert.True(tail.Width > 10);
                Assert.Equal(0, CountDifferences(silent, active, tail));
                var measured = Rectangle.FromLTRB((int)(plot.Left + plot.Width / 2), (int)plot.Top - 1,
                    (int)(plot.Left + plot.Width / 2) + 20, (int)plot.Bottom - 2);
                Assert.True(CountDifferences(silent, active, measured) > 10);
            }
        });
    }

    private static void ShowWithoutCapture(FormAudioSpectrum form)
    {
        // Program.Main switches to GDI text before building controls and a test host doesn't, so we do it by hand here.
        foreach (var label in DescendantLabels(form)) label.UseCompatibleTextRendering = false;
        CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
        var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", BindingFlags.Instance | BindingFlags.NonPublic);
        form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
        form.ShowInTaskbar = false;
        form.Show();
    }

    private static System.Collections.Generic.IEnumerable<Label> DescendantLabels(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Label label) yield return label;
            foreach (var descendant in DescendantLabels(control)) yield return descendant;
        }
    }

    private static void Reflow(FormAudioSpectrum form)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            typeof(FormAudioSpectrum).GetMethod("PositionSelectionPanel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            typeof(FormAudioSpectrum).GetMethod("RecalculateBarLayout", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            form.PerformLayout();
        }
    }

    private static int CountNonBackground(Bitmap image, Rectangle bounds, Color background)
    {
        var count = 0;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
            if (image.GetPixel(x, y).ToArgb() != background.ToArgb()) count++;
        return count;
    }

    private static int CountDifferences(Bitmap first, Bitmap second, Rectangle bounds)
    {
        var count = 0;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
            if (first.GetPixel(x, y) != second.GetPixel(x, y)) count++;
        return count;
    }
}
