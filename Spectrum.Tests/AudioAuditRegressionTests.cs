// Developer :: > Gehan Fernando
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Spectrum;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

[Collection("Capture lifecycle")]
public class AudioAuditRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(16000, 2)]
    [InlineData(22050, 2)]
    [InlineData(32000, 2)]
    [InlineData(48000, 2)]
    public void CapturedDigitalSilenceRetainsRealChannelAndNyquistBandAvailability(int sampleRate, int channels)
    {
        using var analyzer = new Analyzer(false) { SevenBandEnabled = true };
        var capture = CreateCapture(analyzer, sampleRate, channels);
        capture.History.Write(new float[capture.Engine.RequiredFrames * channels], capture.Engine.RequiredFrames);
        var frame = PublishSilence(analyzer, capture.State, capture.Stream, true);
        Assert.True(frame.HasSamples);
        Assert.True(frame.HasChannelSpectrum);
        Assert.Equal(channels >= 2, frame.HasStereo);
        Assert.All(frame.LeftSpectrum, value => Assert.Equal((byte)0, value));
        Assert.All(frame.RightSpectrum, value => Assert.Equal((byte)0, value));
        Assert.True(frame.HasSevenBand);
        for (var band = 0; band < SevenBandAnalyzer.BandCount; band++)
        {
            if (SevenBandAnalyzer.Definitions[band].LowerHz >= sampleRate / 2d)
                Assert.True(double.IsNaN(frame.SevenBandPower[band]), "An unsupported silent band must remain unavailable.");
            else Assert.Equal(0d, frame.SevenBandPower[band]);
        }
        Assert.Equal(CaptureState.Silent, analyzer.CurrentStatus.State);
        // No new PCM on another publication is an observation gap, not a second measured frame.
        var gap = PublishSilence(analyzer, capture.State, capture.Stream, true);
        Assert.False(gap.HasSamples);
        Assert.False(gap.HasChannelSpectrum);
        Assert.False(gap.HasSevenBand);
    }

    [Fact]
    public void DisabledSevenBandAndMissingPcmAreNotPresentedAsMeasuredSilence()
    {
        using var analyzer = new Analyzer(false);
        var capture = CreateCapture(analyzer, 48000, 2);
        capture.History.Write(new float[capture.Engine.RequiredFrames * 2], capture.Engine.RequiredFrames);
        var silent = PublishSilence(analyzer, capture.State, capture.Stream, true);
        Assert.True(silent.HasSamples);
        Assert.True(silent.HasChannelSpectrum);
        Assert.False(silent.HasSevenBand);
        var unavailable = PublishSilence(analyzer, capture.State, capture.Stream, false);
        Assert.False(unavailable.HasSamples);
        Assert.False(unavailable.HasChannelSpectrum);
        Assert.False(unavailable.HasSevenBand);
        Assert.Equal(CaptureState.Unavailable, analyzer.CurrentStatus.State);
    }

    [Theory]
    [InlineData(0.1, false)]
    [InlineData(0.00002, false)]
    [InlineData(0.1, true)]
    public void AZeroNativeLevelDoesNotReplaceNonzeroCapturedPcmOrItsFftTailWithFabricatedSilence(double amplitude, bool silentTail)
    {
        using var analyzer = new Analyzer(false) { SevenBandEnabled = true };
        var capture = CreateCapture(analyzer, 48000, 2);
        var frames = capture.Engine.RequiredFrames;
        var samples = Signals.StereoSine(frames, 48000, capture.Engine.Plan[50].CenterHz, amplitude, amplitude / 2);
        capture.History.Write(samples, frames);
        if (silentTail)
        {
            // The latest observed packet is silent, but the FFT window still contains the tone tail.
            capture.State.GetType().GetProperty("AudioEndFrame").SetValue(capture.State, (long)frames);
            var tail = new float[960];
            capture.History.Write(tail, 480);
        }
        var frame = PublishSilence(analyzer, capture.State, capture.Stream, true);
        Assert.True(frame.HasSamples);
        Assert.True(frame.HasChannelSpectrum);
        Assert.True(frame.HasSevenBand);
        var snapshot = new float[frames * 2];
        Assert.True(capture.History.TryCopyLatest(snapshot, frames, out var endFrame));
        var expected = new double[83];
        var left = new double[83];
        var right = new double[83];
        capture.Engine.Analyze(snapshot, frames, expected, left, right);
        Assert.Equal(left.Select(power => LevelScale.ToDisplayByte(LevelScale.PowerToDb(power))), frame.LeftSpectrum);
        Assert.Equal(right.Select(power => LevelScale.ToDisplayByte(LevelScale.PowerToDb(power))), frame.RightSpectrum);
        var seven = new SevenBandAnalyzer(48000, 2);
        var expectedSeven = new double[7];
        seven.Analyze(snapshot, frames, expectedSeven);
        Assert.Equal(expectedSeven, frame.SevenBandPower);
        Assert.True(frame.SevenBandPower.Sum() > 0);
        Assert.Equal(endFrame, frame.EndFrame);
        Assert.Equal(silentTail ? 480 : frames, frame.WindowFrames);
        Assert.Equal(1, frame.ObservationCount);
        if (silentTail) Assert.Equal(0, frame.LeftPeak);
        else Assert.True(frame.LeftPeak > 0);
    }

    [Fact]
    public void SevenBandReleaseAndHeldPeaksConsumeAFullSchedulingPause()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Mode = AdvancedVisualizationMode.AdvancedSpectralBalance };
            var data = new AudioVisualizationData();
            data.Update(new float[960], 480, 2, 48000);
            data.SetSevenBandPower(new double[7]);
            control.SetAudioData(data);
            var levels = Field<float[]>(control, "_sevenLevels");
            var peaks = Field<float[]>(control, "_sevenPeaks");
            var holds = Field<float[]>(control, "_sevenPeakHoldMs");
            for (var band = 0; band < 7; band++) { levels[band] = peaks[band] = 72; holds[band] = 800; }
            var before = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2;
            CombinedRandomizationTests.SetField(control, "_sevenLastTick", before);
            Call(control, "UpdateSevenBandBallistics");
            Assert.All(levels, level => Assert.InRange(level, 0, 0.05f));
            Assert.All(holds, hold => Assert.Equal(0f, hold));
            // 800ms hold then 1.2s at24 dB/s: 72 -28.8 =43.2 display units.
            Assert.All(peaks, peak => Assert.InRange(peak, 42.5f, 43.3f));
            control.DisplayFrozen = true;
            CombinedRandomizationTests.SetField(control, "_sevenLastTick", before);
            var frozenPeaks = (float[])peaks.Clone();
            Call(control, "UpdateSevenBandBallistics");
            Assert.Equal(frozenPeaks, peaks);
            control.DisplayFrozen = false;
            Assert.Equal(0L, Field<long>(control, "_sevenLastTick"));
        });
    }

    [Fact]
    public void SevenBandTimerSleepsOutsideItsActiveVisibleUnfrozenDataViewAndWakesForAnimation()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var host = new System.Windows.Forms.Form { ShowInTaskbar = false };
            using var control = new AdvancedVisualizationControl();
            host.Controls.Add(control);
            var timer = Field<System.Windows.Forms.Timer>(control, "_sevenTimer");
            Assert.False(timer.Enabled);
            var data = new AudioVisualizationData();
            data.Update(new float[960], 480, 2, 48000);
            data.SetSevenBandPower(Enumerable.Repeat(1d, 7).ToArray());
            control.SetAudioData(data);
            Assert.False(timer.Enabled); // another mode has no seven-band animation work
            control.Mode = AdvancedVisualizationMode.AdvancedSpectralBalance;
            Assert.False(timer.Enabled); // parent is still hidden
            host.Show();
            Assert.True(timer.Enabled);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 100)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            Assert.InRange(control.GetSevenBandDisplayDb(0), -0.5, 0);
            host.Hide();
            Assert.False(timer.Enabled);
            host.Show();
            Assert.True(timer.Enabled);
            host.WindowState = System.Windows.Forms.FormWindowState.Minimized;
            System.Windows.Forms.Application.DoEvents();
            Assert.False(timer.Enabled);
            host.WindowState = System.Windows.Forms.FormWindowState.Normal;
            System.Windows.Forms.Application.DoEvents();
            Assert.True(timer.Enabled);
            control.DisplayFrozen = true;
            Assert.False(timer.Enabled);
            control.DisplayFrozen = false;
            Assert.True(timer.Enabled);
            control.SetAudioData(new AudioVisualizationData());
            Assert.False(timer.Enabled);
            control.SetAudioData(data);
            Assert.True(timer.Enabled);
            control.Mode = AdvancedVisualizationMode.RadialSpectrum;
            Assert.False(timer.Enabled);
            control.SetSpectrum(new byte[83], "Aurora", AdvancedVisualizationMode.AdvancedSpectralBalance);
            Assert.True(timer.Enabled);
            control.SetSpectrum(new byte[83], BarColorThemes.Studio, "Contour");
            Assert.False(timer.Enabled);
            control.SetAudioSamples(new float[960], 480, 2, 48000);
            control.Mode = AdvancedVisualizationMode.AdvancedSpectralBalance;
            Assert.False(timer.Enabled); // PCM without seven-band analysis cannot wake it
            using var secondHost = new System.Windows.Forms.Form { ShowInTaskbar = false };
            secondHost.Show();
            secondHost.Controls.Add(control);
            control.SetAudioData(data);
            Assert.Same(secondHost, Field<System.Windows.Forms.Form>(control, "_sevenHost"));
            Assert.True(timer.Enabled);
            host.Hide();
            Assert.True(timer.Enabled); // the previous host no longer owns the animation
            secondHost.Hide();
            Assert.False(timer.Enabled);
            secondHost.Show();
            Assert.True(timer.Enabled);
            control.Dispose();
            Assert.Null(Field<System.Windows.Forms.Form>(control, "_sevenHost"));
            Assert.False(timer.Enabled);
        });
    }

    private static (object State, object Stream, SampleHistory History, BandSpectrumAnalyzer Engine) CreateCapture(Analyzer analyzer, int sampleRate, int channels)
    {
        var stateType = typeof(Analyzer).GetNestedType("AnalysisState", BindingFlags.NonPublic);
        var state = stateType.GetMethod("Create").Invoke(null, new object[] { sampleRate, channels });
        var streamType = typeof(Analyzer).GetNestedType("CaptureStream", BindingFlags.NonPublic);
        var stream = Activator.CreateInstance(streamType, new[] { state });
        CombinedRandomizationTests.SetField(analyzer, "_stream", stream);
        return (state, stream, (SampleHistory)stateType.GetProperty("History").GetValue(state),
            (BandSpectrumAnalyzer)stateType.GetProperty("Engine").GetValue(state));
    }

    private static AudioVisualizationData PublishSilence(Analyzer analyzer, object state, object stream, bool validCapture)
    {
        AudioVisualizationData snapshot = null;
        OnChangeHandler handler = (_, frame) => { snapshot = new AudioVisualizationData(); snapshot.CopyFrom(frame.Audio); };
        Analyzer.OnChange += handler;
        try { typeof(Analyzer).GetMethod("PublishSilence", PrivateInstance).Invoke(analyzer, new[] { state, validCapture, stream }); }
        finally { Analyzer.OnChange -= handler; }
        Assert.NotNull(snapshot);
        return snapshot;
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void Call(object target, string method) => target.GetType().GetMethod(method, PrivateInstance).Invoke(target, null);
}
