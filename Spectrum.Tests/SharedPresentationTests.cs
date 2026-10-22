using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Xunit;

namespace Spectrum.Tests;

public sealed class SharedPresentationTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void UnmeasuredCoverageRemainsDistinctFromMeasuredQuietPowerInEveryAppearance(string appearance)
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            form.ApplyPresentation(BarColorThemes.ApplyAppearance(BarColorThemes.Studio, appearance).Ui);
            var plot = Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
            plot.SetFrequencyRange(1, 100000); ModernWorkbenchProbe.Refresh(form);
            ModernWorkbenchProbe.AssertUnmeasuredShading(plot, workspace);
            ModernWorkbenchProbe.SaveEvidence(form, "unmeasured-coverage-" + appearance.Replace(" ", "-") + ".png");
        });

    [Fact]
    public void SearchCueSurvivesHandleRecreationWithoutChangingTheQuery()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            Application.EnableVisualStyles();
            using var owner = new Form { ShowInTaskbar = false };
            using var search = new TextBox();
            owner.Controls.Add(search);
            SharedPresentation.SetCueText(search, "Search views…");
            Assert.False(search.IsHandleCreated);
            owner.Show(); Application.DoEvents();
            string ReadCue()
            {
                var text = new StringBuilder(200);
                SendMessage(search.Handle, 0x1502, text, (IntPtr)text.Capacity);
                return text.ToString();
            }
            Assert.Equal("Search views…", ReadCue()); Assert.Equal("", search.Text);
            search.Text = "stereo"; SharedPresentation.SetCueText(search, "Search names, notes or sources…");
            typeof(Control).GetMethod("RecreateHandle", Hidden).Invoke(search, null);
            Assert.Equal("Search names, notes or sources…", ReadCue()); Assert.Equal("stereo", search.Text);
        });

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, StringBuilder wParam, IntPtr lParam);


    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    [InlineData("System")]
    public void AppearanceUpdatesWorkbenchAndRotationChromeWithoutChangingMeasurements(string appearance)
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            Application.EnableVisualStyles();
            var workspace = ModernWorkbenchProbe.Fixture();
            var raw = (double[])workspace.Latest.Power.Clone();
            var curve = (double[])workspace.DisplayPower.Clone();
            var theme = BarColorThemes.ApplyAppearance(BarColorThemes.Studio, appearance);
            using var form = new AnalysisWorkbenchForm(workspace, null);
            form.ApplyPresentation(theme.Ui);
            form.Show(); ModernWorkbenchProbe.Pump();
            Assert.True(Find<Control>(form, "analysisSettingsRail").Visible);
            Assert.Equal(theme.Ui.Canvas, form.BackColor);
            Assert.Equal(theme.Ui.ControlSurface, Find<ComboBox>(form, "analysisFloorDb").BackColor);
            Assert.Equal(DrawMode.OwnerDrawFixed, Find<ComboBox>(form, "analysisFloorDb").DrawMode);
            var floor = Find<ComboBox>(form, "analysisFloorDb");
            using (var image = new Bitmap(floor.Width, floor.Height))
            {
                floor.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                var filled = 0; var count = 0;
                for (var y = 3; y < image.Height - 3; y++) for (var x = 4; x < image.Width - 24; x++)
                { count++; if (image.GetPixel(x, y).ToArgb() == theme.Ui.ControlSurface.ToArgb()) filled++; }
                Assert.True(count > 0 && filled > count * .5, appearance + " combo readout should use its supplied surface with native visual styles enabled.");
            }
            Assert.Equal(theme.Ui.PrimaryText, Find<Label>(form, "analysisMetric_samplePeak").ForeColor);
            Assert.Equal(theme.Ui.VisualizationSurface, Find<AnalysisPlotControl>(form, "analysisSpectrumPlot").BackColor);
            using var rotation = new RotationSettingsForm(true, 7, theme);
            Assert.Equal(theme.Ui.Canvas, rotation.BackColor);
            Assert.Equal(theme.Ui.ControlSurface, Find<NumericUpDown>(rotation, "rotationInterval").BackColor);
            Assert.True(rotation.RandomEnabled); Assert.Equal(7, rotation.IntervalMinutes);
            ModernWorkbenchProbe.SaveEvidence(form, "shared-appearance-" + appearance.Replace(" ", "-") + ".png");
            Assert.Equal(raw, workspace.Latest.Power); Assert.Equal(curve, workspace.DisplayPower);
        });

    [Fact]
    public void EmbeddedAnalyzeAndComparisonKeepUsefulPlotsAndExactCapturedInspection()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = ModernWorkbenchProbe.Fixture(); workspace.CaptureReference();
            using var host = new Form { ClientSize = new Size(1200, 340) };
            using var form = new AnalysisWorkbenchForm(workspace, null);
            form.SetEmbedded(true); form.TopLevel = false; form.FormBorderStyle = FormBorderStyle.None; form.Dock = DockStyle.Fill;
            host.Controls.Add(form); form.Show(); host.Show(); ModernWorkbenchProbe.Pump();
            try
            {
                Assert.False(Find<Control>(form, "analysisHeader").Visible);
                Assert.False(Find<Label>(form, "analysisStatus").Visible);
                var spectrum = Find<AnalysisPlotControl>(form, "analysisSpectrumPlot");
                ModernWorkbenchProbe.SaveEvidence(form, "shared-embedded-analyze.png");
                Assert.True(spectrum.Compact); Assert.True(spectrum.Height >= 110, "Embedded Analyze plot height: " + spectrum.Height);
                Find<TabControl>(form, "analysisPlotTabs").SelectedIndex = 2; ModernWorkbenchProbe.Pump();
                Assert.True(Find<AnalysisPlotControl>(form, "analysisPhasePlot").Visible);
                ModernWorkbenchProbe.SaveEvidence(form, "shared-embedded-phase.png");
                Find<TabControl>(form, "analysisPlotTabs").SelectedIndex = 0;
                form.SelectWorkspaceView("Compare"); ModernWorkbenchProbe.Pump();
                var current = Find<AnalysisPlotControl>(form, "analysisComparisonCurrentPlot");
                var reference = Find<AnalysisPlotControl>(form, "analysisComparisonReferencePlot");
                ModernWorkbenchProbe.SaveEvidence(form, "shared-embedded-compare.png");
                Assert.True(current.Visible && reference.Visible);
                Assert.True(current.Height >= 110 && reference.Height >= 110, "Embedded Compare plot heights: " + current.Height + "/" + reference.Height);
                Assert.True(Find<Button>(form, "analysisCaptureReference").Visible);
                Assert.True(Find<Button>(form, "analysisClearReference").Visible);
                Assert.True(reference.ReferenceOnly); Assert.True(current.ShowReference);
                ModernWorkbenchProbe.SaveEvidence(form, "shared-embedded-compare.png");
                var height = current.Height;
                Find<Button>(form, "analysisDetailsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
                Assert.True(workspace.DetailsVisible);
                Assert.Equal(height, current.Height);
                var detailWindow = Application.OpenForms.Cast<Form>().Single(window => window.Name == "analysisEmbeddedDetailsWindow");
                Assert.True(detailWindow.Visible);
                Find<Button>(form, "analysisDetailsToggle").PerformClick(); ModernWorkbenchProbe.Pump();
                Assert.False(detailWindow.Visible);
                var band = 80; var plotLeft = 47; var plotWidth = reference.Width - 61;
                var x = plotLeft + (int)Math.Round(Math.Log(workspace.Latest.Plan[band].CenterHz / workspace.Settings.MinHz) /
                    Math.Log(workspace.Settings.MaxHz / workspace.Settings.MinHz) * plotWidth);
                typeof(AnalysisPlotControl).GetMethod("OnMouseMove", Hidden).Invoke(reference,
                    new object[] { new MouseEventArgs(MouseButtons.None, 0, x, 40, 0) });
                Assert.Equal(band + 1, reference.Inspection.BandNumber);
                Assert.Equal(10 * Math.Log10(workspace.ReferencePower[band]), reference.Inspection.LevelDb, 8);
                Assert.True(double.IsNaN(reference.Inspection.PeakDb));
                Assert.Contains("Captured reference curve", reference.InspectionText);
                Assert.Contains("Reference inspection", Find<Label>(form, "analysisBandInspector").Text);
                workspace.SetUnavailable("Capture gap"); reference.RefreshInspection();
                Assert.False(reference.Inspection.IsUnavailable);
                Assert.True(double.IsNaN(reference.Inspection.DeltaDb));
                typeof(AnalysisPlotControl).GetMethod("OnMouseMove", Hidden).Invoke(current,
                    new object[] { new MouseEventArgs(MouseButtons.None, 0, x, 40, 0) });
                ModernWorkbenchProbe.Refresh(form);
                Assert.Contains("Current inspection", Find<Label>(form, "analysisBandInspector").Text);
                Assert.DoesNotContain("Reference inspection", Find<Label>(form, "analysisBandInspector").Text);
                form.SelectWorkspaceView("Analyze"); ModernWorkbenchProbe.Pump(); Assert.True(spectrum.Visible);
                form.SetEmbedded(false); Assert.False(spectrum.Compact);
            }
            finally { host.Close(); }
        });

    [Fact]
    public void NamedLayoutsAndKeyboardResizePersistWithoutChangingAnalysis()
        => ModernWorkbenchProbe.Shown((form, workspace) =>
        {
            var raw = (double[])workspace.Latest.Power.Clone();
            var layouts = Find<ComboBox>(form, "analysisLayoutSelector");
            layouts.SelectedItem = "Compact"; ModernWorkbenchProbe.Pump();
            Assert.False(Find<Control>(form, "analysisSettingsRail").Visible);
            layouts.SelectedItem = "Measurement"; ModernWorkbenchProbe.Pump();
            Assert.True(Find<Control>(form, "analysisMeasurementRail").Visible);
            Assert.True(Find<Control>(form, "analysisSettingsRail").Visible);
            var resize = Find<Button>(form, "analysisSettingsResizeHandle"); var oldWidth = workspace.SettingsWidth;
            typeof(Control).GetMethod("OnKeyDown", Hidden).Invoke(resize, new object[] { new KeyEventArgs(Keys.Right) });
            Assert.Equal(oldWidth + 10, workspace.SettingsWidth);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                workspace.SaveWorkspace(path); var restored = new AnalysisWorkspace(); restored.LoadWorkspace(path);
                Assert.Equal("Measurement", restored.LayoutName); Assert.True(restored.SettingsVisible); Assert.True(restored.DetailsVisible);
                Assert.Equal(workspace.SettingsWidth, restored.SettingsWidth);
            }
            finally { File.Delete(path); }
            Assert.Equal(raw, workspace.Latest.Power);
        });

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Find<T>(Control root, string name) where T : Control
        => (T)ModernWorkbenchProbe.Descendants(root).Single(control => control.Name == name);
}
