using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class AnalysisWorkspaceTests
{
    internal static AnalysisFrame Tone(double db = -20, long end = 16384, int frames = 16384, object epoch = null, bool opposite = false, int channels = 2, long firstValidFrame = 0)
    {
        const int rate = 48000;
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, rate);
        var samples = new float[frames * channels];
        var amplitude = Math.Pow(10, db / 20);
        for (var f = 0; f < frames; f++)
        {
            var time = end - frames + f;
            var sample = time < 0 ? 0 : (float)(amplitude * Math.Sin(2 * Math.PI * plan[80].CenterHz * time / rate));
            samples[f * channels] = sample;
            if (channels > 1) samples[f * channels + 1] = opposite ? -sample : sample;
        }
        var power = new double[83]; var left = new double[83]; var right = new double[83];
        if (frames >= 16384) new BandSpectrumAnalyzer(plan, channels).Analyze(samples, frames, power, left, right);
        else { power[80] = Math.Pow(10, db / 10); left[80] = power[80]; right[80] = channels > 1 ? power[80] : 0; }
        return new AnalysisFrame(plan, channels, frames, end, samples, power, left, right, epoch, firstValidFrame);
    }
    private static AnalysisWorkspace Workspace()
    {
        var workspace = new AnalysisWorkspace();
        workspace.Settings.AveragingMilliseconds = 0; workspace.Settings.SmoothingBands = 0;
        workspace.UpdateSettings(); return workspace;
    }

    [Theory]
    [InlineData(-60)] [InlineData(-72)] [InlineData(-90)] [InlineData(-120)]
    public void DisplayRangePreservesSubfloorMeasuredPower(double floor)
    {
        var workspace = Workspace(); var frame = Tone(-100);
        workspace.Accept(frame); workspace.Settings.FloorDb = floor; workspace.UpdateSettings();
        Assert.InRange(10 * Math.Log10(workspace.Latest.Power[80]), -100.01, -99.99);
        Assert.Equal(frame.Power[80], workspace.DisplayPower[80]);
        Assert.Equal(0, LevelScale.ToDisplayByte(LevelScale.PowerToDb(frame.Power[80])));
    }
    [Fact]
    public void OverlappingFftSnapshotsDoNotDoubleCountPcmOrLoudness()
    {
        var workspace = Workspace(); var epoch = new object();
        workspace.Accept(Tone(end: 16384, epoch: epoch));
        workspace.Accept(Tone(end: 17584, epoch: epoch));
        workspace.Accept(Tone(end: 17584, epoch: epoch));
        Assert.Equal(17584, workspace.Measurements.TotalFrames);
        Assert.Equal(17584 / 48000d, workspace.CurrentSeconds, 8);
    }
    [Fact]
    public void SpectrogramRowsFollowFixedSampleHopsAcrossIrregularUpdates()
    {
        var regular = Workspace(); var irregular = Workspace(); var epoch = new object();
        foreach (var end in new long[] { 2400, 4800, 7200, 9600, 12000, 14400 }) regular.Accept(Tone(end: end, epoch: epoch));
        foreach (var end in new long[] { 1234, 6777, 8000, 14400 }) irregular.Accept(Tone(end: end, epoch: epoch));
        Assert.Equal(regular.History.Select(r => r.Seconds), irregular.History.Select(r => r.Seconds));
        Assert.True(regular.History.Count > 8);
        for (var i = 1; i < regular.History.Count; i++) Assert.Equal(1024 / 48000d, regular.History[i].Seconds - regular.History[i - 1].Seconds, 9);
        for (var i = 0; i < regular.History.Count; i++) Assert.InRange(Math.Abs(regular.History[i].Power[80] - irregular.History[i].Power[80]), 0, 1e-12);
    }
    [Fact]
    public void CaptureGapsStayExplicitAndRestartContiguousMeters()
    {
        var workspace = Workspace(); var epoch = new object();
        workspace.Accept(Tone(end: 16384, epoch: epoch)); workspace.SetUnavailable("Device lost");
        Assert.True(workspace.IsUnavailable);
        workspace.Accept(Tone(end: 18000, epoch: epoch));
        Assert.False(workspace.IsUnavailable); Assert.Contains(workspace.History, r => r.Gap);
        Assert.Equal(1616, workspace.Measurements.TotalFrames);
    }
    [Fact]
    public void ReconnectedStreamDoesNotCountPaddedOrPreviousStreamSamplesAsNewAudio()
    {
        var workspace = Workspace(); workspace.Accept(Tone(end: 48000, epoch: new object()));
        workspace.Accept(Tone(end: 48480, epoch: new object(), firstValidFrame: 48000));
        Assert.Equal(480, workspace.Measurements.TotalFrames);
        Assert.Equal(1.01, workspace.CurrentSeconds, 8);
        Assert.DoesNotContain(workspace.History, r => !r.Gap && r.Seconds > 1);
    }
    [Fact]
    public void KnownCaptureGapRemainsExplicitWhenTheStatusNotificationWasCoalescedAway()
    {
        var workspace = Workspace(); var epoch = new object();
        workspace.Accept(Tone(end: 16384, epoch: epoch));
        workspace.Accept(Tone(end: 20000, epoch: epoch, firstValidFrame: 18800));
        Assert.Equal(1200, workspace.Measurements.TotalFrames);
        Assert.Contains(workspace.History, row => row.Gap);
        Assert.DoesNotContain(workspace.History, row => !row.Gap && row.Seconds > 16384 / 48000d);
        Assert.Equal(20000 / 48000d, workspace.CurrentSeconds, 8);
    }
    [Fact]
    public void ClearingLiveAnalysisDoesNotReplayTheOverlappingWindowIntoNewMeters()
    {
        var workspace = Workspace(); var epoch = new object();
        workspace.Accept(Tone(end: 48000, epoch: epoch)); workspace.Reset();
        workspace.Accept(Tone(end: 48000, epoch: epoch)); Assert.Null(workspace.Measurements);
        workspace.Accept(Tone(end: 49200, epoch: epoch));
        Assert.Equal(1200, workspace.Measurements.TotalFrames);
        Assert.Equal(.025, workspace.CurrentSeconds, 8);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StereoPhaseShowsMatchingChannelsVerticallyAndOppositeChannelsHorizontally(bool opposite)
    {
        var workspace = Workspace(); workspace.Accept(Tone(opposite: opposite));
        var occupied = workspace.PhaseDensity.Select((count, cell) => new { count, cell }).Where(p => p.count > 0).ToArray();
        Assert.Equal(16384, workspace.PhaseDensity.Sum());
        Assert.True(occupied.Length > 40);
        Assert.All(occupied, p => Assert.InRange(opposite ? p.cell / 64 : p.cell % 64, 31, 32));
    }
    [Fact]
    public void PhaseUsesAllSamplesSoHighFrequencyQuadratureCannotAliasIntoOnePoint()
    {
        var workspace = Workspace(); var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        var samples = new float[16384 * 2];
        for (var i = 0; i < 16384; i++)
        { samples[2 * i] = (float)Math.Sin(Math.PI * i / 2); samples[2 * i + 1] = (float)Math.Cos(Math.PI * i / 2); }
        workspace.Accept(new AnalysisFrame(plan, 2, 16384, 16384, samples, new double[83], new double[83], new double[83]));
        Assert.Equal(16384, workspace.PhaseDensity.Sum());
        foreach (var top in new[] { false, true }) foreach (var left in new[] { false, true })
            Assert.Contains(workspace.PhaseDensity.Select((count, cell) => new { count, cell }),
                p => p.count > 0 && (p.cell / 64 < 32) == top && (p.cell % 64 < 32) == left);
        Assert.InRange(workspace.PhaseExtent, .499999, .500001);
    }
    [Fact]
    public void SavedPhaseRetainsItsExtentWindowAndViewWhileMonoHasNoInventedStereo()
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Accept(Tone(opposite: true)); workspace.PreferredView = "Stereo phase";
            workspace.SaveSession(path); var loaded = Workspace(); loaded.LoadSession(path);
            Assert.Equal(workspace.PhaseDensity, loaded.PhaseDensity); Assert.Equal(workspace.PhaseExtent, loaded.PhaseExtent);
            Assert.Equal(workspace.PhaseWindowSeconds, loaded.PhaseWindowSeconds); Assert.Equal("Stereo phase", loaded.PreferredView);
            workspace.Accept(Tone(channels: 1, epoch: new object())); Assert.Empty(workspace.PhaseDensity);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void PowerAveragingInterpolatesInPowerRatherThanDb()
    {
        var workspace = Workspace(); var epoch = new object();
        workspace.Accept(Tone(-20, end: 16384, epoch: epoch));
        workspace.Settings.AveragingMilliseconds = 1000; workspace.UpdateSettings();
        var before = workspace.DisplayPower[80]; var next = Tone(-40, end: 21184, epoch: epoch);
        workspace.Accept(next);
        var expected = before + (1 - Math.Exp(-.1)) * (next.Power[80] - before);
        Assert.InRange(Math.Abs(expected - workspace.DisplayPower[80]), 0, 1e-12);
    }
    [Fact]
    public void ReferenceIsStableAndReportsExpectedSixDbDifference()
    {
        var workspace = Workspace(); var epoch = new object();
        workspace.Accept(Tone(-20, epoch: epoch)); workspace.CaptureReference(); var reference = workspace.ReferencePower[80];
        workspace.Accept(Tone(-26, end: 17584, epoch: epoch));
        Assert.Equal(reference, workspace.ReferencePower[80]);
        Assert.InRange(10 * Math.Log10(workspace.DisplayPower[80] / reference), -6.001, -5.999);
        workspace.ClearReference(); Assert.Null(workspace.ReferencePower);
    }
    [Fact]
    public void ReferenceSurvivesOpeningASecondRecordingButAnImportedEmptyWorkspaceClearsIt()
    {
        var first = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"); var second = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"); var settings = Path.GetTempFileName();
        try
        {
            AnalysisUpgradeVerification.WriteWave(first); AnalysisUpgradeVerification.WriteWave(second, -6);
            var workspace = Workspace(); workspace.AnalyzeFile(first, 0, 6); workspace.CaptureReference();
            var reference = workspace.ReferencePower[80]; workspace.AnalyzeFile(second, 0, 6);
            Assert.Equal(reference, workspace.ReferencePower[80]);
            Assert.InRange(10 * Math.Log10(workspace.DisplayPower[80] / reference), -6.01, -5.99);
            workspace.AnalyzeFile(second, 1, 3); workspace.SaveSession(settings);
            var capture = XElement.Load(settings).Element("capture");
            Assert.Equal(Path.GetFileName(second), (string)capture.Attribute("sourceName"));
            Assert.Equal("1", (string)capture.Attribute("selectionStartSeconds"));
            var loaded = Workspace(); loaded.LoadSession(settings); loaded.SaveSession(settings);
            Assert.Equal("1", (string)XElement.Load(settings).Element("capture").Attribute("selectionStartSeconds"));
            Workspace().SaveWorkspace(settings); workspace.LoadWorkspace(settings); Assert.Null(workspace.ReferencePower);
        }
        finally { File.Delete(first); File.Delete(second); File.Delete(settings); }
    }
    [Theory]
    [InlineData("Fast")] [InlineData("Balanced")] [InlineData("Detailed")]
    public void ResponseSelectionsRetainTheMeasuredTone(string response)
    {
        var workspace = Workspace(); workspace.Settings.Response = response; workspace.UpdateSettings(); workspace.Accept(Tone());
        Assert.Equal(80, Array.IndexOf(workspace.DisplayPower, workspace.DisplayPower.Max()));
        Assert.InRange(10 * Math.Log10(workspace.DisplayPower[80]), -20.01, -19.99);
    }
    [Theory]
    [InlineData("Left", false, true)] [InlineData("Right", false, true)]
    [InlineData("Mid", true, false)] [InlineData("Side", true, true)]
    public void SelectedChannelsUseTheirOwnSignal(string channel, bool opposite, bool hasEnergy)
    {
        var workspace = Workspace(); workspace.Settings.Channel = channel; workspace.UpdateSettings(); workspace.Accept(Tone(opposite: opposite));
        Assert.Equal(hasEnergy, workspace.DisplayPower.Max() > 1e-12);
    }
    [Fact]
    public void SavedSessionRoundTripsOriginalSubfloorPowersReferenceHistoryAndMetadata()
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Settings.FloorDb = -120; workspace.UpdateSettings();
            workspace.Accept(Tone(-100)); workspace.CaptureReference(); workspace.WorkspaceName = "Quiet tail";
            workspace.PreferredView = "Spectrogram"; workspace.SaveSession(path);
            var loaded = Workspace(); loaded.LoadSession(path);
            Assert.True(loaded.IsOffline); Assert.Equal(workspace.Latest.Power, loaded.Latest.Power);
            Assert.Equal(workspace.ReferencePower, loaded.ReferencePower); Assert.Equal(workspace.History.Count, loaded.History.Count);
            Assert.Equal(workspace.Latest.CapturedUtc, loaded.Latest.CapturedUtc);
            Assert.Equal("Quiet tail", loaded.WorkspaceName); Assert.Equal("Spectrogram", loaded.PreferredView);
            Assert.Equal(workspace.Measurements.TotalFrames, loaded.Measurements.TotalFrames);
            loaded.Accept(Tone()); Assert.Equal(workspace.Latest.Power, loaded.Latest.Power);
            loaded.ResumeLive(); loaded.Accept(Tone()); Assert.False(loaded.IsOffline);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void CustomWorkspaceRestoresNameLayoutSettingsAndCompatibleReference()
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Accept(Tone()); workspace.CaptureReference();
            workspace.WorkspaceName = "Mastering"; workspace.Settings.FloorDb = -120; workspace.Settings.MinHz = 1000;
            workspace.Settings.MaxHz = 19000; workspace.Settings.AveragingMilliseconds = 888; workspace.PreferredView = "Spectrogram";
            workspace.WindowWidth = 1300; workspace.WindowHeight = 900; workspace.UpdateSettings(); workspace.SaveWorkspace(path);
            var restored = Workspace(); restored.LoadWorkspace(path); restored.Accept(Tone());
            Assert.Equal("Mastering", restored.WorkspaceName); Assert.Equal("Spectrogram", restored.PreferredView);
            Assert.Equal(1300, restored.WindowWidth); Assert.Equal(900, restored.WindowHeight);
            Assert.Equal(-120, restored.Settings.FloorDb); Assert.Equal(888, restored.Settings.AveragingMilliseconds);
            Assert.NotNull(restored.ReferencePower); Assert.Equal(workspace.ReferencePower, restored.ReferencePower);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void CsvExportsRawValuesBelowTheDisplayFloorAndAnalysisSettings()
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Accept(Tone(-100)); workspace.ExportCsv(path);
            var lines = File.ReadAllLines(path); Assert.Equal(84, lines.Length);
            Assert.Contains("raw_dbfs", lines[0]); Assert.Contains("end_frame", lines[0]); Assert.Contains("averaging_ms", lines[0]);
            var tail = lines[81].Split(',');
            Assert.InRange(double.Parse(tail[12], System.Globalization.CultureInfo.InvariantCulture), -100.01, -99.99);
            Assert.True(double.Parse(tail[9], System.Globalization.CultureInfo.InvariantCulture) > 0);
            var headers = lines[0].Split(',');
            Assert.Equal("16384", tail[Array.IndexOf(headers, "measured_frames")]);
            Assert.Equal("", tail[Array.IndexOf(headers, "integrated_lufs")]);
            Assert.True(double.Parse(tail[Array.IndexOf(headers, "estimated_true_peak_dbtp")], System.Globalization.CultureInfo.InvariantCulture) < -90);
            Assert.Equal(headers.Length, tail.Length);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void InvalidWorkspaceAndExternalEntityFilesCannotReplaceCurrentSettings()
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Settings.FloorDb = -90; workspace.UpdateSettings();
            File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY test SYSTEM 'file:///C:/Windows/win.ini'>]><spectrum-workspace version='1'><settings name='&test;'/></spectrum-workspace>");
            Assert.ThrowsAny<Exception>(() => workspace.LoadWorkspace(path)); Assert.Equal(-90, workspace.Settings.FloorDb);
            File.WriteAllText(path, "<spectrum-workspace version='1'><settings floor='NaN'/></spectrum-workspace>");
            Assert.ThrowsAny<Exception>(() => workspace.LoadWorkspace(path)); Assert.Equal(-90, workspace.Settings.FloorDb);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("Mid")]
    [InlineData("Side")]
    public void SavedSelectedChannelRetainsRawAndProcessedMeasurementsWhenRangeChanges(string channel)
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Settings.Response = "Detailed"; workspace.Settings.Channel = channel;
            workspace.Settings.SmoothingBands = 2; workspace.UpdateSettings(); workspace.Accept(Tone(opposite: true));
            var raw = (double[])workspace.RawSelectedPower.Clone(); var display = (double[])workspace.DisplayPower.Clone();
            workspace.SaveSession(path); var restored = Workspace(); restored.LoadSession(path);
            restored.Settings.FloorDb = -120; restored.Settings.MinHz = 500; restored.UpdateSettings();
            Assert.Equal(raw, restored.RawSelectedPower); Assert.Equal(display, restored.DisplayPower);
            Assert.Equal(channel, restored.Settings.Channel); Assert.Equal("Detailed", restored.Settings.Response);
            if (channel == "Mid") Assert.Equal(0, restored.DisplayPower.Max());
            else Assert.True(restored.RawSelectedPower[80] > restored.DisplayPower[80] * 4);
            restored.Settings.Channel = "Combined";
            Assert.Throws<InvalidOperationException>(() => restored.UpdateSettings());
            Assert.Equal(channel, restored.Settings.Channel); Assert.Equal(raw, restored.RawSelectedPower);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void PostingCapturedFramesDoesNotWaitForAnAnalysisOrDiskIoLock()
    {
        var workspace = Workspace(); var frame = Tone();
        var sync = typeof(AnalysisWorkspace).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(workspace);
        Monitor.Enter(sync);
        try
        {
            using var posted = new ManualResetEventSlim();
            var capture = new Thread(() => { for (var i = 0; i < 100; i++) workspace.Post(frame); posted.Set(); }) { IsBackground = true };
            capture.Start();
            Assert.True(posted.Wait(1000), "Posting capture frames must not wait for workspace calculations or disk I/O.");
        }
        finally { Monitor.Exit(sync); }
        Assert.True(SpinWait.SpinUntil(() => workspace.Latest != null, 3000));
        Assert.Equal(frame.EndFrame, workspace.Latest.EndFrame);
    }
    [Theory]
    [InlineData(-1, 10)] [InlineData(0, 0)] [InlineData(0, 601)]
    public void OfflineSelectionRejectsInvalidRangesBeforeReading(double start, double duration)
    { Assert.Throws<ArgumentException>(() => Workspace().AnalyzeFile("unused.wav", start, duration)); }
}
