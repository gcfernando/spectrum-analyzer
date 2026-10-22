using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class MainWorkspaceTests
{
    [Fact]
    public void AppliedInspectorPairsFramesAndFreezeRetainsItsOwnMeasurement()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000); var epoch = new object();
            AnalysisFrame Frame(long end, double power) => new(plan, 2, 64, end, Enumerable.Repeat(.1f, 128).ToArray(),
                Enumerable.Repeat(power, 83).ToArray(), Enumerable.Repeat(power, 83).ToArray(), Enumerable.Repeat(power, 83).ToArray(), epoch);
            void Apply(AnalysisFrame frame, long? audioEnd = null)
            {
                var audio = new AudioVisualizationData();
                audio.UpdateObservedWindow(frame.Samples, 64, 2, 48000, audioEnd ?? frame.EndFrame, frame.EndFrame - 64, 0, epoch);
                Invoke(form, "ReceiveAnalysisFrame", frame);
                Invoke(form, "Spectrum_Change", null, new OnChangeEventArgs(new byte[83], audio) { Plan = plan });
            }
            Apply(Frame(64, 1e-10));
            var original = Get<MainSpectrumSnapshot>(form, "_displayedInspection"); Assert.Equal(64, original.Frame.EndFrame);
            Assert.Equal(1e-10, original.Frame.Power[80]);
            form.SetDisplayFrozen(true); Apply(Frame(128, .1));
            Assert.Same(original, Get<MainSpectrumSnapshot>(form, "_displayedInspection"));
            form.SetDisplayFrozen(false);
            Assert.Equal(128, Get<MainSpectrumSnapshot>(form, "_displayedInspection").Frame.EndFrame);
            Apply(Frame(192, .01), 193); Assert.Null(Get<MainSpectrumSnapshot>(form, "_displayedInspection").Frame);
            Set(form, "_inspectedChannel", "Right"); form.RandomizeVisualSelection(new Random(41));
            Assert.Equal("Combined", Get<string>(form, "_inspectedChannel"));
        });
    [Fact]
    public void CatalogFavoritesAndRecentViewsPersistAndRejectUnknownModes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Spectrum-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "views.xml"); var catalog = new VisualizationCatalogPreferences(path);
            Assert.False(Directory.Exists(directory));
            catalog.ToggleFavorite("Bricks"); catalog.RecordRecent("Contour"); catalog.RecordRecent("Bricks"); catalog.RecordRecent("Contour");
            var loaded = new VisualizationCatalogPreferences(path);
            Assert.True(loaded.IsFavorite("Bricks")); Assert.Equal(new[] { "Contour", "Bricks" }, loaded.Recent);
            Assert.Throws<ArgumentException>(() => loaded.ToggleFavorite("Imaginary spectrum"));
            File.WriteAllText(path, "<broken"); var invalid = new VisualizationCatalogPreferences(path); invalid.RecordRecent("Spectrum");
            Assert.NotNull(invalid.Error); Assert.Equal("<broken", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void OptionalLayoutMetadataIsBackwardCompatibleAndInvalidValuesDoNotPartiallyApply()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = ModernWorkbenchProbe.Fixture(); source.LayoutName = "Measurement"; source.SettingsWidth = 310;
            source.SettingsVisible = false; source.DetailsVisible = true; source.SaveWorkspace(path);
            var loaded = new AnalysisWorkspace(); loaded.LoadWorkspace(path);
            Assert.Equal("Measurement", loaded.LayoutName); Assert.Equal(310, loaded.SettingsWidth);
            Assert.False(loaded.SettingsVisible); Assert.True(loaded.DetailsVisible);
            var xml = XElement.Load(path); var settings = xml.Element("settings");
            foreach (var name in new[] { "layout", "settingsWidth", "settingsVisible", "detailsVisible" }) settings.Attribute(name).Remove();
            xml.Save(path); loaded.LoadWorkspace(path);
            Assert.Equal("Studio", loaded.LayoutName); Assert.Equal(238, loaded.SettingsWidth); Assert.True(loaded.SettingsVisible); Assert.False(loaded.DetailsVisible);
            settings.SetAttributeValue("floor", -120); settings.SetAttributeValue("settingsVisible", "invalid"); xml.Save(path);
            var prior = loaded.Settings.FloorDb;
            Assert.Throws<FormatException>(() => loaded.LoadWorkspace(path)); Assert.Equal(prior, loaded.Settings.FloorDb);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StoredReferenceChecksAppliedSettingsAndCopiesOnlyCompatibleMeasurements()
    {
        var workspace = ModernWorkbenchProbe.Fixture(); var frame = workspace.Latest;
        var power = (double[])workspace.DisplayPower.Clone(); var expected = (double[])power.Clone();
        workspace.Settings.Channel = "Right"; // A pending UI edit must not relabel the applied measurement.
        workspace.ApplyStoredReference(frame.Plan, power, "Reference A", DateTime.UtcNow, "Combined", workspace.Settings.Response);
        power[0] = 0; Assert.Equal(expected, workspace.ReferencePower);
        Assert.Throws<InvalidOperationException>(() => workspace.ApplyStoredReference(frame.Plan, power, "Wrong channel", null, "Right", workspace.Settings.Response));
        Assert.Equal("Reference A", workspace.ReferenceName); Assert.Equal(expected, workspace.ReferencePower);
        var wrong = BandPlan.CreateLogarithmic(24, 20, 20000, 48000);
        Assert.Throws<InvalidOperationException>(() => workspace.ApplyStoredReference(wrong, new double[24], "Wrong bands", null, "Combined", workspace.Settings.Response));
        power[0] = double.NaN;
        Assert.Throws<ArgumentException>(() => workspace.ApplyStoredReference(frame.Plan, power, "Bad", null, "Combined", workspace.Settings.Response));
        Assert.Equal(expected, workspace.ReferencePower);
    }

    [Fact]
    public void MainNavigationPreservesOneWorkspaceAllModesAndPopoutStateInShownApplication()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            Application.EnableVisualStyles();
            using var form = CombinedRandomizationTests.CreateForm();
            form.Show(); ModernWorkbenchProbe.Pump(250);
            Assert.Equal("Spectrum", form.Text);
            var directory = Path.Combine(Path.GetTempPath(), "Spectrum-main-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var bounds = form.Bounds; var workspace = ModernWorkbenchProbe.Fixture(); workspace.CaptureReference();
                Invoke(form, "ShowMainSourceMenu"); ModernWorkbenchProbe.Pump();
                var sourceMenu = Get<ContextMenuStrip>(form, "_mainSourceMenu"); Assert.True(sourceMenu.Visible);
                Assert.True(PostMessage(sourceMenu.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero)); ModernWorkbenchProbe.Pump();
                Assert.False(sourceMenu.Visible); Assert.False(sourceMenu.IsDisposed);
                Invoke(form, "ShowMainSourceMenu"); ModernWorkbenchProbe.Pump(); Assert.Same(sourceMenu, Get<ContextMenuStrip>(form, "_mainSourceMenu"));
                sourceMenu.Close(); Assert.Equal(bounds, form.Bounds);
                var session = Path.Combine(directory, "fixture.spectrum-session.xml"); workspace.SaveSession(session); workspace.LoadSession(session);
                Set(form, "_analysisWorkspace", workspace);
                var host = Get<Panel>(form, "_workspaceHost");
                var library = new SessionLibraryControl(workspace, Path.Combine(directory, "library")) { Dock = DockStyle.Fill };
                Set(form, "_sessionLibrary", library); host.Controls.Add(library);
                Snapshot(form, "01-main-live");
                form.NavigateWorkspace("Analyze"); ModernWorkbenchProbe.Pump();
                var workbench = Get<AnalysisWorkbenchForm>(form, "_analysisWorkbench");
                Assert.False(workbench.TopLevel); Assert.Same(workspace, Get<AnalysisWorkspace>(form, "_analysisWorkspace"));
                Assert.True(workbench.Visible); Assert.True(workbench.Controls.Find("analysisSpectrumPlot", true)[0].Height >= 110);
                Assert.All(Get<VerticalProgressBar[]>(form, "_progressBars"), bar => Assert.False(bar.Visible));
                Assert.False(Get<AdvancedVisualizationControl>(form, "_advancedVisualizer").Visible);
                Snapshot(form, "02-main-analyze");
                form.NavigateWorkspace("Compare"); ModernWorkbenchProbe.Pump();
                Assert.Same(workbench, Get<AnalysisWorkbenchForm>(form, "_analysisWorkbench"));
                Assert.True(workbench.Controls.Find("analysisComparisonReferencePlot", true)[0].Visible);
                Snapshot(form, "03-main-compare");
                form.NavigateWorkspace("Sessions"); ModernWorkbenchProbe.Pump(); Assert.True(library.Visible);
                Assert.Same(workspace, Get<AnalysisWorkspace>(form, "_analysisWorkspace"));
                Snapshot(form, "04-main-sessions");
                form.NavigateWorkspace("Analyze"); form.ShowAnalysisWorkbench(); ModernWorkbenchProbe.Pump();
                Assert.Same(workbench, Get<AnalysisWorkbenchForm>(form, "_analysisWorkbench")); Assert.True(workbench.TopLevel && workbench.Visible);
                Assert.Same(workspace, Get<AnalysisWorkspace>(form, "_analysisWorkspace")); Snapshot(workbench, "05-analysis-popout");
                form.NavigateWorkspace("Compare"); ModernWorkbenchProbe.Pump(); Assert.False(workbench.TopLevel);
                foreach (var appearance in new[] { "Dark", "Light", "High Contrast", "System" })
                {
                    form.SetAppearance(appearance); form.NavigateWorkspace("Visualize"); ModernWorkbenchProbe.Pump();
                    Assert.Equal("Spectrum", form.Text);
                    Assert.Equal(bounds, form.Bounds); Snapshot(form, "06-main-" + appearance.Replace(" ", "-"));
                }
                var mode = Get<ComboBox>(form, "_modeSelector");
                foreach (var name in FormAudioSpectrum.VisualModeNames) { mode.SelectedItem = name; ModernWorkbenchProbe.Pump(12); Assert.Equal(bounds, form.Bounds); }
                Assert.Equal(21, mode.Items.Count);
                using var browser = form.CreateVisualizationBrowser(); browser.Show(form); ModernWorkbenchProbe.Pump();
                var list = (ListBox)browser.Controls.Find("visualizationResults", true)[0]; Assert.Equal(21, list.Items.Count);
                var search = (TextBox)browser.Controls.Find("visualizationSearch", true)[0]; search.Text = "Stereo"; ModernWorkbenchProbe.Pump(); Assert.True(list.Items.Count > 0 && list.Items.Count < 21);
                Snapshot(browser, "07-view-browser"); browser.Close();
                Assert.Equal(bounds, form.Bounds);
                form.NavigateWorkspace("Analyze");
                using (var openFromAnalysis = form.CreateVisualizationBrowser())
                {
                    openFromAnalysis.Show(form); ModernWorkbenchProbe.Pump();
                    ((Button)openFromAnalysis.Controls.Find("visualizationOpen", true)[0]).PerformClick();
                    Assert.Equal("Visualize", Get<string>(form, "_workspaceView")); openFromAnalysis.Close();
                }
                form.ClientSize = new Size(900, 250); form.NavigateWorkspace("Analyze"); ModernWorkbenchProbe.Pump();
                Assert.True(form.Controls.Find("compactAnalysisNotice", true)[0].Visible);
                Assert.True(form.Controls.Find("compactOpenAnalysis", true)[0].Visible);
                form.Bounds = bounds; ModernWorkbenchProbe.Pump(); Assert.True(workbench.Visible);
                Assert.False(form.Controls.Find("compactAnalysisNotice", true)[0].Visible);
            }
            finally { form.Close(); Directory.Delete(directory, true); }
        });

    private static T Get<T>(object target, string name) => (T)typeof(FormAudioSpectrum).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void Set(object target, string name, object value) => typeof(FormAudioSpectrum).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private static void Invoke(object target, string name, params object[] arguments) => typeof(FormAudioSpectrum).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
    private static void Snapshot(Form form, string name)
    {
        var output = Environment.GetEnvironmentVariable("SPECTRUM_WORKSPACE_EVIDENCE"); if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output); form.Refresh(); Application.DoEvents();
        using var image = new Bitmap(form.Width, form.Height); using var graphics = Graphics.FromImage(image);
        var dc = graphics.GetHdc();
        try { Assert.True(PrintWindow(form.Handle, dc, 2), "Native window capture failed."); }
        finally { graphics.ReleaseHdc(dc); }
        image.Save(Path.Combine(output, name + ".png"));
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
