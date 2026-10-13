using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum;
using Spectrum.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

public class AnalyticalModeTests
{
    private readonly ITestOutputHelper _output;
    public AnalyticalModeTests(ITestOutputHelper output) => _output = output;
    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void OnSta(Action action) => CombinedRandomizationTests.RunOnSta(action);

    [Fact]
    public void RegistryContainsExactlyTheSeventeenImplementedModesAndNoRetiredViews()
    {
        OnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var selector = Field<ComboBox>(form, "_modeSelector");
            var expected = new[] { "Spectrum", "Bricks", "LED", "Waterfall", "Radial Spectrum", "Contour", "Peak Trace",
                "Threshold Monitor", "Band Matrix", "Octave Spectrum", "Level Change", "Octave Waterfall", "Stereo Levels", "Oscilloscope", "Spectral Balance", "Stereo Spectrum", "Dynamics History" };
            Assert.Equal(expected.OrderBy(x => x), selector.Items.Cast<string>().OrderBy(x => x));
            foreach (var retired in new[] { "Dots", "Lollipop", "Wave", "Orbit History", "Level Change Map", "Frequency Ribbon" })
                Assert.DoesNotContain(retired, selector.Items.Cast<string>());
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            foreach (var mode in expected.Skip(3))
            {
                selector.SelectedItem = mode;
                var advanced = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
                var resolve = typeof(FormAudioSpectrum).GetMethod("ResolveAdvancedMode", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.Equal((AdvancedVisualizationMode)resolve.Invoke(null, new object[] { mode }), advanced.Mode);
                Assert.Equal("Precision", FormAudioSpectrum.GetAppliedVisualStyle(mode, "Precision"));
            }
        });
    }

    [Theory]
    [InlineData(9, AdvancedVisualizationMode.PeakTrace)]
    [InlineData(11, AdvancedVisualizationMode.SpectralFlux)]
    [InlineData(12, AdvancedVisualizationMode.PeakTrace)]
    public void StableRetiredEnumIdentifiersNormalizeWithoutAddingHistory(int value, AdvancedVisualizationMode target)
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            control.SetSpectrum(new byte[] { 200 });
            control.Mode = (AdvancedVisualizationMode)value;
            Assert.Equal(target, control.Mode);
            Assert.Equal(1, Field<int>(control, "_historyCount"));
            control.SetSpectrum(new byte[] { 100 }, "Ice", (AdvancedVisualizationMode)value);
            Assert.Equal(target, control.Mode);
            Assert.Equal(2, Field<int>(control, "_historyCount"));
            Assert.Throws<ArgumentOutOfRangeException>(() => control.Mode = (AdvancedVisualizationMode)18);
            Assert.Throws<ArgumentOutOfRangeException>(() => control.SetSpectrum(new byte[83], "Ice", (AdvancedVisualizationMode)(-1)));
        });
    }

    [Theory]
    [InlineData("Waterfall")]
    [InlineData("Octave Waterfall")]
    [InlineData("Radial Spectrum")]
    [InlineData("Band Matrix")]
    [InlineData("Octave Spectrum")]
    [InlineData("Level Change")]
    [InlineData("Stereo Levels")]
    [InlineData("Oscilloscope")]
    [InlineData("Spectral Balance")]
    public void MeasurementViewsOfferOnlyMeaningfulReferenceStyles(string mode)
    {
        Assert.Equal(new[] { "None", "Precision" }, VisualStyles.GetSupportedNames(mode));
        Assert.Equal("None", FormAudioSpectrum.GetAppliedVisualStyle(mode, "Scanline"));
        Assert.Equal("None", FormAudioSpectrum.GetAppliedVisualStyle(mode, "Glow"));
    }

    [Fact]
    public void PcmPeakAndRmsUseIndependentChannelsAndAnExplicitFullScaleReference()
    {
        var samples = StereoTone(4800, 0.5f, 0.25f);
        var data = new AudioVisualizationData();
        data.Update(samples, 4800, 2, 48000);
        Assert.True(data.HasStereo);
        Assert.Equal(0.5, data.LeftPeak, 6);
        Assert.Equal(0.25, data.RightPeak, 6);
        Assert.Equal(0.5 / Math.Sqrt(2), data.LeftRms, 6);
        Assert.Equal(0.25 / Math.Sqrt(2), data.RightRms, 6);
        Assert.Equal(-9.03089987, AudioVisualizationData.AmplitudeToDb(data.LeftRms), 5);
        Assert.Equal(-6.02059991, AudioVisualizationData.AmplitudeToDb(data.LeftPeak), 5);
        data.Update(StereoTone(4800, 1, 1), 4800, 2, 48000);
        Assert.Equal(-3.01029996, AudioVisualizationData.AmplitudeToDb(data.LeftRms), 5);
        Assert.Equal(0, AudioVisualizationData.AmplitudeToDb(data.LeftPeak), 5);
    }

    [Fact]
    public void ObservedWindowExcludesOldSamplesAndCopiesOnlyItsBoundedTail()
    {
        var data = new AudioVisualizationData();
        data.Update(new[] { 1f, 1f, 0.25f, -0.25f, 0.5f, -0.5f }, 3, 2, 48000, 2);
        Assert.Equal(0.5, data.LeftPeak);
        Assert.Equal(Math.Sqrt((0.0625 + 0.25) / 2), data.LeftRms, 8);
        Assert.Equal(2, data.WindowFrames);
        Assert.Equal(new[] { 0.25f, 0.5f }, data.Wave.Take(data.WaveCount));
        var samples = Enumerable.Range(0, 5000).Select(i => i / 5000f).ToArray();
        data.Update(samples, samples.Length, 1, 48000);
        Assert.Equal(AudioVisualizationData.MaximumWaveSamples, data.WaveCount);
        Assert.Equal(samples[5000 - data.WaveCount], data.Wave[0]);
        var copy = new AudioVisualizationData();
        copy.CopyFrom(data);
        Array.Clear(samples, 0, samples.Length);
        data.Clear();
        Assert.True(copy.HasSamples);
        Assert.Equal(4999 / 5000f, copy.Wave[copy.WaveCount - 1]);
        Assert.False(copy.HasStereo);
        Assert.Equal(0, copy.RightPeak);
        copy.CopyFrom(null);
        Assert.False(copy.HasSamples);
        Assert.All(copy.Wave, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void StreamingPcmExcludesGapsAndPreviouslyObservedFramesAndKeepsRealSilence()
    {
        var data = new AudioVisualizationData();
        var samples = new[] { 1f, 0.5f, 0.25f, 0.125f };
        data.UpdateObservedWindow(samples, 4, 1, 48000, 104, 101, 102);
        Assert.Equal(2, data.WindowFrames);
        Assert.Equal(0.25, data.LeftPeak);
        Assert.Equal(new[] { 0.25f, 0.125f }, data.Wave.Take(data.WaveCount));
        data.UpdateObservedWindow(samples, 4, 1, 48000, 104, 104, 102);
        Assert.False(data.HasSamples);
        Assert.Equal(0, data.LeftPeak);
        data.UpdateObservedWindow(samples, 4, 1, 48000, 104, 101, 104);
        Assert.False(data.HasSamples);
        data.UpdateObservedWindow(new float[4], 4, 1, 48000, 108, 104, 104);
        Assert.True(data.HasSamples);
        Assert.False(data.HasStereo);
        Assert.Equal(0, data.LeftRms);
        Assert.Equal(4, data.WindowFrames);
    }

    [Fact]
    public void InvalidPcmIsRejectedAndNonfiniteValuesDoNotCorruptMeasurements()
    {
        var data = new AudioVisualizationData();
        Assert.Throws<ArgumentNullException>(() => data.Update(null, 0, 1, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => data.Update(new float[1], 1, 0, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => data.Update(new float[1], 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => data.Update(new float[1], int.MaxValue, int.MaxValue, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => data.Update(new float[1], 1, 1, 48000, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => data.Update(new float[1], 1, 1, 48000, -2));
        data.Update(new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2f }, 4, 1, 48000);
        Assert.Equal(2, data.LeftPeak);
        Assert.Equal(1, data.LeftRms);
        Assert.Equal(6.02059991, AudioVisualizationData.AmplitudeToDb(data.LeftPeak), 5);
        Assert.Equal(new[] { 0f, 0f, 0f, 2f }, data.Wave.Take(data.WaveCount));
        data.Update(Array.Empty<float>(), 0, 1, 48000);
        Assert.False(data.HasSamples);
    }

    [Fact]
    public void NumericAudioLabelsPreserveQuietValuesAndSilenceIsExplicit()
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            control.SetAudioSamples(new[] { 0.00001f, 0f }, 1, 2, 48000);
            var labels = Field<string[]>(control, "_audioLabels");
            Assert.Equal("-100.0 dBFS", labels[0]);
            Assert.Equal("-100.0 dBFS", labels[1]);
            Assert.Equal("−∞ dBFS", labels[2]);
            Assert.Contains("FS amplitude = 1", Field<string>(control, "_audioCaption"));
            Assert.InRange(Field<double>(control, "_waveRange"), 0.00001, 1);
            control.SetAudioSamples(new[] { 2f }, 1, 1, 48000);
            Assert.Equal("6.0 dBFS", labels[1]);
            Assert.Contains("Mono", Field<string>(control, "_audioCaption"));
        });
    }

    [Fact]
    public void FormCopiesPcmOnTheSameFrameAndSwitchingToScopeUsesTheLatestSamples()
    {
        OnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var producer = new AudioVisualizationData();
            producer.Update(new[] { -0.25f, 0.5f, 0.25f, -0.5f }, 2, 2, 48000);
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
            typeof(FormAudioSpectrum).GetMethod("Spectrum_Change", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { null, new OnChangeEventArgs(new byte[] { 120 }, producer) { Plan = plan } });
            producer.Clear();
            typeof(FormAudioSpectrum).GetMethod("ApplySpectrumToUI", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            Field<ComboBox>(form, "_modeSelector").SelectedItem = "Oscilloscope";
            var actual = Field<AudioVisualizationData>(Field<AdvancedVisualizationControl>(form, "_advancedVisualizer"), "_audio");
            Assert.Equal(0.25, actual.LeftPeak);
            Assert.Equal(0.5, actual.RightPeak);
            Assert.Equal(48000, actual.SampleRate);
            Assert.Same(plan, Field<BandPlan>(Field<AdvancedVisualizationControl>(form, "_advancedVisualizer"), "_bandPlan"));
            Field<ComboBox>(form, "_modeSelector").SelectedItem = "Spectrum";
            var newPlan = BandPlan.CreateLogarithmic(83, 20, 20000, 24000);
            typeof(FormAudioSpectrum).GetMethod("Spectrum_Change", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { null, new OnChangeEventArgs(new byte[83]) { Plan = newPlan } });
            typeof(FormAudioSpectrum).GetMethod("ApplySpectrumToUI", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            Field<ComboBox>(form, "_modeSelector").SelectedItem = "Stereo Levels";
            Assert.False(actual.HasSamples);
            Assert.Same(newPlan, Field<BandPlan>(Field<AdvancedVisualizationControl>(form, "_advancedVisualizer"), "_bandPlan"));
        });
    }

    [Fact]
    public void ScopeTriggerUsesRealSamplesAndEnvelopePreservesNarrowTransients()
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(70, 42), Mode = AdvancedVisualizationMode.Oscilloscope };
            var samples = new float[2048];
            samples[0] = -0.01f;
            samples[1] = 0.01f;
            samples[101] = 0.8f;
            control.SetAudioSamples(samples, samples.Length, 1, 48000);
            Assert.Equal(1, Field<int>(control, "_waveStart"));
            Assert.Equal(1024, Field<int>(control, "_waveDisplayCount"));
            Assert.Equal(1, Field<double>(control, "_waveRange"));
            using var signal = VisualizationReadabilityTests.Render(control);
            control.SetAudioSamples(new float[2048], 2048, 1, 48000);
            using var silence = VisualizationReadabilityTests.Render(control);
            Assert.True(DifferentPixels(signal, silence) > 15, "A brief real PCM peak must remain visible after pixel reduction.");
        });
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(24000)]
    public void SpectralGroupsUseTheActualBandCentersAndSumEstimatedPower(int sampleRate)
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, sampleRate);
            control.SetBandPlan(plan);
            var assignments = Field<int[]>(control, "_octaveByBand");
            for (var band = 0; band < 83; band++)
                Assert.Equal(Math.Max(0, Math.Min(9, (int)Math.Floor(Math.Log(plan[band].CenterHz / 20, 2)))), assignments[band]);
            for (var group = 0; group < 3; group++)
            {
                var levels = new byte[83];
                var band = Enumerable.Range(0, 83).First(i => (plan[i].CenterHz < 250 ? 0 : plan[i].CenterHz < 4000 ? 1 : 2) == group);
                levels[band] = LevelScale.ToDisplayByte(-12);
                control.SetSpectrum(levels);
                var shares = Field<double[]>(control, "_balance");
                Assert.Equal(1, shares[group]);
                Assert.Equal(1, shares.Sum(), 8);
            }
            var twoBands = new byte[83];
            twoBands[0] = twoBands[1] = LevelScale.ToDisplayByte(-12);
            control.SetSpectrum(twoBands);
            var expected = LevelScale.ToDisplayByte(LevelScale.PowerToDb(2 * LevelScale.DisplayByteToRelativePower(twoBands[0])));
            Assert.Equal(expected, Field<byte[]>(control, "_octaveLevels")[assignments[0]]);
            control.SetSpectrum(new byte[83]);
            Assert.All(Field<double[]>(control, "_balance"), share => Assert.Equal(0, share));
        });
    }

    [Fact]
    public void BandPlanChangesClearHistoricalAndDerivedData()
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            control.SetSpectrum(Enumerable.Repeat((byte)220, 83).ToArray());
            control.SetBandPlan(BandPlan.CreateLogarithmic(83, 20, 20000, 32000));
            Assert.Equal(0, Field<int>(control, "_historyCount"));
            Assert.All(Field<byte[]>(control, "_spectrum"), value => Assert.Equal((byte)0, value));
            Assert.All(Field<byte[]>(control, "_octaveLevels"), value => Assert.Equal((byte)0, value));
            Assert.All(Field<double[]>(control, "_balance"), value => Assert.Equal(0, value));
        });
    }

    [Fact]
    public void WaterfallRingKeepsChronologyFixedSlotsAndRecolorsWithoutAppending()
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(83, 60), Mode = AdvancedVisualizationMode.Waterfall, HistoryFrames = 3 };
            for (var value = 50; value <= 200; value += 50)
                control.SetSpectrum(Enumerable.Repeat((byte)value, 83).ToArray());
            Assert.Equal(3, Field<int>(control, "_historyCount"));
            var ticks = Field<long[]>(control, "_historyTicks").ToArray();
            control.ThemeName = "Studio";
            Assert.Equal(ticks, Field<long[]>(control, "_historyTicks"));
            Assert.Equal(3, Field<int>(control, "_historyCount"));
            var palette = Field<int[]>(control, "_paletteArgb");
            using var bitmap = VisualizationReadabilityTests.Render(control);
            Assert.Equal(palette[100], bitmap.GetPixel(40, 10).ToArgb());
            Assert.Equal(palette[150], bitmap.GetPixel(40, 30).ToArgb());
            Assert.Equal(palette[200], bitmap.GetPixel(40, 50).ToArgb());
            control.HistoryFrames = 4;
            control.SetSpectrum(Enumerable.Repeat((byte)180, 83).ToArray());
            using var partial = VisualizationReadabilityTests.Render(control);
            Assert.Equal(control.BackColor.ToArgb(), partial.GetPixel(40, 30).ToArgb());
            Assert.Equal(palette[180], partial.GetPixel(40, 55).ToArgb());
        });
    }

    [Fact]
    public void HistoryRasterStaysBoundedAndPinnedResourcesAreReleased()
    {
        OnSta(() =>
        {
            var control = new AdvancedVisualizationControl { HistoryFrames = 2048 };
            try
            {
                Assert.Equal(93 * 2048, Field<int[]>(control, "_historyPixels").Length);
                Assert.True(Field<GCHandle>(control, "_historyPixelsHandle").IsAllocated);
                Assert.Throws<ArgumentOutOfRangeException>(() => control.HistoryFrames = 2049);
                control.HistoryFrames = 1;
                Assert.Equal(93, Field<int[]>(control, "_historyPixels").Length);
                control.SetSpectrum(new byte[] { 255 });
                control.SetSpectrum(new byte[] { 80 });
                Assert.Equal(1, Field<int>(control, "_historyCount"));
            }
            finally { control.Dispose(); }
            Assert.False(Field<GCHandle>(control, "_historyPixelsHandle").IsAllocated);
            control.Dispose();
        });
    }

    [Fact]
    public void HistoryCaptionsCountOnlyRowsVisibleBetweenTheCaptionAndOctaveAxis()
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(520, 100), Mode = AdvancedVisualizationMode.OctaveWaterfall };
            for (var frame = 0; frame < 160; frame++) control.SetSpectrum(new byte[] { 180 });
            Assert.Contains("58 displayed frames", Field<string>(control, "_historyCaption"));
            control.Mode = AdvancedVisualizationMode.Waterfall;
            Assert.Contains("78 displayed frames", Field<string>(control, "_historyCaption"));
            control.Size = new Size(520, 280);
            Assert.Contains("160 displayed frames", Field<string>(control, "_historyCaption"));
        });
    }

    [Fact]
    public void PeakHoldUsesElapsedTimeAndLevelChangePreservesDirection()
    {
        var peaks = new float[83]; var holds = new float[83]; var levels = new byte[83];
        levels[0] = 200;
        AdvancedVisualizationControl.UpdatePeakEnvelope(peaks, holds, levels, 0);
        levels[0] = 0;
        AdvancedVisualizationControl.UpdatePeakEnvelope(peaks, holds, levels, 299);
        Assert.Equal(200, peaks[0]);
        AdvancedVisualizationControl.UpdatePeakEnvelope(peaks, holds, levels, 101);
        Assert.Equal(184f, peaks[0]);
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(830, 200), Mode = AdvancedVisualizationMode.SpectralFlux, ThemeName = "Studio" };
            control.SynchronizeSpectrum(Enumerable.Repeat((byte)100, 83).ToArray());
            var next = Enumerable.Repeat((byte)100, 83).ToArray();
            next[1] = 240; next[2] = 0;
            control.SetSpectrum(next);
            using var bitmap = VisualizationReadabilityTests.Render(control);
            Assert.Equal(BarColorThemes.Studio.High.ToArgb(), bitmap.GetPixel(15, 80).ToArgb());
            Assert.Equal(BarColorThemes.Studio.Low.ToArgb(), bitmap.GetPixel(25, 125).ToArgb());
        });
    }

    [Fact]
    public void PeakHoldExpiresAfterALongUiPause()
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Mode = AdvancedVisualizationMode.PeakTrace };
            control.SetSpectrum(new byte[] { 255 });
            CombinedRandomizationTests.SetField(control, "_lastFrameTick", Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency);
            control.SetSpectrum(new byte[83]);
            Assert.Equal(0f, Field<float[]>(control, "_heldPeaks")[0]);
            Assert.Equal(0f, Field<float[]>(control, "_peakHoldMs")[0]);
        });
    }

    [Fact]
    public void AllRetainedAndNewRenderersHandleSilenceLowLevelsAndCompactSizes()
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            foreach (var size in new[] { new Size(1, 1), new Size(70, 42), new Size(900, 280) })
            {
                control.Size = size;
                foreach (var mode in new[] { AdvancedVisualizationMode.Waterfall, AdvancedVisualizationMode.RadialSpectrum,
                    AdvancedVisualizationMode.Contour, AdvancedVisualizationMode.PeakTrace, AdvancedVisualizationMode.ThresholdMonitor,
                    AdvancedVisualizationMode.BandMatrix, AdvancedVisualizationMode.OctaveSpectrum, AdvancedVisualizationMode.SpectralFlux,
                    AdvancedVisualizationMode.OctaveWaterfall, AdvancedVisualizationMode.StereoLevels, AdvancedVisualizationMode.Oscilloscope,
                    AdvancedVisualizationMode.SpectralBalance })
                {
                    control.Mode = mode;
                    foreach (var level in new byte[] { 0, 1, 255 })
                    {
                        control.SetSpectrum(Enumerable.Repeat(level, 83).ToArray());
                        control.SetAudioSamples(new[] { level / 255f }, 1, 1, 48000);
                        using var bitmap = VisualizationReadabilityTests.Render(control);
                    }
                }
            }
        });
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Precision")]
    [InlineData("Scanline")]
    public void FullScaleCurvePeaksAndCompactThresholdActivityRemainVisible(string styleName)
    {
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(830, 200), Mode = AdvancedVisualizationMode.PeakTrace, ThemeName = "Studio", Style = VisualStyles.Parse(styleName) };
            control.SetSpectrum(Enumerable.Repeat((byte)255, 83).ToArray());
            using var curve = VisualizationReadabilityTests.Render(control);
            Assert.All(Field<PointF[]>(control, "_plotPoints"), point => Assert.Equal(22f, point.Y));
            var peakPixels = 0;
            for (var x = 5; x < 825; x++)
                if (curve.GetPixel(x, 22).ToArgb() == BarColorThemes.Studio.Ui.PrimaryText.ToArgb()) peakPixels++;
            Assert.True(peakPixels > 50, "Full-scale peak hold must remain visible below the caption.");
            control.Mode = AdvancedVisualizationMode.ThresholdMonitor;
            control.Size = new Size(70, 42);
            using var active = VisualizationReadabilityTests.Render(control);
            control.SetSpectrum(new byte[83]);
            using var silence = VisualizationReadabilityTests.Render(control);
            Assert.True(DifferentPixels(active, silence) > 100, "Compact threshold mode must show signal rather than just its grid.");
        });
    }

    [Fact]
    public void PcmExtractionReusesBuffersAndAdvancedPaintingFitsARealtimeBudget()
    {
        var data = new AudioVisualizationData();
        var samples = StereoTone(1200, 0.5f, 0.25f);
        for (var warmup = 0; warmup < 20; warmup++) data.Update(samples, 1200, 2, 48000);
        var watch = new Stopwatch();
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (var frame = 0; frame < 600; frame++) data.Update(samples, 1200, 2, 48000);
        watch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
        _output.WriteLine($"PCM extraction (1200 stereo frames): {watch.Elapsed.TotalMilliseconds / 600:F4} ms/frame; {bytes} bytes over 600 frames");
        Assert.Equal(0, bytes);
        Assert.True(watch.Elapsed.TotalMilliseconds / 600 < 2);
        OnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(900, 280), ThemeName = "Studio" };
            control.SetAudioData(data);
            var levels = Enumerable.Range(0, 83).Select(i => (byte)(1 + i * 254 / 82)).ToArray();
            for (var row = 0; row < 160; row++) control.SetSpectrum(levels);
            using var bitmap = VisualizationReadabilityTests.Render(control);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            var paint = (Action<PaintEventArgs>)Delegate.CreateDelegate(typeof(Action<PaintEventArgs>), control,
                typeof(AdvancedVisualizationControl).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic));
            var allocationFailures = new System.Collections.Generic.List<string>();
            foreach (var mode in new[] { AdvancedVisualizationMode.Waterfall, AdvancedVisualizationMode.RadialSpectrum,
                AdvancedVisualizationMode.PeakTrace, AdvancedVisualizationMode.BandMatrix, AdvancedVisualizationMode.OctaveWaterfall,
                AdvancedVisualizationMode.StereoLevels, AdvancedVisualizationMode.Oscilloscope, AdvancedVisualizationMode.SpectralBalance,
                AdvancedVisualizationMode.Contour, AdvancedVisualizationMode.ThresholdMonitor, AdvancedVisualizationMode.OctaveSpectrum,
                AdvancedVisualizationMode.SpectralFlux })
            {
                control.Mode = mode; control.Style = VisualStyle.Precision;
                for (var frame = 0; frame < 10; frame++) paint(args);
                allocation = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                for (var frame = 0; frame < 100; frame++) paint(args);
                watch.Stop();
                bytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
                var ms = watch.Elapsed.TotalMilliseconds / 100;
                _output.WriteLine($"{mode}, 900x280 Precision: {ms:F3} ms/frame OnPaint; {bytes / 100d:F1} bytes/frame");
                Assert.True(ms < 16.7, $"{mode}: {ms:F3} ms exceeds a 60 Hz renderer budget.");
                if (bytes / 100d >= 256) allocationFailures.Add($"{mode}: {bytes / 100d:F1} bytes/frame allocated.");
            }
            Assert.Empty(allocationFailures);
        });
    }

    // Actual offscreen controls with synthetic input; no image files are created.
    public static string RenderModeContactSheet(int firstRow = 0, int rowCount = 12)
    {
        string result = null;
        OnSta(() =>
        {
            var modes = new[] { AdvancedVisualizationMode.StereoLevels, AdvancedVisualizationMode.Oscilloscope,
                AdvancedVisualizationMode.SpectralBalance, AdvancedVisualizationMode.Waterfall, AdvancedVisualizationMode.RadialSpectrum,
                AdvancedVisualizationMode.PeakTrace, AdvancedVisualizationMode.BandMatrix, AdvancedVisualizationMode.OctaveSpectrum,
                AdvancedVisualizationMode.OctaveWaterfall, AdvancedVisualizationMode.SpectralFlux, AdvancedVisualizationMode.Contour,
                AdvancedVisualizationMode.ThresholdMonitor };
            var themes = new[] { "ClassicSmooth", "Studio", "Crimson Velvet" };
            const int panelWidth = 520, panelHeight = 180;
            using var sheet = new Bitmap(panelWidth * 3, 30 + (panelHeight + 26) * rowCount);
            using var g = Graphics.FromImage(sheet);
            using var font = new Font("Segoe UI", 9);
            g.Clear(Color.FromArgb(12, 16, 21));
            g.DrawString("Actual offscreen controls • synthetic test PCM and display bands • not live audio capture", font, Brushes.White, 8, 5);
            for (var row = firstRow; row < firstRow + rowCount; row++)
            for (var column = 0; column < themes.Length; column++)
            {
                var y = 30 + (row - firstRow) * (panelHeight + 26); var x = column * panelWidth;
                g.DrawString(modes[row] + " / " + themes[column], font, Brushes.White, x + 4, y);
                using var control = new AdvancedVisualizationControl { Size = new Size(panelWidth - 8, panelHeight),
                    ThemeName = themes[column], Mode = modes[row], Style = VisualStyle.Precision };
                control.SetAudioSamples(StereoTone(1200, 0.5f, 0.18f), 1200, 2, 48000);
                var levels = new byte[83];
                for (var frame = 0; frame < 160; frame++)
                {
                    for (var band = 0; band < 83; band++)
                        levels[band] = (byte)(15 + (195 * Math.Exp(-Math.Pow((band - 25 - 5 * Math.Sin(frame / 12d)) / 12, 2))) +
                            35 * (0.5 + 0.5 * Math.Cos(band * 0.5 + frame * 0.1)));
                    control.SetSpectrum(levels);
                }
                using var bitmap = VisualizationReadabilityTests.Render(control);
                g.DrawImageUnscaled(bitmap, x + 4, y + 22);
            }
            using var stream = new MemoryStream();
            sheet.Save(stream, ImageFormat.Png);
            result = Convert.ToBase64String(stream.ToArray());
        });
        return result;
    }

    private static float[] StereoTone(int frames, float left, float right)
    {
        var samples = new float[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = left * (float)Math.Sin(2 * Math.PI * 1000 * frame / 48000);
            samples[frame * 2 + 1] = -right * (float)Math.Sin(2 * Math.PI * 1000 * frame / 48000);
        }
        return samples;
    }

    private static int DifferentPixels(Bitmap first, Bitmap second)
    {
        var count = 0;
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++)
            if (first.GetPixel(x, y) != second.GetPixel(x, y)) count++;
        return count;
    }
}
