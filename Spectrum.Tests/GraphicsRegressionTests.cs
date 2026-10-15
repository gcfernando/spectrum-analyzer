// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

// Drawing regressions: pins down pixel-level behaviour of the renderers so visual tweaks don't sneak in.
public class GraphicsRegressionTests
{
    private readonly ITestOutputHelper _output;
    public GraphicsRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SpectrumTrailShowsRealPreviousDisplayLevelsSeparatelyFromThePeakMarker()
    {
        OnSta(() =>
        {
            using var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 220);
            bar.Size = new Size(24, 200);
            bar.VisualStyle = VisualStyle.Trail;
            bar.PeakHoldEnabled = true;
            Set(bar, "_peakValue", 255f);
            bar.SetTargetValueUI(0);
            Step(bar, 0.05f);
            Step(bar, 0.05f);
            using var trail = VisualizationReadabilityTests.Render(bar);
            var levels = Field<float[]>(bar, "_trailLevels");
            Assert.Contains(220f, levels);
            Assert.Contains(levels, level => level > Field<float>(bar, "_displayValue"));
            bar.VisualStyle = VisualStyle.None;
            using var plain = VisualizationReadabilityTests.Render(bar);
            Assert.True(DifferentPixels(trail, plain) > 10, "The peak separator must not erase the entire trail.");
        });
    }

    [Fact]
    public void TrailsExpireAndLongElapsedStepsSettleMetersAndPeaksToSilence()
    {
        OnSta(() =>
        {
            using var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 255);
            bar.VisualStyle = VisualStyle.Trail;
            bar.PeakHoldEnabled = true;
            bar.PeakHoldMilliseconds = 300;
            Set(bar, "_peakValue", 255f);
            Set(bar, "_peakHoldLeftMs", 300f);
            bar.SetTargetValueUI(0);
            Step(bar, 0.05f);
            Assert.True(Field<bool>(bar, "_trailActive"));
            Step(bar, 10f);
            Assert.Equal(0f, Field<float>(bar, "_displayValue"));
            Assert.Equal(0f, Field<float>(bar, "_peakValue"));
            Assert.False(Field<bool>(bar, "_trailActive"));
            Assert.All(Field<float[]>(bar, "_trailLife"), life => Assert.Equal(0f, life));
        });
    }

    [Fact]
    public void WorkerTargetsAreCoalescedAndWakeTheTimerOnlyThroughTheOwnerThread()
    {
        OnSta(() =>
        {
            using var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 0);
            _ = bar.Handle;
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 100; i++) bar.SetTargetValueThreadSafe(i);
                }
                catch (Exception exception) { failure = exception; }
            }) { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(5000));
            Assert.Null(failure);
            Assert.Equal(99, Field<int>(bar, "_targetValue"));
            Assert.Equal(1, Field<int>(bar, "_timerWakePending"));
            Application.DoEvents();
            Assert.Equal(0, Field<int>(bar, "_timerWakePending"));
        });
    }

    [Fact]
    public void WorkerTargetsBeforeHandleCreationRemainAvailableAndDisposalIsSafe()
    {
        OnSta(() =>
        {
            var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 0);
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try { bar.SetTargetValueThreadSafe(180); }
                catch (Exception exception) { failure = exception; }
            }) { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(5000));
            Assert.Null(failure);
            Assert.False(bar.IsHandleCreated);
            Assert.Equal(180, Field<int>(bar, "_targetValue"));
            _ = bar.Handle;
            bar.Dispose();
            bar.SetTargetValueThreadSafe(10);
        });
    }

    [Fact]
    public void ShowingAMeterSynchronizesTheLatestHiddenTargetWithoutRevivingOldPeaks()
    {
        OnSta(() =>
        {
            using var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 220);
            Set(bar, "_peakValue", 255f);
            bar.Visible = false;
            bar.SetTargetValueUI(0);
            bar.Visible = true;
            Assert.Equal(0f, Field<float>(bar, "_displayValue"));
            Assert.Equal(0f, Field<float>(bar, "_peakValue"));
            bar.Visible = false;
            bar.SetTargetValueUI(140);
            bar.Visible = true;
            Assert.Equal(140f, Field<float>(bar, "_displayValue"));
            Assert.Equal(140f, Field<float>(bar, "_peakValue"));
        });
    }

    [Fact]
    public void BrickPropertiesRebuildCachedGeometryWithoutAResize()
    {
        OnSta(() =>
        {
            using var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 220);
            bar.Tag = "Bricks";
            bar.Size = new Size(24, 200);
            using var initial = VisualizationReadabilityTests.Render(bar);
            var originalCount = Field<List<Rectangle>>(bar, "_brickRects").Count;
            bar.BrickHeight = 10;
            bar.BrickGap = 5;
            bar.BrickPadding = 3;
            Assert.Equal(-1, Field<int>(bar, "_cachedWidth"));
            using var changed = VisualizationReadabilityTests.Render(bar);
            var rectangles = Field<List<Rectangle>>(bar, "_brickRects");
            Assert.True(rectangles.Count < originalCount);
            Assert.Equal(10, rectangles[0].Height);
            Assert.Equal(3, rectangles[0].X);
            Assert.Equal(bar.ClientSize.Width - 6, rectangles[0].Width);
            Assert.Equal(15, rectangles[0].Y - rectangles[1].Y);
            bar.BrickHighlight = false;
            Assert.Null(Field<SolidBrush>(bar, "_brickHighlightBrush"));
            bar.BrickHighlight = true;
            Assert.NotNull(Field<SolidBrush>(bar, "_brickHighlightBrush"));
            Assert.Throws<ArgumentOutOfRangeException>(() => bar.BrickHeight = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => bar.BrickPadding = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => bar.BrickGap = -1);
            bar.BrickGap = int.MaxValue;
            using var largeGap = VisualizationReadabilityTests.Render(bar);
            Assert.Single(Field<List<Rectangle>>(bar, "_brickRects"));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(83)]
    public void RetainedWaveHandlesEmptySingleAndFullBandCounts(int count)
    {
        OnSta(() =>
        {
            using var control = new WaveSpectrumControl(count) { Size = new Size(120, 80), Style = VisualStyle.Trail };
            control.SynchronizeSpectrum(Enumerable.Repeat((byte)200, count).ToArray());
            using var active = VisualizationReadabilityTests.Render(control);
            control.SynchronizeSpectrum(new byte[count]);
            using var silent = VisualizationReadabilityTests.Render(control);
            if (count > 0) Assert.True(DifferentPixels(active, silent) > 20);
        });
    }

    [Fact]
    public void RetainedWaveClearsStaleTargetsAndUsesTheEntireElapsedRelease()
    {
        OnSta(() =>
        {
            using var control = new WaveSpectrumControl(4);
            control.SynchronizeSpectrum(new byte[] { 255, 200, 128, 64 });
            var clock = Field<Stopwatch>(control, "_clock");
            Set(control, "_lastTickTimestamp", clock.ElapsedTicks - 10 * Stopwatch.Frequency);
            Set(control, "_lastSpectrumTimestamp", clock.ElapsedTicks - 10 * Stopwatch.Frequency);
            Invoke(control, "Timer_Tick", null, EventArgs.Empty);
            Assert.All(Field<byte[]>(control, "_targets"), value => Assert.Equal((byte)0, value));
            Assert.All(Field<float[]>(control, "_levels"), value => Assert.Equal(0f, value));
            Assert.False(Field<System.Windows.Forms.Timer>(control, "_timer").Enabled);
        });
    }

    [Fact]
    public void RetainedWaveKeepsFreshTargetsAndFinishesEvenASubpixelSilentResidual()
    {
        OnSta(() =>
        {
            using var control = new WaveSpectrumControl(1);
            control.SetSpectrum(new byte[] { 255 });
            var clock = Field<Stopwatch>(control, "_clock");
            Set(control, "_lastTickTimestamp", clock.ElapsedTicks - Stopwatch.Frequency / 10);
            Invoke(control, "Timer_Tick", null, EventArgs.Empty);
            Assert.Equal((byte)255, Field<byte[]>(control, "_targets")[0]);
            Assert.InRange(Field<float[]>(control, "_levels")[0], 200, 255);
            control.SetSpectrum(new byte[] { 0 });
            Field<float[]>(control, "_levels")[0] = 0.055f;
            Set(control, "_lastTickTimestamp", clock.ElapsedTicks - Stopwatch.Frequency);
            Invoke(control, "Timer_Tick", null, EventArgs.Empty);
            Assert.Equal(0f, Field<float[]>(control, "_levels")[0]);
        });
    }

    [Fact]
    public void SpectralHistoryPreservesExistingIdsAndCopiesOnlyGenuineDisplayFrames()
    {
        Assert.Equal(18, (int)AdvancedVisualizationMode.SpectralHistory);
        Assert.Equal(17, (int)AdvancedVisualizationMode.DynamicsHistory);
        Assert.Equal(9, (int)AdvancedVisualizationMode.OrbitHistory);
        Assert.Equal(11, (int)AdvancedVisualizationMode.TransientMap);
        Assert.Equal(12, (int)AdvancedVisualizationMode.FrequencyRibbon);
        OnSta(() =>
        {
            using var control = HistoryControl();
            var frame = new byte[83];
            frame[40] = 255;
            control.SetSpectrum(frame);
            frame[40] = 0;
            var history = Field<byte[]>(control, "_history").ToArray();
            using var first = VisualizationReadabilityTests.Render(control);
            using var second = VisualizationReadabilityTests.Render(control);
            Assert.Equal(0, DifferentPixels(first, second));
            Assert.Equal(history, Field<byte[]>(control, "_history"));
            Assert.Equal((byte)255, history[40]);
            Assert.Equal(1, Field<int>(control, "_historyCount"));
            Assert.Contains("irregular intervals, not a time axis", Field<string>(control, "_historyCaption"));
            Assert.Equal(VisualStyle.None, control.Style);
            control.Style = VisualStyle.Glow;
            Assert.Equal(VisualStyle.None, control.Style);
            control.Style = VisualStyle.Precision;
            Assert.Equal(VisualStyle.Precision, control.Style);
        });
    }

    [Fact]
    public void SpectralHistoryRecedesThroughBoundedMeasuredFramesAndSilenceRemovesActivity()
    {
        OnSta(() =>
        {
            using var control = HistoryControl();
            using var silent = HistoryControl();
            var signal = new byte[83];
            signal[40] = 255;
            control.SetSpectrum(signal);
            using var active = VisualizationReadabilityTests.Render(control);
            control.SetSpectrum(new byte[83]);
            using var receding = VisualizationReadabilityTests.Render(control);
            Assert.True(DifferentPixels(active, receding) > 20);
            for (var i = 1; i < 64; i++) control.SetSpectrum(new byte[83]);
            for (var i = 0; i < 64; i++) silent.SetSpectrum(new byte[83]);
            using var settled = VisualizationReadabilityTests.Render(control);
            using var empty = VisualizationReadabilityTests.Render(silent);
            Assert.Equal(0, DifferentPixels(settled, empty));
            Assert.Contains("64 display frames", Field<string>(control, "_historyCaption"));
            Assert.Equal(65, Field<int>(control, "_historyCount"));
        });
    }

    [Fact]
    public void SwitchingHistoryModesUpdatesCaptionsWithoutAppendingOrRecoloringMeasurements()
    {
        OnSta(() =>
        {
            using var control = HistoryControl();
            control.SetSpectrum(new byte[] { 200 });
            var history = Field<byte[]>(control, "_history").ToArray();
            control.Mode = AdvancedVisualizationMode.Waterfall;
            Assert.Contains("newest at bottom", Field<string>(control, "_historyCaption"));
            control.Mode = AdvancedVisualizationMode.SpectralHistory;
            Assert.Contains("latest at front", Field<string>(control, "_historyCaption"));
            control.ThemeName = "Ice";
            Assert.Equal(1, Field<int>(control, "_historyCount"));
            Assert.Equal(history, Field<byte[]>(control, "_history"));
        });
    }

    [Theory]
    [InlineData(1, 1, 96)]
    [InlineData(70, 42, 96)]
    [InlineData(900, 280, 96)]
    [InlineData(1350, 420, 144)]
    [InlineData(1800, 560, 192)]
    public void SpectralHistoryAndMeasuredCaptionsRenderAcrossThemesSizesAndDpi(int width, int height, int dpi)
    {
        OnSta(() =>
        {
            using var control = HistoryControl();
            control.Size = new Size(width, height);
            control.SetAudioSamples(new[] { 0.5f, -0.25f, -0.5f, 0.25f }, 2, 2, 48000);
            control.SetSpectrum(Enumerable.Repeat((byte)160, 83).ToArray());
            using var bitmap = new Bitmap(width, height);
            bitmap.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            foreach (var theme in BarColorThemes.Names)
            foreach (var mode in new[] { AdvancedVisualizationMode.SpectralHistory, AdvancedVisualizationMode.StereoLevels,
                AdvancedVisualizationMode.Contour, AdvancedVisualizationMode.Oscilloscope })
            foreach (var style in new[] { VisualStyle.None, VisualStyle.Precision })
            {
                control.ThemeName = theme;
                control.Mode = mode;
                control.Style = style;
                Invoke(control, "OnPaint", args);
                if (width >= 180 && height >= 80)
                {
                    var captionHeight = (int)Invoke(control, "GetCaptionHeight", width, height);
                    Assert.True(captionHeight >= Math.Ceiling(control.Font.GetHeight(graphics)) + 9);
                }
            }
        });
    }

    [Fact]
    public void EnlargedCaptionFontsUseMeasuredHeightRatherThanFixedPixelClipping()
    {
        OnSta(() =>
        {
            using var control = HistoryControl();
            using var font = new Font("Segoe UI", 20);
            control.Font = font;
            using var bitmap = VisualizationReadabilityTests.Render(control);
            using var graphics = Graphics.FromImage(bitmap);
            var captionHeight = (int)Invoke(control, "GetCaptionHeight", control.Width, control.Height);
            Assert.True(captionHeight >= Math.Ceiling(font.GetHeight(graphics)) + 9);
            Assert.True(captionHeight > 22);
        });
    }

    [Fact]
    public void SpectralHistoryPaintingKeepsExistingRealtimeAndAllocationBudgetsAndReleasesItsHistoryPin()
    {
        OnSta(() =>
        {
            var control = HistoryControl();
            try
            {
                for (var row = 0; row < 160; row++)
                    control.SetSpectrum(Enumerable.Range(0, 83).Select(band => (byte)(1 + (band * 7 + row * 3) % 254)).ToArray());
                control.Style = VisualStyle.Precision;
                using var bitmap = VisualizationReadabilityTests.Render(control);
                using var graphics = Graphics.FromImage(bitmap);
                using var args = new PaintEventArgs(graphics, control.ClientRectangle);
                var paint = (Action<PaintEventArgs>)Delegate.CreateDelegate(typeof(Action<PaintEventArgs>), control,
                    typeof(AdvancedVisualizationControl).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic));
                for (var i = 0; i < 20; i++) paint(args);
                var watch = new Stopwatch();
                var before = GC.GetAllocatedBytesForCurrentThread();
                watch.Start();
                for (var i = 0; i < 100; i++) paint(args);
                watch.Stop();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var milliseconds = watch.Elapsed.TotalMilliseconds / 100;
                _output.WriteLine($"Spectral History 900x280 Precision: {milliseconds:F3} ms/OnPaint; {bytes / 100d:F1} bytes/paint");
                Assert.True(milliseconds < 16.7);
                Assert.True(bytes / 100d < 256);
            }
            finally { control.Dispose(); }
            Assert.False(Field<GCHandle>(control, "_historyPixelsHandle").IsAllocated);
        });
    }

    private static AdvancedVisualizationControl HistoryControl() => new()
    {
        Size = new Size(900, 280), Mode = AdvancedVisualizationMode.SpectralHistory, ThemeName = "Studio"
    };

    private static int DifferentPixels(Bitmap left, Bitmap right)
    {
        var count = 0;
        for (var y = 0; y < left.Height; y++)
        for (var x = 0; x < left.Width; x++)
            if (left.GetPixel(x, y) != right.GetPixel(x, y)) count++;
        return count;
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void Set(object target, string name, object value) => CombinedRandomizationTests.SetField(target, name, value);
    private static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    private static void Step(VerticalProgressBar bar, float seconds) => Invoke(bar, "AnimateStep", seconds, seconds * 1000);
    private static void OnSta(Action action) => CombinedRandomizationTests.RunOnSta(action);
}
