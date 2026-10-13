using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

public class VisualizationReadabilityTests
{
    private readonly ITestOutputHelper _output;
    public VisualizationReadabilityTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Themes
    {
        get
        {
            foreach (var name in BarColorThemes.Names)
                yield return new object[] { name };
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void PaletteStopsHavePerceptualSeparationAndSignalsAndTextHaveSurfaceContrast(string name)
    {
        var theme = BarColorThemes.Resolve(name);
        var colors = new[] { theme.Low, theme.Mid, theme.High, theme.Peak };
        var minimumDelta = double.MaxValue;
        var minimumContrast = double.MaxValue;
        for (var i = 0; i < colors.Length; i++)
        {
            Assert.Equal(255, colors[i].A);
            minimumContrast = Math.Min(minimumContrast, Contrast(colors[i], theme.Ui.VisualizationSurface));
            minimumContrast = Math.Min(minimumContrast, Contrast(colors[i], theme.Ui.WaveSurface));
            if (i > 0)
                minimumDelta = Math.Min(minimumDelta, DeltaE(colors[i - 1], colors[i]));
        }
        _output.WriteLine($"{name}: minimum adjacent CIELAB DeltaE76 {minimumDelta:F2}; minimum signal/surface contrast {minimumContrast:F2}:1");
        // 16 is a palette design regression target, not a WCAG requirement or a proof of perceived quality.
        Assert.True(minimumDelta >= 16, $"{name}: adjacent DeltaE76 {minimumDelta:F2}");
        Assert.True(minimumContrast >= 3, $"{name}: signal/surface contrast {minimumContrast:F2}");
        foreach (var surface in new[] { theme.Ui.Canvas, theme.Ui.ControlSurface, theme.Ui.Selection, theme.Ui.Surface })
        {
            Assert.True(Contrast(theme.Ui.PrimaryText, surface) >= 4.5);
            Assert.True(Contrast(theme.Ui.SecondaryText, surface) >= 4.5);
        }
    }

    [Theory]
    [InlineData("Sunset")]
    [InlineData("MonoCyan")]
    [InlineData("Aurora")]
    [InlineData("Obsidian Gold")]
    [InlineData("Midnight Prism")]
    [InlineData("Emerald Noir")]
    [InlineData("Crimson Velvet")]
    [InlineData("Studio")]
    public void RevisedRampsAlsoIncreaseLightnessRatherThanRelyingOnlyOnHue(string name)
    {
        var theme = BarColorThemes.Resolve(name);
        var colors = new[] { theme.Low, theme.Mid, theme.High, theme.Peak };
        for (var i = 1; i < colors.Length; i++)
            Assert.True(Lab(colors[i])[0] - Lab(colors[i - 1])[0] >= 5, $"{name} lightness step {i}");
    }

    [Fact]
    public void ContinuousBarsRepaintForColorChangesWithinTheSamePixelRow()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = CreateBar(BarColorThemes.Studio, 99f);
            bar.Size = new Size(18, 44);
            var type = typeof(VerticalProgressBar);
            var mode = type.GetMethod("GetVisualizationModeCached", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(bar, null);
            var shouldInvalidate = type.GetMethod("ComputeShouldInvalidate", BindingFlags.Instance | BindingFlags.NonPublic);
            using var before = Render(bar);
            CombinedRandomizationTests.SetField(bar, "_displayValue", 100f);
            using var after = Render(bar);
            Assert.NotEqual(before.GetPixel(9, 40), after.GetPixel(9, 40));
            Assert.Equal((int)Math.Round(42f * 99f / 255f), (int)Math.Round(42f * 100f / 255f));
            CombinedRandomizationTests.SetField(bar, "_displayValue", 99f);
            Assert.True((bool)shouldInvalidate.Invoke(bar, new[] { mode }));
            CombinedRandomizationTests.SetField(bar, "_displayValue", 100f);
            Assert.True((bool)shouldInvalidate.Invoke(bar, new[] { mode }));
            Assert.False((bool)shouldInvalidate.Invoke(bar, new[] { mode }));
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void AllNonzeroCachedHistoryLevelsHaveContrastAndZeroKeepsTheBackground(string name)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { ThemeName = name };
            var brushes = CombinedRandomizationTests.GetField<SolidBrush[]>(control, "_levelBrushes");
            Assert.Equal(control.BackColor, brushes[0].Color);
            for (var i = 1; i < brushes.Length; i++)
                Assert.True(Contrast(brushes[i].Color, control.BackColor) >= 3, $"{name} level {i}");
        });
    }

