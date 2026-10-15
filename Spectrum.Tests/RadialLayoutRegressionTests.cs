// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum;
using Spectrum.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

// Regression net for the radial layout geometry so refactors don't quietly shift things around.
public class RadialLayoutRegressionTests
{
    private readonly ITestOutputHelper _output;
    public RadialLayoutRegressionTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> LayoutCases
    {
        get
        {
            foreach (var size in new[] { new Size(1, 1), new Size(70, 42), new Size(240, 140), new Size(320, 180),
                new Size(900, 280), new Size(900, 600), new Size(1350, 900), new Size(1800, 1200) })
            foreach (var dpi in new[] { 96, 144, 192 })
            foreach (var fontPoints in new[] { 8.25f, 20f })
                yield return new object[] { size.Width, size.Height, dpi, fontPoints };
        }
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void RadialTextFitsMeasuredFontBoundsWithoutOverlapAndLeavesTheSignalInsideTheViewport(
        int width, int height, int dpi, float fontPoints)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(width, height);
            using var font = new Font("Segoe UI", fontPoints);
            control.Font = font;
            control.SetSpectrum(Enumerable.Repeat((byte)255, 83).ToArray());
            using var bitmap = new Bitmap(width, height);
            bitmap.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            foreach (var style in new[] { VisualStyle.None, VisualStyle.Precision })
            {
                control.Style = style;
                Paint(control, args);
                var texts = VisibleTexts(control);
                foreach (var entry in texts)
                {
                    var bounds = entry.Bounds;
                    AssertInside(bounds, width, height);
                    Assert.True(bounds.Height >= font.GetHeight(graphics), $"{entry.Text}: font height exceeds its rectangle.");
                    Assert.True(bounds.Width >= graphics.MeasureString(entry.Text, font).Width,
                        $"{entry.Text}: text would be ellipsized rather than fully fitted.");
                }
                for (var first = 0; first < texts.Count; first++)
                for (var second = first + 1; second < texts.Count; second++)
                    Assert.False(texts[first].Bounds.IntersectsWith(texts[second].Bounds),
                        $"Text bounds overlap: {texts[first].Text} / {texts[second].Text}");
                var radius = Field<float>(control, "_radialRadius");
                var center = Field<PointF>(control, "_radialCenter");
                if (radius > 0)
                    AssertInside(new RectangleF(center.X - radius, center.Y - radius, 2 * radius, 2 * radius), width, height);
                foreach (var entry in texts)
                {
                    var x = Math.Max(entry.Bounds.Left, Math.Min(center.X, entry.Bounds.Right));
                    var y = Math.Max(entry.Bounds.Top, Math.Min(center.Y, entry.Bounds.Bottom));
                    var distance = Math.Sqrt((center.X - x) * (center.X - x) + (center.Y - y) * (center.Y - y));
                    Assert.True(distance >= radius, $"Text crosses the signal circle: {entry.Text}");
                }
                _output.WriteLine($"{width}x{height}, {dpi} DPI, {fontPoints} pt, {style}: " +
                    $"font {font.GetHeight(graphics):F2}px; radius {radius:F2}px; " +
                    $"{texts.Count} visible text rectangles; frequency labels {Field<bool>(control, "_radialLabelsVisible")}");
            }
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void LargeFontFrequencyLabelsAndLegendsPaintActualGlyphsWithoutEscapingTheirRectangles(int dpi)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(1800, 1200);
            using var font = new Font("Segoe UI", 20);
            control.Font = font;
            control.ForeColor = Color.Magenta;
            using var withText = new Bitmap(control.Width, control.Height);
            withText.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(withText))
            using (var args = new PaintEventArgs(graphics, control.ClientRectangle))
                Paint(control, args);
            Assert.True(Field<bool>(control, "_radialLabelsVisible"));
            var texts = VisibleTexts(control);
            Assert.Equal(6, texts.Count);

