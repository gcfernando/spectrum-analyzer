using System;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Reflection;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class RolesSpectrogramTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void MissingTimeAndSampleGapsAreDistinctFromActualDigitalSilence(string appearance)
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            var workspace = new AnalysisWorkspace();
            workspace.Settings.HistorySeconds = 1;
            workspace.Settings.AveragingMilliseconds = 0;
            workspace.Settings.SmoothingBands = 0;
            workspace.UpdateSettings();
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
            var epoch = new object();
            // Explicit isolated silence fixture. Two real windows leave an
            // unobserved sample interval; the renderer cannot fill that gap.
            AnalysisFrame Silent(long end) => new AnalysisFrame(plan, 2, 16000, end,
                new float[32000], new double[83], new double[83], new double[83], epoch);
            workspace.Accept(Silent(16000));
            workspace.Accept(Silent(48000));
            var history = workspace.History;
            Assert.Contains(history, row => row.Gap);
            var originalTimes = history.Select(row => row.Seconds).ToArray();
            var originalPower = history.SelectMany(row => row.Power).ToArray();
            using var plot = new AnalysisPlotControl(workspace) { Size = new Size(820, 390), ShowSpectrogram = true };
            Assert.Contains("hatched areas have no measured data", plot.AccessibleDescription);
            plot.ApplyPresentation(BarColorThemes.ApplyAppearance(BarColorThemes.Studio, appearance).Ui);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var bounds = (Rectangle)typeof(AnalysisPlotControl).GetProperty("Plot", flags).GetValue(plot);
            using var bitmap = new Bitmap(plot.Width, plot.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                typeof(AnalysisPlotControl).GetMethod("PaintHistory", flags).Invoke(plot, new object[] { graphics, workspace.Latest });
            int[] Row(double sourceSeconds)
            {
                var y = bounds.Top + (int)Math.Floor(sourceSeconds * bounds.Height);
                return Enumerable.Range(bounds.Left + bounds.Width / 3, bounds.Width / 3)
                    .Select(x => bitmap.GetPixel(x, y).ToArgb()).ToArray();
            }
            var silence = Row(.2);
            Assert.All(silence, pixel => Assert.Equal(Color.FromArgb(15, 21, 29).ToArgb(), pixel));
            foreach (var missing in new[] { Row(.01), Row(.5) })
            {
                Assert.DoesNotContain(Color.FromArgb(15, 21, 29).ToArgb(), missing);
                Assert.Equal(2, missing.Distinct().Count()); // Neutral fill and diagonal hatch.
            }
            Assert.Equal(originalTimes, workspace.History.Select(row => row.Seconds));
            Assert.Equal(originalPower, workspace.History.SelectMany(row => row.Power));
            Assert.Equal(1, workspace.CurrentSeconds);
            var evidence = Environment.GetEnvironmentVariable("SPECTRUM_ROLES_EVIDENCE");
            if (!string.IsNullOrEmpty(evidence))
            {
                Directory.CreateDirectory(evidence);
                bitmap.Save(Path.Combine(evidence, "spectrogram-gap-" + appearance.Replace(" ", "-") + ".png"));
            }
        });
}