    [Fact]
    public void WaveKeepsSubpixelHeightDifferencesAndTheFixedDisplayRange()
    {
        var points = new PointF[4];
        WaveSpectrumControl.PopulateFrequencyPoints(new[] { 0f, 100f, 100.25f, 255f }, 400, 301, points);
        Assert.Equal(300f, points[0].Y);
        Assert.Equal(0f, points[3].Y);
        Assert.True(points[2].Y < points[1].Y);
        Assert.InRange(points[1].Y - points[2].Y, 0.29f, 0.30f);
    }

    [Fact]
    public void PeakMarkersAreNotOverpaintedByTheActiveEdgeHighlight()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = CreateBar(BarColorThemes.Studio, 255f);
            bar.PeakHoldEnabled = true;
            bar.PeakColorMatchesHeat = false;
            bar.PeakLineColor = Color.Magenta;
            CombinedRandomizationTests.SetField(bar, "_peakValue", 255f);
            using var bitmap = Render(bar);
            var top = (bar.Height - bar.ClientSize.Height) / 2 + bar.BrickPadding;
            Assert.Equal(Color.Magenta.ToArgb(), bitmap.GetPixel(9, top).ToArgb());
        });
    }

    [Fact]
    public void ThresholdMonitorDoesNotPaintActiveSignalColorsDuringSilence()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(664, 80), ThemeName = "Studio" };
            control.SetSpectrum(new byte[83]);
            control.Mode = AdvancedVisualizationMode.Contour;
            using var empty = Render(control);
            control.Mode = AdvancedVisualizationMode.ThresholdMonitor;
            using var threshold = Render(control);
            Assert.Equal(empty.GetPixel(333, 79), threshold.GetPixel(333, 79));
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ABackgroundSeparatorKeepsPeaksDistinctEvenOverPaleFullScaleFill(string name)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var theme = BarColorThemes.Resolve(name);
            using var bar = CreateBar(theme, 255f);
            bar.PeakHoldEnabled = true;
            bar.PeakColorMatchesHeat = false;
            bar.PeakLineColor = theme.Ui.PrimaryText;
            CombinedRandomizationTests.SetField(bar, "_peakValue", 255f);
            using var bitmap = Render(bar);
            var top = (bar.Height - bar.ClientSize.Height) / 2 + bar.BrickPadding;
            Assert.Equal(theme.Ui.PrimaryText.ToArgb(), bitmap.GetPixel(9, top).ToArgb());
            Assert.Equal(bar.BackColor.ToArgb(), bitmap.GetPixel(9, top + 2).ToArgb());
            Assert.NotEqual(bar.BackColor.ToArgb(), bitmap.GetPixel(9, top + 3).ToArgb());
        });
    }

    [Fact]
    public void RepresentativeRenderingStaysWithinTheFrameBudget()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Size = new Size(900, 280), Mode = AdvancedVisualizationMode.Waterfall };
            var values = new byte[83];
            for (var i = 0; i < values.Length; i++) values[i] = (byte)(1 + i * 254 / 82);
            for (var i = 0; i < 160; i++) control.SetSpectrum(values);
            using var bitmap = new Bitmap(control.Width, control.Height);
            for (var i = 0; i < 10; i++) control.DrawToBitmap(bitmap, control.ClientRectangle);
            var watch = Stopwatch.StartNew();
            const int frames = 40;
            for (var i = 0; i < frames; i++) control.DrawToBitmap(bitmap, control.ClientRectangle);
            watch.Stop();
            var ms = watch.Elapsed.TotalMilliseconds / frames;
            _output.WriteLine($"900x280 waterfall with 160 history rows: {ms:F3} ms/render (includes DrawToBitmap)");
            Assert.True(ms < 33.3, $"{ms:F3} ms/render exceeds the analysis frame interval");
        });
    }

    [Fact]
    public void MeterModesAndWaveRenderEverySupportedStyleAndTheme()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            foreach (var mode in new[] { "Spectrum", "Bricks", "LED", "Dots", "Lollipop", "Wave" })
            foreach (var name in BarColorThemes.Names)
            foreach (var style in VisualStyles.GetSupportedNames(mode))
            {
                var theme = BarColorThemes.Resolve(name);
                if (mode == "Wave")
                {
                    using var wave = new WaveSpectrumControl(4) { Size = new Size(120, 64), Style = VisualStyles.Parse(style) };
                    wave.SetTheme(theme);
                    wave.SetDisplayedLevelsForTesting(new[] { 2f, 64f, 128f, 255f });
                    using var bitmap = Render(wave);
                    wave.SetDisplayedLevelsForTesting(new float[4]);
                    using var silent = Render(wave);
                    Assert.True(BitmapsDifferInsideFrame(bitmap, silent), $"{mode} / {style} / {name} should render signal changes");
                }
                else
                {
                    using var bar = CreateBar(theme, 200f);
                    bar.Tag = mode + "|1";
                    bar.VisualStyle = VisualStyles.Parse(style);
                    using var bitmap = Render(bar);
                    CombinedRandomizationTests.SetField(bar, "_displayValue", 0f);
                    using var silent = Render(bar);
                    Assert.True(BitmapsDifferInsideFrame(bitmap, silent), $"{mode} / {style} / {name} should render signal changes");
                }
            }
        });
    }

    private static bool BitmapsDifferInsideFrame(Bitmap first, Bitmap second)
    {
        for (var y = 2; y < first.Height - 2; y++)
        for (var x = 2; x < first.Width - 2; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb())
                return true;
        return false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraphicsQualityPresetsAreRestoredWhenAContextChanges(bool highQuality)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = CreateBar(BarColorThemes.Studio, 128f);
            bar.HighQualityRendering = highQuality;
            using var bitmap = new Bitmap(18, 64);
            using var graphics = Graphics.FromImage(bitmap);
            var configure = (Action<Graphics>)Delegate.CreateDelegate(typeof(Action<Graphics>), bar,
                typeof(VerticalProgressBar).GetMethod("ConfigureGraphicsQuality", BindingFlags.Instance | BindingFlags.NonPublic));
            for (var i = 0; i < 2; i++)
            {
                graphics.SmoothingMode = highQuality ? SmoothingMode.None : SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = highQuality ? PixelOffsetMode.None : PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = highQuality ? CompositingQuality.HighSpeed : CompositingQuality.HighQuality;
                graphics.InterpolationMode = highQuality ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                configure(graphics);
                Assert.Equal(highQuality ? SmoothingMode.AntiAlias : SmoothingMode.None, graphics.SmoothingMode);
                Assert.Equal(highQuality ? PixelOffsetMode.HighQuality : PixelOffsetMode.None, graphics.PixelOffsetMode);
                Assert.Equal(highQuality ? CompositingQuality.HighQuality : CompositingQuality.HighSpeed, graphics.CompositingQuality);
                Assert.Equal(highQuality ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor, graphics.InterpolationMode);
            }
        });
    }

    [Fact]
    public void EightyThreeContinuousMetersRenderWithinTheSixtyHzFrameBudget()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var bars = new VerticalProgressBar[83];
            var bitmaps = new Bitmap[83];
            var graphics = new Graphics[83];
            var paintArgs = new System.Windows.Forms.PaintEventArgs[83];
            var painters = new Action<System.Windows.Forms.PaintEventArgs>[83];
            try
            {
                for (var i = 0; i < bars.Length; i++)
                {
                    bars[i] = CreateBar(BarColorThemes.Studio, 1 + i * 254f / 82);
                    bars[i].Size = new Size(11, 280);
                    bars[i].PeakHoldEnabled = true;
                    bars[i].PeakColorMatchesHeat = false;
                    bars[i].PeakLineColor = BarColorThemes.Studio.Ui.PrimaryText;
                    CombinedRandomizationTests.SetField(bars[i], "_peakValue", 255f);
                    bitmaps[i] = new Bitmap(bars[i].Width, bars[i].Height);
                    bars[i].DrawToBitmap(bitmaps[i], new Rectangle(Point.Empty, bars[i].Size));
                    graphics[i] = Graphics.FromImage(bitmaps[i]);
                    paintArgs[i] = new System.Windows.Forms.PaintEventArgs(graphics[i], bars[i].ClientRectangle);
                    painters[i] = (Action<System.Windows.Forms.PaintEventArgs>)Delegate.CreateDelegate(
                        typeof(Action<System.Windows.Forms.PaintEventArgs>), bars[i],
                        typeof(VerticalProgressBar).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic));
                }
                var watch = Stopwatch.StartNew();
                const int frames = 40;
                for (var frame = 0; frame < frames; frame++)
                for (var i = 0; i < bars.Length; i++)
                    bars[i].DrawToBitmap(bitmaps[i], new Rectangle(Point.Empty, bars[i].Size));
                watch.Stop();
                _output.WriteLine($"83 continuous meter captures, 11x280 each: {watch.Elapsed.TotalMilliseconds / frames:F3} ms/frame (includes DrawToBitmap)");
                var configure = (Action<Graphics>)Delegate.CreateDelegate(typeof(Action<Graphics>), bars[0],
                    typeof(VerticalProgressBar).GetMethod("ConfigureGraphicsQuality", BindingFlags.Instance | BindingFlags.NonPublic));
                configure(graphics[0]);
                var qualityAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 400; i++) configure(graphics[0]);
                _output.WriteLine($"Graphics quality configuration: {(GC.GetAllocatedBytesForCurrentThread() - qualityAllocationBefore) / 400d:F1} bytes/call");
                // DrawToBitmap performs WM_PRINT and a bitmap copy for each control. Capture cost is
                // reported separately; the application's renderer budget applies to its OnPaint work.
                for (var frame = 0; frame < 10; frame++)
                for (var i = 0; i < bars.Length; i++) painters[i](paintArgs[i]);
                const int paintFrames = 400;
                var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                for (var frame = 0; frame < paintFrames; frame++)
                for (var i = 0; i < bars.Length; i++) painters[i](paintArgs[i]);
                watch.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
                var ms = watch.Elapsed.TotalMilliseconds / paintFrames;
                _output.WriteLine($"83 continuous meters with peak separators: {ms:F3} ms/frame OnPaint; {allocated} bytes allocated over {paintFrames} frames");
                Assert.True(ms < 16.7, $"{ms:F3} ms/frame exceeds the 60 Hz budget");
                Assert.True(allocated / (double)paintFrames < 64, $"{allocated / (double)paintFrames:F1} bytes/frame allocated");
            }
            finally
            {
                foreach (var args in paintArgs) args?.Dispose();
                foreach (var context in graphics) context?.Dispose();
                foreach (var bitmap in bitmaps) bitmap?.Dispose();
                foreach (var bar in bars) bar?.Dispose();
            }
        });
    }

    // Returns an actual offscreen renderer comparison without creating a prohibited image file.
    public static string RenderThemeContactSheet()
    {
        string result = null;
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var sheet = new Bitmap(920, 30 + BarColorThemes.Names.Length * 76);
            using var graphics = Graphics.FromImage(sheet);
            using var font = new Font("Segoe UI", 9f);
            graphics.Clear(Color.FromArgb(12, 16, 21));
            graphics.DrawString("Theme                  continuous meters                  Wave                                  Waterfall (levels 1-255)", font, Brushes.White, 8, 5);
            for (var row = 0; row < BarColorThemes.Names.Length; row++)
            {
                var name = BarColorThemes.Names[row];
                var theme = BarColorThemes.Resolve(name);
                var y = 30 + row * 76;
                graphics.DrawString(name, font, Brushes.White, 8, y + 22);
                var levels = new[] { 2f, 16f, 64f, 128f, 200f, 255f };
                for (var i = 0; i < levels.Length; i++)
                {
                    using var bar = CreateBar(theme, levels[i]);
                    using var bitmap = Render(bar);
                    graphics.DrawImageUnscaled(bitmap, 155 + i * 22, y);
                }
                using var wave = new WaveSpectrumControl(6) { Size = new Size(260, 64) };
                wave.SetTheme(theme);
                wave.SetDisplayedLevelsForTesting(levels);
                using var waveBitmap = new Bitmap(wave.Width, wave.Height);
                wave.DrawToBitmap(waveBitmap, wave.ClientRectangle);
                graphics.DrawImageUnscaled(waveBitmap, 310, y);
                using var waterfall = new AdvancedVisualizationControl { Size = new Size(310, 64), ThemeName = name, Mode = AdvancedVisualizationMode.Waterfall };
                var values = new byte[83];
                for (var i = 0; i < values.Length; i++) values[i] = (byte)(1 + i * 254 / 82);
                waterfall.SetSpectrum(values);
                using var waterfallBitmap = new Bitmap(waterfall.Width, waterfall.Height);
                waterfall.DrawToBitmap(waterfallBitmap, waterfall.ClientRectangle);
                graphics.DrawImageUnscaled(waterfallBitmap, 596, y);
            }
            using var stream = new MemoryStream();
            sheet.Save(stream, ImageFormat.Png);
            result = Convert.ToBase64String(stream.ToArray());
        });
        return result;
    }

    internal static VerticalProgressBar CreateBar(BarColorTheme theme, float level)
    {
        var bar = new VerticalProgressBar { Size = new Size(18, 64), Maximum = 255, Tag = "Spectrum|1", BrickPadding = 1,
            HeatLowColor = theme.Low, HeatMidColor = theme.Mid, HeatHighColor = theme.High, HeatPeakColor = theme.Peak,
            HeatIntensityCurve = theme.IntensityCurve, TopEmphasisEnabled = false, PeakHoldEnabled = false };
        bar.ApplyThemeChrome(theme.Ui);
        CombinedRandomizationTests.SetField(bar, "_displayValue", level);
        return bar;
    }

    internal static Bitmap Render(System.Windows.Forms.Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        return bitmap;
    }

    internal static double Contrast(Color first, Color second) =>
        (Math.Max(Luminance(first), Luminance(second)) + 0.05) / (Math.Min(Luminance(first), Luminance(second)) + 0.05);

    private static double Linear(byte value)
    {
        var v = value / 255d;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double[] Lab(Color color)
    {
        static double F(double v) => v > 216d / 24389d ? Math.Pow(v, 1d / 3d) : ((24389d / 27d * v) + 16d) / 116d;
        var r = Linear(color.R); var g = Linear(color.G); var b = Linear(color.B);
        var x = F((0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047);
        var y = F(0.2126729 * r + 0.7151522 * g + 0.0721750 * b);
        var z = F((0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883);
        return new[] { 116 * y - 16, 500 * (x - y), 200 * (y - z) };
    }

    private static double DeltaE(Color first, Color second)
    {
        var a = Lab(first); var b = Lab(second);
        return Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2) + Math.Pow(a[2] - b[2], 2));
    }
}
