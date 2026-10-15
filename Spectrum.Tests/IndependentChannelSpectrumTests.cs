// Developer :: > Gehan Fernando
using System;
using System.Diagnostics;
using System.Linq;
using Spectrum.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

// Left/right channels get their own FFT, and the result must match measuring each mono signal on its own.
public class IndependentChannelSpectrumTests
{
    private readonly ITestOutputHelper _output;
    public IndependentChannelSpectrumTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(48000, 1.0, 0.25, 0.0, false)]
    [InlineData(48000, 1.0, 0.0, 0.0, false)]
    [InlineData(48000, 0.0, 1.0, 0.0, false)]
    [InlineData(48000, 0.0, 0.0, 0.0, false)]
    [InlineData(48000, 1.0, 1.0, Math.PI, false)]
    [InlineData(48000, 0.5, 0.9, Math.PI / 3, true)]
    [InlineData(16000, 0.5, 0.25, Math.PI / 2, true)]
    [InlineData(44100, 0.8, 0.4, 0.0, true)]
    [InlineData(96000, 0.5, 1.0, Math.PI, true)]
    public void IndependentOutputsAgreeWithMonoEnginesAndPreserveTheCombinedSpectrum(
        int sampleRate, double leftAmplitude, double rightAmplitude, double phase, bool differentFrequencies)
    {
        var engine = Signals.DefaultEngine(sampleRate, 2);
        var leftHz = engine.Plan[50].CenterHz;
        var rightHz = engine.Plan[differentFrequencies ? 66 : 50].CenterHz;
        var samples = Signals.Interleaved(engine.RequiredFrames, 2, (channel, frame) => channel == 0
            ? Signals.Sine(leftHz, leftAmplitude, frame, sampleRate)
            : Signals.Sine(rightHz, rightAmplitude, frame, sampleRate, phase));
        var combined = new double[engine.Plan.Count];
        var left = new double[combined.Length];
        var right = new double[combined.Length];
        engine.Analyze(samples, engine.RequiredFrames, combined, left, right);

        AssertPowersClose(AnalyzeMonoChannel(samples, sampleRate, 0), left);
        AssertPowersClose(AnalyzeMonoChannel(samples, sampleRate, 1), right);
        Assert.Equal(Signals.AnalyzePower(Signals.DefaultEngine(sampleRate, 2), samples), combined);
        for (var band = 0; band < combined.Length; band++)
            AssertPowerClose((left[band] + right[band]) / 2, combined[band]);
        Assert.Equal(combined[50], engine.GetDiagnostic(50).RelativePower);
        if (leftAmplitude > 0)
            Assert.InRange(LevelScale.PowerToDb(left[50]) - 20 * Math.Log10(leftAmplitude), -0.05, 0.05);
        if (rightAmplitude > 0)
            Assert.InRange(LevelScale.PowerToDb(right[differentFrequencies ? 66 : 50]) -
                20 * Math.Log10(rightAmplitude), -0.05, 0.05);
    }

    [Fact]
    public void SwappingFrontChannelsSwapsTheirIndependentSpectra()
    {
        var engine = Signals.DefaultEngine();
        var samples = Signals.Interleaved(engine.RequiredFrames, 2, (channel, frame) =>
            Signals.Sine(engine.Plan[channel == 0 ? 42 : 68].CenterHz, channel == 0 ? 0.8 : 0.3, frame, 48000));
        var mixed = new double[83];
        var left = new double[83];
        var right = new double[83];
        engine.Analyze(samples, engine.RequiredFrames, mixed, left, right);
        var swapped = Signals.Interleaved(engine.RequiredFrames, 2,
            (channel, frame) => samples[frame * 2 + 1 - channel]);
        var swappedMixed = new double[83];
        var swappedLeft = new double[83];
        var swappedRight = new double[83];
        engine.Analyze(swapped, engine.RequiredFrames, swappedMixed, swappedLeft, swappedRight);
        AssertPowersClose(left, swappedRight);
        AssertPowersClose(right, swappedLeft);
        AssertPowersClose(mixed, swappedMixed);
    }

    [Fact]
    public void MonoHasNoInventedRightChannelAndWritesOnlyTheBandCount()
    {
        var engine = Signals.DefaultEngine(48000, 1);
        var samples = Signals.MonoSine(engine.RequiredFrames, 48000, engine.Plan[50].CenterHz, 0.7);
        var combined = Enumerable.Repeat(-1d, 84).ToArray();
        var left = Enumerable.Repeat(-2d, 84).ToArray();
        var right = Enumerable.Repeat(-3d, 84).ToArray();
        engine.Analyze(samples, engine.RequiredFrames, combined, left, right);
        for (var band = 0; band < 83; band++)
        {
            Assert.Equal(combined[band], left[band]);
            Assert.Equal(0, right[band]);
        }
        Assert.Equal(-1, combined[83]);
        Assert.Equal(-2, left[83]);
        Assert.Equal(-3, right[83]);
    }

    [Fact]
    public void MultichannelOutputsMeasureOnlyTheFrontPair()
    {
        var engine = Signals.DefaultEngine(48000, 6);
        var samples = Signals.Interleaved(engine.RequiredFrames, 6, (channel, frame) =>
            Signals.Sine(engine.Plan[channel == 0 ? 48 : channel == 1 ? 62 : 35].CenterHz,
                channel == 0 ? 0.25 : channel == 1 ? 0.75 : 10, frame, 48000));
        var frontPair = Signals.Interleaved(engine.RequiredFrames, 2,
            (channel, frame) => samples[frame * 6 + channel]);
        var combined = new double[83];
        var left = new double[83];
        var right = new double[83];
        engine.Analyze(samples, engine.RequiredFrames, combined, left, right);
        Assert.Equal(Signals.AnalyzePower(Signals.DefaultEngine(), frontPair), combined);
        AssertPowersClose(AnalyzeMonoChannel(frontPair, 48000, 0), left);
        AssertPowersClose(AnalyzeMonoChannel(frontPair, 48000, 1), right);
    }

    [Fact]
    public void NonfiniteSamplesAreSanitizedBeforeBothIndependentMeasurements()
    {
        var engine = Signals.DefaultEngine();
        var dirty = Signals.StereoSine(engine.RequiredFrames, 48000, 1000, 0.5, 0.25);
        var clean = (float[])dirty.Clone();
        var indices = new[] { 10, 777, 5000, 20001, 32000 };
        for (var i = 0; i < indices.Length; i++)
        {
            dirty[indices[i]] = i % 3 == 0 ? float.NaN : i % 3 == 1 ? float.PositiveInfinity : float.NegativeInfinity;
            clean[indices[i]] = 0;
        }
        var combined = new double[83];
        var left = new double[83];
        var right = new double[83];
        engine.Analyze(dirty, engine.RequiredFrames, combined, left, right);
        var cleanCombined = new double[83];
        var cleanLeft = new double[83];
        var cleanRight = new double[83];
        engine.Analyze(clean, engine.RequiredFrames, cleanCombined, cleanLeft, cleanRight);
        Assert.Equal(cleanCombined, combined);
        Assert.Equal(cleanLeft, left);
        Assert.Equal(cleanRight, right);
        Assert.All(left.Concat(right), power => Assert.True(power >= 0 && !double.IsInfinity(power)));
    }

    [Fact]
    public void OptionalOutputsAreIndependentAndRefreshAfterCombinedOnlyCalls()
    {
        var engine = Signals.DefaultEngine();
        var samples = Signals.StereoSine(engine.RequiredFrames, 48000, 1000, 0.5, 0.25);
        var combined = new double[83];
        var left = new double[83];
        var right = new double[83];
        engine.Analyze(samples, engine.RequiredFrames, combined, left, null);
        AssertPowersClose(AnalyzeMonoChannel(samples, 48000, 0), left);
        engine.Analyze(new float[samples.Length], engine.RequiredFrames, combined);
        engine.Analyze(samples, engine.RequiredFrames, combined, null, right);
        AssertPowersClose(AnalyzeMonoChannel(samples, 48000, 1), right);
        engine.Analyze(new float[samples.Length], engine.RequiredFrames, combined, left, right);
        Assert.All(left.Concat(right).Concat(combined), power => Assert.Equal(0, power));
    }

    [Fact]
    public void InvalidOrAliasedOutputsFailBeforeAnyMeasurementIsWritten()
    {
        var engine = Signals.DefaultEngine();
        var samples = new float[engine.RequiredFrames * 2];
        var combined = Enumerable.Repeat(-1d, 83).ToArray();
        var left = Enumerable.Repeat(-2d, 83).ToArray();
        var right = Enumerable.Repeat(-3d, 83).ToArray();
        Assert.Throws<ArgumentNullException>(() => engine.Analyze(null, engine.RequiredFrames, combined, left, right));
        Assert.Throws<ArgumentException>(() => engine.Analyze(samples, engine.RequiredFrames, null, left, right));
        Assert.Throws<ArgumentException>(() => engine.Analyze(samples, engine.RequiredFrames, new double[82], left, right));
        Assert.Throws<ArgumentException>(() => engine.Analyze(samples, engine.RequiredFrames, combined, new double[82], right));
        Assert.Throws<ArgumentException>(() => engine.Analyze(samples, engine.RequiredFrames, combined, left, new double[82]));
        Assert.Throws<ArgumentException>(() => engine.Analyze(samples, engine.RequiredFrames, combined, combined, right));
        Assert.Throws<ArgumentException>(() => engine.Analyze(samples, engine.RequiredFrames, combined, left, combined));
        Assert.Throws<ArgumentException>(() => engine.Analyze(samples, engine.RequiredFrames, combined, left, left));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Analyze(samples, engine.RequiredFrames - 1, combined, left, right));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Analyze(samples, engine.RequiredFrames + 1, combined, left, right));
        Assert.All(combined, value => Assert.Equal(-1, value));
        Assert.All(left, value => Assert.Equal(-2, value));
        Assert.All(right, value => Assert.Equal(-3, value));
    }

    [Fact]
    public void IndependentOutputsAllocateNoFrameMemoryAndFitTheExistingAnalysisBudget()
    {
        var engine = Signals.DefaultEngine();
        var samples = Signals.StereoSine(engine.RequiredFrames, 48000, 1000, 0.5, 0.25);
        var combined = new double[83];
        var left = new double[83];
        var right = new double[83];
        for (var frame = 0; frame < 20; frame++)
            engine.Analyze(samples, engine.RequiredFrames, combined, left, right);
        var watch = new Stopwatch();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        const int frames = 200;
        for (var frame = 0; frame < frames; frame++)
            engine.Analyze(samples, engine.RequiredFrames, combined, left, right);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var milliseconds = watch.Elapsed.TotalMilliseconds / frames;
        _output.WriteLine($"Independent stereo 83-band analysis: {milliseconds:F3} ms/frame; {allocated} bytes over {frames} frames.");
        Assert.Equal(0, allocated);
        Assert.True(milliseconds < 12.5, $"Independent analysis exceeded the existing 12.5 ms budget: {milliseconds:F3} ms/frame.");
    }

    private static double[] AnalyzeMonoChannel(float[] interleaved, int sampleRate, int channel)
    {
        var engine = Signals.DefaultEngine(sampleRate, 1);
        var mono = Signals.Interleaved(interleaved.Length / 2, 1, (_, frame) => interleaved[frame * 2 + channel]);
        return Signals.AnalyzePower(engine, mono);
    }

    private static void AssertPowersClose(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var band = 0; band < expected.Length; band++) AssertPowerClose(expected[band], actual[band]);
    }

    private static void AssertPowerClose(double expected, double actual)
        => Assert.InRange(actual, expected - (1e-12 + Math.Abs(expected) * 1e-8),
            expected + (1e-12 + Math.Abs(expected) * 1e-8));
}
