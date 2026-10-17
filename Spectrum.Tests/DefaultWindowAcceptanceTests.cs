// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Acceptance checks that the main window comes up with sensible defaults on first launch.
public class DefaultWindowAcceptanceTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void ShownDefaultIsTwelveHundredByFourEightyAndAllModePathsPreserveItsWindow(float fontScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownDefault();
            Assert.Equal(new Size(1200, 480), form.ClientSize);
            var controls = Descendants(form).Prepend(form).ToArray();
            var originalFonts = controls.Select(control => control.Font).ToArray();
            var fonts = new List<Font>();
            try
            {
                for (var index = 0; index < controls.Length; index++)
                {
                    var source = originalFonts[index];
                    var font = new Font(source.FontFamily, source.SizeInPoints * fontScale, source.Style);
                    fonts.Add(font);
                    controls[index].Font = font;
                }
                Reflow(form);
                var bounds = form.Bounds;
                var client = form.ClientSize;
                var minimum = form.MinimumSize;
                var selector = Field<ComboBox>(form, "_modeSelector");
                Assert.Equal(21, selector.Items.Count);
                foreach (var mode in selector.Items.Cast<string>().ToArray())
                {
                    selector.SelectedItem = mode;
                    Reflow(form);
                    AssertStable(form, bounds, client, minimum, mode);
                }
                selector.SelectedIndex = selector.Items.Count - 1;
                foreach (var expected in selector.Items.Cast<string>().ToArray())
                {
                    var key = new KeyEventArgs(Keys.Control | Keys.M);
                    typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_KeyUp", PrivateInstance)
                        .Invoke(form, new object[] { form, key });
                    Assert.True(key.Handled);
                    Assert.Equal(expected, selector.SelectedItem);
                    Reflow(form);
                    AssertStable(form, bounds, client, minimum, "keyboard " + expected);
                }
                var random = new Random(51023);
                for (var selection = 0; selection < selector.Items.Count; selection++)
                {
                    form.RandomizeVisualSelection(random);
                    Reflow(form);
                    AssertStable(form, bounds, client, minimum, "random " + selection);
                }
            }
            finally
            {
                form.Dispose();
                foreach (var font in fonts) font.Dispose();
            }
        });
    }

    [Fact]
    public void ActualDefaultStereoPaintHasReadableBothPanelAxesWithoutAssumingAnOrientation()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownDefault();
            Assert.Equal(new Size(1200, 480), form.ClientSize);
            var bounds = form.Bounds;
            var minimum = form.MinimumSize;
            Field<ComboBox>(form, "_modeSelector").SelectedItem = "Stereo Spectrum";
            Reflow(form);
            AssertStable(form, bounds, new Size(1200, 480), minimum, "default Stereo Spectrum");
            var control = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            control.SetSpectrum(new byte[83]);
            using var actual = Paint(control);
            var body = Field<Font>(control, "_analysisBodyFont");
            var title = Field<Font>(control, "_analysisTitleFont");
            Assert.True(body.SizeInPoints >= 7 && title.SizeInPoints >= 7);
            var first = control.GetStereoSpectrumPlotBounds(0);
            var second = control.GetStereoSpectrumPlotBounds(1);
            Assert.False(first.IntersectsWith(second));
            Assert.True(first.Width > 100 && first.Height > 100);
            Assert.True(second.Width > 100 && second.Height > 100);
            var grid = Field<Pen>(control, "_gridPen");
            var text = Field<SolidBrush>(control, "_textBrush");
            using var noGrid = PaintWithout(control, grid, null);
            using var noText = PaintWithout(control, null, text);
            var lineHeight = body.GetHeight(control.DeviceDpi);
            foreach (var plot in new[] { first, second })
            {
                Assert.True(new RectangleF(PointF.Empty, control.ClientSize).Contains(plot));
                Assert.True(plot.Height / 12 >= lineHeight + 1,
                    "Default stereo must provide readable separation for all thirteen dB labels.");
                var previousInkBottom = -1;
                for (var tick = 0; tick < 13; tick++)
                {
                    var y = plot.Top + plot.Height * tick / 12f;
                    var guide = new Rectangle((int)(plot.Left + plot.Width * 0.87),
                        (int)Math.Round(y) - 1, 4, 3);
                    Assert.False(DifferenceBounds(actual, noGrid, guide).IsEmpty,
                        $"Missing actual default stereo {tick * -6} dB grid.");
                    var label = Rectangle.FromLTRB(
                        Math.Max(0, (int)(plot.Left - Math.Max(48, lineHeight * 3))),
                        Math.Max(0, (int)Math.Floor(y - lineHeight / 2)),
                        Math.Max(0, (int)plot.Left - 3),
                        Math.Min(actual.Height, (int)Math.Ceiling(y + lineHeight / 2)));
                    var ink = DifferenceBounds(actual, noText, label);
                    Assert.False(ink.IsEmpty, $"Missing actual default stereo {tick * -6} dB label.");
                    Assert.True(ink.Top > previousInkBottom, "Default stereo dB text overlaps.");
                    previousInkBottom = ink.Bottom - 1;
                }
                foreach (var frequency in new double[]
                             { 20, 30, 40, 60, 100, 200, 300, 400, 600, 1000, 2000, 3000, 4000, 6000, 10000, 20000 })
                {
                    var x = plot.Left + plot.Width * Math.Log(frequency / 20) / Math.Log(1000);
                    var column = new Rectangle((int)Math.Round(x) - 1,
                        (int)(plot.Top + plot.Height * 5.5 / 12), 3, 4);
                    Assert.False(DifferenceBounds(actual, noGrid, column).IsEmpty,
                        $"Missing actual default stereo {frequency} Hz grid.");
                }
            }
        });
    }

    private static FormAudioSpectrum CreateShownDefault()
    {
        var form = CombinedRandomizationTests.CreateForm(mode: "Oscilloscope");
        CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
        // Show/Load/autoscale run for real, but loopback capture stays off in the unit host.
        var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
        form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
        foreach (var label in Descendants(form).OfType<Label>())
            label.UseCompatibleTextRendering = false;
        form.ShowInTaskbar = false;
        form.Show();
        Reflow(form);
        return form;
    }

    private static void AssertStable(FormAudioSpectrum form, Rectangle bounds, Size client,
        Size minimum, string context)
    {
        Assert.True(form.Bounds == bounds, $"{context}: outer bounds changed.");
        Assert.True(form.ClientSize == client, $"{context}: client size changed.");
        Assert.True(form.MinimumSize == minimum, $"{context}: minimum size changed.");
        var toolbar = Field<FlowLayoutPanel>(form, "_selectionPanel");
        var status = Field<Label>(form, "_captureStatusLabel");
        Assert.True(toolbar.Parent.ClientRectangle.Contains(toolbar.Bounds));
        Assert.True(status.Parent.ClientRectangle.Contains(status.Bounds));
        var advanced = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
        foreach (var plot in Field<VerticalProgressBar[]>(form, "_progressBars")
                     .Cast<Control>().Append(advanced).Where(control => control.Visible))
        {
            Assert.True(plot.Parent.ClientRectangle.Contains(plot.Bounds));
            Assert.True(plot.Top >= toolbar.Bottom && plot.Bottom <= status.Top,
                $"{context}: plot overlaps toolbar or status.");
        }
    }

    private static Bitmap PaintWithout(AdvancedVisualizationControl control, Pen pen, SolidBrush brush)
    {
        var original = pen != null ? pen.Color : brush.Color;
        try
        {
            if (pen != null) pen.Color = Color.Transparent;
            else brush.Color = Color.Transparent;
            return Paint(control);
        }
        finally
        {
            if (pen != null) pen.Color = original;
            else brush.Color = original;
        }
    }

    private static Bitmap Paint(Control control)
    {
        var image = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        using var args = new PaintEventArgs(graphics, control.ClientRectangle);
        control.GetType().GetMethod("OnPaint", PrivateInstance).Invoke(control, new object[] { args });
        return image;
    }

    private static Rectangle DifferenceBounds(Bitmap first, Bitmap second, Rectangle region)
    {
        region.Intersect(new Rectangle(Point.Empty, first.Size));
        var minX = first.Width;
        var minY = first.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = region.Top; y < region.Bottom; y++)
        for (var x = region.Left; x < region.Right; x++)
        {
            if (first.GetPixel(x, y).ToArgb() == second.GetPixel(x, y).ToArgb()) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var descendant in Descendants(control)) yield return descendant;
        }
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void Reflow(FormAudioSpectrum form)
    {
        foreach (var method in new[] { "PositionSelectionPanel", "RecalculateBarLayout" })
            typeof(FormAudioSpectrum).GetMethod(method, PrivateInstance).Invoke(form, null);
        form.PerformLayout();
    }
}
