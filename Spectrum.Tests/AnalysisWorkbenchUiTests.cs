// Developer :: > Gehan Fernando
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
using Spectrum;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

// Exercises actual shown WinForms controls. Optional PNGs are application renders, not mockups.
public sealed class AnalysisWorkbenchUiTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1200, 800, 1f)]
    [InlineData(900, 650, 1f)]
    [InlineData(1800, 1200, 1.5f)]
    public void ShownWorkbenchKeepsCommandsAndPlotsUsableAtSupportedSizes(int width, int height, float scale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Fixture();
            using var analyzer = new Analyzer(false);
            using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
            var fonts = new List<Font>();
            try
            {
                form.Show();
                if (scale != 1)
                {
                    var controls = Descendants(form).Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
                    form.AutoScaleMode = AutoScaleMode.None;
                    form.Scale(new SizeF(scale, scale));
                    foreach (var pair in controls)
                    {
                        var font = new Font(pair.Font.FontFamily, pair.Font.SizeInPoints * scale, pair.Font.Style);
                        fonts.Add(font);
                        pair.Control.Font = font;
                    }
                }
                form.ClientSize = new Size(width, height);
                Pump();
                foreach (var name in new[] { "analysisOpenAudio", "analysisResumeLive", "analysisSessionGroup", "analysisCompareGroup" })
                {
                    var command = Find<Button>(form, name);
                    Assert.True(command.Visible);
                    Assert.True(command.Enabled);
                    Assert.False(string.IsNullOrWhiteSpace(command.AccessibleName));
                    Assert.True(command.Parent.ClientRectangle.Contains(command.Bounds), name + " must remain inside the compact command strip.");
                }
                foreach (var group in new[]
                {
                    (Button: "analysisSessionGroup", Panel: "analysisSessionPanel", Commands: new[] { "analysisExportCsv", "analysisSaveSession", "analysisOpenSession", "analysisSaveWorkspace", "analysisLoadWorkspace" }),
                    (Button: "analysisCompareGroup", Panel: "analysisComparePanel", Commands: new[] { "analysisCaptureReference", "analysisClearReference" })
                })
                {
                    Reveal(form, group.Button, group.Panel);
                    foreach (var name in group.Commands)
                    {
                        var command = Find<Button>(form, name);
                        Assert.True(command.Visible, name + " must be reachable through its command group.");
                        Assert.True(command.Enabled);
                        Assert.False(string.IsNullOrWhiteSpace(command.AccessibleName));
                        Assert.True(command.Parent.ClientRectangle.Contains(command.Bounds), name + " must remain inside its opened command group.");
                    }
                    Find<Button>(form, group.Button).PerformClick();
                    Pump();
                }
                Assert.False(Find<FlowLayoutPanel>(form, "analysisMeasurementRail").HorizontalScroll.Visible,
                    "The collapsed Details drawer must not retain a horizontal scrollbar.");
                Reveal(form, "analysisDetailsToggle", "analysisMeasurementRail");
                foreach (var railName in new[] { "analysisSettingsRail", "analysisMeasurementRail" })
                {
                    var rail = Find<FlowLayoutPanel>(form, railName);
                    Assert.True(rail.Visible);
                    Assert.False(rail.HorizontalScroll.Visible, railName + " should scroll vertically without horizontal clipping.");
                    foreach (Control card in rail.Controls)
                        if (card.Visible) Assert.True(card.Right <= rail.ClientSize.Width,
                            railName + "/" + card.Name + " must fit the shown rail width.");
                }
                Find<Button>(form, "analysisDetailsToggle").PerformClick(); Pump();
                Assert.True(Find<ComboBox>(form, "analysisSourceSelector").Width >= 180 * scale,
                    "The source selector must retain enough width to identify the selected endpoint.");
                var channelSelector = Find<ComboBox>(form, "analysisChannelSelector");
                var longestChannel = channelSelector.Items.Cast<object>().Max(item => TextRenderer.MeasureText(item.ToString(), channelSelector.Font).Width);
                Assert.True(channelSelector.Width >= longestChannel + SystemInformation.VerticalScrollBarWidth + 10 * scale,
                    "The paired channel selector must show its longest option without clipping.");
                Assert.True(Find<Label>(form, "analysisMetric_samplePeak").Width >= 100 * scale,
                    "Numerical metrics must have enough width for an ordinary signed dB value.");
                var spectrum = Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
                SaveEvidence(form, $"workbench-{width}x{height}-{(int)(scale * 100)}-spectrum-before-size-check.png");
                Assert.True(spectrum.Width >= 250 && spectrum.Height >= 200,
                    $"The actual spectrum must retain at least 250×200 pixels; observed {spectrum.Width}×{spectrum.Height} at {form.ClientSize.Width}×{form.ClientSize.Height} client size.");
                Assert.True(Find<Label>(form, "analysisInspectFrequency").Visible);
                Assert.True(Find<Label>(form, "analysisInspectLevel").Visible);
                SaveEvidence(form, $"workbench-{width}x{height}-{(int)(scale * 100)}-spectrum.png");
                Find<TabControl>(form, "analysisPlotTabs").SelectedIndex = 1;
                Pump();
                Assert.True(Find<AnalysisPlotControl>(form, "analysisSpectrogramPlot").Visible);
                Assert.Equal("Spectrogram", workspace.PreferredView);
                SaveEvidence(form, $"workbench-{width}x{height}-{(int)(scale * 100)}-spectrogram.png");
                Find<TabControl>(form, "analysisPlotTabs").SelectedIndex = 2;
                Pump();
                Assert.True(Find<AnalysisPlotControl>(form, "analysisPhasePlot").Visible);
                Assert.True(Find<AnalysisPlotControl>(form, "analysisPhasePlot").ShowPhase);
                Assert.Equal("Stereo phase", workspace.PreferredView);
                SaveEvidence(form, $"workbench-{width}x{height}-{(int)(scale * 100)}-stereo-phase.png");
            }
            finally
            {
                form.Close();
                foreach (var font in fonts) font.Dispose();
            }
        });
    }

    [Fact]
    public void RangeResponseAndReferenceCommandsChangeMeasuredWorkspaceWithoutChangingRawPower()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Fixture();
            var original = (double[])workspace.Latest.Power.Clone();
            using var analyzer = new Analyzer(false);
            using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
            form.Show();
            Reveal(form, "analysisAdvancedToggle", "analysisAdvancedPanel");
            Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-120";
            Assert.Equal(-120, workspace.Settings.FloorDb);
            Find<ComboBox>(form, "analysisResponsePreset").SelectedItem = "Detailed";
            Assert.Equal("Detailed", workspace.Settings.Response);
            Assert.Equal(600, workspace.Settings.AveragingMilliseconds);
            Find<NumericUpDown>(form, "analysisAveragingMs").Value = 350;
            Assert.Equal(350, workspace.Settings.AveragingMilliseconds);
            Find<ComboBox>(form, "analysisChannelSelector").SelectedItem = "Left";
            Assert.Equal("Left", workspace.Settings.Channel);
            var span = workspace.Settings.MaxHz / workspace.Settings.MinHz;
            Find<Button>(form, "analysisZoomIn").PerformClick();
            Assert.True(workspace.Settings.MaxHz / workspace.Settings.MinHz < span);
            var lower = workspace.Settings.MinHz;
            Find<Button>(form, "analysisPanHigher").PerformClick();
            Assert.True(workspace.Settings.MinHz > lower);
            Find<Button>(form, "analysisResetZoom").PerformClick();
            Assert.Equal(20, workspace.Settings.MinHz);
            Assert.Equal(20000, workspace.Settings.MaxHz);
            Reveal(form, "analysisCompareGroup", "analysisComparePanel");
            Find<Button>(form, "analysisCaptureReference").PerformClick();
            Assert.NotNull(workspace.ReferencePower);
            var reference = (double[])workspace.ReferencePower.Clone();
            Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-90";
            Assert.Equal(reference, workspace.ReferencePower);
            Find<Button>(form, "analysisClearReference").PerformClick();
            Assert.Null(workspace.ReferencePower);
            Assert.Equal(original, workspace.Latest.Power);
            Assert.False(Find<Label>(form, "analysisError").Visible);
            form.Close();
        });
    }

    [Fact]
    public void ExactTailInspectionDistinguishesBelowFloorFromSilenceAndUnavailable()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Fixture();
            using var analyzer = new Analyzer(false);
            using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
            form.Show();
            Pump();
            var plot = Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
            Inspect(plot, workspace.Latest.Plan[81].CenterHz, workspace);
            var inspector = Find<Label>(form, "analysisBandInspector");
            Assert.Contains("Band 82", inspector.AccessibleDescription);
            Assert.Contains("below display floor", inspector.AccessibleDescription);
            Assert.Contains((-100.0).ToString("F2", CultureInfo.CurrentCulture), Find<Label>(form, "analysisInspectLevel").Text);
            Assert.Contains("held curve peak", inspector.AccessibleDescription);
            Pin(plot, workspace.Latest.Plan[81].CenterHz, workspace);
            Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-120";
            // A pinned readout must update after a scale change without another mouse move.
            Assert.Contains("Band 82", inspector.AccessibleDescription);
            Assert.DoesNotContain("below display floor", inspector.AccessibleDescription);
            SaveEvidence(form, "workbench-quiet-tail-inspection-minus120.png");
            workspace.SetUnavailable("Test capture gap");
            Invoke(form, "RefreshFromWorkspace");
            Inspect(plot, workspace.Latest.Plan[81].CenterHz, workspace);
            Assert.Contains("unavailable", inspector.AccessibleDescription);
            Assert.Equal("AUDIO UNAVAILABLE", Find<Label>(form, "analysisSourceState").Text);
            Assert.Equal("—", Find<Label>(form, "analysisMetric_samplePeak").Text);
            form.Close();
        });
    }

    [Fact]
    public void SourceChooserRetainsDefaultAndExposesAllTwelveFeatureControlPaths()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = Fixture();
            using var analyzer = new Analyzer(false);
            using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
            form.Show();
            Pump();
            var source = Find<ComboBox>(form, "analysisSourceSelector");
            Assert.Single(source.Items.Cast<AnalysisAudioSource>());
            Assert.True(((AnalysisAudioSource)source.SelectedItem).IsDefault);
            Find<Button>(form, "analysisRefreshSources").PerformClick();
            Assert.True(((AnalysisAudioSource)source.SelectedItem).IsDefault);
            foreach (var name in new[]
            {
                "analysisFloorDb", "analysisBandInspector", "analysisMinimumHz", "analysisMaximumHz",
                "analysisSourceSelector", "analysisChannelSelector", "analysisResponsePreset", "analysisAveragingMs",
                "analysisSmoothingBands", "analysisPeakHoldMs", "analysisCaptureReference", "analysisPlotTabs",
                "analysisHistorySeconds", "analysisOpenAudio", "analysisSelectionStartSeconds", "analysisSelectionDurationSeconds",
                "analysisAnalyzeSelection", "analysisExportCsv", "analysisSaveSession", "analysisOpenSession",
                "analysisPhasePlot",
                "analysisMetric_correlation", "analysisMetric_mid", "analysisMetric_side", "analysisMetric_momentary",
                "analysisMetric_shortTerm", "analysisMetric_integrated", "analysisMetric_truePeak",
                "analysisWorkspaceName", "analysisSaveWorkspace", "analysisLoadWorkspace"
            })
                Assert.False(string.IsNullOrWhiteSpace(Find<Control>(form, name).AccessibleName), name + " requires an accessible label.");
            var frame = workspace.Latest;
            workspace.Accept(new AnalysisFrame(frame.Plan, 6, frame.FrameCount, frame.EndFrame + frame.FrameCount,
                new float[frame.FrameCount * 6], frame.Power, frame.LeftPower, frame.RightPower));
            Invoke(form, "RefreshFromWorkspace");
            Assert.Contains("Front L/R measured", Find<Label>(form, "analysisSourceDetails").Text);
            Assert.Contains("Programme loudness unavailable", Find<Label>(form, "analysisSourceDetails").Text);
            form.Close();
        });
    }

    [Fact]
    public void FileSelectionRunsAsynchronouslyAndCanReturnToLiveWithoutPlayingAudio()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var directory = NewTestDirectory();
            try
            {
                var path = Path.Combine(directory, "selection.wav");
                WriteTone(path);
                var workspace = Fixture();
                using var analyzer = new Analyzer(false);
                using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
                form.Show();
                ModernWorkbenchProbe.Drop(form, path);
                Assert.True(Find<Control>(form, "analysisFileSection").Visible);
                Find<NumericUpDown>(form, "analysisSelectionStartSeconds").Value = .25m;
                Find<NumericUpDown>(form, "analysisSelectionDurationSeconds").Value = 1;
                Invoke(form, "RefreshFromWorkspace");
                Find<Button>(form, "analysisAnalyzeSelection").PerformClick();
                var clock = Stopwatch.StartNew();
                while ((bool)typeof(AnalysisWorkbenchForm).GetField("_busy", Hidden).GetValue(form) && clock.ElapsedMilliseconds < 15000)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
                Assert.False((bool)typeof(AnalysisWorkbenchForm).GetField("_busy", Hidden).GetValue(form), "Offline analysis should finish without blocking the UI thread.");
                Assert.False(Find<Label>(form, "analysisError").Visible, Find<Label>(form, "analysisError").Text);
                Assert.True(workspace.IsOffline);
                Assert.NotNull(workspace.Latest);
                Assert.Equal("OFFLINE ANALYSIS", Find<Label>(form, "analysisSourceState").Text);
                Assert.DoesNotContain("Analyzing", Find<Label>(form, "analysisStatus").Text);
                Assert.True(Find<Button>(form, "analysisAnalyzeSelection").Enabled);
                Assert.True(Find<ComboBox>(form, "analysisChannelSelector").Enabled);
                Assert.True(Find<ComboBox>(form, "analysisResponsePreset").Enabled);
                SaveEvidence(form, "workbench-offline-selected-audio.png");
                Find<Button>(form, "analysisResumeLive").PerformClick();
                Assert.False(workspace.IsOffline);
                form.Close();
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public void FileDropUsesTheRealAsyncAnalysisPathAndRejectsMultipleFiles()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var directory = NewTestDirectory();
            try
            {
                var path = Path.Combine(directory, "dropped-selection.wav");
                WriteTone(path);
                var workspace = Fixture();
                using var analyzer = new Analyzer(false);
                using var form = new AnalysisWorkbenchForm(workspace, analyzer) { ShowInTaskbar = false };
                form.Show();
                Find<NumericUpDown>(form, "analysisSelectionStartSeconds").Value = .25m;
                Find<NumericUpDown>(form, "analysisSelectionDurationSeconds").Value = 1;
                var rejected = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { path, path }),
                    0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
                typeof(Control).GetMethod("OnDragEnter", Hidden).Invoke(form, new object[] { rejected });
                Assert.Equal(DragDropEffects.None, rejected.Effect);
                var accepted = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { path }),
                    0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
                typeof(Control).GetMethod("OnDragEnter", Hidden).Invoke(form, new object[] { accepted });
                Assert.Equal(DragDropEffects.Copy, accepted.Effect);
                typeof(Control).GetMethod("OnDragDrop", Hidden).Invoke(form, new object[] { accepted });
                var clock = Stopwatch.StartNew();
                while ((bool)typeof(AnalysisWorkbenchForm).GetField("_busy", Hidden).GetValue(form) && clock.ElapsedMilliseconds < 15000)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
                Assert.False((bool)typeof(AnalysisWorkbenchForm).GetField("_busy", Hidden).GetValue(form));
                Assert.False(Find<Label>(form, "analysisError").Visible, Find<Label>(form, "analysisError").Text);
                Assert.True(workspace.IsOffline);
                Assert.Equal("dropped-selection.wav", Find<Label>(form, "analysisSelectedFile").Text);
                Assert.Equal("OFFLINE ANALYSIS", Find<Label>(form, "analysisSourceState").Text);
                Assert.DoesNotContain("Analyzing", Find<Label>(form, "analysisStatus").Text);
                Assert.True(Find<Button>(form, "analysisAnalyzeSelection").Enabled);
                Assert.True(Find<ComboBox>(form, "analysisChannelSelector").Enabled);
                Assert.True(Find<ComboBox>(form, "analysisResponsePreset").Enabled);
                Assert.Contains(.25.ToString("F2", CultureInfo.CurrentCulture), workspace.Status);
                SaveEvidence(form, "workbench-dropped-audio-selected-range.png");
                form.Close();
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    [Theory]
    [InlineData("Spectrogram", 1)]
    [InlineData("Stereo phase", 2)]
    public void ImportedWorkspaceRestoresNamedViewRangeAndWindowWithinWorkingArea(string view, int expectedTab)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var directory = NewTestDirectory();
            try
            {
                var workspace = Fixture();
                workspace.WorkspaceName = "Quiet high-frequency inspection";
                workspace.PreferredView = view;
                workspace.WindowWidth = 100000;
                workspace.WindowHeight = 100000;
                workspace.Settings.FloorDb = -120;
                workspace.Settings.MinHz = 14000;
                workspace.Settings.MaxHz = 21000;
                workspace.UpdateSettings();
                var path = Path.Combine(directory, "quiet.spectrum-workspace.xml");
                workspace.SaveWorkspace(path);
                var restored = new AnalysisWorkspace();
                restored.LoadWorkspace(path);
                using var analyzer = new Analyzer(false);
                using var form = new AnalysisWorkbenchForm(restored, analyzer) { ShowInTaskbar = false };
                form.Show();
                Pump();
                Reveal(form, "analysisSessionGroup", "analysisSessionPanel");
                Assert.Equal("Quiet high-frequency inspection", Find<TextBox>(form, "analysisWorkspaceName").Text);
                Assert.Equal("-120", Find<ComboBox>(form, "analysisFloorDb").SelectedItem);
                Assert.Equal(expectedTab, Find<TabControl>(form, "analysisPlotTabs").SelectedIndex);
                Assert.Equal(14000, Find<NumericUpDown>(form, "analysisMinimumHz").Value);
                var area = Screen.FromControl(form).WorkingArea;
                Assert.True(form.Width <= area.Width && form.Height <= area.Height);
                form.Close();
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    private static AnalysisWorkspace Fixture()
    {
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        var engine = new BandSpectrumAnalyzer(plan, 2);
        var samples = Signals.StereoSine(engine.RequiredFrames, 48000, plan[81].CenterHz, 1e-5, 1e-5);
        for (var frame = 0; frame < engine.RequiredFrames; frame++)
        {
            var audible = (float)(.05 * Math.Sin(2 * Math.PI * 997 * frame / 48000));
            samples[frame * 2] += audible;
            samples[frame * 2 + 1] += audible;
        }
        var power = new double[83]; var left = new double[83]; var right = new double[83];
        engine.Analyze(samples, engine.RequiredFrames, power, left, right);
        var workspace = new AnalysisWorkspace();
        workspace.Accept(new AnalysisFrame(plan, 2, engine.RequiredFrames, engine.RequiredFrames, samples, power, left, right));
        return workspace;
    }

    private static void Inspect(AnalysisPlotControl plot, double frequency, AnalysisWorkspace workspace)
    {
        var x = 62 + (int)Math.Round(Math.Log(frequency / workspace.Settings.MinHz) /
            Math.Log(workspace.Settings.MaxHz / workspace.Settings.MinHz) * (plot.Width - 82));
        typeof(AnalysisPlotControl).GetMethod("OnMouseMove", Hidden).Invoke(plot, new object[] { new MouseEventArgs(MouseButtons.None, 0, x, 60, 0) });
    }

    private static void Pin(AnalysisPlotControl plot, double frequency, AnalysisWorkspace workspace)
    {
        var x = 62 + (int)Math.Round(Math.Log(frequency / workspace.Settings.MinHz) /
            Math.Log(workspace.Settings.MaxHz / workspace.Settings.MinHz) * (plot.Width - 82));
        typeof(AnalysisPlotControl).GetMethod("OnMouseDown", Hidden).Invoke(plot, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, 60, 0) });
    }

    private static void Invoke(object instance, string method) => instance.GetType().GetMethod(method, Hidden).Invoke(instance, null);
    internal static void Reveal(Control form, string button, string panel)
    {
        if (!Find<Control>(form, panel).Visible) Find<Button>(form, button).PerformClick();
        Pump();
        Assert.True(Find<Control>(form, panel).Visible, panel + " must open through its visible command.");
    }
    private static T Find<T>(Control root, string name) where T : Control => (T)Descendants(root).Single(c => c.Name == name);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Pump() { for (var i = 0; i < 3; i++) { Application.DoEvents(); Thread.Sleep(10); } }
    private static string NewTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SpectrumWorkbenchTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static void WriteTone(string path)
    {
        const int rate = 48000, frames = rate * 2, channels = 2;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * channels * sizeof(short));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
        writer.Write((short)channels); writer.Write(rate); writer.Write(rate * channels * sizeof(short));
        writer.Write((short)(channels * sizeof(short))); writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * channels * sizeof(short));
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (short)(16000 * Math.Sin(2 * Math.PI * 997 * frame / rate));
            writer.Write(value); writer.Write(value);
        }
    }
    private static void SaveEvidence(Form form, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("SPECTRUM_WORKBENCH_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, filename), ImageFormat.Png);
    }
}
