using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

// Role-document acceptance: a current failure must be visible without hovering
// or relying on assistive technology, and stale values must not look live.
public sealed class RolesUiStateTests
{
    [Theory]
    [InlineData("Analysis failed: invalid PCM", false)]
    [InlineData("Analysis failed: invalid PCM", true)]
    [InlineData("Device disconnected", false)]
    [InlineData("Device disconnected", true)]
    [InlineData("Capture permission denied", false)]
    [InlineData("Capture permission denied", true)]
    public void FailureReasonIsVisibleAndPreviousMeasurementsAreExplicitlyRetained(string reason, bool previous)
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = new AnalysisWorkspace();
            if (previous) workspace.Accept(AnalysisWorkspaceTests.Tone(frames: 64));
            var retained = workspace.Latest;
            using var form = new AnalysisWorkbenchForm(workspace, null) { ShowInTaskbar = false };
            form.Show(); ModernWorkbenchProbe.Refresh(form);
            if (previous)
                Assert.NotEqual("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
            workspace.SetUnavailable(reason); ModernWorkbenchProbe.Refresh(form);
            var status = ModernWorkbenchProbe.Find<Label>(form, "analysisStatus");
            Assert.Contains(reason, status.Text);
            Assert.Equal(reason, status.AccessibleDescription);
            Assert.DoesNotContain("Live capture", status.Text);
            Assert.Equal(previous, status.Text.Contains("previous measurements retained"));
            Assert.Equal("AUDIO UNAVAILABLE", ModernWorkbenchProbe.Find<Label>(form, "analysisSourceState").Text);
            Assert.Equal("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
            foreach (var metric in ModernWorkbenchProbe.Descendants(form))
                if (metric.Name.StartsWith("analysisMetric_"))
                    Assert.Equal("Unavailable: " + reason, metric.AccessibleDescription);
            Assert.Same(retained, workspace.Latest);
            if (previous && reason.StartsWith("Analysis failed"))
                ModernWorkbenchProbe.SaveEvidence(form, "roles-analysis-error-retained.png");

            // Recovery requires actual newly accepted data, not a footer toggle.
            workspace.Accept(AnalysisWorkspaceTests.Tone(end: 16448, frames: 64));
            ModernWorkbenchProbe.Refresh(form);
            Assert.False(workspace.IsUnavailable);
            Assert.Equal("LIVE ANALYSIS", ModernWorkbenchProbe.Find<Label>(form, "analysisSourceState").Text);
            Assert.Contains("Live capture", status.Text);
            Assert.DoesNotContain(reason, status.Text);
            Assert.NotEqual("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
            Assert.DoesNotContain("Unavailable", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").AccessibleDescription);
            form.Close();
        });

    [Fact]
    public void InitialWaitingStateDoesNotClaimAProcessingFailureOrPreviousMeasurements()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = new AnalysisWorkspace();
            using var form = new AnalysisWorkbenchForm(workspace, null) { ShowInTaskbar = false };
            form.Show(); ModernWorkbenchProbe.Refresh(form);
            var status = ModernWorkbenchProbe.Find<Label>(form, "analysisStatus");
            Assert.Equal("Waiting for captured audio", status.Text);
            Assert.DoesNotContain("previous", status.Text);
            Assert.DoesNotContain("failed", status.Text);
            Assert.Equal("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
            Assert.Equal("Unavailable: Waiting for captured audio", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").AccessibleDescription);
            ModernWorkbenchProbe.SaveEvidence(form, "roles-waiting-for-audio.png");
            form.Close();
        });
}

// Explicit shown-application helper. The inputs below are isolated QA fixtures,
// never a production fallback or a claim that a physical device was exercised.
public static class RolesUiStateVerification
{
    public static string Run(string outputDirectory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The UI state verification requires an STA thread.");
        var output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        var workspace = new AnalysisWorkspace();
        using var form = new AnalysisWorkbenchForm(workspace, null)
        {
            ShowInTaskbar = false,
            Text = "Spectrum · Isolated QA fixture (not live capture)"
        };
        form.Show(); ModernWorkbenchProbe.Refresh(form);
        var status = ModernWorkbenchProbe.Find<Label>(form, "analysisStatus");
        Assert.Equal("Waiting for captured audio", status.Text);
        Assert.Equal("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "01-waiting.png"));

        workspace.Accept(AnalysisWorkspaceTests.Tone(frames: 64));
        ModernWorkbenchProbe.Refresh(form);
        Assert.NotEqual("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
        var valid = workspace.Latest;
        workspace.SetUnavailable("Analysis failed: invalid PCM (isolated QA fixture)");
        ModernWorkbenchProbe.Refresh(form);
        Assert.Contains("Analysis failed: invalid PCM", status.Text);
        Assert.Contains("previous measurements retained", status.Text);
        Assert.Same(valid, workspace.Latest);
        Assert.Equal("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
        Assert.Equal("Unavailable: " + workspace.Status, ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").AccessibleDescription);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "02-error-retained.png"));

        // The frame is a measured all-zero isolated input; it is not fabricated
        // by the renderer and is distinguishable from missing source data.
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        var silent = new AnalysisFrame(plan, 2, 64, 16512, new float[128],
            new double[83], new double[83], new double[83], new object());
        workspace.Accept(silent); ModernWorkbenchProbe.Refresh(form);
        Assert.False(workspace.IsUnavailable);
        Assert.Equal("LIVE ANALYSIS", ModernWorkbenchProbe.Find<Label>(form, "analysisSourceState").Text);
        Assert.DoesNotContain("Analysis failed", status.Text);
        var plot = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
        typeof(AnalysisPlotControl).GetMethod("InspectAt", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(plot, new object[] { plot.Width / 2 });
        Assert.True(plot.Inspection.IsDigitalSilence);
        Assert.False(plot.Inspection.IsUnavailable);
        Assert.Equal("Digital silence", plot.Inspection.Status);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "03-measured-silence-recovery.png"));
        form.Close();
        var result = "{\"passed\":true,\"inputKind\":\"Isolated QA fixtures, not physical capture\",\"waiting\":true,\"visibleProcessingError\":true,\"previousMeasurementsLabeled\":true,\"unavailableMeters\":true,\"accessibleStaleValuesCleared\":true,\"recoveryRequiresAcceptedFrame\":true,\"measuredDigitalSilenceDistinct\":true,\"shownCaptures\":3}";
        File.WriteAllText(Path.Combine(output, "ui-state-proof.json"), result, new UTF8Encoding(false));
        return result;
    }
}
