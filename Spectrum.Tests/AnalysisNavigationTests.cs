using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace Spectrum.Tests;

public sealed class AnalysisNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedWheelZoomOutSaturatesAtTheExactSupportedEndpoints(bool spectrogram)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = new AnalysisWorkspace();
            workspace.Accept(AnalysisWorkspaceTests.Tone());
            var measured = (double[])workspace.Latest.Power.Clone();
            using var plot = new AnalysisPlotControl(workspace) { ShowSpectrogram = spectrogram };
            var wheel = typeof(AnalysisPlotControl).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic);
            for (var step = 0; step < 100; step++)
            {
                wheel.Invoke(plot, new object[] { new MouseEventArgs(MouseButtons.None, 0, 100, 100, -120) });
                AssertValidRange(workspace);
            }
            Assert.Equal(1, workspace.Settings.MinHz);
            Assert.Equal(384000, workspace.Settings.MaxHz);
            Assert.Equal(measured, workspace.Latest.Power);
        });
    }

    [Theory]
    [InlineData(1, 384000)]
    [InlineData(20, 20000)]
    [InlineData(1, 100)]
    [InlineData(300000, 384000)]
    public void ZoomAndPanStayInsideTheSupportedRangeAtBothLimits(double minimum, double maximum)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = new AnalysisWorkspace();
            workspace.Settings.MinHz = minimum; workspace.Settings.MaxHz = maximum; workspace.UpdateSettings();
            using var plot = new AnalysisPlotControl(workspace);
            foreach (var factor in new[] { .5, .8, 1.25, 2d })
            {
                for (var step = 0; step < 50; step++) { plot.Zoom(factor); AssertValidRange(workspace); }
                foreach (var direction in new[] { -.25, .25 })
                    for (var step = 0; step < 50; step++) { plot.Pan(direction); AssertValidRange(workspace); }
            }
            plot.ResetZoom();
            Assert.Equal(20, workspace.Settings.MinHz); Assert.Equal(20000, workspace.Settings.MaxHz);
        });
    }

    private static void AssertValidRange(AnalysisWorkspace workspace)
    {
        Assert.InRange(workspace.Settings.MinHz, 1, 384000);
        Assert.InRange(workspace.Settings.MaxHz, 1, 384000);
        Assert.True(workspace.Settings.MaxHz > workspace.Settings.MinHz);
        workspace.Settings.Validate();
    }
}

