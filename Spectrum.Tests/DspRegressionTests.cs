// Developer :: > Gehan Fernando
using System;
using System.Linq;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

// Locks in the DSP maths so refactors can't silently shift the numbers.
public class DspRegressionTests
{
    [Fact]
    public void PeakHoldAppliesDecayForTheRemainderOfAnElapsedInterval()
    {
        float peak = 200, hold = 300;
        BarBallistics.StepPeak(ref peak, ref hold, 0, 1000, 300, 69, 0);
        Assert.InRange(peak, 151.699f, 151.701f);
        Assert.Equal(0, hold);
    }

    [Fact]
    public void PeakHoldIsIndependentOfElapsedIntervalPartitioning()
    {
        foreach (var intervals in new[]
        {
            new[] { 1000f },
            Enumerable.Repeat(100f, 10).ToArray(),
            Enumerable.Repeat(10f, 100).ToArray(),
            new[] { 90f, 173f, 168f, 19f, 550f }
        })
        {
            float peak = 200, hold = 300;
            foreach (var interval in intervals)
                BarBallistics.StepPeak(ref peak, ref hold, 0, interval, 300, 69, 0);
            Assert.InRange(peak, 151.699f, 151.701f);
            Assert.Equal(0, hold);
        }
    }

    [Theory]
    [InlineData(299f, 200f, 1f)]
    [InlineData(300f, 200f, 0f)]
    [InlineData(301f, 199.931f, 0f)]
    public void PeakHoldBoundaryConsumesOnlyTheHeldPortion(float elapsed, float expectedPeak, float expectedHold)
    {
        float peak = 200, hold = 300;
        BarBallistics.StepPeak(ref peak, ref hold, 0, elapsed, 300, 69, 0);
        Assert.InRange(peak, expectedPeak - 0.001f, expectedPeak + 0.001f);
        Assert.Equal(expectedHold, hold);
    }

    [Fact]
    public void PeakDecayStillRespectsTheCurrentLevelMinimumAndNewPeaks()
    {
        float peak = 200, hold = 300;
        BarBallistics.StepPeak(ref peak, ref hold, 50, 10000, 300, 69, 0);
        Assert.Equal(50, peak);
        BarBallistics.StepPeak(ref peak, ref hold, 0, 10000, 300, 69, 10);
        Assert.Equal(10, peak);
        BarBallistics.StepPeak(ref peak, ref hold, 210, 10000, 300, 69, 0);
        Assert.Equal(210, peak);
        Assert.Equal(300, hold);
    }

    [Fact]
    public void InvalidManagedWritesAreRejectedWithoutChangingHistory()
    {
        var history = new SampleHistory(2, 8);
        var samples = new[] { 0.25f, -0.25f, 0.5f, -0.5f };
        history.Write(samples, 2);
        Assert.Throws<ArgumentNullException>(() => history.Write(null, 0));
        foreach (var count in new[] { -1, 3, int.MaxValue, int.MinValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => history.Write(samples, count));
        Assert.Equal(2, history.TotalFrames);
        var copy = new float[4];
        Assert.True(history.TryCopyLatest(copy, 2, out var end));
        Assert.Equal(2, end);
        Assert.Equal(samples, copy);
    }

    [Fact]
    public void ZeroFrameManagedWritesAreNoOpsAndPartialTrailingFramesAreNotRead()
    {
        var history = new SampleHistory(2, 8);
        history.Write(Array.Empty<float>(), 0);
        history.Write(new[] { 0.25f, -0.25f, 99f }, 1);
        history.Write(new[] { 99f }, 0);
        Assert.Equal(1, history.TotalFrames);
        var copy = new float[2];
        Assert.True(history.TryCopyLatest(copy, 1, out _));
        Assert.Equal(new[] { 0.25f, -0.25f }, copy);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Write(new[] { 99f }, 1));
    }

