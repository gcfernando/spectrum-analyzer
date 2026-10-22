using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

// These tests exercise the rendered, interactive presentation without changing the measurement contracts.
public sealed class ModernWorkbenchTests
{
    [Fact]
    public void CompactHeaderRevealsSessionAndCompareCommandsThroughTheirGroups()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisSessionPanel").Visible);
            Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisComparePanel").Visible);
            var header = ModernWorkbenchProbe.Find<Control>(form, "analysisHeader");
            Assert.InRange(header.Height, 30, 110);
            foreach (var name in new[] { "analysisOpenAudio", "analysisResumeLive", "analysisSessionGroup", "analysisCompareGroup" })
                Assert.True(ModernWorkbenchProbe.Find<Button>(form, name).Visible, name);
            ModernWorkbenchProbe.Reveal(form, "analysisSessionGroup", "analysisSessionPanel");
            foreach (var name in new[] { "analysisExportCsv", "analysisSaveSession", "analysisOpenSession", "analysisSaveWorkspace", "analysisLoadWorkspace" })
            {
                var button = ModernWorkbenchProbe.Find<Button>(form, name);
                Assert.True(button.Visible && button.Enabled, name);
                Assert.True(button.Parent.ClientRectangle.Contains(button.Bounds), name);
            }
            ModernWorkbenchProbe.Reveal(form, "analysisCompareGroup", "analysisComparePanel");
            Assert.True(ModernWorkbenchProbe.Find<Button>(form, "analysisCaptureReference").Visible);
            Assert.True(ModernWorkbenchProbe.Find<Button>(form, "analysisClearReference").Visible);
            Assert.Equal("Inspection workspace", workspace.WorkspaceName);
        });

    [Fact]
    public void LiveAndFileContextsFollowRealFileDropAndResumeWithoutPlayingAudio()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisFileSection").Visible);
            Assert.True(ModernWorkbenchProbe.Find<ComboBox>(form, "analysisSourceSelector").Enabled);
            var directory = ModernWorkbenchProbe.NewDirectory();
            try
            {
                var path = Path.Combine(directory, "context.wav"); AnalysisUpgradeVerification.WriteWave(path);
                ModernWorkbenchProbe.Drop(form, path);
                Assert.True(workspace.IsOffline);
                Assert.True(ModernWorkbenchProbe.Find<Control>(form, "analysisFileSection").Visible);
                Assert.False(ModernWorkbenchProbe.Find<ComboBox>(form, "analysisSourceSelector").Enabled);
                Assert.Equal("OFFLINE ANALYSIS", ModernWorkbenchProbe.Find<Label>(form, "analysisSourceState").Text);
                ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisSelectionStartSeconds").Value = 1;
                ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisSelectionDurationSeconds").Value = 1;
                ModernWorkbenchProbe.Find<Button>(form, "analysisAnalyzeSelection").PerformClick(); ModernWorkbenchProbe.WaitForFile(form);
                Assert.Equal(48000, workspace.Measurements.TotalFrames);
                ModernWorkbenchProbe.Find<Button>(form, "analysisResumeLive").PerformClick(); ModernWorkbenchProbe.Pump();
                Assert.False(workspace.IsOffline);
                Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisFileSection").Visible);
                Assert.True(ModernWorkbenchProbe.Find<ComboBox>(form, "analysisSourceSelector").Enabled);
                // Returning to File reuses the selected file rather than opening a blocking picker.
                ModernWorkbenchProbe.Find<Button>(form, "analysisOpenAudio").PerformClick(); ModernWorkbenchProbe.WaitForFile(form);
                Assert.True(workspace.IsOffline); Assert.Equal(48000, workspace.Measurements.TotalFrames);
            }
            finally { Directory.Delete(directory, true); }
        });

    [Fact]
    public void AdvancedAndSettingsPanelsAreProgressiveAndDoNotAlterMeasuredPower()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var raw = (double[])workspace.Latest.Power.Clone();
            Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisAdvancedPanel").Visible);
            ModernWorkbenchProbe.Reveal(form, "analysisAdvancedToggle", "analysisAdvancedPanel");
            ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisAveragingMs").Value = 350;
            ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisSmoothingBands").Value = 2;
            ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisPeakHoldMs").Value = 1250;
            Assert.Equal(350, workspace.Settings.AveragingMilliseconds);
            Assert.Equal(2, workspace.Settings.SmoothingBands); Assert.Equal(1250, workspace.Settings.PeakHoldMilliseconds);
            ModernWorkbenchProbe.Find<Button>(form, "analysisAdvancedToggle").PerformClick(); ModernWorkbenchProbe.Pump();
            Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisAdvancedPanel").Visible);
            var plot = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
            var width = plot.Width;
            ModernWorkbenchProbe.Find<Button>(form, "analysisSettingsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
            Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisSettingsRail").Visible);
            Assert.True(plot.Width > width + 100, "Hiding settings must give the plot useful space.");
            ModernWorkbenchProbe.Find<Button>(form, "analysisSettingsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
            Assert.True(ModernWorkbenchProbe.Find<Control>(form, "analysisSettingsRail").Visible);
            Assert.Equal(raw, workspace.Latest.Power);
        });

    [Fact]
    public void EssentialMetersRemainVisibleAndDetailsDrawerPreservesExactValues()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var size = form.Size;
            var strip = ModernWorkbenchProbe.Find<Control>(form, "analysisEssentialMeters");
            Assert.True(strip.Visible);
            var keys = new[] { "samplePeak", "truePeak", "integrated", "correlation" };
            var before = keys.Select(key => ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + key).Text).ToArray();
            foreach (var key in keys)
            {
                var meter = ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + key);
                Assert.True(meter.Visible); Assert.False(string.IsNullOrWhiteSpace(meter.Text));
            }
            Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisMeasurementRail").Visible);
            ModernWorkbenchProbe.Reveal(form, "analysisDetailsToggle", "analysisMeasurementRail");
            foreach (var key in new[] { "leftRms", "rightRms", "mid", "side", "momentary", "shortTerm", "clipped" })
                Assert.True(ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + key).Visible, key);
            Assert.Equal(before, keys.Select(key => ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + key).Text).ToArray());
            Assert.Equal(size, form.Size);
            Assert.InRange(workspace.Measurements.Correlation, .999999, 1.000001);
        });

    [Fact]
    public void StructuredInspectorPreservesSubfloorPeakPinAndAvailabilitySemantics()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var plot = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
            ModernWorkbenchProbe.Inspect(plot, workspace, 80, true);
            Assert.Equal(81, plot.Inspection.BandNumber); Assert.True(plot.Inspection.IsPinned);
            Assert.True(plot.Inspection.IsBelowFloor);
            Assert.InRange(plot.Inspection.LevelDb, -100.02, -99.98);
            Assert.Contains((-100d).ToString("F2", CultureInfo.CurrentCulture), ModernWorkbenchProbe.Find<Label>(form, "analysisInspectLevel").Text);
            Assert.Contains("Hz", ModernWorkbenchProbe.Find<Label>(form, "analysisInspectFrequency").Text);
            Assert.False(string.IsNullOrWhiteSpace(ModernWorkbenchProbe.Find<Label>(form, "analysisInspectPeak").Text));
            Assert.Contains("below", ModernWorkbenchProbe.Find<Label>(form, "analysisInspectState").Text.ToLowerInvariant());
            ModernWorkbenchProbe.Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-120";
            Assert.True(plot.Inspection.IsPinned); Assert.False(plot.Inspection.IsBelowFloor);
            workspace.SetUnavailable("A known capture gap"); ModernWorkbenchProbe.Refresh(form);
            Assert.True(plot.Inspection.IsUnavailable);
            Assert.Contains("unavailable", ModernWorkbenchProbe.Find<Label>(form, "analysisInspectState").Text.ToLowerInvariant());
            Assert.Equal("—", ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_samplePeak").Text);
        });

    [Fact]
    public void FrequencyPresetsChangeTheViewportAndPreserveRawAnalysis()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var power = (double[])workspace.Latest.Power.Clone();
            foreach (var preset in ModernWorkbenchProbe.Presets)
            {
                ModernWorkbenchProbe.Find<Button>(form, preset.Name).PerformClick(); ModernWorkbenchProbe.Pump();
                Assert.Equal(preset.Lower, workspace.Settings.MinHz); Assert.Equal(preset.Upper, workspace.Settings.MaxHz);
                Assert.Equal((decimal)preset.Lower, ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisMinimumHz").Value);
                Assert.Equal((decimal)preset.Upper, ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisMaximumHz").Value);
                workspace.Settings.Validate(); Assert.Equal(power, workspace.Latest.Power);
            }
        });

    [Fact]
    public void ReferenceOverlayVisibilityIsPaintOnlyAndTheReferenceIsNamed()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            ModernWorkbenchProbe.Reveal(form, "analysisCompareGroup", "analysisComparePanel");
            ModernWorkbenchProbe.Find<Button>(form, "analysisCaptureReference").PerformClick(); ModernWorkbenchProbe.Refresh(form);
            Assert.NotNull(workspace.ReferencePower); Assert.False(string.IsNullOrWhiteSpace(workspace.ReferenceName));
            Assert.True(workspace.ReferenceCapturedUtc.HasValue);
            Assert.Contains(workspace.ReferenceName, ModernWorkbenchProbe.Find<Label>(form, "analysisReferenceStatus").Text);
            var raw = (double[])workspace.Latest.Power.Clone(); var reference = (double[])workspace.ReferencePower.Clone();
            var display = (double[])workspace.DisplayPower.Clone();
            var overlay = ModernWorkbenchProbe.Find<CheckBox>(form, "analysisReferenceOverlay");
            Assert.True(overlay.Visible && overlay.Checked);
            var plot = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
            ModernWorkbenchProbe.Inspect(plot, workspace, 80, false); var difference = plot.Inspection.DeltaDb;
            var visibleAmber = ModernWorkbenchProbe.CountReferencePixels(plot);
            overlay.Checked = false; ModernWorkbenchProbe.Refresh(form);
            Assert.False(workspace.ReferenceVisible); Assert.False(plot.ShowReference);
            Assert.Equal(raw, workspace.Latest.Power); Assert.Equal(reference, workspace.ReferencePower); Assert.Equal(display, workspace.DisplayPower);
            Assert.Equal(difference, plot.Inspection.DeltaDb);
            Assert.True(visibleAmber > ModernWorkbenchProbe.CountReferencePixels(plot) + 10,
                "The actual amber curve must disappear when its overlay is hidden.");
            overlay.Checked = true; Assert.True(workspace.ReferenceVisible); Assert.True(plot.ShowReference);
        });

    [Fact]
    public void ExpandedViewportShadesOnlyFrequenciesOutsideTheMeasuredPlan()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var plot = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
            var raw = (double[])workspace.Latest.Power.Clone();
            plot.SetFrequencyRange(1, 100000); ModernWorkbenchProbe.Refresh(form);
            ModernWorkbenchProbe.AssertUnmeasuredShading(plot, workspace);
            Assert.Equal(raw, workspace.Latest.Power);
        });

    [Fact]
    public void AFailedFileShowsFriendlyInlineErrorAndOptInTechnicalDetailsWithoutLosingMeasurements()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var directory = ModernWorkbenchProbe.NewDirectory();
            try
            {
                var path = Path.Combine(directory, "malformed.wav"); File.WriteAllText(path, "This is not a RIFF WAVE file.");
                var before = workspace.Latest;
                ModernWorkbenchProbe.Drop(form, path, false);
                var error = ModernWorkbenchProbe.Find<Label>(form, "analysisError");
                Assert.True(error.Visible); Assert.False(string.IsNullOrWhiteSpace(error.Text));
                Assert.DoesNotContain("System.IO", error.Text); Assert.DoesNotContain("   at ", error.Text);
                Assert.Same(before, workspace.Latest);
                var details = ModernWorkbenchProbe.Find<TextBox>(form, "analysisErrorDetails");
                Assert.False(details.Visible);
                ModernWorkbenchProbe.Find<Button>(form, "analysisErrorDetailsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
                Assert.True(details.Visible && details.ReadOnly);
                Assert.Contains("Exception", details.Text);
                Assert.True(ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot").Visible);
            }
            finally { Directory.Delete(directory, true); }
        });

    [Theory]
    [InlineData(1200, 800, 1f)]
    [InlineData(960, 680, 1f)]
    [InlineData(1800, 1200, 1.5f)]
    public void DarkTabsAndTheDominantPlotRemainUsableAtNormalCompactAndScaledSizes(int width, int height, float scale)
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var fonts = new List<Font>();
            try
            {
                if (scale != 1)
                {
                    var controls = ModernWorkbenchProbe.Descendants(form).Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
                    form.AutoScaleMode = AutoScaleMode.None; form.Scale(new SizeF(scale, scale));
                    foreach (var pair in controls)
                    {
                        var font = new Font(pair.Font.FontFamily, pair.Font.SizeInPoints * scale, pair.Font.Style);
                        fonts.Add(font); pair.Control.Font = font;
                    }
                }
                form.ClientSize = new Size(width, height); ModernWorkbenchProbe.Pump();
                var plot = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
                Assert.True(plot.Width >= form.ClientSize.Width * .60, "The plot should dominate the default workspace width.");
                Assert.True(plot.Height >= form.ClientSize.Height * (height < 700 ? .30 : .40), "Default utility chrome should leave a useful plot height.");
                var tabs = ModernWorkbenchProbe.Find<TabControl>(form, "analysisPlotTabs");
                Assert.Equal(TabDrawMode.OwnerDrawFixed, tabs.DrawMode);
                foreach (var index in new[] { 0, 1, 2 })
                {
                    tabs.SelectedIndex = index; ModernWorkbenchProbe.Pump();
                    var active = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, index == 0 ? "analysisSpectrumPlot" : index == 1 ? "analysisSpectrogramPlot" : "analysisPhasePlot");
                    Assert.True(active.Visible && active.Width >= 250 && active.Height >= 200);
                    ModernWorkbenchProbe.AssertDarkTabs(tabs);
                    ModernWorkbenchProbe.SaveEvidence(form, $"modern-{width}x{height}-{(int)(scale * 100)}-view{index}.png");
                }
            }
            finally { form.Close(); foreach (var font in fonts) font.Dispose(); }
        });

    [Fact]
    public void HeaderWorkspaceSelectorIdentifiesTheCurrentNamedWorkspace()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var selector = ModernWorkbenchProbe.Find<ComboBox>(form, "analysisWorkspaceSelector");
            Assert.True(selector.Visible && selector.Enabled); Assert.False(string.IsNullOrWhiteSpace(selector.AccessibleName));
            Assert.Contains("Inspection workspace", selector.Text);
            ModernWorkbenchProbe.Reveal(form, "analysisSessionGroup", "analysisSessionPanel");
            Assert.Equal(workspace.WorkspaceName, ModernWorkbenchProbe.Find<TextBox>(form, "analysisWorkspaceName").Text);
        });
}