// Explicit shown-application proof, outside automatic test discovery.
public static class AnalysisNavigationVerification
{
    public static string Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        var errors = new List<Exception>();
        ThreadExceptionEventHandler handler = (_, args) => errors.Add(args.Exception);
        Application.ThreadException += handler;
        try
        {
            using var main = new FormAudioSpectrum(); main.Show(); Pump(300); main.ShowAnalysisWorkbench(); Pump(300);
            var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var form = (AnalysisWorkbenchForm)typeof(FormAudioSpectrum).GetField("_analysisWorkbench", hidden).GetValue(main);
            var workspace = (AnalysisWorkspace)typeof(FormAudioSpectrum).GetField("_analysisWorkspace", hidden).GetValue(main);
            var wave = Path.Combine(output, "navigation-verification.wav"); AnalysisUpgradeVerification.WriteWave(wave);
            Find<NumericUpDown>(form, "analysisSelectionDurationSeconds").Value = 6;
            var loading = (Task)typeof(AnalysisWorkbenchForm).GetMethod("OpenAudioPathAsync", hidden).Invoke(form, new object[] { wave });
            var clock = Stopwatch.StartNew();
            while (!loading.IsCompleted && clock.ElapsedMilliseconds < 20000) Pump(25);
            Assert.True(loading.IsCompleted, "Shown file analysis did not finish.");
            loading.GetAwaiter().GetResult();
            Find<ComboBox>(form, "analysisFloorDb").SelectedItem = "-60";
            Find<ComboBox>(form, "analysisResponsePreset").SelectedItem = "Detailed";
            AnalysisWorkbenchUiTests.Reveal(form, "analysisAdvancedToggle", "analysisAdvancedPanel");
            Find<NumericUpDown>(form, "analysisAveragingMs").Value = 350;
            Find<NumericUpDown>(form, "analysisHistorySeconds").Value = 5;
            var measured = (double[])workspace.Latest.Power.Clone();
            var tabs = Find<TabControl>(form, "analysisPlotTabs");
            var wheelEvents = 0; var keyEvents = 0; var buttonClicks = 0;
            foreach (var view in new[] { 0, 1 })
            {
                tabs.SelectedIndex = view; Pump(100);
                var plot = Find<AnalysisPlotControl>(form, view == 0 ? "analysisSpectrumPlot" : "analysisSpectrogramPlot");
                plot.ResetZoom(); plot.Focus();
                for (var step = 1; step <= 100; step++)
                {
                    Wheel(plot, -120); wheelEvents++; Check();
                    if (view == 0 && step == 2) Snapshot("01-before-third-notch.png");
                    if (view == 0 && step == 3) Snapshot("02-after-third-notch.png");
                    if (step % 10 == 0) Pump(20);
                }
                Assert.Equal(1, workspace.Settings.MinHz); Assert.Equal(384000, workspace.Settings.MaxHz);
                Snapshot(view == 0 ? "03-saturated-spectrum.png" : "04-saturated-spectrogram.png");
                plot.ResetZoom(); var span = workspace.Settings.MaxHz / workspace.Settings.MinHz;
                Key(plot, Keys.Add); keyEvents++; Check(); Assert.True(workspace.Settings.MaxHz / workspace.Settings.MinHz < span);
                for (var step = 0; step < 100; step++) { Key(plot, Keys.Subtract); keyEvents++; Check(); }
                workspace.Settings.MinHz = 1; workspace.Settings.MaxHz = 100; workspace.UpdateSettings();
                foreach (var key in new[] { Keys.Right, Keys.Left })
                    for (var step = 0; step < 100; step++) { Key(plot, key); keyEvents++; Check(); if (step % 10 == 0) Pump(20); }
                Assert.Equal(1, workspace.Settings.MinHz);
            }
            foreach (var name in new[] { "analysisZoomOut", "analysisZoomIn", "analysisPanHigher", "analysisPanLower" })
                for (var step = 0; step < 100; step++) { Find<Button>(form, name).PerformClick(); buttonClicks++; Check(); }
            Find<Button>(form, "analysisResetZoom").PerformClick(); buttonClicks++; Pump(150);
            Assert.Equal(20, workspace.Settings.MinHz); Assert.Equal(20000, workspace.Settings.MaxHz);
            Assert.Equal(measured, workspace.Latest.Power); Assert.Empty(errors);
            Assert.False(Find<Label>(form, "analysisError").Visible);
            tabs.SelectedIndex = 0; Snapshot("05-reset-full-spectrum.png");
            var json = $"{{\"passed\":true,\"shownApplication\":true,\"nativeWheelMessages\":{wheelEvents},\"nativeKeyMessages\":{keyEvents},\"buttonClicks\":{buttonClicks},\"threadExceptions\":{errors.Count},\"fullBoundaryMinimumHz\":1,\"fullBoundaryMaximumHz\":384000,\"resetMinimumHz\":20,\"resetMaximumHz\":20000,\"measuredPowerUnchanged\":true}}";
            File.WriteAllText(Path.Combine(output, "navigation-runtime.json"), json);
            form.Close(); main.Close(); return json;

            void Check() { workspace.Settings.Validate(); Assert.Empty(errors); }
            void Snapshot(string name)
            {
                Pump(120); using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, name), ImageFormat.Png);
            }
        }
        finally { Application.ThreadException -= handler; }
    }
    private static T Find<T>(Control form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();
    private static void Pump(int milliseconds)
    { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); } }
    private static void Wheel(Control plot, int delta)
    {
        var point = plot.PointToScreen(new Point(100, 100));
        SendMessage(plot.Handle, 0x020A, new IntPtr(delta << 16), new IntPtr((point.Y << 16) | (point.X & 0xffff)));
    }
    private static void Key(Control plot, Keys key) => SendMessage(plot.Handle, 0x0100, new IntPtr((int)key), IntPtr.Zero);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
