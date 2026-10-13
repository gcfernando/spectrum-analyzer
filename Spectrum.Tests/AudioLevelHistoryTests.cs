using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

public class AudioLevelHistoryTests
{
    private readonly ITestOutputHelper _output;
    public AudioLevelHistoryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ObservationsHaveRealIntervalsAndCorrectSineAndDcCrest()
    {
        var data = new AudioVisualizationData();
        var epoch = new object();
        var samples = Signals.StereoSine(4800, 48000, 1000, 0.5, 0.25);
        data.UpdateObservedWindow(samples, 4800, 2, 48000, 4800, 0, 0, epoch);
        var first = data.ObservationAt(0);
        Assert.Equal(0, first.StartFrame);
        Assert.Equal(4800, first.EndFrame);
        Assert.Equal(3.01029996, first.CrestDb(0), 5);
        Assert.Equal(3.01029996, first.CrestDb(1), 5);
        data.UpdateObservedWindow(new[] { 0.5f, -0.5f }, 2, 1, 48000, 2, 0, 0, epoch);
        Assert.Equal(1, data.ObservationCount); // A channel-format change starts a new history.
        Assert.Equal(0, data.ObservationAt(0).CrestDb(0));
        Assert.True(double.IsNaN(data.ObservationAt(0).CrestDb(1)));
    }

    [Fact]
    public void SilenceMissingAudioAndUnobservedRangesAreDifferentStates()
    {
        var data = new AudioVisualizationData();
        var epoch = new object();
        data.UpdateObservedWindow(new float[4], 4, 1, 48000, 4, 0, 0, epoch);
        Assert.True(data.ObservationAt(0).Available);
        Assert.True(double.IsNaN(data.ObservationAt(0).CrestDb(0)));
        data.SetUnavailableWindow(4, 1, 48000, epoch);
        data.SetUnavailableWindow(4, 1, 48000, epoch);
        Assert.Equal(2, data.ObservationCount);
        Assert.False(data.ObservationAt(1).Available);
        data.UpdateObservedWindow(new[] { 1f, 0f, 0f, 0f }, 4, 1, 48000, 20, 4, 10, epoch);
        var resumed = data.ObservationAt(2);
        Assert.Equal(16, resumed.StartFrame);
        Assert.True(resumed.Discontinuity);
        Assert.Equal(6.02059991, resumed.CrestDb(0), 5);
        data.UpdateObservedWindow(new float[4], 4, 1, 48000, 24, 20, 10, epoch);
        Assert.False(data.ObservationAt(3).Discontinuity);
    }

    [Fact]
    public void ANewStreamEpochResetsHistoryEvenWithTheSameFormatAndIncreasingCursor()
    {
        var data = new AudioVisualizationData();
        var samples = new[] { 0.25f, 0.5f };
        data.UpdateObservedWindow(samples, 2, 1, 48000, 100, 98, 0, new object());
        var restartEpoch = new object();
        data.UpdateObservedWindow(samples, 2, 1, 48000, 102, 100, 100, restartEpoch);
        Assert.Equal(1, data.ObservationCount);
        Assert.Same(restartEpoch, data.SourceEpoch);
        Assert.Equal(100, data.ObservationAt(0).StartFrame);
        Assert.False(data.ObservationAt(0).Discontinuity);
    }

    [Fact]
    public void QuietPcmRemainsMeasuredWhenChannelFftWasNotPerformed()
    {
        var data = new AudioVisualizationData();
        data.Update(new[] { 0.00001f, -0.00001f }, 2, 1, 48000);
        data.SetChannelSpectrum(null, null);
        Assert.True(data.HasSamples);
        Assert.True(data.ObservationAt(0).Available);
        Assert.True(data.LeftPeak > 0);
        Assert.False(data.HasChannelSpectrum);
    }

