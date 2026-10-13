using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Spectrum.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

public class NewAnalyticalModeTests
{
    private readonly ITestOutputHelper _output;
    public NewAnalyticalModeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void BothApprovedModesAreReachableByManualSelectionAndCombinedRandom()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var selector = CombinedRandomizationTests.GetField<ComboBox>(form, "_modeSelector");
            var control = CombinedRandomizationTests.GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            foreach (var item in new[] { ("Stereo Spectrum", AdvancedVisualizationMode.StereoSpectrum), ("Dynamics History", AdvancedVisualizationMode.DynamicsHistory) })
            {
                selector.SelectedItem = item.Item1;
                Assert.Equal(item.Item2, control.Mode);
                Assert.Equal(new[] { "None", "Precision" }, VisualStyles.GetSupportedNames(item.Item1));
                Assert.Equal("None", FormAudioSpectrum.GetAppliedVisualStyle(item.Item1, "Glow"));
            }
            var modes = selector.Items.Cast<string>().ToArray();
            var randomizer = new VisualSelectionRandomizer();
            var random = new Random(8317);
            var current = new VisualSelection("Spectrum", "Glow", "Ice");
            var visited = new System.Collections.Generic.HashSet<string>();
            for (var i = 0; i < 2000; i++)
            {
                var next = randomizer.Next(current, modes, BarColorThemes.Names, random);
                Assert.NotEqual(current.Mode, next.Mode);
                Assert.NotEqual(current.Style, next.Style);
                Assert.NotEqual(current.Theme, next.Theme);
                Assert.Contains(next.Style, VisualStyles.GetSupportedNames(next.Mode));
                visited.Add(next.Mode); current = next;
            }
            Assert.Contains("Stereo Spectrum", visited);
            Assert.Contains("Dynamics History", visited);
        });
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(70, 42)]
    [InlineData(512, 180)]
    [InlineData(340, 180)]
    [InlineData(480, 180)]
    [InlineData(900, 280)]
    [InlineData(480, 600)]
    public void RenderAllThemesAndStylesWithMeasuredUnavailableAndMonoInput(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var data = CreateMeasuredData();
            using var control = new AdvancedVisualizationControl { Size = new Size(width, height) };
            foreach (var mode in new[] { AdvancedVisualizationMode.StereoSpectrum, AdvancedVisualizationMode.DynamicsHistory })
            foreach (var theme in BarColorThemes.Names)
            foreach (var style in new[] { VisualStyle.None, VisualStyle.Precision })
            {
                control.Mode = mode; control.ThemeName = theme; control.Style = style;
                control.SetAudioData(data);
                using var active = VisualizationReadabilityTests.Render(control);
                control.SetAudioData(null);
                using var absent = VisualizationReadabilityTests.Render(control);
                control.SetAudioSamples(new[] { 0f, 0f }, 2, 1, 48000);
                using var silence = VisualizationReadabilityTests.Render(control);
                Assert.Equal(width, active.Width); Assert.Equal(height, active.Height);
            }
        });
    }

    [Fact]
    public void StereoDataIsNotDerivedFromTheCombinedSpectrumAndScopeSwitchUsesCurrentHistory()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var data = CreateMeasuredData();
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
            typeof(FormAudioSpectrum).GetMethod("Spectrum_Change", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { null, new OnChangeEventArgs(new byte[83], data) { Plan = plan } });
            data.Clear();
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var selector = CombinedRandomizationTests.GetField<ComboBox>(form, "_modeSelector");
            var control = CombinedRandomizationTests.GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            selector.SelectedItem = "Stereo Spectrum";
            var actual = CombinedRandomizationTests.GetField<AudioVisualizationData>(control, "_audio");
            Assert.True(actual.HasChannelSpectrum);
            Assert.True(actual.LeftSpectrum[25] > actual.RightSpectrum[25]);
            Assert.True(actual.RightSpectrum[65] > actual.LeftSpectrum[65]);
            selector.SelectedItem = "Dynamics History";
            Assert.Equal(1024, actual.ObservationCount);
            Assert.Same(plan, CombinedRandomizationTests.GetField<BandPlan>(control, "_bandPlan"));
        });
    }

    [Fact]
    public void UnavailableStereoCaptionsDoNotClaimMonoOrMeasuredFloor()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var data = CreateMeasuredData();
            data.SetUnavailableWindow(data.EndFrame, 2, 48000, data.SourceEpoch);
            control.SetAudioData(data);
            var labels = CombinedRandomizationTests.GetField<string[]>(control, "_channelSpectrumLabels");
            Assert.Equal("LEFT  •  unavailable", labels[0]);
            Assert.Equal("RIGHT  •  unavailable", labels[1]);
            control.SetAudioSamples(new[] { 0f }, 1, 1, 48000);
            Assert.Equal("MONO  •  unavailable", labels[0]);
            Assert.Equal("RIGHT  •  unavailable", labels[1]);
        });
    }

    [Fact]
    public void CompactChannelPanelsRetainVisibleIdentityAboveTheirTraces()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(280, 180), ThemeName = "Studio" };
            control.SetAudioData(CreateMeasuredData());
            var textColor = BarColorThemes.Studio.Ui.PrimaryText.ToArgb();
            foreach (var mode in new[] { AdvancedVisualizationMode.StereoSpectrum, AdvancedVisualizationMode.DynamicsHistory })
            {
                control.Mode = mode;
                using var bitmap = VisualizationReadabilityTests.Render(control);
                foreach (var panelY in new[] { 0, 90 })
                {
                    var textPixels = 0;
                    for (var y = panelY + 2; y < panelY + 16; y++)
                    for (var x = 2; x < 55; x++)
                        if (bitmap.GetPixel(x, y).ToArgb() == textColor) textPixels++;
                    Assert.True(textPixels > 5, $"{mode} must identify each compact channel panel.");
                }
            }
        });
    }

    [Fact]
    public void DynamicsHeadingFitsTheSmallestLabeledViewport()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(480, 180), Mode = AdvancedVisualizationMode.DynamicsHistory };
            control.SetAudioData(CreateMeasuredData());
            using var bitmap = VisualizationReadabilityTests.Render(control);
            using var graphics = Graphics.FromImage(bitmap);
            var font = CombinedRandomizationTests.GetField<Font>(control, "_analysisBodyFont");
            var caption = CombinedRandomizationTests.GetField<string>(control, "_dynamicsCaption");
            Assert.True(graphics.MeasureString(caption, font).Width <= 464, "A complete caption must fit between the viewport insets.");
        });
    }

    [Fact]
    public void AMeasuredChannelAtFloorDoesNotInventADominantFrequency()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var data = new AudioVisualizationData();
            data.Update(new float[2], 1, 2, 48000);
            data.SetChannelSpectrum(new double[83], new double[83]);
            control.SetAudioData(data);
            var labels = CombinedRandomizationTests.GetField<string[]>(control, "_channelSpectrumLabels");
            Assert.Equal("LEFT  •  no band above display floor", labels[0]);
            Assert.Equal("RIGHT  •  no band above display floor", labels[1]);
        });
    }

    [Fact]
    public void HistoryPaintUsesProportionalSampleTimeAndDoesNotBridgeMissingIntervals()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(900, 280), ThemeName = "Studio", Mode = AdvancedVisualizationMode.DynamicsHistory };
            var data = new AudioVisualizationData();
            var epoch = new object();
            data.UpdateObservedWindow(Enumerable.Repeat(0.5f, 2000).ToArray(), 2000, 1, 1000, 2000, 0, 0, epoch);
            data.SetUnavailableWindow(5000, 1, 1000, epoch);
            data.UpdateObservedWindow(Enumerable.Repeat(0.25f, 3000).ToArray(), 3000, 1, 1000, 8000, 5000, 5000, epoch);
            data.UpdateObservedWindow(Enumerable.Repeat(0.125f, 4000).ToArray(), 4000, 1, 1000, 12000, 8000, 5000, epoch);
            control.SetAudioData(data);
            using var bitmap = VisualizationReadabilityTests.Render(control);
            using var graphics = Graphics.FromImage(bitmap);
            var body = CombinedRandomizationTests.GetField<Font>(control, "_analysisBodyFont");
            var title = CombinedRandomizationTests.GetField<Font>(control, "_analysisTitleFont");
            var top = body.GetHeight(graphics) + 10 + title.GetHeight(graphics) + 8;
            var bottom = 280 - body.GetHeight(graphics) - 8 - 7;
            var firstY = (int)Math.Round(bottom - (bottom - top) * LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(0.5)));
            var secondY = (int)Math.Round(bottom - (bottom - top) * LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(0.25)));
            var peak = BarColorThemes.Studio.Ui.PrimaryText.ToArgb();
            var firstLength = CountTracePixels(bitmap, firstY, peak);
            var secondLength = CountTracePixels(bitmap, secondY, peak);
            Assert.InRange(firstLength, 78, 92); // 2 seconds in a 20-second, 842-pixel plotting area.
            Assert.InRange(secondLength / (double)firstLength, 1.4, 1.6); // 3 seconds uses 1.5× the width.
            for (var x = 490; x < 575; x++)
            for (var y = firstY - 2; y <= secondY + 2; y++)
                Assert.NotEqual(peak, bitmap.GetPixel(x, y).ToArgb()); // Missing source interval stays empty.
            data.UpdateObservedWindow(new float[1000], 1000, 1, 1000, 13000, 12000, 0, new object());
            control.SetAudioData(data);
            using var newEpoch = VisualizationReadabilityTests.Render(control);
            Assert.Equal(0, CountTracePixels(newEpoch, firstY, peak));
        });
    }

    private static int CountTracePixels(Bitmap bitmap, int y, int argb)
    {
        var count = 0;
        for (var x = 60; x < 880; x++)
            if (Enumerable.Range(y - 1, 3).Any(row => bitmap.GetPixel(x, row).ToArgb() == argb)) count++;
        return count;
    }

    [Fact]
    public void HistoryPaintDoesNotChangeOtherModesPeakPenAtDifferentDpi()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(1350, 420), Mode = AdvancedVisualizationMode.DynamicsHistory };
            control.SetAudioData(CreateMeasuredData());
            var pen = CombinedRandomizationTests.GetField<Pen>(control, "_peakPen");
            var width = pen.Width;
            var dash = pen.DashStyle;
            using var bitmap = new Bitmap(1350, 420);
            bitmap.SetResolution(144, 144);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            PaintDelegate(control)(args);
            Assert.Equal(width, pen.Width);
            Assert.Equal(dash, pen.DashStyle);
        });
    }

    [Fact]
    public void ActualPaintMetricsScaleAtOneHundredFiftyPercentDpi()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(1350, 420) };
            control.SetAudioData(CreateMeasuredData());
            using var bitmap = new Bitmap(1350, 420);
            bitmap.SetResolution(144, 144);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            var paint = PaintDelegate(control);
            foreach (var mode in new[] { AdvancedVisualizationMode.StereoSpectrum, AdvancedVisualizationMode.DynamicsHistory })
            {
                control.Mode = mode; paint(args);
                var font = CombinedRandomizationTests.GetField<Font>(control, "_analysisBodyFont");
                Assert.True(font.GetHeight(graphics) > font.GetHeight(96));
            }
        });
    }

    [Fact]
    public void FullHistoryAndStereoPaintingFitExistingRealtimeAndAllocationBudgets()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(900, 280), ThemeName = "Studio" };
            control.SetAudioData(CreateMeasuredData());
            using var bitmap = VisualizationReadabilityTests.Render(control);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            var paint = PaintDelegate(control);
            var watch = new Stopwatch();
            foreach (var mode in new[] { AdvancedVisualizationMode.StereoSpectrum, AdvancedVisualizationMode.DynamicsHistory })
            {
                control.Mode = mode; control.Style = VisualStyle.Precision;
                for (var i = 0; i < 20; i++) paint(args);
                var before = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                for (var i = 0; i < 100; i++) paint(args);
                watch.Stop();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var ms = watch.Elapsed.TotalMilliseconds / 100;
                _output.WriteLine($"{mode}, populated1024 history, 900x280 Precision: {ms:F3} ms/frame OnPaint; {bytes / 100d:F1} bytes/frame");
                Assert.True(ms < 16.7, $"{mode} painting exceeded existing frame budget: {ms:F3} ms.");
                Assert.True(bytes / 100d < 256, $"{mode} painting allocated {bytes / 100d:F1} bytes/frame.");
            }
        });
    }

    internal static AudioVisualizationData CreateMeasuredData()
    {
        var data = new AudioVisualizationData();
        var samples = new float[1200 * 2];
        for (var index = 0; index < 1100; index++)
        {
            var level = 0.05 + 0.35 * (0.5 + 0.5 * Math.Sin(index / 25d));
            for (var frame = 0; frame < 1200; frame++)
            {
                samples[frame * 2] = (float)(level * Math.Sin(2 * Math.PI * 1000 * frame / 48000));
                samples[frame * 2 + 1] = (float)(level * 0.6 * Math.Sin(2 * Math.PI * 4000 * frame / 48000));
            }
            data.Update(samples, 1200, 2, 48000);
        }
        var left = new double[83]; var right = new double[83];
        for (var band = 0; band < 83; band++)
        {
            left[band] = Math.Pow(10, (-55 + 40 * Math.Exp(-Math.Pow((band - 25d) / 9, 2))) / 10);
            right[band] = Math.Pow(10, (-55 + 35 * Math.Exp(-Math.Pow((band - 65d) / 9, 2))) / 10);
        }
        data.SetChannelSpectrum(left, right);
        return data;
    }

    private static Action<PaintEventArgs> PaintDelegate(AdvancedVisualizationControl control)
        => (Action<PaintEventArgs>)Delegate.CreateDelegate(typeof(Action<PaintEventArgs>), control,
            typeof(AdvancedVisualizationControl).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic));

    public static string RenderSyntheticContactSheet() => RenderContactSheet(CreateMeasuredData(), BandPlan.CreateLogarithmic(83, 20, 20000, 48000));

    internal static string RenderContactSheet(AudioVisualizationData data, BandPlan plan, int width = 900, int height = 280, int dpi = 96, string theme = "Studio")
    {
        string result = null;
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var stride = height + 40;
            using var sheet = new Bitmap(width + 20, 30 + stride * 2);
            sheet.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(sheet);
            using var font = new Font("Segoe UI", 9);
            graphics.Clear(Color.FromArgb(12, 16, 21));
            graphics.DrawString("Actual WinForms renderer • supplied PCM measurements and independent channel bands", font, Brushes.White, 8, 4);
            var row = 0;
            foreach (var mode in new[] { AdvancedVisualizationMode.StereoSpectrum, AdvancedVisualizationMode.DynamicsHistory })
            {
                graphics.DrawString(mode + " / " + theme + " / Precision / " + dpi + " DPI", font, Brushes.White, 8, 30 + row * stride);
                using var control = new AdvancedVisualizationControl { Size = new Size(width, height), Mode = mode, ThemeName = theme, Style = VisualStyle.Precision };
                control.SetBandPlan(plan); control.SetAudioData(data);
                using var bitmap = new Bitmap(width, height);
                bitmap.SetResolution(dpi, dpi);
                using (var paintGraphics = Graphics.FromImage(bitmap))
                using (var args = new PaintEventArgs(paintGraphics, control.ClientRectangle))
                    PaintDelegate(control)(args);
                graphics.DrawImageUnscaled(bitmap, 10, 52 + row * stride); row++;
            }
            using var stream = new MemoryStream();
            sheet.Save(stream, ImageFormat.Png);
            result = Convert.ToBase64String(stream.ToArray());
        });
        return result;
    }
}
