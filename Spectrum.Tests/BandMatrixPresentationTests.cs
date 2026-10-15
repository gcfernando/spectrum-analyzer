// Developer :: > Gehan Fernando
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Makes sure the band matrix presents values and layout the way the UI expects.
public class BandMatrixPresentationTests
{
    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(70, 42)]
    [InlineData(320, 180)]
    [InlineData(480, 500)]
    [InlineData(900, 280)]
    [InlineData(1200, 360)]
    public void ResponsiveCardsKeepEveryBandOrderedInsideTheViewport(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(width, height);
            using var bitmap = VisualizationReadabilityTests.Render(control);
            var cards = Field<RectangleF[]>(control, "_matrixCards");
            Assert.Equal(83, cards.Length);
            if (width >= 900 && height >= 280)
                Assert.True(cards[0].Height >= Field<int>(control, "_matrixFrequencyHeight") + Field<int>(control, "_matrixValueHeight") + 8,
                    "Normal-size layouts must retain the numeric level readouts.");
            foreach (var card in cards)
            {
                Assert.True(card.Width > 0 && card.Height > 0);
                Assert.InRange(card.Left, 0, width);
                Assert.InRange(card.Top, 0, height);
                Assert.True(card.Right <= width + 0.05f && card.Bottom <= height + 0.05f);
            }
            for (var band = 1; band < cards.Length; band++)
            {
                var previous = cards[band - 1];
                var current = cards[band];
                Assert.True(current.Top > previous.Top || (current.Top == previous.Top && current.Left > previous.Left));
            }
            for (var first = 0; first < cards.Length; first++)
            for (var second = first + 1; second < cards.Length; second++)
            {
                var overlap = RectangleF.Intersect(cards[first], cards[second]);
                Assert.True(overlap.Width <= 0.001f || overlap.Height <= 0.001f, "Band cards must not overlap.");
            }
        });
    }

    [Fact]
    public void MagnitudeMetersDistinguishLowHighAndDisplayFloor()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(900, 280);
            var frame = new byte[83];
            frame[0] = 80;
            control.SetSpectrum(frame);
            using var low = VisualizationReadabilityTests.Render(control);
            var card = Field<RectangleF[]>(control, "_matrixCards")[0];
            var brushes = Field<SolidBrush[]>(control, "_levelBrushes");
            var lowPixels = CountMeterPixels(low, card, brushes[80].Color);
            frame[0] = 240;
            control.SetSpectrum(frame);
            using var high = VisualizationReadabilityTests.Render(control);
            var highPixels = CountMeterPixels(high, card, brushes[240].Color);
            Assert.True(lowPixels > 0);
            Assert.True(highPixels > lowPixels + 10, "Magnitude must change meter length as well as color.");
            control.SetSpectrum(new byte[83]);
            using var floor = VisualizationReadabilityTests.Render(control);
            Assert.Equal(0, CountMeterPixels(floor, card, brushes[240].Color));
            var labels = (string[])typeof(AdvancedVisualizationControl)
                .GetField("s_matrixValueLabels", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Assert.Equal("FLOOR", labels[0]);
            Assert.Equal("0.0", labels[255]);
        });
    }

    [Theory]
    [InlineData(70, 42)]
    [InlineData(900, 280)]
    public void LowestNonzeroLevelsRemainVisiblyDifferentFromTheFloor(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(width, height);
            control.SetSpectrum(new byte[83]);
            using var floor = VisualizationReadabilityTests.Render(control);
            control.SetSpectrum(Enumerable.Repeat((byte)1, 83).ToArray());
            using var active = VisualizationReadabilityTests.Render(control);
            var changed = 0;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (floor.GetPixel(x, y) != active.GetPixel(x, y)) changed++;
            Assert.True(changed > 83, "Low activity must remain visible in both detailed and compact layouts.");
        });
    }

    [Fact]
    public void CardTextHasContrastAcrossAllThemesAndCachedIntensitySurfaces()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(900, 280);
            foreach (var name in BarColorThemes.Names)
            {
                control.ThemeName = name;
                var theme = BarColorThemes.Resolve(name);
                foreach (var surface in Field<SolidBrush[]>(control, "_matrixSurfaceBrushes"))
                {
                    Assert.True(VisualizationReadabilityTests.Contrast(theme.Ui.PrimaryText, surface.Color) >= 4.5);
                    Assert.True(VisualizationReadabilityTests.Contrast(theme.Ui.SecondaryText, surface.Color) >= 4.5);
                }
            }
        });
    }

    [Fact]
    public void LayoutAndFontsAreReusedUntilSizeOrFontChanges()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(900, 280);
            using var first = VisualizationReadabilityTests.Render(control);
            var paths = Field<GraphicsPath[]>(control, "_matrixCardPaths").ToArray();
            var valueFont = Field<Font>(control, "_matrixValueFont");
            control.SetSpectrum(new byte[] { 200 });
            using var second = VisualizationReadabilityTests.Render(control);
            Assert.Same(valueFont, Field<Font>(control, "_matrixValueFont"));
            for (var band = 0; band < 83; band++) Assert.Same(paths[band], Field<GraphicsPath[]>(control, "_matrixCardPaths")[band]);
            control.Size = new Size(1200, 360);
            using var resized = VisualizationReadabilityTests.Render(control);
            Assert.NotSame(paths[0], Field<GraphicsPath[]>(control, "_matrixCardPaths")[0]);
            using var customFont = new Font("Segoe UI", 12);
            control.Font = customFont;
            using var largerFont = VisualizationReadabilityTests.Render(control);
            Assert.NotSame(valueFont, Field<Font>(control, "_matrixValueFont"));
            Assert.True(Field<Font>(control, "_matrixValueFont").Bold);
            control.Dispose();
            Assert.True(customFont.GetHeight() > 0, "The renderer must not dispose the caller's font.");
        });
    }

    [Fact]
    public void DpiChangesRebuildGeometryUsingTheActualPaintFontMetrics()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Create(1350, 420);
            using var baseline = VisualizationReadabilityTests.Render(control);
            var paths = Field<GraphicsPath[]>(control, "_matrixCardPaths").ToArray();
            using var bitmap = new Bitmap(1350, 420);
            bitmap.SetResolution(144, 144);
            using var graphics = Graphics.FromImage(bitmap);
            using var args = new System.Windows.Forms.PaintEventArgs(graphics, control.ClientRectangle);
            typeof(AdvancedVisualizationControl).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(control, new object[] { args });
            Assert.Equal(144f, Field<float>(control, "_matrixDpi"));
            Assert.NotSame(paths[0], Field<GraphicsPath[]>(control, "_matrixCardPaths")[0]);
            var valueHeight = Field<int>(control, "_matrixValueHeight");
            Assert.Equal((int)Math.Ceiling(Field<Font>(control, "_matrixValueFont").GetHeight(graphics)), valueHeight);
            Assert.True(Field<RectangleF[]>(control, "_matrixCards")[0].Height >=
                Field<int>(control, "_matrixFrequencyHeight") + valueHeight + 12, "Scaled normal-size cards must retain numeric readouts.");
        });
    }

    public static string RenderPresentationContactSheet()
    {
        string result = null;
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var themes = new[] { "Studio", "Crimson Velvet", "Ice" };
            using var sheet = new Bitmap(920, 30 + 3 * 312);
            using var graphics = Graphics.FromImage(sheet);
            using var font = new Font("Segoe UI", 9);
            graphics.Clear(Color.FromArgb(12, 16, 21));
            graphics.DrawString("Band Matrix • actual offscreen renderer • synthetic display-band input", font, Brushes.White, 8, 5);
            for (var row = 0; row < themes.Length; row++)
            {
                var y = 30 + row * 312;
                graphics.DrawString(themes[row], font, Brushes.White, 8, y);
                using var control = Create(900, 280);
                control.ThemeName = themes[row];
                control.Style = VisualStyle.Precision;
                using var bitmap = VisualizationReadabilityTests.Render(control);
                graphics.DrawImageUnscaled(bitmap, 10, y + 22);
            }
            using var stream = new MemoryStream();
            sheet.Save(stream, ImageFormat.Png);
            result = Convert.ToBase64String(stream.ToArray());
        });
        return result;
    }

    private static AdvancedVisualizationControl Create(int width, int height)
    {
        var control = new AdvancedVisualizationControl { Size = new Size(width, height), Mode = AdvancedVisualizationMode.BandMatrix, ThemeName = "Studio" };
        var levels = new byte[83];
        for (var band = 0; band < levels.Length; band++)
            levels[band] = (byte)(15 + 200 * Math.Exp(-Math.Pow((band - 25) / 12d, 2)));
        control.SetSpectrum(levels);
        return control;
    }

    private static int CountMeterPixels(Bitmap bitmap, RectangleF card, Color color)
    {
        var count = 0;
        var y = Math.Min(bitmap.Height - 1, (int)(card.Bottom - 3.5f));
        for (var x = (int)card.Left + 5; x < card.Right - 5; x++)
            if (bitmap.GetPixel(x, y).ToArgb() == color.ToArgb()) count++;
        return count;
    }
}
