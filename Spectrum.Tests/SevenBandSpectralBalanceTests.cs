// Developer :: > Gehan Fernando
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using Spectrum.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

// Covers the 7-band analyzer end to end: the maths, the plumbing into the analyzer and how it ends up on screen.
public class SevenBandSpectralBalanceTests
{
    private readonly ITestOutputHelper _out;

    public SevenBandSpectralBalanceTests(ITestOutputHelper output) => _out = output;

    private static double Db(double power) => LevelScale.PowerToDb(power);

    private static double[] Measure(int fs, float[] interleaved, int channels, SevenBandAnalyzer analyzer = null)
    {
        analyzer ??= new SevenBandAnalyzer(fs, channels);
        var power = new double[SevenBandAnalyzer.BandCount];
        analyzer.Analyze(interleaved, interleaved.Length / channels, power);
        return power;
    }

    [Fact]
    public void DefinitionsAreContiguousAndCoverTwentyHertzToTwentyKilohertz()
    {
        var d = SevenBandAnalyzer.Definitions;
        Assert.Equal(7, d.Count);
        Assert.Equal(new double[] { 20, 60, 250, 500, 2000, 6000, 10000 }, d.Select(b => b.LowerHz));
        Assert.Equal(new double[] { 60, 250, 500, 2000, 6000, 10000, 20000 }, d.Select(b => b.UpperHz));
        for (var i = 1; i < d.Count; i++) Assert.Equal(d[i - 1].UpperHz, d[i].LowerHz);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void ATonePerBandLightsOnlyThatBandAtItsFullScaleLevel(int fs)
    {
        var analyzer = new SevenBandAnalyzer(fs, 2);
        var d = SevenBandAnalyzer.Definitions;
        for (var band = 0; band < 7; band++)
        {
            var hz = Math.Sqrt(d[band].LowerHz * d[band].UpperHz);
            var power = Measure(fs, Signals.StereoSine(analyzer.RequiredFrames, fs, hz, 1, 1), 2, analyzer);
            _out.WriteLine($"{fs} Hz  {d[band].Name,-12} tone {hz,8:0.0} Hz -> {Db(power[band]):0.00} dBFS");
            Assert.InRange(Db(power[band]), -0.15, 0.15);
            for (var other = 0; other < 7; other++)
                if (other != band) Assert.True(Db(power[other]) < -30, $"band {other} leaked {Db(power[other]):0.0} dB for tone in band {band}");
        }
    }

    [Theory]
    [InlineData(48000, 59.0, 0, 1)]
    [InlineData(48000, 61.0, 1, 0)]
    [InlineData(48000, 249.0, 1, 2)]
    [InlineData(48000, 251.0, 2, 1)]
    [InlineData(48000, 499.0, 2, 3)]
    [InlineData(48000, 2001.0, 4, 3)]
    [InlineData(48000, 5999.0, 4, 5)]
    [InlineData(48000, 10001.0, 6, 5)]
    public void TonesNextToABoundaryLandInTheCorrectBand(int fs, double hz, int expected, int neighbour)
    {
        var analyzer = new SevenBandAnalyzer(fs, 2);
        var power = Measure(fs, Signals.StereoSine(analyzer.RequiredFrames, fs, hz, 1, 1), 2, analyzer);
        Assert.True(power[expected] > power[neighbour], $"{hz} Hz: expected band {expected} > band {neighbour}");
        Assert.Equal(expected, Array.IndexOf(power, power.Max()));
        Assert.True(power[expected] > 0.5, "Most of an edge-adjacent tone's power must be in its own band.");
    }

    [Fact]
    public void SimultaneousTonesAreMeasuredIndependentlyAndScaleWithAmplitude()
    {
        const int fs = 48000;
        var analyzer = new SevenBandAnalyzer(fs, 1);
        var n = analyzer.RequiredFrames;
        var amplitudes = new[] { 0.5, 0, 0.25, 0, 0.125, 0, 0.0625 };
        var tones = new[] { 40.0, 120.0, 350.0, 1000.0, 3500.0, 8000.0, 14000.0 };
        var samples = Signals.Interleaved(n, 1, (_, i) =>
            Enumerable.Range(0, 7).Sum(b => Signals.Sine(tones[b], amplitudes[b], i, fs)));
        var power = Measure(fs, samples, 1, analyzer);
        for (var b = 0; b < 7; b++)
        {
            if (amplitudes[b] > 0)
                Assert.InRange(Db(power[b]), 20 * Math.Log10(amplitudes[b]) - 0.2, 20 * Math.Log10(amplitudes[b]) + 0.2);
            else
                Assert.True(Db(power[b]) < -45, $"silent band {b} reads {Db(power[b]):0.0} dBFS");
        }
    }

    [Fact]
    public void SilenceReadsAsTheFloorAndBandsBelowAndAboveNeverLeak()
    {
        var analyzer = new SevenBandAnalyzer(48000, 2);
        var power = Measure(48000, new float[analyzer.RequiredFrames * 2], 2, analyzer);
        Assert.All(power, p => Assert.True(p < 1e-20));
    }

    [Fact]
    public void WhiteNoiseDistributesPowerInProportionToBandwidth()
    {
        const int fs = 48000;
        var analyzer = new SevenBandAnalyzer(fs, 1);
        var noise = Signals.WhiteNoise(analyzer.RequiredFrames, 0.1, 7);
        var power = Measure(fs, noise.Select(v => (float)v).ToArray(), 1, analyzer);
        var total = 0.1 * 0.1;
        // Noise power is sigma^2, and against a full-scale sine (0.5) the per-hertz power is total / 0.5 / (fs/2).
        for (var b = 0; b < 7; b++)
        {
            var d = SevenBandAnalyzer.Definitions[b];
            var expected = total / 0.5 * (d.UpperHz - d.LowerHz) / (fs / 2.0);
            Assert.InRange(Db(power[b]) - Db(expected), -2.5, 2.5);
        }
    }

    [Theory]
    [InlineData(22050, 7)]
    [InlineData(32000, 7)]
    [InlineData(16000, 6)]
    public void BandsAboveNyquistAreUnavailableAndTheStraddlingBandIsTruncated(int fs, int reachable)
    {
        var analyzer = new SevenBandAnalyzer(fs, 2);
        var power = Measure(fs, Signals.StereoSine(analyzer.RequiredFrames, fs, 1000, 1, 1), 2, analyzer);
        for (var b = 0; b < 7; b++)
        {
            Assert.Equal(b >= reachable, double.IsNaN(power[b]));
        }
        Assert.InRange(Db(power[3]), -0.2, 0.2);
        Assert.InRange(analyzer.Plan[analyzer.Plan.Count - 1].UpperHz, 0, fs / 2.0);
    }

    [Fact]
    public void ALeftOnlyToneIsStillMeasuredAndTheMixDoesNotCancelOutOfPhaseChannels()
    {
        const int fs = 48000;
        var analyzer = new SevenBandAnalyzer(fs, 2);
        var left = Measure(fs, Signals.StereoSine(analyzer.RequiredFrames, fs, 1000, 1, 0), 2, analyzer);
        Assert.InRange(Db(left[3]), -3.3, -2.7);
        var antiPhase = Measure(fs, Signals.StereoSine(analyzer.RequiredFrames, fs, 1000, 1, 1, Math.PI), 2, analyzer);
        Assert.InRange(Db(antiPhase[3]), -0.15, 0.15);
    }

    [Fact]
    public void AnalysisIsFastEnoughForRealTimeUse()
    {
        const int fs = 48000;
        var analyzer = new SevenBandAnalyzer(fs, 2);
        var samples = Signals.StereoSine(analyzer.RequiredFrames, fs, 440, 0.5, 0.5);
        var power = new double[7];
        analyzer.Analyze(samples, analyzer.RequiredFrames, power);
        var watch = new Stopwatch();
        // Scope allocation evidence to the analysis thread. Process-wide retained heap
        // includes unrelated test-host work and can grow while this allocation-free loop runs.
        var before = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        const int iterations = 200;
        for (var i = 0; i < iterations; i++) analyzer.Analyze(samples, analyzer.RequiredFrames, power);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"7-band analysis: {watch.Elapsed.TotalMilliseconds / iterations:0.000} ms/call, {allocated} bytes allocated on analysis thread");
        Assert.True(watch.Elapsed.TotalMilliseconds / iterations < 10);
        Assert.True(allocated < 64 * 1024);
    }

