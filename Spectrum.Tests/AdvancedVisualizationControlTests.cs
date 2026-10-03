using System;
using System.Drawing;
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
                AdvancedVisualizationMode.NoteMap
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

    private static void SetDisplayedLevel(VerticalProgressBar bar, float level)
    {
        typeof(VerticalProgressBar)
            .GetField("_displayValue", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(bar, level);
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