internal static class ModernWorkbenchProbe
{
    internal static readonly (string Name, double Lower, double Upper)[] Presets =
    {
        ("analysisRangeBass", 20, 250), ("analysisRangeVocals", 250, 4000),
        ("analysisRangeHigh", 4000, 20000), ("analysisRangeFull", 20, 20000)
    };
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Shown(Action<AnalysisWorkbenchForm, AnalysisWorkspace> action)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Fixture(); using var analyzer = new Analyzer(false);
            using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
            form.Show(); Pump();
            try { action(form, workspace); } finally { form.Close(); }
        });
    }
    internal static AnalysisWorkspace Fixture()
    {
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        var engine = new BandSpectrumAnalyzer(plan, 2);
        var samples = Signals.StereoSine(engine.RequiredFrames, 48000, plan[80].CenterHz, 1e-5, 1e-5);
        for (var frame = 0; frame < engine.RequiredFrames; frame++)
        {
            var signal = (float)(.05 * Math.Sin(2 * Math.PI * 997 * frame / 48000));
            samples[frame * 2] += signal; samples[frame * 2 + 1] += signal;
        }
        var power = new double[83]; var left = new double[83]; var right = new double[83];
        engine.Analyze(samples, engine.RequiredFrames, power, left, right);
        var workspace = new AnalysisWorkspace { WorkspaceName = "Inspection workspace" };
        workspace.Settings.AveragingMilliseconds = 0; workspace.Settings.SmoothingBands = 0; workspace.UpdateSettings();
        workspace.Accept(new AnalysisFrame(plan, 2, engine.RequiredFrames, engine.RequiredFrames, samples, power, left, right, new object()));
        return workspace;
    }
    internal static T Find<T>(Control root, string name) where T : Control => (T)Descendants(root).Single(c => c.Name == name);
    internal static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    internal static void Pump(int milliseconds = 80)
    { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); } }
    internal static void Refresh(AnalysisWorkbenchForm form)
    { typeof(AnalysisWorkbenchForm).GetMethod("RefreshFromWorkspace", Hidden).Invoke(form, null); Pump(); }
    internal static void Reveal(Control form, string button, string panel)
    { if (!Find<Control>(form, panel).Visible) Find<Button>(form, button).PerformClick(); Pump(); Assert.True(Find<Control>(form, panel).Visible, panel); }
    internal static void Inspect(AnalysisPlotControl plot, AnalysisWorkspace workspace, int band, bool pin)
    {
        var x = 62 + (int)Math.Round(Math.Log(workspace.Latest.Plan[band].CenterHz / workspace.Settings.MinHz) /
            Math.Log(workspace.Settings.MaxHz / workspace.Settings.MinHz) * (plot.Width - 82));
        typeof(AnalysisPlotControl).GetMethod(pin ? "OnMouseDown" : "OnMouseMove", Hidden).Invoke(plot,
            new object[] { new MouseEventArgs(pin ? MouseButtons.Left : MouseButtons.None, pin ? 1 : 0, x, 60, 0) });
        Pump();
    }
    internal static void Drop(AnalysisWorkbenchForm form, string path, bool success = true)
    {
        var data = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { path }), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
        typeof(Control).GetMethod("OnDragEnter", Hidden).Invoke(form, new object[] { data }); Assert.Equal(DragDropEffects.Copy, data.Effect);
        typeof(Control).GetMethod("OnDragDrop", Hidden).Invoke(form, new object[] { data }); WaitForFile(form, success);
    }
    internal static void WaitForFile(AnalysisWorkbenchForm form, bool success = true)
    {
        var busy = typeof(AnalysisWorkbenchForm).GetField("_busy", Hidden); var clock = Stopwatch.StartNew();
        while ((bool)busy.GetValue(form) && clock.ElapsedMilliseconds < 20000) Pump(25);
        Assert.False((bool)busy.GetValue(form), "File analysis should complete without blocking the UI."); Pump();
        Assert.Equal(!success, Find<Label>(form, "analysisError").Visible);
    }
    internal static void AssertDarkTabs(TabControl tabs)
    {
        using var image = new Bitmap(tabs.Width, tabs.Height); tabs.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        for (var tab = 0; tab < tabs.TabCount; tab++)
        {
            var bounds = tabs.GetTabRect(tab); var dark = 0; var count = 0;
            for (var y = bounds.Top + 3; y < bounds.Bottom - 3; y += 3)
                for (var x = bounds.Left + 3; x < bounds.Right - 3; x += 3)
                { count++; if (image.GetPixel(x, y).GetBrightness() < .5) dark++; }
            Assert.True(count > 0 && dark >= count * .60, "Each rendered tab must keep a predominantly dark surface.");
        }
    }
    internal static int CountReferencePixels(AnalysisPlotControl plot)
    {
        using var image = new Bitmap(plot.Width, plot.Height); plot.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        var count = 0;
        for (var y = 43; y < plot.Height - 49; y++)
            for (var x = 63; x < plot.Width - 20; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.R > 170 && pixel.G > 95 && pixel.G < 215 && pixel.B < 145 && pixel.R > pixel.G * 1.15) count++;
            }
        return count;
    }
    internal static void AssertUnmeasuredShading(AnalysisPlotControl plot, AnalysisWorkspace workspace)
    {
        var plan = workspace.Latest.Plan;
        Assert.True(workspace.Settings.MinHz < plan.MinHz && workspace.Settings.MaxHz > plan.MaxHz);
        using var image = new Bitmap(plot.Width, plot.Height); plot.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        int X(double hz) => 62 + (int)(Math.Log(hz / workspace.Settings.MinHz) /
            Math.Log(workspace.Settings.MaxHz / workspace.Settings.MinHz) * (plot.Width - 82));
        double Changed(double hz)
        {
            var changed = 0; var count = 0;
            for (var y = 55; y < 72; y++) for (var x = X(hz) - 8; x <= X(hz) + 8; x++)
            {
                var pixel = image.GetPixel(x, y); count++;
                if (Math.Abs(pixel.R - plot.BackColor.R) + Math.Abs(pixel.G - plot.BackColor.G) + Math.Abs(pixel.B - plot.BackColor.B) > 5) changed++;
            }
            return changed / (double)count;
        }
        Assert.True(Changed(Math.Sqrt(workspace.Settings.MinHz * plan.MinHz)) > .8, "The lower unmeasured region must be visibly hatched.");
        Assert.True(Changed(Math.Sqrt(workspace.Settings.MaxHz * plan.MaxHz)) > .8, "The upper unmeasured region must be visibly hatched.");
        Assert.True(Changed(650) < .25, "Quiet measured frequencies must retain the measured plot surface.");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AnalysisPlotControl).GetMethod("OnMouseDown", flags).Invoke(plot,
            new object[] { new MouseEventArgs(MouseButtons.Right, 1, 90, 70, 0) });
        typeof(AnalysisPlotControl).GetMethod("OnMouseMove", flags).Invoke(plot,
            new object[] { new MouseEventArgs(MouseButtons.None, 0, X(Math.Sqrt(workspace.Settings.MaxHz * plan.MaxHz)), 70, 0) });
        Assert.Equal(0, plot.Inspection.BandNumber);
        Assert.Contains("Outside the measured frequency range", plot.InspectionText);
    }
    internal static string NewDirectory()
    { var path = Path.Combine(Path.GetTempPath(), "SpectrumModernUi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static void SaveEvidence(Form form, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("SPECTRUM_WORKBENCH_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); Snapshot(form, Path.Combine(directory, filename));
    }
    internal static void Snapshot(Form form, string path)
    { Pump(); form.Refresh(); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path, ImageFormat.Png); }
}

