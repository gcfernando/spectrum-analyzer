// Developer :: > Gehan Fernando
using System;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class AnalysisMeasurementsTests
{
    private const int Fs = 48000;

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Mono997Hz_UsesTheItuReferenceLoudnessAndAmplitudeRms(int rate)
    {
        var samples = Tone(rate, 1, rate * 4, 997, .1);
        var result = new AudioMeasurementAnalyzer(rate, 1).Process(samples, samples.Length);
        Assert.InRange(result.LeftRmsDb, -23.02, -23.00);
        // BS.1770-5 Annex 1 gives -3.01 LKFS for the 0 dBFS 997 Hz mono sine.
        Assert.InRange(result.MomentaryLufs, -23.08, -22.94);
        Assert.InRange(result.ShortTermLufs, -23.08, -22.94);
        Assert.InRange(result.IntegratedLufs, -23.08, -22.94);
        Assert.True(double.IsNaN(result.RightRmsDb));
        Assert.True(double.IsNaN(result.Correlation));
        Assert.True(result.LoudnessSupported);
        Assert.False(result.HasStereo);
        Assert.True((long)result.TruePeakOversampling * rate >= 192000);
    }

    [Fact]
    public void DualMono997Hz_AddsChannelPowersWithoutAveragingThem()
    {
        var samples = Tone(Fs, 2, Fs * 4, 997, Math.Pow(10, -23.0 / 20));
        var result = new AudioMeasurementAnalyzer(Fs, 2).Process(samples, samples.Length / 2);
        Assert.InRange(result.IntegratedLufs, -23.05, -22.95);
        Assert.InRange(result.LeftRmsDb, -26.02, -26.00);
        Assert.InRange(result.Correlation, .999999999, 1);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.SideRmsDb);
        Assert.InRange(result.MidRmsDb, -26.02, -26.00);
    }

    [Fact]
    public void AntiPhaseStereo_HasNegativeCorrelationAndNoMidSignal()
    {
        var samples = Tone(Fs, 2, Fs, 1000, .5, true);
        var result = new AudioMeasurementAnalyzer(Fs, 2).Process(samples, Fs);
        Assert.InRange(result.Correlation, -1, -.999999999);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.MidRmsDb);
        Assert.InRange(result.SideRmsDb, -9.04, -9.02);
        Assert.InRange(result.LeftRmsDb, -9.04, -9.02);
        var samePhase = new AudioMeasurementAnalyzer(Fs, 2).Process(Tone(Fs, 2, Fs, 1000, .5), Fs);
        Assert.Equal(samePhase.IntegratedLufs, result.IntegratedLufs, 10);
    }

    [Fact]
    public void DigitalSilence_IsMeasuredSilenceWithUndefinedCorrelation()
    {
        var result = new AudioMeasurementAnalyzer(Fs, 2).Process(new float[Fs * 6], Fs * 3);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.LeftRmsDb);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.RightRmsDb);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.SamplePeakDb);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.TruePeakDb);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.IntegratedLufs);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.MomentaryLufs);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.ShortTermLufs);
        Assert.True(double.IsNaN(result.Correlation));
        Assert.Equal(0, result.ClippedSamples);
    }

    [Fact]
    public void NonFiniteSamples_AreScrubbedBeforeTheyReachFilterState()
    {
        var samples = new float[Fs * 6];
        samples[0] = float.NaN;
        samples[10] = float.PositiveInfinity;
        samples[100] = float.NegativeInfinity;
        var result = new AudioMeasurementAnalyzer(Fs, 2).Process(samples, Fs * 3);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.IntegratedLufs);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.TruePeakDb);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.LeftRmsDb);
        Assert.Equal(0, result.ClippedSamples);
    }

    [Fact]
    public void LoudnessAndPeakResults_AreIndependentOfPcmChunkBoundaries()
    {
        const int frames = Fs * 4 + 713;
        var samples = Tone(Fs, 2, frames, 997, .37);
        var wholeAnalyzer = new AudioMeasurementAnalyzer(Fs, 2);
        wholeAnalyzer.Process(samples, frames);
        var whole = wholeAnalyzer.Complete();
        var chunkedAnalyzer = new AudioMeasurementAnalyzer(Fs, 2);
        var position = 0;
        while (position < frames)
        {
            var count = Math.Min(257, frames - position);
            var chunk = new float[count * 2];
            Array.Copy(samples, position * 2, chunk, 0, chunk.Length);
            chunkedAnalyzer.Process(chunk, count);
            position += count;
        }
        var chunked = chunkedAnalyzer.Complete();
        Assert.Equal(whole.TotalFrames, chunked.TotalFrames);
        Assert.Equal(whole.GateBlocks, chunked.GateBlocks);
        Assert.Equal(whole.SamplePeakDb, chunked.SamplePeakDb, 12);
        Assert.Equal(whole.TruePeakDb, chunked.TruePeakDb, 12);
        Assert.Equal(whole.IntegratedLufs, chunked.IntegratedLufs, 12);
        Assert.Equal(whole.MomentaryLufs, chunked.MomentaryLufs, 12);
        Assert.Equal(whole.ShortTermLufs, chunked.ShortTermLufs, 12);
    }

    [Fact]
    public void Loudness_RequiresCompleteWindowsAndDoesNotInventWarmupSilence()
    {
        var analyzer = new AudioMeasurementAnalyzer(Fs, 1);
        var samples = Tone(Fs, 1, Fs * 3, 997, .1);
        var warmup = analyzer.Process(samples, 19199);
        Assert.True(double.IsNaN(warmup.MomentaryLufs));
        Assert.True(double.IsNaN(warmup.IntegratedLufs));
        Assert.True(double.IsNaN(warmup.ShortTermLufs));
        var boundary = analyzer.Process(new[] { samples[19199] }, 1);
        Assert.False(double.IsNaN(boundary.MomentaryLufs));
        Assert.False(double.IsNaN(boundary.IntegratedLufs));
        Assert.True(double.IsNaN(boundary.ShortTermLufs));
        var remaining = new float[samples.Length - 19200];
        Array.Copy(samples, 19200, remaining, 0, remaining.Length);
        Assert.False(double.IsNaN(analyzer.Process(remaining, remaining.Length).ShortTermLufs));
    }

    [Fact]
    public void AbsoluteGate_RejectsSignalBelowMinus70Lufs()
    {
        var result = new AudioMeasurementAnalyzer(Fs, 1).Process(Tone(Fs, 1, Fs * 3, 997, 1e-4), Fs * 3);
        Assert.InRange(result.MomentaryLufs, -83.08, -82.94);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, result.IntegratedLufs);
        Assert.Equal(0, result.GateBlocks);
    }

    [Fact]
    public void RelativeGate_ExcludesLongQuietSectionAboveTheAbsoluteGate()
    {
        var analyzer = new AudioMeasurementAnalyzer(Fs, 1);
        var loud = analyzer.Process(Tone(Fs, 1, Fs * 3, 997, .1), Fs * 3);
        // This -53 LUFS section is above the absolute gate, but below the relative one.
        var afterQuiet = analyzer.Process(Tone(Fs, 1, Fs * 12, 997, .00316227766), Fs * 12);
        Assert.InRange(afterQuiet.IntegratedLufs - loud.IntegratedLufs, -.5, .05);
        Assert.InRange(afterQuiet.MomentaryLufs, -53.08, -52.94);
    }

    [Fact]
    public void FirTruePeak_DetectsIntersamplePeakAboveSamplePeak()
    {
        var samples = new float[Fs];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)Math.Sin(Math.PI * i / 2 + Math.PI / 4);
        var analyzer = new AudioMeasurementAnalyzer(Fs, 1);
        var result = analyzer.Process(samples, samples.Length);
        result = analyzer.Complete();
        Assert.InRange(result.SamplePeakDb, -3.011, -3.009);
        Assert.True(result.TruePeakDb > result.SamplePeakDb + 2.0);
        Assert.InRange(result.TruePeakDb, -.5, .8);
    }

    [Fact]
    public void ClippingAndHeldPeaks_PersistUntilReset()
    {
        var analyzer = new AudioMeasurementAnalyzer(Fs, 2);
        var first = analyzer.Process(new[] { 1f, -1f, 1.1f, -.99f }, 2);
        Assert.Equal(3, first.ClippedSamples);
        Assert.InRange(first.SamplePeakDb, .82, .84);
        var silent = analyzer.Process(new float[20], 10);
        Assert.Equal(3, silent.ClippedSamples);
        Assert.Equal(first.SamplePeakDb, silent.SamplePeakDb);
        analyzer.Reset();
        var reset = analyzer.Process(new float[20], 10);
        Assert.Equal(0, reset.ClippedSamples);
        Assert.Equal(10, reset.TotalFrames);
        Assert.Equal(AudioMeasurementAnalyzer.SilenceDb, reset.TruePeakDb);
        Assert.True(double.IsNaN(reset.IntegratedLufs));
    }

    [Fact]
    public void MultichannelInput_LabelsLoudnessUnavailableAndMeasuresOnlyFrontPair()
    {
        var samples = new float[6 * 480];
        for (var frame = 0; frame < 480; frame++)
        {
            samples[frame * 6] = .1f;
            samples[frame * 6 + 1] = .2f;
            for (var channel = 2; channel < 6; channel++) samples[frame * 6 + channel] = 10;
        }
        var result = new AudioMeasurementAnalyzer(Fs, 6).Process(samples, 480);
        Assert.True(result.HasStereo);
        Assert.False(result.LoudnessSupported);
        Assert.True(double.IsNaN(result.IntegratedLufs));
        Assert.True(double.IsNaN(result.MomentaryLufs));
        Assert.InRange(result.LeftRmsDb, -20.001, -19.999);
        Assert.InRange(result.RightRmsDb, -13.980, -13.978);
        Assert.Equal(0, result.ClippedSamples);
    }

    [Fact]
    public void Complete_FlushesOnlyTheTruePeakTailAndKeepsEarlierSnapshotsStable()
    {
        var analyzer = new AudioMeasurementAnalyzer(Fs, 1);
        var first = analyzer.Process(Tone(Fs, 1, 73, 12000, .9), 73);
        var firstPeak = first.TruePeakDb;
        var complete = analyzer.Complete();
        Assert.Equal(firstPeak, first.TruePeakDb);
        Assert.Equal(73, complete.TotalFrames);
        Assert.Equal(first.DurationSeconds, complete.DurationSeconds);
        Assert.True(complete.TruePeakDb >= first.TruePeakDb);
        Assert.Same(complete, analyzer.Complete());
    }

    [Fact]
    public void InvalidInputsFailBeforeChangingStreamingState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMeasurementAnalyzer(7999, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMeasurementAnalyzer(Fs, 0));
        var analyzer = new AudioMeasurementAnalyzer(Fs, 2);
        Assert.Throws<ArgumentNullException>(() => analyzer.Process(null, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.Process(new float[1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.Process(new float[2], -1));
        var empty = analyzer.Process(Array.Empty<float>(), 0);
        Assert.Equal(0, empty.TotalFrames);
        Assert.True(double.IsNaN(empty.SamplePeakDb));
    }

    private static float[] Tone(int rate, int channels, int frames, double frequency, double amplitude, bool antiPhase = false)
    {
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / rate));
            for (var channel = 0; channel < channels; channel++)
                samples[frame * channels + channel] = antiPhase && channel == 1 ? -sample : sample;
        }
        return samples;
    }
}
