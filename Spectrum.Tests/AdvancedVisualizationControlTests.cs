using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using Spectrum;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public class AdvancedVisualizationControlTests
{
    [Fact]
    public void EverySharedViewModeRendersActiveBandsAtNormalAndCompactSizes()
    {
        RunOnSta(() =>
        {
            var modes = new[]
            {
                AdvancedVisualizationMode.Waterfall,
                AdvancedVisualizationMode.RadialSpectrum,
                AdvancedVisualizationMode.Contour,
                AdvancedVisualizationMode.NoteMap,
                AdvancedVisualizationMode.PeakTrace,
                AdvancedVisualizationMode.ThresholdMonitor,
                AdvancedVisualizationMode.BandMatrix,
                AdvancedVisualizationMode.OctaveSpectrum,
                AdvancedVisualizationMode.SpectralFlux,
                AdvancedVisualizationMode.OrbitHistory,
                AdvancedVisualizationMode.OctaveWaterfall,
                AdvancedVisualizationMode.TransientMap,
                AdvancedVisualizationMode.FrequencyRibbon
            };
            var values = new byte[AdvancedVisualizationControl.BandCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = (byte)(64 + ((i * 191) / (values.Length - 1)));

            using var control = new AdvancedVisualizationControl();
            foreach (var size in new[] { new Size(320, 180), new Size(70, 42) })
            {
                control.Size = size;
                foreach (var mode in modes)
                {
                    control.Mode = mode;
                    control.SetSpectrum(values, "Aurora", mode);
                    using var bitmap = new Bitmap(size.Width, size.Height);
                    control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
                    Assert.True(CountNonBackgroundPixels(bitmap, control.BackColor) > 0,
                        $"{mode} should paint the active spectrum.");
                }
            }
        });
    }

    [Fact]
    public void WaterfallCopiesFramesInsteadOfRetainingCallerBuffer()
    {
        RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.Waterfall,
                Size = new Size(120, 80),
                HistoryFrames = 2
            };
            var values = new byte[AdvancedVisualizationControl.BandCount];
            values[0] = 255;
            control.SetSpectrum(values);
            Array.Clear(values, 0, values.Length);

            using var bitmap = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            Assert.True(CountNonBackgroundPixels(bitmap, control.BackColor) > 0);
        });
    }

    [Fact]
    public void EveryExposedAdvancedModeStyleThemeCombinationRenders()
    {
        RunOnSta(() =>
        {
            var modes = new[]
            {
                AdvancedVisualizationMode.Waterfall, AdvancedVisualizationMode.RadialSpectrum, AdvancedVisualizationMode.Contour,
                AdvancedVisualizationMode.PeakTrace, AdvancedVisualizationMode.ThresholdMonitor, AdvancedVisualizationMode.BandMatrix,
                AdvancedVisualizationMode.OctaveSpectrum, AdvancedVisualizationMode.SpectralFlux, AdvancedVisualizationMode.OrbitHistory,
                AdvancedVisualizationMode.OctaveWaterfall, AdvancedVisualizationMode.TransientMap, AdvancedVisualizationMode.FrequencyRibbon
            };
            var values = new byte[AdvancedVisualizationControl.BandCount];
            for (var band = 0; band < values.Length; band++)
                values[band] = (byte)(1 + ((band * 254) / (values.Length - 1)));

            using var control = new AdvancedVisualizationControl { Size = new Size(220, 120) };
            foreach (var mode in modes)
            {
                foreach (var theme in BarColorThemes.Names)
                {
                    foreach (var styleName in VisualStyles.GetSupportedNames(GetModeName(mode)))
                    {
                        control.Mode = mode;
                        control.Style = VisualStyles.Parse(styleName);
                        control.SetSpectrum(values, theme, mode);
                        using var bitmap = new Bitmap(control.Width, control.Height);
                        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
                        Assert.True(CountNonBackgroundPixels(bitmap, control.BackColor) > 0,
                            $"{mode} / {styleName} / {theme} should render.");
                    }
                }
            }
        });
    }

    [Fact]
    public void NoteMapAcceptsTheAnalyzerBandPlanForTheActiveSampleRate()
    {
        RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.NoteMap,
                Size = new Size(320, 180)
            };
            var values = new byte[AdvancedVisualizationControl.BandCount];
            values[AdvancedVisualizationControl.BandCount - 1] = 255;
            control.SetBandPlan(BandPlan.CreateLogarithmic(values.Length, 20, 20000, 32000));
            control.SetSpectrum(values);

            using var bitmap = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            Assert.True(CountNonBackgroundPixels(bitmap, control.BackColor) > 0);
        });
    }

    [Fact]
    public void LollipopRendersDifferentlyFromTheStandardSpectrum()
    {
        RunOnSta(() =>
        {
            using var spectrum = CreateBar("Spectrum|1");
            using var lollipop = CreateBar("Lollipop|1");
            SetDisplayedLevel(spectrum, 200);
            SetDisplayedLevel(lollipop, 200);

            using var spectrumBitmap = new Bitmap(spectrum.Width, spectrum.Height);
            using var lollipopBitmap = new Bitmap(lollipop.Width, lollipop.Height);
            spectrum.DrawToBitmap(spectrumBitmap, new Rectangle(Point.Empty, spectrum.Size));
            lollipop.DrawToBitmap(lollipopBitmap, new Rectangle(Point.Empty, lollipop.Size));

            var differentPixels = 0;
            for (var y = 0; y < spectrumBitmap.Height; y++)
            {
                for (var x = 0; x < spectrumBitmap.Width; x++)
                {
                    if (spectrumBitmap.GetPixel(x, y).ToArgb() != lollipopBitmap.GetPixel(x, y).ToArgb())
                        differentPixels++;
                }
            }

            Assert.True(differentPixels > 20);
        });
    }

    [Fact]
    public void SpectrumUsesAContinuousFillWhileBricksRemainSegmented()
    {
        RunOnSta(() =>
        {
            using var spectrum = CreateBar("Spectrum|1");
            using var bricks = CreateBar("Bricks|1");
            spectrum.ForeColor = bricks.ForeColor = Color.DodgerBlue;
            spectrum.HeatmapEnabled = bricks.HeatmapEnabled = false;
            spectrum.BrickHighlight = bricks.BrickHighlight = false;
            SetDisplayedLevel(spectrum, 200);
            SetDisplayedLevel(bricks, 200);

            using var spectrumBitmap = new Bitmap(spectrum.Width, spectrum.Height);
            using var bricksBitmap = new Bitmap(bricks.Width, bricks.Height);
            spectrum.DrawToBitmap(spectrumBitmap, new Rectangle(Point.Empty, spectrum.Size));
            bricks.DrawToBitmap(bricksBitmap, new Rectangle(Point.Empty, bricks.Size));

            for (var y = 35; y <= 110; y++)
                Assert.Equal(Color.DodgerBlue.ToArgb(), spectrumBitmap.GetPixel(spectrum.Width / 2, y).ToArgb());

            Assert.Contains(Enumerable.Range(35, 76),
                y => bricksBitmap.GetPixel(bricks.Width / 2, y).ToArgb() != Color.DodgerBlue.ToArgb());
        });
    }

    [Fact]
    public void LedRendersActiveCellsAsCirclesInsteadOfBrickRectangles()
    {
        RunOnSta(() =>
        {
            using var led = CreateBar("LED|1");
            using var bricks = CreateBar("Bricks|1");
            led.ForeColor = bricks.ForeColor = Color.DodgerBlue;
            led.HeatmapEnabled = bricks.HeatmapEnabled = false;
            led.BrickHighlight = bricks.BrickHighlight = false;
            SetDisplayedLevel(led, 200);
            SetDisplayedLevel(bricks, 200);

            using var ledBitmap = Render(led);
            using var brickBitmap = Render(bricks);

            const int cellLeft = 6;
            const int cellTop = 113;
            const int cellCenterX = 9;
            const int cellCenterY = 116;

            Assert.Equal(Color.DodgerBlue.ToArgb(), ledBitmap.GetPixel(cellCenterX, cellCenterY).ToArgb());
            Assert.Equal(led.BackColor.ToArgb(), ledBitmap.GetPixel(cellLeft, cellTop).ToArgb());
            Assert.NotEqual(bricks.BackColor.ToArgb(), brickBitmap.GetPixel(cellLeft, cellTop).ToArgb());
        });
    }

    [Fact]
    public void LedKeepsTheSegmentedCellCountOfTheBrickMeter()
    {
        RunOnSta(() =>
        {
            using var led = CreateBar("LED|1");
            using var bricks = CreateBar("Bricks|1");
            led.ForeColor = bricks.ForeColor = Color.DodgerBlue;
            led.HeatmapEnabled = bricks.HeatmapEnabled = false;
            led.BrickHighlight = bricks.BrickHighlight = false;
            SetDisplayedLevel(led, 200);
            SetDisplayedLevel(bricks, 200);

            using var ledBitmap = Render(led);
            using var brickBitmap = Render(bricks);

            var ledCellCount = CountActiveCellCenters(ledBitmap);
            Assert.True(ledCellCount > 0);
            Assert.Equal(ledCellCount, CountActiveCellCenters(brickBitmap));
            Assert.True(CountColorPixels(ledBitmap, Color.DodgerBlue) < CountColorPixels(brickBitmap, Color.DodgerBlue));
        });
    }

    [Fact]
    public void WaveConnectsAdjacentFrequencyBandsAcrossTheFullWidth()
    {
        var levels = new[] { 0f, 255f, 64f, 192f };
        var points = new PointF[levels.Length];

        WaveSpectrumControl.PopulateFrequencyPoints(levels, 401, 101, points);

        Assert.Equal(0f, points[0].X);
        Assert.Equal(400f, points[points.Length - 1].X);
        Assert.Equal(100f, points[0].Y);
        Assert.Equal(0f, points[1].Y);
        for (var i = 1; i < points.Length; i++)
            Assert.Equal(400f / (levels.Length - 1), points[i].X - points[i - 1].X, 3);
    }

    private static VerticalProgressBar CreateBar(string mode) => new()
    {
        BackColor = Color.FromArgb(50, 50, 50),
        Maximum = 255,
        Size = new Size(18, 120),
        Tag = mode,
        BrickPadding = 1,
        GridlineLevels = Array.Empty<float>(),
        PeakHoldEnabled = false
    };

    private static string GetModeName(AdvancedVisualizationMode mode) => mode switch
    {
        AdvancedVisualizationMode.RadialSpectrum => "Radial Spectrum",
        AdvancedVisualizationMode.PeakTrace => "Peak Trace",
        AdvancedVisualizationMode.ThresholdMonitor => "Threshold Monitor",
        AdvancedVisualizationMode.BandMatrix => "Band Matrix",
        AdvancedVisualizationMode.OctaveSpectrum => "Octave Spectrum",
        AdvancedVisualizationMode.SpectralFlux => "Spectral Flux",
        AdvancedVisualizationMode.OrbitHistory => "Orbit History",
        AdvancedVisualizationMode.OctaveWaterfall => "Octave Waterfall",
        AdvancedVisualizationMode.TransientMap => "Transient Map",
        AdvancedVisualizationMode.FrequencyRibbon => "Frequency Ribbon",
        _ => mode.ToString()
    };

    private static void SetDisplayedLevel(VerticalProgressBar bar, float level)
    {
        typeof(VerticalProgressBar)
            .GetField("_displayValue", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(bar, level);
    }

    private static Bitmap Render(VerticalProgressBar bar)
    {
        var bitmap = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bar.Size));
        return bitmap;
    }

    private static int CountActiveCellCenters(Bitmap bitmap)
    {
        var active = 0;
        for (var y = 116; y >= 4; y -= 8)
        {
            if (bitmap.GetPixel(bitmap.Width / 2, y).ToArgb() == Color.DodgerBlue.ToArgb())
                active++;
        }
        return active;
    }

    private static int CountColorPixels(Bitmap bitmap, Color color)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).ToArgb() == color.ToArgb())
                    count++;
            }
        }
        return count;
    }

    private static int CountNonBackgroundPixels(Bitmap bitmap, Color background)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).ToArgb() != background.ToArgb())
                    count++;
            }
        }
        return count;
    }

    private static void RunOnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw failure;
    }
}