    [Fact]
    public void ManagedOversizedWritesPreserveChannelAlignmentAbsolutePositionsAndGaps()
    {
        var history = new SampleHistory(2, 4);
        var samples = Signals.Interleaved(9, 2, (channel, frame) => frame * 10 + channel);
        history.Write(samples, 9);
        history.Write(new[] { 90f, 91f }, 1);
        var copy = new float[8];
        Assert.True(history.TryCopyLatest(copy, 4, out var end));
        Assert.Equal(10, end);
        Assert.Equal(new[] { 60f, 61f, 70f, 71f, 80f, 81f, 90f, 91f }, copy);
        Assert.True(history.TryCopyLatest(copy, 4, out end, discardBeforeFrame: 9));
        Assert.Equal(10, end);
        Assert.Equal(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 90f, 91f }, copy);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-4)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(4095)]
    public void DegenerateOrNonRadixTwoWindowsAreRejectedBeforePadding(int length)
    {
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandSpectrumAnalyzer(plan, 2, new[] { length }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandSpectrumAnalyzer(plan, 2, new[] { 4096, length }));
    }

    [Fact]
    public void ValidUnsortedAndDuplicateWindowsRetainTheirMeasurementContract()
    {
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        var engine = new BandSpectrumAnalyzer(plan, 2, new[] { 16384, 4096, 8192, 4096 });
        var reference = new BandSpectrumAnalyzer(plan, 2);
        var samples = Signals.StereoSine(engine.RequiredFrames, 48000, plan[50].CenterHz, 0.5, -0.25);
        Assert.Equal(reference.RequiredFrames, engine.RequiredFrames);
        Assert.Equal(Signals.AnalyzePower(reference, samples), Signals.AnalyzePower(engine, samples));
        Assert.Throws<ArgumentException>(() => new BandSpectrumAnalyzer(plan, 2, Array.Empty<int>()));
    }

    [Fact]
    public void SmallestSupportedHannWindowProducesFiniteMeasurements()
    {
        var engine = new BandSpectrumAnalyzer(
            BandPlan.CreateLogarithmic(83, 20, 20000, 48000), 1, new[] { 4 });
        var power = Signals.AnalyzePower(engine, new[] { 0f, 1f, -1f, 0f });
        Assert.All(power, value => Assert.True(value >= 0 && !double.IsInfinity(value)));
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void SampleRatesPreserveSineCalibrationAntiPhaseAndFrontPairSemantics(int sampleRate)
    {
        var engine = Signals.DefaultEngine(sampleRate, 6);
        const int band = 50;
        var hz = engine.Plan[band].CenterHz;
        var samples = Signals.Interleaved(engine.RequiredFrames, 6, (channel, frame) =>
            Signals.Sine(hz, channel == 0 ? 0.25 : channel == 1 ? -0.5 : 10, frame, sampleRate));
        var combined = new double[83];
        var left = new double[83];
        var right = new double[83];
        engine.Analyze(samples, engine.RequiredFrames, combined, left, right);
        Assert.Equal(band, Signals.ArgMax(combined));
        Assert.InRange(LevelScale.PowerToDb(left[band]) - 20 * Math.Log10(0.25), -0.05, 0.05);
        Assert.InRange(LevelScale.PowerToDb(right[band]) - 20 * Math.Log10(0.5), -0.05, 0.05);
        Assert.InRange(LevelScale.PowerToDb(combined[band]) - 10 * Math.Log10((0.25 * 0.25 + 0.5 * 0.5) / 2), -0.05, 0.05);
        Assert.All(combined, power => Assert.True(power >= 0 && !double.IsInfinity(power)));
        Assert.InRange(engine.RequiredFrames / (double)sampleRate, 0.24, 0.49);
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(48000)]
    [InlineData(192000)]
    public void ImpulseBandPowersMatchAnIndependentWindowedDft(int sampleRate)
    {
        const int length = 256;
        var engine = new BandSpectrumAnalyzer(
            BandPlan.CreateLogarithmic(83, 20, 20000, sampleRate), 1, new[] { length });
        var samples = Enumerable.Repeat(0.2f, length).ToArray();
        samples[length / 3] += 0.75f;
        var actual = Signals.AnalyzePower(engine, samples);
        var window = Enumerable.Range(0, length).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / length)).ToArray();
        var mean = Enumerable.Range(0, length).Sum(i => window[i] * samples[i]) / window.Sum();
        var windowEnergy = window.Sum(value => value * value);
        var fftLength = engine.GetDiagnostic(0).FftLength;
        var binPower = new double[fftLength / 2 + 1];
        for (var bin = 1; bin < binPower.Length; bin++)
        {
            double re = 0, im = 0;
            for (var frame = 0; frame < length; frame++)
            {
                var value = (samples[frame] - mean) * window[frame];
                var phase = 2 * Math.PI * bin * frame / fftLength;
                re += value * Math.Cos(phase);
                im -= value * Math.Sin(phase);
            }
            binPower[bin] = (bin == fftLength / 2 ? 1 : 2) * (re * re + im * im) / (fftLength * windowEnergy);
        }

        for (var band = 0; band < engine.Plan.Count; band++)
        {
            var diagnostic = engine.GetDiagnostic(band);
            Assert.Equal(fftLength, diagnostic.FftLength);
            double expected = 0;
            for (var index = 0; index < diagnostic.Weights.Length; index++)
                expected += diagnostic.Weights[index] * binPower[diagnostic.FirstBin + index];
            expected /= BandSpectrumAnalyzer.FullScaleSinePower;
            Assert.InRange(actual[band], expected - 1e-12, expected + 1e-12);
        }
    }
}
