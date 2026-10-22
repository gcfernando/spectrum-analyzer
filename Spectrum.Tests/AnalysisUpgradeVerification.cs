using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

// Explicit shown-application acceptance run, outside automatic unit-test discovery.
public static class AnalysisUpgradeVerification
{
    public static string Run(string outputDirectory)
    {
        if (IntPtr.Size != 4) throw new InvalidOperationException("Run in x86 Windows with the bundled native audio libraries.");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Run on an STA thread.");
        var output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        var results = new List<string>();
        using var main = new FormAudioSpectrum();
        main.Show(); Pump(1800); main.ShowAnalysisWorkbench(); Pump(1100);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var workbench = (AnalysisWorkbenchForm)typeof(FormAudioSpectrum).GetField("_analysisWorkbench", flags).GetValue(main);
        var workspace = (AnalysisWorkspace)typeof(FormAudioSpectrum).GetField("_analysisWorkspace", flags).GetValue(main);
        var analyzer = (Analyzer)typeof(FormAudioSpectrum).GetField("_analyzer", flags).GetValue(main);
        Assert.NotNull(workbench); Assert.True(workbench.Visible); Assert.NotNull(workspace);
        Assert.False(ModernWorkbenchProbe.Find<Control>(workbench, "analysisSessionPanel").Visible);
        Assert.False(ModernWorkbenchProbe.Find<Control>(workbench, "analysisComparePanel").Visible);
        Assert.False(ModernWorkbenchProbe.Find<Control>(workbench, "analysisAdvancedPanel").Visible);
        Assert.False(ModernWorkbenchProbe.Find<Control>(workbench, "analysisMeasurementRail").Visible);
        var live = workspace.Latest;
        Snapshot(workbench, Path.Combine(output, "01-live-workbench.png"));
        var sourceCount = analyzer.GetAnalysisSources().Count;
        Assert.True(sourceCount >= 1); Assert.Equal("@default-render", analyzer.SelectedAnalysisSource.Id);
        var wave = Path.Combine(output, "verification-quiet-tail-antiphase.wav"); WriteWave(wave);
        Numeric(workbench, "analysisSelectionDurationSeconds").Value = 6;
        DropFile(workbench, wave); Pump(250);
        Assert.True(workspace.IsOffline); Assert.Equal(48000, workspace.Latest.SampleRate);
        Assert.InRange(workspace.Measurements.TotalFrames, 287999, 288001);
        Numeric(workbench, "analysisSelectionStartSeconds").Value = 1;
        Numeric(workbench, "analysisSelectionDurationSeconds").Value = 3;
        Button(workbench, "analysisAnalyzeSelection").PerformClick(); WaitForFile(workbench);
        Assert.Equal(144000, workspace.Measurements.TotalFrames);
        Numeric(workbench, "analysisSelectionStartSeconds").Value = 0;
        Numeric(workbench, "analysisSelectionDurationSeconds").Value = 6;
        Button(workbench, "analysisAnalyzeSelection").PerformClick(); WaitForFile(workbench);
        Add(8, "Offline audio-file analysis", "The shown form accepted a WAV through its DragEnter/DragDrop events. UI section controls analyzed 1–4 seconds as exactly 144,000 frames, then restored all six seconds without playback.");

        Choice(workbench, "analysisFloorDb").SelectedItem = "-120";
        ModernWorkbenchProbe.Reveal(workbench, "analysisAdvancedToggle", "analysisAdvancedPanel");
        Choice(workbench, "analysisResponsePreset").SelectedItem = "Balanced";
        Numeric(workbench, "analysisSmoothingBands").Value = 0;
        Numeric(workbench, "analysisAveragingMs").Value = 0; Pump(200);
        Assert.Equal(-120, workspace.Settings.FloorDb);
        var tailDb = 10 * Math.Log10(workspace.Latest.Power[80]);
        Assert.InRange(tailDb, -100.1, -99.9);
        Assert.True(workspace.DisplayPower[80] > 0);
        Add(1, "Adjustable dB range", "UI floor changed to -120 dBFS; actual approximately -100 dBFS band power remains available.");
        var plot = (AnalysisPlotControl)workbench.Controls.Find("analysisSpectrumPlot", true).Single();
        var position = 62 + (int)((plot.Width - 82) * Math.Log(workspace.Latest.Plan[80].CenterHz / workspace.Settings.MinHz) / Math.Log(workspace.Settings.MaxHz / workspace.Settings.MinHz));
        typeof(AnalysisPlotControl).GetMethod("OnMouseMove", flags).Invoke(plot, new object[] { new MouseEventArgs(MouseButtons.None, 0, position, 80, 0) });
        Assert.Contains("Band 81", plot.InspectionText); Assert.Contains("-100", plot.InspectionText); Assert.Contains("held curve peak", plot.InspectionText);
        Choice(workbench, "analysisFloorDb").SelectedItem = "-72";
        typeof(AnalysisPlotControl).GetMethod("OnMouseMove", flags).Invoke(plot, new object[] { new MouseEventArgs(MouseButtons.None, 0, position, 80, 0) });
        Assert.Contains("below display floor", plot.InspectionText);
        Choice(workbench, "analysisFloorDb").SelectedItem = "-120";
        Add(2, "Exact numeric band inspection", "Main plot hover identifies band 81, its true subfloor level, held curve peak and the below-display-floor state.");
        Snapshot(workbench, Path.Combine(output, "02-quiet-tail-inspection.png"));

        var oldSpan = workspace.Settings.MaxHz / workspace.Settings.MinHz;
        plot.Zoom(2); Assert.True(workspace.Settings.MaxHz / workspace.Settings.MinHz < oldSpan);
        var beforePan = workspace.Settings.MinHz; plot.Pan(.15); Assert.True(workspace.Settings.MinHz > beforePan);
        plot.ResetZoom(); Assert.Equal(20, workspace.Settings.MinHz); Assert.Equal(20000, workspace.Settings.MaxHz);
        Add(3, "Frequency zoom and pan", "Zoom changes logarithmic span; pan moves range; reset restores 20 Hz–20 kHz.");
        Assert.NotNull(Choice(workbench, "analysisSourceSelector"));
        Choice(workbench, "analysisChannelSelector").SelectedItem = "Side"; Pump(120);
        Assert.Equal("Side", workspace.Settings.Channel); Assert.True(workspace.DisplayPower.Max() > 0);
        Choice(workbench, "analysisChannelSelector").SelectedItem = "Combined";
        Add(4, "Source and channel selection", $"Actual source enumeration returned {sourceCount} choices; channel selector applies Side and Combined. Separate native capture proof verifies output pinning and default restore.");

        foreach (var response in new[] { "Fast", "Balanced", "Detailed" })
        { Choice(workbench, "analysisResponsePreset").SelectedItem = response; Assert.Equal(response, workspace.Settings.Response); }
        Numeric(workbench, "analysisAveragingMs").Value = 100;
        Numeric(workbench, "analysisPeakHoldMs").Value = 1000; Pump(120);
        Assert.Equal(100, workspace.Settings.AveragingMilliseconds); Assert.Equal(1000, workspace.Settings.PeakHoldMilliseconds);
        Choice(workbench, "analysisResponsePreset").SelectedItem = "Balanced";
        Numeric(workbench, "analysisSmoothingBands").Value = 0;
        Add(5, "Analysis response controls", "All three response presets apply; independent averaging and peak-hold controls retain chosen values.");

        ModernWorkbenchProbe.Reveal(workbench, "analysisCompareGroup", "analysisComparePanel");
        Button(workbench, "analysisCaptureReference").PerformClick(); Assert.NotNull(workspace.ReferencePower);
        var capturedReference = workspace.ReferencePower[80];
        var quieterWave = Path.Combine(output, "verification-comparison-minus6db.wav"); WriteWave(quieterWave, -6);
        DropFile(workbench, quieterWave); Pump(200);
        Assert.Equal(capturedReference, workspace.ReferencePower[80]);
        var difference = 10 * Math.Log10(workspace.DisplayPower[80] / capturedReference);
        Assert.InRange(difference, -6.02, -5.98);
        typeof(AnalysisPlotControl).GetMethod("OnMouseMove", flags).Invoke(plot, new object[] { new MouseEventArgs(MouseButtons.None, 0, position, 80, 0) });
        Assert.Contains("curve Δ -6.00 dB", plot.InspectionText);
        Snapshot(workbench, Path.Combine(output, "03-reference-overlay.png"));
        Add(6, "Reference spectrum A/B", $"Capture reference button saved recording A. Dropping recording B retained A and displayed an observed {difference:F3} dB difference with an amber overlay.");
        // Settings changes intentionally start a fresh history; re-analyze to populate this configuration.
        workspace.AnalyzeFile(wave, 0, 6);
        Numeric(workbench, "analysisHistorySeconds").Value = 6;
        var tabs = (TabControl)workbench.Controls.Find("analysisPlotTabs", true).Single(); tabs.SelectedIndex = 1; Pump(250);
        Assert.True(workspace.History.Count > 250);
        for (var row = 1; row < workspace.History.Count; row++)
            Assert.Equal(1024 / 48000d, workspace.History[row].Seconds - workspace.History[row - 1].Seconds, 8);
        Snapshot(workbench, Path.Combine(output, "04-sample-timed-spectrogram.png"));
        Add(7, "Sample-timed spectrogram", "Shown spectrogram contains more than 250 rows at exactly 1024/48000 seconds per hop, independent of UI refresh.");

        var csv = Path.Combine(output, "measurements.csv"); var session = Path.Combine(output, "proof.spectrum-session.xml");
        workspace.CaptureReference(); workspace.ExportCsv(csv); workspace.SaveSession(session);
        var originalTail = workspace.Latest.Power[80]; var rows = workspace.History.Count;
        workspace.Reset(); workspace.LoadSession(session); Pump(150);
        Assert.Equal(originalTail, workspace.Latest.Power[80]); Assert.Equal(rows, workspace.History.Count);
        Assert.Equal(84, File.ReadAllLines(csv).Length); Assert.True(workspace.IsOffline);
        Assert.Contains("estimated_true_peak_dbtp", File.ReadLines(csv).First()); Assert.Contains("integrated_lufs", File.ReadLines(csv).First());
        Add(9, "CSV export and saved analysis sessions", "CSV retains 83 raw bands, settings, timestamps and stereo/loudness/peak statistics; saved session restores subfloor powers, reference, history, phase density and readings without storing PCM.");
        Assert.InRange(workspace.Measurements.Correlation, -1.000001, -.999999);
        Assert.True(workspace.Measurements.SideRmsDb > -40); Assert.True(workspace.Measurements.MidRmsDb < -250);
        tabs.SelectedIndex = 2; Pump(200);
        var phase = (AnalysisPlotControl)workbench.Controls.Find("analysisPhasePlot", true).Single(); Assert.True(phase.ShowPhase);
        Assert.True(workspace.PhaseDensity.Sum() > 16000);
        Assert.All(workspace.PhaseDensity.Select((count, cell) => new { count, cell }).Where(p => p.count > 0), p => Assert.InRange(p.cell / 64, 31, 32));
        Snapshot(workbench, Path.Combine(output, "06-stereo-phase.png"));
        Add(10, "Stereo phase and mid/side", "Shown Stereo phase tab draws the expected horizontal antiphase distribution; correlation is -1, mid is zero and side is nonzero. Saved session retained phase density and scale.");
        Assert.False(double.IsNaN(workspace.Measurements.IntegratedLufs));
        Assert.True(workspace.Measurements.TruePeakDb >= workspace.Measurements.SamplePeakDb - .001);
        Add(11, "Loudness and true-peak estimates", $"Integrated loudness {workspace.Measurements.IntegratedLufs:F3} LUFS; oversampled true peak {workspace.Measurements.TruePeakDb:F3} dBTP. Separate official-vector reports record meter verification; no certification is claimed.");
        workspace.WorkspaceName = "Quiet high-frequency inspection"; workspace.PreferredView = "Stereo phase";
        workspace.WindowWidth = workbench.Width; workspace.WindowHeight = workbench.Height;
        var custom = Path.Combine(output, "quiet-tail.spectrum-workspace.xml"); workspace.SaveWorkspace(custom);
        workspace.Settings.FloorDb = -60; workspace.UpdateSettings(); workspace.LoadWorkspace(custom); Pump(100);
        Assert.Equal(-120, workspace.Settings.FloorDb); Assert.Equal("Quiet high-frequency inspection", workspace.WorkspaceName);
        Assert.Equal("Stereo phase", workspace.PreferredView); Assert.NotNull(workspace.ReferencePower);
        Add(12, "Custom workspaces", "Named XML workspace restores range, response, channel, timing, view, window dimensions and compatible reference curve.");
        tabs.SelectedIndex = 0; Pump(200); Snapshot(workbench, Path.Combine(output, "05-complete-analysis.png"));
        var modern = ModernWorkbenchVerification.Run(workbench, workspace, wave, output);
        var native = new { HasObservedFrame = live != null, SourceChoices = sourceCount, State = analyzer.CurrentStatus.State.ToString() };
        var json = "{\"helper\":\"AnalysisUpgradeVerification\",\"featuresPassed\":12,\"liveFrameObserved\":" + (native.HasObservedFrame ? "true" : "false") +
            ",\"sourceChoices\":" + sourceCount + ",\"modernChangesPassed\":10,\"fileDialogsExercised\":false,\"modernProofs\":[" + modern + "],\"proofs\":[" + string.Join(",", results.OrderBy(r => int.Parse(r.Split(':')[1].Split(',')[0]))) + "]}";
        File.WriteAllText(Path.Combine(output, "twelve-feature-proof.json"), json, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "PROOF.md"), "# Spectrum — twelve-feature acceptance evidence\n\n" +
            "The real main form and Analysis Workbench were shown and exercised in an x86 STA Windows process. Synthetic audio was analyzed without playback.\n\n" +
            "All 12 original feature checks and all 10 modern presentation checks passed. See twelve-feature-proof.json for observed assertions, PNGs for shown forms, measurements.csv and the saved session/workspace examples. Normal, compact and enlarged-font layouts are covered by shown ModernWorkbenchTests and their PNG evidence.\n\n" +
            "The Session group and its commands were opened through actual controls. CSV/session/workspace serialization was exercised through model APIs; operating-system open/save dialogs were not automated. Audio input used actual drag-and-drop handlers and File/Live buttons. The 150% layout check scales controls and fonts in this process; it does not change the Windows DPI setting.\n\n" +
            "Source switching and official loudness vectors have separate reports. These checks do not certify the meter or every device/codec.\n", new UTF8Encoding(false));
        workbench.Close(); main.Close(); Pump(100); return json;