    [Fact]
    public void RingIsBoundedOrderedCopiedAndResetsOnEpochRateOrCursorChange()
    {
        var source = new AudioVisualizationData();
        var copy = new AudioVisualizationData();
        var epoch = new object();
        var sample = new[] { 0.25f };
        for (var i = 0; i < 1100; i++) source.UpdateObservedWindow(sample, 1, 1, 48000, i + 1, i, 0, epoch);
        Assert.Equal(1024, source.ObservationCount);
        Assert.Equal(76, source.ObservationAt(0).StartFrame);
        Assert.Equal(1100, source.ObservationAt(1023).EndFrame);
        copy.CopyFrom(source);
        source.Clear();
        Assert.Equal(1024, copy.ObservationCount);
        Assert.Equal(0.25, copy.ObservationAt(1023).LeftPeak);
        copy.UpdateObservedWindow(sample, 1, 1, 48000, 1, 0, 0, new object());
        Assert.Equal(1, copy.ObservationCount);
        copy.UpdateObservedWindow(sample, 1, 1, 24000, 2, 1, 0, epoch);
        Assert.Equal(1, copy.ObservationCount);
        copy.UpdateObservedWindow(sample, 1, 1, 24000, 1, 0, 0, epoch);
        Assert.Equal(1, copy.ObservationCount);
        copy.CopyFrom(null);
        Assert.Equal(0, copy.ObservationCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => copy.ObservationAt(0));
    }

    [Theory]
    [InlineData(0.00001f)]
    [InlineData(2f)]
    public void QuietAndOverRangeValuesRetainActualSamplePeak(float amplitude)
    {
        var data = new AudioVisualizationData();
        data.Update(new[] { amplitude, 0f, 0f, 0f }, 4, 1, 48000);
        Assert.Equal((double)amplitude, data.ObservationAt(0).LeftPeak);
        Assert.Equal(6.02059991, data.ObservationAt(0).CrestDb(0), 5);
    }

    [Fact]
    public void ChannelBandsHaveOwnedStorageAndMonoDoesNotInventRightData()
    {
        var data = new AudioVisualizationData();
        var copy = new AudioVisualizationData();
        data.Update(new[] { 0.5f }, 1, 1, 48000);
        var left = Enumerable.Repeat(0.25, 83).ToArray();
        var right = Enumerable.Repeat(1d, 83).ToArray();
        data.SetChannelSpectrum(left, right);
        copy.CopyFrom(data);
        Array.Clear(left, 0, left.Length);
        data.Clear();
        Assert.True(copy.HasChannelSpectrum);
        Assert.All(copy.LeftSpectrum, level => Assert.InRange(level, (byte)233, (byte)234));
        Assert.All(copy.RightSpectrum, level => Assert.Equal((byte)0, level));
        Assert.False(copy.HasStereo);
        Assert.Throws<ArgumentException>(() => copy.SetChannelSpectrum(new double[82], right));
        copy.SetChannelSpectrum(null, null);
        Assert.False(copy.HasChannelSpectrum); // Skipping analysis is not measured spectral silence.
    }

    [Fact]
    public void CoalescedFormUpdatesPreserveAnInterveningTransientAndDoNotDuplicateHistory()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var producer = new AudioVisualizationData();
            var changed = typeof(FormAudioSpectrum).GetMethod("Spectrum_Change", BindingFlags.Instance | BindingFlags.NonPublic);
            var apply = typeof(FormAudioSpectrum).GetMethod("ApplySpectrumToUI", BindingFlags.Instance | BindingFlags.NonPublic);
            CombinedRandomizationTests.SetField(form, "_updatePending", true);
            foreach (var value in new[] { 0.1f, 1f, 0.2f })
            {
                producer.Update(new[] { value }, 1, 1, 48000);
                changed.Invoke(form, new object[] { null, new OnChangeEventArgs(new byte[83], producer) });
            }
            producer.Clear();
            apply.Invoke(form, null);
            var copied = CombinedRandomizationTests.GetField<AudioVisualizationData>(form, "_audioApplyBuffer");
            Assert.Equal(3, copied.ObservationCount);
            Assert.Equal(1, copied.ObservationAt(1).LeftPeak);
            apply.Invoke(form, null);
            Assert.Equal(3, copied.ObservationCount);
        });
    }

    [Fact]
    public void FullHistoryCopiesAndMeasurementUpdatesReuseBuffersWithinTheBudget()
    {
        var data = new AudioVisualizationData();
        var copy = new AudioVisualizationData();
        var samples = Signals.StereoSine(1200, 48000, 1000, 0.5, 0.25);
        for (var i = 0; i < 1100; i++) data.Update(samples, 1200, 2, 48000);
        for (var i = 0; i < 20; i++) copy.CopyFrom(data);
        var watch = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (var i = 0; i < 600; i++) { data.Update(samples, 1200, 2, 48000); copy.CopyFrom(data); }
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"PCM update + full 1024-observation copy: {watch.Elapsed.TotalMilliseconds / 600:F4} ms/frame; {allocated} bytes over 600 frames");
        Assert.Equal(0, allocated);
        Assert.True(watch.Elapsed.TotalMilliseconds / 600 < 2);
    }
}
