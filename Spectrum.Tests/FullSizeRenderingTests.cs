// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

// Renders at full window size to catch scaling and clipping problems small test sizes would hide.
public class FullSizeRenderingTests
{
    private readonly ITestOutputHelper _output;

    public FullSizeRenderingTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Modes()
    {
        foreach (var mode in FormAudioSpectrum.VisualModeNames)
            yield return new object[] { mode };
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryModeHasAMeasuredFullHdRenderingBudget(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            if (mode == "Spectrum" || mode == "Bricks" || mode == "LED")
            {
                MeasureMeters(mode);
                return;
            }

            var resolve = typeof(FormAudioSpectrum).GetMethod("ResolveAdvancedMode",
                BindingFlags.Static | BindingFlags.NonPublic);
            using var control = new AdvancedVisualizationControl
            {
                Size = new Size(1920, 1080),
                Mode = (AdvancedVisualizationMode)resolve.Invoke(null, new object[] { mode }),
                ThemeName = "Studio"
            };
            control.SetAudioData(NewAnalyticalModeTests.CreateMeasuredData());
            var spectrum = new byte[83];
            for (var row = 0; row < 160; row++)
            {
                for (var band = 0; band < spectrum.Length; band++)
                    spectrum[band] = (byte)(1 + (band * 7 + row * 3) % 254);
                control.SetSpectrum(spectrum);
            }
            using var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            var paint = PaintDelegate(control);
            Measure(mode, () => paint(args));
        });
    }

    private void MeasureMeters(string mode)
    {
        var bars = new VerticalProgressBar[83];
        using var bitmap = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var args = new PaintEventArgs[83];
        var paints = new Action<PaintEventArgs>[83];
        try
        {
            for (var band = 0; band < bars.Length; band++)
            {
                var bar = VisualizationReadabilityTests.CreateBar(
                    BarColorThemes.Studio, 1 + band * 254f / 82);
                bars[band] = bar;
                bar.Size = new Size(21, 1080);
                bar.Tag = mode;
                bar.PeakHoldEnabled = true;
                CombinedRandomizationTests.SetField(bar, "_peakValue", 255f);
                args[band] = new PaintEventArgs(graphics, bar.ClientRectangle);
                paints[band] = PaintDelegate(bar);
            }
            Measure(mode, () =>
            {
                for (var band = 0; band < bars.Length; band++)
                {
                    graphics.TranslateTransform(band * 23, 0);
                    paints[band](args[band]);
                    graphics.ResetTransform();
                }
            });
        }
        finally
        {
            foreach (var paintArgs in args) paintArgs?.Dispose();
            foreach (var bar in bars) bar?.Dispose();
        }
    }

    private void Measure(string mode, Action paint)
    {
        for (var frame = 0; frame < 20; frame++) paint();
        var durations = new double[60];
        var watch = new Stopwatch();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < durations.Length; frame++)
        {
            watch.Restart();
            paint();
            watch.Stop();
            durations[frame] = watch.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var total = 0d;
        foreach (var duration in durations) total += duration;
        Array.Sort(durations);
        var mean = total / durations.Length;
        _output.WriteLine($"{mode}, 1920x1080 premultiplied direct OnPaint: mean={mean:F3}ms, " +
            $"p95={durations[56]:F3}ms, max={durations[59]:F3}ms, " +
            $"allocated={allocated / (double)durations.Length:F1}B/frame. Not compositor FPS.");
        Assert.True(mean < 1000d / 60,
            $"{mode}: {mean:F3}ms mean drawing time exceeds the 16.667ms reference-hardware target.");
    }

    private static Action<PaintEventArgs> PaintDelegate(Control control) =>
        (Action<PaintEventArgs>)Delegate.CreateDelegate(typeof(Action<PaintEventArgs>), control,
            control.GetType().GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic));
}