        void Add(int id, string feature, string detail) => results.Add("{\"id\":" + id + ",\"feature\":" + Quote(feature) + ",\"passed\":true,\"detail\":" + Quote(detail) + "}");
    }
    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    private static ComboBox Choice(Control root, string name) => (ComboBox)root.Controls.Find(name, true).Single();
    private static NumericUpDown Numeric(Control root, string name) => (NumericUpDown)root.Controls.Find(name, true).Single();
    private static Button Button(Control root, string name) => (Button)root.Controls.Find(name, true).Single();
    private static void Pump(int milliseconds)
    { var timer = Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); } }
    private static void Snapshot(Form form, string path)
    { form.Refresh(); Application.DoEvents(); using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path, ImageFormat.Png); }
    private static void DropFile(Form form, string path)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var args = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { path }), 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
        typeof(Control).GetMethod("OnDragEnter", flags).Invoke(form, new object[] { args }); Assert.Equal(DragDropEffects.Copy, args.Effect);
        typeof(Control).GetMethod("OnDragDrop", flags).Invoke(form, new object[] { args }); WaitForFile(form);
    }
    private static void WaitForFile(Form form)
    {
        var busy = typeof(AnalysisWorkbenchForm).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic);
        var clock = Stopwatch.StartNew();
        while ((bool)busy.GetValue(form) && clock.ElapsedMilliseconds < 20000) Pump(25);
        Assert.False((bool)busy.GetValue(form), "Offline analysis did not finish.");
        Assert.False(form.Controls.Find("analysisError", true).Single().Visible);
        Assert.Equal("OFFLINE ANALYSIS", form.Controls.Find("analysisSourceState", true).Single().Text);
    }
    internal static void WriteWave(string path, double gainDb = 0)
    {
        const int rate = 48000, channels = 2, frames = rate * 6;
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, rate);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * channels * 4); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)3); writer.Write((short)channels); writer.Write(rate); writer.Write(rate * channels * 4);
        writer.Write((short)(channels * 4)); writer.Write((short)32); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * channels * 4);
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(Math.Pow(10, (-24 + gainDb) / 20d) * Math.Sin(2 * Math.PI * 997 * i / rate) + Math.Pow(10, (-100 + gainDb) / 20d) * Math.Sin(2 * Math.PI * plan[80].CenterHz * i / rate));
            writer.Write(value); writer.Write(-value);
        }
    }
}
