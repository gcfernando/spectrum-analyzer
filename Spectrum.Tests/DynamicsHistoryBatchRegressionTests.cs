// Developer :: > Gehan Fernando
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using Spectrum;
using Spectrum.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

// Batched dynamics history should give the same answers as processing frame by frame.
public class DynamicsHistoryBatchRegressionTests
{
    private readonly ITestOutputHelper _output;
    public DynamicsHistoryBatchRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void BatchedPathsRetainExactSampleTimeStepsAndSeparateMissingIntervals()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var data = new AudioVisualizationData();
            var epoch = new object();
            data.UpdateObservedWindow(Enumerable.Repeat(0.5f, 2000).ToArray(), 2000, 1, 1000, 2000, 0, 0, epoch);
            data.SetUnavailableWindow(5000, 1, 1000, epoch);
            data.UpdateObservedWindow(Enumerable.Repeat(0.25f, 3000).ToArray(), 3000, 1, 1000, 8000, 5000, 5000, epoch);
            data.UpdateObservedWindow(Enumerable.Repeat(0.125f, 4000).ToArray(), 4000, 1, 1000, 12000, 8000, 5000, epoch);
            control.SetAudioData(data);
            Builder(control)(0, 0, 200, 0, 100, 0);
            var first = Y(0.5);
            var second = Y(0.25);
            var third = Y(0.125);
            var expected = new[] { new PointF(0, first), new PointF(20, first), new PointF(50, second),
                new PointF(80, second), new PointF(80, third), new PointF(120, third) };
            foreach (var name in new[] { "_dynamicsRmsPath", "_dynamicsPeakPath" })
            {
                var path = Field<GraphicsPath>(control, name);
                AssertPoints(expected, path.PathPoints);
                Assert.Equal(new byte[] { 0, 1, 0, 1, 1, 1 }, path.PathTypes);
            }
            AssertPoints(new[] { new PointF(50, 0), new PointF(50, 100) },
                Field<GraphicsPath>(control, "_dynamicsBreakPath").PathPoints);
        });
    }

    [Fact]
    public void LeftEdgeClippingUsesTheSameTimestampScaleAndDoesNotMoveWindowBoundaries()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var data = new AudioVisualizationData();
            data.Update(Enumerable.Repeat(0.5f, 10000).ToArray(), 10000, 1, 1000);
            data.Update(Enumerable.Repeat(0.25f, 5000).ToArray(), 5000, 1, 1000);
            control.SetAudioData(data);
            Builder(control)(0, 0, 200, 0, 100, 5000);
            var path = Field<GraphicsPath>(control, "_dynamicsPeakPath");
            AssertPoints(new[] { new PointF(0, Y(0.5)), new PointF(50, Y(0.5)),
                new PointF(50, Y(0.25)), new PointF(100, Y(0.25)) }, path.PathPoints);
            Assert.Single(path.PathTypes, type => type == (byte)PathPointType.Start);
        });
    }

    [Fact]
    public void RightChannelRequiresActualStereoObservationsAndKeepsMeasuredSilence()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            control.SetAudioSamples(new[] { 0.5f, -0.5f }, 2, 1, 1000);
            var build = Builder(control);
            build(1, 0, 200, 0, 100, 0);
            Assert.Equal(0, Field<GraphicsPath>(control, "_dynamicsRmsPath").PointCount);
            Assert.Equal(0, Field<GraphicsPath>(control, "_dynamicsPeakPath").PointCount);
            control.SetAudioSamples(new[] { 0.5f, 0f, -0.5f, 0f }, 2, 2, 1000);
            build(1, 0, 200, 0, 100, 0);
            Assert.Equal(2, Field<GraphicsPath>(control, "_dynamicsPeakPath").PointCount);
            Assert.All(Field<GraphicsPath>(control, "_dynamicsPeakPath").PathPoints, point => Assert.Equal(100f, point.Y));
            build(0, 0, 200, 0, 100, 0);
            Assert.All(Field<GraphicsPath>(control, "_dynamicsPeakPath").PathPoints, point => Assert.Equal(Y(0.5), point.Y, 4));
            Assert.Equal(DashStyle.Dash, Field<Pen>(control, "_rmsHistoryPen").DashStyle);
            Assert.Equal(DashStyle.Solid, Field<Pen>(control, "_peakHistoryPen").DashStyle);
        });
    }

    [Fact]
    public void RmsAndPeakGeometryUseTheirOwnMeasuredAmplitudes()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            control.SetAudioSamples(new[] { 1f, 0f, -1f, 0f }, 4, 1, 1000);
            Builder(control)(0, 0, 200, 0, 100, 0);
            var rms = Field<GraphicsPath>(control, "_dynamicsRmsPath").PathPoints;
            var peak = Field<GraphicsPath>(control, "_dynamicsPeakPath").PathPoints;
            Assert.All(rms, point => Assert.Equal(Y(Math.Sqrt(0.5)), point.Y, 4));
            Assert.All(peak, point => Assert.Equal(0f, point.Y));
            Assert.All(rms, point => Assert.True(point.Y > 0));
        });
    }

    [Fact]
    public void UnavailableAndNewStreamHistoryClearTheReusedPathsInsteadOfRevivingOldTraces()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var data = new AudioVisualizationData();
            data.Update(new[] { 0.5f }, 1, 1, 1000);
            control.SetAudioData(data);
            var build = Builder(control);
            build(0, 0, 200, 0, 100, 0);
            Assert.True(Field<GraphicsPath>(control, "_dynamicsPeakPath").PointCount > 0);
            data.SetUnavailableWindow(5000, 1, 1000, new object());
            control.SetAudioData(data);
            build(0, 0, 200, 0, 100, 0);
            Assert.Equal(0, Field<GraphicsPath>(control, "_dynamicsPeakPath").PointCount);
            Assert.Equal(0, Field<GraphicsPath>(control, "_dynamicsRmsPath").PointCount);
            Assert.Equal(0, Field<GraphicsPath>(control, "_dynamicsBreakPath").PointCount);
        });
    }

    [Fact]
    public void EveryVisibleObservationRemainsInTheBatchedGeometryWithoutSamplingOrFrameAllocations()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var data = NewAnalyticalModeTests.CreateMeasuredData();
            control.SetAudioData(data);
            var build = Builder(control);
            var oldest = data.EndFrame - 20L * data.SampleRate;
            for (var i = 0; i < 20; i++) build(0, 48, 1910, 40, 1000, oldest);
            var rms = Field<GraphicsPath>(control, "_dynamicsRmsPath");
            var peak = Field<GraphicsPath>(control, "_dynamicsPeakPath");
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) build(0, 48, 1910, 40, 1000, oldest);
            var allocation = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocation);
            Assert.Same(rms, Field<GraphicsPath>(control, "_dynamicsRmsPath"));
            Assert.Same(peak, Field<GraphicsPath>(control, "_dynamicsPeakPath"));
            var points = peak.PathPoints;
            var expected = 0;
            for (var index = 0; index < data.ObservationCount; index++)
            {
                var entry = data.ObservationAt(index);
                if (!entry.Available || entry.EndFrame <= oldest) continue;
                var x1 = (float)Math.Max(48, 48 + 1862 * (entry.StartFrame - oldest) / (20d * data.SampleRate));
                var x2 = (float)Math.Min(1910, 48 + 1862 * (entry.EndFrame - oldest) / (20d * data.SampleRate));
                var y = 1000 - 960 * (float)LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(entry.LeftPeak));
                Assert.Contains(points, point => Math.Abs(point.X - x1) < 0.001f && Math.Abs(point.Y - y) < 0.001f);
                Assert.Contains(points, point => Math.Abs(point.X - x2) < 0.001f && Math.Abs(point.Y - y) < 0.001f);
                expected++;
            }
            Assert.InRange(points.Length, expected + 1, 4 * AudioVisualizationData.MaximumObservations);
            _output.WriteLine($"{expected} visible observed intervals retained; {points.Length} peak path points; " +
                $"{allocation} managed bytes over 100 path rebuilds. No time/pixel decimation.");
        });
    }

    [Fact]
    public void BatchedNativePathsAreDisposedWithTheirControl()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var control = new AdvancedVisualizationControl();
            var paths = new[] { Field<GraphicsPath>(control, "_dynamicsRmsPath"),
                Field<GraphicsPath>(control, "_dynamicsPeakPath"), Field<GraphicsPath>(control, "_dynamicsBreakPath") };
            control.Dispose();
            foreach (var path in paths) Assert.Throws<ArgumentException>(() => path.GetBounds());
        });
    }

    private delegate void BuildPaths(int channel, float left, float right, float top, float bottom, long oldestFrame);
    private static BuildPaths Builder(AdvancedVisualizationControl control) =>
        (BuildPaths)Delegate.CreateDelegate(typeof(BuildPaths), control,
            typeof(AdvancedVisualizationControl).GetMethod("BuildDynamicsPaths", BindingFlags.Instance | BindingFlags.NonPublic));
    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static float Y(double amplitude) => 100 - 100 * (float)LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(amplitude));
    private static void AssertPoints(PointF[] expected, PointF[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].X, actual[index].X, 3);
            Assert.Equal(expected[index].Y, actual[index].Y, 3);
        }
    }
}