            control.ForeColor = control.BackColor;
            using var withoutText = new Bitmap(control.Width, control.Height);
            withoutText.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(withoutText))
            using (var args = new PaintEventArgs(graphics, control.ClientRectangle))
                Paint(control, args);
            var ink = new int[texts.Count];
            var painted = Snapshot(withText);
            var background = Snapshot(withoutText);
            for (var y = 0; y < control.Height; y++)
            for (var x = 0; x < control.Width; x++)
            {
                if (painted[y * control.Width + x] == background[y * control.Width + x]) continue;
                var owner = texts.FindIndex(text => text.Bounds.Contains(x, y));
                Assert.True(owner >= 0, $"A text pixel escaped its reserved rectangle at {x},{y}.");
                ink[owner]++;
            }
            Assert.All(ink, count => Assert.True(count > 10, "Each reserved label must paint actual visible glyphs."));
            _output.WriteLine($"{dpi} DPI, 20 pt: glyph pixels per caption/legend/landmark = {string.Join(", ", ink)}.");
        });
    }

    [Fact]
    public void CompactRadialLayoutOmitsUnfittableTextInsteadOfCoveringOrInventingSignal()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(70, 42);
            using var font = new Font("Segoe UI", 20);
            control.Font = font;
            using var bitmap = new Bitmap(70, 42);
            bitmap.SetResolution(192, 192);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            control.SynchronizeSpectrum(new byte[83]);
            Paint(control, args);
            Assert.Empty(VisibleTexts(control));
            Assert.False(Field<bool>(control, "_radialLabelsVisible"));
            var silent = Snapshot(bitmap);
            var frame = new byte[83];
            frame[20] = 255;
            control.SetSpectrum(frame);
            var measurements = Field<byte[]>(control, "_spectrum").ToArray();
            var historyCount = Field<int>(control, "_historyCount");
            Paint(control, args);
            Assert.NotEqual(silent, Snapshot(bitmap));
            Assert.Equal(measurements, Field<byte[]>(control, "_spectrum"));
            Assert.Equal(historyCount, Field<int>(control, "_historyCount"));
        });
    }

    [Fact]
    public void RadialLayoutReusesGeometryAndRecalculatesForDpiFontSizeAndBandPlanChanges()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(1800, 1200);
            using var first = new Bitmap(1800, 1200);
            using var graphics = Graphics.FromImage(first);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            Paint(control, args);
            var array = Field<RectangleF[]>(control, "_radialLabelBounds");
            var original = array.ToArray();
            Paint(control, args);
            Assert.Same(array, Field<RectangleF[]>(control, "_radialLabelBounds"));
            Assert.Equal(original, array);

            using var second = new Bitmap(1800, 1200);
            second.SetResolution(192, 192);
            using var higherDpiGraphics = Graphics.FromImage(second);
            using var higherDpiArgs = new PaintEventArgs(higherDpiGraphics, control.ClientRectangle);
            Paint(control, higherDpiArgs);
            Assert.Equal(192f, Field<float>(control, "_radialLayoutDpi"));
            Assert.True(array[0].Height > original[0].Height);
            using var font = new Font("Segoe UI", 20);
            control.Font = font;
            Paint(control, higherDpiArgs);
            Assert.Same(font, Field<Font>(control, "_radialLayoutFont"));
            Assert.True(array[0].Height >= font.GetHeight(higherDpiGraphics));

            control.Size = new Size(900, 600);
            using var resizedArgs = new PaintEventArgs(higherDpiGraphics, control.ClientRectangle);
            Paint(control, resizedArgs);
            Assert.Equal(control.Size, Field<Size>(control, "_radialLayoutSize"));
            var originalCaption = Field<string>(control, "_radialCaption");
            control.SetBandPlan(BandPlan.CreateLogarithmic(83, 20, 20000, 24000));
            Assert.Null(Field<Font>(control, "_radialLayoutFont"));
            Paint(control, resizedArgs);
            Assert.Same(font, Field<Font>(control, "_radialLayoutFont"));
            Assert.NotEqual(originalCaption, Field<string>(control, "_radialCaption"));
            Assert.Contains(Field<string[]>(control, "_bandLabels")[82], Field<string>(control, "_radialCaption"));
        });
    }

    [Theory]
    [InlineData("Studio")]
    [InlineData("Platinum Light")]
    [InlineData("High Contrast")]
    public void RadialMeasuredLayoutSurvivesThemeChangesWithoutChangingMeasurements(string theme)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(900, 600);
            using var font = new Font("Segoe UI", 20);
            control.Font = font;
            control.SetSpectrum(Enumerable.Range(0, 83).Select(band => (byte)(band * 3)).ToArray());
            var spectrum = Field<byte[]>(control, "_spectrum").ToArray();
            using var bitmap = new Bitmap(900, 600);
            bitmap.SetResolution(144, 144);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new PaintEventArgs(graphics, control.ClientRectangle);
            Paint(control, args);
            var layout = Field<RectangleF[]>(control, "_radialLabelBounds").ToArray();
            control.ThemeName = theme;
            Paint(control, args);
            Assert.Equal(layout, Field<RectangleF[]>(control, "_radialLabelBounds"));
            Assert.Equal(spectrum, Field<byte[]>(control, "_spectrum"));
            foreach (var text in VisibleTexts(control)) AssertInside(text.Bounds, control.Width, control.Height);
        });
    }

    private static AdvancedVisualizationControl Create(int width, int height) => new()
    {
        Size = new Size(width, height), ThemeName = "Studio", Mode = AdvancedVisualizationMode.RadialSpectrum
    };

    private static List<(string Text, RectangleF Bounds)> VisibleTexts(AdvancedVisualizationControl control)
    {
        var texts = new List<(string, RectangleF)>();
        var caption = Field<RectangleF>(control, "_radialCaptionBounds");
        var legend = Field<RectangleF>(control, "_radialLegendBounds");
        if (!caption.IsEmpty) texts.Add((Field<string>(control, "_radialLayoutCaption"), caption));
        if (!legend.IsEmpty) texts.Add((Field<string>(control, "_radialLayoutLegend"), legend));
        if (Field<bool>(control, "_radialLabelsVisible"))
        {
            var bounds = Field<RectangleF[]>(control, "_radialLabelBounds");
            var labels = Field<string[]>(control, "_bandLabels");
            var bands = new[] { 0, 20, 40, 60 };
            for (var index = 0; index < bands.Length; index++) texts.Add((labels[bands[index]], bounds[index]));
        }
        return texts;
    }

    private static void AssertInside(RectangleF bounds, int width, int height)
    {
        Assert.True(bounds.Left >= -0.01f && bounds.Top >= -0.01f && bounds.Right <= width + 0.01f && bounds.Bottom <= height + 0.01f,
            $"Bounds {bounds} escape {width}x{height}.");
    }

    private static int[] Snapshot(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < bitmap.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * bitmap.Width, bitmap.Width);
        }
        finally { bitmap.UnlockBits(data); }
        return pixels;
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void Paint(AdvancedVisualizationControl control, PaintEventArgs args) =>
        typeof(AdvancedVisualizationControl).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { args });
}
