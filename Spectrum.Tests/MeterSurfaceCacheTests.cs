// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// The meter surface cache should reuse bitmaps when nothing changed and rebuild when it has to.
public class MeterSurfaceCacheTests
{
    [Fact]
    public void PremultipliedHistoryTextureRetainsEveryOriginalOpaquePaletteColor()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl();
            var values = new byte[83];
            for (var band = 0; band < values.Length; band++) values[band] = (byte)(band * 3);
            foreach (var theme in BarColorThemes.Names)
            {
                control.ThemeName = theme;
                control.SetSpectrum(values);
                var image = Field<Bitmap>(control, "_historyImage");
                Assert.Equal(PixelFormat.Format32bppPArgb, image.PixelFormat);
                var row = (Field<int>(control, "_historyWriteIndex") + control.HistoryFrames - 1) % control.HistoryFrames;
                var palette = Field<int[]>(control, "_paletteArgb");
                for (var band = 0; band < values.Length; band++)
                {
                    var actual = image.GetPixel(band, row);
                    Assert.Equal(255, actual.A);
                    Assert.Equal(palette[values[band]], actual.ToArgb());
                }
            }
        });
    }

    [Theory]
    [InlineData("Spectrum", false)]
    [InlineData("Bricks", false)]
    [InlineData("LED", false)]
    [InlineData("Spectrum", true)]
    [InlineData("Bricks", true)]
    [InlineData("LED", true)]
    public void CachedSurfacesPreserveTheOriginalRenderingAndInvalidateForAppearanceChanges(
        string name, bool highQuality)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 170);
            bar.Size = new Size(24, 240);
            bar.HighQualityRendering = highQuality;
            bar.GridlineLevels = new float[] { 0.25f, 0.5f, 0.75f };
            AssertMatchesOriginal(bar, name);
            var changes = new Action[]
            {
                () => bar.ApplyThemeChrome(BarColorThemes.PlatinumLight.Ui),
                () => bar.BrickHeight = 10,
                () => bar.BrickGap = 3,
                () => bar.BrickPadding = 2,
                () => bar.BrickHighlight = false,
                () => bar.HighlightAlpha = 200,
                () => bar.HeatLowColor = Color.Cyan,
                () => bar.HeatmapEnabled = false,
                () => bar.ForeColor = Color.Magenta,
                () => bar.GridlineColor = Color.Blue,
                () => ((float[])bar.GridlineLevels)[0] = 0.4f,
                () => bar.HighQualityRendering = !highQuality,
                () => bar.Size = new Size(30, 260)
            };
            foreach (var change in changes)
            {
                change();
                AssertMatchesOriginal(bar, name);
            }
        });
    }

    [Fact]
    public void ReplacedAndDisposedMeterSurfacesReleaseTheirNativeImages()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var bar = VisualizationReadabilityTests.CreateBar(BarColorThemes.Studio, 180);
            try
            {
                AssertMatchesOriginal(bar, "Bricks");
                var previous = Field<Bitmap>(bar, "_inactiveMeterSurface");
                bar.BrickHeight = 9;
                AssertMatchesOriginal(bar, "Bricks");
                Assert.NotSame(previous, Field<Bitmap>(bar, "_inactiveMeterSurface"));
                Assert.Throws<ArgumentException>(() => previous.GetPixel(0, 0));
                var inactive = Field<Bitmap>(bar, "_inactiveMeterSurface");
                var active = Field<Bitmap>(bar, "_activeMeterSurface");
                bar.Dispose();
                Assert.Throws<ArgumentException>(() => inactive.GetPixel(0, 0));
                Assert.Throws<ArgumentException>(() => active.GetPixel(0, 0));
            }
            finally { bar.Dispose(); }
        });
    }

    private static void AssertMatchesOriginal(VerticalProgressBar bar, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(VerticalProgressBar);
        var modeType = type.GetNestedType("VisualizationMode", BindingFlags.NonPublic);
        var mode = Enum.Parse(modeType, name == "LED" ? "Led" : name);
        type.GetMethod("EnsureBrickGeometry", flags).Invoke(bar, new object[] { bar.ClientRectangle });
        foreach (var lit in new[] { 0, 1, 5, Field<List<Rectangle>>(bar, "_brickRects").Count })
        {
            CombinedRandomizationTests.SetField(bar, "_litBricks", lit);
            using var expected = new Bitmap(bar.Width, bar.Height, PixelFormat.Format32bppPArgb);
            using var actual = new Bitmap(bar.Width, bar.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(actual))
            {
                type.GetMethod("ConfigureGraphicsQuality", flags).Invoke(bar, new object[] { graphics });
                Assert.True((bool)type.GetMethod("DrawCachedMeter", flags)
                    .Invoke(bar, new[] { graphics, (object)bar.ClientRectangle, mode }));
            }
            using (var graphics = Graphics.FromImage(expected))
            {
                type.GetMethod("ConfigureGraphicsQuality", flags).Invoke(bar, new object[] { graphics });
                type.GetMethod("DrawMeterBackground", flags).Invoke(bar, new object[] { graphics, bar.ClientRectangle });
                type.GetMethod("DrawMode_" + (name == "LED" ? "Led" : name), flags)
                    .Invoke(bar, new object[] { graphics, bar.ClientRectangle });
            }
            for (var y = 0; y < bar.Height; y++)
            for (var x = 0; x < bar.Width; x++)
                Assert.True(expected.GetPixel(x, y).ToArgb() == actual.GetPixel(x, y).ToArgb(),
                    $"{name}, quality={bar.HighQualityRendering}, lit={lit}, ({x},{y}): cached rendering differs.");
        }
    }

    private static T Field<T>(object target, string name) =>
        CombinedRandomizationTests.GetField<T>(target, name);
}
