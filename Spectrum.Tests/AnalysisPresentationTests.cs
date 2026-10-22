using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Linq;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class AnalysisPresentationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static AnalysisWorkspace Workspace()
    {
        var workspace = new AnalysisWorkspace();
        workspace.Settings.AveragingMilliseconds = 0; workspace.Settings.SmoothingBands = 0;
        workspace.UpdateSettings(); return workspace;
    }
    private static void Inspect(AnalysisPlotControl plot, AnalysisWorkspace workspace, int band = 80, bool pin = false)
    {
        var x = 62 + (int)(Math.Log(workspace.Latest.Plan[band].CenterHz / workspace.Settings.MinHz) /
            Math.Log(workspace.Settings.MaxHz / workspace.Settings.MinHz) * (plot.Width - 82));
        typeof(AnalysisPlotControl).GetMethod(pin ? "OnMouseDown" : "OnMouseMove", Hidden).Invoke(plot,
            new object[] { new MouseEventArgs(pin ? MouseButtons.Left : MouseButtons.None, pin ? 1 : 0, x, 80, 0) });
    }
    private static AnalysisFrame ZeroPower(bool hasSignal, int channels = 2, bool opposite = false, long firstValid = 0)
    {
        var pcm = new float[32 * channels];
        for (var f = 0; f < 32; f++)
        {
            pcm[f * channels] = hasSignal ? .25f : 0;
            if (channels > 1) pcm[f * channels + 1] = opposite ? -pcm[f * channels] : pcm[f * channels];
        }
        if (firstValid > 0) Array.Clear(pcm, (int)firstValid * channels, (32 - (int)firstValid) * channels);
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        return new AnalysisFrame(plan, channels, 32, 32, pcm, new double[83], new double[83], new double[83], firstValidFrame: firstValid);
    }

    [Fact]
    public void StructuredInspectionKeepsRawLevelHeldCurvePeakAndReferenceDeltaDistinct()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Workspace(); var epoch = new object();
            workspace.Accept(AnalysisWorkspaceTests.Tone(-20, frames: 64, epoch: epoch)); workspace.CaptureReference();
            workspace.Accept(AnalysisWorkspaceTests.Tone(-26, end: 16448, frames: 64, epoch: epoch));
            using var plot = new AnalysisPlotControl(workspace) { Size = new Size(900, 400) }; Inspect(plot, workspace);
            var value = plot.Inspection;
            Assert.Equal(81, value.BandNumber); Assert.Equal(workspace.Latest.Plan[80].CenterHz, value.CenterHz);
            Assert.Equal(-26, value.LevelDb, 8); Assert.Equal(-20, value.PeakDb, 8); Assert.Equal(-6, value.DeltaDb, 8);
            Assert.Equal("Measured", value.Status); Assert.False(value.IsUnavailable);
            var reference = (double[])workspace.ReferencePower.Clone(); var live = (double[])workspace.DisplayPower.Clone();
            plot.ShowReference = false; Inspect(plot, workspace);
            Assert.Equal(-6, plot.Inspection.DeltaDb, 8); Assert.Equal(reference, workspace.ReferencePower); Assert.Equal(live, workspace.DisplayPower);
        });
    }

    [Theory]
    [InlineData(false, false, 0, true)]
    [InlineData(true, false, 0, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, false, 16, true)]
    public void DigitalSilenceRequiresTheValidSelectedPcmWindowToBeSilent(bool signal, bool opposite, long firstValid, bool silent)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Workspace(); workspace.Accept(ZeroPower(signal, opposite: opposite, firstValid: firstValid));
            using var plot = new AnalysisPlotControl(workspace) { Size = new Size(900, 400) }; Inspect(plot, workspace);
            Assert.Equal(silent, plot.Inspection.IsDigitalSilence);
            Assert.Equal(silent ? "Digital silence" : "Zero band power", plot.Inspection.Status);
            Assert.False(plot.Inspection.IsUnavailable); Assert.True(double.IsNegativeInfinity(plot.Inspection.LevelDb));
        });
    }

    [Fact]
    public void BelowFloorAndUnavailableChannelHaveDifferentStatesAndValues()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Workspace(); workspace.Accept(AnalysisWorkspaceTests.Tone(-100, frames: 64));
            using var plot = new AnalysisPlotControl(workspace) { Size = new Size(900, 400) }; Inspect(plot, workspace);
            Assert.True(plot.Inspection.IsBelowFloor); Assert.Equal(-100, plot.Inspection.LevelDb, 8);
            Assert.False(plot.Inspection.IsUnavailable); Assert.False(plot.Inspection.IsDigitalSilence);
            workspace.Settings.Channel = "Right"; workspace.UpdateSettings(); workspace.Accept(ZeroPower(false, channels: 1));
            Inspect(plot, workspace); Assert.True(plot.Inspection.IsUnavailable);
            Assert.True(double.IsNaN(plot.Inspection.LevelDb)); Assert.True(double.IsNaN(plot.Inspection.PeakDb));
            Assert.Equal("Channel unavailable", plot.Inspection.Status);
        });
    }

    [Theory]
    [InlineData("Mid", true, true)] [InlineData("Side", true, false)]
    [InlineData("Mid", false, false)] [InlineData("Side", false, true)]
    public void SilenceStateFollowsTheSelectedMidOrSideSignal(string channel, bool opposite, bool silent)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Workspace(); workspace.Settings.Channel = channel; workspace.UpdateSettings();
            workspace.Accept(ZeroPower(true, opposite: opposite));
            using var plot = new AnalysisPlotControl(workspace) { Size = new Size(900, 400) }; Inspect(plot, workspace);
            Assert.Equal(silent, plot.Inspection.IsDigitalSilence);
            Assert.False(plot.Inspection.IsUnavailable);
        });
    }

    [Fact]
    public void PinnedInspectionClearsWithEscapeAndUnavailableStateRecoversWhenAudioArrives()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Workspace(); using var plot = new AnalysisPlotControl(workspace) { Size = new Size(900, 400) };
            plot.RefreshInspection(); Assert.True(plot.Inspection.IsUnavailable);
            workspace.Accept(AnalysisWorkspaceTests.Tone(frames: 64)); plot.RefreshInspection();
            Assert.False(plot.Inspection.IsUnavailable); Assert.Equal(0, plot.Inspection.BandNumber);
            Inspect(plot, workspace, pin: true); Assert.True(plot.Inspection.IsPinned);
            typeof(AnalysisPlotControl).GetMethod("OnKeyDown", Hidden).Invoke(plot, new object[] { new KeyEventArgs(Keys.Escape) });
            Assert.False(plot.Inspection.IsPinned); Assert.Equal(0, plot.Inspection.BandNumber);
            Inspect(plot, workspace, pin: true); workspace.SetUnavailable("Device disconnected"); plot.RefreshInspection();
            Assert.True(plot.Inspection.IsUnavailable); Assert.True(double.IsNaN(plot.Inspection.LevelDb));
        });
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void HiddenReferenceMetadataSurvivesSessionAndWorkspaceRoundTrips(bool session)
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Accept(AnalysisWorkspaceTests.Tone(frames: 64)); workspace.CaptureReference();
            var captured = workspace.ReferenceCapturedUtc; var powers = (double[])workspace.ReferencePower.Clone();
            workspace.ReferenceVisible = false;
            if (session) workspace.SaveSession(path); else workspace.SaveWorkspace(path);
            var loaded = Workspace(); if (session) loaded.LoadSession(path); else loaded.LoadWorkspace(path);
            Assert.False(loaded.ReferenceVisible); Assert.Equal(powers, loaded.ReferencePower);
            Assert.Equal("Live capture", loaded.ReferenceName); Assert.Equal(captured, loaded.ReferenceCapturedUtc);
            loaded.ClearReference(); Assert.Equal("", loaded.ReferenceName); Assert.Null(loaded.ReferenceCapturedUtc);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void EarlierVersionOneFilesDefaultToVisibleReferenceAndUnknownTimestamp(bool session)
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.Accept(AnalysisWorkspaceTests.Tone(frames: 64)); workspace.CaptureReference();
            if (session) workspace.SaveSession(path); else workspace.SaveWorkspace(path);
            var xml = XElement.Load(path); xml.Element("settings").Attribute("referenceVisible").Remove();
            var reference = session ? xml.Element("capture").Element("reference") : xml.Element("reference");
            reference.Attribute("name").Remove(); reference.Attribute("capturedUtc").Remove(); xml.Save(path);
            var loaded = Workspace(); loaded.ReferenceVisible = false;
            if (session) loaded.LoadSession(path); else loaded.LoadWorkspace(path);
            Assert.True(loaded.ReferenceVisible); Assert.NotNull(loaded.ReferencePower);
            Assert.Equal("Saved reference", loaded.ReferenceName); Assert.Null(loaded.ReferenceCapturedUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SavedFileSessionKeepsItsOriginAndSelectionWithoutPretendingToRetainPcm()
    {
        var wave = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"); var session = Path.GetTempFileName();
        try
        {
            AnalysisUpgradeVerification.WriteWave(wave);
            var workspace = Workspace(); workspace.AnalyzeFile(wave, 1, 2); workspace.CaptureReference();
            Assert.Equal("Audio file", workspace.SourceKind); Assert.Equal(Path.GetFileName(wave), workspace.SourceName);
            Assert.False(workspace.IsSavedSession); Assert.Equal(1, workspace.SelectionStartSeconds);
            Assert.Equal(Path.GetFileName(wave), workspace.ReferenceName);
            workspace.SaveSession(session); var loaded = Workspace(); loaded.LoadSession(session);
            Assert.True(loaded.IsSavedSession); Assert.Equal("Audio file", loaded.SourceKind);
            Assert.Equal(workspace.SourceName, loaded.SourceName); Assert.Equal(1, loaded.SelectionStartSeconds);
            Assert.Equal(workspace.Latest.Power, loaded.Latest.Power); Assert.Equal(0, loaded.Latest.FrameCount);
        }
        finally { File.Delete(wave); File.Delete(session); }
    }

    [Theory]
    [InlineData(0, 20000)] [InlineData(20, 384001)] [InlineData(20000, 20)] [InlineData(double.NaN, 20000)]
    public void InvalidFrequencyPresetRejectsBeforeChangingTheCurrentRange(double minimum, double maximum)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Workspace(); workspace.Accept(AnalysisWorkspaceTests.Tone(frames: 64));
            using var plot = new AnalysisPlotControl(workspace);
            var power = (double[])workspace.DisplayPower.Clone();
            Assert.Throws<ArgumentException>(() => plot.SetFrequencyRange(minimum, maximum));
            Assert.Equal(20, workspace.Settings.MinHz); Assert.Equal(20000, workspace.Settings.MaxHz);
            Assert.Equal(power, workspace.DisplayPower);
        });
    }

    [Fact]
    public void InvalidReferenceVisibilityRejectsBeforeReplacingCurrentSettings()
    {
        var path = Path.GetTempFileName();
        try
        {
            var workspace = Workspace(); workspace.SaveWorkspace(path);
            var xml = XElement.Load(path); xml.Element("settings").SetAttributeValue("referenceVisible", "invalid");
            xml.Element("settings").SetAttributeValue("floor", "-120"); xml.Save(path);
            Assert.Throws<FormatException>(() => workspace.LoadWorkspace(path)); Assert.Equal(-72, workspace.Settings.FloorDb);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SavedZeroPowerMeasurementsDoNotInventSilentPcm()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var path = Path.GetTempFileName();
            try
            {
                var workspace = Workspace(); workspace.Accept(ZeroPower(false)); workspace.SaveSession(path);
                var loaded = Workspace(); loaded.LoadSession(path);
                using var plot = new AnalysisPlotControl(loaded) { Size = new Size(900, 400) }; Inspect(plot, loaded);
                Assert.Equal("Zero band power", plot.Inspection.Status); Assert.False(plot.Inspection.IsDigitalSilence);
                Assert.False(plot.Inspection.IsUnavailable); Assert.True(double.IsNegativeInfinity(plot.Inspection.LevelDb));
            }
            finally { File.Delete(path); }
        });
    }
}
