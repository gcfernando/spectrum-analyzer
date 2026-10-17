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

// Keeps the main window layout stable across sizes and resizes.
public class StableWindowLayoutTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static IEnumerable<object[]> WindowCases()
    {
        foreach (var size in new[] { new Size(1200, 480), new Size(1184, 378), new Size(900, 350),
                     new Size(1480, 474), new Size(1920, 1080) })
        foreach (var scale in new[] { 1f, 1.5f, 2f })
            yield return new object[] { size.Width, size.Height, scale };
    }

    [Theory]
    [MemberData(nameof(WindowCases))]
    public void EveryActualSelectorAndRandomModeTransitionPreservesTheUsersWindow(
        int width, int height, float fontScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm(new Size(width, height));
            var fonts = EnlargeFonts(form, fontScale);
            try
            {
                Reflow(form);
                if (fontScale == 1) Assert.Equal(new Size(width, height), form.ClientSize);
                var initialBounds = form.Bounds;
                var initialClient = form.ClientSize;
                var initialMinimum = form.MinimumSize;
                var selector = Field<ComboBox>(form, "_modeSelector");
                var registeredModes = selector.Items.Cast<string>().ToArray();
                Assert.Equal(21, registeredModes.Length);
                Assert.Equal(FormAudioSpectrum.VisualModeNames.OrderBy(name => name),
                    registeredModes.OrderBy(name => name));
                foreach (var mode in registeredModes)
                {
                    selector.SelectedItem = mode;
                    Reflow(form);
                    Assert.Equal(mode, Field<string>(form, "_visualMode"));
                    AssertWindowUnchanged(form, initialBounds, initialClient, initialMinimum, mode);
                    AssertContainedLayout(form, mode);
                }
                for (var repeat = 0; repeat < 3; repeat++)
                foreach (var mode in new[] { "Stereo Spectrum", "Oscilloscope", "Spectrum", "Bricks", "LED" })
                {
                    selector.SelectedItem = mode;
                    Reflow(form);
                    AssertWindowUnchanged(form, initialBounds, initialClient, initialMinimum, mode);
                    AssertContainedLayout(form, mode);
                }
                var random = new Random(40311);
                for (var rotation = 0; rotation < 36; rotation++)
                {
                    form.RandomizeVisualSelection(random);
                    Reflow(form);
                    AssertWindowUnchanged(form, initialBounds, initialClient, initialMinimum,
                        "random rotation " + rotation);
                    AssertContainedLayout(form, Field<string>(form, "_visualMode"));
                }
            }
            finally
            {
                form.Dispose();
                foreach (var font in fonts) font.Dispose();
            }
        });
    }

    [Theory]
    [MemberData(nameof(WindowCases))]
    public void StereoUsesExistingWindowSpaceWithActualReadableAxesAndAllGridRows(
        int width, int height, float fontScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm(new Size(width, height));
            var fonts = EnlargeFonts(form, fontScale);
            try
            {
                Reflow(form);
                if (fontScale == 1) Assert.Equal(new Size(width, height), form.ClientSize);
                var initialBounds = form.Bounds;
                var initialClient = form.ClientSize;
                var initialMinimum = form.MinimumSize;
                Field<ComboBox>(form, "_modeSelector").SelectedItem = "Stereo Spectrum";
                Reflow(form);
                AssertWindowUnchanged(form, initialBounds, initialClient, initialMinimum, "Stereo Spectrum");
                AssertContainedLayout(form, "Stereo Spectrum");
                var control = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
                control.SetSpectrum(new byte[83]);
                using var image = Paint(control);
                var left = control.GetStereoSpectrumPlotBounds(0);
                var right = control.GetStereoSpectrumPlotBounds(1);
                Assert.True(left.Width > 20 && left.Height > 20);
                Assert.True(right.Width > 20 && right.Height > 20);
                Assert.True(new RectangleF(PointF.Empty, control.ClientSize).Contains(left));
                Assert.True(new RectangleF(PointF.Empty, control.ClientSize).Contains(right));
                Assert.False(left.IntersectsWith(right), "Stereo panels overlap.");
                var columns = left.Right < right.Left;
                if (initialClient.Height < 480)
                {
                    Assert.True(columns, "Short windows must use side-by-side stereo panels.");
                }
                else if (initialClient.Height >= 600 && fontScale == 1)
                {
                    Assert.True(left.Bottom < right.Top, "Tall windows should retain vertically stacked stereo panels.");
                }
                if (columns)
                {
                    Assert.InRange(Math.Abs(left.Top - right.Top), 0, 1);
                    Assert.InRange(Math.Abs(left.Bottom - right.Bottom), 0, 1);
                }
                else
                {
                    Assert.True(left.Bottom < right.Top, "Stereo panels must form separate rows or columns.");
                    Assert.InRange(Math.Abs(left.Left - right.Left), 0, 1);
                    Assert.InRange(Math.Abs(left.Right - right.Right), 0, 1);
                }

                var grid = Field<Pen>(control, "_gridPen");
                var originalGrid = grid.Color;
                Bitmap withoutGrid;
                try
                {
                    grid.Color = Color.Transparent;
                    withoutGrid = Paint(control);
                }
                finally { grid.Color = originalGrid; }
                var text = Field<SolidBrush>(control, "_textBrush");
                var originalText = text.Color;
                Bitmap withoutText;
                try
                {
                    text.Color = Color.Transparent;
                    withoutText = Paint(control);
                }
                finally { text.Color = originalText; }
                using (withoutGrid)
                using (withoutText)
                {
                    var bodyFont = Field<Font>(control, "_analysisBodyFont");
                    var textHeight = bodyFont.GetHeight(control.DeviceDpi);
                    foreach (var plot in new[] { left, right })
                    {
                        var lastTextBottom = float.MinValue;
                        for (var tick = 0; tick < 13; tick++)
                        {
                            var y = plot.Top + plot.Height * tick / 12f;
                            var gridRegion = new Rectangle((int)(plot.Left + plot.Width * 0.87),
                                (int)Math.Round(y) - 1, 4, 3);
                            Assert.True(DifferenceCount(image, withoutGrid, gridRegion) > 0,
                                $"Missing actual stereo grid row {tick * -6} dB.");
                            var labelRegion = Rectangle.FromLTRB(
                                Math.Max(0, (int)Math.Floor(plot.Left -
                                    Math.Max(48f * control.DeviceDpi / 96, textHeight * 3))),
                                Math.Max(0, (int)Math.Floor(y - textHeight / 2)),
                                Math.Max(0, (int)Math.Floor(plot.Left - 3)),
                                Math.Min(image.Height, (int)Math.Ceiling(y + textHeight / 2)));
                            var paintedText = DifferenceBounds(image, withoutText, labelRegion);
                            var mustBeVisible = fontScale == 1 || tick == 0 || tick == 12;
                            if (mustBeVisible)
                                Assert.False(paintedText.IsEmpty,
                                    $"Missing actual stereo label {tick * -6} dB at {width}x{height}/{fontScale}.");
                            if (paintedText.IsEmpty) continue;
                            Assert.True(paintedText.Top > lastTextBottom,
                                $"Stereo text rows overlap at {tick * -6} dB.");
                            lastTextBottom = paintedText.Bottom - 1;
                        }
                        foreach (var frequency in new double[]
                                     { 20, 30, 40, 60, 100, 200, 300, 400, 600, 1000, 2000, 3000, 4000, 6000, 10000, 20000 })
                        {
                            var x = plot.Left + plot.Width * Math.Log(frequency / 20) / Math.Log(1000);
                            var region = new Rectangle((int)Math.Round(x) - 1,
                                (int)(plot.Top + plot.Height * 5.5 / 12), 3, 4);
                            Assert.True(DifferenceCount(image, withoutGrid, region) > 0,
                                $"Missing actual stereo frequency grid {frequency} Hz.");
                        }
                    }
                }
            }
            finally
            {
                form.Dispose();
                foreach (var font in fonts) font.Dispose();
            }
        });
    }

    private static FormAudioSpectrum CreateShownForm(Size clientSize)
    {
        var form = CombinedRandomizationTests.CreateForm(mode: "Oscilloscope");
        Set(form, "_applyingRandomSelection", true);
        var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
        form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
        foreach (var label in Descendants(form).OfType<Label>())
            label.UseCompatibleTextRendering = false;
        form.ShowInTaskbar = false;
        form.Show();
        form.ClientSize = clientSize;
        Reflow(form);
        Assert.Equal(clientSize, form.ClientSize);
        return form;
    }

    private static void AssertWindowUnchanged(FormAudioSpectrum form, Rectangle bounds,
        Size clientSize, Size minimum, string context)
    {
        Assert.True(form.Bounds == bounds, $"{context}: window changed from {bounds} to {form.Bounds}.");
        Assert.True(form.ClientSize == clientSize,
            $"{context}: client changed from {clientSize} to {form.ClientSize}.");
        Assert.True(form.MinimumSize == minimum,
            $"{context}: minimum changed from {minimum} to {form.MinimumSize}.");
    }

    private static void AssertContainedLayout(FormAudioSpectrum form, string mode)
    {
        var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
        var status = Field<Label>(form, "_captureStatusLabel");
        var advanced = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
        var bars = Field<VerticalProgressBar[]>(form, "_progressBars");
        Assert.True(panel.Parent.ClientRectangle.Contains(panel.Bounds));
        Assert.True(status.Parent.ClientRectangle.Contains(status.Bounds));
        foreach (var plot in bars.Cast<Control>().Append(advanced).Where(control => control.Visible))
        {
            Assert.True(plot.Parent.ClientRectangle.Contains(plot.Bounds),
                $"{mode}: plot {plot.Bounds} exceeds parent.");
            Assert.True(plot.Top >= panel.Bottom, $"{mode}: plot overlaps selectors.");
            Assert.True(plot.Bottom <= status.Top, $"{mode}: plot overlaps capture status.");
        }
        var frequencyLabels = Field<Label[]>(form, "_axisLabels");
        AssertOrderedLabels(frequencyLabels.Where(label => label.Visible).OrderBy(label => label.Left), true);
        foreach (var field in new[] { "_dbAxisLabelsLeft", "_dbAxisLabelsRight",
                     "_dbAxisLabelsLeftMirror", "_dbAxisLabelsRightMirror" })
            AssertOrderedLabels(Field<Label[]>(form, field).Where(label => label.Visible)
                .OrderBy(label => label.Top), false);
        var calibrated = new[] { "Spectrum", "Bricks", "LED", "Contour", "Peak Trace", "Threshold Monitor" };
        if (!calibrated.Contains(mode)) return;
        foreach (var field in new[] { "_dbAxisLabelsLeft", "_dbAxisLabelsRight" })
        {
            var labels = Field<Label[]>(form, field);
            Assert.True(labels[0].Visible, $"{mode}: full-scale endpoint omitted.");
            Assert.True(labels[12].Visible, $"{mode}: display-floor endpoint omitted.");
        }
        Assert.True(frequencyLabels[0].Visible, $"{mode}: 20Hz endpoint omitted.");
        Assert.True(frequencyLabels[15].Visible, $"{mode}: 20kHz endpoint omitted.");
    }

    private static void AssertOrderedLabels(IEnumerable<Label> labels, bool horizontal)
    {
        var previousEnd = int.MinValue;
        foreach (var label in labels)
        {
            Assert.True(label.Parent.ClientRectangle.Contains(label.Bounds), $"{label.Text}: clipped label.");
            var text = TextRenderer.MeasureText(label.Text, label.Font);
            Assert.True(label.Width >= text.Width && label.Height >= text.Height,
                $"{label.Text}: native text does not fit label {label.Bounds}.");
            Assert.True((horizontal ? label.Left : label.Top) >= previousEnd + 2,
                $"{label.Text}: overlapping visible axis labels.");
            previousEnd = horizontal ? label.Right : label.Bottom;
        }
    }

    private static List<Font> EnlargeFonts(Form form, float scale)
    {
        var controls = Descendants(form).Prepend(form).ToArray();
        var originals = controls.Select(control => control.Font).ToArray();
        var fonts = new List<Font>();
        for (var index = 0; index < controls.Length; index++)
        {
            var font = new Font(originals[index].FontFamily, originals[index].SizeInPoints * scale, originals[index].Style);
            fonts.Add(font);
            controls[index].Font = font;
        }
        return fonts;
    }

    private static int DifferenceCount(Bitmap first, Bitmap second, Rectangle region)
    {
        var count = 0;
        region.Intersect(new Rectangle(Point.Empty, first.Size));
        for (var y = region.Top; y < region.Bottom; y++)
        for (var x = region.Left; x < region.Right; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) count++;
        return count;
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

    private static Bitmap Paint(Control control)
    {
        var image = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(image);
        using var args = new PaintEventArgs(graphics, control.ClientRectangle);
        control.GetType().GetMethod("OnPaint", PrivateInstance).Invoke(control, new object[] { args });
        return image;
    }

    private static void Reflow(FormAudioSpectrum form)
    {
        Call(form, "PositionSelectionPanel");
        Call(form, "RecalculateBarLayout");
        form.PerformLayout();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void Set(object target, string name, object value) => CombinedRandomizationTests.SetField(target, name, value);
    private static void Call(object target, string name) =>
        target.GetType().GetMethod(name, PrivateInstance).Invoke(target, null);
}