// Called by the explicit shown-application acceptance run, never by automatic discovery.
internal static class ModernWorkbenchVerification
{
    internal static string Run(AnalysisWorkbenchForm form, AnalysisWorkspace workspace, string wave, string output)
    {
        var results = new List<string>();
        var tabs = ModernWorkbenchProbe.Find<TabControl>(form, "analysisPlotTabs"); tabs.SelectedIndex = 0;
        foreach (var pair in new[] {
            ("analysisSessionGroup", "analysisSessionPanel"), ("analysisCompareGroup", "analysisComparePanel"),
            ("analysisAdvancedToggle", "analysisAdvancedPanel"), ("analysisDetailsToggle", "analysisMeasurementRail") })
            if (ModernWorkbenchProbe.Find<Control>(form, pair.Item2).Visible)
                ModernWorkbenchProbe.Find<Button>(form, pair.Item1).PerformClick();
        ModernWorkbenchProbe.Drop(form, wave); ModernWorkbenchProbe.Refresh(form);
        foreach (var name in new[] { "analysisOpenAudio", "analysisResumeLive", "analysisSessionGroup", "analysisCompareGroup" })
            Assert.True(ModernWorkbenchProbe.Find<Button>(form, name).Visible);
        var selector = ModernWorkbenchProbe.Find<ComboBox>(form, "analysisWorkspaceSelector");
        Assert.True(selector.Visible); Assert.Contains(workspace.WorkspaceName, selector.Text);
        ModernWorkbenchProbe.Reveal(form, "analysisSessionGroup", "analysisSessionPanel");
        foreach (var name in new[] { "analysisExportCsv", "analysisSaveSession", "analysisOpenSession", "analysisSaveWorkspace", "analysisLoadWorkspace" })
            Assert.True(ModernWorkbenchProbe.Find<Button>(form, name).Visible);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-01-session-group.png"));
        ModernWorkbenchProbe.Find<Button>(form, "analysisSessionGroup").PerformClick();
        Add(1, "Compact header, named workspace and grouped commands");

        Assert.True(ModernWorkbenchProbe.Find<Control>(form, "analysisFileSection").Visible);
        ModernWorkbenchProbe.Find<Button>(form, "analysisResumeLive").PerformClick(); ModernWorkbenchProbe.Pump();
        Assert.False(workspace.IsOffline); Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisFileSection").Visible);
        Assert.True(ModernWorkbenchProbe.Find<ComboBox>(form, "analysisSourceSelector").Enabled);
        ModernWorkbenchProbe.Find<Button>(form, "analysisOpenAudio").PerformClick(); ModernWorkbenchProbe.WaitForFile(form);
        Assert.True(workspace.IsOffline); Assert.True(ModernWorkbenchProbe.Find<Control>(form, "analysisFileSection").Visible);
        Assert.False(ModernWorkbenchProbe.Find<ComboBox>(form, "analysisSourceSelector").Enabled);
        Add(2, "Live/File context returns to the retained decoded file through actual header buttons");

        var raw = (double[])workspace.Latest.Power.Clone();
        ModernWorkbenchProbe.Reveal(form, "analysisAdvancedToggle", "analysisAdvancedPanel");
        ModernWorkbenchProbe.Find<NumericUpDown>(form, "analysisAveragingMs").Value = 225;
        Assert.Equal(225, workspace.Settings.AveragingMilliseconds);
        ModernWorkbenchProbe.Find<Button>(form, "analysisAdvancedToggle").PerformClick();
        Assert.False(ModernWorkbenchProbe.Find<Control>(form, "analysisAdvancedPanel").Visible);
        Assert.Equal(raw, workspace.Latest.Power); Add(3, "Progressive advanced controls preserve measured power");

        var plot = ModernWorkbenchProbe.Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
        var beforeWidth = plot.Width;
        ModernWorkbenchProbe.Find<Button>(form, "analysisSettingsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
        Assert.True(plot.Width > beforeWidth + 100);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-02-expanded-plot.png"));
        ModernWorkbenchProbe.Find<Button>(form, "analysisSettingsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
        Add(4, "Settings drawer gives space back to the dominant plot");

        var keys = new[] { "samplePeak", "truePeak", "integrated", "correlation" };
        var meterText = keys.Select(k => ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + k).Text).ToArray();
        Assert.All(keys, k => Assert.True(ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + k).Visible));
        ModernWorkbenchProbe.Reveal(form, "analysisDetailsToggle", "analysisMeasurementRail");
        foreach (var key in new[] { "leftRms", "rightRms", "mid", "side", "momentary", "shortTerm", "clipped" })
            Assert.True(ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + key).Visible);
        Assert.Equal(meterText, keys.Select(k => ModernWorkbenchProbe.Find<Label>(form, "analysisMetric_" + k).Text).ToArray());
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-03-meter-details.png"));
        ModernWorkbenchProbe.Find<Button>(form, "analysisDetailsToggle").PerformClick();
        Add(5, "Four essential meters stay visible while Details reveals the full readings");