    [Fact]
    public void AudioDataCarriesSevenBandPowerThroughCopyAndClear()
    {
        var data = new AudioVisualizationData();
        data.Update(new float[4800], 4800, 1, 48000);
        data.SetSevenBandPower(new[] { 1, 0.5, 0.25, 0.125, 0.0625, 0.03125, double.NaN });
        Assert.True(data.HasSevenBand);
        var copy = new AudioVisualizationData();
        copy.CopyFrom(data);
        Assert.True(copy.HasSevenBand);
        Assert.Equal(0.25, copy.SevenBandPower[2]);
        Assert.True(double.IsNaN(copy.SevenBandPower[6]));
        data.Update(new float[4800], 4800, 1, 48000);
        Assert.False(data.HasSevenBand == false && data.SevenBandPower.Any(v => v != 0));
        data.Clear();
        Assert.False(data.HasSevenBand);
        copy.CopyFrom(null);
        Assert.False(copy.HasSevenBand);
    }

    private static AudioVisualizationData Data(params double[] power)
    {
        var data = new AudioVisualizationData();
        data.Update(new float[9600], 4800, 2, 48000);
        data.SetSevenBandPower(power);
        return data;
    }

    [Fact]
    public void BarsRespondIndependentlyWithAttackAndReleaseAndDoNotTouchTheAudioInput()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(840, 260), Mode = AdvancedVisualizationMode.AdvancedSpectralBalance };
            var loud = Data(1, 0, 0, 0, 0, 0, 0);
            var quiet = Data(0, 0, 0, 0.001, 0, 0, 0);
            for (var i = 0; i < 20; i++) { control.SetAudioData(Data(1, 0, 0, 0, 0, 0, 0)); System.Threading.Thread.Sleep(10); }
            Assert.InRange(control.GetSevenBandDisplayDb(0), -1, 0.01);
            for (var b = 1; b < 7; b++) Assert.InRange(control.GetSevenBandDisplayDb(b), -72.01, -71.9);
            Assert.Equal("0.0", control.GetSevenBandLabel(0));
            Assert.Equal("< −72", control.GetSevenBandLabel(1));

            control.SetAudioData(quiet);
            System.Threading.Thread.Sleep(30);
            control.SetAudioData(quiet);
            Assert.InRange(control.GetSevenBandDisplayDb(0), -71.9, -3);
            Assert.True(control.GetSevenBandDisplayDb(0) > -60, "release must be gradual, not an instant drop");
            Assert.True(control.GetSevenBandDisplayDb(3) > -72, "attack must start immediately");
            Assert.True(control.GetSevenBandPeakDb(0) > -5, "peak marker must hold after the bar falls");
            for (var i = 0; i < 40; i++) { control.SetAudioData(quiet); System.Threading.Thread.Sleep(20); }
            Assert.InRange(control.GetSevenBandDisplayDb(3), -30.5, -29.5);
            Assert.InRange(loud.SevenBandPower[0], 1, 1);
        });
    }

    [Fact]
    public void MissingAudioDecaysToTheFloorAndShowsAnUnavailableReadout()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(840, 260), Mode = AdvancedVisualizationMode.AdvancedSpectralBalance };
            control.SetAudioData(Data(1, 1, 1, 1, 1, 1, double.NaN));
            Assert.True(double.IsNaN(control.GetSevenBandDisplayDb(6)));
            Assert.Equal("n/a", control.GetSevenBandLabel(6));
            control.SetAudioData(new AudioVisualizationData());
            Assert.Equal("—", control.GetSevenBandLabel(0));
            for (var i = 0; i < 60; i++) { control.SetAudioData(new AudioVisualizationData()); System.Threading.Thread.Sleep(20); }
            Assert.InRange(control.GetSevenBandDisplayDb(0), -72.01, -71.9);
        });
    }

    [Theory]
    [InlineData(1920, 400, 1.0)]
    [InlineData(1280, 300, 1.0)]
    [InlineData(840, 240, 1.25)]
    [InlineData(520, 180, 1.5)]
    [InlineData(300, 120, 1.0)]
    [InlineData(160, 70, 1.0)]
    [InlineData(60, 30, 1.0)]
    public void SevenDistinctBarsRenderAtEverySizeWithoutExceptions(int width, int height, double dpiScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Size = new Size(width, height),
                Font = new Font("Segoe UI", (float)(9 * dpiScale)),
                Mode = AdvancedVisualizationMode.AdvancedSpectralBalance
            };
            // Different level per band so every bar ends up a distinct height.
            var levels = new[] { -6.0, -12, -18, -24, -30, -36, -42 };
            var power = levels.Select(l => Math.Pow(10, l / 10)).ToArray();
            for (var i = 0; i < 40; i++) { control.SetAudioData(Data(power)); System.Threading.Thread.Sleep(10); }
            using var bitmap = new Bitmap(width, height);
            control.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
            for (var b = 0; b < 7; b++) Assert.InRange(control.GetSevenBandDisplayDb(b), levels[b] - 0.5, levels[b] + 0.5);
            if (width >= 160) Assert.Contains(Enumerable.Range(0, bitmap.Width), x => bitmap.GetPixel(x, bitmap.Height / 2) != bitmap.GetPixel(0, bitmap.Height / 2));
        });
    }

}
