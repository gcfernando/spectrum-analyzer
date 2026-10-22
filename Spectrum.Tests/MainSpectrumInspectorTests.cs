using System;
using System.Drawing;
using System.Linq;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class MainSpectrumInspectorTests
{
    private static CaptureStatus Status(CaptureState state = CaptureState.Capturing, string id = "output-a", int rate = 48000, int channels = 2)
        => new(state, "test capture", id, "Test output", null, rate, channels);
    private static AnalysisFrame Frame(double power = 1e-10, bool signal = true, bool opposite = false,
        int channels = 2, int frames = 32, long firstValid = 0, int rate = 48000)
    {
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, rate);
        var pcm = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            pcm[f * channels] = signal ? .25f : 0;
            if (channels > 1) pcm[f * channels + 1] = opposite ? -pcm[f * channels] : pcm[f * channels];
        }
        var powers = Enumerable.Repeat(power, 83).ToArray();
        return new AnalysisFrame(plan, channels, frames, Math.Max(frames, firstValid), pcm, powers, powers, powers, firstValidFrame: firstValid);
    }
    private static MainSpectrumReadout Read(AnalysisFrame frame, string mode = "Spectrum", int band = 80)
        => MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(frame), band, mode, Status(rate: frame.SampleRate, channels: frame.Channels));

    [Theory]
    [InlineData("Spectrum")] [InlineData("Bricks")] [InlineData("LED")]
    [InlineData("Contour")] [InlineData("Peak Trace")] [InlineData("Threshold Monitor")]
    [InlineData("Overview")] [InlineData("Band Matrix")] [InlineData("Radial Spectrum")]
    public void RawQuietBandIsPreservedBelowTheLegacyDisplayFloor(string mode)
    {
        var frame = Frame(); var result = Read(frame, mode);
        Assert.Equal(81, result.BandNumber); Assert.Equal(-100, result.LevelDb, 10);
        Assert.Equal(1e-10, result.Power); Assert.True(result.IsBelowFloor);
        Assert.False(result.IsDigitalSilence); Assert.False(result.IsUnavailable);
        Assert.Equal(frame.Plan[80].LowerHz, result.LowerHz); Assert.Equal(frame.Plan[80].UpperHz, result.UpperHz);
        Assert.Equal("dBFS (full-scale sine)", result.Units);
        Assert.Equal(0, LevelScale.ToDisplayByte(result.LevelDb));
    }

    [Theory]
    [InlineData(false, false, true)] [InlineData(true, false, false)] [InlineData(true, true, false)]
    public void ZeroBandPowerIsNotSilenceUnlessValidPcmEstablishesIt(bool signal, bool opposite, bool silence)
    {
        var result = Read(Frame(0, signal, opposite));
        Assert.Equal(silence, result.IsDigitalSilence);
        Assert.Equal(silence ? "Digital silence" : "Zero band power", result.Status);
        Assert.True(double.IsNegativeInfinity(result.LevelDb)); Assert.False(result.IsBelowFloor);
    }

    [Fact]
    public void SavedZeroPowerAndNonSilentCaptureStatusDoNotFabricatePcmSilence()
    {
        var result = Read(Frame(0, frames: 0));
        Assert.Equal("Zero band power", result.Status); Assert.False(result.IsDigitalSilence);
        Assert.False(result.IsUnavailable);
    }

    [Fact]
    public void SilenceChecksExcludeTheInvalidPrefixAndScrubNonfiniteSamples()
    {
        var source = Frame(0, firstValid: 16); // Only the last sixteen frames are valid.
        Array.Clear(source.Samples, 32, 32);
        source.Samples[32] = float.NaN; source.Samples[33] = float.PositiveInfinity;
        Assert.True(Read(source).IsDigitalSilence);
    }

    [Fact]
    public void DerivedChannelPowersMustBeSuppliedRatherThanInventedFromIndependentPowers()
    {
        var frame = Frame(0, opposite: true);
        var missing = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(frame, selectedChannel: "Mid"), 80, "Spectrum", Status());
        Assert.True(missing.IsUnavailable); Assert.True(double.IsNaN(missing.LevelDb));
        var zero = new double[83];
        var mid = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(frame, selectedPower: zero, selectedChannel: "Mid"), 80, "Spectrum", Status());
        var side = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(frame, selectedPower: zero, selectedChannel: "Side"), 80, "Spectrum", Status());
        Assert.True(mid.IsDigitalSilence); Assert.Equal("Zero band power", side.Status);
        Assert.False(side.IsDigitalSilence);
    }

    [Theory]
    [InlineData("Right")] [InlineData("Side")]
    public void MissingMonoChannelIsUnavailableRatherThanMeasuredZero(string channel)
    {
        var result = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(Frame(0, false, channels: 1), selectedChannel: channel),
            80, "Spectrum", Status(channels: 1));
        Assert.True(result.IsUnavailable); Assert.False(result.IsDigitalSilence);
        Assert.True(double.IsNaN(result.LevelDb)); Assert.True(double.IsNaN(result.RightDb));
    }

    [Fact]
    public void FrozenDisplayedFrameKeepsItsMeasurementsWhenCurrentSourceChangesOrFails()
    {
        var snapshot = new MainSpectrumSnapshot(Frame(), sourceDeviceId: "output-a");
        var frozen = MainSpectrumInspector.Inspect(snapshot, 80, "Spectrum", Status(id: "output-b"), true);
        Assert.True(frozen.IsDisplayFrozen); Assert.True(frozen.SourceChanged); Assert.False(frozen.IsUnavailable);
        Assert.Equal(-100, frozen.LevelDb, 10); Assert.Equal("Capture active", frozen.CaptureText);
        var live = MainSpectrumInspector.Inspect(snapshot, 80, "Spectrum", Status(id: "output-b"));
        Assert.True(live.IsUnavailable); Assert.True(double.IsNaN(live.LevelDb));
        var unavailable = MainSpectrumInspector.Inspect(snapshot, 80, "Spectrum", Status(CaptureState.Unavailable), true);
        Assert.Equal(-100, unavailable.LevelDb, 10); Assert.Equal("Capture unavailable", unavailable.CaptureText);
        Assert.False(unavailable.IsUnavailable);
    }

    [Fact]
    public void CaptureSilenceDoesNotRelabelAFrozenNonSilentWindow()
    {
        var result = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(Frame(0, signal: true)), 80,
            "Spectrum", Status(CaptureState.Silent), true);
        Assert.False(result.IsDigitalSilence); Assert.Equal("Zero band power", result.Status);
        Assert.Equal("Capture active · digital silence", result.CaptureText);
    }

    [Fact]
    public void StereoCompareReportsIndependentPowersRatherThanPresentingCombinedAsATrace()
    {
        var source = Frame();
        var combined = Enumerable.Repeat(2.5e-4, 83).ToArray();
        var left = Enumerable.Repeat(4e-4, 83).ToArray();
        var right = Enumerable.Repeat(1e-4, 83).ToArray();
        var frame = new AnalysisFrame(source.Plan, 2, source.FrameCount, source.EndFrame, source.Samples, combined, left, right);
        var result = Read(frame, "Stereo Compare");
        Assert.Equal(10 * Math.Log10(4e-4), result.LeftDb, 10);
        Assert.Equal(-40, result.RightDb, 10);
        Assert.Equal(10 * Math.Log10(2.5e-4), result.LevelDb, 10);
        Assert.Contains("L ", result.Text); Assert.Contains(" / R ", result.Text);
        var selected = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(frame), 80, "Stereo Spectrum", Status(), selectedChannel: "Right");
        Assert.Equal(-40, selected.LevelDb, 10); Assert.Equal("Right", selected.Channel);
    }

    [Theory]
    [InlineData(CaptureState.Starting)] [InlineData(CaptureState.Unavailable)]
    [InlineData(CaptureState.Recovering)] [InlineData(CaptureState.Stopped)]
    public void UnfrozenStaleFrameDoesNotBecomeACurrentMeasurement(CaptureState state)
    {
        var result = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(Frame()), 80, "LED", Status(state));
        Assert.True(result.IsUnavailable); Assert.True(double.IsNaN(result.LevelDb));
    }

    [Fact]
    public void SevenMeasuredPowersAreCopiedAndUseTheirActualTruncatedEdges()
    {
        var powers = Enumerable.Repeat(1e-10, 7).ToArray();
        var frame = Frame(rate: 8000); var snapshot = new MainSpectrumSnapshot(frame, powers);
        powers[4] = 1; // The paired seven-band payload must remain a stable snapshot.
        var partial = MainSpectrumInspector.Inspect(snapshot, 4, "Advanced Spectral Balance", Status(rate: 8000));
        Assert.Equal(2000, partial.LowerHz); Assert.Equal(4000, partial.UpperHz);
        Assert.Equal(-100, partial.LevelDb, 10); Assert.Equal(Math.Sqrt(2000 * 4000), partial.CenterHz, 8);
        var absent = MainSpectrumInspector.Inspect(snapshot, 5, "Advanced Spectral Balance", Status(rate: 8000));
        Assert.True(absent.IsUnavailable); Assert.True(double.IsNaN(absent.LevelDb));
        var notMeasured = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(frame), 4, "Advanced Spectral Balance", Status(rate: 8000));
        Assert.True(notMeasured.IsUnavailable);
    }

    [Theory]
    [InlineData("Oscilloscope")] [InlineData("Stereo Levels")] [InlineData("Dynamics History")]
    [InlineData("Waterfall")] [InlineData("Spectral History")] [InlineData("Octave Waterfall")]
    [InlineData("Octave Spectrum")] [InlineData("Spectral Balance")] [InlineData("Level Change")]
    [InlineData("Stereo Phase")]
    public void NonBandViewsDescribeTheirPurposeWithoutFabricatedBandLevels(string mode)
    {
        var result = Read(Frame(), mode);
        Assert.False(result.IsBandApplicable); Assert.Equal(0, result.BandNumber);
        Assert.True(double.IsNaN(result.LevelDb)); Assert.Equal("Mode purpose", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Purpose));
    }

    [Fact]
    public void FrequencyLookupUsesActualEdgesIncludingUnequalBandsAndFinalBoundary()
    {
        var plan = BandPlan.CreateFromEdges(new[] { 20d, 60, 250, 500, 2000, 6000, 10000, 20000 }, 48000);
        Assert.Equal(0, MainSpectrumInspector.FindBandAtFrequency(plan, 20));
        Assert.Equal(1, MainSpectrumInspector.FindBandAtFrequency(plan, 60));
        Assert.Equal(6, MainSpectrumInspector.FindBandAtFrequency(plan, 20000));
        Assert.Equal(-1, MainSpectrumInspector.FindBandAtFrequency(plan, 20000.1));
        Assert.Equal(-1, MainSpectrumInspector.FindBandAtFrequency(plan, 19.9));
        Assert.Equal(-1, MainSpectrumInspector.FindBandAtFrequency(plan, double.NaN));
    }

    [Fact]
    public void HitTestingUsesActualLogPlotBoundsAndRejectsUnmeasuredHighFrequencies()
    {
        var plan = Frame(rate: 8000).Plan; var bounds = new RectangleF(44, 30, 600, 200);
        var hz = 1000d; var x = bounds.Left + bounds.Width * SpectrumAxis.Position(hz);
        var hit = MainSpectrumHitTest.HitTest("Contour", plan, new PointF((float)x, 100), bounds);
        Assert.Equal(MainSpectrumInspector.FindBandAtFrequency(plan, hz), hit.BandIndex);
        Assert.False(MainSpectrumHitTest.HitTest("Contour", plan, new PointF(644, 100), bounds).HasBand);
        Assert.False(MainSpectrumHitTest.HitTest("Contour", plan, new PointF((float)x, 29), bounds).HasBand);
    }

    [Fact]
    public void CardAndLegacyHitsRejectGapsRatherThanApproximatingIndicesFromWidth()
    {
        var plan = Frame().Plan; var cards = new RectangleF[83];
        cards[5] = new RectangleF(100, 30, 20, 40);
        Assert.Equal(5, MainSpectrumHitTest.HitTest("Band Matrix", plan, new PointF(110, 40), RectangleF.Empty, cards).BandIndex);
        Assert.False(MainSpectrumHitTest.HitTest("Band Matrix", plan, new PointF(121, 40), RectangleF.Empty, cards).HasBand);
        Assert.Equal(5, MainSpectrumHitTest.HitTest("Bricks", plan, new PointF(110, 40), RectangleF.Empty, cards).BandIndex);
        Assert.False(MainSpectrumHitTest.HitTest("Bricks", plan, new PointF(110, 40), new RectangleF(0, 0, 900, 400)).HasBand);
    }

    [Fact]
    public void RadialHitsUseTheActualShiftedCenterAndExcludeTheEmptyCenterAndLegend()
    {
        var plan = Frame().Plan; var center = new PointF(400, 180);
        var top = MainSpectrumHitTest.HitTest("Radial Spectrum", plan, new PointF(400, 100), RectangleF.Empty,
            radialCenter: center, radialRadius: 100);
        Assert.Equal(0, top.BandIndex);
        var angle = 2 * Math.PI * 82 / 83;
        var lastPoint = new PointF(center.X + (float)(80 * Math.Sin(angle)), center.Y - (float)(80 * Math.Cos(angle)));
        Assert.Equal(82, MainSpectrumHitTest.HitTest("Radial Spectrum", plan, lastPoint, RectangleF.Empty,
            radialCenter: center, radialRadius: 100).BandIndex);
        Assert.False(MainSpectrumHitTest.HitTest("Radial Spectrum", plan, center, RectangleF.Empty,
            radialCenter: center, radialRadius: 100).HasBand);
        Assert.False(MainSpectrumHitTest.HitTest("Radial Spectrum", plan, new PointF(0, 180), RectangleF.Empty,
            radialCenter: center, radialRadius: 100).HasBand);
    }

    [Fact]
    public void SevenSlotsAndStereoPanelsReturnTheActualBandAndChannel()
    {
        var frame = Frame(); var seven = new RectangleF(40, 30, 700, 150);
        Assert.Equal(4, MainSpectrumHitTest.HitTest("Advanced Spectral Balance", frame.Plan, new PointF(490, 80), seven).BandIndex);
        Assert.False(MainSpectrumHitTest.HitTest("Advanced Spectral Balance", frame.Plan, new PointF(30, 80), seven).HasBand);
        var panels = new[] { new RectangleF(44, 30, 600, 150), new RectangleF(44, 210, 600, 150) };
        var point = new PointF(44 + 600 * (float)SpectrumAxis.Position(frame.Plan[80].CenterHz), 240);
        var hit = MainSpectrumHitTest.HitTest("Stereo Spectrum", frame.Plan, point, RectangleF.Empty, stereoPlotBounds: panels);
        Assert.Equal(80, hit.BandIndex); Assert.Equal("Right", hit.Channel);
        var result = MainSpectrumInspector.Inspect(new MainSpectrumSnapshot(frame), hit.BandIndex, "Stereo Spectrum", Status(), selectedChannel: hit.Channel);
        Assert.Equal("Right", result.Channel); Assert.Equal(-100, result.LevelDb, 10);
        Assert.False(MainSpectrumHitTest.HitTest("Waterfall", frame.Plan, point, panels[1]).HasBand);
    }
}