        ModernWorkbenchProbe.Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-72";
        ModernWorkbenchProbe.Inspect(plot, workspace, 80, true);
        Assert.Equal(81, plot.Inspection.BandNumber); Assert.True(plot.Inspection.IsPinned && plot.Inspection.IsBelowFloor);
        Assert.InRange(plot.Inspection.LevelDb, -100.1, -99.9);
        Assert.Contains("below", ModernWorkbenchProbe.Find<Label>(form, "analysisInspectState").Text.ToLowerInvariant());
        Assert.Contains("Hz", ModernWorkbenchProbe.Find<Label>(form, "analysisInspectFrequency").Text);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-04-structured-inspector.png"));
        ModernWorkbenchProbe.Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-120";
        Add(6, "Structured inspector retains exact subfloor, held peak and pin semantics");

        foreach (var preset in ModernWorkbenchProbe.Presets)
        {
            ModernWorkbenchProbe.Find<Button>(form, preset.Name).PerformClick();
            Assert.Equal(preset.Lower, workspace.Settings.MinHz); Assert.Equal(preset.Upper, workspace.Settings.MaxHz);
        }
        plot.SetFrequencyRange(1, 100000); ModernWorkbenchProbe.Refresh(form);
        ModernWorkbenchProbe.AssertUnmeasuredShading(plot, workspace);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-05-unmeasured-frequency-regions.png"));
        ModernWorkbenchProbe.Find<Button>(form, "analysisRangeFull").PerformClick(); ModernWorkbenchProbe.Refresh(form);
        Add(7, "Range presets apply exact boundaries; expanded regions outside the measured plan are shaded");

