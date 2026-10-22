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
                        for (var tick = 0; tick < 13; tick++)
                        {
                            var y = plot.Top + plot.Height * tick / 12f;
                            var gridRegion = new Rectangle((int)(plot.Left + plot.Width * 0.87),
                                (int)Math.Round(y) - 1, 4, 3);
                            Assert.True(DifferenceCount(image, withoutGrid, gridRegion) > 0,
                                $"Missing actual stereo grid row {tick * -6} dB.");
                        }
                        AssertStereoLabelRows(image, withoutText, plot, bodyFont, textHeight,
                            control.DeviceDpi, originalText, control.BackColor, fontScale,
                            $"{width}x{height}/{fontScale}");
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
            Assert.True(labels[0].Visible, $"{mode}: full-scale endpoint omitted; viewport {advanced.Bounds}, " +
                $"curve plot {advanced.SpectrumPlotBounds}, toolbar {panel.Bounds}, status {status.Bounds}, " +
                $"endpoint size {labels[0].Size}.");
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

    private static void AssertStereoLabelRows(Bitmap actual, Bitmap withoutText, RectangleF plot,
        Font font, float textHeight, int dpi, Color ink, Color background, float fontScale, string context)
    {
        // Sampling a font-height window at every tick counts neighboring ink again when
        // a tick is deliberately unlabeled. Read each actual raster row once instead;
        // reference glyphs identify its location/height, never the renderer's stride.
        var references = Enumerable.Range(0, 13).Select(tick =>
        {
            var y = plot.Top + plot.Height * tick / 12f - textHeight / 2;
            return RasterizeLabel(tick, y, font, dpi, ink, background);
        }).ToArray();
        var scale = dpi / 96f;
        var reserve = Math.Max(48 * scale, references.Max(reference => reference.Bounds.Width) + 6 * scale);
        var gutter = Rectangle.FromLTRB(Math.Max(0, (int)Math.Floor(plot.Left - reserve)),
            Math.Max(0, references.Min(reference => reference.Bounds.Top) - 1),
            Math.Max(0, (int)Math.Floor(plot.Left - 3)),
            Math.Min(actual.Height, references.Max(reference => reference.Bounds.Bottom) + 1));
        var rows = PaintedRows(actual, withoutText, gutter);
        var observed = new HashSet<int>();
        var maximumSingleHeight = references.Max(reference => reference.Bounds.Height);
        var previousBottom = int.MinValue;
        foreach (var row in rows)
        {
            Assert.True(row.Top > previousBottom,
                $"Stereo text rows have no blank separation at {context}: {row} after {previousBottom}.");
            Assert.True(row.Height <= maximumSingleHeight + 1,
                $"Stereo text ink merged/overlapped at {context}: row {row}, maximum single-label height {maximumSingleHeight}.");
            var matches = references.Where(reference => Math.Abs(reference.Bounds.Top - row.Top) <= 1 &&
                Math.Abs(reference.Bounds.Bottom - row.Bottom) <= 1).ToArray();
            Assert.True(matches.Length == 1,
                $"Stereo text row is clipped, merged or misaligned at {context}: actual {row}; matching reference ticks={matches.Length}.");
            var match = matches[0];
            Assert.True(row.Width >= match.Bounds.Width - 1,
                $"Clipped stereo label {match.Tick * -6} dB at {context}: ink {row}, required width {match.Bounds.Width}.");
            Assert.True(observed.Add(match.Tick), $"Stereo label {match.Tick * -6} dB was painted more than once at {context}.");
            previousBottom = row.Bottom;
        }
        foreach (var required in references.Where(reference => fontScale == 1 || reference.Tick == 0 || reference.Tick == 12))
        {
            Assert.True(required.Bounds.Top >= 0 && required.Bounds.Bottom <= actual.Height,
                $"Stereo label {required.Tick * -6} dB is clipped by the control at {context}.");
            Assert.Contains(required.Tick, observed);
        }
    }

    private sealed class LabelRasterReference
    {
        internal int Tick;
        internal Rectangle Bounds;
    }

    private static LabelRasterReference RasterizeLabel(int tick, float y, Font font, int dpi, Color ink, Color background)
    {
        var margin = (int)Math.Ceiling(8 * dpi / 96d);
        var text = SpectrumAxis.DecibelLabels[tick];
        var size = TextRenderer.MeasureText(text, font);
        using var label = new Bitmap(size.Width + margin * 2, Math.Max(size.Height, (int)Math.Ceiling(font.GetHeight(dpi))) + margin * 2,
            PixelFormat.Format32bppPArgb);
        label.SetResolution(dpi, dpi);
        using var blank = new Bitmap(label.Width, label.Height, PixelFormat.Format32bppPArgb);
        blank.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(background);
        using (var graphics = Graphics.FromImage(label))
        {
            graphics.Clear(background);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var brush = new SolidBrush(ink);
            graphics.DrawString(text, font, brush, margin, margin + y - (float)Math.Floor(y));
        }
        var bounds = DifferenceBounds(label, blank, new Rectangle(Point.Empty, label.Size));
        Assert.False(bounds.IsEmpty, "Single-label reference raster must contain actual glyph ink.");
        bounds.Y += (int)Math.Floor(y) - margin;
        return new LabelRasterReference { Tick = tick, Bounds = bounds };
    }

    private static List<Rectangle> PaintedRows(Bitmap actual, Bitmap withoutText, Rectangle region)
    {
        region.Intersect(new Rectangle(Point.Empty, actual.Size));
        var result = new List<Rectangle>(); var current = Rectangle.Empty;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var row = DifferenceBounds(actual, withoutText, new Rectangle(region.Left, y, region.Width, 1));
            if (row.IsEmpty)
            {
                if (!current.IsEmpty) { result.Add(current); current = Rectangle.Empty; }
            }
            else current = current.IsEmpty ? row : Rectangle.Union(current, row);
        }
        if (!current.IsEmpty) result.Add(current);
        return result;
    }

    private static Bitmap Paint(Control control)
    {
        var image = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppPArgb);
        image.SetResolution(control.DeviceDpi, control.DeviceDpi);
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