        ModernWorkbenchProbe.Reveal(form, "analysisCompareGroup", "analysisComparePanel");
        ModernWorkbenchProbe.Find<Button>(form, "analysisCaptureReference").PerformClick(); ModernWorkbenchProbe.Refresh(form);
        Assert.NotNull(workspace.ReferencePower); var reference = (double[])workspace.ReferencePower.Clone();
        Assert.Contains(workspace.ReferenceName, ModernWorkbenchProbe.Find<Label>(form, "analysisReferenceStatus").Text);
        var overlay = ModernWorkbenchProbe.Find<CheckBox>(form, "analysisReferenceOverlay");
        var amber = ModernWorkbenchProbe.CountReferencePixels(plot);
        overlay.Checked = false; ModernWorkbenchProbe.Refresh(form);
        Assert.False(plot.ShowReference); Assert.Equal(reference, workspace.ReferencePower);
        Assert.True(amber > ModernWorkbenchProbe.CountReferencePixels(plot) + 10);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-06-reference-hidden.png"));
        overlay.Checked = true; ModernWorkbenchProbe.Refresh(form); Assert.True(plot.ShowReference);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-07-reference-visible.png"));
        ModernWorkbenchProbe.Find<Button>(form, "analysisCompareGroup").PerformClick();
        Add(8, "Named reference overlay visibly hides and returns without discarding reference measurements");

        var invalid = Path.Combine(output, "verification-malformed.wav"); File.WriteAllText(invalid, "Not a WAV file");
        var previous = workspace.Latest; ModernWorkbenchProbe.Drop(form, invalid, false);
        Assert.Same(previous, workspace.Latest);
        var error = ModernWorkbenchProbe.Find<Label>(form, "analysisError"); Assert.DoesNotContain("   at ", error.Text);
        var diagnostics = ModernWorkbenchProbe.Find<TextBox>(form, "analysisErrorDetails"); Assert.False(diagnostics.Visible);
        ModernWorkbenchProbe.Find<Button>(form, "analysisErrorDetailsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
        Assert.True(diagnostics.Visible && diagnostics.ReadOnly); Assert.Contains("Exception", diagnostics.Text);
        ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-08-inline-error-details.png"));
        ModernWorkbenchProbe.Drop(form, wave); Assert.False(error.Visible);
        Add(9, "Nonfatal inline error exposes technical details only on demand and recovers on a valid file");

        foreach (var size in new[] { new Size(1200, 800), new Size(960, 680) })
        {
            form.ClientSize = size; ModernWorkbenchProbe.Pump();
            Assert.True(plot.Width >= size.Width * .60); Assert.True(plot.Height >= size.Height * .30);
            for (var view = 0; view < 3; view++)
            {
                tabs.SelectedIndex = view; ModernWorkbenchProbe.Pump(); ModernWorkbenchProbe.AssertDarkTabs(tabs);
                ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, $"modern-layout-{size.Width}x{size.Height}-view{view}.png"));
            }
        }
        var originals = ModernWorkbenchProbe.Descendants(form).Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
        var fonts = new List<Font>();
        try
        {
            form.AutoScaleMode = AutoScaleMode.None; form.Scale(new SizeF(1.5f, 1.5f));
            foreach (var pair in originals)
            { var font = new Font(pair.Font.FontFamily, pair.Font.SizeInPoints * 1.5f, pair.Font.Style); fonts.Add(font); pair.Control.Font = font; }
            form.ClientSize = new Size(1800, 1200); tabs.SelectedIndex = 0; ModernWorkbenchProbe.Pump();
            Assert.True(plot.Width >= 1080 && plot.Height >= 480); ModernWorkbenchProbe.AssertDarkTabs(tabs);
            ModernWorkbenchProbe.Snapshot(form, Path.Combine(output, "modern-layout-1800x1200-enlarged-fonts.png"));
        }
        finally { foreach (var pair in originals) pair.Control.Font = pair.Font; foreach (var font in fonts) font.Dispose(); }
        Add(10, "Dark tabs and useful plots are shown at normal, compact and 150 percent control/font scale");
        return string.Join(",", results);

        void Add(int id, string detail) => results.Add("{\"id\":" + id + ",\"passed\":true,\"detail\":\"" + detail.Replace("\"", "\\\"") + "\"}");
    }
}
